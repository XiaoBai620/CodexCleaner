'use strict';
const test=require('node:test');
const assert=require('node:assert/strict');
const http=require('node:http');
const fs=require('node:fs');
const path=require('node:path');
const {startServer,validateSelection,parseArgs}=require('../server.cjs');
const {version}=require('../package.json');
const {fixture,rows}=require('./fixture.cjs');
function request(origin,endpoint,{method='GET',token,body,headers={}}={}) {
  return new Promise((resolve,reject)=>{
    const payload=body===undefined?null:JSON.stringify(body);
    const req=http.request(new URL(endpoint,origin),{method,headers:{...(token?{'X-Cleaner-Token':token}:{}),...(payload?{'Content-Type':'application/json','Content-Length':Buffer.byteLength(payload)}:{}),...headers}},res=>{
      const chunks=[];res.on('data',chunk=>chunks.push(chunk));res.on('end',()=>{const raw=Buffer.concat(chunks).toString('utf8');let value;try{value=JSON.parse(raw);}catch{value=raw;}resolve({status:res.statusCode,headers:res.headers,body:value});});
    });req.on('error',reject);req.end(payload);
  });
}
test('HTTP access guards and one-click permanent deletion operate on synthetic data only',async t=>{
  const f=fixture();let active=false;
  const app=await startServer({dataRoot:f.dataRoot,trashRoot:f.trashRoot,openBrowser:false,processCheck:async()=>({running:active,processes:active?[{name:'codex',pid:42}]:[]})});
  t.after(async()=>{await app.close();f.dispose();});
  assert.equal(app.server.address().address,'127.0.0.1');
  assert.equal((await request(app.origin,'/api/state')).status,401);
  assert.equal((await request(app.origin,'/api/state',{token:'a'.repeat(64)})).status,401);
  assert.equal((await request(app.origin,'/api/state',{token:'é'.repeat(64)})).status,401);
  assert.equal((await request(app.origin,'/api/state',{token:app.token,headers:{Host:'evil.example'}})).status,403);
  assert.equal((await request(app.origin,'/api/state',{token:app.token,headers:{Origin:'https://evil.example'}})).status,403);
  assert.equal((await request(app.origin,'/api/state',{token:app.token,headers:{'Sec-Fetch-Site':'cross-site'}})).status,403);
  const state=await request(app.origin,'/api/state',{token:app.token});assert.equal(state.status,200);assert.equal(state.body.chats.length,2);assert.equal(state.body.version,version);assert.equal(state.body.apiVersion,1);
  assert.equal(state.headers['cache-control'],'no-store');assert.ok(state.headers['content-security-policy'].includes("frame-ancestors 'none'"));
  const sel={chatIds:[f.ids[0]],clearMemories:false,clearSharedHistory:false};
  const planned=await request(app.origin,'/api/plan',{method:'POST',token:app.token,body:sel});assert.equal(planned.status,200);assert.ok(planned.body.id);
  active=true;
  const blocked=await request(app.origin,'/api/execute',{method:'POST',token:app.token,body:{id:planned.body.id}});assert.equal(blocked.status,400);assert.equal(rows(f.dataRoot,'state_5.sqlite','threads').length,2);
  active=false;
  const deleted=await request(app.origin,'/api/execute',{method:'POST',token:app.token,body:{id:planned.body.id}});assert.equal(deleted.status,200);assert.equal(rows(f.dataRoot,'state_5.sqlite','threads').length,1);
  assert.equal((await request(app.origin,'/api/restore',{method:'POST',token:app.token,body:{id:'obsolete'}})).status,404);
  assert.equal((await request(app.origin,'/api/purge',{method:'POST',token:app.token,body:{id:'obsolete'}})).status,404);
  const after=await request(app.origin,'/api/state',{token:app.token});assert.equal(Object.hasOwn(after.body,'trash'),false,'removed recovery area is not exposed by API');
});
test('HTTP selection validation preserves the explicit shared-history choice',()=>{
  const value=validateSelection({chatIds:[],clearMemories:false,clearSharedHistory:true});assert.equal(value.clearSharedHistory,true);
});

