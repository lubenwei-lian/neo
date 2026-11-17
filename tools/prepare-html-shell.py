from pathlib import Path
import shutil
import hashlib,re,argparse
parser=argparse.ArgumentParser(description='Bind a complete, versioned Neo HTML source to native services.')
parser.add_argument('--source',type=Path,required=True,help='Directory containing the complete HTML prototype to bind; not needed for a normal build.')
args=parser.parse_args()
R=args.source.resolve()
D=Path(__file__).resolve().parents[1]/'src/Neo.App/Assets/HtmlShell'
s=(R/'js/app2.js').read_text(encoding='utf-8')
s=s.replace('https://api.github.com/zen','https://github.com/')
# Callers add final list classes and rows before this microtask measures the reveal.
# Measuring the empty shell makes its circle too small and exposes the rest abruptly at the end.
dialog_start=s.index('  function showUiDialog(')
dialog_end=s.index('  function saveText(',dialog_start)
dialog=s[dialog_start:dialog_end]
dialog=dialog.replace("if(!matchMedia('(prefers-reduced-motion:reduce)').matches){", "if(!matchMedia('(prefers-reduced-motion:reduce)').matches)queueMicrotask(function(){\n      if(!d.isConnected||!d.open)return;",1)
dialog=dialog.replace('    }\n    return d;', '    });\n    return d;',1)
s=s[:dialog_start]+dialog+s[dialog_end:]
entry=R/'index.html' if (R/'index.html').exists() else R/'neo 重构稿（主）.html'
# Copy the complete reference cascade, including late typography overrides.
# Native adaptations are confined to native.css and the explicit bridge hooks.
for folder in ['css','lib','assets']:
 for f in (R/folder).rglob('*'):
  if f.is_file() and not f.name.startswith('native'):
   dest=D/f.relative_to(R);dest.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(f,dest)
def replace(a,b,count=-1):
 global s
 if a not in s:raise ValueError('Missing source anchor: '+a[:110])
 s=s.replace(a,b,count)
replace("  'use strict';","  'use strict';\n  var NATIVE=!!window.__NEO_DESKTOP,nativeApplying=false;\n  function host(action,payload){return window.neoNative.request(action,payload||{});}\n  function currentNode(){return NODES[CUR]||{id:null,f:'',name:'未选择节点',region:'—',proto:'—',trans:'',real:0,tcp:0,server:'',port:'',src:''};}")
for n in ('NODES','SUBS','LOGS','SUBLOG'):replace('var '+n+' = [','var '+n+' = NATIVE?[]:[',1)
replace('var DAYS = (function (){','var DAYS = NATIVE?Array(30).fill(0):(function (){',1)
replace('var h = Math.max(4, v / DAY_CAP * 100);','var h = Math.max(4, v / (NATIVE?Math.max(DAY_CAP,Math.max.apply(null,DAYS)):DAY_CAP) * 100);',1)
replace('NODES[CUR]', 'currentNode()')
# Restore the one lookup inside the fallback accessor itself.
replace('function currentNode(){return currentNode()||','function currentNode(){return NODES[CUR]||',1)
replace('function toggleConn(){','function toggleConn(){\n    if(NATIVE){if(nodeSheetIsVisible()){closeNodeSheet();return;}return host("connection.toggle");}',1)
replace('function nSelect(i){','function nSelect(i){\n    if(!NODES[i])return;\n    if(NATIVE&&!nativeApplying)return nativeSelectNode(i);',1)
replace('function nTest(i){','function nTest(i){\n    if(NATIVE)return host("nodes.test",{keys:NODES[i]?[NODES[i].id]:[],tcp:false});',1)
replace('async function testNodeBatch(indices){','async function testNodeBatch(indices){\n    if(NATIVE)return host("nodes.test",{keys:indices.map(function(i){return NODES[i].id;}),tcp:false});',1)
replace('function nodeDetail(i){','function nodeDetail(i){\n    if(!NODES[i]){var empty=$("#nodeDrawer");empty.innerHTML="";empty.__built=false;return;}',1)
replace('function updateSubscription(i){','function updateSubscription(i){\n    if(NATIVE)return host("subscription.update",{id:SUBS[i].id});',1)
replace('function runDiagnostics(){','function runDiagnostics(){\n    if(NATIVE)return host("diagnostics.run");',1)
replace("function routeOf(d){return ","function routeOf(d){if(NATIVE)return d.route||'未确认';return ",1)
replace('start: function (){','start: function (){\n        if(NATIVE)return;',1)
replace('function drawAll(){','function drawAll(){\n      if(NATIVE){window.neoTraffic.draw();return;}',1)
replace('nodeOrder().forEach(function(i){if(lists.D[i])host.appendChild(lists.D[i]);});', 'nodeOrder().forEach(function(i,k){var node=lists.D[i],before=host.children[k]||null;if(node&&node!==before){if(host.moveBefore&&node.parentNode===host)host.moveBefore(node,before);else host.insertBefore(node,before);}});',1)
replace("if(page==='subs')growBars('#subsBody1');", "if(page==='subs')growBars('#subsBody1');if(NATIVE&&page==='conn')tick.draw();",1)
replace('function go(page){',"function go(page){\n    if(NATIVE&&page==='sublog')page='subs';",1)
replace('function renderSublogs(){', 'function renderSublogs(){\n    if(NATIVE){nativeSyncSublogs();return;}',1)
replace('    paintSel(lists.D,nSel);nodeDetail(nSel);footerNodes();','    paintSel(lists.D,nSel);nodeDetail(nSel);footerNodes();if(NATIVE)nativePaintExitMarks();',1)
# Dialog and menu motion comes directly from this reference revision.
replace('var i = +b.dataset.node;','var i = +b.dataset.node;\n        if(NATIVE){host("node.select",{key:NODES[i].id}).then(function(){closeMenu();closeNodeSheet();});return;}',2)
replace("function esc(v){var e=document.createElement('span');e.textContent=String(v);return e.innerHTML;}","function esc(v){var e=document.createElement('span');e.textContent=String(v==null?'':v);return e.innerHTML.replace(/\"/g,'&quot;').replace(/'/g,'&#39;');}")
replace("function saveText(name,value,type){","function saveText(name,value,type){if(NATIVE)return host('file.saveText',{name:name.replace('-界面预览',''),text:String(value),type:type||'text/plain'});",1)
replace("function copyText(value){","function copyText(value){if(NATIVE)return host('clipboard.copy',{text:String(value)});",1)
# Remote subscription names and kernel logs are text, never executable markup.
for raw,safe in [('+ n.name +','+ esc(n.name) +'),('+n.name.split(\'·\')[0].trim()+',"+esc(n.name.split('·')[0].trim())+"),('+n.name+','+esc(n.name)+'),('+ r[2] +','+ esc(r[2]) +'),('+r[1]+','+esc(r[1])+'),('+ r[1] +','+ esc(r[1]) +'),('+r[3]+','+esc(r[3])+')]:
 s=s.replace(raw,safe)
