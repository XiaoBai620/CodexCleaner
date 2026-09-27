'use strict';
if (Number(process.versions.node.split('.')[0]) < 24) throw new Error('需要 Node.js 24 或更新版本。');
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const crypto = require('node:crypto');
const {execFile} = require('node:child_process');
const {promisify} = require('node:util');
const runFile = promisify(execFile);
const {Cleaner} = require('./lib/cleaner.cjs');
const {version} = require('./package.json');
const PUBLIC = path.join(__dirname, 'public');
const MAX_BODY_BYTES = 65536;
const STATIC = new Map([['/', ['index.html','text/html; charset=utf-8']], ['/app.js',['app.js','text/javascript; charset=utf-8']], ['/style.css',['style.css','text/css; charset=utf-8']]]);

async function detectCodex() {
  if (process.platform !== 'win32') throw new Error('此版本仅支持 Windows；无法可靠检查其他系统上的 Codex 进程。');
  const command = "$ErrorActionPreference = 'Stop'; [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new(); $p = @(Get-Process | Where-Object { $_.ProcessName -match '^(codex|chatgpt|codex-desktop|codex-code-mode-host|codex-app-server)$' } | Select-Object @{Name='name';Expression={$_.ProcessName}}, @{Name='pid';Expression={$_.Id}}); ConvertTo-Json -InputObject $p -Compress";
  const powershell = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
  const {stdout} = await runFile(powershell, ['-NoLogo','-NoProfile','-NonInteractive','-Command', command], {windowsHide:true,timeout:12000,maxBuffer:1024*1024,encoding:'utf8'});
  const result = JSON.parse(stdout.replace(/^\uFEFF/,'').trim());
  if (!Array.isArray(result)) throw new Error('无法验证 Codex 进程状态，已禁止清理。');
  return {running: result.length > 0, processes: result};
}

function safeMessage(error) { return error && error.message ? error.message : '操作失败，请刷新后重试。'; }
function requestError(message, status=400) { return Object.assign(new Error(message), {status}); }
async function readBody(req) {
  if (!/^application\/json(?:\s*;|$)/i.test(req.headers['content-type'] || '')) throw requestError('仅接受 JSON 请求。',415);
  const raw = await new Promise((resolve,reject)=>{
    let length=0; const chunks=[];
    const cleanup=()=>{req.off('data',onData);req.off('end',onEnd);req.off('error',onError);req.off('aborted',onAborted);};
    const onError=error=>{cleanup();reject(error);};
    const onAborted=()=>onError(requestError('请求已中断。'));
    const onData=chunk=>{
      length+=chunk.length;
      if(length>MAX_BODY_BYTES) {onError(requestError('请求内容过大。',413));req.resume();return;}
      chunks.push(chunk);
    };
    const onEnd=()=>{cleanup();resolve(Buffer.concat(chunks).toString('utf8'));};
    req.on('data',onData);req.once('end',onEnd);req.once('error',onError);req.once('aborted',onAborted);
  });
  let body;
  try {body=JSON.parse(raw || '{}');} catch {throw requestError('请求格式错误。');}
  if(!body || typeof body!=='object' || Array.isArray(body)) throw requestError('请求内容必须是 JSON 对象。');
  return body;
}
function validateSelection(s) {
  if (!s || !Array.isArray(s.chatIds) || s.chatIds.length>500 || s.chatIds.some(x=>typeof x!=='string' || !/^[A-Za-z0-9_-]{1,128}$/.test(x)) || typeof s.clearMemories!=='boolean' || (s.clearSharedHistory!==undefined && typeof s.clearSharedHistory!=='boolean')) throw requestError('清理范围无效。');
  return {chatIds:[...new Set(s.chatIds)],clearMemories:s.clearMemories,clearSharedHistory:!!s.clearSharedHistory};
}
function validatePort(value) {
  if(!Number.isInteger(value) || value<0 || value>65535) throw new Error('端口必须是 0 到 65535 之间的整数。');
  return value;
}
function parseArgs(args) {
  const options={},seen=new Set();
  for(let i=0;i<args.length;i++) {
    const flag=args[i];
    if(seen.has(flag)) throw new Error('参数重复：'+flag);
    seen.add(flag);
    if(flag==='--no-open') {options.openBrowser=false;continue;}
    if(flag==='--help' || flag==='-h') {options.help=true;continue;}
    if(!['--data-root','--transaction-root','--port'].includes(flag)) throw new Error('未知参数：'+flag);
    const value=args[++i];
    if(!value || value.startsWith('--')) throw new Error('参数缺少取值：'+flag);
    if(flag==='--port') {
      if(!/^\d+$/.test(value)) throw new Error('端口必须是 0 到 65535 之间的整数。');
      options.port=validatePort(Number(value));
    } else options[flag==='--data-root'?'dataRoot':'transactionRoot']=value;
  }
  return options;
}

