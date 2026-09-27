'use strict';
const test=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const vm=require('node:vm');
const source=fs.readFileSync(path.join(__dirname,'../public/app.js'),'utf8');
const {version}=require('../package.json');
const response=body=>({ok:true,status:200,json:async()=>body});
const flush=()=>new Promise(resolve=>setImmediate(resolve));

// Run the shipped event handlers with controllable network completion order.
// This harness checks behavior only; browser rendering is verified separately.
function boot({storageDisabled=false,withToken=true}={}) {
  const elements=new Map(),intervals=new Map(),stored=new Map(),calls=[];
  const processReply=Promise.withResolvers(),shutdownReply=Promise.withResolvers();
  let timerId=0,cleanUrl;
  function element(tag='div') {
    const classes=new Set(),attributes=new Map(),listeners=new Map();
    return {
      tagName:tag.toUpperCase(),textContent:'',className:'',value:'',disabled:false,checked:false,children:[],listeners,
      classList:{toggle(name,force){const enabled=force===undefined?!classes.has(name):force;enabled?classes.add(name):classes.delete(name);return enabled;}},
      addEventListener(event,callback){listeners.set(event,callback);},
      setAttribute(name,value){attributes.set(name,value);},removeAttribute(name){attributes.delete(name);},
      append(...children){this.children.push(...children);},replaceChildren(...children){this.children=children;},
      contains(){return false;},focus(){},scrollIntoView(){},
    };
  }
  const get=id=>{
    if(!elements.has(id)) elements.set(id,Object.assign(element(),{id}));
    return elements.get(id);
  };
  const controls=['refresh','shutdown','chat-search','select-all','chats-memory','memory-all','chats-shared','preview','repair','confirm-action','close-dialog','cancel-dialog','file-up'].map(get);
  const document={
    activeElement:null,getElementById:get,createElement:element,createElementNS:(_,tag)=>element(tag),
    querySelectorAll(selector){return selector==='button,input'?controls:[];},
  };
  const state={apiVersion:1,version,dataRoot:'synthetic-codex',chats:[],inventory:[],warnings:[],recoveryIssues:[],processStatus:{running:false,verified:true,processes:[]}};
  const context=vm.createContext({
    document,URLSearchParams,location:{hash:withToken?'#token=synthetic-local-token':'',pathname:'/'},
    history:{replaceState(_state,_title,url){cleanUrl=url;}},
    sessionStorage:{
      setItem(key,value){if(storageDisabled)throw new Error('Storage disabled');stored.set(key,value);},
      getItem(key){if(storageDisabled)throw new Error('Storage disabled');return stored.get(key) || null;},
      removeItem(key){if(storageDisabled)throw new Error('Storage disabled');stored.delete(key);},
    },
    fetch(url,options){
      calls.push({url,options});
      if(url==='/api/state') return Promise.resolve(response(state));
      if(url==='/api/process') return processReply.promise;
      if(url==='/api/shutdown') return shutdownReply.promise;
      throw new Error('Unexpected test request: '+url);
    },
    setInterval(callback){const id=++timerId;intervals.set(id,callback);return id;},
    clearInterval(id){intervals.delete(id);},setTimeout(){return 0;},clearTimeout(){},
    requestAnimationFrame(callback){callback();},window:{scrollTo(){}},matchMedia(){return {matches:true};},
  });
  vm.runInContext(source,context,{filename:'public/app.js'});
  return {
    get,controls,calls,stored,intervals,processReply,shutdownReply,
    cleanUrl:()=>cleanUrl,
    poll:()=>{assert.equal(intervals.size,1);return [...intervals.values()][0]();},
    click:id=>get(id).listeners.get('click')({target:get(id)}),
  };
}

for(const lateResult of ['success','failure']) {
  test(`shutdown remains final when an earlier process request completes with ${lateResult}`,async()=>{
    const app=boot();await flush();
    assert.equal(app.get('status-title').textContent,'Codex 已退出');
    const poll=app.poll();
    assert.equal(app.calls.filter(x=>x.url==='/api/process').length,1);
    const shutdown=app.click('shutdown');
    assert.equal(app.get('shutdown').disabled,true);
    await app.poll();
    assert.equal(app.calls.filter(x=>x.url==='/api/process').length,1,'shutdown prevents starting another poll');
    app.shutdownReply.resolve(response({ok:true}));await shutdown;
    assert.equal(app.intervals.size,0);
    if(lateResult==='success') app.processReply.resolve(response({running:true,verified:true,processes:[{name:'synthetic',pid:1}]}));
    else app.processReply.reject(new Error('Synthetic closed server'));
    await poll;
    assert.equal(app.get('status-title').textContent,'工具已退出');
    assert.equal(app.get('status-detail').textContent,'可以关闭此浏览器页面。下次双击启动文件即可。');
    assert.equal(app.get('process-pill').textContent,'已退出');
    assert.ok(app.controls.every(control=>control.disabled),'late responses cannot re-enable controls');
    assert.equal(app.stored.has('codex-cleaner-token'),false);
  });
}

test('launch token works when browser session storage is disabled and is removed from the URL',async()=>{
  const app=boot({storageDisabled:true});await flush();
  assert.equal(app.cleanUrl(),'/');
  assert.equal(app.calls.length,1);
  assert.equal(app.calls[0].options.headers['x-cleaner-token'],'synthetic-local-token');
  assert.equal(app.get('status-title').textContent,'Codex 已退出');
  assert.equal(app.get('alerts').children.length,0);
  const shutdown=app.click('shutdown');app.shutdownReply.resolve(response({ok:true}));await shutdown;
  assert.equal(app.get('status-title').textContent,'工具已退出');
  assert.ok(app.controls.every(control=>control.disabled));
});

test('missing token with disabled session storage shows launch instructions without requesting data',()=>{
  const app=boot({storageDisabled:true,withToken:false});
  assert.equal(app.calls.length,0);assert.equal(app.intervals.size,0);
  assert.match(app.get('alerts').children[0].textContent,/Start-CodexCleaner\.cmd/);
});
