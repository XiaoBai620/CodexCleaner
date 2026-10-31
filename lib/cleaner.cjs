'use strict';

// Local-only storage engine. Never follows links and never accepts arbitrary SQL.
// Supported schemas were inspected on 2026-10-31. A schema change disables writes.
const fs = require('node:fs');
const fsp = fs.promises;
const path = require('node:path');
const crypto = require('node:crypto');
const os = require('node:os');
const { DatabaseSync, backup } = require('node:sqlite');

const SCHEMAS = Object.freeze({
  'state_5.sqlite': '8c829eff66f38fbaa1edc0a89e2c148f755569dab3ae6bd3bd572e8cf636dc8c',
  'thread_history_1.sqlite': '96ed60adb80f45d47f2735ef546fb6880d87552b1c85df5432b41e3884e49610',
  'memories_1.sqlite': 'da722ff7607ab390384b30c26de1e45bf0a6a991ceaf3a9ff24bb06046668f55',
  'logs_2.sqlite': '0ced295540ebe7cdee7f1be50bc9989a2db63b8b33e9588c7c1f2918eb06cced',
  'goals_1.sqlite': '94f1ccfb1a050966dee766b9c0a037040882ac7eb2b0b38571163792d1f4bdc9',
  'queue_1.sqlite': '93dbfc824a719acb2a513bc4de1932f7ab3ee979bb197f5638e3fe5b61fd5553',
  'sqlite/codex-dev.db': '55f16b86b9e32e53b6c5dac23df73002781a3de8294c8aa840bb3424e8dc808e',
});
const MEMORY_DIRS = ['memories', 'memories_v2', 'memories_extensions'];
const GLOBAL_FILES = ['.codex-global-state.json', '.codex-global-state.json.bak'];
const MAX_JSON_BYTES = 32 * 1024 * 1024;
const PLAN_TTL = 15 * 60 * 1000;
const ID_RE = /^[A-Za-z0-9_-]{1,128}$/;
const DB_ROLES = {
  'state_5.sqlite': '聊天标题、预览、会话路径和项目索引；只删除选中聊天的记录，保留项目。',
  'thread_history_1.sqlite': '完整聊天消息、工具输出、轮次和实时历史缓存。',
  'memories_1.sqlite': '按聊天提取的记忆、记忆整理任务与进度。',
  'logs_2.sqlite': '运行日志，部分含聊天、反馈或工具文本；可按聊天或清理所有共享日志。',
  'goals_1.sqlite': '聊天的持续任务目标、进度和延迟继续状态。',
  'queue_1.sqlite': '尚未处理的聊天消息及队列版本。',
  'sqlite/codex-dev.db': '桌面聊天目录、时间线、收件箱和自动化；名称含 dev 仍可能存正式聊天内容。',
};
const PURPOSES = [
  [/^(sessions|archived_sessions)(\/|$)/, '聊天记录', '会话原始记录（JSONL），包含消息和工具调用。仅处理数据库精确关联的会话文件。', '按聊天清理'],
  [/^(memories|memories_v2|memories_extensions)(\/|$)/, '记忆', '模型整理出的本机记忆资料；可能融合多条聊天，无法可靠按聊天拆分。', '全部记忆可选清理'],
  [/^session_index\.jsonl$/, '聊天索引', '会话 ID、标题和更新时间的快速索引；按准确会话 ID 同步清理。', '按聊天清理'],
  [/^history\.jsonl$/, '输入历史', '命令行输入历史；仅删除有明确 session_id 的选中会话记录。', '按聊天清理'],
  [/^\.codex-global-state\.json(\.bak)?$/, '桌面状态', '桌面设置、每条聊天的界面状态与输入历史；只改识别出的聊天字段，保留其余设置。', '精确字段清理'],
  [/^(config\.toml|AGENTS\.md|rules)(\/|$)/, '设置', '用户配置、持久行为说明或命令规则。可能包含个人信息，但本工具不会改动。', '保留'],
  [/^(auth\.json|\.credentials|\.credentials\.json|\.config)(\/|$)/, '登录凭据', '登录、认证或集成配置；不显示文件内容，不纳入聊天清理。', '保留'],
  [/^(skills|plugins|vendor_imports|bin|runtimes|tools)(\/|$)/, '插件与运行环境', '已安装技能、插件、依赖和运行环境。技能里的用户资料需自行审阅。', '保留'],
  [/^(worktrees|projects|worktree_snapshots|worktree-backups)(\/|$)/, '项目与工作树', '项目代码、工作树或其恢复快照；删除聊天不会删除这些文件。', '保留'],
  [/^automations(\/|$)/, '自动化', '定时任务的定义和提示词。有明确目标引用时会显示具体自动化名称，请先在 Codex 中处理该任务。', '保留'],
  [/^(log|logs|otel|traces)(\/|$)/, '诊断日志', '诊断日志可能有用户输入片段，但归属不能可靠确定；本版本仅清理已识别 SQLite 日志。', '保留，可能含残留'],
  [/^(transcription-history\.jsonl|dictation-history|audio|voice)(\/|$)/, '语音与听写', '可能保存语音、听写或转录内容；目前没有可靠的按聊天映射。', '保留，可能含残留'],
  [/^(shell_snapshots|shell-snapshots)(\/|$)/, '终端环境', '会话终端环境快照，可能带有路径或环境变量；本版本保留。', '保留，可能含残留'],
  [/^(visualizations|browser|browser-profile|browser_profiles|browser-contexts)(\/|$)/, '浏览器或可视化', '浏览器状态或可视化产物可能与聊天有关；本版本保留。', '保留，可能含残留'],
  [/^(tmp|cache|caches|downloads)(\/|$)/, '缓存与临时文件', '缓存、下载或临时数据，内容来源不一定能确认；不会笼统删除。', '保留'],
];

