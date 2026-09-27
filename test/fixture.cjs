'use strict';
// Synthetic data only. Never point this fixture at the user's CODEX_HOME.
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const { DatabaseSync } = require('node:sqlite');
const assert = require('node:assert/strict');
const schema = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixture-schema.json'), 'utf8').replace(/^\uFEFF/, ''));
const TEST_WORK = fs.realpathSync(os.tmpdir());
const FIXTURE_PREFIX = 'codex-cleaner-test-';
const IDS = ['11111111-1111-4111-8111-111111111111', '22222222-2222-4222-8222-222222222222'];
const q = s => '"' + s.replaceAll('"', '""') + '"';

function open(dataRoot, name) { return new DatabaseSync(path.join(dataRoot, name)); }
function insert(db, table, values, key='synthetic') {
  const row = {...values};
  for (const column of db.prepare(`PRAGMA table_info(${q(table)})`).all()) {
    if (column.pk && !column.notnull && /^INTEGER$/i.test(column.type)) continue;
    if (!(column.name in row) && (column.notnull || column.pk) && column.dflt_value == null) {
      row[column.name] = /INT|REAL|BOOL/i.test(column.type) ? 1 : /BLOB/i.test(column.type) ? Buffer.from('fixture') : key;
    }
  }
  const names = Object.keys(row);
  db.prepare(`INSERT INTO ${q(table)} (${names.map(q).join(',')}) VALUES (${names.map(() => '?').join(',')})`).run(...names.map(k=>row[k]));
}
function put(root, relative, body) { const file=path.join(root,relative); fs.mkdirSync(path.dirname(file),{recursive:true});fs.writeFileSync(file,body);return file; }
function fixture() {
  const base=fs.mkdtempSync(path.join(TEST_WORK,FIXTURE_PREFIX));
  const dispose=()=>{
    const resolved=path.resolve(base);
    assert.equal(path.dirname(resolved),TEST_WORK);
    assert.ok(path.basename(resolved).startsWith(FIXTURE_PREFIX));
    if(fs.existsSync(resolved)) assert.equal(fs.lstatSync(resolved).isSymbolicLink(),false);
    fs.rmSync(resolved,{recursive:true,force:true});
  };
  try {return populateFixture(base,dispose);}
  catch(error) {dispose();throw error;}
}
function populateFixture(base,dispose) {
  const dataRoot = path.join(base, 'synthetic-codex');
  const trashRoot = path.join(base, 'transactions');
  fs.mkdirSync(dataRoot,{recursive:true});
  for (const [name, objects] of Object.entries(schema)) {
    fs.mkdirSync(path.dirname(path.join(dataRoot,name)),{recursive:true});
    const db=open(dataRoot,name);
    for (const type of ['table','index','trigger','view']) for (const object of objects.filter(x=>x.type===type)) db.exec(object.sql);
    db.close();
  }
  const rolloutPaths = IDS.map((id,index)=>put(dataRoot,`sessions/2026/09/27/rollout-${id}.jsonl`, [
    {timestamp:'2026-09-27T00:00:00Z',type:'session_meta',payload:{id,cwd:base}},
    {timestamp:'2026-09-27T00:00:01Z',type:'response_item',payload:{type:'message',role:'user',content:[{type:'input_text',text:`Invented chat ${index+1}`}]}},
  ].map(x=>JSON.stringify(x)).join('\n')+'\n'));
  {
    const db=open(dataRoot,'state_5.sqlite');
    IDS.forEach((id,i)=>{
      insert(db,'threads',{id,rollout_path:rolloutPaths[i],created_at:1780000000+i,updated_at:1780001000+i,source:'cli',model_provider:'fixture',cwd:base,title:`Synthetic chat ${i+1}`,sandbox_policy:'workspace-write',approval_mode:'on-request'});
      insert(db,'thread_attachments',{id:`attachment-${i}`,thread_id:id,attachment_type:'file',identity_key:`fake-${i}`,payload:'{"synthetic":true}',created_at:1});
      insert(db,'thread_dynamic_tools',{thread_id:id,position:0,name:'fixture',description:'Invented tool',input_schema:'{}'});
    });
    insert(db,'thread_spawn_edges',{parent_thread_id:IDS[0],child_thread_id:IDS[1],status:'completed'});
    db.close();
  }
  {
    const db=open(dataRoot,'thread_history_1.sqlite');
    IDS.forEach(id=>{
      insert(db,'thread_turns',{thread_id:id,turn_id:'turn',rollout_ordinal:1,status:'completed'});
      insert(db,'thread_items',{thread_id:id,turn_id:'turn',item_id:'item',rollout_ordinal:1,created_at_ms:1,item_json:'{"text":"invented message"}',item_type:'message'});
      insert(db,'thread_realtime_items',{thread_id:id,item_id:'realtime',rollout_ordinal:2,created_at_ms:2,item_type:'message',item_json:'{"text":"invented realtime"}'});
      insert(db,'thread_history_projection_state',{thread_id:id,next_rollout_byte_offset:0,next_rollout_ordinal:3});
    }); db.close();
  }
  {
    const db=open(dataRoot,'memories_1.sqlite');
    IDS.forEach(id=>insert(db,'stage1_outputs',{thread_id:id,source_updated_at:1,raw_memory:'Invented personal preference',rollout_summary:'Invented summary',generated_at:1}));
    insert(db,'jobs',{kind:'consolidation',job_key:'global',status:'complete',retry_remaining:0});
    insert(db,'consolidation_progress',{singleton:1,max_thread_count:2});db.close();
  }
  {
    const db=open(dataRoot,'goals_1.sqlite');
    IDS.forEach(id=>{insert(db,'thread_goals',{thread_id:id,goal_id:'goal',objective:'Invented goal',status:'complete',created_at_ms:1,updated_at_ms:1});insert(db,'thread_goal_continuation_deferrals',{thread_id:id});});db.close();
  }
  {
    const db=open(dataRoot,'queue_1.sqlite');
    IDS.forEach((id,i)=>insert(db,'queued_items',{id:`queued-${i}`,thread_id:id,payload_json:'{"text":"invented queued content"}',queue_order:i,created_at_ms:1,updated_at_ms:1}));db.close();
  }
  {
    const db=open(dataRoot,'logs_2.sqlite');
    IDS.forEach(id=>insert(db,'logs',{ts:1,ts_nanos:1,level:'INFO',target:'fixture',feedback_log_body:'Invented log',thread_id:id}));
    insert(db,'logs',{ts:1,ts_nanos:1,level:'INFO',target:'fixture',feedback_log_body:'Invented unlinked log',thread_id:null});db.close();
  }
  {
    const db=open(dataRoot,'sqlite/codex-dev.db');
    insert(db,'local_thread_catalog_hosts',{host_id:'local',host_kind:'local'});
    IDS.forEach((id,i)=>{
      insert(db,'automation_runs',{thread_id:id,automation_id:`run-${i}`,status:'complete',thread_title:`Synthetic chat ${i+1}`,archived_user_message:'Invented archived input',archived_assistant_message:'Invented archived reply',created_at:1,updated_at:1});
      insert(db,'inbox_items',{id:`inbox-${i}`,title:'Invented inbox title',description:'Invented inbox detail',thread_id:id,created_at:1});
      insert(db,'live_visualization_suggestions',{account_id:'fixture',user_id:'fixture',host_id:'local',thread_id:id,id:`suggestion-${i}`,title:'Invented visualization title',description:'Invented visualization detail',status:'pending'});
      insert(db,'local_thread_catalog',{host_id:'local',thread_id:id,display_title:`Synthetic chat ${i+1}`,source_created_at:1,source_updated_at:1,source_kind:'local',observation_sequence:1});
      insert(db,'local_thread_catalog_scan_entries',{host_id:'local',thread_id:id,removed:0});
      insert(db,'thread_timeline_ledger',{host_id:'local',thread_id:id,sequence:1,record_id:`record-${i}`,payload_json:'{"text":"Invented timeline content"}'});
    }); db.close();
  }
  put(dataRoot,'session_index.jsonl',IDS.map((id,i)=>JSON.stringify({id,thread_name:`Synthetic chat ${i+1}`,updated_at:'2026-09-27T00:00:00Z'})).join('\n')+'\n');
  put(dataRoot,'.codex-global-state.json',JSON.stringify({'electron-persisted-atom-state':{'prompt-history':{[IDS[0]]:['invented selected input'],[IDS[1]]:['invented retained input'],global:['invented global input']},'composer-prompt-drafts-v2':{[IDS[0]]:'Invented selected draft',[IDS[1]]:'Invented retained draft'}},'saved-workspace-roots':[base]}));
  put(dataRoot,'history.jsonl',JSON.stringify({session_id:IDS[0],ts:1,text:'Invented history'})+'\n');
  put(dataRoot,'memories/MEMORY.md','Invented consolidated memory');
  put(dataRoot,'memories_v2/memory.txt','Invented v2 memory');
  put(dataRoot,'memories_extensions/extension.txt','Invented extension memory');
  put(dataRoot,'config.toml','model = "synthetic-model"\n');
  put(dataRoot,'auth.json','{"syntheticCredential":"not-a-real-secret"}');
  put(dataRoot,'plugins/example/README.md','Unrelated plugin');
  put(dataRoot,'skills/example/SKILL.md','Unrelated skill');
  put(dataRoot,'mystery-user-file.txt','Keep unknown data');
  put(dataRoot,'log/codex-tui.log','Invented shared text log');
  return {base,dataRoot,trashRoot,rolloutPaths,ids:IDS,dispose};
}
function rows(dataRoot,name,table) {const db=open(dataRoot,name);try{return db.prepare(`SELECT * FROM ${q(table)}`).all();}finally{db.close();}}
module.exports={fixture,rows,open,insert,put,IDS,TEST_WORK};
