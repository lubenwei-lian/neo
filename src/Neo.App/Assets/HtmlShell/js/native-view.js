  var nativeLast={},nativeSnapshot=null,nativePreviewKey=null,nativePendingNode=null;
  function nativeSignalViewReady(){
    if(nativeLast.ready)return;
    nativeLast.ready=true;
    var sent=false,timer;
    function send(){
      if(sent)return;
      sent=true;clearTimeout(timer);
      host('view.ready').catch(function(){});
    }
    // A hidden WebView may suspend animation frames until its native window is shown.
    // Keep the normal two-frame paint path, with a DOM-ready fallback to break that cycle.
    timer=setTimeout(send,150);
    requestAnimationFrame(function(){requestAnimationFrame(send);});
  }
  function nativeOpenFullSublog(){
    var d=showUiDialog('订阅日志','<div class="native-full-sublog">'+(subLogRows()||'<div class="empty-state">没有更新记录</div>')+'</div><div class="dialog-actions"><button class="btn ghost" id="nativeExportSublog">导出</button></div>');
    d.classList.add('native-list-dialog');$('.native-full-sublog',d).classList.add('scroll');
    $$('.lgrow',d).forEach(nativeBindLogRow);
    $('#nativeExportSublog',d).onclick=function(){saveText('neo-订阅日志.csv',csv([['时间','订阅','状态','结果','耗时','响应','来源']].concat(SUBLOG)),'text/csv;charset=utf-8');};
  }
  var nativeExitStorage='neo.exit-regions.v2',nativeExitHistory=Object.create(null),nativeExitPending=new Set(),nativeExitRunning=false,nativeExitPaint=null;
  try{
    var currentExit=localStorage.getItem(nativeExitStorage),storedExit=JSON.parse(currentExit||localStorage.getItem('neo.exit-regions.v1')||'{}');
    if(!currentExit)Object.keys(storedExit).forEach(function(key){if(!storedExit[key]||!storedExit[key].confirmed)delete storedExit[key];});
    Object.keys(storedExit).forEach(function(key){var r=storedExit[key];if(r&&Number.isFinite(r.at)&&r.at>0&&typeof r.confirmed==='boolean'&&typeof r.country==='string'&&typeof r.ip==='string')nativeExitHistory[key]={at:r.at,confirmed:r.confirmed,country:r.country,ip:r.ip,detail:typeof r.detail==='string'?r.detail:'',retryable:r.retryable!==false&&!(r.detail||'').includes('不支持')};});
  }catch(_){}
  function nativeSaveExitHistory(){
    var keys=Object.keys(nativeExitHistory).sort(function(a,b){return nativeExitHistory[b].at-nativeExitHistory[a].at;});
    keys.slice(2000).forEach(function(key){delete nativeExitHistory[key];});
    try{localStorage.setItem(nativeExitStorage,JSON.stringify(nativeExitHistory));}catch(_){toast('出口结果暂存于本次运行，未能写入本地记录');}
  }
  function nativeExitResult(n){
    var saved=nativeExitHistory[n.id];
    return Object.assign({ip:'',country:'',confirmed:false,detail:'未检测',at:0},saved||{},
      {id:n.id,name:n.name,region:n.region,matches:!!saved&&saved.confirmed&&saved.country===n.region,pending:nativeExitPending.has(n.id)});
  }
  function nativeExitLabel(r){
    if(r.pending)return '检测中';
    if(!r.at)return '未检测';
    if(r.confirmed)return r.matches?'已测 · 一致':'已测 · 不符 · '+r.country;
    return r.detail&&r.detail.indexOf('不支持')>=0?'已测 · 不支持':'已测 · 未确认';
  }
  function nativePaintExitMarks(){
    NODES.forEach(function(n,i){
      var card=lists.D[i];if(!card)return;
      var r=nativeExitResult(n),mark=card.querySelector('.native-exit-mark');
      if(!r.at&&!r.pending){if(mark)mark.remove();return;}
      if(!mark){mark=document.createElement('span');mark.className='native-exit-mark';card.querySelector('.node-bottom').insertBefore(mark,card.querySelector('.node-test'));}
      mark.textContent=r.pending?'检测中':r.confirmed?'已测':r.detail.indexOf('不支持')>=0?'不支持':'未确认';
      mark.dataset.state=r.confirmed?(r.matches?'confirmed':'mismatch'):'unconfirmed';
      mark.title='出口地区：'+nativeExitLabel(r)+(r.at?'\n'+[r.country,r.ip].filter(Boolean).join(' · ')+'\n检测时间：'+new Date(r.at).toLocaleString(): '');
    });
  }
  function nativePaintTestControls(){
    var testing=nativeSnapshot&&nativeSnapshot.testing||{},busy=!!testing.running||nativeExitRunning;
    $('#nTestAll').textContent=testing.running?'测速 '+testing.done+' / '+testing.total:'全部测速';
    $('#nTcpAll').textContent='快速 TCP';
    ['#nTestAll','#nTcpAll','#nRetest'].forEach(function(selector){$(selector).disabled=busy;});
    var count=NODES.filter(function(n){return !!nativeExitHistory[n.id];}).length;
    $('#nExitRegions').title='出口地区 · 已测 '+count+' / '+NODES.length;
  }
  function nativeExitNeedsTest(n){var r=nativeExitHistory[n.id];return !r||!r.confirmed&&r.retryable!==false;}
  async function nativeRunExitTests(force){
    if(nativeExitRunning||nativeSnapshot&&nativeSnapshot.testing.running)return;
    var seen=new Set(),requested=NODES.filter(function(n){if(seen.has(n.id)||!force&&!nativeExitNeedsTest(n))return false;seen.add(n.id);return true;}).map(function(n){return {id:n.id,name:n.name,region:n.region};});
    if(!requested.length)return;
    nativeExitRunning=true;requested.forEach(function(n){nativeExitPending.add(n.id);});
    nativePaintExitMarks();nativePaintTestControls();if(nativeExitPaint)nativeExitPaint();
    try{
      var results=await host('nodes.exitTest',{keys:requested.map(function(n){return n.id;})});
      if(!Array.isArray(results))throw new Error('出口检测结果不完整，请重试');
      var byKey=new Map(results.map(function(r){return [r.key,r];}));
      if(byKey.size!==requested.length||requested.some(function(n){return !byKey.has(n.id);}))throw new Error('出口检测结果不完整，请重试');
      var at=Date.now();
      requested.forEach(function(n){var r=byKey.get(n.id);nativeExitHistory[n.id]={ip:r.ip||'',country:r.country||'',confirmed:!!r.confirmed,detail:r.detail||'',retryable:r.retryable!==false,at:at};});
      nativeSaveExitHistory();
    }catch(e){toast(e.message||'出口检测未完成，已保留之前的记录');}
    finally{nativeExitRunning=false;nativeExitPending.clear();nativePaintExitMarks();nativePaintTestControls();if(nativeExitPaint)nativeExitPaint();}
  }
  function nativeOpenExitTest(){
    var d=showUiDialog('出口地区','<p class="native-exit-summary"></p><div class="xlist scroll"></div><div class="dialog-actions"><button class="btn ghost" id="exitExport">导出结果</button><button class="btn ghost" id="exitRetest" title="重新检测全部节点并更新已有记录">重新检测全部</button><button class="btn primary" id="exitRetry">检测未测</button></div>');
    d.classList.add('native-list-dialog','native-exit-dialog');var lastPaint='';
    function paint(){
      if(!d.open)return;
      var results=NODES.map(nativeExitResult),tested=results.filter(function(r){return !!r.at;}).length,busy=nativeExitRunning||nativeSnapshot&&nativeSnapshot.testing.running;
      var signature=JSON.stringify([results,busy]);if(signature===lastPaint)return;lastPaint=signature;
      d.querySelector('.native-exit-summary').textContent='已测 '+tested+' / '+results.length;
      d.querySelector('.xlist').innerHTML=results.map(function(r){var label=nativeExitLabel(r),when=r.at?'检测时间：'+new Date(r.at).toLocaleString():'';return '<div class="xrow" data-exit-key="'+esc(r.id)+'"><span class="xn" title="'+esc(r.name)+'">'+esc(r.name)+'</span><span class="xi">'+esc(r.ip||'—')+'</span><span class="xg">'+esc(r.region)+'</span><span class="xv '+(r.confirmed?(r.matches?'ok':'no'):'')+'" title="'+esc([r.detail,when].filter(Boolean).join('\n'))+'">'+esc(label)+'</span></div>';}).join('');
      $('#exitRetry',d).disabled=busy||!NODES.some(nativeExitNeedsTest);$('#exitRetry',d).textContent=nativeExitRunning?'检测中':'检测未测';
      $('#exitRetest',d).disabled=busy||!results.length;$('#exitExport',d).disabled=!tested;
    }
    nativeExitPaint=paint;d.addEventListener('close',function(){if(nativeExitPaint===paint)nativeExitPaint=null;},{once:true});
    $('#exitRetry',d).onclick=function(){nativeRunExitTests(false);};$('#exitRetest',d).onclick=function(){nativeRunExitTests(true);};
    $('#exitExport',d).onclick=function(){saveText('neo-出口地区.csv',csv([['节点','标注地区','出口 IP','实际地区','结果','检测时间']].concat(NODES.map(nativeExitResult).filter(function(r){return !!r.at;}).map(function(r){return[r.name,r.region,r.ip,r.country,nativeExitLabel(r),new Date(r.at).toISOString()];}))),'text/csv;charset=utf-8');};
    paint();
  }
  function nativeSelectNode(i){
    var key=NODES[i].id;nativePreviewKey=key;nSel=i;
    paintSel(lists.D,i);nodeDetail(i);nativePaintNodeAction();
    if(nativeSnapshot&&key===nativeSnapshot.selected)return Promise.resolve(true);
    // Inspect immediately; reconnecting a server must never delay its fields.
    nativePendingNode=key;nativePaintNodeAction();
    return host('node.select',{key:key}).then(function(){return true;},function(){return false;}).finally(function(){if(nativePendingNode===key)nativePendingNode=null;nativePaintNodeAction();});
  }
  function nativePaintNodeAction(){
    var button=$('#dConnect');if(!button||!nativeSnapshot)return;
    button.disabled=!!nativePendingNode;
    button.textContent=nativePendingNode?'切换中':nSel!==CUR?'使用此节点':nativeSnapshot.connecting?'取消连接':nativeSnapshot.connected?'断开':'连接';
  }
  function nativeConnectInspected(){
    if(nativePendingNode)return;
    if(nSel!==CUR&&NODES[nSel]){nativeSelectNode(nSel).then(function(ok){if(ok&&!nativeSnapshot.connected)host('connection.toggle').catch(function(){});});return;}
    toggleConn();
  }
  function nativeSaveInterval(value){
    if(value==='manual')return host('settings.set',{title:'自动更新订阅',value:false}).catch(function(){});
    return host('settings.set',{title:'更新间隔',value:Number(value)}).catch(function(){});
  }
  function nativeBindLogRow(row){
    var detail=row.querySelector('.lgdetail');if(!detail)return;
    function setHeight(open){
      row.classList.toggle('open',open);row.setAttribute('aria-expanded',String(open));
      detail.style.setProperty('max-height','none','important');
      detail.style.setProperty('height',(open?detail.scrollHeight:0)+'px','important');
      detail.style.setProperty('opacity',open?'1':'0','important');
    }
    setHeight(row.classList.contains('open'));
    row.setAttribute('aria-expanded',String(row.classList.contains('open')));row.tabIndex=0;row.setAttribute('role','button');
    function toggle(){setHeight(!row.classList.contains('open'));}
    row.onclick=toggle;row.onkeydown=function(e){if(e.key==='Enter'||e.key===' '){e.preventDefault();toggle();}};
  }
  function nativeSyncSublogs(){
    var host=$('#subsBody1 .s1log .rows'),saved=new Map(),counts=new Map();if(!host)return;
    $$('.lgrow',host).forEach(function(row){saved.set(row.dataset.nativeLogKey,row);});
    var t=document.createElement('template');t.innerHTML=subLogRows();
    Array.from(t.content.children).forEach(function(fresh,i){
      var r=SUBLOG[i],base=JSON.stringify([r[0],r[1],r[6]]),count=counts.get(base)||0;counts.set(base,count+1);
      var key=base+':'+count,signature=JSON.stringify(r),row=saved.get(key);saved.delete(key);
      if(!row||row.dataset.nativeLogSignature!==signature){var open=row&&row.classList.contains('open');if(row)row.remove();row=fresh;row.classList.toggle('open',!!open);row.dataset.nativeLogKey=key;row.dataset.nativeLogSignature=signature;nativeBindLogRow(row);}
      if(host.children[i]!==row){if(host.moveBefore&&row.parentNode===host)host.moveBefore(row,host.children[i]||null);else host.insertBefore(row,host.children[i]||null);}
    });saved.forEach(function(row){row.remove();});
  }
  var nativeUsageTip=null,nativeUsageBar=null;
  function nativeHideUsage(){if(nativeUsageTip)nativeUsageTip.hidden=true;if(nativeUsageBar)nativeUsageBar.removeAttribute('aria-describedby');nativeUsageBar=null;}
  function nativeShowUsage(bar){
    if(!bar||!bar.isConnected)return;
    if(!nativeUsageTip){nativeUsageTip=document.createElement('div');nativeUsageTip.id='nativeUsageTip';nativeUsageTip.className='native-usage-tip';nativeUsageTip.setAttribute('role','tooltip');document.body.appendChild(nativeUsageTip);}
    nativeUsageBar=bar;var i=+bar.dataset.dayIndex,v=DAYS[i]||0,date=new Date();date.setDate(date.getDate()-DAYS.length+1+i);
    var text=v>=1?v.toFixed(2)+' GB':v>0?(v*1024>=1?(v*1024).toFixed(1)+' MB':(v*1048576).toFixed(1)+' KB'):'0 B';
    nativeUsageTip.textContent=date.toLocaleDateString('sv-SE')+' · '+text;nativeUsageTip.hidden=false;bar.setAttribute('aria-describedby',nativeUsageTip.id);
    var r=bar.getBoundingClientRect(),w=nativeUsageTip.offsetWidth,h=nativeUsageTip.offsetHeight;
    nativeUsageTip.style.left=Math.max(8,Math.min(innerWidth-w-8,r.left+r.width/2-w/2))+'px';nativeUsageTip.style.top=Math.max(8,r.top-h-8)+'px';
  }
  function nativeBindUsage(){$$('#subsBody1 .uchart>i').forEach(function(bar,i){bar.dataset.dayIndex=i;bar.removeAttribute('title');bar.tabIndex=0;});}
  document.addEventListener('pointerover',function(e){var bar=e.target.closest('#subsBody1 .uchart>i');if(bar&&bar!==nativeUsageBar)nativeShowUsage(bar);});
  document.addEventListener('pointerout',function(e){if(nativeUsageBar&&(!e.relatedTarget||!nativeUsageBar.contains(e.relatedTarget)))nativeHideUsage();});
  document.addEventListener('focusin',function(e){if(e.target.matches('#subsBody1 .uchart>i'))nativeShowUsage(e.target);});
  document.addEventListener('focusout',nativeHideUsage);document.addEventListener('visibilitychange',nativeHideUsage);document.addEventListener('scroll',nativeHideUsage,true);
  function patchNativeElement(old,fresh){
    Array.from(old.attributes).forEach(function(a){if(!fresh.hasAttribute(a.name))old.removeAttribute(a.name);});
    Array.from(fresh.attributes).forEach(function(a){if(old.getAttribute(a.name)!==a.value)old.setAttribute(a.name,a.value);});
    var before=Array.from(old.childNodes),after=Array.from(fresh.childNodes);
    after.forEach(function(node,i){var was=before[i];if(!was){old.appendChild(node);return;}if(was.nodeType!==node.nodeType||was.nodeName!==node.nodeName){old.replaceChild(node,was);return;}if(node.nodeType===3){if(was.data!==node.data)was.data=node.data;}else if(node.nodeType===1)patchNativeElement(was,node);});
    before.slice(after.length).forEach(function(node){node.remove();});
  }
  var nativeSubPillObserver=null;
  function nativeRefreshSubPill(){
    var list=$('#subsBody1 .s1list'),rows=list&&$('.rows',list);if(!rows||!rows.offsetWidth)return;
    paintSubPill(false);
    var pill=$('.srow-pill',list),row=$$('.srow',rows).find(function(e,i){return SUBS[i]&&SUBS[i].primary;});if(!pill||!row)return;
    var rr=row.getBoundingClientRect(),box=rows.getBoundingClientRect();
    pill.style.clipPath='inset('+Math.max(0,box.top-rr.top)+'px 0 '+Math.max(0,rr.bottom-box.bottom)+'px 0 round 14px)';
  }
  function nativeBindSubPill(){
    var list=$('#subsBody1 .s1list'),rows=list&&$('.rows',list);if(!rows)return;
    if(nativeSubPillObserver)nativeSubPillObserver.disconnect();
    nativeSubPillObserver=new ResizeObserver(nativeRefreshSubPill);nativeSubPillObserver.observe(rows);
    rows.onscroll=nativeRefreshSubPill;
  }
  function syncNativeSubscriptions(){
    var rows=$('#subsBody1 .s1list .rows'),existing=new Map();
    $$('.srow',rows).forEach(function(row){existing.set(row.dataset.nativeId,row);});
    SUBS.forEach(function(sub,i){var t=document.createElement('template');t.innerHTML=subRow(sub,i);var fresh=t.content.firstElementChild;fresh.dataset.nativeId=sub.id;var row=existing.get(sub.id);if(row){patchNativeElement(row,fresh);existing.delete(sub.id);}else row=fresh;if(rows.children[i]!==row){if(rows.moveBefore&&row.parentNode===rows)rows.moveBefore(row,rows.children[i]||null);else rows.insertBefore(row,rows.children[i]||null);}});
    existing.forEach(function(row){row.remove();});$$('.empty-state',rows).forEach(function(row){row.remove();});
    var auto=$('#planAuto');auto.classList.toggle('on',!!PLAN.auto);auto.setAttribute('aria-checked',String(!!PLAN.auto));
    $('#subsBody1 .ps-t').textContent=PLAN.auto?'自动更新':'已关闭';
    $('#planInterval').classList.toggle('off',!PLAN.auto);$('#planInterval span').textContent=planLabel();
    bindSubs();enhanceSwitches();
    paintSubPill(true);
  }
  function syncNativeSettings(){
    // Keep the actual control, focus, switch thumb and any reveal layer alive.
    $$('#setBody [data-setting]').forEach(function(row){
      var item;Object.keys(SETTINGS).some(function(g){item=SETTINGS[g].items.find(function(it){return it[0]===row.dataset.setting;});return!!item;});
      if(!item)return;
      var ctl=row.lastElementChild;
      if(item[2]==='sw'){ctl.classList.toggle('on',!!item[3]);ctl.setAttribute('aria-checked',String(!!item[3]));}
      else if(item[2]==='btn'){if(ctl.textContent!==String(item[3]))ctl.textContent=item[3];}
      else if(item[2]!=='link'){var text=ctl.querySelector('span')||ctl;if(text.textContent!==String(item[3]))text.textContent=item[3];}
      var description=row.querySelector('.d');if(description&&description.textContent!==item[1])description.textContent=item[1];
    });
    setHintText();
  }
  function syncNativeUsage(s){
    var signature=JSON.stringify(s.days);
    if(nativeLast.days!==signature){
      DAYS.splice.apply(DAYS,[0,DAYS.length].concat(s.days));
      var chart=$('#subsBody1 .uchart'),bars=$$('i',chart),scale=Math.max(DAY_CAP,Math.max.apply(null,DAYS)),peak=DAYS.indexOf(Math.max.apply(null,DAYS));
      if(bars.length!==DAYS.length){chart.innerHTML=dayBars(30);}
      else bars.forEach(function(bar,i){var v=DAYS[i];bar.style.height=Math.max(4,v/scale*100).toFixed(1)+'%';bar.classList.toggle('peak',i===peak);var tag=bar.querySelector('.ptag');if(!tag){tag=document.createElement('span');tag.className='ptag';bar.appendChild(tag);}tag.textContent=v>=10?String(Math.round(v)):v%1===0?v.toFixed(0):v.toFixed(1);});
      nativeBindUsage();if(nativeUsageBar)nativeShowUsage(nativeUsageBar);
      nativeLast.days=signature;
    }
    var title=$('#subsBody1 .s1usage .ptitle .right');
    title.firstChild.textContent='本周 '+s.usage.week+' · 本月 '+s.usage.month+' · 累计 '+s.usage.total+' ';
  }
  function applyNativeSnapshot(s){
    if(!NATIVE)return;nativeApplying=true;nativeSnapshot=s;
    try{
      var previousConnected=state.conn;
      if(nativeLast.actualSelected!==undefined&&nativeLast.actualSelected!==s.selected&&s.selected!==nativePendingNode)nativePreviewKey=null;
      nativeLast.actualSelected=s.selected;
      state.conn=!!s.connected;state.mode=s.mode;state.t0=s.connectedAt||0;
      if(nativeLast.nodes!==s.nodes){
      var structure=JSON.stringify(s.nodes.map(function(n){return [n.id,n.name,n.proto,n.region,n.trans,n.src];}));
      var selection=s.nodes.findIndex(function(n){return n.id===s.selected;});
      CUR=selection;nSel=nativePreviewKey?s.nodes.findIndex(function(n){return n.id===nativePreviewKey;}):selection;if(nSel<0){nativePreviewKey=null;nSel=selection;}
      if(nativeLast.structure!==structure){
        NODES.splice.apply(NODES,[0,NODES.length].concat(s.nodes));
        if(nRegion!=='all'&&!NODES.some(function(n){return n.region===nRegion;}))nRegion='all';
        var regionText=$('#nRegionText');if(regionText)regionText.textContent=nRegion==='all'?'全部地区':nRegion;
        renderNodes();requestAnimationFrame(function(){alignLatUnits();});nativeLast.structure=structure;
      }else{
        var changed=false,latencyUpdates=[];
        s.nodes.forEach(function(n,i){var previous=NODES[i];if(previous.real!==n.real||previous.tcp!==n.tcp){changed=true;latencyUpdates.push([i,n.real]);}Object.assign(previous,n);});
        if(changed){if(NODES.some(function(n){return !npass(n)&&lists.D[NODES.indexOf(n)];}))renderNodes();else{reorderNodes();latencyUpdates.forEach(function(it){if(lists.D[it[0]])setLat(lists.D[it[0]].__lat,it[1]);});}}
      }
      nativeLast.nodes=s.nodes;
      }else{var selection=NODES.findIndex(function(n){return n.id===s.selected;});CUR=selection;nSel=nativePreviewKey?NODES.findIndex(function(n){return n.id===nativePreviewKey;}):selection;if(nSel<0){nativePreviewKey=null;nSel=selection;}}
      if(nativeLast.selection!==nSel){paintSel(lists.D,nSel);nativeLast.selection=nSel;}
      var nodeSignature=JSON.stringify([s.selected,currentNode(),nSel,NODES[nSel]]);
      if(nativeLast.node!==nodeSignature){paintCurrent();if(nSel>=0)nodeDetail(nSel);nativeLast.node=nodeSignature;}
      footerNodes();
      $$('.nav[data-page="nodes"] .nmeta em').forEach(function(e){e.textContent=NODES.length;});
      $('#nCnt').textContent=nodeOrder().length+' 个 · '+new Set(NODES.filter(npass).map(function(n){return n.region;})).size+' 个地区';
      nodeTesting.clear();(s.testing.keys||[]).forEach(function(key){var i=NODES.findIndex(function(n){return n.id===key;});if(i>=0)nodeTesting.add(i);});
      NODES.forEach(function(n,i){var card=lists.D[i];if(!card)return;card.classList.toggle('testing',nodeTesting.has(i));var txt=card.__lat.querySelector('.txt');if(nodeTesting.has(i)){card.__lat.replaceChildren();var label=document.createElement('span');label.className='txt';label.textContent='测速中';card.__lat.appendChild(label);}else if(txt&&txt.textContent==='测速中')setLat(card.__lat,n.real);});
      nativePaintTestControls();
      syncNodeSheet();
      if(nativeExitPaint)nativeExitPaint();
      var subSignature=JSON.stringify([s.subs,s.plan]);
      if(nativeLast.subs!==subSignature){
        SUBS.splice.apply(SUBS,[0,SUBS.length].concat(s.subs));DAYS.splice.apply(DAYS,[0,DAYS.length].concat(s.days));PLAN.auto=s.plan.auto;PLAN.hours=s.plan.hours;
        var initialSubs=nativeLast.subs===undefined;
        if(initialSubs){renderSubsPages();$$('#subsBody1 .srow').forEach(function(row,i){row.dataset.nativeId=SUBS[i].id;});nativeBindSubPill();}else syncNativeSubscriptions();
        nativeLast.subs=subSignature;
        $('#subsBody1 .s1usage .ptitle .right').firstChild.textContent='本周 '+s.usage.week+' · 本月 '+s.usage.month+' · 累计 '+s.usage.total+' ';
        var times=$$('#subsBody1 .planstrip .ps-i');if(times[1])times[1].querySelector('span').textContent=s.plan.last||'从未更新';if(times[2])times[2].querySelector('span').textContent=s.plan.next||'—';
        growBars('#subsBody1');
      }
      syncNativeUsage(s);
      syncSubscriptionUi();
      var settingSignature=JSON.stringify([s.settings,s.kernels]);
      if(nativeLast.settings!==settingSignature){
        Object.keys(SETTINGS).forEach(function(g){SETTINGS[g].items.forEach(function(it){if(Object.prototype.hasOwnProperty.call(s.settings,it[0]))it[3]=s.settings[it[0]];});});
        document.body.classList.toggle('compact-nodes',!!s.settings['紧凑布局']);
        (s.kernels||[]).forEach(function(k){var it=SETTINGS.kernel.items.find(function(i){return i[0]===k.name;});if(it){it[1]=k.installed?'本机内核 · '+(k.version||'版本未识别'):'未安装';it[3]=k.installed?'检查更新':'下载';}});
        syncNativeSettings();enhanceSwitches();nativeLast.settings=settingSignature;
      }
      var logsSignature=JSON.stringify(s.logs);
      if(nativeLast.logs!==logsSignature){
        LOGS.splice.apply(LOGS,[0,LOGS.length].concat(s.logs));
        var logTop=$('#logList').scrollTop;renderLogs();if($('[data-sw="autoscroll"]').classList.contains('on'))$('#logList').scrollTop=$('#logList').scrollHeight;else $('#logList').scrollTop=logTop;
        $('.logstream').replaceChildren();s.logs.slice(-7).forEach(function(row){var e=document.createElement('div');e.className='l';e.innerHTML='<span class="t">'+esc(row[0])+'</span><span class="lv '+esc(row[1])+'">'+esc({info:'信息',warn:'警告',error:'错误',debug:'调试',trace:'跟踪'}[row[1]]||row[1])+'</span>'+esc(row[2]);$('.logstream').appendChild(e);});
        nativeLast.logs=logsSignature;
      }
      var sublogsSignature=JSON.stringify(s.sublogs);
      if(nativeLast.sublogs!==sublogsSignature){SUBLOG.splice.apply(SUBLOG,[0,SUBLOG.length].concat(s.sublogs));renderSublogs();syncSubscriptionUi();nativeLast.sublogs=sublogsSignature;}
      // Diagnostic states and routes come from this committed kernel session.
      var diagnosticsSignature=JSON.stringify(s.diagnostics);
      if(nativeLast.diag!==diagnosticsSignature){
        DIAG.splice.apply(DIAG,[0,DIAG.length].concat(s.diagnostics.results));diagBusy=s.diagnostics.running;diagTime=s.diagnostics.when||'';
        renderDiag();$('#healthPct').textContent=$('#diagScore').textContent=s.diagnostics.percentage;
        $$('#healthSites .hx>b').forEach(function(e,i){e.textContent=DIAG[i].label;});
        $('#healthCount').textContent=s.diagnostics.summary;$('#diagWhen').textContent=s.diagnostics.summary+(diagTime?' · '+diagTime:'');
        $$('#diagList .ditem').forEach(function(e,i){var d=DIAG[i];e.querySelector('.diag-result').innerHTML=esc(d.label)+'<small>'+esc(d.response||'—')+'</small>';e.title=d.detail||'';});
        nativeLast.diag=diagnosticsSignature;
      }
      var label=s.connecting?'连接中':s.connected?'已连接':'未连接';
      $$('.cState').forEach(function(e){e.textContent=label;e.style.color=s.connected?'var(--NeoGreen)':'';});
      $$('.cTime').forEach(function(e){e.textContent=clock(s.connectedAt?Math.max(0,Math.floor((Date.now()-s.connectedAt)/1000)):0);});
      $$('.ringButton').forEach(function(e){e.disabled=false;e.setAttribute('aria-pressed',String(s.connected));e.dataset.connecting=String(!!s.connecting);e.setAttribute('aria-label',s.connecting?'取消连接':s.connected?'断开连接':'连接');});
      $$('.nav[data-page="conn"] .nmeta em').forEach(function(e){e.textContent=label;});
      $$('.nav[data-page="conn"] .ndot').forEach(function(e){e.style.background=s.connected?'var(--NeoGreen)':'';});
      $$('.connect,#dConnect').forEach(function(e){e.textContent=s.connecting?'取消连接':s.connected?'断开':'连接';e.disabled=false;});
      setRings(s.connected);
      $$('.cExit').forEach(function(e){e.textContent='出口地区 '+s.exit.region;});
      $$('.cExitShort').forEach(function(e){e.textContent=s.exit.region;});
      $('#diagExit').textContent=s.exit.confirmed?'当前出口 · '+s.exit.region+' · '+s.exit.ip:s.exit.detail;
      if(nSel>=0){var exit=$('#nodeDrawer [data-k="exit"] .v');if(exit)rollText(exit,nSel===CUR?(s.exit.confirmed?s.exit.region+' · '+s.exit.ip:s.exit.detail):'连接后检测');}
      $$('.proxyState').forEach(function(e){e.textContent=s.proxyTaken?'已接管':'未接管';});
      $$('.proxyBadge').forEach(function(e){e.textContent=s.proxyTaken?'已启用':'未启用';e.classList.toggle('g',s.proxyTaken);});
      $('#connCaption').textContent=s.connecting?'正在建立连接':s.connected?'已固定当前节点':'准备就绪';
      $$('.modeSeg button').forEach(function(e){e.classList.toggle('on',e.dataset.m===s.mode);});
      $$('.modeDesc').forEach(function(e){e.textContent={rule:'国内域名与 IP 直连，其余走节点',global:'所有流量经节点',direct:'全部直连，不使用节点'}[s.mode];});
      $$('.localPorts').forEach(function(e){e.textContent=s.ports.socks+' / '+s.ports.http;});
      $$('.proxyAddr').forEach(function(e){e.textContent='127.0.0.1:'+s.ports.http;});
      $$('.sw[data-sw="float"],.sw[data-sw="tun"]').forEach(function(e){var on=e.dataset.sw==='float'?s.floating:s.tun;e.classList.toggle('on',on);e.setAttribute('aria-checked',String(on));});
      $('.h1stats>div:last-child>b').innerHTML=esc(s.usage.todayValue)+'<em>'+esc(s.usage.todayUnit)+'</em>';
      $('#uToday').textContent=s.usage.todayValue;$('.u2>span').textContent=s.usage.todayUnit;$('.u3>span:last-child').textContent=s.usage.total;
      $('#coreActive').textContent=s.core.active;$('#coreParticipants').textContent=s.core.participants+' / '+NODES.length;
      $('.corebar>b').textContent='内核 '+(s.core.name||'—')+' '+(s.core.version||'');
      var coreVersion=$('#coreVersion');if(coreVersion)coreVersion.textContent=s.core.version||'—';
      $('.corebar>span').textContent='内存 '+(s.core.memory==null?'—':s.core.memory.toFixed(1)+' MB');
      nativePaintNodeAction();
      window.neoTraffic.push(s);
      [['.dnV',s.stats.downBps],['.upV',s.stats.upBps]].forEach(function(it){var v=it[1],unit=v>=1048576?'MB/s':v>=1024?'KB/s':'B/s',div=v>=1048576?1048576:v>=1024?1024:1;$(it[0]);$$(it[0]).forEach(function(e){e.innerHTML=(v/div).toFixed(v>=1024?1:0)+'<small>'+unit+'</small>';});});
      $$('.flowPeak').forEach(function(e){var v=Math.max(0,Number(s.stats.peakBps)||0),unit=v>=1048576?'MB/s':v>=1024?'KB/s':'B/s',div=v>=1048576?1048576:v>=1024?1024:1;e.textContent=v?'峰值 '+(v/div).toFixed(v>=1024?1:0)+' '+unit:'峰值 —';});
      document.body.classList.add('native-ready');
      nativeSignalViewReady();
    }finally{nativeApplying=false;}
  }