function fail(message) { throw new Error(message); }
function sha(value) { return crypto.createHash('sha256').update(value).digest('hex'); }
function normalPath(value) {
  if (process.platform !== 'win32') return path.resolve(value);
  const unprefixed = value.startsWith('\\\\?\\UNC\\') ? '\\\\' + value.slice(8) : value.startsWith('\\\\?\\') ? value.slice(4) : value;
  return path.resolve(unprefixed);
}
function slash(value) { return value.split(path.sep).join('/'); }
function samePath(a, b) { return process.platform === 'win32' ? a.toLowerCase() === b.toLowerCase() : a === b; }
function inside(root, target) {
  const rel = path.relative(root, target);
  return rel === '' || (!rel.startsWith(`..${path.sep}`) && rel !== '..' && !path.isAbsolute(rel));
}
function validRelative(rel) {
  if (typeof rel !== 'string' || !rel || rel.includes('\0') || path.isAbsolute(rel) || /^[A-Za-z]:/.test(rel)) fail('路径必须是数据目录内的相对路径。');
  const parts = rel.replace(/\\/g, '/').split('/');
  if (parts.some(p => !p || p === '.' || p === '..' || /[:\x00-\x1f]/.test(p) || /[. ]$/.test(p))) fail('路径含有不允许的部分。');
  return parts.join('/');
}
function describe(rel) {
  const normalized = rel.replace(/\\/g, '/');
  const baseDb = normalized.replace(/-(wal|shm|journal)$/, '');
  if (DB_ROLES[baseDb]) return { category: '数据库', purpose: DB_ROLES[baseDb] + (baseDb !== normalized ? ' 此文件为 SQLite 日志/共享内存，不能单独删除。' : ''), policy: '数据库内精确清理' };
  for (const [pattern, category, purpose, policy] of PURPOSES) if (pattern.test(normalized)) return { category, purpose, policy };
  if (/\.(sqlite|db)(-(wal|shm|journal))?$/.test(normalized)) return { category: '未识别数据库', purpose: '本版本未验证此数据库的用途和结构；为避免破坏，保持原样。', policy: '保留' };
  return { category: '未识别文件', purpose: '尚不能确认这个文件的具体用途。目录名称只能提供线索，不证明文件内容；本工具保留它。', policy: '保留' };
}
function schemaHash(db) {
  const defs = db.prepare("SELECT type,name,sql FROM sqlite_schema WHERE type IN ('table','trigger') AND name NOT LIKE 'sqlite_%'").all();
  return sha(defs.sort((a,b) => `${a.type}:${a.name}`.localeCompare(`${b.type}:${b.name}`, 'en')).map(r => `${r.type}:${r.name}:${r.sql.replace(/\r\n/g, '\n').trim()}`).join('\n'));
}
function readDb(file, writable = false) {
  let db;
  try { db = new DatabaseSync(file, { readOnly: !writable, enableForeignKeyConstraints: true, timeout: 1000 }); }
  catch (error) {
    // Some WAL databases cannot be opened read-only without creating sidecars.
    // Inspect a verified stable private copy, never create files in the source.
    if (writable || error.errcode !== 14) throw error;
    for (const suffix of ['-wal','-journal']) if (fs.existsSync(file+suffix) && fs.statSync(file+suffix).size) throw error;
    const temporary = fs.mkdtempSync(path.join(os.tmpdir(),'codex-cleaner-inspect-'));
    const copy = path.join(temporary,'inspection.sqlite');
    const dispose = () => { for (const suffix of ['','-wal','-shm','-journal']) fs.rmSync(copy+suffix,{force:true}); fs.rmdirSync(temporary); };
    try {
      const before = sha(fs.readFileSync(file));
      fs.copyFileSync(file,copy,fs.constants.COPYFILE_EXCL);
      if (sha(fs.readFileSync(copy))!==before || sha(fs.readFileSync(file))!==before) fail('数据库正在变化，请退出 Codex 后重新检查。');
      for (const suffix of ['-wal','-journal']) if (fs.existsSync(file+suffix) && fs.statSync(file+suffix).size) fail('数据库日志正在变化，请退出 Codex 后重新检查。');
      db = new DatabaseSync(copy,{readOnly:true,enableForeignKeyConstraints:true,timeout:1000});
      if (db.prepare('PRAGMA quick_check').get().quick_check!=='ok') fail('数据库副本一致性校验未通过。');
      const close = db.close.bind(db);
      db.close = () => { try { close(); } finally { dispose(); } };
    } catch(e) { try { db?.close(); } catch {} try { dispose(); } catch {} throw e; }
  }
  try { db.exec('PRAGMA busy_timeout=1000'); if (!writable) db.exec('PRAGMA query_only=ON'); return db; }
  catch (e) { db.close(); throw e; }
}
function qid(name) { return `"${name.replace(/"/g, '""')}"`; }
function sizeNumber(value) { const n = Number(value); return Number.isSafeInteger(n) ? n : null; }
function tomlString(token) {
  if (/^'[^']*'$/.test(token)) return token.slice(1, -1);
  if (!/^"(?:[^"\\\x00-\x1f]|\\(?:[btnfr"\\]|u[0-9a-fA-F]{4}|U[0-9a-fA-F]{8}))*"$/.test(token)) fail('自动化目标使用了未验证的 TOML 字符串格式。');
  const escapes = { b: '\b', t: '\t', n: '\n', f: '\f', r: '\r', '"': '"', '\\': '\\' };
  return token.slice(1, -1).replace(/\\(u[0-9a-fA-F]{4}|U[0-9a-fA-F]{8}|[btnfr"\\])/g, (_, code) => {
    if (Object.hasOwn(escapes, code)) return escapes[code];
    const scalar = parseInt(code.slice(1), 16);
    if (scalar > 0x10ffff || (scalar >= 0xd800 && scalar <= 0xdfff)) fail('自动化 TOML 字符串含无效 Unicode 字符。');
    return String.fromCodePoint(scalar);
  });
}
function automationTomlTargets(text) {
  const targets=[];let multiline=null,inTable=false;
  for(const original of text.replace(/^\uFEFF/, '').split(/\r?\n/)) {
    let line='',quote=null;
    for(let i=0;i<original.length;i++) {
      if(multiline) {
        if(original.slice(i,i+3)===multiline) {
          let backslashes=0;
          if(multiline==='"""') for(let j=i-1;j>=0 && original[j]==='\\';j--) backslashes++;
          if(backslashes%2===0) { multiline=null;i+=2; }
        }
        continue;
      }
      const char=original[i];
      if(quote) {
        line+=char;
        if(quote==='"' && char==='\\') { if(i+1<original.length) line+=original[++i]; }
        else if(char===quote) quote=null;
      } else if(char==='#') break;
      else if(original.slice(i,i+3)==='"""' || original.slice(i,i+3)==="'''") { multiline=original.slice(i,i+3);i+=2; }
      else { line+=char;if(char==='"' || char==="'") quote=char; }
    }
    if(/^\s*\[/.test(line)) inTable=true;
    if(inTable) continue;
    const assignment=line.match(/^\s*([A-Za-z0-9_-]+|"(?:\\.|[^"\\])*"|'[^']*')\s*=\s*(.*?)\s*$/);
    if(!assignment) continue;
    const key=/^["']/.test(assignment[1]) ? tomlString(assignment[1]) : assignment[1];
    if(!['target_thread_id','targetThreadId'].includes(key)) continue;
    const target=tomlString(assignment[2]);
    if(!ID_RE.test(target)) fail('自动化目标使用了未验证的 TOML 格式，请在 Codex 中检查该自动化。');
    targets.push(target);
  }
  if(multiline) fail('自动化 TOML 包含未闭合的多行字符串。');
  return targets;
}
function cleanableFile(rel) {
  return GLOBAL_FILES.includes(rel) || ['session_index.jsonl','history.jsonl'].includes(rel)
    || /^(sessions|archived_sessions)\/.+\.jsonl$/.test(rel)
    || MEMORY_DIRS.some(dir=>rel===dir || rel.startsWith(dir+'/'));
}

class Cleaner {
  constructor({ dataRoot, transactionRoot, trashRoot, processCheck } = {}) {
    if (!dataRoot || !(transactionRoot || trashRoot)) fail('需要明确的数据目录和临时事务目录。');
    if (transactionRoot && trashRoot && !samePath(normalPath(transactionRoot), normalPath(trashRoot))) fail('临时事务目录参数不一致。');
    this.dataRoot = normalPath(dataRoot);
    this.trashRoot = normalPath(transactionRoot || trashRoot);
    if (inside(this.dataRoot, this.trashRoot) || inside(this.trashRoot, this.dataRoot)) fail('临时事务目录和数据目录不能互相包含。');
    this.processCheck = processCheck;
    this.plans = new Map();
    this.busy = false;
    this.readers = 0;
  }

  // Reject links/junctions at every existing path component, including root ancestors.
  async _safe(root, relative = '', { missing = false, createRoot = false } = {}) {
    const target = relative ? path.resolve(root, validRelative(relative)) : root;
    if (!inside(root, target)) fail('路径越出受控目录。');
    const parsed = path.parse(target);
    const components = path.relative(parsed.root, target).split(path.sep).filter(Boolean);
    let current = parsed.root;
    let absent = false;
    for (const part of components) {
      current = path.join(current, part);
      if (absent) continue;
      let st;
      try { st = await fsp.lstat(current); }
      catch (e) { if (e.code === 'ENOENT' && (missing || createRoot)) { absent = true; continue; } throw e; }
      if (st.isSymbolicLink()) fail(`拒绝符号链接或目录联接：${current}`);
      // The JS resolver also works where Windows denies native GetFinalPathName
      // on a readable ancestor. It still resolves links; lstat already rejects them.
      const real = fs.realpathSync(current);
      if (!samePath(normalPath(real), normalPath(current))) fail(`拒绝重解析路径：${current}`);
    }
    return target;
  }

  async _data(rel, missing = false) { return this._safe(this.dataRoot, rel, { missing }); }
  async _trash(rel = '', missing = false) { return this._safe(this.trashRoot, rel, { missing, createRoot: missing }); }
  async _stat(rel) {
    try { const file = await this._data(rel); return await fsp.lstat(file); }
    catch(e) { if (e.code === 'ENOENT') return null; throw e; }
  }
  async _fingerprint(root, rel) {
    const file = await this._safe(root, rel, { missing: true });
    let st;
    try { st = await fsp.lstat(file); } catch(e) { if (e.code === 'ENOENT') return { exists: false }; throw e; }
    if (!st.isFile() || st.nlink > 1) fail(`拒绝特殊文件或硬链接：${rel}`);
    // An empty WAL is equivalent to no WAL; read-only SQLite opens may create it.
    if (rel.endsWith('-wal') && st.size === 0) return { exists: false };
    const hash = crypto.createHash('sha256');
    for await (const chunk of fs.createReadStream(file)) hash.update(chunk);
    return { exists: true, sizeBytes: st.size, sha256: hash.digest('hex') };
  }
  async _fingerprints(rels) {
    const result = {};
    // SHM is a coordination index and can change during an innocent read. WAL and
    // the main DB carry persistent content and are the stale-preview guard.
    for (const rel of [...new Set(rels)].filter(r=>!r.endsWith('-shm')).sort()) result[rel] = await this._fingerprint(this.dataRoot, rel);
    return result;
  }
  async _assertFingerprints(expected) {
    for (const [rel, old] of Object.entries(expected)) {
      const now = await this._fingerprint(this.dataRoot, rel);
      if (JSON.stringify(now) !== JSON.stringify(old)) fail(`数据在预览后已改变，请刷新后重新选择：${rel}`);
    }
  }

  async _withRead(operation) {
    if (this.busy) fail('清理正在进行，请完成后再刷新或预览。');
    this.readers++;
    try { return await operation(); }
    finally { this.readers--; }
  }

  async _planBaseline() {
    const automationFiles = await this._automationFiles();
    const tracked = [...GLOBAL_FILES, 'history.jsonl', 'session_index.jsonl', ...automationFiles];
    for (const rel of Object.keys(SCHEMAS)) for (const suffix of ['', '-wal', '-journal']) tracked.push(rel + suffix);
    return { fingerprints: await this._fingerprints(tracked), automationFiles };
  }

  async _processGuard() {
    if (typeof this.processCheck !== 'function') fail('没有可用的进程检测器；禁止写入。');
    const state = await this.processCheck();
    if (!state || typeof state.running !== 'boolean' || !Array.isArray(state.processes)) fail('无法可靠确认 Codex 是否退出；禁止写入。');
    if (state.running || state.processes.length) fail('请先完全退出 Codex 桌面程序、命令行与相关后台进程，再重试。');
  }
  async _exclusive(operation) {
    if (this.busy) fail('另一项清理或恢复正在进行。');
    if (this.readers) fail('预览或文件检查尚未完成，请稍后重试。');
    this.busy = true;
    let lockHandle, lockFile;
    try {
      await this._processGuard();
      const root = await this._trash('',true);
      await fsp.mkdir(root,{recursive:true,mode:0o700});
      lockFile=await this._trash('.mutation.lock',true);
      try { lockHandle=await fsp.open(lockFile,'wx',0o600); }
      catch(e) {
        if(e.code!=='EEXIST') throw e;
        const reclaimFile=await this._trash('.mutation-reclaim.lock',true);
        let reclaim;
        try { reclaim=await fsp.open(reclaimFile,'wx',0o600); }
        catch(error) {
          if(error.code!=='EEXIST') throw error;
          fail(`存在未完成的写入锁检查，无法确认状态。请关闭其他清理窗口并检查故障文件：${reclaimFile}；本次未执行。`);
        }
        try {
        let stale=false;
        try {
          const lock=JSON.parse(await fsp.readFile(await this._trash('.mutation.lock'),'utf8'));
          if(!Number.isSafeInteger(lock.pid) || lock.pid<=0 || !samePath(normalPath(lock.dataRoot),this.dataRoot)) fail('锁文件信息无效。');
          try { process.kill(lock.pid,0); } catch(error) { if(error.code==='ESRCH') stale=true;else throw error; }
        } catch(error) { fail(`无法验证清理工具的写入锁，请关闭其他清理窗口后重试：${error.message}`); }
        if(!stale) fail('另一个清理工具正在写入，请等待其完成。');
        await fsp.unlink(await this._trash('.mutation.lock'));
        lockHandle=await fsp.open(lockFile,'wx',0o600);
        } finally { await reclaim.close().catch(()=>{});await fsp.unlink(reclaimFile).catch(()=>{}); }
      }
      await lockHandle.writeFile(JSON.stringify({pid:process.pid,createdAt:new Date().toISOString(),dataRoot:this.dataRoot}));
      await this._processGuard();
      return await operation();
    }
    finally {
      if(lockHandle) { await lockHandle.close().catch(()=>{}); await fsp.unlink(lockFile).catch(()=>{}); }
      this.busy = false;
    }
  }

  async _metadata(relativePath) {
    const rel = validRelative(relativePath);
    // Inventory can describe a link but never traverse it.
    const parent = path.dirname(rel);
    const parentFile = parent === '.' ? await this._safe(this.dataRoot) : await this._data(slash(parent));
    const file = path.join(parentFile, path.basename(rel));
    const st = await fsp.lstat(file);
    const link = st.isSymbolicLink();
    return { path: rel, kind: link ? 'link' : st.isDirectory() ? 'directory' : st.isFile() ? 'file' : 'special', type: link ? 'link' : st.isDirectory() ? 'directory' : 'file', sizeBytes: st.isFile() ? st.size : null, modifiedAt: st.mtime.toISOString(), ...describe(rel), ...(link ? { category: '链接', policy: '保留，禁止跟随', purpose: '符号链接或目录联接可能指向数据目录外；不会展开或修改。' } : {}) };
  }
  async inspectFile(relativePath) {
    return this._withRead(() => this._inspectFile(relativePath));
  }
  async _inspectFile(relativePath) {
    const item = await this._metadata(relativePath);
    if (item.kind === 'directory') {
      const dir = await this._data(item.path);
      const entries = await fsp.readdir(dir);
      item.children = [];
      for (const name of entries.sort()) {
        try { item.children.push(await this._metadata(`${item.path}/${name}`)); }
        catch(e) { item.children.push({ path: `${item.path}/${name}`, kind: 'unreadable', category: '无法检查', purpose: e.message, policy: '保留', sizeBytes: null }); }
      }
    }
    return item;
  }

  async _inspectDatabases() {
    const databases = {};
    for(const name of await fsp.readdir(await this._safe(this.dataRoot))) {
      if(/^(state|thread_history|memories|logs|goals|queue)_\d+\.sqlite$/.test(name) && !Object.hasOwn(SCHEMAS,name)) databases[name]={present:true,supported:false,reason:'发现其他版本的聊天数据库，本工具尚未验证。'};
    }
    for (const rel of Object.keys(SCHEMAS)) {
      let db;
      try {
        const st = await this._stat(rel);
        if (!st) { databases[rel] = { present: false, supported: true }; continue; }
        if (!st.isFile() || st.nlink > 1) fail('数据库不是独立普通文件。');
        for (const suffix of ['-wal','-shm','-journal']) {
          const sidecar = await this._stat(`${rel}${suffix}`);
          if (sidecar && (!sidecar.isFile() || sidecar.nlink > 1)) fail('数据库附属文件不是独立普通文件。');
        }
        db = readDb(await this._data(rel));
        const signature = schemaHash(db);
        databases[rel] = { present: true, supported: signature === SCHEMAS[rel], signature };
        if (!databases[rel].supported) databases[rel].reason = '数据库结构与已验证版本不同；只允许浏览。';
      } catch(e) { databases[rel] = { present: true, supported: false, reason: e.message }; }
      finally { if (db) db.close(); }
    }
    return databases;
  }

  async _automationTargets(databases) {
    const targets = new Set();
    targets.details=new Map();
    const add=(id,label)=>{ targets.add(id);targets.details.set(id,[...(targets.details.get(id)||[]),label]); };
    if (databases['sqlite/codex-dev.db']?.present && databases['sqlite/codex-dev.db'].supported) {
      const db = readDb(await this._data('sqlite/codex-dev.db'));
      try { for (const r of db.prepare('SELECT id,name,target_thread_id FROM automations WHERE target_thread_id IS NOT NULL').all()) add(r.target_thread_id,`${r.name||r.id}（${r.id}）`); }
      finally { db.close(); }
    }
    // Older automation definitions may exist only as TOML. An exact quoted ID match
    // blocks cleanup regardless of ACTIVE/PAUSED so resumed jobs remain valid.
    const dir = await this._stat('automations');
    if (dir?.isDirectory()) {
      for (const entry of await fsp.readdir(await this._data('automations'), { withFileTypes: true })) {
        if (!entry.isDirectory() || entry.isSymbolicLink()) continue;
        const rel = `automations/${entry.name}/automation.toml`;
        const st = await this._stat(rel);
        if (!st?.isFile()) continue;
        if (st.size > MAX_JSON_BYTES) fail('自动化定义太大，不能可靠验证关联。');
        const text = await fsp.readFile(await this._data(rel), 'utf8');
        for(const id of automationTomlTargets(text)) add(id,rel);
      }
    }
    return targets;
  }

  async _automationFiles() {
    const result=[];
    if((await this._stat('automations'))?.isDirectory()) for(const entry of await fsp.readdir(await this._data('automations'),{withFileTypes:true})) {
      if(!entry.isDirectory() || entry.isSymbolicLink()) continue;
      const rel=`automations/${entry.name}/automation.toml`;
      if((await this._stat(rel))?.isFile()) result.push(rel);
    }
    return result.sort();
  }

  async snapshot() {
    return this._withRead(() => this._snapshot());
  }
  async _snapshot() {
    await this._safe(this.dataRoot);
    const inventory = [];
    const warnings = ['本工具只处理本机已识别的数据；云端、项目文件、第三方备份和未识别文件不在删除范围。'];
    for (const name of (await fsp.readdir(this.dataRoot)).sort()) {
      try { inventory.push(await this._metadata(name)); }
      catch(e) { inventory.push({ path: name, kind: 'unreadable', sizeBytes: null, category: '无法检查', purpose: e.message, policy: '保留' }); }
    }
    const databases = await this._inspectDatabases();
    const unsupported = Object.entries(databases).filter(([,v]) => v.present && !v.supported);
    for (const [name, info] of unsupported) warnings.push(`${name}：${info.reason}`);
    let targets = new Set();
    let automationError = '';
    try { targets = await this._automationTargets(databases); } catch(e) { automationError = e.message; warnings.push(`自动化关联检查失败：${e.message}`); }
    const memoryCounts = new Map();
    if (databases['memories_1.sqlite'].present && databases['memories_1.sqlite'].supported) {
      const db = readDb(await this._data('memories_1.sqlite'));
      try { for (const r of db.prepare('SELECT thread_id, count(*) AS n FROM stage1_outputs GROUP BY thread_id').all()) memoryCounts.set(r.thread_id, Number(r.n)); }
      finally { db.close(); }
    }
    const chats = [];
    if (databases['state_5.sqlite'].present && databases['state_5.sqlite'].supported) {
      const db = readDb(await this._data('state_5.sqlite'));
      try {
        const rows = db.prepare('SELECT id,title,rollout_path,created_at,updated_at,created_at_ms,updated_at_ms,archived FROM threads ORDER BY updated_at DESC').all();
        for (const r of rows) {
          let relativePath = null, sizeBytes = 0,rolloutMissing=false;
          let reason = unsupported.length ? '数据库检查未通过，请查看上方具体原因。' : automationError ? '自动化定义无法解析，无法安全维护其目标。' : '';
          let reasonCode = unsupported.length ? 'database' : automationError ? 'automation-inspection' : '';
          if(targets.has(r.id)) { reason=`此聊天是自动化 ${targets.details.get(r.id).join('、')} 的目标，请先在 Codex 中处理该任务。`;reasonCode='automation'; }
          if (r.rollout_path) {
            try {
              relativePath = this._rolloutRelative(r.rollout_path);
              const st = await this._stat(relativePath);
              if (st && (!st.isFile() || st.nlink > 1)) fail('会话路径不是独立普通文件。');
              sizeBytes = st?.size || 0;
              rolloutMissing=!st;
            } catch(e) { reason = e.message; reasonCode = 'unsafe-path'; }
          }
          const created = Number(r.created_at_ms ?? Number(r.created_at) * 1000);
          const updated = Number(r.updated_at_ms ?? Number(r.updated_at) * 1000);
          chats.push({ id: r.id, title: r.title || '未命名聊天', createdAt: Number.isFinite(created) ? new Date(created).toISOString() : null, updatedAt: Number.isFinite(updated) ? new Date(updated).toISOString() : null, archived: !!r.archived, relativePath, sizeBytes, rolloutMissing, warning:rolloutMissing?'会话文件已缺失，可以清除残留索引。':'', memoryCount: memoryCounts.get(r.id) || 0, automationLinked:targets.has(r.id), canDelete: !reason, reason, reasonCode });
        }
      } finally { db.close(); }
    }
    if (!databases['state_5.sqlite'].present) warnings.push('未发现受支持的聊天索引 state_5.sqlite；不会猜测或删除孤立会话文件。');
    return { dataRoot: this.dataRoot, transactionRoot: this.trashRoot, chats, inventory, databases, warnings, capabilities: { deleteChats: !!databases['state_5.sqlite'].present && !unsupported.length && !automationError, repairReferences:!!databases['state_5.sqlite'].present && !unsupported.length, clearMemories: !unsupported.length, clearSharedHistory: !unsupported.length }, recoveryIssues: await this.recoveryIssues() };
  }

  _rolloutRelative(value) {
    const full = normalPath(path.isAbsolute(value) ? value : path.resolve(this.dataRoot, value));
    if (!inside(this.dataRoot, full)) fail('聊天会话路径在数据目录外，禁止清理。');
    const rel = validRelative(slash(path.relative(this.dataRoot, full)));
    if (!/^(sessions|archived_sessions)\/.+\.jsonl$/.test(rel)) fail('会话文件不在已验证的 sessions / archived_sessions 目录。');
    return rel;
  }

  async _checkRollout(rel, id) {
    const file = await this._data(rel);
    const handle = await fsp.open(file, 'r');
    try {
      const buffer = Buffer.alloc(1024 * 1024);
      const { bytesRead } = await handle.read(buffer, 0, buffer.length, 0);
      const text = buffer.subarray(0, bytesRead).toString('utf8');
      const end = text.indexOf('\n');
      if (end < 0 && bytesRead === buffer.length) fail('会话元数据行太长，无法验证。');
      const first = JSON.parse((end < 0 ? text : text.slice(0,end)).replace(/^\uFEFF/,'').trim());
      if (first.type !== 'session_meta' || first.payload?.id !== id) fail(`会话文件 ID 与聊天索引不一致：${rel}`);
    } finally { await handle.close(); }
  }

  async _walkMemory(rel, result = []) {
    const st = await this._stat(rel);
    if (!st) return result;
    if (st.isFile()) { if (st.nlink > 1) fail(`记忆目录中有硬链接，禁止清理：${rel}`); result.push(rel); return result; }
    if (!st.isDirectory()) fail(`记忆目录存在未知文件类型：${rel}`);
    for (const name of await fsp.readdir(await this._data(rel))) await this._walkMemory(`${rel}/${name}`, result);
    return result;
  }

  _sqlActions(rel, ids, selection) {
    const actions = [];
    const marks = ids.map(() => '?').join(',');
    const add = (table, column = 'thread_id') => { if (ids.length) actions.push({ table, where: `${qid(column)} IN (${marks})`, params: ids }); };
    const all = table => actions.push({ table, where: '1=1', params: [] });
    if (rel === 'state_5.sqlite') {
      if (ids.length) actions.push({ table: 'threads', kind: 'clear-reference', column: 'attached_thread_parent_id', where: `attached_thread_parent_id IN (${marks}) AND id NOT IN (${marks})`, params: [...ids,...ids] });
      add('thread_attachments'); add('thread_dynamic_tools'); add('thread_read_receipts'); add('guardian_review_feedback');
      if (ids.length) actions.push({ table: 'thread_spawn_edges', where: `parent_thread_id IN (${marks}) OR child_thread_id IN (${marks})`, params: [...ids,...ids] });
      add('threads','id');
    } else if (rel === 'thread_history_1.sqlite') {
      add('thread_items'); add('thread_turns'); add('thread_realtime_items'); add('thread_history_projection_state');
    } else if (rel === 'memories_1.sqlite') {
      // consolidation_progress contains only a singleton count, no remembered text.
      // Preserve it because the runtime's recreation behavior is not documented.
      if (selection.clearMemories) { all('stage1_outputs'); all('jobs'); } else add('stage1_outputs');
    } else if (rel === 'logs_2.sqlite') {
      if (selection.clearSharedHistory) all('logs'); else add('logs');
    } else if (rel === 'goals_1.sqlite') { add('thread_goal_continuation_deferrals'); add('thread_goals'); }
    else if (rel === 'queue_1.sqlite') { add('queued_items'); add('queued_thread_revisions'); }
    else if (rel === 'sqlite/codex-dev.db') {
      add('automation_runs'); add('inbox_items');
      for(const table of ['live_visualization_suggestions','local_thread_catalog','local_thread_catalog_scan_entries','thread_timeline_ledger']) {
        if(ids.length) actions.push({table,where:`thread_id IN (${marks}) AND host_id = ?`,params:[...ids,'local']});
      }
    }
    return actions;
  }

  _isLocalHost(host) {
    if (host==='local') return true;
    if (typeof host!=='string' || !host.startsWith('local:')) return false;
    return samePath(normalPath(host.slice(6)),this.dataRoot);
  }

  _readHostIsLocal(reads,host) {
    if(this._isLocalHost(host)) return true;
    const adopted=reads?.legacyMigration?.adoptedHostIds?.local;
    return typeof adopted==='string' && /^local:[a-f0-9]{64}$/i.test(adopted) && host===adopted;
  }

  _recentThreadId(entry,bindings) {
    if(!this._isLocalHost(entry?.hostId) || typeof entry.threadId!=='string') return null;
    if(entry.threadId.startsWith('local:')) {
      const id=entry.threadId.slice(6);return ID_RE.test(id)?id:null;
    }
    if(ID_RE.test(entry.threadId)) return entry.threadId;
    if(entry.threadId.startsWith('client-new-thread:')) {
      const id=bindings?.[entry.threadId];return typeof id==='string' && ID_RE.test(id)?id:null;
    }
    return null;
  }

  _globalLocalIds(value) {
    const ids = new Set();
    const add = id => { if(typeof id==='string' && ID_RE.test(id)) ids.add(id); };
    const list = array => { if(Array.isArray(array)) array.forEach(add); };
    for(const object of [value,value?.['electron-persisted-atom-state']]) {
      if(!object || typeof object!=='object' || Array.isArray(object)) continue;
      const migrations = object['app-server-projects-migration-by-host'];
      for(const [host,state] of Object.entries(migrations||{})) if(this._isLocalHost(host)) list(state?.pendingThreadAssignmentIds);
      const reads = object['electron-thread-read-state-v1'];
      for(const hosts of Object.values(reads?.unreadByIdentity||{})) for(const [host,array] of Object.entries(hosts||{})) if(this._readHostIsLocal(reads,host)) list(array);
      for(const [host,array] of Object.entries(reads?.legacyMigration?.unreadThreadIdsByHostId||{})) if(this._isLocalHost(host)) list(array);
      for(const entry of Object.values(object['recent-workspace-files-v3']||{})) add(this._recentThreadId(entry,object['client-thread-bindings-v1']));
      for(const key of Object.keys(object)) if(key.startsWith('thread-client-id-v1:local%3A')) add(key.slice('thread-client-id-v1:local%3A'.length));
    }
    return ids;
  }

  _cleanGlobal(value, ids, shared, references = []) {
    const selected = new Set(ids);
    let changed = false;
    const removeMap = (object, name) => {
      if (!object || typeof object !== 'object' || Array.isArray(object)) return;
      const map = object[name];
      if (!map || typeof map !== 'object' || Array.isArray(map)) return;
      for (const id of ids) if (Object.hasOwn(map,id)) { delete map[id]; changed = true; references.push({field:name,threadId:id}); }
    };
    const removeArray = (object, name) => {
      if (!Array.isArray(object?.[name])) return;
      const next = object[name].filter(item => { if(typeof item==='string' && selected.has(item)) { references.push({field:name,threadId:item}); return false; } return true; });
      if (next.length !== object[name].length) { object[name] = next; changed = true; }
    };
    const mapKeys = ['thread-project-assignments','thread-writable-roots','thread-projectless-output-directories','thread-workspace-root-hints','queued-follow-ups','thread-descriptions-v1','thread-permission-settings-by-id','heartbeat-thread-permissions-by-id','thread-workspace-draft-tabs-v1','composer-prompt-drafts-v1','composer-prompt-drafts-v2'];
    const containers = [value, value?.['electron-persisted-atom-state']].filter(v => v && typeof v === 'object' && !Array.isArray(v));
    for (const object of containers) {
      for (const name of mapKeys) removeMap(object,name);
      for (const name of ['projectless-thread-ids','pinned-thread-ids','archived-thread-ids']) removeArray(object,name);
      removeMap(object,'prompt-history');
      if (shared && object['prompt-history'] && Object.hasOwn(object['prompt-history'],'global')) { delete object['prompt-history'].global; changed = true; }
      const prefixes = ['thread-reference-capability:','thread-browser-tabs-v1:','thread-tab-routes-v1:','thread-client-id-v1:local%3A','codex-writing-block-deleted-thread-v1:'];
      for (const id of ids) for (const prefix of prefixes) if (Object.hasOwn(object,prefix+id)) { delete object[prefix+id]; changed = true; references.push({field:prefix,threadId:id}); }
      const bindings = object['client-thread-bindings-v1'];
      for(const [host,migration] of Object.entries(object['app-server-projects-migration-by-host']||{})) if(this._isLocalHost(host)) removeArray(migration,'pendingThreadAssignmentIds');
      const reads=object['electron-thread-read-state-v1'];
      for(const hosts of Object.values(reads?.unreadByIdentity||{})) for(const host of Object.keys(hosts||{})) if(this._readHostIsLocal(reads,host)) removeArray(hosts,host);
      for(const host of Object.keys(reads?.legacyMigration?.unreadThreadIdsByHostId||{})) if(this._isLocalHost(host)) removeArray(reads.legacyMigration.unreadThreadIdsByHostId,host);
      const recent=object['recent-workspace-files-v3'];
      if(recent && typeof recent==='object' && !Array.isArray(recent)) for(const [key,entry] of Object.entries(recent)) {
        const id=this._recentThreadId(entry,bindings);
        if(selected.has(id)) { delete recent[key]; changed=true; references.push({field:'recent-workspace-files-v3',threadId:id}); }
      }
      if (bindings && typeof bindings === 'object' && !Array.isArray(bindings)) for (const key of Object.keys(bindings)) if (selected.has(bindings[key])) { references.push({field:'client-thread-bindings-v1',threadId:bindings[key]});delete bindings[key]; changed = true; }
    }
    return changed;
  }

  async _jsonEdits(ids, selection) {
    const edits = [];
    for (const rel of GLOBAL_FILES) {
      const st = await this._stat(rel);
      if (!st) continue;
      if (!st.isFile() || st.size > MAX_JSON_BYTES) fail(`无法安全解析桌面状态：${rel}`);
      const original = await fsp.readFile(await this._data(rel), 'utf8');
      let object;
      try { object = JSON.parse(original.replace(/^\uFEFF/,'')); }
      catch { fail(`桌面状态 JSON 无法解析，停止清理：${rel}`); }
      if (!object || typeof object !== 'object' || Array.isArray(object)) fail(`桌面状态结构未知：${rel}`);
      const references=[];
      if (this._cleanGlobal(object,ids,selection.clearSharedHistory,references)) edits.push({ path: rel, action: 'rewrite', content: JSON.stringify(object,null,2)+'\n', sourceHash:sha(original), references });
    }
    const idSet = new Set(ids);
    for (const [rel,key] of [['session_index.jsonl','id'],['history.jsonl','session_id']]) {
      if (!ids.length) break;
      const st = await this._stat(rel);
      if (!st) continue;
      if (!st.isFile() || st.size > MAX_JSON_BYTES) fail(`索引太大或类型未知：${rel}`);
      const content = await fsp.readFile(await this._data(rel),'utf8');
      const lines = content.split(/(?<=\n)/);
      let changed = false;
      const kept = lines.filter(line => {
        if (!line.trim()) return true;
        let value;
        try { value = JSON.parse(line.replace(/^\uFEFF/,'')); } catch { fail(`索引包含无法解析的 JSON 行：${rel}`); }
        if (idSet.has(value[key])) { changed = true; return false; }
        return true;
      });
      if (changed) edits.push({ path: rel, action: 'rewrite', content: kept.join(''), sourceHash:sha(content) });
    }
    return edits;
  }

  async planRepair() {
    return this._withRead(() => this._planRepair());
  }
  async _planRepair() {
    const baseline = await this._planBaseline();
    const snap=await this.snapshot();
    if(!snap.capabilities.repairReferences) fail('聊天数据库检查未通过，暂不能判断哪些本机引用已经失效。');
    const live=new Set(snap.chats.map(c=>c.id)),candidates=new Set();
    for(const rel of GLOBAL_FILES) {
      const st=await this._stat(rel); if(!st) continue;
      if(!st.isFile() || st.size>MAX_JSON_BYTES) fail(`无法安全解析桌面状态：${rel}`);
      const value=JSON.parse((await fsp.readFile(await this._data(rel),'utf8')).replace(/^\uFEFF/,''));
      for(const id of this._globalLocalIds(value)) candidates.add(id);
    }
    // These stores describe local Codex histories. Do not infer local identity
    // from UUID syntax or unscoped desktop settings shared with cloud chats.
    for(const [rel,table,column] of [['state_5.sqlite','thread_read_receipts','thread_id'],['state_5.sqlite','threads','attached_thread_parent_id'],['thread_history_1.sqlite','thread_history_projection_state','thread_id'],['memories_1.sqlite','stage1_outputs','thread_id'],['goals_1.sqlite','thread_goals','thread_id'],['queue_1.sqlite','queued_thread_revisions','thread_id']]) {
      if(!snap.databases[rel]?.present) continue;
      const db=readDb(await this._data(rel));
      try { for(const row of db.prepare(`SELECT DISTINCT ${qid(column)} AS id FROM ${qid(table)}`).all()) if(typeof row.id==='string' && ID_RE.test(row.id)) candidates.add(row.id); }
      finally { db.close(); }
    }
    if(snap.databases['sqlite/codex-dev.db']?.present) {
      const db=readDb(await this._data('sqlite/codex-dev.db'));
      try { for(const row of db.prepare("SELECT thread_id FROM local_thread_catalog WHERE host_id = 'local' AND source_kind NOT IN ('chatgpt','cloud')").all()) candidates.add(row.thread_id); }
      finally { db.close(); }
    }
    const ids=[...candidates].filter(id=>!live.has(id));
    if(!ids.length) {
      await this._assertFingerprints(baseline.fingerprints);
      return {kind:'repair',empty:true,chatIds:[],referenceRepairs:[],files:[],databases:[],warnings:[],summary:{chats:0,repairThreads:0,referenceRepairs:0,files:0,databaseRows:0}};
    }
    return this._plan({chatIds:ids,clearMemories:false,clearSharedHistory:false},true,snap,baseline);
  }

  async plan(selection = {}) {
    return this._withRead(() => this._plan(selection,false));
  }

  async _plan(selection = {}, repair=false, initialSnapshot, initialBaseline) {
    if (!Array.isArray(selection.chatIds) || selection.chatIds.some(id => typeof id !== 'string' || !ID_RE.test(id))) fail('请选择有效的聊天 ID。');
    const ids = [...new Set(selection.chatIds)];
    if (ids.length > (repair?10000:500)) fail('选择的条目过多，请分批清理。');
    const normalized = { chatIds: ids, clearMemories: selection.clearMemories === true, clearSharedHistory: selection.clearSharedHistory === true };
    if (!ids.length && !normalized.clearMemories && !normalized.clearSharedHistory) fail(repair?'未发现可以安全修复的本机悬空引用。':'没有选择要清理的内容。');
    // Take the baseline before reading paths and row counts. Otherwise a write
    // during preview construction can silently become the accepted baseline.
    const baseline = initialBaseline || await this._planBaseline();
    const snap = initialSnapshot || await this.snapshot();
    for (const [rel,info] of Object.entries(snap.databases)) if (info.present && !info.supported) fail(`无法安全清理：${rel} 的结构未验证。`);
    if(repair) {
      const targets=await this._automationTargets(snap.databases);
      for(const id of ids) if(targets.has(id)) fail(`失效聊天 ${id} 仍被自动化 ${targets.details.get(id).join('、')} 引用，请先在 Codex 中处理该任务。`);
    }
    const selected = repair ? [] : ids.map(id => {
      const chat = snap.chats.find(c => c.id === id);
      if (!chat) fail('选中的聊天已不存在，请刷新。');
      if (!chat.canDelete) fail(chat.reason);
      return chat;
    });
    const rolloutPaths = selected.map(chat => chat.relativePath).filter(Boolean);
    const rolloutFingerprints = await this._fingerprints(rolloutPaths);
    const fileMap = new Map();
    for (const chat of selected) if (chat.relativePath && await this._stat(chat.relativePath)) {
      await this._checkRollout(chat.relativePath,chat.id);
      fileMap.set(chat.relativePath, { path: chat.relativePath, action: 'remove' });
    }
    const memoryPaths=[];
    if (normalized.clearMemories) for (const dir of MEMORY_DIRS) for (const rel of await this._walkMemory(dir)) { memoryPaths.push(rel); fileMap.set(rel,{ path: rel, action: 'remove' }); }
    for (const edit of await this._jsonEdits(ids,normalized)) fileMap.set(edit.path,edit);
    const databases = [];
    for (const [rel,info] of Object.entries(snap.databases)) {
      if (!info.present) continue;
      const actions = this._sqlActions(rel,ids,normalized).filter(a=>!(repair && rel==='state_5.sqlite' && a.table==='threads' && !a.kind));
      if (!actions.length) continue;
      const db = readDb(await this._data(rel));
      try {
        for (const action of actions) action.count = Number(db.prepare(`SELECT count(*) AS n FROM ${qid(action.table)} WHERE ${action.where}`).get(...action.params).n);
        if(rel==='sqlite/codex-dev.db') {
          if((repair && ids.length) || actions.some(a=>a.table==='local_thread_catalog' && a.count)) actions.push({table:'local_thread_catalog_metadata',kind:'catalog-revision',where:'id=1',params:[],count:1});
        }
        // Queue delete triggers can recreate revision rows; keep zero-count actions.
        if (actions.some(a=>a.count)) databases.push({ path: rel, actions, rowCount: actions.reduce((sum,a)=>sum+a.count,0) });
      } finally { db.close(); }
    }
    const files = [...fileMap.values()];
    const tracked = [...files.map(f=>f.path), ...rolloutPaths];
    for (const d of databases) for (const suffix of ['','-wal','-shm','-journal']) tracked.push(d.path+suffix);
    // Even preserved DBs can gain new messages or automations between preview and execution.
    for (const rel of Object.keys(SCHEMAS)) for (const suffix of ['','-wal','-shm','-journal']) tracked.push(rel+suffix);
    tracked.push(...GLOBAL_FILES,'history.jsonl','session_index.jsonl');
    const automationFiles=await this._automationFiles();
    if (JSON.stringify(automationFiles) !== JSON.stringify(baseline.automationFiles)) fail('自动化文件列表在生成预览时改变，请重新预览。');
    tracked.push(...automationFiles);
    const fingerprints = await this._fingerprints(tracked);
    for (const [rel, before] of Object.entries({ ...baseline.fingerprints, ...rolloutFingerprints })) {
      if (JSON.stringify(fingerprints[rel]) !== JSON.stringify(before)) fail(`数据在生成预览时发生变化，请重新预览：${rel}`);
    }
    for(const file of files) if(file.sourceHash && file.sourceHash!==fingerprints[file.path]?.sha256) fail(`数据在生成预览时发生变化，请重新预览：${file.path}`);
    const id = crypto.randomUUID();
    const warnings = [
      '先完全退出 Codex，再执行；执行期间请勿重新启动。',
      '执行后直接删除所选内容，成功后自动清除临时事务副本；没有可恢复区。',
      '删除一条聊天不会删除其派生聊天里已复制的上下文，也不会删除项目、工作树、导出、语音文件或未识别日志。',
      '单条记忆删除不能移除已汇总进其他记忆的内容；需要时勾选“全部记忆”。设置和记忆开关不变，日后使用可能再次产生记忆。',
      '这是本机逻辑删除，不保证从 SSD、系统还原点或外部备份中不可恢复。',
    ];
    if (!normalized.clearSharedHistory && !repair) warnings.push('共享输入历史和无法归属聊天的数据库日志仍保留；可勾选共享历史清理。');
    if(repair) warnings.splice(0,warnings.length,'先完全退出 Codex，再执行；执行期间请勿重新启动。','仅清除已不在本机聊天索引中的明确本机引用，保留现有聊天、项目和远端状态。','成功后自动删除临时事务副本，没有可恢复区。');
    const estimatedBackupBytes=files.reduce((total,f)=>total+(fingerprints[f.path]?.sizeBytes||0),0)+databases.reduce((total,d)=>total+(fingerprints[d.path]?.sizeBytes||0)+(fingerprints[d.path+'-wal']?.sizeBytes||0),0);
    const referenceRepairs=files.flatMap(f=>(f.references||[]).map(r=>({path:f.path,...r})));
    const publicPlan = { id, kind:repair?'repair':'cleanup', createdAt: new Date().toISOString(), expiresAt: new Date(Date.now()+PLAN_TTL).toISOString(), selection: normalized, chatIds: ids, chats: selected.map(({id,title})=>({id,title})), referenceRepairs, files: files.map(f=>({ path: f.path, action: f.action, sizeBytes: fingerprints[f.path]?.sizeBytes || 0, ...describe(f.path) })), databases: databases.map(d=>({ path:d.path, rowCount:d.rowCount, tables:d.actions.map(a=>({name:a.table,rows:a.count,action:a.kind||'delete'})), ...describe(d.path) })), summary:{ chats:repair?0:ids.length, repairThreads:repair?ids.length:0, referenceRepairs:referenceRepairs.length, files:files.length, databaseRows:databases.reduce((n,d)=>n+d.rowCount,0), estimatedTemporaryBytes:estimatedBackupBytes, clearMemories:normalized.clearMemories, clearSharedHistory:normalized.clearSharedHistory }, warnings };
    this.plans.set(id,{ publicPlan, files, databases, fingerprints, automationFiles, memoryPaths:memoryPaths.sort(), expires:Date.now()+PLAN_TTL });
    for (const [key,value] of this.plans) if (value.expires<Date.now()) this.plans.delete(key);
    return publicPlan;
  }

  async _writeJson(file, value) {
    if(!inside(this.trashRoot,file)) fail('清单必须位于临时事务目录。');
    await this._atomicContent(this.trashRoot,slash(path.relative(this.trashRoot,file)),JSON.stringify(value,null,2)+'\n');
  }
  async _atomicContent(root,rel,content) {
    const target = await this._safe(root,rel,{missing:true});
    const temporary = `${target}.cleaner-${crypto.randomUUID()}.tmp`;
    await fsp.writeFile(temporary,content,{flag:'wx',mode:0o600});
    try { await this._safe(root,rel,{missing:true}); await fsp.rename(temporary,target); }
    finally { await fsp.rm(temporary,{force:true}).catch(()=>{}); }
  }
  async _copyToBackup(packageId,rel) {
    const targetRel = `${packageId}/files/${rel}`;
    const target = await this._trash(targetRel,true);
    await fsp.mkdir(path.dirname(target),{recursive:true});
    await this._trash(targetRel,true);
    await fsp.copyFile(await this._data(rel),target,fs.constants.COPYFILE_EXCL);
    return slash(path.relative(path.join(this.trashRoot,packageId),target));
  }

  async execute(planId) { return this._execute(planId,'cleanup'); }
  async executeRepair(planId) { return this._execute(planId,'repair'); }

  async _execute(planId,kind) {
    return this._exclusive(async () => {
      const plan=this.plans.get(planId);
      if(!plan || plan.expires<Date.now() || plan.publicPlan.kind!==kind) fail('清理预览无效或已过期，请重新预览。');
      const pending=await this.recoveryIssues();
      if(pending.some(x=>x.blocksWrites)) fail('上次清理异常中断，尚有未验证的故障材料。为防止覆盖数据，本次未执行；请查看故障路径。');
      await this._assertFingerprints(plan.fingerprints);
      if(kind==='repair') {
        const db=readDb(await this._data('state_5.sqlite'));
        try { for(const id of plan.publicPlan.chatIds) if(db.prepare('SELECT 1 FROM threads WHERE id=?').get(id)) fail('待修复引用对应的聊天已重新出现，请重新检查；本次未删除现有聊天。'); }
        finally { db.close(); }
      }
      const latest=await this._inspectDatabases();
      for(const [rel,info] of Object.entries(latest)) if(info.present && !info.supported) fail(`数据库无法安全写入：${rel}：${info.reason||'结构未验证'}`);
      if(JSON.stringify(await this._automationFiles())!==JSON.stringify(plan.automationFiles)) fail('自动化文件列表在预览后改变，请重新预览。');
      const targets=await this._automationTargets(latest);
      for(const id of plan.publicPlan.chatIds) if(targets.has(id)) fail(`聊天被自动化 ${targets.details.get(id).join('、')} 引用，请先在 Codex 中处理该任务。`);
      if(plan.publicPlan.selection.clearMemories) {
        const now=[]; for(const dir of MEMORY_DIRS) now.push(...await this._walkMemory(dir));
        if(JSON.stringify(now.sort())!==JSON.stringify(plan.memoryPaths)) fail('记忆文件列表在预览后改变，请重新预览。');
      }
      const id=`${new Date().toISOString().replace(/[:.]/g,'-')}-${crypto.randomUUID()}`;
      const directory=await this._trash(id,true);
      await fsp.mkdir(directory,{recursive:true,mode:0o700});
      const manifest={format:'codex-cleaner-v2',id,dataRoot:this.dataRoot,createdAt:new Date().toISOString(),status:'preparing',kind,summary:plan.publicPlan.summary,files:[],databases:[]};
      const manifestPath=await this._trash(`${id}/manifest.json`,true);
      await this._writeJson(manifestPath,manifest);
      const opened=[];
      let mutationStarted=false,complete=false;
      try {
        for(const f of plan.files) manifest.files.push({path:f.path,action:f.action,backup:await this._copyToBackup(id,f.path),original:plan.fingerprints[f.path]});
        for(const d of plan.databases) {
          const db=readDb(await this._data(d.path),true);
          opened.push({...d,db,transaction:false});
          const backupRel=`databases/${d.path}`,backupPath=await this._trash(`${id}/${backupRel}`,true);
          await fsp.mkdir(path.dirname(backupPath),{recursive:true});
          await this._trash(`${id}/${backupRel}`,true);
          await backup(db,backupPath);
          manifest.databases.push({path:d.path,backup:backupRel});
        }
        for(const item of [...manifest.files,...manifest.databases]) {
          item.backupFingerprint=await this._fingerprint(directory,item.backup);
          if (item.original && JSON.stringify(item.backupFingerprint) !== JSON.stringify(item.original)) fail(`临时事务副本与原文件不一致，停止清理：${item.path}`);
        }
        manifest.status='prepared';await this._writeJson(manifestPath,manifest);
        await this._processGuard();await this._assertFingerprints(plan.fingerprints);
        for(const item of opened) { item.db.exec('BEGIN IMMEDIATE');item.transaction=true; }
        // Recheck after obtaining every SQLite write lock, closing the window
        // between the previous fingerprint check and BEGIN IMMEDIATE.
        await this._assertFingerprints(plan.fingerprints);
        manifest.status='applying';await this._writeJson(manifestPath,manifest);
        mutationStarted=true;
        for(const item of opened) {
          item.db.exec('PRAGMA secure_delete=ON');
          for(const action of item.actions) {
            if(action.kind==='catalog-revision') item.db.exec('INSERT INTO local_thread_catalog_metadata (id,catalog_revision) VALUES (1,1) ON CONFLICT(id) DO UPDATE SET catalog_revision=catalog_revision+1');
            else if(action.kind==='clear-reference') item.db.prepare(`UPDATE ${qid(action.table)} SET ${qid(action.column)} = NULL WHERE ${action.where}`).run(...action.params);
            else item.db.prepare(`DELETE FROM ${qid(action.table)} WHERE ${action.where}`).run(...action.params);
          }
        }
        for(const f of plan.files) {
          if(f.action==='remove') await fsp.unlink(await this._data(f.path));
          else await this._atomicContent(this.dataRoot,f.path,f.content);
        }
        await this._processGuard();
        for(const item of opened) { item.db.exec('COMMIT');item.transaction=false; }
        for(const item of opened) {
          item.db.exec('PRAGMA wal_checkpoint(TRUNCATE)');
          for(const action of item.actions) if(action.kind!=='catalog-revision') {
            const remaining=Number(item.db.prepare(`SELECT count(*) AS n FROM ${qid(action.table)} WHERE ${action.where}`).get(...action.params).n);
            if(remaining) fail(`清理结果校验失败：${item.path}/${action.table}`);
          }
          if(item.db.prepare('PRAGMA quick_check').get().quick_check!=='ok') fail(`数据库一致性校验失败：${item.path}`);
          item.db.close();item.db=null;
        }
        for(const f of plan.files) {
          const after=await this._fingerprint(this.dataRoot,f.path);
          if(f.action==='remove' ? after.exists : !after.exists || after.sha256!==sha(f.content)) fail(`文件清理结果校验失败：${f.path}`);
        }
        await this._processGuard();
        manifest.status='complete';manifest.completedAt=new Date().toISOString();
        await this._writeJson(manifestPath,manifest);
        complete=true;
        this.plans.delete(planId);
        try { await this._removeTransaction(id); }
        catch(error) {
          return {id,status:'complete',summary:manifest.summary,canRestore:false,warnings:[...plan.publicPlan.warnings,`清理完成，但临时事务副本未能完全删除：${directory}。${error.message}`],residualTransaction:{directory,status:'cleanup-required'}};
        }
        return {id,status:'complete',summary:manifest.summary,canRestore:false,warnings:plan.publicPlan.warnings};
      } catch(error) {
        for(const item of opened) if(item.db) {
          try { if(item.transaction) item.db.exec('ROLLBACK'); } catch {}
          try { item.db.close(); } catch {}
          item.db=null;
        }
        if(complete) throw error;
        let rollbackError;
        if(mutationStarted) {
          try { await this._processGuard();await this._rollbackTransaction(manifest,directory); }
          catch(e) { rollbackError=e; }
        }
        if(!rollbackError) {
          try { await this._removeTransaction(id); }
          catch(e) {
            manifest.status='cleanup-required';manifest.error=e.message;
            await this._writeJson(manifestPath,manifest).catch(()=>{});
            fail(`${error.message} ${mutationStarted?'已自动还原并验证原有数据。':'未开始清理。'}临时副本删除失败：${directory}。`);
          }
          fail(`${error.message} ${mutationStarted?'已自动还原并验证原有数据，没有保留恢复副本。':'未开始清理，临时副本已移除。'}`);
        }
        manifest.status='needs-recovery';manifest.error=error.message;manifest.recoveryError=rollbackError.message;
        await this._writeJson(manifestPath,manifest).catch(()=>{});
        fail(`${error.message} 自动回滚未能完成验证：${rollbackError.message}。故障材料已保留在 ${directory}；请保持 Codex 关闭，不要继续清理。`);
      } finally {
        for(const item of opened) if(item.db) try { item.db.close(); } catch {}
      }
    });
  }

  async _rollbackTransaction(manifest,directory) {
    for(const item of [...manifest.files,...manifest.databases]) {
      const current=await this._fingerprint(directory,item.backup);
      if(JSON.stringify(current)!==JSON.stringify(item.backupFingerprint)) fail(`临时事务副本校验失败：${item.backup}`);
    }
    for(const item of manifest.databases) {
      const destination=await this._data(item.path,true),temporary=`${destination}.cleaner-${crypto.randomUUID()}.tmp`;
      try {
        await fsp.copyFile(await this._trash(`${manifest.id}/${item.backup}`),temporary,fs.constants.COPYFILE_EXCL);
        for(const suffix of ['-wal','-shm','-journal']) await fsp.rm(await this._data(item.path+suffix,true),{force:true});
        await this._data(item.path,true);await fsp.rename(temporary,destination);
      } finally { await fsp.rm(temporary,{force:true}).catch(()=>{}); }
      const restored=await this._fingerprint(this.dataRoot,item.path);
      if(JSON.stringify(restored)!==JSON.stringify(item.backupFingerprint)) fail(`数据库自动还原校验失败：${item.path}`);
      const db=readDb(destination);try { if(db.prepare('PRAGMA quick_check').get().quick_check!=='ok') fail(`数据库自动还原一致性失败：${item.path}`); } finally { db.close(); }
    }
    for(const item of manifest.files) {
      const destination=await this._data(item.path,true);
      await fsp.mkdir(path.dirname(destination),{recursive:true});
      await this._atomicContent(this.dataRoot,item.path,await fsp.readFile(await this._trash(`${manifest.id}/${item.backup}`)));
      const restored=await this._fingerprint(this.dataRoot,item.path);
      if(JSON.stringify(restored)!==JSON.stringify(item.original)) fail(`文件自动还原校验失败：${item.path}`);
    }
  }

  async _removeTransaction(id) {
    if(typeof id!=='string' || !/^[0-9TZ-]+-[0-9a-f-]{36}$/.test(id)) fail('临时事务 ID 无效。');
    const files=[],directories=[];
    const walk=async rel=>{
      const full=await this._trash(rel),st=await fsp.lstat(full);
      if(st.isDirectory()) { for(const name of await fsp.readdir(full)) await walk(`${rel}/${name}`);directories.push(rel); }
      else if(st.isFile() && st.nlink===1) files.push(rel);
      else fail('临时事务目录含特殊文件，拒绝删除。');
    };
    await walk(id);
    for(const rel of files.filter(x=>!x.endsWith('/manifest.json'))) await fsp.unlink(await this._trash(rel));
    for(const rel of files.filter(x=>x.endsWith('/manifest.json'))) await fsp.unlink(await this._trash(rel));
    for(const rel of directories) await fsp.rmdir(await this._trash(rel));
  }

  async recoveryIssues() {
    let directory;
    try { directory=await this._trash(); } catch(error) { if(error.code==='ENOENT') return [];throw error; }
    const issues=[];
    for(const entry of await fsp.readdir(directory,{withFileTypes:true})) {
      if(!entry.isDirectory() || entry.isSymbolicLink()) continue;
      const record={id:entry.name,directory:path.join(directory,entry.name),status:'unrecognized',blocksWrites:true};
      try {
        const file=await this._trash(`${entry.name}/manifest.json`),st=await fsp.lstat(file);
        if(!st.isFile() || st.nlink>1 || st.size>MAX_JSON_BYTES) fail('事务清单无法安全读取。');
        const manifest=JSON.parse(await fsp.readFile(file,'utf8'));
        if(manifest.format!=='codex-cleaner-v2' || manifest.id!==entry.name || !samePath(normalPath(manifest.dataRoot),this.dataRoot)) fail('事务清单与当前数据目录不匹配。');
        record.status=manifest.status;record.error=manifest.error||manifest.recoveryError||'';
        record.blocksWrites=!['complete','cleanup-required'].includes(manifest.status);
      } catch(error) { record.error=error.message; }
      issues.push(record);
    }
    return issues;
  }
}

module.exports = { Cleaner, describe, SUPPORTED_DATABASES: Object.keys(SCHEMAS) };
