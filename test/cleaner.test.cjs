'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const {DatabaseSync}=require('node:sqlite');
const {Cleaner} = require('../lib/cleaner.cjs');
const {fixture, rows, open, insert, put} = require('./fixture.cjs');
const STOPPED = async () => ({running:false,processes:[]});
function setup(t, options={}) {const f=fixture();t.after(()=>f.dispose());const cleaner=new Cleaner({dataRoot:f.dataRoot,trashRoot:f.trashRoot,processCheck:STOPPED,...options});return {...f,cleaner};}
function selection(ids=[],extra={}) {return {chatIds:ids,clearMemories:false,clearSharedHistory:false,...extra};}
function checksum(file) {return crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');}
const linkedTables={
  'state_5.sqlite':['threads','thread_attachments','thread_dynamic_tools'],
  'thread_history_1.sqlite':['thread_turns','thread_items','thread_realtime_items','thread_history_projection_state'],
  'memories_1.sqlite':['stage1_outputs'],
  'logs_2.sqlite':['logs'],
  'goals_1.sqlite':['thread_goals','thread_goal_continuation_deferrals'],
  'queue_1.sqlite':['queued_items','queued_thread_revisions'],
  'sqlite/codex-dev.db':['automation_runs','inbox_items','live_visualization_suggestions','local_thread_catalog','local_thread_catalog_scan_entries','thread_timeline_ledger'],
};
function linked(f,id) {const out={};for(const [db,tables] of Object.entries(linkedTables))for(const table of tables)out[db+'/'+table]=rows(f.dataRoot,db,table).filter(r=>(table==='threads'?r.id:r.thread_id)===id);return out;}
function noBackups(f) {assert.deepEqual(fs.existsSync(f.trashRoot)?fs.readdirSync(f.trashRoot):[],[],'successful cleanup must not retain chat/database recovery copies');}
function addDesktopReferences(f,target) {
  const file=path.join(f.dataRoot,'.codex-global-state.json');const value=JSON.parse(fs.readFileSync(file,'utf8'));const localHost='local:'+f.dataRoot;const localUnreadHost='local:'+'a'.repeat(64);
  value['app-server-projects-migration-by-host']={
    [localHost]:{version:1,projectsMigrated:true,threadAssignmentsMigrated:false,pendingThreadAssignmentIds:[target,f.ids[1]]},
    'ssh:fixture':{version:1,projectsMigrated:true,threadAssignmentsMigrated:false,pendingThreadAssignmentIds:[target,f.ids[1]]},
  };
  value['electron-thread-read-state-v1']={version:1,unreadByIdentity:{'fixture-account':{[localUnreadHost]:[target,f.ids[1]],['local:'+'b'.repeat(64)]:[target,f.ids[1]],'ssh:fixture':[target,f.ids[1]]}},legacyMigration:{identityKey:'fixture-account',unreadThreadIdsByHostId:{local:[target,f.ids[1]],'ssh:fixture':[target,f.ids[1]]},adoptedHostIds:{local:localUnreadHost}}};
  value['thread-project-assignments']={[target]:'project-fixture',[f.ids[1]]:'project-fixture'};
  value['archived-thread-ids']=[target];value['pinned-thread-ids']=[target,f.ids[1]];
  const atom=value['electron-persisted-atom-state'];
  atom['heartbeat-thread-permissions-by-id']={[target]:{allow:true},[f.ids[1]]:{allow:false}};
  atom['client-thread-bindings-v1']={'client-new-thread:fixture-client':target,'client-new-thread:retained-client':f.ids[1]};
  atom['recent-workspace-files-v3']={
    [JSON.stringify(['local',target,'selected.txt'])]:{hostId:'local',threadId:'local:'+target,path:'selected.txt',updatedAt:1},
    [JSON.stringify(['local',target,'selected-legacy.txt'])]:{hostId:'local',threadId:target,path:'selected-legacy.txt',updatedAt:1},
    [JSON.stringify(['local',target,'selected-client.txt'])]:{hostId:'local',threadId:'client-new-thread:fixture-client',path:'selected-client.txt',updatedAt:1},
    [JSON.stringify(['local',f.ids[1],'retained.txt'])]:{hostId:'local',threadId:'local:'+f.ids[1],path:'retained.txt',updatedAt:1},
    [JSON.stringify(['ssh:fixture',target,'remote.txt'])]:{hostId:'ssh:fixture',threadId:'local:'+target,path:'remote.txt',updatedAt:1},
  };
  value['unknown-future-state']={text:target,ids:[target,f.ids[1]],settings:{keep:true}};
  for(const rel of ['.codex-global-state.json','.codex-global-state.json.bak'])put(f.dataRoot,rel,JSON.stringify(value));
  return {before:value,localHost,localUnreadHost};
}
function assertDesktopReferences(f,target,{before,localHost,localUnreadHost}) {
  for(const rel of ['.codex-global-state.json','.codex-global-state.json.bak']){
    const value=JSON.parse(fs.readFileSync(path.join(f.dataRoot,rel),'utf8'));
    assert.deepEqual(value['app-server-projects-migration-by-host'][localHost].pendingThreadAssignmentIds,[f.ids[1]],rel+' local pending project assignments');
    assert.deepEqual(value['app-server-projects-migration-by-host']['ssh:fixture'],before['app-server-projects-migration-by-host']['ssh:fixture']);
    const unread=value['electron-thread-read-state-v1'];assert.deepEqual(unread.unreadByIdentity['fixture-account'][localUnreadHost],[f.ids[1]]);
    assert.deepEqual(unread.unreadByIdentity['fixture-account']['ssh:fixture'],[target,f.ids[1]]);
    assert.deepEqual(unread.unreadByIdentity['fixture-account']['local:'+'b'.repeat(64)],[target,f.ids[1]],'unadopted local-looking host identity belongs to a different data location');
    assert.deepEqual(unread.legacyMigration.unreadThreadIdsByHostId.local,[f.ids[1]]);assert.deepEqual(unread.legacyMigration.unreadThreadIdsByHostId['ssh:fixture'],[target,f.ids[1]]);
    assert.deepEqual(value['thread-project-assignments'],{[f.ids[1]]:'project-fixture'});assert.deepEqual(value['pinned-thread-ids'],[f.ids[1]]);assert.deepEqual(value['archived-thread-ids'],[]);
    const atom=value['electron-persisted-atom-state'];assert.deepEqual(atom['heartbeat-thread-permissions-by-id'],{[f.ids[1]]:{allow:false}});
    assert.deepEqual(atom['client-thread-bindings-v1'],{'client-new-thread:retained-client':f.ids[1]});
    assert.deepEqual(Object.values(atom['recent-workspace-files-v3']),Object.values(before['electron-persisted-atom-state']['recent-workspace-files-v3']).filter(x=>x.hostId!=='local'||![target,'local:'+target,'client-new-thread:fixture-client'].includes(x.threadId)));
    assert.deepEqual(value['unknown-future-state'],before['unknown-future-state']);assert.deepEqual(value['saved-workspace-roots'],before['saved-workspace-roots']);
  }
}

test('preview is read-only and enumerates synthetic chats',async t=>{
  const f=setup(t);const before=checksum(path.join(f.dataRoot,'state_5.sqlite'));
  const state=await f.cleaner.snapshot();assert.equal(state.chats.length,2);
  const plan=await f.cleaner.plan(selection([f.ids[0]]));assert.ok(plan.id);
  assert.equal(checksum(path.join(f.dataRoot,'state_5.sqlite')),before);
  assert.ok(fs.existsSync(f.rolloutPaths[0]));
});

test('one execution permanently removes selected chat copies and preserves unselected data',async t=>{
  const f=setup(t);const otherBefore=linked(f,f.ids[1]);
  const protectedFiles=['config.toml','auth.json','plugins/example/README.md','skills/example/SKILL.md','mystery-user-file.txt'];
  const protectedBefore=Object.fromEntries(protectedFiles.map(file=>[file,checksum(path.join(f.dataRoot,file))]));
  const plan=await f.cleaner.plan(selection([f.ids[0]]));await f.cleaner.execute(plan.id);
  assert.equal(fs.existsSync(f.rolloutPaths[0]),false);assert.ok(fs.existsSync(f.rolloutPaths[1]));
  for(const [table,values] of Object.entries(linked(f,f.ids[0])))assert.equal(values.length,0,table);
  assert.deepEqual(linked(f,f.ids[1]),otherBefore);
  assert.equal(rows(f.dataRoot,'state_5.sqlite','thread_spawn_edges').length,0,'spawn edge must be removed from either direction');
  assert.equal(fs.readFileSync(path.join(f.dataRoot,'session_index.jsonl'),'utf8').includes(f.ids[0]),false);
  for(const file of protectedFiles)assert.equal(checksum(path.join(f.dataRoot,file)),protectedBefore[file],file);
  assert.ok(fs.existsSync(path.join(f.dataRoot,'memories/MEMORY.md')),'global memory needs explicit opt-in');
  noBackups(f);
});

test('clearing all memories preserves chat history and unrelated settings',async t=>{
  const f=setup(t);const chatBefore=linked(f,f.ids[1]);
  const plan=await f.cleaner.plan(selection([],{clearMemories:true}));await f.cleaner.execute(plan.id);
  for(const table of ['stage1_outputs','jobs'])assert.equal(rows(f.dataRoot,'memories_1.sqlite',table).length,0,table);
  assert.equal(rows(f.dataRoot,'memories_1.sqlite','consolidation_progress').length,1,'non-content singleton progress row is preserved');
  for(const rel of ['memories/MEMORY.md','memories_v2/memory.txt','memories_extensions/extension.txt'])assert.equal(fs.existsSync(path.join(f.dataRoot,rel)),false,rel);
  assert.equal(rows(f.dataRoot,'state_5.sqlite','threads').length,2);assert.ok(fs.existsSync(f.rolloutPaths[0]));
  assert.deepEqual(rows(f.dataRoot,'thread_history_1.sqlite','thread_items').filter(x=>x.thread_id===f.ids[1]),chatBefore['thread_history_1.sqlite/thread_items']);
  assert.ok(fs.existsSync(path.join(f.dataRoot,'skills/example/SKILL.md')));
  noBackups(f);
});

test('active Codex blocks deletion even if the plan was created while stopped',async t=>{
  let running=false;const f=setup(t,{processCheck:async()=>({running,processes:running?[{name:'codex',pid:42}]:[]})});
  const plan=await f.cleaner.plan(selection([f.ids[0]]));running=true;
  await assert.rejects(()=>f.cleaner.execute(plan.id));
  assert.equal(rows(f.dataRoot,'state_5.sqlite','threads').length,2);assert.ok(fs.existsSync(f.rolloutPaths[0]));
});

test('failed process verification blocks deletion',async t=>{
  const f=setup(t,{processCheck:async()=>{throw new Error('synthetic process query failure');}});
  await assert.rejects(async()=>{const plan=await f.cleaner.plan(selection([f.ids[0]]));await f.cleaner.execute(plan.id);});
  assert.equal(rows(f.dataRoot,'state_5.sqlite','threads').length,2);
});

test('execution does not require typed confirmation and cannot repeat the consumed plan',async t=>{
  const f=setup(t);const plan=await f.cleaner.plan(selection([f.ids[0]]));
  await f.cleaner.execute(plan.id);assert.equal(fs.existsSync(f.rolloutPaths[0]),false);
  await assert.rejects(()=>f.cleaner.execute(plan.id),/过期|重新预览/);noBackups(f);
});

test('changes after a preview invalidate the cleanup plan',async t=>{
  const f=setup(t);const plan=await f.cleaner.plan(selection([f.ids[0]]));fs.appendFileSync(f.rolloutPaths[0],'\n');
  await assert.rejects(()=>f.cleaner.execute(plan.id));assert.equal(rows(f.dataRoot,'state_5.sqlite','threads').length,2);
});

test('missing rollout is removable as a stale database/index entry',async t=>{
  const f=setup(t);fs.unlinkSync(f.rolloutPaths[0]);
  const state=await f.cleaner.snapshot();assert.equal(state.chats.find(x=>x.id===f.ids[0]).canDelete,true);
  const plan=await f.cleaner.plan(selection([f.ids[0]]));await f.cleaner.execute(plan.id);
  for(const [table,values] of Object.entries(linked(f,f.ids[0])))assert.equal(values.length,0,table);
  assert.ok(fs.existsSync(f.rolloutPaths[1]));noBackups(f);
});

test('unsupported database schema is refused without deleting the rollout',async t=>{
  const f=setup(t);const db=open(f.dataRoot,'state_5.sqlite');db.exec('CREATE TABLE future_chat_cache (thread_id TEXT, message TEXT)');db.close();
  const state=await f.cleaner.snapshot();assert.equal(state.capabilities.deleteChats,false);assert.match(state.databases['state_5.sqlite'].reason,/结构|版本/);assert.ok(state.warnings.some(x=>/state_5.sqlite/.test(x)));
  await assert.rejects(async()=>{const plan=await f.cleaner.plan(selection([f.ids[0]]));await f.cleaner.execute(plan.id);});
  assert.ok(fs.existsSync(f.rolloutPaths[0]));assert.equal(rows(f.dataRoot,'state_5.sqlite','threads').length,2);
});

test('arbitrary file inspection cannot escape the selected Codex data root',async t=>{
  const f=setup(t);const outside=put(f.base,'private.txt','Unrelated outside content');
  for(const relative of ['../private.txt',outside,'sessions/../../private.txt'])await assert.rejects(async()=>f.cleaner.inspectFile(relative));
  assert.equal(fs.readFileSync(outside,'utf8'),'Unrelated outside content');
});

test('an outside rollout path is refused without touching that file',async t=>{
  const f=setup(t);const outside=put(f.base,'outside-rollout.jsonl','Unrelated outside data');
  const db=open(f.dataRoot,'state_5.sqlite');db.prepare('UPDATE threads SET rollout_path=? WHERE id=?').run(outside,f.ids[0]);db.close();
  const state=await f.cleaner.snapshot();assert.match(state.chats.find(x=>x.id===f.ids[0]).reason,/路径|目录外/);
  await assert.rejects(async()=>{const plan=await f.cleaner.plan(selection([f.ids[0]]));await f.cleaner.execute(plan.id);});
  assert.equal(fs.readFileSync(outside,'utf8'),'Unrelated outside data');assert.equal(rows(f.dataRoot,'state_5.sqlite','threads').length,2);
});

test('memory directory junction cannot make cleanup traverse outside data root',async t=>{
  const f=setup(t);const outsideDir=path.join(f.base,'unrelated');put(outsideDir,'keep.txt','Must survive');
  const link=path.join(f.dataRoot,'memories','junction');
  try {fs.symlinkSync(outsideDir,link,process.platform==='win32'?'junction':'dir');}catch(error){if(['EPERM','EACCES'].includes(error.code)){t.skip('OS does not allow fixture symlink creation');return;}throw error;}
  await assert.rejects(async()=>{const plan=await f.cleaner.plan(selection([],{clearMemories:true}));await f.cleaner.execute(plan.id);});
  assert.equal(fs.readFileSync(path.join(outsideDir,'keep.txt'),'utf8'),'Must survive');
});

test('genuine automation references name the exact blocker and leave the schedule unchanged',async t=>{
  const f=setup(t);const db=open(f.dataRoot,'sqlite/codex-dev.db');
  insert(db,'automations',{id:'fixture-automation',name:'Invented schedule',prompt:'Invented prompt',status:'ACTIVE',created_at:1,updated_at:1,target_thread_id:f.ids[0],kind:'heartbeat',next_run_at:123});
  insert(db,'automations',{id:'retained-automation',name:'Retained schedule',prompt:'Retained prompt',status:'ACTIVE',created_at:1,updated_at:1,target_thread_id:f.ids[1],kind:'heartbeat',next_run_at:456});db.close();
  const before=rows(f.dataRoot,'sqlite/codex-dev.db','automations');
  const state=await f.cleaner.snapshot();assert.match(state.chats.find(x=>x.id===f.ids[0]).reason,/自动化/);assert.match(state.chats.find(x=>x.id===f.ids[0]).reason,/Invented schedule|fixture-automation/);
  await assert.rejects(()=>f.cleaner.plan(selection([f.ids[0]])),/自动化/);
  assert.deepEqual(rows(f.dataRoot,'sqlite/codex-dev.db','automations'),before);assert.ok(fs.existsSync(f.rolloutPaths[0]));noBackups(f);
});

test('multiline automation prompt examples do not create a false target association',async t=>{
  const f=setup(t);const rel='automations/fixture/automation.toml';
  const text='name = "Example task"\nstatus = "ACTIVE"\ntarget_thread_id = "'+f.ids[1]+'"\nprompt = """\nAn invented configuration example:\ntarget_thread_id = "'+f.ids[0]+'"\nstatus = "PAUSED"\n"""\n';put(f.dataRoot,rel,text);
  const state=await f.cleaner.snapshot();assert.equal(state.chats.find(x=>x.id===f.ids[0]).canDelete,true);assert.match(state.chats.find(x=>x.id===f.ids[1]).reason,/自动化/);
  const plan=await f.cleaner.plan(selection([f.ids[0]]));await f.cleaner.execute(plan.id);assert.equal(fs.readFileSync(path.join(f.dataRoot,rel),'utf8'),text);noBackups(f);
});

test('matching remote host records survive local chat deletion',async t=>{
  const f=setup(t);const db=open(f.dataRoot,'sqlite/codex-dev.db');insert(db,'local_thread_catalog_hosts',{host_id:'remote-fixture',host_kind:'ssh'});
  const tables=['live_visualization_suggestions','local_thread_catalog','local_thread_catalog_scan_entries','thread_timeline_ledger'];
  for(const table of tables){const source=db.prepare('SELECT * FROM '+table+' WHERE thread_id=?').get(f.ids[0]);insert(db,table,{...source,host_id:'remote-fixture'});}db.close();
  const before=Object.fromEntries(tables.map(table=>[table,rows(f.dataRoot,'sqlite/codex-dev.db',table).filter(r=>r.host_id==='remote-fixture')]));
  const plan=await f.cleaner.plan(selection([f.ids[0]]));await f.cleaner.execute(plan.id);
  for(const table of tables)assert.deepEqual(rows(f.dataRoot,'sqlite/codex-dev.db',table).filter(r=>r.host_id==='remote-fixture'),before[table],table);
  noBackups(f);
});

test('cold WAL databases clean once without false stale-plan failure or leftover copies',async t=>{
  const f=setup(t);
  for(const dbName of Object.keys(linkedTables)){const db=open(f.dataRoot,dbName);db.exec('PRAGMA journal_mode=WAL');db.close();}
  const plan=await f.cleaner.plan(selection([f.ids[0]]));await f.cleaner.execute(plan.id);
  assert.equal(rows(f.dataRoot,'state_5.sqlite','threads').length,1);
  assert.equal(fs.existsSync(f.rolloutPaths[0]),false);noBackups(f);
});

test('shared history cleanup clears global desktop input and SQLite logs, preserving per-chat drafts',async t=>{
  const f=setup(t);const plan=await f.cleaner.plan(selection([],{clearSharedHistory:true}));
  await f.cleaner.execute(plan.id);
  assert.equal(rows(f.dataRoot,'logs_2.sqlite','logs').length,0);
  const global=JSON.parse(fs.readFileSync(path.join(f.dataRoot,'.codex-global-state.json'),'utf8'));
  assert.equal(global['electron-persisted-atom-state']['prompt-history'].global,undefined);
  assert.deepEqual(global['electron-persisted-atom-state']['prompt-history'][f.ids[1]],['invented retained input']);
  assert.deepEqual(global['saved-workspace-roots'],[f.base]);
  assert.equal(rows(f.dataRoot,'state_5.sqlite','threads').length,2);
});

test('injected second file deletion failure automatically rolls back all previous changes',async t=>{
  const f=setup(t);const beforeA=linked(f,f.ids[0]),beforeB=linked(f,f.ids[1]);
  const plan=await f.cleaner.plan(selection(f.ids));
  const originalUnlink=fs.promises.unlink;let removedFirst=false;
  fs.promises.unlink=async function(file,...args){if(path.resolve(file)===path.resolve(f.rolloutPaths[1])){assert.equal(removedFirst,true);throw new Error('Injected second rollout unlink failure');}const result=await originalUnlink.call(this,file,...args);if(path.resolve(file)===path.resolve(f.rolloutPaths[0]))removedFirst=true;return result;};
  try {await assert.rejects(()=>f.cleaner.execute(plan.id),/Injected second rollout unlink failure/);}
  finally {fs.promises.unlink=originalUnlink;}
  assert.ok(fs.existsSync(f.rolloutPaths[0]),'previously removed file is automatically restored');
  assert.ok(fs.existsSync(f.rolloutPaths[1]));
  assert.deepEqual(linked(f,f.ids[0]),beforeA);assert.deepEqual(linked(f,f.ids[1]),beforeB);noBackups(f);
});

test('failure after a database commit restores every database and rewritten file automatically',async t=>{
  const f=setup(t);const beforeA=linked(f,f.ids[0]),beforeB=linked(f,f.ids[1]);
  const globalPath=path.join(f.dataRoot,'.codex-global-state.json');const globalBefore=fs.readFileSync(globalPath,'utf8');
  const plan=await f.cleaner.plan(selection(f.ids));
  const originalExec=DatabaseSync.prototype.exec;let commits=0;
  DatabaseSync.prototype.exec=function(sql,...args){if(/^COMMIT\b/i.test(sql)&&++commits===2)throw new Error('Injected later database commit failure');return originalExec.call(this,sql,...args);};
  try {await assert.rejects(()=>f.cleaner.execute(plan.id),/Injected later database commit failure/);}
  finally {DatabaseSync.prototype.exec=originalExec;}
  assert.ok(commits>=2,'failure must occur after one committed database');
  assert.deepEqual(linked(f,f.ids[0]),beforeA);assert.deepEqual(linked(f,f.ids[1]),beforeB);
  assert.ok(fs.existsSync(f.rolloutPaths[0]));assert.ok(fs.existsSync(f.rolloutPaths[1]));assert.equal(fs.readFileSync(globalPath,'utf8'),globalBefore);noBackups(f);
});

test('rollback failure preserves fault materials and blocks a subsequent cleanup',async t=>{
  const f=setup(t);const plan=await f.cleaner.plan(selection(f.ids));const originalUnlink=fs.promises.unlink;const originalRollback=f.cleaner._rollbackTransaction;
  fs.promises.unlink=async function(file,...args){if(path.resolve(file)===path.resolve(f.rolloutPaths[1]))throw new Error('Injected delete failure');return originalUnlink.call(this,file,...args);};
  f.cleaner._rollbackTransaction=async()=>{throw new Error('Injected rollback failure');};
  try {await assert.rejects(()=>f.cleaner.execute(plan.id),/Injected rollback failure/);}
  finally {fs.promises.unlink=originalUnlink;f.cleaner._rollbackTransaction=originalRollback;}
  const packages=fs.readdirSync(f.trashRoot);assert.equal(packages.length,1);const manifest=JSON.parse(fs.readFileSync(path.join(f.trashRoot,packages[0],'manifest.json'),'utf8'));assert.equal(manifest.status,'needs-recovery');assert.ok(manifest.databases.length>0);
  const state=await f.cleaner.snapshot();assert.ok(state.recoveryIssues.length>0);const before=checksum(f.rolloutPaths[1]);
  await assert.rejects(async()=>{const next=await f.cleaner.plan(selection([f.ids[1]]));await f.cleaner.execute(next.id);},/故障|中断|未完成/);
  assert.equal(checksum(f.rolloutPaths[1]),before);assert.equal(rows(f.dataRoot,'state_5.sqlite','threads').length,2);assert.deepEqual(fs.readdirSync(f.trashRoot),packages);
});

test('ordinary cleanup does not delete old recovery folders or arbitrary files there',async t=>{
  const f=setup(t);const legacy=put(f.base,'Recovery/legacy-v1/keep.txt','Existing user recovery content');const before=checksum(legacy);
  const plan=await f.cleaner.plan(selection([f.ids[0]]));await f.cleaner.execute(plan.id);
  assert.equal(checksum(legacy),before);assert.deepEqual(fs.readdirSync(path.join(f.base,'Recovery')),['legacy-v1']);noBackups(f);
});

test('archived chat deletion removes its archive file and leaves the live chat intact',async t=>{
  const f=setup(t);const archive=path.join(f.dataRoot,'archived_sessions',path.basename(f.rolloutPaths[0]));fs.mkdirSync(path.dirname(archive),{recursive:true});fs.renameSync(f.rolloutPaths[0],archive);
  const db=open(f.dataRoot,'state_5.sqlite');db.prepare('UPDATE threads SET archived=1,rollout_path=? WHERE id=?').run(archive,f.ids[0]);db.close();
  const state=await f.cleaner.snapshot();assert.equal(state.chats.find(x=>x.id===f.ids[0]).archived,true);
  const plan=await f.cleaner.plan(selection([f.ids[0]]));await f.cleaner.execute(plan.id);
  assert.equal(fs.existsSync(archive),false);assert.ok(fs.existsSync(f.rolloutPaths[1]));noBackups(f);
});

test('Windows extended path prefix maps to the same verified local rollout',async t=>{
  if(process.platform!=='win32'){t.skip('Windows extended path regression');return;}
  const f=setup(t);const db=open(f.dataRoot,'state_5.sqlite');db.prepare('UPDATE threads SET rollout_path=? WHERE id=?').run('\\\\?\\'+f.rolloutPaths[0],f.ids[0]);db.close();
  const state=await f.cleaner.snapshot();const chat=state.chats.find(x=>x.id===f.ids[0]);assert.equal(chat.canDelete,true,chat.reason);
  const plan=await f.cleaner.plan(selection([f.ids[0]]));await f.cleaner.execute(plan.id);
  assert.equal(fs.existsSync(f.rolloutPaths[0]),false);assert.ok(fs.existsSync(f.rolloutPaths[1]));noBackups(f);
});

test('selected deletion updates project/archive/read/file references in both desktop state files',async t=>{
  const f=setup(t);const context=addDesktopReferences(f,f.ids[0]);const db=open(f.dataRoot,'state_5.sqlite');
  insert(db,'projects',{id:'project-fixture',name:'Keep project',position:0,created_at_ms:1,updated_at_ms:1});insert(db,'project_roots',{project_id:'project-fixture',position:0,path:f.base});db.prepare('UPDATE threads SET project_id=?').run('project-fixture');db.close();
  const projectBefore=rows(f.dataRoot,'state_5.sqlite','projects'),rootsBefore=rows(f.dataRoot,'state_5.sqlite','project_roots');
  const desktop=open(f.dataRoot,'sqlite/codex-dev.db');insert(desktop,'local_thread_catalog_metadata',{id:1,catalog_revision:7});desktop.close();
  const plan=await f.cleaner.plan(selection([f.ids[0]]));await f.cleaner.execute(plan.id);
  assertDesktopReferences(f,f.ids[0],context);assert.deepEqual(rows(f.dataRoot,'state_5.sqlite','projects'),projectBefore);assert.deepEqual(rows(f.dataRoot,'state_5.sqlite','project_roots'),rootsBefore);
  assert.equal(rows(f.dataRoot,'state_5.sqlite','threads')[0].project_id,'project-fixture');assert.ok(rows(f.dataRoot,'sqlite/codex-dev.db','local_thread_catalog_metadata')[0].catalog_revision>7);noBackups(f);
});

test('repair removes preexisting stale local references while preserving valid and remote references',async t=>{
  const f=setup(t);const stale='33333333-3333-4333-8333-333333333333';const context=addDesktopReferences(f,stale);const before=linked(f,f.ids[0]);
  const globalBefore=checksum(path.join(f.dataRoot,'.codex-global-state.json'));const plan=await f.cleaner.planRepair();
  assert.equal(checksum(path.join(f.dataRoot,'.codex-global-state.json')),globalBefore,'repair preview must be read-only');
  await f.cleaner.executeRepair(plan.id);assertDesktopReferences(f,stale,context);
  assert.equal(rows(f.dataRoot,'state_5.sqlite','threads').length,2);assert.deepEqual(linked(f,f.ids[0]),before);for(const file of f.rolloutPaths)assert.ok(fs.existsSync(file));noBackups(f);
});

test('repair removes orphan local catalog entries and invalidates catalog while preserving remote copies',async t=>{
  const f=setup(t);const stale='33333333-3333-4333-8333-333333333333';const db=open(f.dataRoot,'sqlite/codex-dev.db');
  insert(db,'local_thread_catalog_hosts',{host_id:'ssh:fixture',host_kind:'ssh'});insert(db,'local_thread_catalog_metadata',{id:1,catalog_revision:10});
  const tables=['local_thread_catalog','local_thread_catalog_scan_entries','thread_timeline_ledger'];
  for(const table of tables){const source=db.prepare('SELECT * FROM '+table+' WHERE thread_id=?').get(f.ids[0]);for(const host_id of ['local','ssh:fixture'])insert(db,table,{...source,thread_id:stale,host_id});}db.close();
  const plan=await f.cleaner.planRepair();await f.cleaner.executeRepair(plan.id);
  for(const table of tables){const leftovers=rows(f.dataRoot,'sqlite/codex-dev.db',table).filter(x=>x.thread_id===stale);assert.equal(leftovers.length,1,table);assert.equal(leftovers[0].host_id,'ssh:fixture');}
  assert.ok(rows(f.dataRoot,'sqlite/codex-dev.db','local_thread_catalog_metadata')[0].catalog_revision>10);assert.equal(rows(f.dataRoot,'state_5.sqlite','threads').length,2);noBackups(f);
});

test('repair does not guess that an unknown pinned or remote-only identifier belongs to the local database',async t=>{
  const f=setup(t);const file=path.join(f.dataRoot,'.codex-global-state.json');const value=JSON.parse(fs.readFileSync(file,'utf8'));const unknown='33333333-3333-4333-8333-333333333333';
  value['pinned-thread-ids']=[unknown];value['app-server-projects-migration-by-host']={'ssh:fixture':{version:1,pendingThreadAssignmentIds:[unknown]}};fs.writeFileSync(file,JSON.stringify(value));const before=checksum(file);
  const plan=await f.cleaner.planRepair();assert.equal(plan.empty,true);assert.equal(checksum(file),before);assert.equal(rows(f.dataRoot,'state_5.sqlite','threads').length,2);noBackups(f);
});

test('repair refuses a stale candidate that becomes a live thread during preview construction',async t=>{
  const f=setup(t);const stale='33333333-3333-4333-8333-333333333333';addDesktopReferences(f,stale);
  const globalFile=path.join(f.dataRoot,'.codex-global-state.json');const before=checksum(globalFile);const original=f.cleaner._jsonEdits.bind(f.cleaner);let inserted=false;let newRollout;
  f.cleaner._jsonEdits=async function(...args){if(!inserted){inserted=true;newRollout=put(f.dataRoot,'sessions/2026/09/27/rollout-'+stale+'.jsonl',JSON.stringify({type:'session_meta',payload:{id:stale,cwd:f.base}})+'\n');const db=open(f.dataRoot,'state_5.sqlite');const source=db.prepare('SELECT * FROM threads WHERE id=?').get(f.ids[0]);insert(db,'threads',{...source,id:stale,title:'New thread created during preview',rollout_path:newRollout});db.close();}return original(...args);};
  await assert.rejects(async()=>{const plan=await f.cleaner.planRepair();await f.cleaner.executeRepair(plan.id);},/变化|存在|有效|重新|出现/);
  assert.equal(inserted,true);assert.equal(rows(f.dataRoot,'state_5.sqlite','threads').length,3);assert.equal(checksum(globalFile),before);assert.ok(fs.existsSync(newRollout));noBackups(f);
});

test('a rollout-path change during preview cannot become the accepted database baseline',async t=>{
  const f=setup(t);const original=f.cleaner._jsonEdits.bind(f.cleaner);
  const replacement=put(f.dataRoot,'sessions/replacement.jsonl',fs.readFileSync(f.rolloutPaths[0]));
  f.cleaner._jsonEdits=async function(...args){
    const db=open(f.dataRoot,'state_5.sqlite');db.prepare('UPDATE threads SET rollout_path=? WHERE id=?').run(replacement,f.ids[0]);db.close();
    return original(...args);
  };
  await assert.rejects(()=>f.cleaner.plan(selection([f.ids[0]])),/生成预览时发生变化/);
  assert.equal(rows(f.dataRoot,'state_5.sqlite','threads').length,2);
  assert.ok(fs.existsSync(f.rolloutPaths[0]));assert.ok(fs.existsSync(replacement));noBackups(f);
});

test('rollout metadata replaced during preview invalidates the plan',async t=>{
  const f=setup(t);const original=f.cleaner._checkRollout.bind(f.cleaner);
  f.cleaner._checkRollout=async function(...args){
    await original(...args);
    fs.writeFileSync(f.rolloutPaths[0],JSON.stringify({type:'session_meta',payload:{id:'unrelated-fixture-chat'}})+'\n');
  };
  await assert.rejects(()=>f.cleaner.plan(selection([f.ids[0]])),/生成预览时发生变化/);
  assert.equal(rows(f.dataRoot,'state_5.sqlite','threads').length,2);assert.ok(fs.existsSync(f.rolloutPaths[0]));noBackups(f);
});

test('a previously missing rollout that appears after preview requires a new preview',async t=>{
  const f=setup(t);const content=fs.readFileSync(f.rolloutPaths[0]);fs.unlinkSync(f.rolloutPaths[0]);
  const plan=await f.cleaner.plan(selection([f.ids[0]]));fs.writeFileSync(f.rolloutPaths[0],content);
  await assert.rejects(()=>f.cleaner.execute(plan.id),/预览后已改变/);
  assert.equal(rows(f.dataRoot,'state_5.sqlite','threads').length,2);assert.deepEqual(fs.readFileSync(f.rolloutPaths[0]),content);noBackups(f);
});

test('a corrupt temporary file backup aborts before deleting any original data',async t=>{
  const f=setup(t);const before=linked(f,f.ids[0]),rolloutBefore=checksum(f.rolloutPaths[0]);
  const plan=await f.cleaner.plan(selection([f.ids[0]]));const original=f.cleaner._copyToBackup.bind(f.cleaner);
  f.cleaner._copyToBackup=async function(packageId,rel){
    const backup=await original(packageId,rel);
    if(rel===path.relative(f.dataRoot,f.rolloutPaths[0]).split(path.sep).join('/'))fs.appendFileSync(path.join(f.trashRoot,packageId,backup),'Injected backup corruption');
    return backup;
  };
  await assert.rejects(()=>f.cleaner.execute(plan.id),/副本与原文件不一致/);
  assert.deepEqual(linked(f,f.ids[0]),before);assert.equal(checksum(f.rolloutPaths[0]),rolloutBefore);noBackups(f);
});

test('a database write immediately before acquiring write locks aborts without overwriting it',async t=>{
  const f=setup(t);const plan=await f.cleaner.plan(selection([f.ids[0]]));const original=f.cleaner._assertFingerprints.bind(f.cleaner);let checks=0;
  f.cleaner._assertFingerprints=async function(...args){
    await original(...args);
    if(++checks===2){const db=open(f.dataRoot,'state_5.sqlite');db.prepare('UPDATE threads SET title=? WHERE id=?').run('Concurrent fixture update',f.ids[0]);db.close();}
  };
  await assert.rejects(()=>f.cleaner.execute(plan.id),/预览后已改变/);
  assert.equal(rows(f.dataRoot,'state_5.sqlite','threads').find(row=>row.id===f.ids[0]).title,'Concurrent fixture update');
  assert.ok(fs.existsSync(f.rolloutPaths[0]));noBackups(f);
});

test('an in-progress read prevents a write and releases the read guard afterward',async t=>{
  const f=setup(t);const plan=await f.cleaner.plan(selection([f.ids[0]]));const original=f.cleaner._metadata.bind(f.cleaner);
  let releaseRead,markStarted;const started=new Promise(resolve=>{markStarted=resolve;});const released=new Promise(resolve=>{releaseRead=resolve;});
  f.cleaner._metadata=async function(...args){markStarted();await released;return original(...args);};
  const reading=f.cleaner.inspectFile('sessions');await started;
  try {await assert.rejects(()=>f.cleaner.execute(plan.id),/文件检查尚未完成/);}
  finally {releaseRead();await reading;f.cleaner._metadata=original;}
  await f.cleaner.execute(plan.id);assert.equal(fs.existsSync(f.rolloutPaths[0]),false);noBackups(f);
});

test('SQLite shared-memory hard links are rejected before opening the database',async t=>{
  const f=setup(t);const outside=put(f.base,'outside-shared-memory.txt','Unrelated outside content');
  fs.linkSync(outside,path.join(f.dataRoot,'state_5.sqlite-shm'));
  const before=checksum(outside);const state=await f.cleaner.snapshot();
  assert.equal(state.databases['state_5.sqlite'].supported,false);assert.match(state.databases['state_5.sqlite'].reason,/附属文件/);
  await assert.rejects(()=>f.cleaner.plan(selection([f.ids[0]])),/结构未验证/);
  assert.equal(checksum(outside),before);assert.ok(fs.existsSync(f.rolloutPaths[0]));noBackups(f);
});

test('transactionRoot works as the public option and conflicting legacy paths are refused',async t=>{
  const f=fixture();t.after(()=>f.dispose());
  const cleaner=new Cleaner({dataRoot:f.dataRoot,transactionRoot:f.trashRoot,processCheck:STOPPED});
  const plan=await cleaner.plan(selection([f.ids[0]]));await cleaner.execute(plan.id);noBackups(f);
  assert.throws(()=>new Cleaner({dataRoot:f.dataRoot,transactionRoot:f.trashRoot,trashRoot:path.join(f.base,'other-transactions'),processCheck:STOPPED}),/不一致/);
});

test('quoted and escaped TOML target keys and values still protect their referenced chat',async t=>{
  const f=setup(t);const rel='automations/quoted-target/automation.toml';
  const escapedStart='\\u'+f.ids[0].charCodeAt(0).toString(16).padStart(4,'0')+f.ids[0].slice(1);
  const variants=[
    '"target_thread_id" = "'+f.ids[0]+'"',
    "'targetThreadId' = '"+f.ids[0]+"'",
    '"target\\u005fthread\\U0000005fid" = "'+escapedStart+'"',
    '\uFEFFtarget_thread_id = "'+f.ids[0]+'"',
  ];
  for(const target of variants){
    put(f.dataRoot,rel,target+'\nname = "Synthetic automation"\n');
    await assert.rejects(()=>f.cleaner.plan(selection([f.ids[0]])),/自动化/,target);
    assert.equal(fs.readFileSync(path.join(f.dataRoot,rel),'utf8'),target+'\nname = "Synthetic automation"\n');
  }
  assert.ok(fs.existsSync(f.rolloutPaths[0]));assert.equal(rows(f.dataRoot,'state_5.sqlite','threads').length,2);noBackups(f);
});

test('TOML multiline closing quotes after an even backslash run allow a prompt without a target',async t=>{
  const f=setup(t);const rel='automations/backslash-prompt/automation.toml';let content;
  for(const count of [2,4]){
    content='name = "Synthetic automation"\nprompt = """Work in C:'+ '\\'.repeat(count)+'"""\n';
    put(f.dataRoot,rel,content);
    const plan=await f.cleaner.plan(selection([f.ids[0]]));assert.ok(plan.id);
  }
  const plan=await f.cleaner.plan(selection([f.ids[0]]));await f.cleaner.execute(plan.id);
  assert.equal(fs.readFileSync(path.join(f.dataRoot,rel),'utf8'),content);noBackups(f);
});

test('TOML odd backslash escapes keep fake targets inside prompts while the real trailing target stays protected',async t=>{
  const f=setup(t);const rel='automations/backslash-target/automation.toml';let content;
  for(const count of [1,3]){
    content='name = "Synthetic automation"\nprompt = """\nAn escaped quote: '+ '\\'.repeat(count)+'"""\n'
      +'target_thread_id = "'+f.ids[0]+'"\nWork in C:'+ '\\'.repeat(2)+'"""\n'
      +'target_thread_id = "'+f.ids[1]+'"\n';
    put(f.dataRoot,rel,content);
    const plan=await f.cleaner.plan(selection([f.ids[0]]));assert.ok(plan.id);
    await assert.rejects(()=>f.cleaner.plan(selection([f.ids[1]])),/自动化/);
  }
  const plan=await f.cleaner.plan(selection([f.ids[0]]));await f.cleaner.execute(plan.id);
  assert.equal(fs.readFileSync(path.join(f.dataRoot,rel),'utf8'),content);
  assert.ok(fs.existsSync(f.rolloutPaths[1]));noBackups(f);
});

test('an unfinished lock inspection reports its exact path and preserves the fault files',async t=>{
  const f=setup(t);const plan=await f.cleaner.plan(selection([f.ids[0]]));
  const lock=put(f.trashRoot,'.mutation.lock','Synthetic previous mutation lock');
  const reclaim=put(f.trashRoot,'.mutation-reclaim.lock','Synthetic interrupted lock inspection');
  await assert.rejects(()=>f.cleaner.execute(plan.id),error=>error.message.includes('未完成的写入锁检查') && error.message.includes(reclaim));
  assert.equal(fs.readFileSync(lock,'utf8'),'Synthetic previous mutation lock');
  assert.equal(fs.readFileSync(reclaim,'utf8'),'Synthetic interrupted lock inspection');
  assert.ok(fs.existsSync(f.rolloutPaths[0]));assert.equal(rows(f.dataRoot,'state_5.sqlite','threads').length,2);
});

test('a lock inspection permission failure retains its original diagnostic',async t=>{
  const f=setup(t);const plan=await f.cleaner.plan(selection([f.ids[0]]));
  put(f.trashRoot,'.mutation.lock','Synthetic previous mutation lock');
  const reclaim=path.join(f.trashRoot,'.mutation-reclaim.lock');const originalOpen=fs.promises.open;
  fs.promises.open=async function(file,...args){
    if(path.resolve(file)===path.resolve(reclaim)){const error=new Error('Synthetic reclaim access denied');error.code='EACCES';throw error;}
    return originalOpen.call(this,file,...args);
  };
  try {await assert.rejects(()=>f.cleaner.execute(plan.id),error=>error.code==='EACCES' && error.message==='Synthetic reclaim access denied');}
  finally {fs.promises.open=originalOpen;}
  assert.ok(fs.existsSync(f.rolloutPaths[0]));assert.equal(rows(f.dataRoot,'state_5.sqlite','threads').length,2);
});