replace('var n = NODES[i], d = $(\'#nodeDrawer\');','var n = NODES[i], d = $(\'#nodeDrawer\');',1)
replace("s.name.indexOf(currentNode().src)===0","(NATIVE?s.name===currentNode().src:s.name.indexOf(currentNode().src)===0)")
replace("new Date(2026,10,15)","new Date()")
replace("'<div class=\"lgbot\"><span>' + r[0]", "'<div class=\"lgbot\"><span>' + esc(r[0])")
s=s.replace("+ r[3] +", "+ esc(r[3]) +").replace("+ r[4] +", "+ esc(r[4]) +")
replace('setLat(d.__latReal, n.real);','setLat(d.__latReal, NATIVE?n.rawReal:n.real);')
replace("$('#dConnect',d).addEventListener('click',toggleConn);", "$('#dConnect',d).addEventListener('click',function(){if(NATIVE)nativeConnectInspected();else toggleConn();});",1)
replace("    rollText(d.querySelector('[data-k=\"exit\"] .v'), state.conn&&i===CUR ? n.region+' · 出口预览' : '连接后检测');", "    if(!NATIVE)rollText(d.querySelector('[data-k=\"exit\"] .v'),state.conn&&i===CUR?n.region+' · 出口预览':'连接后检测');",1)
replace("        PLAN.hours = v === 'manual' ? null : +v;", "        if(NATIVE){nativeSaveInterval(v);return;}\n        PLAN.hours = v === 'manual' ? null : +v;",1)
replace("      PLAN.hours = v;", "      if(NATIVE){nativeSaveInterval(v);closeMenu();return;}\n      PLAN.hours = v;",1)
replace("    var inp = $('#ivH', m);", "    placeMenu(m,r,wrap);\n    var inp = $('#ivH', m);",1)
replace("      var det = row.querySelector('.lgdetail');", "      if(NATIVE){nativeBindLogRow(row);return;}\n      var det = row.querySelector('.lgdetail');",1)
replace("    bindSubs();\n    enhanceSwitches();", "    bindSubs();\n    if(NATIVE)nativeBindUsage();\n    enhanceSwitches();",1)
replace("    if(page==='subs')growBars('#subsBody1');", "    if(page==='subs')requestAnimationFrame(function(){paintSubPill(false);});\n    if(page==='subs')growBars('#subsBody1');",1)
replace("(ok ? 'HTTP 200' : 'HTTP 403 Forbidden')", "(NATIVE?esc(r[5]||'未返回 HTTP 状态'):(ok ? 'HTTP 200' : 'HTTP 403 Forbidden'))",1)
replace("(i % 2 ? 'subscription.example.invalid' : 'subscription.example.invalid')", "(NATIVE?esc(r[6]||'—'):(i % 2 ? 'subscription.example.invalid' : 'subscription.example.invalid'))",1)
replace("' · 新增 0 · 删除 0</span>", "(NATIVE?'':' · 新增 0 · 删除 0') + '</span>",1)
replace("          '<button class=\"btn ghost\" style=\"margin-top:8px\">打开完整日志</button>' +", "          '<button class=\"btn ghost\" id=\"nativeFullSublog\" style=\"margin-top:8px\">打开完整日志</button>' +",1)
# Newer reference revisions already removed this retired entry point.
s=s.replace("    $('.s1log>.btn',body).onclick=function(){go('sublog');};", "    if($('.s1log>.btn',body))$('.s1log>.btn',body).onclick=function(){go('sublog');};",1)
# Both node surfaces use the same ordering; the connection picker shows all
# nodes even when a search/region filter remains active on the nodes page.
replace('function nodeOrder(){return NODES.map(function(n,i){return i;}).filter(function(i){return npass(NODES[i]);})',
        'function nodeOrder(includeAll){return NODES.map(function(n,i){return i;}).filter(function(i){return includeAll||npass(NODES[i]);})',1)
