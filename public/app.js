'use strict';
const $=id=>document.getElementById(id);
const ui={state:null,view:'chats',selected:new Set(),clearMemories:false,clearSharedHistory:false,filePath:'',action:null,dialogOpener:null,busy:false,closed:false,process:null,compatible:false,refreshing:false};
const tokenFromUrl=new URLSearchParams(location.hash.slice(1)).get('token');
let token=tokenFromUrl || '';
try{if(tokenFromUrl)sessionStorage.setItem('codex-cleaner-token',tokenFromUrl);else token=sessionStorage.getItem('codex-cleaner-token') || '';}catch{/* The launch fragment still works when browser storage is disabled. */}
if(tokenFromUrl)history.replaceState(null,'',location.pathname);
function el(tag,cls,text){const n=document.createElement(tag);if(cls)n.className=cls;if(text!==undefined)n.textContent=text;return n;}
function icon(name){const svg=document.createElementNS('http://www.w3.org/2000/svg','svg'),use=document.createElementNS('http://www.w3.org/2000/svg','use');svg.setAttribute('class','icon');svg.setAttribute('aria-hidden','true');svg.setAttribute('focusable','false');use.setAttribute('href','#icon-'+name);svg.append(use);return svg;}
function show(n,visible){n.classList.toggle('hidden',!visible);}
function setLoading(n,loading){n.classList.toggle('is-loading',loading);if(loading)n.setAttribute('aria-busy','true');else n.removeAttribute('aria-busy');}
function fmtBytes(n){if(n===null||n===undefined||!Number.isFinite(Number(n)))return '—';n=Number(n);for(const u of ['B','KB','MB','GB']){if(n<1024||u==='GB')return `${n<10&&u!=='B'?n.toFixed(1):Math.round(n)} ${u}`;n/=1024;}return '—';}
function fmtDate(v){if(!v)return '—';if(typeof v==='number' && v<1e12)v*=1000;const d=new Date(v);return isNaN(d)?String(v):d.toLocaleString('zh-CN',{year:'numeric',month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit',hour12:false});}
async function api(route,body){const r=await fetch('/api/'+route,{method:body===undefined?'GET':'POST',headers:{'x-cleaner-token':token,...(body===undefined?{}:{'Content-Type':'application/json'})},...(body===undefined?{}:{body:JSON.stringify(body)})});const data=await r.json();if(!r.ok)throw new Error(data.error||`请求失败 (${r.status})`);return data;}
function toast(text){$('toast').textContent=text;show($('toast'),true);clearTimeout(toast.timer);toast.timer=setTimeout(()=>show($('toast'),false),6000);}
function alertError(e){$('alerts').replaceChildren(el('div','alert error',e.message||String(e)));}
function setProcess(status){if(ui.closed)return;ui.process=status;const ready=status.verified!==false&&!status.running;$('status-banner').className='status-banner '+(ready?'ready':'');$('status-title').textContent=ready?'Codex 已退出':'Codex 正在运行';$('status-detail').textContent=ready?'可以清理，执行期间请保持关闭。':'退出后即可清理；现在可浏览和预览。';$('process-pill').textContent=ready?'可执行':'只读浏览';if(status.verified===false){$('status-title').textContent='无法确认进程状态 · 清理已锁定';$('status-detail').textContent=status.error||'请重新打开工具再试。';}updateConfirm();}
function warningTexts(values){return (values||[]).map(x=>typeof x==='string'?x:x.message||x.reason||JSON.stringify(x));}
async function refresh(){
  if(ui.busy||ui.refreshing)return;ui.refreshing=true;$('refresh').disabled=true;setLoading($('refresh'),true);
  try{
    const state=await api('state');ui.compatible=state.apiVersion===1;
    if(!ui.compatible){ui.state=null;renderSelection();throw new Error('后台版本不匹配。请点击“退出工具”，再双击新版 Start-CodexCleaner.cmd，使用新打开的页面。');}
    ui.state=state;$('app-version').textContent=state.version;setProcess(state.processStatus);
    const ids=new Set((state.chats||[]).filter(x=>x.canDelete!==false).map(x=>x.id));for(const id of ui.selected)if(!ids.has(id))ui.selected.delete(id);
    $('data-root').textContent=state.dataRoot;$('nav-count').textContent=(state.chats||[]).length;
    const warnings=warningTexts(state.warnings).filter(x=>!x.startsWith('本工具只处理本机已识别的数据'));
    for(const issue of state.recoveryIssues||[])warnings.push((issue.blocksWrites?'上次操作异常中断，继续清理已锁定。故障材料：':'上次操作的临时副本未能移除：')+(issue.directory||issue.path||issue.id||'请查看启动日志')+(issue.error?'。'+issue.error:''));
    $('alerts').replaceChildren(...warnings.map(x=>el('div','alert',x)));renderChats();if(ui.view==='files')await browse(ui.filePath);renderSelection();
  }catch(e){alertError(e);}finally{ui.refreshing=false;$('refresh').disabled=false;setLoading($('refresh'),false);}
}
function filteredChats(){const q=$('chat-search').value.trim().toLocaleLowerCase();return (ui.state?.chats||[]).filter(x=>`${x.title||''} ${x.id||''}`.toLocaleLowerCase().includes(q));}
function blockedLabel(chat){const codes={database:'数据库检查未通过','automation-inspection':'关联检查失败','unsafe-path':'路径不可清理',automation_reference:'有关联任务',automation:'有关联任务',outside_root:'路径超出范围',outside_data_root:'路径超出范围',unsafe_path:'路径不可清理',schema_unsupported:'数据版本待适配',unsupported_schema:'数据版本待适配'};return codes[chat.reasonCode]||'暂不可清理';}
function renderChats(){
  const chats=filteredChats(),tbody=$('chat-list'),active=document.activeElement,focusId=active?.type==='checkbox'&&(tbody.contains(active)||active===$('select-all'))?active.id:null;tbody.replaceChildren();let selectedVisible=0;const selectable=chats.filter(x=>x.canDelete!==false);
  for(const chat of chats){
    const tr=el('tr',ui.selected.has(chat.id)?'selected':''),tc=el('td','checkbox-cell'),target=el('label','check-target'),check=el('input');check.id='chat-select-'+chat.id;check.type='checkbox';check.checked=ui.selected.has(chat.id);check.disabled=chat.canDelete===false;check.setAttribute('aria-label','选择聊天：'+(chat.title||chat.id));if(check.checked)selectedVisible++;
    check.addEventListener('change',()=>{check.checked?ui.selected.add(chat.id):ui.selected.delete(chat.id);renderChats();renderSelection();});target.append(check);tc.append(target);
    const name=el('td'),title=el('span','chat-title',chat.title||'未命名聊天');title.title=chat.title||chat.id;name.append(title,el('span','chat-id',chat.id));
    if(chat.canDelete===false)name.append(el('small','block-reason',chat.reason||'暂不可清理，请查看检查结果。'));else if(chat.warning||chat.reason)name.append(el('small','chat-note',chat.warning||chat.reason));
    const updated=el('td','chat-date',fmtDate(chat.updatedAt||chat.updated_at||chat.createdAt)),badge=el('td');badge.append(el('span','badge '+(chat.canDelete===false?'blocked':chat.archived?'archived':''),chat.canDelete===false?blockedLabel(chat):chat.archived?'已归档':'本地记录'));tr.append(tc,name,updated,badge);tbody.append(tr);
  }
  show($('chat-empty'),chats.length===0);$('chat-total').textContent=chats.length+' 条记录';$('select-all').disabled=!selectable.length;$('select-all').checked=!!selectable.length&&selectedVisible===selectable.length;$('select-all').indeterminate=selectedVisible>0&&selectedVisible<selectable.length;
  if(focusId)$(focusId)?.focus({preventScroll:true});
}
function renderSelection(){const items=[];if(ui.selected.size)items.push(`${ui.selected.size} 个聊天`);if(ui.clearMemories)items.push('全部本地记忆');if(ui.clearSharedHistory)items.push('共享输入历史与日志');$('selection-summary').textContent=items.length?'已选择：'+items.join(' + '):'尚未选择清理内容';$('preview').disabled=!items.length||ui.busy||!ui.compatible;$('repair').disabled=ui.busy||!ui.compatible;$('chats-memory').checked=ui.clearMemories;$('memory-all').checked=ui.clearMemories;$('chats-shared').checked=ui.clearSharedHistory;}
function setView(name){ui.view=name;document.querySelectorAll('.view').forEach(n=>show(n,n.id==='view-'+name));document.querySelectorAll('.nav').forEach(n=>{const active=n.dataset.view===name;n.classList.toggle('active',active);if(active)n.setAttribute('aria-current','page');else n.removeAttribute('aria-current');});$('view-label').textContent={chats:'聊天记录',memory:'本地记忆',files:'文件用途'}[name];if(name==='files')browse(ui.filePath).catch(alertError);window.scrollTo({top:0});}
const filePathOf=x=>x.path||x.relativePath||x.name||'';
const fileNameOf=x=>x.name||filePathOf(x).split(/[\\/]/).pop();
const directoryOf=x=>x.kind==='directory'||x.type==='directory'||x.isDirectory===true;
function inventoryArray(result){if(Array.isArray(result))return result;return result.children||result.entries||result.inventory||[];}
function purposeOf(x){return x.purpose||x.description||'尚未确认用途；保留，不自动清理。';}
function policyOf(x){const value=x.policy||x.action||'';const map={preserve:'保留',protected:'保留',unknown:'未知 · 保留',associated:'随关联聊天',managed:'按选项清理',cleanup:'按选项清理',review:'需人工核对'};return map[value]||value||'保留';}
async function browse(relative=''){if(!ui.state)return;const info=relative?await api('file?path='+encodeURIComponent(relative)):ui.state.inventory;const rows=inventoryArray(info);ui.filePath=relative;show($('file-detail'),false);const crumbs=$('breadcrumbs');crumbs.replaceChildren();const root=el('button','','Codex 数据');root.addEventListener('click',()=>browse('').catch(alertError));crumbs.append(root);const parts=relative.replace(/\\/g,'/').split('/').filter(Boolean);parts.forEach((part,i)=>{crumbs.append(el('span','','/'));const button=el('button','',part);button.addEventListener('click',()=>browse(parts.slice(0,i+1).join('/')).catch(alertError));crumbs.append(button);});$('file-up').disabled=!relative;const tbody=$('file-list');tbody.replaceChildren();for(const x of rows){const rel=filePathOf(x);const tr=el('tr');const td=el('td');const button=el('button','file-name');button.append(icon(directoryOf(x)?'folder':'file'),el('span','',fileNameOf(x)));button.title=rel;button.addEventListener('click',async()=>{try{if(directoryOf(x))await browse(rel);else renderFileDetail(await api('file?path='+encodeURIComponent(rel)));}catch(e){alertError(e);}});td.append(button);tr.append(td,el('td','file-purpose',purposeOf(x)),el('td','file-policy',policyOf(x)),el('td','file-size',fmtBytes(x.sizeBytes??x.size)));tbody.append(tr);}show($('file-empty'),!rows.length);}
function renderFileDetail(info){const box=$('file-detail');box.replaceChildren(el('h2','',fileNameOf(info)));box.append(el('p','',purposeOf(info)));const list=el('dl','detail-grid');for(const [label,value] of [['相对路径',filePathOf(info)],['类型',info.kind||info.type||'文件'],['处理方式',policyOf(info)],['大小',fmtBytes(info.sizeBytes??info.size)],['修改时间',fmtDate(info.modifiedAt||info.mtimeMs)]]){list.append(el('dt','',label),el('dd','',value));}box.append(list);if(info.warning)box.append(el('p','',info.warning));show(box,true);box.scrollIntoView({behavior:matchMedia('(prefers-reduced-motion: reduce)').matches?'auto':'smooth',block:'nearest'});}

function sectionList(root,title,items){if(!items.length)return;root.append(el('h3','',title));const ul=el('ul');for(const item of items)ul.append(el('li','',item));root.append(ul);}
function detailList(root,title,items){if(!items.length)return;const details=el('details','plan-details'),summary=el('summary','',`${title} · ${items.length}`),ul=el('ul');for(const item of items)ul.append(el('li','',item));details.append(summary,ul);root.append(details);}
function describePlanFile(x){if(typeof x==='string')return x;return [x.relativePath||x.path||x.name||x.file,({remove:'删除',rewrite:'更新关联记录'}[x.action]||x.action||x.operation),x.reason||x.purpose].filter(Boolean).join(' · ')||JSON.stringify(x);}
function describePlanDb(x){if(typeof x==='string')return x;const name=x.relativePath||x.path||x.name||x.database||x.file||'';const tables=Array.isArray(x.tables)?x.tables.map(t=>typeof t==='string'?t:`${t.name||t.table}${t.action==='clear-reference'?'（解除关联，保留记录）':''}: ${t.count??t.rows??'—'} 条`).join('；'):'';return [name,x.purpose,tables,x.rowCount!==undefined?`${x.rowCount} 条`:null].filter(Boolean).join(' · ')||JSON.stringify(x);}
function baseDialog(title,action,label,opener){ui.action=action;ui.dialogOpener=opener;$('dialog-title').textContent=title;$('confirm-action').textContent=label;$('confirm-action').className='button '+(action.route==='execute'?'danger':'primary');$('dialog-error').textContent='';show($('dialog-error'),false);updateConfirm();$('review-dialog').showModal();}
function renderPlanDetails(plan,content){detailList(content,'涉及的数据库',(plan.databases||[]).map(describePlanDb));detailList(content,'涉及的文件',(plan.files||[]).map(describePlanFile));detailList(content,'清理说明',warningTexts(plan.warnings));if(plan.expiresAt)content.append(el('p','muted','预览有效至 '+fmtDate(plan.expiresAt)+'；数据发生变化时需要重新预览。'));}
async function preview(){
  if(ui.busy||!ui.compatible)return;ui.busy=true;setLoading($('preview'),true);renderSelection();
  try{
    const plan=await api('plan',{chatIds:[...ui.selected],clearMemories:ui.clearMemories,clearSharedHistory:ui.clearSharedHistory}),content=$('dialog-content');content.replaceChildren();
    const summary=el('div','plan-summary');summary.append(el('span','',(plan.chatIds?.length??ui.selected.size)+' 个聊天'));if(plan.selection?.clearMemories)summary.append(el('span','','全部本地记忆'));if(plan.selection?.clearSharedHistory)summary.append(el('span','','共享输入历史与日志'));
    content.append(summary,el('div','plan-warning','确认后直接删除所选内容，无法通过本工具撤销。'));sectionList(content,'选中的聊天',(plan.chats||[]).map(x=>(x.title||'未命名聊天')+'（'+x.id+'）'));renderPlanDetails(plan,content);baseDialog('确认清理范围',{route:'execute',id:plan.id},'确认删除',$('preview'));
  }catch(e){alertError(e);}finally{ui.busy=false;setLoading($('preview'),false);renderSelection();updateConfirm();}
}
async function previewRepair(){
  if(ui.busy||!ui.compatible)return;ui.busy=true;setLoading($('repair'),true);renderSelection();
  try{
    const plan=await api('plan-repair',{}),content=$('dialog-content');content.replaceChildren();const count=plan.summary?.referenceRepairs??0;
    const hasChanges=!!count||(plan.files||[]).length>0||(plan.databases||[]).some(x=>x.tables?.some(t=>Number(t.count||t.rows)>0));
    if(!hasChanges){toast('检查完成，没有发现可修复的失效引用。');return;}
    const summary=el('div','plan-summary');summary.append(el('span','',count+' 处界面引用'));if(plan.summary?.databaseRows)summary.append(el('span','',plan.summary.databaseRows+' 条数据库记录'));content.append(summary,el('p','','清除项目迁移、聊天列表和归档索引中已失效的本机聊天引用。项目目录和有效聊天将保留。'));renderPlanDetails(plan,content);baseDialog('修复失效引用',{route:'execute-repair',id:plan.id},'确认修复',$('repair'));
  }catch(e){alertError(e);}finally{ui.busy=false;setLoading($('repair'),false);renderSelection();updateConfirm();}
}

function updateConfirm(){const ready=ui.process&&ui.process.verified!==false&&!ui.process.running;$('confirm-status').textContent=ready?'Codex 已退出，可以确认执行。':'请先退出 Codex，状态会自动更新。';if(!ui.compatible)$('confirm-status').textContent='请退出旧版清理工具并重新启动新版。';$('confirm-action').disabled=ui.busy||!ui.compatible||!ready||!ui.action;}
async function confirmAction(){
  if($('confirm-action').disabled||!ui.action)return;ui.busy=true;const action=ui.action,opener=ui.dialogOpener;updateConfirm();renderSelection();$('close-dialog').disabled=true;$('cancel-dialog').disabled=true;const previous=$('confirm-action').textContent;$('confirm-action').textContent='正在处理…';setLoading($('confirm-action'),true);
  try{
    const response=await api(action.route,{id:action.id});dismissDialog(false);
    if(action.route==='execute'){ui.selected.clear();ui.clearMemories=false;ui.clearSharedHistory=false;toast('所选内容已删除。');}else toast('失效引用已修复。可以重新启动 Codex 检查项目和归档。');
    ui.busy=false;await refresh();if(response.result?.residualTransaction)toast('清理已完成，但临时副本未完全移除，请查看上方具体路径。');
  }catch(e){$('dialog-error').textContent=e.message;show($('dialog-error'),true);}finally{ui.busy=false;$('close-dialog').disabled=false;$('cancel-dialog').disabled=false;$('confirm-action').textContent=previous;setLoading($('confirm-action'),false);updateConfirm();renderSelection();if(!$('review-dialog').open)returnFocus(opener);}
}
document.querySelectorAll('.nav').forEach(n=>n.addEventListener('click',()=>setView(n.dataset.view)));
$('refresh').addEventListener('click',refresh);$('chat-search').addEventListener('input',renderChats);$('select-all').addEventListener('change',()=>{const checked=$('select-all').checked;for(const x of filteredChats())if(x.canDelete!==false)checked?ui.selected.add(x.id):ui.selected.delete(x.id);renderChats();renderSelection();});
for(const id of ['chats-memory','memory-all'])$(id).addEventListener('change',()=>{ui.clearMemories=$(id).checked;renderSelection();});
$('chats-shared').addEventListener('change',()=>{ui.clearSharedHistory=$('chats-shared').checked;renderSelection();});$('preview').addEventListener('click',preview);$('repair').addEventListener('click',previewRepair);$('confirm-action').addEventListener('click',confirmAction);
function returnFocus(opener){requestAnimationFrame(()=>{if(opener?.isConnected&&!opener.disabled)opener.focus({preventScroll:true});});}
function dismissDialog(restoreFocus=true){const opener=ui.dialogOpener;$('review-dialog').close();ui.action=null;ui.dialogOpener=null;if(restoreFocus)returnFocus(opener);}
function closeDialog(){if(!ui.busy)dismissDialog();}$('close-dialog').addEventListener('click',closeDialog);$('cancel-dialog').addEventListener('click',closeDialog);$('review-dialog').addEventListener('cancel',e=>{e.preventDefault();closeDialog();});
$('file-up').addEventListener('click',()=>{const parts=ui.filePath.replace(/\\/g,'/').split('/');parts.pop();browse(parts.join('/')).catch(alertError);});
$('shutdown').addEventListener('click',async()=>{
  if(ui.busy||ui.refreshing||ui.closed)return;
  ui.busy=true;$('shutdown').disabled=true;renderSelection();
  try{
    await api('shutdown',{});ui.closed=true;clearInterval(processTimer);
    try{sessionStorage.removeItem('codex-cleaner-token');}catch{}token='';
    $('status-title').textContent='工具已退出';$('status-detail').textContent='可以关闭此浏览器页面。下次双击启动文件即可。';$('process-pill').textContent='已退出';$('status-banner').className='status-banner loading';document.querySelectorAll('button,input').forEach(x=>x.disabled=true);
  }catch(e){ui.busy=false;$('shutdown').disabled=false;renderSelection();alertError(e);}
});
let processTimer=setInterval(async()=>{if(ui.busy)return;try{setProcess(await api('process'));}catch{setProcess({running:true,verified:false,error:'无法连接清理工具，请重新双击启动。'});}},5000);
if(!token){clearInterval(processTimer);alertError(new Error('请双击 Start-CodexCleaner.cmd 启动工具，直接打开此网页无法操作。'));}else refresh();