async function startServer(options={}) {
  const port=validatePort(options.port===undefined?0:options.port);
  const dataRoot = path.resolve(options.dataRoot || process.env.CODEX_HOME || path.join(os.homedir(),'.codex'));
  const transactionRoot = path.resolve(options.transactionRoot || options.trashRoot || path.join(process.env.LOCALAPPDATA || os.homedir(),'CodexCleaner','Transactions'));
  const processCheck = options.processCheck || detectCodex;
  const cleaner = new Cleaner({dataRoot,transactionRoot,trashRoot:transactionRoot,processCheck});
  const token = crypto.randomBytes(32).toString('hex');
  const tokenBytes=Buffer.from(token,'ascii');
  let origin, busy=false, closing=false, closePromise;
  const idleWaiters=[];
  // Every storage operation shares this gate: a read must never observe a partial transaction.
  async function withData(action) {
    if(busy || closing) throw requestError('正在处理其他操作，请稍后重试。',409);
    busy=true;
    try {return await action();}
    finally {busy=false;for(const resolve of idleWaiters.splice(0)) resolve();}
  }
  async function status() {
    try {
      const result=await processCheck();
      if(!result || typeof result.running!=='boolean' || !Array.isArray(result.processes)) throw new Error('无法验证 Codex 进程状态，已禁止清理。');
      return {...result,running:result.running || result.processes.length>0,verified:true};
    } catch(e) {return {running:true,verified:false,processes:[],error:safeMessage(e)};}
  }
  const server = http.createServer(async(req,res)=>{
    res.setHeader('Cache-Control','no-store');
    res.setHeader('X-Content-Type-Options','nosniff');
    res.setHeader('Referrer-Policy','no-referrer');
    res.setHeader('Content-Security-Policy',"default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'");
    const json=(value,code=200)=>{
      if(res.destroyed || res.writableEnded) return;
      res.writeHead(code,{'Content-Type':'application/json; charset=utf-8'});
      res.end(JSON.stringify(value,(_,v)=>typeof v==='bigint'?String(v):v));
    };
    try {
      if(req.headers.host!==new URL(origin).host) return json({error:'只允许从本机地址访问。'},403);
      if(req.headers.origin && req.headers.origin!==origin) return json({error:'拒绝跨站请求。'},403);
      if(req.headers['sec-fetch-site']==='cross-site') return json({error:'拒绝跨站请求。'},403);
      if(closing) return json({error:'工具正在退出。'},503);
      const url=new URL(req.url,origin);
      const asset=STATIC.get(url.pathname);
      if(req.method==='GET' && asset) {res.writeHead(200,{'Content-Type':asset[1]});res.end(fs.readFileSync(path.join(PUBLIC,asset[0])));return;}
      if(req.method==='GET' && url.pathname==='/favicon.ico') {res.writeHead(204);res.end();return;}
      const given=req.headers['x-cleaner-token'];
      if(typeof given!=='string' || !/^[a-f0-9]{64}$/.test(given) || !crypto.timingSafeEqual(Buffer.from(given,'ascii'),tokenBytes)) return json({error:'本地访问凭证无效，请重新双击启动工具。'},401);
      if(req.method==='GET') {
        if(url.pathname==='/api/process') return json(await status());
        if(url.pathname==='/api/state') return json(await withData(async()=>{
          const [state,processStatus]=await Promise.all([cleaner.snapshot(),status()]);
          return {...state,processStatus,version,apiVersion:1};
        }));
        if(url.pathname==='/api/file') return json(await withData(()=>cleaner.inspectFile(url.searchParams.get('path') || '')));
      }
      if(req.method!=='POST' || !['/api/plan','/api/plan-repair','/api/execute','/api/execute-repair','/api/shutdown'].includes(url.pathname)) return json({error:'找不到此入口。'},404);
      const body=await readBody(req);
      if(url.pathname==='/api/plan') return json(await withData(()=>cleaner.plan(validateSelection(body))));
      if(url.pathname==='/api/plan-repair') return json(await withData(()=>cleaner.planRepair()));
      if(url.pathname==='/api/shutdown') {
        if(busy) return json({error:'正在处理其他操作，请完成后退出。'},409);
        json({ok:true});void close();return;
      }
      if(typeof body.id!=='string' || !body.id || body.id.length>200) throw requestError('操作编号无效。');
      const result=await withData(()=>url.pathname==='/api/execute'?cleaner.execute(body.id):cleaner.executeRepair(body.id));
      json({ok:true,result});
    } catch(e) {req.resume();json({error:safeMessage(e),code:e.code || null}, e.status || 400);}
  });
  // Closing through the API or a CLI signal waits for any transaction already in progress.
  function close() {
    if(closePromise) return closePromise;
    closing=true;
    const idle=busy?new Promise(resolve=>idleWaiters.push(resolve)):Promise.resolve();
    closePromise=Promise.all([idle,new Promise(resolve=>server.close(resolve))]).then(()=>undefined);
    return closePromise;
  }
  server.requestTimeout=20000;server.headersTimeout=15000;
  await new Promise((resolve,reject)=>{
    const onError=error=>reject(error);
    server.once('error',onError);
    server.listen(port,'127.0.0.1',()=>{server.off('error',onError);resolve();});
  });
  origin=`http://127.0.0.1:${server.address().port}`;
  const launchUrl=`${origin}/#token=${token}`;
  if(options.openBrowser!==false) {
    const powershell=path.join(process.env.SystemRoot || 'C:\\Windows','System32','WindowsPowerShell','v1.0','powershell.exe');
    try {await runFile(powershell,['-NoProfile','-NonInteractive','-Command',`Start-Process '${launchUrl}'`],{windowsHide:true,timeout:10000});}
    catch {await close();throw new Error('无法自动打开浏览器。请在终端运行 node server.cjs --no-open，并打开终端显示的本机链接。');}
  }
  return {server,cleaner,origin,token,launchUrl,dataRoot,transactionRoot,close};
}

async function main() {
  const options=parseArgs(process.argv.slice(2));
  if(options.help) {
    console.log('Codex Cleaner\nUsage: node server.cjs [--no-open] [--data-root PATH] [--transaction-root PATH] [--port 0..65535]\n--no-open prints a temporary local access link instead of opening your browser. Keep that link private.');
    return;
  }
  const app=await startServer(options);
  const shutdown=()=>{void app.close().then(()=>{process.off('SIGINT',shutdown);process.off('SIGTERM',shutdown);});};
  process.on('SIGINT',shutdown);process.on('SIGTERM',shutdown);
  // The Windows launcher redirects stdout to a diagnostic log; never write its token there.
  if(options.openBrowser===false) console.log(app.launchUrl);
  console.log('Codex Cleaner ready: '+app.origin+'/');
}
if(require.main===module) main().catch(e=>{console.error(safeMessage(e));process.exitCode=1;});
module.exports={startServer,detectCodex,validateSelection,parseArgs};