sheet_start=s.index('  function buildNodeSheet(){')
sheet_end=s.index('  function openNodeSheet(){',sheet_start)
s=s[:sheet_start]+(D/'js/native-node-sheet.js').read_text(encoding='utf-8')+'\n'+s[sheet_end:]
native=(D/'js/native-view.js').read_text(encoding='utf-8')
anchor='  window.__app = { go:go,nSelect:nSelect,nodes:NODES,state:state,openDiag:openDiag,previewDiagnostics:DIAG,renderDiag:renderDiag,settings:SETTINGS };'
replace(anchor,native+'\n'+'''  window.__app = {go:go,nSelect:nSelect,nodes:NODES,subscriptions:SUBS,state:state,openDiag:openDiag,
    previewDiagnostics:DIAG,renderDiag:renderDiag,settings:SETTINGS,setOptions:SET_OPTS,
    renderSettings:renderSettings,renderSetBody:renderSetBody,renderSubs:renderSubsPages,
    openPicker:openPicker,openIntervalMenu:openIntervalMenu,showDialog:showUiDialog,toast:toast,switchSetGroup:switchSetGroup,
    paintSubPill:paintSubPill,openExitRegionTest:nativeOpenExitTest,openFullSublog:nativeOpenFullSublog,
    applySnapshot:applyNativeSnapshot,currentNode:currentNode,editPort:editPort};
  if(NATIVE)window.neoNative.attach(window.__app);''')
(D/'js/app2.js').write_text(s,encoding='utf-8')
html=entry.read_text(encoding='utf-8')
exit_button='<button class="btn primary" id="nTestAll" title="出口 IP 与地区归属比对">出口地区测试</button>'
if exit_button not in html:raise ValueError('Missing node testing toolbar anchor')
html=html.replace(exit_button,'<button class="btn ghost" id="nExitRegions" title="查看已测出口地区，按需检测未测节点">出口地区</button>',1)
test_group='<div class="testgroup" id="nTestGroup">'
if test_group not in html:raise ValueError('Missing node test group anchor')
html=html.replace(test_group,test_group+'<button class="btn primary" id="nTestAll" title="测试全部节点延迟，结果逐个排序">全部测速</button>',1)
html=re.sub(r'\s*<button class="nav" data-page="sublog">.*?</button>','',html,count=1)
html=re.sub(r'<title>.*?</title>','<title>neo</title>',html).replace('</head>','<style>body:not(.native-ready){visibility:hidden}</style>\n<script src="js/native-host.js"></script>\n</head>')
html=html.replace('<svg class="sparkSvg" preserveAspectRatio="none"></svg>','<canvas class="sparkCanvas" role="img" aria-label="实时下载与上传流量曲线"></canvas>')
html=html.replace('</head>','<link rel="stylesheet" href="css/native.css">\n<script src="js/native-traffic.js"></script>\n</head>')
for asset in ['js/app2.js','js/native-host.js','js/native-traffic.js','js/native-motion.js','css/native.css']+[str(f.relative_to(D)).replace('\\','/') for f in (D/'css').glob('*.css') if f.name!='native.css']:
 digest=hashlib.sha256((D/asset).read_bytes()).hexdigest()[:12]
 html=re.sub(r'((?:src|href)=")'+re.escape(asset)+r'(?:\?[^"]*)?(\")',lambda m:m[1]+asset+'?v='+digest+m[2],html)
(D/'index.html').write_text(html,encoding='utf-8')
print('Prepared native HTML view from the original source.')