test('HTTP stale-reference repair uses a read-only preview and one guarded execution',async t=>{
  const f=fixture();let active=false;
  const globalFile=path.join(f.dataRoot,'.codex-global-state.json');const before=JSON.parse(fs.readFileSync(globalFile,'utf8'));const stale='33333333-3333-4333-8333-333333333333';before['pinned-thread-ids']=[f.ids[0],stale];before['app-server-projects-migration-by-host']={['local:'+f.dataRoot]:{version:1,projectsMigrated:true,threadAssignmentsMigrated:false,pendingThreadAssignmentIds:[stale]}};fs.writeFileSync(globalFile,JSON.stringify(before));
  const app=await startServer({dataRoot:f.dataRoot,trashRoot:f.trashRoot,openBrowser:false,processCheck:async()=>({running:active,processes:active?[{name:'codex',pid:42}]:[]})});
  t.after(async()=>{await app.close();f.dispose();});
  const planned=await request(app.origin,'/api/plan-repair',{method:'POST',token:app.token,body:{}});assert.equal(planned.status,200);assert.ok(planned.body.id);
  assert.deepEqual(JSON.parse(fs.readFileSync(globalFile,'utf8')),before);
  active=true;assert.equal((await request(app.origin,'/api/execute-repair',{method:'POST',token:app.token,body:{id:planned.body.id}})).status,400);
  active=false;const result=await request(app.origin,'/api/execute-repair',{method:'POST',token:app.token,body:{id:planned.body.id}});assert.equal(result.status,200);
  assert.deepEqual(JSON.parse(fs.readFileSync(globalFile,'utf8'))['pinned-thread-ids'],[f.ids[0]]);assert.equal(rows(f.dataRoot,'state_5.sqlite','threads').length,2);
});

test('HTTP rejects oversized bodies, invalid JSON shapes, and misleading content types',async t=>{
  const f=fixture();
  const app=await startServer({dataRoot:f.dataRoot,transactionRoot:f.trashRoot,openBrowser:false,processCheck:async()=>({running:false,processes:[]})});
  t.after(async()=>{await app.close();f.dispose();});
  const post=body=>request(app.origin,'/api/plan',{method:'POST',token:app.token,body});
  assert.equal((await post(null)).status,400);
  assert.equal((await post([])).status,400);
  assert.equal((await post({extra:'x'.repeat(65536)})).status,413);
  assert.equal((await request(app.origin,'/api/plan',{method:'POST',token:app.token,body:{},headers:{'Content-Type':'application/json-not-really'}})).status,415);
  assert.equal((await request(app.origin,'/api/state',{token:app.token})).status,200,'invalid requests leave the server usable');
});

test('a storage read excludes cleanup, repair, and shutdown until its snapshot completes',async t=>{
  const f=fixture(),entered=Promise.withResolvers(),gate=Promise.withResolvers();
  const app=await startServer({dataRoot:f.dataRoot,transactionRoot:f.trashRoot,openBrowser:false,processCheck:async()=>({running:false,processes:[]})});
  t.after(async()=>{gate.resolve();await app.close();f.dispose();});
  const snapshot=app.cleaner.snapshot.bind(app.cleaner);
  app.cleaner.snapshot=async()=>{entered.resolve();await gate.promise;return snapshot();};
  const pending=request(app.origin,'/api/state',{token:app.token});
  await entered.promise;
  for(const [endpoint,method,body] of [
    ['/api/state','GET'],['/api/file?path=memories','GET'],
    ['/api/plan','POST',{chatIds:[f.ids[0]],clearMemories:false}],
    ['/api/plan-repair','POST',{}],['/api/execute','POST',{id:'placeholder'}],
    ['/api/execute-repair','POST',{id:'placeholder'}],['/api/shutdown','POST',{}],
  ]) assert.equal((await request(app.origin,endpoint,{method,body,token:app.token})).status,409,endpoint);
  assert.equal((await request(app.origin,'/api/process',{token:app.token})).status,200);
  gate.resolve();assert.equal((await pending).status,200);
  assert.equal(rows(f.dataRoot,'state_5.sqlite','threads').length,2);
});

