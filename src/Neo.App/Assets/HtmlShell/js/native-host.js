(function(){
  'use strict';
  window.__NEO_DESKTOP=true;
  var bridge=window.chrome&&window.chrome.webview,pending=new Map(),nextId=0,app=null,lastSnapshot=null;
  function snapshotReady(){return lastSnapshot&&lastSnapshot.nodes&&lastSnapshot.subs&&lastSnapshot.plan&&lastSnapshot.days&&lastSnapshot.settings&&lastSnapshot.stats&&lastSnapshot.usage&&lastSnapshot.logs&&lastSnapshot.sublogs&&lastSnapshot.diagnostics;}
  function request(action,payload,onProgress){
    if(!bridge)return Promise.reject(new Error('本机服务未连接'));
    return new Promise(function(resolve,reject){
      var id=String(++nextId),timer=setTimeout(function(){pending.delete(id);var error=new Error('操作超时，请检查状态后重试');if(app)app.toast(error.message);reject(error);},action==='settings.install'?1800000:action==='settings.action'?900000:120000);
      pending.set(id,{resolve:resolve,reject:reject,timer:timer,progress:onProgress,token:payload&&payload.token});bridge.postMessage({id:id,action:action,payload:payload||{}});
    });
  }
  if(bridge)bridge.addEventListener('message',function(e){
    if(e.data.type==='traffic.frame'){if(window.neoTraffic)window.neoTraffic.tick();return;}
    if(e.data.type==='update.progress'){pending.forEach(function(p){if(p.token===e.data.token&&p.progress)p.progress(e.data.text);});return;}
    var m=e.data;if(m.type==='snapshot'){lastSnapshot=Object.assign(lastSnapshot||{},m.data);if(app&&snapshotReady())app.applySnapshot(lastSnapshot);return;}
    if(m.type==='toast'){if(app)app.toast(m.text);return;}
    if(m.type==='navigate'){if(app)app.go(m.page);return;}
    var p=pending.get(m.id);if(!p)return;clearTimeout(p.timer);pending.delete(m.id);
    if(m.ok)p.resolve(m.data);else{var err=new Error(m.error||'操作失败');if(app)app.toast(err.message);p.reject(err);}
  });
  function stop(e){e.preventDefault();e.stopImmediatePropagation();}
  function nodeKeys(){return app.nodes.map(function(n){return n.id;});}
  function saveSetting(title,value){return request('settings.set',{title:title,value:value});}
  function addSubscription(index){
    var s=typeof index==='number'?app.subscriptions[index]:null;
    var d=app.showDialog(s?'编辑订阅':'添加订阅','<label class="field">名称<input id="nativeSubName" maxlength="80"></label><label class="field">订阅地址或本地文件<input id="nativeSubUrl" autocomplete="off" spellcheck="false"></label><p class="form-error" id="nativeSubError"></p><div class="dialog-actions">'+(s?'':'<button class="btn ghost" id="nativeSubFile">从文件导入</button>')+'<button class="btn primary" id="nativeSubSave">'+(s?'保存':'添加')+'</button></div>');
    d.querySelector('#nativeSubName').value=s?s.name:'';d.querySelector('#nativeSubUrl').value=s?s.fullUrl:'';
    if(!s)d.querySelector('#nativeSubFile').onclick=async function(){this.disabled=true;try{var r=await request('nodes.importFile');if(r&&!r.cancelled){d.close();importNotice(r);}}catch(err){d.querySelector('#nativeSubError').textContent=err.message;}finally{this.disabled=false;}};
    d.querySelector('#nativeSubSave').onclick=async function(){
      var b=this;b.disabled=true;
      try{await request('subscription.save',{id:s?s.id:null,name:d.querySelector('#nativeSubName').value.trim(),url:d.querySelector('#nativeSubUrl').value.trim()});d.close();}
      catch(err){d.querySelector('#nativeSubError').textContent=err.message;}finally{b.disabled=false;}
    };
  }
  function importNotice(r){if(r&&!r.cancelled)app.toast('已导入 '+r.added+' 个节点'+(r.enabled?'':'，可在订阅页启用'));}
  function inputDialog(title,label,value,multiline,save){
    var d=app.showDialog(title,'<label class="field">'+label+(multiline?'<textarea class="ui-textarea" id="nativeValue"></textarea>':'<input id="nativeValue">')+'</label><p class="form-error" id="nativeError"></p><div class="dialog-actions"><button class="btn primary" id="nativeSave">保存</button></div>');
    d.querySelector('#nativeValue').value=value||'';
    d.querySelector('#nativeSave').onclick=async function(){this.disabled=true;try{await save(d.querySelector('#nativeValue').value);d.close();}catch(err){d.querySelector('#nativeError').textContent=err.message;}finally{this.disabled=false;}};
  }
  function confirm(title,copy,action){var d=app.showDialog(title,'<p class="dialog-copy"></p><div class="dialog-actions"><button class="btn primary" id="nativeConfirm">确认</button></div>');d.querySelector('p').textContent=copy;d.querySelector('#nativeConfirm').onclick=async function(){this.disabled=true;try{await action();d.close();}catch(_){}finally{this.disabled=false;}};}
  var updateChecks=new Map();
  function isUpdateAction(title){return ['软件更新','xray','sing-box','mihomo','naive'].indexOf(title)>=0;}
  function paintUpdateCheck(title,ctl){
    var controls=updateChecks.get(title);if(!controls||!ctl||controls.has(ctl))return;
    controls.set(ctl,{html:ctl.innerHTML,disabled:ctl.disabled,label:ctl.getAttribute('aria-label')});
    var width=getComputedStyle(ctl).width;if(parseFloat(width)>0)ctl.style.setProperty('--neo-check-width',width);
    ctl.classList.add('is-checking');ctl.disabled=true;ctl.setAttribute('aria-busy','true');ctl.setAttribute('aria-label',title+' 正在检查更新');
    var label=ctl.textContent==='下载'?'查询':'检查中';
    ctl.innerHTML='<span class="update-check-spinner" aria-hidden="true"></span><span>'+label+'</span>';
  }
  function syncUpdateChecks(){
    document.querySelectorAll('#setBody [data-setting]').forEach(function(row){paintUpdateCheck(row.dataset.setting,row.querySelector('button'));});
  }
  function finishUpdateCheck(title){
    var controls=updateChecks.get(title);if(!controls)return;updateChecks.delete(title);
    var item;Object.keys(app.settings).some(function(g){item=app.settings[g].items.find(function(i){return i[0]===title;});return!!item;});
    controls.forEach(function(before,ctl){
      ctl.classList.remove('is-checking');ctl.style.removeProperty('--neo-check-width');ctl.disabled=before.disabled;ctl.removeAttribute('aria-busy');
      if(before.label===null)ctl.removeAttribute('aria-label');else ctl.setAttribute('aria-label',before.label);
      if(item){ctl.textContent=item[3];ctl.setAttribute('aria-label',title+' '+item[3]);}else ctl.innerHTML=before.html;
    });
  }
  async function runAction(title,ctl){
    var checking=isUpdateAction(title);if(checking&&updateChecks.has(title))return;
    if(checking){updateChecks.set(title,new Map());paintUpdateCheck(title,ctl);syncUpdateChecks();}
    var result;
    try{result=await request('settings.action',{title:title});}
    finally{if(checking)finishUpdateCheck(title);}
    if(!result||!result.message)return;
    var d=app.showDialog(result.title||title,'<pre class="dialog-copy" style="white-space:pre-wrap;overflow-wrap:anywhere;max-height:50vh;overflow:auto"></pre>'+(result.installToken?'<div class="dialog-actions"><button class="btn primary" id="nativeInstall">安装更新</button></div>':''));
    d.querySelector('pre').textContent=result.message;
    if(result.installToken)d.querySelector('#nativeInstall').onclick=async function(){this.disabled=true;var copy=d.querySelector('pre');try{var done=await request('settings.install',{token:result.installToken},function(text){copy.textContent=text;});copy.textContent=done.message;}catch(e){copy.textContent=e.message;}finally{this.remove();}};
  }
  function setting(title,ctl){
    var item;Object.keys(app.settings).some(function(g){item=app.settings[g].items.find(function(i){return i[0]===title;});return!!item;});if(!item)return;
    if(item[2]==='sw'){saveSetting(title,!item[3]).catch(function(){});return;}
    if(title==='网络检测详情'){app.openDiag(true,ctl);return;}
    if(title==='SOCKS5 端口'||title==='HTTP 端口'){app.editPort(item);return;}
    if(title==='应用端口'){var ports={};app.settings.network.items.forEach(function(i){if(i[0]==='SOCKS5 端口')ports.socks=Number(i[3]);if(i[0]==='HTTP 端口')ports.http=Number(i[3]);});request('ports.apply',ports).catch(function(){});return;}
    if(title==='DNS 服务器'){inputDialog(title,'DNS 服务器',item[3],false,function(v){return saveSetting(title,v);});return;}
    if(title==='自定义游戏进程'){inputDialog(title,'每行一个进程名',(lastSnapshot.gameProcesses||[]).join('\n'),true,function(v){return saveSetting(title,v);});return;}
    if(app.setOptions[title]){app.openPicker(ctl,title,app.setOptions[title].map(function(v){return{v:v,label:v};}),item[3],function(v){saveSetting(title,v).catch(function(){});});return;}
    if(title==='协议指定内核'){
      var protos=['VLESS','VMess','Trojan','Shadowsocks','Hysteria2','TUIC','WireGuard','Naive'];
      var d=app.showDialog(title,protos.map(function(p){return'<label class="map-row"><span>'+p+'</span><select class="ui-select" data-native-proto="'+p+'">'+['自动','xray','sing-box','mihomo','naive'].map(function(k){return'<option>'+k+'</option>';}).join('')+'</select></label>';}).join('')+'<div class="dialog-actions"><button class="btn primary" id="nativeMapSave">保存</button></div>');
      d.querySelectorAll('[data-native-proto]').forEach(function(e){e.value=lastSnapshot.coreMap[e.dataset.nativeProto]||'自动';});
      d.querySelector('#nativeMapSave').onclick=async function(){var map={};d.querySelectorAll('[data-native-proto]').forEach(function(e){map[e.dataset.nativeProto]=e.value;});await request('settings.coreMap',{map:map});d.close();};return;
    }
    if(title==='恢复默认'||title==='导入配置'){confirm(title,title==='导入配置'?'导入配置后将按文件内容恢复设置并重启。':'恢复默认界面与网络设置，保留订阅与手选节点。',function(){return runAction(title);});return;}
    if(item[2]==='btn'||item[2]==='link')runAction(title,ctl).catch(function(){});
  }
  function click(e){
    var t=e.target,button=t.closest('button,[role="switch"],.addrow');if(!button)return;
    var settingRow=button.closest('[data-setting]');if(settingRow){stop(e);setting(settingRow.dataset.setting,button);return;}
    if(button.matches('.winbtn')){stop(e);var all=Array.from(document.querySelectorAll('.winbtn'));request('window.'+(['minimize','maximize','close'][all.indexOf(button)])).catch(function(){});return;}
    if(button.matches('.modeSeg button')){stop(e);request('connection.mode',{mode:button.dataset.m}).catch(function(){});return;}
    if(button.matches('.sw[data-sw="float"],.sw[data-sw="tun"]')){stop(e);saveSetting(button.dataset.sw==='float'?'悬浮球':'TUN 模式',!button.classList.contains('on')).catch(function(){});return;}
    if(button.matches('[data-subsw]')){stop(e);var sub=app.subscriptions[+button.dataset.subsw];request('subscription.enabled',{id:sub.id,enabled:!sub.enabled}).catch(function(){});return;}
    if(button.matches('[data-submore]')){stop(e);var i=+button.dataset.submore,s=app.subscriptions[i];app.openPicker(button,'订阅管理',[{v:'primary',label:s.primary?'取消主用':'设为主用'},{v:'edit',label:'编辑订阅'},{v:'copy',label:'复制订阅地址'},{v:'delete',label:'删除订阅'}],'',function(v){if(v==='primary')request('subscription.primary',{id:s.primary?'':s.id}).catch(function(){});else if(v==='edit')addSubscription(i);else if(v==='copy')request('clipboard.copy',{text:s.fullUrl}).catch(function(){});else confirm('删除订阅','确认删除「'+s.name+'」？',function(){return request('subscription.delete',{id:s.id});});});return;}
    if(button.matches('.addrow')){stop(e);addSubscription();return;}
    if(button.id==='planAuto'){stop(e);saveSetting('自动更新订阅',!button.classList.contains('on')).catch(function(){});return;}
    if(button.id==='planInterval'){stop(e);if(lastSnapshot.plan.auto)app.openIntervalMenu(button);return;}
    if(button.id==='planAll'){stop(e);request('subscription.updateAll').catch(function(){});return;}
    if(button.id==='nativeFullSublog'){stop(e);app.openFullSublog();return;}
    if(button.id==='nExitRegions'){stop(e);app.openExitRegionTest();return;}
    if(button.id==='nTestAll'){stop(e);var allKeys=nodeKeys();if(!allKeys.length){app.toast('没有可测节点');return;}request('nodes.test',{keys:allKeys,tcp:false}).catch(function(){});return;}
    if(button.id==='nTcpAll'||button.id==='nRetest'){stop(e);var keys=button.id==='nRetest'?app.nodes.filter(function(n){return n.real<=0;}).map(function(n){return n.id;}):nodeKeys();if(!keys.length){app.toast('没有待补测的节点');return;}request('nodes.test',{keys:keys,tcp:button.id==='nTcpAll'}).catch(function(){});return;}
    if(button.id==='nMore'){stop(e);app.openPicker(button,'节点操作',[{v:'import',label:'手动添加节点'},{v:'content',label:'粘贴节点／配置'},{v:'file',label:'从文件导入'},{v:'warp',label:'注册 WARP'}],'',function(v){if(v==='import')inputDialog('手动添加节点','每行一条分享链接','',true,function(links){return request('nodes.import',{links:links});});else if(v==='content')inputDialog('粘贴节点／配置','分享链接、Base64、Clash、SIP008、WireGuard 或完整配置','',true,async function(content){var r=await request('nodes.importContent',{content:content});importNotice(r);});else if(v==='file')request('nodes.importFile').then(importNotice).catch(function(){});else if(v==='warp')confirm('注册 WARP','向 Cloudflare 注册并添加 WARP 节点。',function(){return request('nodes.warp');});});return;}
    if(button.id==='lMore'||button.id==='slMore'){stop(e);var sublog=button.id==='slMore';app.openPicker(button,sublog?'订阅日志':'运行日志',[{v:'dir',label:'打开日志目录'},{v:'copy',label:'复制记录'},{v:'clear',label:'清空记录'}],'',function(v){if(v==='clear')confirm('清空记录','清空当前显示的记录？',function(){return request(sublog?'sublogs.clear':'logs.clear');});else request(v==='dir'?'logs.directory':'clipboard.copy',v==='copy'?{text:document.querySelector(sublog?'#slList':'#logList').innerText}:{}).catch(function(){});});return;}
    if(button.id==='coreDetails'){stop(e);var d=app.showDialog('内核运行','<div class="detail-grid" id="nativeCoreGrid"></div><div class="dialog-actions"><button class="btn ghost" id="nativeCoreSettings">内核设置</button><button class="btn primary" id="nativeCoreLogs">查看日志</button></div>'),c=lastSnapshot.core;
      [['版本',c.name+' '+(c.version||'—')],['内存占用',c.memory==null?'—':c.memory+' MB'],['策略',c.strategy],['参与节点',c.participants+' / '+app.nodes.length],['本次上传',c.upload],['本次下载',c.download],['活跃连接',c.active]].forEach(function(r){var a=document.createElement('span'),b=document.createElement('b');a.textContent=r[0];b.textContent=r[1];d.querySelector('#nativeCoreGrid').append(a,b);});
      d.querySelector('#nativeCoreSettings').onclick=function(){d.close();app.go('settings');app.switchSetGroup('kernel',80);};d.querySelector('#nativeCoreLogs').onclick=function(){d.close();app.go('log');};return;}
  }
  window.neoNative={request:request,snapshot:function(){return lastSnapshot;},syncUpdateChecks:syncUpdateChecks,attach:function(api){
    app=api;
    var titleObserver=new MutationObserver(function(){var p=document.querySelector(".page.on");if(p)request("view.page",{page:p.dataset.page}).catch(function(){});});titleObserver.observe(document.querySelector("#pageTitle"),{childList:true,subtree:true,characterData:true});
    Object.keys(app.settings).forEach(function(k){app.settings[k].items=app.settings[k].items.filter(function(i){return i[0]!=='自动维护';});app.settings[k].items.forEach(function(i){if(i[0]==='日志目录')i[2]='btn';});});
    app.setOptions['日志保留']=['7 天','15 天','30 天'];app.renderSetBody();
    document.addEventListener('click',click,true);
    document.querySelector('.tbar').addEventListener('pointerdown',function(e){if(e.button===0&&!e.target.closest('button,input,select'))request('window.drag').catch(function(){});});
    document.querySelector('.tbar').addEventListener('dblclick',function(e){if(!e.target.closest('button,input,select'))request('window.maximize').catch(function(){});});
    if(snapshotReady())app.applySnapshot(lastSnapshot);request('ready').catch(function(){});
  }};
  window.addEventListener('unhandledrejection',function(e){console.error('Neo UI rejected operation',e.reason);});
})();