test('a running cleanup excludes reads and new previews, and graceful close waits for its result',async t=>{
  const f=fixture(),entered=Promise.withResolvers(),gate=Promise.withResolvers();
  let hold=false;
  const app=await startServer({dataRoot:f.dataRoot,transactionRoot:f.trashRoot,openBrowser:false,processCheck:async()=>{
    if(hold) {entered.resolve();await gate.promise;}
    return {running:false,processes:[]};
  }});
  t.after(async()=>{gate.resolve();await app.close();f.dispose();});
  const planned=await request(app.origin,'/api/plan',{method:'POST',token:app.token,body:{chatIds:[f.ids[0]],clearMemories:false}});
  assert.equal(planned.status,200);
  hold=true;
  const pending=request(app.origin,'/api/execute',{method:'POST',token:app.token,body:{id:planned.body.id}});
  await entered.promise;
  assert.equal((await request(app.origin,'/api/state',{token:app.token})).status,409);
  assert.equal((await request(app.origin,'/api/file?path=memories',{token:app.token})).status,409);
  assert.equal((await request(app.origin,'/api/plan-repair',{method:'POST',token:app.token,body:{}})).status,409);
  let closed=false;
  const closing=app.close().then(()=>{closed=true;});
  await new Promise(resolve=>setImmediate(resolve));assert.equal(closed,false);
  gate.resolve();
  assert.equal((await pending).status,200);await closing;
  assert.equal(rows(f.dataRoot,'state_5.sqlite','threads').length,1);
  assert.equal(app.server.listening,false);
});

test('shutdown stops listening and invalid process-check results never report ready',async t=>{
  const f=fixture();
  const app=await startServer({dataRoot:f.dataRoot,transactionRoot:f.trashRoot,openBrowser:false,processCheck:async()=>({running:false})});
  t.after(async()=>{await app.close();f.dispose();});
  const state=await request(app.origin,'/api/process',{token:app.token});
  assert.equal(state.body.verified,false);assert.equal(state.body.running,true);
  assert.equal((await request(app.origin,'/api/shutdown',{method:'POST',token:app.token,body:{}})).status,200);
  await app.close();assert.equal(app.server.listening,false);
});

test('CLI options reject missing values, duplicate options, and invalid ports before startup',async()=>{
  assert.deepEqual(parseArgs(['--no-open','--data-root','synthetic data','--transaction-root','synthetic transactions','--port','0']),{openBrowser:false,dataRoot:'synthetic data',transactionRoot:'synthetic transactions',port:0});
  assert.deepEqual(parseArgs(['--help']),{help:true});
  for(const args of [['--data-root'],['--transaction-root','--no-open'],['--port'],['--port','NaN'],['--port','1.5'],['--port','-1'],['--port','65536'],['--no-open','--no-open'],['--unknown']]) assert.throws(()=>parseArgs(args),args.join(' '));
  for(const port of [-1,65536,1.5,'80',NaN]) await assert.rejects(startServer({port,openBrowser:false}),/端口/);
  assert.throws(()=>validateSelection({chatIds:[''],clearMemories:false}),/清理范围/);
  assert.throws(()=>validateSelection({chatIds:Array(501).fill('synthetic'),clearMemories:false}),/清理范围/);
});

test('fixtures live in isolated system temporary directories and remove their synthetic data',()=>{
  const f=fixture();
  assert.equal(path.dirname(f.base),fs.realpathSync(require('node:os').tmpdir()));
  assert.match(path.basename(f.base),/^codex-cleaner-test-/);
  f.dispose();assert.equal(fs.existsSync(f.base),false);
  f.dispose();
});
