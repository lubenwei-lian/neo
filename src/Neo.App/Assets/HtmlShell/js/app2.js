/* neo 重构稿 · 全页交互（motion 14） */
(function () {
  'use strict';
  var NATIVE=!!window.__NEO_DESKTOP,nativeApplying=false;
  function host(action,payload){return window.neoNative.request(action,payload||{});}
  function currentNode(){return NODES[CUR]||{id:null,f:'',name:'未选择节点',region:'—',proto:'—',trans:'',real:0,tcp:0,server:'',port:'',src:''};}
  var animate = Motion.animate;
  var $ = function (s, r) { return (r || document).querySelector(s); };
  var $$ = function (s, r) { return Array.prototype.slice.call((r || document).querySelectorAll(s)); };

  /* ---------------- 数据 ---------------- */
  var NODES = NATIVE?[]:[
    { f:'hk', name:'香港 01 · IPLC',      region:'香港',  proto:'VLESS',       trans:'TCP + TLS', real:32,  tcp:18, server:'hk01.neo.im', port:443,  src:'机场 A' },
    { f:'jp', name:'日本东京 02 · 0.1 倍', region:'日本',  proto:'VLESS',       trans:'TLS',       real:132, tcp:78, server:'jp02.neo.im', port:443,  src:'机场 A' },
    { f:'sg', name:'新加坡 03 · NAT',     region:'新加坡', proto:'Hysteria2',   trans:'UDP',       real:58,  tcp:0,  server:'sg03.neo.im', port:8443, src:'机场 A' },
    { f:'us', name:'美国洛杉矶 04 · CN2',  region:'美国',  proto:'VLESS',       trans:'gRPC',      real:188, tcp:142,server:'us04.neo.im', port:443,  src:'机场 B' },
    { f:'tw', name:'台湾台北 05',         region:'台湾',  proto:'Shadowsocks', trans:'TCP',       real:54,  tcp:31, server:'tw05.neo.im', port:8388, src:'机场 B' },
    { f:'kr', name:'韩国首尔 08',         region:'韩国',  proto:'Trojan',      trans:'TLS',       real:78,  tcp:46, server:'kr08.neo.im', port:443,  src:'机场 B' },
    { f:'de', name:'德国法兰克福 09',     region:'德国',  proto:'VLESS',       trans:'TCP + TLS', real:212, tcp:168,server:'de09.neo.im', port:443,  src:'机场 C' },
    { f:'jp', name:'日本大阪 07',         region:'日本',  proto:'VMess',       trans:'WS + TLS',  real:52,  tcp:29, server:'jp07.neo.im', port:443,  src:'机场 A' },
    { f:'sg', name:'新加坡 10 · 流媒体',   region:'新加坡', proto:'Hysteria2',   trans:'UDP',       real:66,  tcp:0,  server:'sg10.neo.im', port:8443, src:'机场 C' },
    { f:'us', name:'美国圣何塞 11',       region:'美国',  proto:'VMess',       trans:'WS + TLS',  real:0,   tcp:0,  server:'us11.neo.im', port:443,  src:'机场 C' },
    { f:'hk', name:'香港 06 · IPLC 备用', region:'香港',  proto:'VLESS',       trans:'REALITY',   real:35,  tcp:19, server:'hk06.neo.im', port:443,  src:'机场 A' },
    { f:'tw', name:'台湾新北 12',         region:'台湾',  proto:'Trojan',      trans:'TCP',       real:71,  tcp:40, server:'tw12.neo.im', port:443,  src:'机场 B' },
    { f:'kr', name:'韩国釜山 13',         region:'韩国',  proto:'VLESS',       trans:'TCP + TLS', real:96,  tcp:57, server:'kr13.neo.im', port:443,  src:'机场 C' },
    { f:'jp', name:'日本东京 14 · 家宽',  region:'日本',  proto:'Trojan',      trans:'TLS',       real:44,  tcp:24, server:'jp14.neo.im', port:443,  src:'机场 A' }
  ];
  var FLAGS = {
    hk:'<svg viewBox="0 0 20 14" preserveAspectRatio="none"><rect width="20" height="14" fill="#DE2910"/><g fill="#fff"><circle cx="10" cy="7" r="1.8"/><ellipse cx="10" cy="3.6" rx="1.2" ry="1.7"/><ellipse cx="10" cy="10.4" rx="1.2" ry="1.7"/><ellipse cx="6.6" cy="5.3" rx="1.2" ry="1.7" transform="rotate(-72 6.6 5.3)"/><ellipse cx="13.4" cy="5.3" rx="1.2" ry="1.7" transform="rotate(72 13.4 5.3)"/></g></svg>',
    jp:'<svg viewBox="0 0 20 14" preserveAspectRatio="none"><rect width="20" height="14" fill="#fff"/><circle cx="10" cy="7" r="3.9" fill="#BC002D"/></svg>',
    sg:'<svg viewBox="0 0 20 14" preserveAspectRatio="none"><rect width="20" height="7" fill="#EF3340"/><rect y="7" width="20" height="7" fill="#fff"/><circle cx="5.4" cy="3.4" r="2.3" fill="#fff"/><circle cx="6.3" cy="3.4" r="2" fill="#EF3340"/></svg>',
    us:'<svg viewBox="0 0 20 14" preserveAspectRatio="none"><rect width="20" height="14" fill="#B22234"/><g fill="#fff"><rect y="1.9" width="20" height="1.1"/><rect y="4.1" width="20" height="1.1"/><rect y="6.3" width="20" height="1.1"/><rect y="8.5" width="20" height="1.1"/><rect y="10.7" width="20" height="1.1"/></g><rect width="8.2" height="7" fill="#3C3B6E"/></svg>',
    tw:'<svg viewBox="0 0 20 14" preserveAspectRatio="none"><rect width="20" height="14" fill="#FE0000"/><rect width="10" height="7" fill="#000095"/><circle cx="5" cy="3.5" r="1.8" fill="#fff"/></svg>',
    kr:'<svg viewBox="0 0 20 14" preserveAspectRatio="none"><rect width="20" height="14" fill="#fff"/><circle cx="10" cy="7" r="2.4" fill="#CD2E3A"/><path d="M10 4.6a2.4 2.4 0 0 0 0 4.8 1.2 1.2 0 0 1 0-2.4 1.2 1.2 0 0 0 0-2.4z" fill="#0047A0"/></svg>',
    de:'<svg viewBox="0 0 20 14" preserveAspectRatio="none"><rect width="20" height="4.7" fill="#000"/><rect y="4.7" width="20" height="4.7" fill="#DD0000"/><rect y="9.4" width="20" height="4.6" fill="#FFCE00"/></svg>'
  };
  var SUBS = NATIVE?[]:[
    { name:'机场 A · 标准版', url:'sub.airport-a.com/api/v1/client/subscribe?token=8f2c', nodes:8, used:154.1, cap:465, exp:'2026-12-14', state:'正常', tone:'g', primary:true, enabled:true, last:'今天 14:06', cost:'1.2 s', regions:'8 个地区' },
    { name:'机场 B · 备用',   url:'sub.airport-b.net/sub?token=4a91',                   nodes:4, used:38.6,  cap:100, exp:'2026-11-22', state:'将到期', tone:'y', enabled:true, last:'今天 14:06', cost:'0.9 s', regions:'4 个地区' },
    { name:'机场 C · 试用',   url:'trial.airport-c.io/api/v1/sub?key=77de',              nodes:2, used:9.9,   cap:20,  exp:'2026-11-18', state:'试用', tone:'a', enabled:false, last:'今天 14:06 · 失败', cost:'403', regions:'2 个地区' }
  ];
  var DAYS = NATIVE?Array(30).fill(0):(function (){
    var a = [];
    for (var i = 0; i < 30; i++)
      a.push(Math.max(0.4, 4.4 + 3.1 * Math.sin(i / 2.05) + 1.7 * Math.sin(i / 1.07 + 1.2)
        + (i % 7 === 5 ? 3.4 : 0) + (i % 9 === 2 ? -1.6 : 0) + Math.random() * 2.6));
    /* 峰值统一成 10 GB，读数与动画都好对 */
    var mx = Math.max.apply(null, a);
    return a.map(function (v){ return +(v / mx * 30).toFixed(1); });
  })();
  /* 日用量上限：渐变按这个绝对刻度锚定，几百 MB 永远只露蓝 */
  var DAY_CAP = 30;

  function dayBars(count){
    var arr = DAYS.slice(-count);
    var peak = arr.indexOf(Math.max.apply(null, arr));
    return arr.map(function (v, i){
      var h = Math.max(4, v / (NATIVE?Math.max(DAY_CAP,Math.max.apply(null,DAYS)):DAY_CAP) * 100);
      var txt = v >= 10 ? String(Math.round(v)) : (v % 1 === 0 ? v.toFixed(0) : v.toFixed(1));
      return '<i class="' + (i === peak ? 'peak' : '') + '" style="height:' + h.toFixed(1) + '%" ' +
        'title="' + v.toFixed(1) + ' GB">' +
        '<span class="ptag">' + txt + '</span>' +
        '</i>';
    }).join('');
  }
  function growBars(scope){
    var host=$(scope),page=host&&host.closest('.page');
    if(!host||!page||!page.classList.contains('on')||host.__barSignature===DAYS.join(','))return;
    host.__barSignature=DAYS.join(',');
    if(matchMedia('(prefers-reduced-motion:reduce)').matches)return;
    $$(scope + ' .uchart i').forEach(function (b, i){
      animate(b, { scaleY: [0, 1], opacity: [.7, 1] },
        { duration: .5, delay: i * .012, easing: [0.16, 0.84, 0.24, 1] });
    });
  }
  function regionChips(){
    var c = {}, order = [];
    NODES.forEach(function (n){ if (!c[n.region]) { c[n.region] = 0; order.push(n.region); } c[n.region]++; });
    return order.map(function (r){
      return '<span class="rchip"><i></i>' + r + ' <b>' + c[r] + '</b></span>';
    }).join('');
  }
  var LOGS = NATIVE?[]:[
    ['14:32:07','info','应用启动 · neo 0.2.8 (win-x64)'],
    ['14:32:07','info','加载配置 data\\config.json（xray）'],
    ['14:32:08','debug','预先探测端口 21808 / 21809 可用'],
    ['14:32:08','info','订阅「机场 A」节点 8 个，已合并到节点列表'],
    ['14:32:09','warn','机场 C 试用剩余 6 节点，可能随时失效'],
    ['14:32:11','info','xray 25.9.6 已就绪，等待连接'],
    ['14:33:40','info','连接到 香港 01 · IPLC (hk01.neo.im:443)'],
    ['14:33:41','debug','TCP 握手 18 ms · TLS 1.3 · h2'],
    ['14:33:41','info','出口确认 香港 · 203.0.113.7'],
    ['14:35:02','warn','节点 美国洛杉矶 04 真连延迟 188 ms，高于阈值'],
    ['14:36:15','error','TUN 网卡创建失败：需要管理员权限（已回退系统代理）'],
    ['14:36:15','info','已自动回退到系统代理模式'],
    ['14:38:20','debug','规则集 geoip:cn 命中 1,284 次'],
    ['14:40:00','info','流量统计写入 data\\usage.json']
  ];
  var subLogOpen = {};   /* 订阅日志哪些行是展开的 */
  var SUBLOG = NATIVE?[]:[
    ['今天 14:06','机场 A · 标准版','成功','8 个节点','1.2 s'],
    ['今天 14:06','机场 B · 备用','成功','4 个节点','0.9 s'],
    ['今天 14:06','机场 C · 试用','失败','订阅返回 403','1.8 s'],
    ['今天 02:06','机场 A · 标准版','成功','8 个节点','1.1 s'],
    ['今天 02:06','机场 B · 备用','成功','4 个节点','1.0 s'],
    ['昨天 14:06','机场 C · 试用','成功','2 个节点','2.3 s']
  ];

  /* ---------------- 数字与延迟 ---------------- */
  function lkind(ms){ return ms > 0 ? (ms <= 800 ? 'ok' : ms <= 2000 ? 'warn' : 'bad') : 'dim'; }
  function ltext(ms){ return ms > 0 ? ms + ' ms' : ms === -1 ? '超时' : '未测速'; }
  function cells(str, zero){
    var frag = document.createDocumentFragment();
    for (var i = 0; i < str.length; i++){
      var d = document.createElement('span'); d.className = 'd';
      var col = document.createElement('span'); col.className = 'col';
      for (var n = 0; n <= 9; n++){ var s = document.createElement('span'); s.textContent = n; col.appendChild(s); }
      col.style.transform = 'translateY(' + (zero ? 0 : -(parseInt(str[i],10) * 10)) + '%)';
      d.appendChild(col); frag.appendChild(d);
    }
    return frag;
  }

  /* 延迟单位 ms 与数字同基线：数字是 1.5em 的滚动盒子，盒子底边比基线低一个降部，
     所以按盒子底边减 descender 算真实基线，再把 ms 顶上去（任何字号都成立） */
  function alignLatUnits(root){
    (root||document).querySelectorAll('.lat .unit').forEach(function (u){
      var lat = u.closest('.lat');
      var dg = lat && lat.querySelector('.digits');
      if (!dg) return;
      u.style.top = '0px';
      var fs = parseFloat(getComputedStyle(lat).fontSize) || 12;
      var desc = fs * 0.22;
      var db = dg.getBoundingClientRect().bottom - desc;
      var ub = u.getBoundingClientRect().bottom;
      u.style.top = (db - ub).toFixed(2) + 'px';
    });
  }
  function latNode(ms){
    var w = document.createElement('div'); w.className = 'lat ' + lkind(ms);
    var dot = document.createElement('i'); dot.className = 'dot'; w.appendChild(dot);
    if (ms > 0){
      var dg = document.createElement('span'); dg.className = 'digits'; dg.appendChild(cells(String(ms), false)); w.appendChild(dg);
      var u = document.createElement('span'); u.className = 'unit'; u.textContent = 'ms'; w.appendChild(u);
    } else {
      var t = document.createElement('span'); t.className = 'txt'; t.textContent = ltext(ms); w.appendChild(t);
    }
    return w;
  }
  function setLat(lat, ms){
    lat.className = 'lat ' + lkind(ms);
    var str = String(ms), dg = lat.querySelector('.digits');
    if (ms <= 0){
      if (dg) dg.remove();
      var u0 = lat.querySelector('.unit'); if (u0) u0.remove();
      var t = lat.querySelector('.txt');
      if (!t){ t = document.createElement('span'); t.className = 'txt'; lat.appendChild(t); }
      t.textContent = ltext(ms);
      animate(t, { opacity: [0, 1] }, { duration: .14 });
      return;
    }
    var t2 = lat.querySelector('.txt'); if (t2) t2.remove();
    if (!dg || dg.children.length !== str.length){
      if (dg) dg.remove();
      dg = document.createElement('span'); dg.className = 'digits';
      var u = lat.querySelector('.unit');
      if (!u){ u = document.createElement('span'); u.className = 'unit'; u.textContent = 'ms'; lat.appendChild(u); }
      lat.insertBefore(dg, u);
      dg.appendChild(cells(str, true));
      void lat.offsetWidth;
      for (var k = 0; k < str.length; k++)
        dg.children[k].firstChild.style.transform = 'translateY(-' + (parseInt(str[k],10) * 10) + '%)';
      return;
    }
    for (var i = 0; i < str.length; i++)
      dg.children[i].firstChild.style.transform = 'translateY(-' + (parseInt(str[i],10) * 10) + '%)';
  }

  /* ---------------- 订阅更新计划（可改） ---------------- */
  var PLAN = { auto: true, hours: 12 };
  var PLAN_OPTS = [6, 12, 24, 48];
  function nextTime(){
    if (!PLAN.auto) return '已关闭';
    if (PLAN.hours === null) return '手动';
    var d = new Date(Date.now() + PLAN.hours * 3600 * 1000);
    var hh = ('0' + d.getHours()).slice(-2), mm = ('0' + d.getMinutes()).slice(-2);
    var sameDay = d.getDate() === new Date().getDate();
    return (sameDay ? '今天 ' : '明天 ') + hh + ':' + mm;
  }
  function planLabel(){
    if (PLAN.hours === null) return '手动';
    return PLAN.hours >= 24 ? (PLAN.hours / 24) + ' 天' : PLAN.hours + ' 小时';
  }

  /* ---------------- 反馈条 ---------------- */
  var toastEl = null, toastTimer = null;
  function toast(msg){
    if (!toastEl){
      toastEl = document.createElement('div');
      toastEl.className = 'toastbar';
      $('.content').appendChild(toastEl);
    }
    toastEl.textContent = msg;
    toastEl.classList.add('on');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function (){ toastEl.classList.remove('on'); }, 1800);
  }

  /* ---------------- 通用小件 ---------------- */
  function sw(on){
    var s = document.createElement('button');
    s.type='button';s.setAttribute('role','switch');s.setAttribute('aria-checked',String(on));
    s.className = 'sw' + (on ? ' on' : '');
    s.innerHTML = '<i></i><b></b>';
    return s;
  }
  function rowC(title, desc, ctl){
    var r = document.createElement('div');
    r.className = 'row';
    var left = document.createElement('div');
    var t = document.createElement('div'); t.className = 't'; t.textContent = title; left.appendChild(t);
    if (desc){ var d = document.createElement('div'); d.className = 'd'; d.textContent = desc; left.appendChild(d); }
    r.appendChild(left);
    r.appendChild(ctl);
    return r;
  }
  function textv(v, cls){
    var s = document.createElement('span');
    s.className = 'v' + (cls ? ' ' + cls : '');
    s.textContent = v;
    return s;
  }

  /* ---------------- 连接页（三版共用一份状态） ---------------- */
  var R = 88, C = 2 * Math.PI * R;
  var ringProgs = [];
  function ringInit(){
    ringProgs = $$('.ring .prog');
    ringProgs.forEach(function (p){
      /* 样式表里残留 stroke:url(#ringGrad) 会把描边置空，这里按各自 svg 的渐变 id 重新指定 */
      var m = (p.getAttribute('stroke') || '').match(/#([\w-]+)/);
      if (m) p.style.stroke = 'url(#' + m[1] + ')';
      p.style.strokeDasharray = C;
      p.style.strokeDashoffset = C;
    });
  }
  function setRings(on){
    ringProgs.forEach(function (p){
      p.style.strokeDashoffset = on ? 0 : C;
    });
  }

  var state = { conn:false, t0:0, timer:null, mode:'rule' };

  function stateTexts(){
    $$('.ringButton').forEach(function (b){
      b.setAttribute('aria-pressed', String(state.conn));
      b.setAttribute('aria-label', state.conn ? '\u65ad\u5f00\u8fde\u63a5' : '\u8fde\u63a5');
    });
    var label = state.conn ? '已连接' : '未连接';
    $$('.cState').forEach(function (e){
      e.textContent = label;
      e.style.color = state.conn ? 'var(--NeoGreen)' : '';
    });
    $$('.cExit').forEach(function (e){ e.textContent = state.conn ? '出口地区 ' + currentNode().region : '出口地区 —'; });
    $$('.cExitShort').forEach(function (e){ e.textContent = state.conn ? currentNode().region : '—'; });
    $$('.nav[data-page="conn"] .ndot').forEach(function (d){ d.style.background = state.conn ? 'var(--NeoGreen)' : ''; });
    $$('.nav[data-page="conn"] .nmeta em').forEach(function (e){ e.textContent = label; });
    $$('.connect').forEach(function (b){
      b.textContent = state.conn ? '断开' : '连接';
      b.classList.toggle('primary', !state.conn);
    });
    $$('.healthD').forEach(function (e){
      e.textContent = state.conn ? '查看检测结果' : '连接后检测';
    });
    $$('.proxyState').forEach(function(e){e.textContent=state.conn?'已接管':'未接管';});
    $$('.proxyBadge').forEach(function(e){e.textContent=state.conn?'已启用':'未启用';e.classList.toggle('g',state.conn);});
    $('#connCaption').textContent=state.conn?'已固定当前节点':'准备就绪';
    if($('#coreActive'))$('#coreActive').textContent=state.conn?'12':'0';
    $('#coreParticipants').textContent=(state.conn?'1':'0')+' / '+NODES.length;
    if (typeof renderDiag==='function' && DIAG) renderDiag();
    if ($('#dConnect')) $('#dConnect').textContent=state.conn?'断开':'连接';
  }

  function toggleConn(){
    if(NATIVE){if(nodeSheetIsVisible()){closeNodeSheet();return;}return host("connection.toggle");}
    if (nodeSheetIsVisible()) { closeNodeSheet(); return; }
    state.conn = !state.conn;
    if (state.conn){
      setRings(true);
      state.t0 = Date.now();
      $$('.cTime').forEach(function (e){ e.textContent = '00:00:00'; });
      state.timer = setInterval(function (){
        var sec = Math.floor((Date.now() - state.t0) / 1000);
        $$('.cTime').forEach(function (e){ e.textContent = clock(sec); });
      }, 1000);
      for (var k = 0; k < series.length; k++)
        series[k] = Math.max(0.2, 2.2 + 1.6 * Math.sin(k / 4.1) + 0.7 * Math.sin(k / 1.7) + Math.random() * .4);
      tick.draw();
      tick.start();
    } else {
      setRings(false);
      clearInterval(state.timer);
      $$('.cTime').forEach(function (e){ e.textContent = '00:00:00'; });
      tick.stop();
    }
    resetDiagnostics();
    if(!state.conn){series=series.map(function(){return 0;});seriesUp=seriesUp.map(function(){return 0;});tick.draw();}
    stateTexts();
    if (state.conn) runDiagnostics();
  }
  function clock(sec){
    var hh = Math.floor(sec / 3600), mm = Math.floor(sec / 60) % 60, ss = sec % 60;
    return [hh, mm, ss].map(function (x){ return (x < 10 ? '0' : '') + x; }).join(':');
  }

  /* 实时流量：正弦叠加 + 噪声，曲线按本段峰值自适应，三版同时绘制 */
  var series = [];
  var seriesUp = [];           /* 上传单独一条，不能和下载共用 */
  var targetDown = [], targetUp = [], springAnim = null;
  for (var s0 = 0; s0 < 65; s0++){ series.push(0); seriesUp.push(0); targetDown.push(0); targetUp.push(0); }
  var phase = 0;

  var tick = (function (){
    var id = null, anim = null, seeded = false;

    /* 3 点平滑：采样密、原始值抖，直连会像蛇 */
    function smooth(arr){
      var out = arr.slice();
      for (var i = 1; i < arr.length - 1; i++) out[i] = (arr[i-1] + arr[i]*2 + arr[i+1]) / 4;
      return out;
    }
    /* Catmull-Rom -> 贝塞尔；控制点 y 夹在两端之间，杜绝上下甩 */
    function pathOf(seq, w, h, top){
      var step = w / (seq.length - 1);
      var p = seq.map(function (v, i){ return { x: i*step, y: h - (v/top)*(h-4) - 2 }; });
      var d = 'M' + p[0].x.toFixed(1) + ' ' + p[0].y.toFixed(1);
      for (var i = 0; i < p.length - 1; i++){
        var p0 = p[i-1] || p[i], p1 = p[i], p2 = p[i+1], p3 = p[i+2] || p[i+1];
        var c1x = p1.x + (p2.x - p0.x) / 6;
        var c2x = p2.x - (p3.x - p1.x) / 6;
        var lo = Math.min(p1.y, p2.y) - 0.6, hi = Math.max(p1.y, p2.y) + 0.6;
        var c1y = Math.max(lo, Math.min(hi, p1.y + (p2.y - p0.y) / 6));
        var c2y = Math.max(lo, Math.min(hi, p2.y - (p3.y - p1.y) / 6));
        d += ' C' + c1x.toFixed(1) + ' ' + c1y.toFixed(1) + ' ' + c2x.toFixed(1) + ' ' + c2y.toFixed(1) +
             ' ' + p2.x.toFixed(1) + ' ' + p2.y.toFixed(1);
      }
      return { line: d, fill: d + ' L' + w + ' ' + h + ' L0 ' + h + ' Z' };
    }


    /* 历史整体左移一格：新数据先画好，然后把整组放到 -step，用 motion 匀速滑回 0。
       到下一帧时形状与"平移后"完全一致，所以没有跳变，也不会有任何纵向抖动 */
    function slideAll(){
      $$('.sparkSvg .flow-slide').forEach(function (g){
        var svg = g.ownerSVGElement;
        var w = svg.clientWidth || 320;
        var step = w / (series.length - 1);
        if (g.__anim) { g.__anim.stop(); g.__anim = null; }
        g.setAttribute('transform', 'translate(' + (-step).toFixed(2) + ',0)');
        g.__anim = window.Motion.animate(g, { transform: 'translate(0px,0px)' }, {
          duration: 0.7, ease: 'linear'
        });
      });
    }

    function drawAll(){
      if(NATIVE){window.neoTraffic.draw();return;}
      var svgs = $$('.sparkSvg');
      if (!svgs.length) return;
      var dn = smooth(series), upv = smooth(seriesUp);
      var peak = Math.max(Math.max.apply(null, dn) || 0, Math.max.apply(null, upv) || 0);
      var empty = !(peak > 0);
      var top = (peak || 1) * 1.18;

      svgs.forEach(function (svg){
        var w = svg.clientWidth || svg.parentElement.clientWidth || 320;
        var h = svg.clientHeight || 60;
        svg.setAttribute('viewBox', '0 0 ' + w + ' ' + h);

        if (!svg.querySelector('.flow-line')){
          svg.innerHTML =
            '<defs>' +
              '<linearGradient id="fg" x1="0" y1="0" x2="0" y2="1">' +
                '<stop offset="0" stop-color="#5aa9ff" stop-opacity=".33"/>' +
                '<stop offset=".55" stop-color="#5aa9ff" stop-opacity=".10"/>' +
                '<stop offset="1" stop-color="#5aa9ff" stop-opacity="0"/></linearGradient>' +
              '<linearGradient id="fadeX" x1="0" y1="0" x2="1" y2="0">' +
                '<stop offset="0" stop-color="#fff" stop-opacity="0"/>' +
                '<stop offset=".18" stop-color="#fff" stop-opacity="1"/></linearGradient>' +
              '<mask id="fm"><rect width="100%" height="100%" fill="url(#fadeX)"/></mask>' +
            '</defs>' +
            '<g class="flow-grid" stroke="rgba(255,255,255,.05)" stroke-width="1"></g>' +
            '<g class="flow-body" mask="url(#fm)">' +
              '<g class="flow-slide">' +
                '<path class="flow-area" fill="url(#fg)"/>' +
                '<path class="flow-line" fill="none" stroke="#5aa9ff" stroke-width="1.5" stroke-linejoin="round" stroke-linecap="round"/>' +
                '<path class="flow-line up" fill="none" stroke="#43d17f" stroke-width="1.2" stroke-linecap="round" stroke-opacity=".95"/>' +
              '</g>' +
            '</g>' +
            '<circle class="flow-now" r="2.6" fill="#5aa9ff"/>' +
            '<circle class="flow-now-ring" r="5.5" fill="none" stroke="#5aa9ff" stroke-opacity=".28" stroke-width="1"/>';
        }

        var g = svg.querySelector('.flow-grid');
        if (g) g.innerHTML = [0.25, 0.5, 0.75].map(function (r){
          var y = (h * r).toFixed(1);
          return '<line x1="0" y1="' + y + '" x2="' + w + '" y2="' + y + '" stroke-dasharray="2 6"/>';
        }).join('');

        if (empty){
          var body0 = svg.querySelector('.flow-body'); if (body0) body0.setAttribute('opacity', '0');
          var n0 = svg.querySelector('.flow-now'); if (n0) n0.setAttribute('opacity', '0');
          var r0 = svg.querySelector('.flow-now-ring'); if (r0) r0.setAttribute('opacity', '0');
          if (!svg.querySelector('.flow-empty')){
            var t = document.createElementNS('http://www.w3.org/2000/svg', 'text');
            t.setAttribute('class', 'flow-empty');
            t.setAttribute('x', w / 2); t.setAttribute('y', h / 2 + 4);
            t.setAttribute('text-anchor', 'middle');
            t.setAttribute('fill', 'rgba(255,255,255,.22)');
            t.setAttribute('font-size', '10.5');
            t.textContent = '连接后开始记录';
            svg.appendChild(t);
          }
          return;
        }
        var em = svg.querySelector('.flow-empty'); if (em) em.remove();
        var body = svg.querySelector('.flow-body'); if (body) body.setAttribute('opacity', '1');

        var down = pathOf(dn, w, h, top), up = pathOf(upv, w, h, top);
        svg.querySelector('.flow-line').setAttribute('d', down.line);
        svg.querySelector('.flow-area').setAttribute('d', down.fill);
        svg.querySelector('.flow-line.up').setAttribute('d', up.line);

        var last = { x: w - 3, y: h - (dn[dn.length-1] / top) * (h - 4) - 2 };
        var now = svg.querySelector('.flow-now'), ring = svg.querySelector('.flow-now-ring');
        if (now){ now.setAttribute('cx', last.x); now.setAttribute('cy', last.y); now.setAttribute('opacity', '1'); }
        if (ring){ ring.setAttribute('cx', last.x); ring.setAttribute('cy', last.y); ring.setAttribute('opacity', '1'); }
      });
    }

    return {
      draw: drawAll,
      start: function (){
        if(NATIVE)return;
        if (id) return;
        id = setInterval(function (){
          if (document.hidden || !$('.page[data-page="conn"]').classList.contains('on')) return;
          phase += 0.16;
          var base = 2.6 + 2.2 * Math.sin(phase) + 0.9 * Math.sin(phase * 2.7) + (Math.random() - .5) * 1.1;
          var v = Math.max(0.05, base);
          var vu = Math.max(0.02, v * 0.19 + Math.random() * .12);
          if (!seeded){
            /* 连接瞬间补一段"过去的历史"：用同一个生成器倒着跑，
               不然左侧会是一整条水平直线，看着假 */
            var ph = phase - 0.16 * series.length;
            for (var sd = 0; sd < series.length; sd++){
              ph += 0.16;
              var hv = Math.max(0.05, 2.6 + 2.2 * Math.sin(ph) + 0.9 * Math.sin(ph * 2.7) + (Math.random() - .5) * 1.1);
              var hu = Math.max(0.02, hv * 0.19 + Math.random() * .12);
              series[sd] = hv; seriesUp[sd] = hu;
              targetDown[sd] = hv; targetUp[sd] = hu;
            }
            series[series.length - 1] = v; seriesUp[seriesUp.length - 1] = vu;
            targetDown[targetDown.length - 1] = v; targetUp[targetUp.length - 1] = vu;
            seeded = true;
          }
          /* 历史值不动：只把新采样接到队尾，整条曲线向左平移一格 */
          targetDown.push(v); targetDown.shift();
          targetUp.push(vu); targetUp.shift();
          series = targetDown.slice();
          seriesUp = targetUp.slice();

          drawAll();      /* 先按新数据画在 0 位置 */
          slideAll();     /* 再把整组从 -step 滑回 0：视觉上就是历史整体左移一格 */

          var peak = Math.max.apply(null, targetDown);
          $$('.dnV').forEach(function (e){ e.innerHTML = v.toFixed(1) + '<small>MB/s</small>'; });
          $$('.upV').forEach(function (e){ e.innerHTML = vu.toFixed(1) + '<small>MB/s</small>'; });
          $$('.flowPeak').forEach(function (e){ e.textContent = '峰值 ' + peak.toFixed(1) + ' MB/s'; });
        }, 700);
      },
      stop: function (){
        clearInterval(id); id = null; seeded = false;
        if (anim){ anim.stop(); anim = null; }
        $$('.dnV').forEach(function (e){ e.innerHTML = '0<small>MB/s</small>'; });
        $$('.upV').forEach(function (e){ e.innerHTML = '0<small>MB/s</small>'; });
        $$('.flowPeak').forEach(function (e){ e.textContent = '峰值 —'; });
        drawAll();
      }
    };
  })();

  /* 底部节点面板：从卡片位置顶出来，把上面的内容整体上移 */
  // Shared latency ordering, independent of the filters on the nodes page.
  // Keep cards alive while results arrive so focus and scrolling survive sorting.
  var nodeSheetCards=new Map(),nodeSheetSignature='',nodeSheetOrder='';
  function buildNodeSheet(){syncNodeSheet(true);}
  function syncNodeSheet(force){
    var list=$('#sheetList'),visible=nodeSheetIsVisible();
    if(!list||(!force&&!visible))return;
    var order=nodeOrder(true);
    var signature=JSON.stringify(order.map(function(i){var n=NODES[i];return [n.id||i,n.name,n.f,n.proto,n.real,i===CUR];}));
    if(signature===nodeSheetSignature)return;
    var keys=order.map(function(i){return NODES[i].id||'index:'+i;}),orderKey=JSON.stringify(keys);
    var moving=visible&&orderKey!==nodeSheetOrder&&!matchMedia('(prefers-reduced-motion:reduce)').matches;
    var positions=new Map(),viewport=moving?list.getBoundingClientRect():null;
    if(moving)nodeSheetCards.forEach(function(card){if(card.isConnected)positions.set(card,card.getBoundingClientRect());});
    var scrollTop=list.scrollTop,focused=list.contains(document.activeElement)?document.activeElement:null,keep=new Set(keys);
    nodeSheetCards.forEach(function(card,key){if(!keep.has(key)){card.remove();nodeSheetCards.delete(key);}});
    order.forEach(function(i,position){
      var n=NODES[i],key=keys[position],card=nodeSheetCards.get(key);
      if(!card){
        card=document.createElement('button');card.type='button';card.className='node-card sheet-node';
        card.innerHTML='<span class="node-name"><span class="flag"></span><b></b></span><span class="node-bottom"><span class="node-proto"></span></span>';
        card.__lat=latNode(n.real);card.__real=n.real;card.querySelector('.node-bottom').appendChild(card.__lat);
        card.addEventListener('click',function(e){
          e.stopPropagation();var index=+card.dataset.node,node=NODES[index];if(!node)return;
          if(NATIVE){
            card.disabled=true;
            host('node.select',{key:node.id}).then(function(){closeMenu();closeNodeSheet();}).catch(function(){}).finally(function(){card.disabled=false;});
            return;
          }
          CUR=index;paintCurrent();nSelect(index);syncNodeSheet(true);closeNodeSheet();toast('已切换到 '+node.name);
        });
        nodeSheetCards.set(key,card);
      }
      card.dataset.node=String(i);card.dataset.key=key;card.title=n.name;
      card.setAttribute('aria-label','切换到 '+n.name);card.setAttribute('aria-pressed',String(i===CUR));card.classList.toggle('on',i===CUR);
      if(card.__name!==n.name){card.querySelector('.node-name b').textContent=n.name;card.__name=n.name;}
      if(card.__flag!==n.f){card.querySelector('.flag').innerHTML=FLAGS[n.f]||'';card.__flag=n.f;}
      if(card.__proto!==n.proto){card.querySelector('.node-proto').textContent=n.proto;card.__proto=n.proto;}
      if(card.__real!==n.real){setLat(card.__lat,n.real);card.__real=n.real;}
      var before=list.children[position]||null;
      if(card!==before){if(list.moveBefore&&card.parentNode===list)list.moveBefore(card,before);else list.insertBefore(card,before);}
    });
    if(!order.length){
      if(!list.querySelector('.empty-state'))list.innerHTML='<div class="empty-state">没有可用的节点记录</div>';
    }else{var empty=list.querySelector('.empty-state');if(empty)empty.remove();}
    if(focused&&focused.isConnected&&document.activeElement!==focused)focused.focus({preventScroll:true});
    list.scrollTop=scrollTop;
    if(moving)nodeSheetCards.forEach(function(card){
      var before=positions.get(card);if(!before)return;
      if(card.__sortAnimation){card.__sortAnimation.cancel();card.__sortAnimation=null;}
      var after=card.getBoundingClientRect(),dx=before.left-after.left,dy=before.top-after.top;
      if((dx||dy)&&((before.bottom>=viewport.top&&before.top<=viewport.bottom)||(after.bottom>=viewport.top&&after.top<=viewport.bottom))){
        var animation=card.animate([{transform:'translate('+dx+'px,'+dy+'px)'},{transform:'none'}],{duration:220,easing:'cubic-bezier(.22,1,.36,1)'});
        card.__sortAnimation=animation;animation.onfinish=function(){if(card.__sortAnimation===animation)card.__sortAnimation=null;};
      }
    });
    nodeSheetSignature=signature;nodeSheetOrder=orderKey;
  }

  function openNodeSheet(){
    var page = $('.page[data-page="conn"]');
    var sheet = $('#nodeSheet');
    if (!page || !sheet) return;
    buildNodeSheet();
    sheet.inert = false;
    sheet.setAttribute('aria-hidden', 'false');
    page.classList.add('sheet-open');
  }
  function closeNodeSheet(){
    var page = $('.page[data-page="conn"]');
    var sheet = $('#nodeSheet');
    if (!page || !sheet) return;
    sheet.inert = true;
    sheet.setAttribute('aria-hidden', 'true');
    page.classList.remove('sheet-open');
  }

  function nodeSheetIsVisible(){
    var page = $('.page[data-page="conn"]'), sheet = $('#nodeSheet');
    return !!(page && sheet && page.classList.contains('on') &&
      (page.classList.contains('sheet-open') || getComputedStyle(sheet).visibility === 'visible'));
  }

  /* 节点选择器：点当前节点卡弹出 */
  function openNodePicker(anchor){
    var r = anchor.getBoundingClientRect();
    if (menuEl && menuEl.__r && Math.abs(menuEl.__r.top - r.top) < 1.5
        && Math.abs(menuEl.__r.left - r.left) < 1.5){ closeMenu(); return; }
    if (menuEl) closeMenu();
    var wrap = $('.content').getBoundingClientRect();
    var m = document.createElement('div');
    m.className = 'popmenu wide';
    m.style.left = Math.max(4, Math.min(r.left - wrap.left, wrap.width - 308)) + 'px';
    m.innerHTML = '<div class="pm-head">切换节点</div>' +
      NODES.map(function (n, i){
        var k = lkind(n.real);
        return '<button class="pm-item node" data-node="' + i + '">' +
          '<span class="flag">' + (FLAGS[n.f] || '') + '</span>' +
          '<span class="nm">' + esc(n.name) + '</span>' +
          '<span class="lat2 ' + k + '">' + ltext(n.real) + '</span>' +
          '<i>' + (i === CUR ? '&#xE73E' : '') + '</i></button>';
      }).join('') +
      '<div class="pm-foot">点击即切换 · 共 ' + NODES.length + ' 个节点</div>';
    m.__r = r;
    $('.content').appendChild(m);
    placeMenu(m, r, wrap);
    menuEl = m; menuAnchor = anchor;
    animate(m, { opacity: [0, 1], transform: ['translateY(-4px)', 'translateY(0px)'] },
      { duration: .18, easing: 'ease-out' });
    $$('.pm-item.node', m).forEach(function (b){
      b.addEventListener('click', function (e){
        e.stopPropagation();
        var i = +b.dataset.node;
        if(NATIVE){host("node.select",{key:NODES[i].id}).then(function(){closeMenu();closeNodeSheet();});return;}
        CUR = i;
        closeMenu();
        paintCurrent();
        nSelect(i);
        toast('已切换到 ' + NODES[i].name);
      });
    });
  }

  /* 当前节点卡（三版同步） */
  var CUR = 0;                       /* 当前连接的节点索引 */
  function paintCurrent(){
    var n = currentNode();
    $$('.curFlag').forEach(function (e){ e.innerHTML = FLAGS[n.f]; });
    $$('.curName').forEach(function (e){ e.textContent = n.name; });
    $$('.curMeta').forEach(function (e){ e.textContent = n.proto + ' · ' + n.trans + ' · ' + n.server + ':' + n.port; });
    $$('.curLat').forEach(function (old){
      var fresh = latNode(n.real);
      fresh.className = 'lat ' + lkind(n.real) + ' curLat';
      old.replaceWith(fresh);
    });
  }

  /* ---------------- 节点页 ---------------- */
  var nSel = 0, nFilter = 'all', nQuery = '', nRegion = 'all';
  var lists = { A:[], B:[], C:[], D:[] };
  function npass(n){
    if (nFilter === 'ok' && !(n.real > 0)) return false;
    if (nFilter === 'timeout' && n.real !== -1) return false;
    if (nFilter === 'untested' && n.real !== 0) return false;
    if (nQuery && (n.name + ' ' + n.proto + ' ' + n.trans + ' ' + n.region).toLowerCase().indexOf(nQuery) < 0) return false;
    if (nRegion !== 'all' && n.region !== nRegion) return false;
    return true;
  }
  function paintSel(list, i){
    list.forEach(function (el, k){
      if (!el) return;
      var on = k === i;
      if (on === el.classList.contains('on')) return;
      el.classList.toggle('on', on); el.setAttribute('aria-pressed',String(on));   // 颜色交给 CSS 过渡，不在脚本里插值，点一下不会闪
    });
  }
  function nSelect(i){
    if(!NODES[i])return;
    if(NATIVE&&!nativeApplying)return nativeSelectNode(i);
    nSel = i;
    CUR = i;
    ['A','C','D'].forEach(function (v){ paintSel(lists[v], i); });
    paintCurrent();
    syncSubscriptionUi();
    resetDiagnostics();
    stateTexts();
    if (state.conn) runDiagnostics();
    if ($('#listD').children.length) nodeDetail(i);
  }
  function nodeRow(n, i){
    var r = document.createElement('div');
    r.className = 'r';
    r.style.cssText = 'height:44px;border-radius:22px;padding:0 12px;display:grid;grid-template-columns:20px minmax(0,1fr) auto 76px 26px;gap:10px;align-items:center;cursor:pointer;background:linear-gradient(180deg,rgba(255,255,255,.045),rgba(255,255,255,.018));border:1px solid rgba(255,255,255,.055)';
    r.innerHTML = '<span class="flag">' + FLAGS[n.f] + '</span>' +
      '<div style="min-width:0"><div style="font-size:12.5px;font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis">' + esc(n.name) + '</div>' +
      '<div style="font-size:10px;color:var(--NeoText3);margin-top:2px">' + n.proto + ' · ' + n.trans + '</div></div>';
    var lat = latNode(n.real); r.appendChild(lat);
    var act = document.createElement('button');
    act.style.cssText = 'width:26px;height:26px;border-radius:13px;color:var(--NeoText3);font-family:"Segoe Fluent Icons";font-size:11px;opacity:0;transition:opacity .16s';
    act.innerHTML = '&#xE9D9;';
    r.appendChild(act);
    r.addEventListener('pointerenter', function (){
      act.style.opacity = 1;
      if (!r.classList.contains('on')) animate(r, { transform: 'translateY(-1.5px)' }, { type: 'spring', stiffness: 380, damping: 30, mass: .6 });
    });
    r.addEventListener('pointerleave', function (){
      act.style.opacity = 0;
      if (!r.classList.contains('on')) animate(r, { transform: 'translateY(0px)' }, { type: 'spring', stiffness: 380, damping: 30, mass: .6 });
    });
    act.addEventListener('click', function (e){ e.stopPropagation(); nTest(i, lat); });
    r.addEventListener('click', function (){ nSelect(i); });
    r.__lat = lat;
    return r;
  }
  function nodeOrder(includeAll){return NODES.map(function(n,i){return i;}).filter(function(i){return includeAll||npass(NODES[i]);}).sort(function(a,b){var x=NODES[a].real,y=NODES[b].real;return (x>0?x:x===0?1e8:1e9)-(y>0?y:y===0?1e8:1e9)||a-b;});}
  function renderNodes(){
    var rd=$('#listD');rd.innerHTML='';lists.D=[];
    nodeOrder().forEach(function(i){
      var n=NODES[i],c=document.createElement('div');c.className='node-card';c.dataset.index=i;c.tabIndex=0;c.setAttribute('role','button');c.setAttribute('aria-label','选择 '+n.name);
      c.innerHTML='<div class="node-name"><span class="flag">'+FLAGS[n.f]+'</span><b>'+esc(n.name.split('·')[0].trim())+'</b></div><div class="node-bottom"><span class="node-proto">'+n.proto+'</span><button class="node-test" aria-label="测试 '+esc(n.name)+' 延迟" title="点击测速"></button></div>';
      var test=c.querySelector('.node-test'),lat=latNode(n.real);test.appendChild(lat);c.__lat=lat;
      test.addEventListener('click',function(e){e.stopPropagation();nTest(i);});
      c.addEventListener('click',function(){nSelect(i);});
      c.addEventListener('keydown',function(e){if(e.target===c&&(e.key==='Enter'||e.key===' ')){e.preventDefault();nSelect(i);}});
      rd.appendChild(c);lists.D[i]=c;
    });
    if(!rd.children.length)rd.innerHTML='<div class="empty-state"><b>没有匹配的节点</b><span>试试其他关键词或清除筛选</span><button class="btn ghost" id="nReset">清除筛选</button></div>';
    if($('#nReset'))$('#nReset').onclick=function(){nQuery='';nRegion='all';nFilter='all';var t0=$('#nRegionText'); if(t0) t0.textContent='全部地区';$$('#nFilter .chip').forEach(function(x){x.classList.toggle('on',x.dataset.f==='all');});renderNodes();};
    paintSel(lists.D,nSel);nodeDetail(nSel);footerNodes();if(NATIVE)nativePaintExitMarks();
    $('#nCnt').textContent=nodeOrder().length+' 个 · '+new Set(NODES.filter(npass).map(function(n){return n.region;})).size+' 个地区';
  }
  function reorderNodes(){
    var host=$('#listD'),pos=new Map();
    $$('.node-card',host).forEach(function(c){pos.set(c,c.getBoundingClientRect());});
    $$('.node-card',host).forEach(function(c){if(c.__sortAnimation){c.__sortAnimation.cancel();c.__sortAnimation=null;}});
    nodeOrder().forEach(function(i,k){var node=lists.D[i],before=host.children[k]||null;if(node&&node!==before){if(host.moveBefore&&node.parentNode===host)host.moveBefore(node,before);else host.insertBefore(node,before);}});
    if(!$('.page[data-page="nodes"]').classList.contains('on')||matchMedia('(prefers-reduced-motion:reduce)').matches)return;
    $$('.node-card',host).forEach(function(c){var before=pos.get(c),after=c.getBoundingClientRect();if(!before)return;var dx=before.left-after.left,dy=before.top-after.top;if(dx||dy){var animation=c.animate([{transform:'translate('+dx+'px,'+dy+'px)'},{transform:'none'}],{duration:220,easing:'cubic-bezier(.22,1,.36,1)'});c.__sortAnimation=animation;animation.onfinish=function(){if(c.__sortAnimation===animation)c.__sortAnimation=null;};}});
  }
  /* 文字与数字同一套竖向滚动：旧值上滚、新值下滚入 */
  function rollText(el, txt){
    if (!el) return;
    var s2 = String(txt),previous=el.__v===undefined?el.textContent:el.__v;
    if (el.__v === s2) return;
    if(el.__rollAnimation){el.__rollAnimation.onfinish=null;el.__rollAnimation.cancel();}
    el.__v = s2;
    el.classList.add('roll');el.style.removeProperty('height');
    var track=document.createElement('span');track.className='track';
    var next=document.createElement('span');next.textContent=s2;
    var page=el.closest('.page');
    if(!previous||(page&&!page.classList.contains('on'))||document.hidden||matchMedia('(prefers-reduced-motion:reduce)').matches){track.appendChild(next);el.replaceChildren(track);return;}
    var old=document.createElement('span');old.textContent=previous;
    track.appendChild(old);track.appendChild(next);el.replaceChildren(track);
    var anim=track.animate([{transform:'translateY(0)'},{transform:'translateY(-50%)'}],{duration:250,easing:'cubic-bezier(.32,0,.16,1)'});
    el.__rollAnimation=anim;
    anim.onfinish=function(){if(el.__rollAnimation!==anim)return;track.replaceChildren(next);el.__rollAnimation=null;};
  }

  function nodeDetail(i){
    if(!NODES[i]){var empty=$("#nodeDrawer");empty.innerHTML="";empty.__built=false;return;}
    var n = NODES[i], d = $('#nodeDrawer');
    if (!d.__built){
      d.innerHTML =
        '<div class="dh"><span class="flag dhFlag"></span>' +
          '<div><div class="dhName" style="font-size:13.5px;font-weight:600"></div>' +
          '<div class="dhMeta" style="font-size:10px;color:var(--NeoText3);margin-top:2px"></div></div></div>' +
        '<div class="rows" style="gap:8px">' +
          '<div class="row" data-k="server" style="min-height:38px;padding:7px 12px"><span style="font-size:10.5px;color:var(--NeoText3)">服务器</span><b class="v" style="font-size:11.5px;font-family:var(--mono);color:var(--NeoText2);font-weight:400"></b></div>' +
          '<div class="row" data-k="real" style="min-height:38px;padding:7px 12px"><span style="font-size:10.5px;color:var(--NeoText3)">真连延迟</span><span class="v latHost"></span></div>' +
          '<div class="row" data-k="tcp" style="min-height:38px;padding:7px 12px"><span style="font-size:10.5px;color:var(--NeoText3)">TCP 握手</span><span class="v latHost"></span></div>' +
          '<div class="row" data-k="src" style="min-height:38px;padding:7px 12px"><span style="font-size:10.5px;color:var(--NeoText3)">来源</span><b class="v" style="font-size:11.5px;color:var(--NeoText2);font-weight:400"></b></div>' +
          '<div class="row" data-k="exit" style="min-height:38px;padding:7px 12px"><span style="font-size:10.5px;color:var(--NeoText3)">实际出口</span><b class="v" style="font-size:11.5px;color:var(--NeoText2);font-weight:400"></b></div>' +
        '</div>' +
        '<div style="display:flex;gap:8px;margin-top:auto"><button class="btn" id="dTest" style="flex:1">测速</button>' +
        '<button class="btn primary" id="dConnect" style="flex:1">连接</button></div>';
      d.__latReal = latNode(0);
      d.__latTcp = latNode(0);
      d.querySelector('[data-k="real"] .latHost').appendChild(d.__latReal);
      d.querySelector('[data-k="tcp"] .latHost').appendChild(d.__latTcp);
      d.querySelector('#dTest').addEventListener('click', function (){
        var idx = d.__idx, el = lists.D[idx];
        nTest(idx);
      });
      $('#dConnect',d).addEventListener('click',function(){if(NATIVE)nativeConnectInspected();else toggleConn();});
      d.__built = true;
    }
    d.__idx = i;
    $('#dConnect',d).textContent=state.conn?'断开':'连接';
    d.querySelector('.dhFlag').innerHTML = FLAGS[n.f];
    rollText(d.querySelector('.dhName'), n.name);
    rollText(d.querySelector('.dhMeta'), n.region + ' · ' + n.proto + ' · ' + n.trans);
    rollText(d.querySelector('[data-k="server"] .v'), n.server + ':' + n.port);
    setLat(d.__latReal, NATIVE?n.rawReal:n.real);
    setLat(d.__latTcp, n.tcp);
    rollText(d.querySelector('[data-k="src"] .v'), n.src);
    if(!NATIVE)rollText(d.querySelector('[data-k="exit"] .v'),state.conn&&i===CUR?n.region+' · 出口预览':'连接后检测');
  }
  function footerNodes(){
    var ok = NODES.filter(function (n){ return n.real > 0; });
    var avg = ok.length ? Math.round(ok.reduce(function (a, n){ return a + n.real; }, 0) / ok.length) : 0;
    $('#nHint').textContent = ok.length
      ? '已测 ' + ok.length + '/' + NODES.length + ' · 平均 ' + avg + ' ms'
      : 'A 宽行 · B 分组 · C 索引 · D 网格';
  }
  var nodeTesting=new Set(), testGeneration=0;
  function nTest(i){
    if(NATIVE)return host("nodes.test",{keys:NODES[i]?[NODES[i].id]:[],tcp:false});
    if(nodeTesting.has(i))return Promise.resolve();nodeTesting.add(i);
    var c=lists.D[i];if(c){c.classList.add('testing');c.__lat.innerHTML='<span class="txt">测速中</span>';}
    return new Promise(function(resolve){setTimeout(function(){
      // 固定的演示完成节奏，验证渐进排序。并非真实网络测速。
      NODES[i].real=32+(i*47)%231;nodeTesting.delete(i);
      if(lists.D[i]){lists.D[i].classList.remove('testing');lists.D[i].__lat.innerHTML='';setLat(lists.D[i].__lat,NODES[i].real);}
      if(!npass(NODES[i]))renderNodes();else{reorderNodes();nodeDetail(nSel);footerNodes();}
      if(i===CUR)paintCurrent();resolve();
    },340+(i*71)%450);});
  }
  async function testNodeBatch(indices){
    if(NATIVE)return host("nodes.test",{keys:indices.map(function(i){return NODES[i].id;}),tcp:false});
    var button=$('#nTestAll');if(button.disabled)return;button.disabled=true;var done=0,cursor=0;
    button.textContent='测速 0 / '+indices.length;
    async function worker(){while(cursor<indices.length){var i=indices[cursor++];await nTest(i);done++;button.textContent='测速 '+done+' / '+indices.length;}}
    await Promise.all([worker(),worker(),worker()]);button.disabled=false;button.textContent='全部测速';footerNodes();
  }
  function buildB(){
    var host = $('#listB'); host.innerHTML = ''; lists.B = [];
    var order = [], map = {};
    NODES.forEach(function (n, i){
      if (!npass(n)) return;
      if (!map[n.region]){ map[n.region] = []; order.push(n.region); }
      map[n.region].push(i);
    });
    order.forEach(function (region){
      var idx = map[region];
      var g = document.createElement('div');
      g.style.cssText = 'border-radius:24px;padding:8px 10px 10px;background:linear-gradient(180deg,rgba(255,255,255,.032),rgba(255,255,255,.012));border:1px solid rgba(255,255,255,.045)';
      var ok = idx.filter(function (i){ return NODES[i].real > 0; });
      var avg = ok.length ? Math.round(ok.reduce(function (a, i){ return a + NODES[i].real; }, 0) / ok.length) : 0;
      var gh = document.createElement('div');
      gh.style.cssText = 'display:flex;align-items:center;gap:8px;height:28px;padding:0 6px 0 4px';
      gh.innerHTML = '<b style="font-size:11.5px">' + region + '</b><em style="font-style:normal;font-size:10px;color:var(--NeoText3)">' + idx.length + ' 个</em>' +
        '<span style="margin-left:auto;font-size:10px;color:var(--NeoText3);font-variant-numeric:tabular-nums">' + (avg ? '平均 ' + avg + ' ms' : '未测速') + '</span>';
      g.appendChild(gh);
      var rows = document.createElement('div');
      rows.style.cssText = 'display:grid;grid-template-columns:1fr 1fr;gap:6px';
      idx.forEach(function (i){
        var n = NODES[i];
        var g2 = document.createElement('div');
        g2.style.cssText = 'height:40px;border-radius:16px;padding:0 10px;cursor:pointer;display:grid;grid-template-columns:20px minmax(0,1fr) 68px;gap:8px;align-items:center;background:rgba(255,255,255,.03);border:1px solid rgba(255,255,255,.04)';
        g2.innerHTML = '<span class="flag">' + FLAGS[n.f] + '</span><span style="font-size:12px;font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis">' + esc(n.name) + '</span>';
        var lat = latNode(n.real); g2.appendChild(lat); g2.__lat = lat;
        g2.addEventListener('click', function (){ nSelect(i); });
        rows.appendChild(g2); lists.B[i] = g2;
      });
      g.appendChild(rows);
      host.appendChild(g);
    });
  }
  function buildC(){
    var host = $('#regionsC'); host.innerHTML = '';
    var counts = {};
    NODES.forEach(function (n){ counts[n.region] = (counts[n.region] || 0) + 1; });
    var data = [['all','全部',NODES.length]].concat(Object.keys(counts).map(function (r){ return [r, r, counts[r]]; }));
    data.forEach(function (it){
      var b = document.createElement('button');
      b.className = 'rg' + (it[0] === nRegion ? ' on' : '');
      b.style.cssText = 'height:30px;flex:none;border-radius:15px;padding:0 10px;display:flex;align-items:center;gap:8px;font-size:11.5px;color:var(--NeoText2);transition:background .16s,color .16s';
      if (it[0] === nRegion){ b.style.background = 'rgba(255,255,255,.1)'; b.style.color = 'var(--NeoText1)'; }
      b.innerHTML = '<span>' + it[1] + '</span><em style="font-style:normal;margin-left:auto;font-size:10px;color:var(--NeoText3)">' + it[2] + '</em>';
      b.addEventListener('click', function (){
        nRegion = it[0];
        $$('#regionsC button').forEach(function (x){
          var on = x === b;
          x.style.background = on ? 'rgba(255,255,255,.1)' : '';
          x.style.color = on ? 'var(--NeoText1)' : 'var(--NeoText2)';
        });
        renderNodes();
      });
      host.appendChild(b);
    });
  }
  $$('#nFilter .chip').forEach(function (c){
    c.addEventListener('click', function (){
      $$('#nFilter .chip').forEach(function (x){ x.classList.toggle('on', x === c); });
      nFilter = c.dataset.f;
      renderNodes();
    });
  });
  var nSearchEl = $('#nSearch');
  if (nSearchEl) nSearchEl.addEventListener('input', function (e){ nQuery = e.target.value.trim().toLowerCase(); renderNodes(); });
  $('#nConnectCompact').addEventListener('click',toggleConn);
  $('#nTestAll').addEventListener('click',function(){openExitRegionTest();});
  if($('#nTcpAll'))$('#nTcpAll').addEventListener('click',function(){testNodeBatch(NODES.map(function(n,i){return i;}));});
  if($('#nRetest'))$('#nRetest').addEventListener('click',function(){
    var ids=[]; NODES.forEach(function(n,i){ if(n.real<=0) ids.push(i); });
    if(ids.length) testNodeBatch(ids); else toast('没有待补测的节点');
  });
  (function initRegionPicker(){
    var btn = $('#nRegion'); if (!btn) return;
    btn.addEventListener('click', function (){
      var regions = [];
      NODES.forEach(function (n){ if (regions.indexOf(n.region) < 0) regions.push(n.region); });
      var items = [{v:'all', label:'全部地区'}].concat(regions.map(function (r){ return {v:r, label:r}; }));
      openPicker(btn, '按地区筛选', items, nRegion, function (v){
        nRegion = v;
        var txt = $('#nRegionText'); if (txt) txt.textContent = (v === 'all' ? '全部地区' : v);
        renderNodes();
      });
    });
  })();
  $('#nMore').addEventListener('click',function(){openPicker(this,'节点操作',[
    {v:'import',label:'手动添加节点'},{v:'warp',label:'注册 WARP'},
  ],'',function(v){if(v==='retry'){var ids=NODES.map(function(n,i){return i;}).filter(function(i){return NODES[i].real<=0;});if(ids.length)testNodeBatch(ids);else toast('没有待重测节点');}else if(v==='tcp'){testNodeBatch(NODES.map(function(n,i){return i;}));}else if(v==='import'){openNodeImport();}else{showUiDialog('WARP','<p class="dialog-copy">注册后会新增 WARP 节点。</p><label class="field">许可证（可选）<input placeholder="输入许可证"></label><div class="dialog-actions"><button class="btn primary" data-preview-action="注册请求">注册</button></div>');}});});

  /* ---------------- 订阅页（三版） ---------------- */

  /* 主用高亮 = 一块共享胶囊，在行之间滑动（实验页第 2 版） */
  function paintSubPill(animate){
    var host = $('#subsBody1');
    if (!host) return;
    var list = host.querySelector('.s1list') || host;
    var pill = list.querySelector('.srow-pill');
    if (!pill){
      pill = document.createElement('div');
      pill.className = 'srow-pill';
      list.style.position = 'relative';
      list.appendChild(pill);
    }
    var rows = [].slice.call(list.querySelectorAll('.srow'));
    var idx = -1;
    SUBS.forEach(function (s, k){ if (s.primary) idx = k; });
    var row = rows[idx];
    if (!row){ pill.style.opacity = '0'; return; }

    var lr = list.getBoundingClientRect(), rr = row.getBoundingClientRect();
    var top = rr.top - lr.top + list.scrollTop;
    var h = rr.height;
    var prev = pill.__top;

    pill.style.height = h + 'px';
    pill.style.opacity = '1';
    pill.style.transform = 'translateY(' + top + 'px)';
    pill.__top = top;

    if (animate && typeof prev === 'number' && Math.abs(prev - top) > 0.5){
      pill.getAnimations().forEach(function (a){ try{ a.cancel(); }catch(e){} });
      pill.animate(
        [{transform:'translateY(' + prev + 'px)'},{transform:'translateY(' + top + 'px)'}],
        {duration:300,easing:'cubic-bezier(.32,0,.16,1)',fill:'none'});
    }
  }

  function setPrimarySub(i){
    SUBS.forEach(function (s, k){ s.primary = (k === i); });
    $$('#subsBody1 .srow').forEach(function (row, k){
      var nm = row.querySelector('.nm'); if (!nm) return;
      var has = nm.querySelector('.badge.a');
      if (k === i && !has){
        var b = document.createElement('span'); b.className = 'badge a'; b.textContent = '主用'; nm.appendChild(b);
      } else if (k !== i && has){ has.remove(); }
    });
    paintSubPill(true);
    toast(SUBS[i].name + ' 设为主用');
  }


  /* 出口地区测试：逐个节点比对 实际出口 IP / 归属地区 与 节点标注地区 */
  var EXIT_REGION = {
    '香港':['香港','HK','Hong Kong'],'日本':['日本','JP','Japan','东京','大阪'],
    '新加坡':['新加坡','SG','Singapore'],'美国':['美国','US','United States','洛杉矶','纽约'],
    '台湾':['台湾','TW','Taiwan','台北'],'韩国':['韩国','KR','Korea','首尔'],'德国':['德国','DE','Germany','法兰克福']
  };
  function openExitRegionTest(){
    var rows = NODES.slice(0, 8).map(function (n, i){
      var ip = '203.0.' + (10 + i) + '.' + (20 + i * 7);
      var hit = true, guess = n.region;
      var seed = (i * 37 + 11) % 11;
      if (seed === 0){ hit = false; guess = '美国'; }
      var badge = hit
        ? '<span class="xv ok">一致</span>'
        : '<span class="xv no">不符 · ' + guess + '</span>';
      return '<div class="xrow"><span class="xn">' + n.name.split('·')[0].trim() + '</span>' +
        '<span class="xi">' + ip + '</span><span class="xg">' + n.region + '</span>' + badge + '</div>';
    }).join('');
    showUiDialog('出口地区测试', '<p class="dialog-copy">比对每个节点的实际出口 IP 归属地与标注地区，不一致的会在节点上标出。</p>' +
      '<div class="xlist">' + rows + '</div>' +
      '<div class="dialog-actions"><button class="btn ghost" data-preview-action="导出结果">导出结果</button>' +
      '<button class="btn primary" data-preview-action="重新测试">重新测试</button></div>');
  }

  function subRow(s, i, compact){
    var pct = s.cap ? Math.round(s.used / s.cap * 100) : null;
    var off = s.enabled === false;
    return '<div class="srow' + (off ? ' off' : '') + '" data-sub="' + i + '" style="align-items:stretch">' +
      '<div class="s-top" style="width:100%">' +
        '<div class="nm">' +
          '<span class="nmtext">' + esc(s.name) + '</span>' +
          '<span class="badge ' + s.tone + '">' + s.state + '</span>' +
          (s.primary ? '<span class="badge a">主用</span>' : '') +
          (off ? '<span class="badge r">已停用</span>' : '') +
        '</div>' +
        '<div class="acts">' +
          '<button class="sw mini' + (off ? '' : ' on') + '" data-subsw="' + i + '" role="switch" aria-checked="' + !off + '" aria-label="启用 '+esc(s.name)+'"><b></b></button>' +
          '<button class="btn sm ghost" data-upd="' + i + '">更新</button><button class="btn sm icon" data-submore="'+i+'" aria-label="管理 '+esc(s.name)+'">&#xE712;</button>' +
        '</div>' +
      '</div>' +
      '<div class="s-bot" style="width:100%">' +
        '<span class="url">' + esc(s.url.split('?')[0]) + '</span>' +
        '<span class="cell">'+(s.cap?'用量 <b>'+s.used+'</b> / '+s.cap+' GB':'未提供额度')+'</span>' +
        '<span class="cell">' + (off ? '—' : s.nodes + ' 个节点') + '</span>' +
        '<span class="cell">' + (s.exp?s.exp.slice(5)+' 到期':'未提供到期时间') + '</span>' +
      '</div>' +
      '</div>';
  }
  function planRows(){
    return '<div class="minirow"><div class="t">自动更新订阅</div>' + '<span class="sw on"><i></i><b></b></span></div>' +
      '<div class="minirow"><div class="t">更新间隔</div><span class="v">每 12 小时</span></div>' +
      '<div class="minirow"><div class="t">上次更新</div><span class="v">今天 14:06</span></div>' +
      '<div class="minirow"><div class="t">下次</div><span class="v">明天 02:06</span></div>' +
      '<div class="minirow"><div><div class="t">最近一次失败</div><div class="d">机场 C · 403 · 今天 14:06</div></div>' +
        '<button class="btn sm ghost">重试</button></div>' +
      '<div style="margin-top:10px;font-size:10px;color:var(--NeoText3)">最近三次</div>' +
      '<div class="minirow" style="min-height:26px"><span class="d">今天 14:06 · 2 成功 1 失败</span><span class="v">3.9 s</span></div>' +
      '<div class="minirow" style="min-height:26px"><span class="d">今天 02:06 · 3 成功</span><span class="v">3.4 s</span></div>' +
      '<div class="minirow" style="min-height:26px"><span class="d">昨天 14:06 · 3 成功</span><span class="v">3.1 s</span></div>' +
      '<button class="btn" style="margin-top:auto">全部更新</button>';
  }
  function renderSubsPages(){
    /* S1 行表 */
    $('#subsBody1').innerHTML =
      '<div class="s1">' +
        '<div class="panel s1usage">' +
          '<div class="ptitle"><span class="g">&#xE9D2;</span>用量<span class="right">本周 53.2 · 本月 118.6 · 累计 412 GB' +
            '<button class="btn sm ghost" id="usageExport">导出 CSV</button></span></div>' +
          '<div class="uchart">' + dayBars(30) + '</div>' +
          '<div style="display:flex;justify-content:space-between;font-size:10.5px;color:var(--NeoText3)">' +
            '<span>近 30 日 · GB / 天</span></div>' +
        '</div>' +
        '<div class="panel planstrip">' +
          '<div class="ps-l"><span class="g">&#xE823;</span><b>更新计划</b>' +
            '<span class="sw' + (PLAN.auto ? ' on' : '') + '" id="planAuto"><b></b></span>' +
            '<span class="ps-t">' + (PLAN.auto ? '自动更新' : '已关闭') + '</span></div>' +
          '<button class="ps-i ps-pick' + (PLAN.auto ? '' : ' off') + '" id="planInterval">' +
            '<b>间隔</b><span>' + planLabel() + '</span><i>▾</i></button>' +
          '<div class="ps-i"><b>上次</b><span>今天 14:06</span></div>' +
          '<div class="ps-i"><b>下次</b><span>' + nextTime() + '</span></div>' +
          '<div class="ps-r"><button class="btn sm ghost" id="planAll">全部更新</button></div>' +
        '</div>' +
        '<div class="panel s1list">' +
          '<div class="ptitle"><span class="g">&#xE8A5;</span>订阅列表<span class="right sub-list-count"></span></div>' +
          '<div class="rows scroll" style="flex:1;gap:6px">' + SUBS.map(function (s, i){ return subRow(s, i); }).join('') + '</div>' +
          '<button class="addrow" style="margin-top:8px">＋ 添加订阅</button>' +
        '</div>' +
        '<div class="panel s1log">' +
          '<div class="ptitle"><span class="g">&#xE81C;</span>订阅日志' +
            '<span class="right"><span class="sublog-summary"></span><button class="btn sm ghost" id="logExport">导出</button></span></div>' +
          '<div class="rows scroll" style="flex:1;gap:6px">' + subLogRows() + '</div>' +
          '<button class="btn ghost" id="nativeFullSublog" style="margin-top:8px">打开完整日志</button>' +
        '</div>' +
      '</div>';

    bindSubs();
    if(NATIVE)nativeBindUsage();
    enhanceSwitches();
    syncSubscriptionUi();
  }

  /* ---------------- 订阅日志（并入订阅页右栏） ---------------- */
  function subLogRows(){
    return SUBLOG.map(function (r, i){
      var ok = r[2] === '成功';
      return '<div class="lgrow" data-log="' + i + '">' +
        '<div class="lgtop"><span class="nm">' + esc(r[1]) + '</span>' +
        '<span class="badge ' + (ok ? 'g' : 'r') + '">' + esc(r[2]) + '</span>' +
        '<span class="lgcaret">&#xE70D</span></div>' +
        '<div class="lgbot"><span>' + esc(r[0]) + '</span><i>·</i><span>' + esc(r[3]) + '</span><i>·</i><span>' + esc(r[4]) + '</span></div>' +
        '<div class="lgdetail"><div><b>返回</b><span>' + (NATIVE?esc(r[5]||'未返回 HTTP 状态'):(ok ? 'HTTP 200' : 'HTTP 403 Forbidden')) + '</span></div>' +
          '<div><b>节点</b><span>' + esc(r[3]) + (NATIVE?'':' · 新增 0 · 删除 0') + '</span></div>' +
          '<div><b>耗时</b><span>' + esc(r[4]) + '</span></div>' +
          '<div><b>来源</b><span class="mono">' + (NATIVE?esc(r[6]||'—'):(i % 2 ? 'subscription.example.invalid' : 'subscription.example.invalid')) + '</span></div></div>' +
        '</div>';
    }).join('');
  }

  /* ---------------- 运行日志 ---------------- */
  var lFilter = 'all', lQuery = '';
  function renderLogs(){
    var host = $('#logList'); host.innerHTML = '';
    var count = 0;
    LOGS.forEach(function (r){
      if (lFilter !== 'all' && r[1] !== lFilter) return;
      if (lQuery && r[2].toLowerCase().indexOf(lQuery) < 0) return;
      count++;
      var el = document.createElement('div');
      el.className = 'lrow';
      el.innerHTML = '<span class="tm">' + r[0] + '</span>' +
        '<span class="lv ' + esc(r[1]) + '">' + ({ info:'信息', warn:'警告', error:'错误', debug:'调试' }[r[1]]) + '</span>' +
        '<span class="msg">' + esc(r[2]) + '</span>';
      host.appendChild(el);
    });
    $('#lCnt').textContent = count + ' 条';
    if(!count)host.innerHTML='<div class="empty-state"><b>没有匹配的记录</b><span>试试其他关键词或日志级别</span></div>';
  }
  $$('#lFilter .chip').forEach(function (c){
    c.addEventListener('click', function (){
      $$('#lFilter .chip').forEach(function (x){ x.classList.toggle('on', x === c); });
      lFilter = c.dataset.l;
      renderLogs();
    });
  });
  $('#lSearch').addEventListener('input', function (e){ lQuery = e.target.value.trim().toLowerCase(); renderLogs(); });

  /* 背景层：只改这一层的变量 */
  var WALLS = { '默认（深空）': 'default', '冷蓝': 'blue', '暖灰': 'warm', '纯色': 'none' };
  function applyWall(title, v){
    var root = document.documentElement;
    if (title === '背景'){
      document.body.dataset.wall = WALLS[v] || 'default';
    } else if (title === '背景模糊'){
      root.style.setProperty('--wall-blur', v === '0' ? '0px' : v.replace(' ', ''));
    } else if (title === '背景暗化'){
      var n = parseFloat(v) / 100;
      root.style.setProperty('--wall-scrim', isNaN(n) ? .5 : n);
    }
  }

  /* ---------------- 设置 ---------------- */
  var SETTINGS = {
    general: { name:'通用', items:[
      ['开机自动启动', '登录 Windows 后自动运行 neo', 'sw', true],
      ['启动后自动连接', '使用上次选中的节点', 'sw', true],
      ['启动自动测速', '启动后测一遍全部节点', 'sw', false],
      ['启动时最小化到托盘', '不弹出主窗口', 'sw', false],
      ['关闭窗口时最小化到托盘', '关闭按钮不退出进程', 'sw', true],
      ['真实 IP 显示', '卡片信息行显示实际出口', 'sw', false],
      ['真连测速', '延迟按真实链路测，而不是 TCP 握手', 'sw', true],
      ['紧凑布局', '减少节点卡片的高度与留白', 'sw', false],
      ['界面语言', '跟随系统或手动指定', 'text', '简体中文']
    ]},
    network: { name:'网络', items:[
      ['SOCKS5 端口', '本地 SOCKS5 监听', 'text', '21808'],
      ['HTTP 端口', '本地 HTTP 监听', 'text', '21809'],
      ['应用端口', '', 'btn', '应用'],
      ['允许来自局域网的连接', '局域网设备共享本机代理', 'sw', false],
      ['自动修复系统代理', '系统代理被改坏时自动恢复', 'sw', true],
      ['自动故障转移', '连续失联时允许切换节点', 'sw', false],
      ['网络检测详情', '查看连通性与各站点结果', 'link', true],
      ['清理内核残留', '检测上次异常退出留下的 TUN 网卡与路由', 'btn', '立即清理']
    ]},
    kernel: { name:'内核', items:[
      ['xray', '本机内置内核 · 25.9.6', 'btn', '检查更新'],
      ['sing-box', '本机内置内核 · TUN 能力', 'btn', '检查更新'],
      ['mihomo', '可选内核 · 未安装（按需下载）', 'btn', '下载'],
      ['naive', '可选内核 · 未安装（辅助进程）', 'btn', '下载'],
      ['分流数据', '决定国内站点直连是否准确', 'btn', '更新数据'],
      ['检查预发布内核', '仅对 xray 生效 · 可能不稳定', 'sw', false],
      ['协议指定内核', '按协议与传输能力选择', 'btn', '配置'],
      ['内核异常退出后自动重启', '连续失败三次后停止', 'sw', true],
      ['恢复安全启动', '恢复被禁用的内核启动项', 'btn', '恢复']
    ]},
    routing: { name:'分流', items:[
      ['国内地址直连', '关闭后所有流量都走节点', 'sw', true],
      ['DNS 服务器', '内置 · 223.5.5.5', 'text', '223.5.5.5'],
      ['分流策略', '由 Neo 维护', 'text', '内置策略'],
      ['自动维护', '更新内置分流数据', 'sw', true],
      ['游戏直连保护', '游戏流量不经节点，避免额外延迟', 'sw', true],
      ['自定义游戏进程', '补充需要直连的游戏进程', 'btn', '编辑'],
      ['用 Rust 生成配置（实验）', '更快生成，行为与默认一致', 'sw', false]
    ]},
    logs: { name:'日志', items:[
      ['日志级别', '写入文件的最低级别', 'text', '警告'],
      ['日志目录', '默认 data\\logs', 'text', '选择'],
      ['日志保留', '超过期限自动清理', 'text', '7 天']
    ]},
    about: { name:'关于', items:[
      ['版本', '当前正式版功能参照', 'text', '0.2.8'],
      ['运行框架', 'Windows x64', 'text', 'WinUI 3 · .NET 10'],
      ['开源许可', '第三方组件许可', 'link', true],
      ['查看日志', '打开 data\\logs', 'link', true],
      ['反馈问题', '在 GitHub 提交 issue', 'link', true],
      ['导出诊断', '打包日志与配置摘要', 'btn', '导出'],
      ['导出配置', '导出 data\\config.json', 'btn', '导出'],
      ['导入配置', '从文件恢复', 'btn', '导入'],
      ['恢复默认', '清空全部设置（保留订阅）', 'btn', '恢复'],
      ['软件更新', '当前版本 0.2.8', 'btn', '检查更新'],
      ['自动检查更新', '启动时检查软件更新', 'sw', true]
    ]}
  };
  var SET_OPTS = {
    '界面语言': ['跟随系统', '简体中文', 'English'],
    '日志级别': ['调试', '信息', '警告', '错误'],
    '日志保留': ['3 天', '7 天', '14 天', '30 天'],
    'DNS 服务器': ['223.5.5.5', '119.29.29.29', '1.1.1.1', '手动输入'],
    '协议指定内核': ['自动', 'xray', 'sing-box', 'mihomo'],
    '分流策略': ['规则', '全局', '直连'],
  };
  var SET_EDIT = ['SOCKS5 端口', 'HTTP 端口'];
  var setGroup = 'general', setQuery = '';

  function ctlOf(item){
    if (item[2] === 'sw') return sw(item[3]);
    if (item[2] === 'link') { var b = document.createElement('button'); b.className = 'btn sm ghost'; b.textContent = '查看'; return b; }
    if (item[2] === 'btn') { var b2 = document.createElement('button'); b2.className = 'btn sm ghost'; b2.textContent = item[3]; b2.setAttribute('aria-label',item[0]+' '+item[3]); return b2; }
    if (item[0]==='分流策略')return textv(item[3]);
    if (SET_OPTS[item[0]]){
      var pb = document.createElement('button');
      pb.className = 'valpick';
      pb.innerHTML = '<span>' + item[3] + '</span><i>▾</i>';
      return pb;
    }
    if (SET_EDIT.indexOf(item[0]) >= 0){
      var eb = document.createElement('button');
      eb.className = 'valpick edit';
      eb.innerHTML = '<span>' + item[3] + '</span><i>▾</i>';
      return eb;
    }
    return textv(item[3]);
  }
  function renderSetIndex(){
    var idx = $('#setIndex');
    idx.innerHTML = '<div class="setpill" id="setPill"></div>';
    Object.keys(SETTINGS).forEach(function (k){
      var g = SETTINGS[k];
      var b = document.createElement('button');
      b.dataset.k = k;
      b.className = (k === setGroup ? 'on' : '');
      b.innerHTML = '<span>' + g.name + '</span><span class="n">' + g.items.length + '</span>';
      b.addEventListener('click', function (){
        if (k === setGroup) return;
        switchSetGroup(k, b.offsetTop + b.offsetHeight / 2);
      });
      idx.appendChild(b);
    });
    moveSetPill();
  }
  function moveSetPill(){
    var pill = $('#setPill'), on = $('#setIndex button.on');
    if (!pill || !on) return;
    pill.style.height = on.offsetHeight + 'px';
    pill.style.transform = 'translateY(' + on.offsetTop + 'px)';
  }
  /* 切换分组：详情卡以被点 tab 的行中线为圆心做圆形扩散 */
  function switchSetGroup(k, fromY){
    var body0 = $('#setBody');
    body0.getAnimations().forEach(function(a){ a.cancel(); });

    setGroup = k;
    $$('#setIndex button').forEach(function (b){ b.classList.toggle('on', b.dataset.k === k); });
    moveSetPill();

    renderSetBody();
    setHintText();

    /* 恢复成"整张详情卡从被点 tab 的位置扩散展开"：动的是卡片本身（背景 + 边框 + 内容一起长出来），
       不是卡片里的内层内容 */
    var body = $('#setBody');
    body.style.clipPath = 'none';
    body.scrollTop = 0;

    var box = body.getBoundingClientRect();
    var idxBox = $('#setIndex').getBoundingClientRect();
    var cy = Math.max(6, Math.min(box.height - 6, idxBox.top + fromY - box.top));
    var maxR = Math.hypot(box.width, Math.max(cy, box.height - cy));

    /* 用 requestAnimationFrame 自己推：不依赖 WAAPI/合成器，任何浏览器都跑，
       也不会被"减少动画"之类的系统开关吃掉时长。720ms，半径线性。 */
    if (body.__raf) cancelAnimationFrame(body.__raf);
    var t0 = performance.now(), dur = 720;
    var step = function (now){
      var t = Math.min(1, (now - t0) / dur);
      var r = maxR * t;
      body.style.clipPath = 'circle(' + r.toFixed(1) + 'px at 0px ' + cy + 'px)';
      if (t < 1){
        body.__raf = requestAnimationFrame(step);
      } else {
        body.style.clipPath = 'none';
        body.__raf = null;
      }
    };
    body.__raf = requestAnimationFrame(step);
    try { console.log('[neo] 设置扩散 rAF 720ms →', k); } catch (e) {}
  }
  function renderSettings(){
    renderSetIndex();
    renderSetBody();
    setHintText();
  }
  function renderSetBody(){
    var body=$('#setBody');body.innerHTML='';body.style.clipPath='none';body.scrollTop=0;
    [setGroup].forEach(function(k){var g=SETTINGS[k],wrap=document.createElement('div');wrap.className='sgroup';var rows=document.createElement('div');rows.className='rows';
      g.items.forEach(function(it){
        var ctl=ctlOf(it),r=rowC(it[0],it[1],ctl);r.dataset.setting=it[0];rows.appendChild(r);
        if(it[2]==='sw'){ctl.setAttribute('role','switch');ctl.tabIndex=0;ctl.setAttribute('aria-label',it[0]);ctl.setAttribute('aria-checked',String(it[3]));ctl.onclick=function(e){e.stopPropagation();it[3]=!it[3];ctl.classList.toggle('on',it[3]);ctl.setAttribute('aria-checked',String(it[3]));if(it[0]==='紧凑布局')document.body.classList.toggle('compact-nodes',it[3]);};}
        else if(it[2]==='btn'||it[2]==='link'){ctl.onclick=function(){settingAction(it,ctl);};}
        else if(SET_EDIT.indexOf(it[0])>=0){ctl.onclick=function(){editPort(it);};}
        else if(SET_OPTS[it[0]]&&it[0]!=='分流策略'){ctl.onclick=function(){openPicker(ctl,it[0],SET_OPTS[it[0]].map(function(v){return{v:v,label:v};}),it[3],function(v){if(v==='手动输入'){editDns(it);}else{it[3]=v;renderSetBody();}});};}
      });wrap.appendChild(rows);body.appendChild(wrap);
    });enhanceSwitches();
    if(window.neoNative)window.neoNative.syncUpdateChecks();
  }
  function editPort(it){var d=showUiDialog(it[0],'<label class="field">端口<input id="portValue" type="number" min="1" max="65535" value="'+it[3]+'"></label><p id="portError" class="form-error" role="alert"></p><div class="dialog-actions"><button class="btn primary" id="portSave">保存</button></div>');$('#portSave',d).onclick=function(){var n=Number($('#portValue',d).value),other=SETTINGS.network.items.find(function(v){return v[0]!==it[0]&&SET_EDIT.indexOf(v[0])>=0;});if(!Number.isInteger(n)||n<1||n>65535){$('#portError',d).textContent='端口范围为 1–65535';return;}if(other&&+other[3]===n){$('#portError',d).textContent='HTTP 与 SOCKS5 端口不能相同';return;}it[3]=String(n);d.close();renderSetBody();};}
  function editDns(it){var d=showUiDialog('自定义 DNS','<label class="field">DNS 服务器<input id="dnsValue" placeholder="IP 地址或 https://…/dns-query" value="'+esc(it[3])+'"></label><div class="dialog-actions"><button class="btn primary" id="dnsSave">保存</button></div>');$('#dnsSave',d).onclick=function(){var v=$('#dnsValue',d).value.trim();if(!v)return;it[3]=v;d.close();renderSetBody();};}
  function settingAction(it,ctl){
    var title=it[0];
    if(title==='网络检测详情'){openDiag(true,ctl);return;}
    if(title==='应用端口'){var ports=SETTINGS.network.items.filter(function(v){return SET_EDIT.indexOf(v[0])>=0;}).map(function(v){return v[3];});$$('.proxyAddr').forEach(function(e){e.textContent='127.0.0.1:'+ports[0];});$$('.localPorts').forEach(function(e){e.textContent=ports.join(' / ');});toast('预览端口已应用');return;}
    if(title==='协议指定内核'){
      var protocols=['VLESS','VMess','Trojan','Shadowsocks','Hysteria2','TUIC','WireGuard','Naive'];
      var d=showUiDialog(title,protocols.map(function(proto){return '<label class="map-row"><span>'+proto+'</span><select class="ui-select" data-proto="'+proto+'">'+['自动','xray','sing-box','mihomo','naive'].map(function(k){return '<option>'+k+'</option>';}).join('')+'</select></label>';}).join('')+'<div class="dialog-actions"><button class="btn primary" id="mapSave">保存</button></div>');
      $$('[data-proto]',d).forEach(function(s){s.value=kernelAssignments[s.dataset.proto]||'自动';});$('#mapSave',d).onclick=function(){$$('[data-proto]',d).forEach(function(s){kernelAssignments[s.dataset.proto]=s.value;});d.close();};return;
    }
    if(title==='自定义游戏进程'){var d=showUiDialog(title,'<label class="field">每行一个进程名<textarea class="ui-textarea" id="gameProcesses" placeholder="例如：cs2.exe">'+esc(gameProcesses)+'</textarea></label><div class="dialog-actions"><button class="btn primary" id="gameSave">保存</button></div>');$('#gameSave',d).onclick=function(){gameProcesses=$('#gameProcesses',d).value.trim();d.close();};return;}
    if(title==='导出配置'){saveText('neo-界面预览配置.json',JSON.stringify({preview:true,settings:SETTINGS,kernels:kernelAssignments},null,2),'application/json');return;}
    if(title==='导出诊断'){saveText('neo-界面预览诊断.txt','UI prototype only\n'+DIAG.map(function(d){return d.name+' '+d.status+' '+routeOf(d);}).join('\n'));return;}
    if(title==='导入配置'){var input=document.createElement('input');input.type='file';input.accept='.json';input.onchange=function(){var f=input.files[0];if(!f)return;f.text().then(function(t){try{var config=JSON.parse(t);if(!config.preview||!config.settings)throw new Error();Object.keys(SETTINGS).forEach(function(g){SETTINGS[g].items.forEach(function(it){var match=config.settings[g]&&config.settings[g].items.find(function(x){return x[0]===it[0];});if(match&&typeof match[3]===typeof it[3])it[3]=match[3];});});renderSettings();toast('已导入预览配置');}catch(e){toast('请选择此界面导出的预览配置');}});};input.click();return;}
    if(title==='恢复默认'){var d=showUiDialog(title,'<p class="dialog-copy">恢复当前预览中的设置，保留订阅列表。</p><div class="dialog-actions"><button class="btn primary" id="restorePreview">恢复</button></div>');$('#restorePreview',d).onclick=function(){SETTINGS=JSON.parse(defaultSettings);document.body.classList.remove('compact-nodes');d.close();renderSettings();};return;}
    if(title==='查看日志'){go('log');return;}
    if(title==='开源许可'){showUiDialog(title,'<div class="detail-grid"><span>界面</span><b>WinUI 3</b><span>运行时</span><b>.NET 10</b><span>内核</span><b>xray · sing-box</b></div><p class="dialog-copy">许可全文由正式客户端随安装包提供。</p>');return;}
    if(title==='反馈问题'){showUiDialog(title,'<label class="field">问题描述<textarea class="ui-textarea" placeholder="描述现象、发生时间及复现步骤"></textarea></label><div class="dialog-actions"><button class="btn primary" data-preview-action="反馈提交">提交</button></div>');return;}
    if(title==='日志目录'){showUiDialog(title,'<label class="field">日志位置<input value="data\\logs" readonly></label>');return;}
    var action=it[3]||'执行';var d=showUiDialog(title,'<p class="dialog-copy">'+esc(it[1])+'</p><div class="operation-status" role="status">等待操作</div><progress class="operation-progress" max="100" value="0" hidden></progress><div class="dialog-actions"><button class="btn primary" id="operationRun">'+esc(action)+'</button></div>');
    $('#operationRun',d).onclick=function(){var b=this,progress=d.querySelector('progress'),status=d.querySelector('.operation-status'),value=0;b.textContent='取消';progress.hidden=false;status.textContent='操作进度预览';var timer=setInterval(function(){if(!d.open){clearInterval(timer);return;}value+=20;progress.value=value;if(value>=100){clearInterval(timer);status.textContent='流程预览结束，未修改系统';b.textContent='关闭';b.onclick=function(){d.close();};}},130);b.onclick=function(){clearInterval(timer);status.textContent='已取消';progress.hidden=true;b.textContent='关闭';b.onclick=function(){d.close();};};};
  }
  function setHintText(){
    if (setQuery){
      var hit = 0;
      Object.keys(SETTINGS).forEach(function (k){
        hit += SETTINGS[k].items.filter(function (it){
          return (it[0] + ' ' + it[1]).toLowerCase().indexOf(setQuery) >= 0;
        }).length;
      });
      $('#setHint').textContent = '搜索「' + setQuery + '」 · ' + hit + ' 项';
    } else {
      $('#setHint').textContent = SETTINGS[setGroup].name + ' · ' + SETTINGS[setGroup].items.length + ' 项';
    }
  }
  var setSearchEl = $('#setSearch');
  if (setSearchEl) setSearchEl.addEventListener('input', function (e){ setQuery = e.target.value.trim().toLowerCase(); renderSettings(); });

  /* ---------------- 诊断抽屉 ---------------- */
  // 与正式版 NetworkDiagnosticTargets.All 对齐；此 HTML 只预览状态，不发起网络探测。
  var DIAG = [["Google", "国外", "https://www.google.com/generate_204"], ["Cloudflare", "国外", "https://cp.cloudflare.com/generate_204"], ["X", "国外", "https://x.com/"], ["Steam 商店", "国外", "https://store.steampowered.com/"], ["Steam 社区", "国外", "https://steamcommunity.com/"], ["百度", "国内", "https://www.baidu.com/"], ["哔哩哔哩", "国内", "https://www.bilibili.com/"], ["腾讯", "国内", "https://www.qq.com/"], ["GitHub", "国外", "https://github.com/"], ["YouTube", "国外", "https://www.youtube.com/"], ["Microsoft", "国外", "https://www.microsoft.com/"], ["维基百科", "国外", "https://www.wikipedia.org/"], ["Apple", "国外", "https://www.apple.com/library/test/success.html"], ["Mozilla", "国外", "https://detectportal.firefox.com/success.txt"], ["Debian", "国外", "https://www.debian.org/"], ["Python", "国外", "https://www.python.org/"], ["阿里云", "国内", "https://www.aliyun.com/"], ["京东", "国内", "https://www.jd.com/"], ["网易", "国内", "https://www.163.com/"], ["必应", "国外", "https://www.bing.com/"]].map(function(t,i){return {name:t[0],group:t[1],url:t[2],ms:48+(i*37)%230,status:'pending'};});
  var diagEpoch=0, diagBusy=false, diagTime='', diagAnim=null, diagOpener=null;
  var diagTextAnimations=[], diagWanted=false, diagVersion=0;
  function routeOf(d){if(NATIVE)return d.route||'未确认';return state.mode==='direct'||(state.mode==='rule'&&d.group==='国内')?'直连':'代理';}
  function resetDiagnostics(){diagEpoch++;diagBusy=false;diagTime='';DIAG.forEach(function(d){d.status='pending';});renderDiag();}
  function renderDiag(){
    var passed=DIAG.filter(function(d){return d.status==='ok';}).length;
    var finished=DIAG.filter(function(d){return d.status!=='pending'&&d.status!=='testing';}).length;
    var pct=finished?Math.round(passed/DIAG.length*100)+'%':'—';
    $('#healthPct').textContent=pct;
    $('#healthPct').className='health-pct '+(finished===DIAG.length?(passed===DIAG.length?'ok':'warn'):'dim');
    $('#healthCount').textContent=diagBusy?'检测中 '+finished+' / '+DIAG.length:finished?passed+' / '+DIAG.length+' 通过':'尚未检测';
    $('#healthSites').innerHTML=DIAG.map(function(d){var status=d.status==='ok'?'通过':d.status==='fail'?'失败':d.status==='testing'?'检测中':'—';return '<div class="hx"><span>'+d.name+'</span><b class="'+(d.status==='ok'?'ok':d.status==='fail'?'bad':'dim')+'">'+status+'</b></div>';}).join('');
    $('#diagScore').textContent=pct;
    $('#diagWhen').textContent=diagBusy?'已完成 '+finished+' / '+DIAG.length:finished?passed+' / '+DIAG.length+' 通过 · '+diagTime:'尚未检测';
    $('#diagExit').textContent=state.conn?'当前出口 · '+currentNode().region+' · '+currentNode().name:'未连接 · 使用当前网络';
    $('#diagList').innerHTML=DIAG.map(function(d){return '<div class="ditem"><div class="diag-name"><b>'+d.name+'</b><span class="route">'+routeOf(d)+'</span><div class="d">'+d.url+'</div></div><div class="diag-result '+(d.status==='ok'?'ok':d.status==='fail'?'bad':'dim')+'">'+(d.status==='ok'?'通过':d.status==='fail'?'连接超时':d.status==='testing'?'检测中':'未检测')+'<small>'+(d.status==='ok'?d.ms+' ms · HTTP '+(d.url.indexOf('generate_204')>=0?'204':'200'):'—')+'</small></div></div>';}).join('');
    ['#diagRun','#healthRun'].forEach(function(sel){var b=$(sel);b.disabled=diagBusy;b.textContent=diagBusy?'检测中…':finished?'重新检测':'检测';});
  }
  function runDiagnostics(){
    if(NATIVE)return host("diagnostics.run");
    if(diagBusy)return;diagBusy=true;var epoch=++diagEpoch;
    DIAG.forEach(function(d,i){d.status='testing';setTimeout(function(){if(epoch!==diagEpoch)return;d.status='ok';if(i===DIAG.length-1){diagBusy=false;diagTime=new Date().toLocaleTimeString('zh-CN',{hour:'2-digit',minute:'2-digit'});}renderDiag();},220+i*65);});renderDiag();
  }
  function diagFrame(rect,radius){
    return {left:rect.left+'px',top:rect.top+'px',width:rect.width+'px',height:rect.height+'px',borderRadius:radius};
  }
  function openDiag(on,anchor){
    var d=$('#diagDrawer'),s=$('#scrim'),visible=d.classList.contains('is-open');
    if(on===diagWanted&&(on||!visible))return;
    if(on&&(!visible||anchor)){
      diagOpener=anchor||document.activeElement;
      if(!diagOpener||diagOpener===document.body||d.contains(diagOpener))diagOpener=$('.btnDiag');
    }
    var source=(diagOpener&&diagOpener.isConnected?diagOpener:$('.btnDiag')).getBoundingClientRect();
    var from=visible?d.getBoundingClientRect():source;
    var radius=visible?getComputedStyle(d).borderRadius:Math.min(source.width,source.height)/2+'px';
    var contents=Array.prototype.slice.call(d.children);
    var opacity=visible?parseFloat(getComputedStyle(contents[0]).opacity):0;
    if(diagAnim){diagAnim.onfinish=null;diagAnim.cancel();}
    diagTextAnimations.forEach(function(a){a.cancel();});diagTextAnimations=[];
    diagWanted=on;var version=++diagVersion;
    d.classList.add('is-open');d.inert=true;d.setAttribute('aria-hidden',on?'false':'true');
    var target=on?d.getBoundingClientRect():source;
    var toRadius=on?'28px':Math.min(source.width,source.height)/2+'px';
    var reduced=matchMedia('(prefers-reduced-motion:reduce)').matches;
    var finish=function(){
      if(version!==diagVersion)return;
      diagAnim=null;
      if(diagWanted){d.inert=false;$('#diagClose').focus({preventScroll:true});}
      else{d.classList.remove('is-open');s.classList.remove('closing');if(diagOpener&&diagOpener.isConnected)diagOpener.focus({preventScroll:true});}
    };
    if(on){s.classList.remove('closing');s.classList.add('on');}
    else{s.classList.remove('on');s.classList.add('closing');}
    contents.forEach(function(e){e.style.opacity=on?'1':'0';});
    if(reduced){finish();return;}
    contents.forEach(function(e){diagTextAnimations.push(e.animate([{opacity:opacity},{opacity:on?1:0}],{delay:on&&opacity<.01?80:0,duration:on?170:70,fill:'backwards',easing:'ease-out'}));});
    var first=diagFrame(from,radius),last=diagFrame(target,toRadius);
    first.right=last.right='auto';first.bottom=last.bottom='auto';
    diagAnim=d.animate([first,last],{duration:on?320:220,delay:!on&&opacity>.01?65:0,fill:'backwards',easing:on?'cubic-bezier(.22,1,.36,1)':'cubic-bezier(.4,0,1,1)'});
    diagAnim.onfinish=finish;
  }
  window.addEventListener('resize',function(){
    if(!diagWanted)return;
    ++diagVersion;if(diagAnim){diagAnim.onfinish=null;diagAnim.cancel();diagAnim=null;}
    diagTextAnimations.forEach(function(a){a.cancel();});diagTextAnimations=[];
    Array.prototype.forEach.call($('#diagDrawer').children,function(e){e.style.opacity='1';});
    $('#diagDrawer').inert=false;
  });
  $('#diagClose').addEventListener('click',function(){openDiag(false);});
  $('#scrim').addEventListener('click',function(e){e.preventDefault();e.stopPropagation();openDiag(false);});
  $('#diagRun').addEventListener('click',runDiagnostics);
  $('#healthRun').addEventListener('click',runDiagnostics);
  document.addEventListener('keydown',function(e){
    var d=$('#diagDrawer');if(!diagWanted||!d.classList.contains('is-open'))return;
    if(e.key==='Escape'){e.preventDefault();openDiag(false);}
    if(e.key==='Tab'&&d.inert){e.preventDefault();return;}
    if(e.key==='Tab'){var items=$$('button:not(:disabled)',d),first=items[0],last=items[items.length-1];if(e.shiftKey&&document.activeElement===first){e.preventDefault();last.focus();}else if(!e.shiftKey&&document.activeElement===last){e.preventDefault();first.focus();}}
  });

  /* ---------------- 导航与外壳 ---------------- */
  var TITLES = { conn:'连接', nodes:'节点', subs:'订阅', sublog:'订阅日志', log:'日志', settings:'设置' };
  var pageAnimations=[];
  document.addEventListener('visibilitychange',function(){if(document.hidden)pageAnimations.forEach(function(a){if(a.playState==='running')a.finish();});});
  function go(page){
    if(NATIVE&&page==='sublog')page='subs';
    if(!TITLES[page])return;
    closeMenu();
    var previous=$('.page.on'),next=$('.page[data-page="'+page+'"]');
    if(!next)return;
    var previousOpacity=previous?getComputedStyle(previous).opacity:'1';
    var previousTransform=previous?getComputedStyle(previous).transform:'none';
    pageAnimations.forEach(function(a){a.onfinish=null;a.cancel();});pageAnimations=[];
    $$('.page').forEach(function(p){p.classList.remove('leaving');});
    $$('.nav[data-page]').forEach(function (b){ b.classList.toggle('on', b.dataset.page === page); });
    $$('.page').forEach(function (p){ p.classList.toggle('on', p.dataset.page === page); });
    $$('.page').forEach(function(p){p.inert=!p.classList.contains('on');});
    $('#navSettings').classList.toggle('on',page==='settings');
    $('#pageTitle').textContent = TITLES[page] || 'neo';
    buildVs(page);
    if (page === 'nodes') applyVariant('nodes', nodeVariant);

    if (page === 'subs') applyVariant('subs', subsVariant);
    if(previous!==next&&!matchMedia('(prefers-reduced-motion:reduce)').matches){
      /* 页面切换：纵向推（实验页第 5 版）
         旧页向上退 14% 并压暗，新页从下方 100% 推上来 */
      if(previous){
        previous.classList.add('leaving');
        var exit=previous.animate(
          [{opacity:previousOpacity,transform:previousTransform},{opacity:.35,transform:'translateY(-14%)'}],
          {duration:300,easing:'cubic-bezier(.32,0,.16,1)',fill:'forwards'});
        exit.onfinish=function(){previous.classList.remove('leaving');};
        pageAnimations.push(exit);
      }
      var enter = next.animate(
        [{opacity:1,transform:'translateY(100%)'},{opacity:1,transform:'translateY(0)'}],
        {duration:340,easing:'cubic-bezier(.32,0,.16,1)',fill:'forwards'});
      pageAnimations.push(enter);
      /* 动画期间关掉磨砂，结束后恢复（避免每帧重算模糊导致的卡顿） */
      document.body.classList.add('page-animating');
      var clearAnim = function(){ document.body.classList.remove('page-animating'); };
      enter.onfinish = clearAnim; enter.oncancel = clearAnim;
    }
    if(page==='subs')requestAnimationFrame(function(){paintSubPill(false);});
    if(page==='subs')growBars('#subsBody1');if(NATIVE&&page==='conn')tick.draw();
  }
  $$('.nav[data-page]').forEach(function (b){ b.addEventListener('click', function (){ go(b.dataset.page); }); });


  /* 变体切换：连接 1/2/3，节点 A/B/C/D */
  function buildVs(page){$('#vsbar').hidden=true;if(page!=='conn')closeNodeSheet();}
  function applyVariant(page, v){
    if (page === 'subs'){
      $$('.sv').forEach(function (p){ p.classList.toggle('on', p.dataset.s === v); });
      $$('#vsbar .vs').forEach(function (x){ x.classList.toggle('on', x.dataset.v === v); });
      return;
    }
    if (page === 'conn'){
      $$('.cv').forEach(function (p){
        var on = true;   /* 现在只有一版 */
        p.classList.toggle('on', on);
        if (on) animate(p, { opacity: [0, 1], transform: ['translateY(6px)', 'translateY(0px)'] },
          { duration: .28, delay: .02, easing: 'ease-out' });
      });
      setTimeout(function (){ tick.draw(); }, 60);
    } else {
      $$('.vp').forEach(function (p){
        var on = p.dataset.v === v;
        p.style.display = on ? (p.dataset.v === 'C' || p.dataset.v === 'D' ? 'grid' : 'flex') : 'none';
        p.classList.toggle('on', on);
        if (on) p.style.gridTemplateColumns = p.dataset.v === 'C' ? '138px minmax(0,1fr)'
          : p.dataset.v === 'D' ? 'minmax(0,1fr) 316px' : '';
      });
    }
    $$('#vsbar .vs').forEach(function (x){ x.classList.toggle('on', x.dataset.v === v); });
  }
  var nodeVariant = 'D', subsVariant = '1';
  $('#vsbar').addEventListener('click', function (e){
    var b = e.target.closest('.vs');
    if (!b) return;
    var page = $('.page.on').dataset.page;
    if (page === 'nodes') nodeVariant = b.dataset.v;
    if (page === 'subs') subsVariant = b.dataset.v;
    applyVariant(page, b.dataset.v);
  });

  /* 侧栏「设置」直接进设置页：分组已经在设置页自己的 tab 里，侧栏不再展开第二套入口 */
  (function (){
    var ns = $('#navSettings');
    if (ns) ns.addEventListener('click', function (){ go('settings'); });
  })();

  document.addEventListener('click', function (e){
    var swEl = e.target.closest('.sw');
    if (swEl && !swEl.dataset.locked){swEl.classList.toggle('on');swEl.setAttribute('aria-checked',String(swEl.classList.contains('on')));}
  });

  /* 通用下拉：锚在元素下方，点外部关闭 */
  var menuEl = null, menuAnchor = null;
  function closeMenu(){
    if (!menuEl) return;
    var m = menuEl; menuEl = null; menuAnchor = null;
    m.style.opacity = '0';
    setTimeout(function (){ m.remove(); }, 140);
  }
  /* 菜单定位：下面放不下就翻到上面 */
  function placeMenu(m, r, wrap){
    var h = m.offsetHeight || 220;
    var spaceBelow = wrap.bottom - r.bottom;
    var top = spaceBelow > h + 12 ? (r.bottom - wrap.top + 6)
                                  : Math.max(6, r.top - wrap.top - h - 6);
    m.style.top = top + 'px';
  }

  function openPicker(anchor, title, items, current, onPick, note){
    var r = anchor.getBoundingClientRect();
    /* 同一个位置再点一次 = 关（用位置比对，比元素身份可靠） */
    if (menuEl && menuEl.__r && Math.abs(menuEl.__r.top - r.top) < 1.5
        && Math.abs(menuEl.__r.left - r.left) < 1.5){
      closeMenu(); return;
    }
    if (menuEl) closeMenu();
    var wrap = $('.content').getBoundingClientRect();
    var m = document.createElement('div');
    m.className = 'popmenu';
    m.style.left = Math.max(4, Math.min(r.left - wrap.left, wrap.width - 240)) + 'px';
    m.innerHTML = '<div class="pm-head">' + title + '</div>' +
      items.map(function (it){
        var on = String(it.v) === String(current);
        return '<button class="pm-item' + (on ? ' on' : '') + '" data-v="' + it.v + '">' +
          '<span>' + it.label + '</span>' + (it.warn ? '<em class="pm-warn">' + it.warn + '</em>' : '') +
          '<i>' + (on ? '&#xE73E' : '') + '</i></button>';
      }).join('') +
      (note ? '<div class="pm-foot">' + note + '</div>' : '');
    m.__r = r;
    $('.content').appendChild(m);
    placeMenu(m, r, wrap);
    menuEl = m; menuAnchor = anchor;
    animate(m, { opacity: [0, 1], transform: ['translateY(-4px)', 'translateY(0px)'] }, { duration: .18, easing: 'ease-out' });
    $$('.pm-item', m).forEach(function (b){
      b.addEventListener('click', function (e){
        e.stopPropagation();
        closeMenu();
        onPick(b.dataset.v, b.textContent.trim());
      });
    });
  }

  /* 更新间隔下拉：锚在按钮上，点外部关闭 */
  var menuEl = null, menuAnchor = null;
  function closeMenu(){
    if (!menuEl) return;
    var m = menuEl; menuEl = null; menuAnchor = null;
    m.style.opacity = '0';
    setTimeout(function (){ m.remove(); }, 140);
  }
  function openIntervalMenu(anchor){
    var items = PLAN_OPTS.map(function (h){
      return { v: h, label: (h >= 24 ? (h / 24) + ' 天' : h + ' 小时'), warn: h <= 6 ? '可能触发风控' : '' };
    }).concat([
      { v: 'manual', label: '手动更新' },
      { v: 'custom', label: '自定义间隔…' }
    ]);
    openPicker(anchor, '自动更新间隔', items, PLAN.hours === null ? 'manual' : PLAN.hours,
      function (v){
        if (v === 'custom'){ openCustomInterval(anchor); return; }
        if(NATIVE){nativeSaveInterval(v);return;}
        PLAN.hours = v === 'manual' ? null : +v;
        renderSubsPages();
        toast(v === 'manual' ? '改为手动更新' : '自动更新间隔改为 ' + planLabel() + ' · 下次 ' + nextTime());
      },
      '间隔过短容易触发订阅服务商的频率限制，建议 12 小时以上');
  }

  /* 自定义：输入小时数，1–720 */
  function openCustomInterval(anchor){
    if (menuEl) closeMenu();
    var r = anchor.getBoundingClientRect();
    var wrap = $('.content').getBoundingClientRect();
    var m = document.createElement('div');
    m.className = 'popmenu';
    m.style.left = Math.max(4, Math.min(r.left - wrap.left, wrap.width - 240)) + 'px';
    var cur = PLAN.hours || 12;
    m.innerHTML = '<div class="pm-head">自定义间隔</div>' +
      '<div class="pm-row"><input id="ivH" type="number" min="1" max="720" step="1" value="' + cur + '"' +
        ' placeholder="12"><span>小时</span></div>' +
      '<button class="pm-apply" id="ivOk">应用</button>' +
      '<div class="pm-foot">1–720 小时。低于 6 小时可能触发订阅服务商的频率限制。</div>';
    m.__r = r;
    m.__r = r;
    $('.content').appendChild(m);
    menuEl = m; menuAnchor = anchor;
    animate(m, { opacity: [0, 1], transform: ['translateY(-4px)', 'translateY(0px)'] }, { duration: .18, easing: 'ease-out' });
    placeMenu(m,r,wrap);
    var inp = $('#ivH', m);
    inp.focus(); inp.select();
    var apply = function (){
      var v = Math.round(parseFloat(inp.value));
      if (!v || v < 1 || v > 720){ toast('请输入 1–720 之间的小时数'); inp.focus(); return; }
      if(NATIVE){nativeSaveInterval(v);closeMenu();return;}
      PLAN.hours = v;
      closeMenu();
      renderSubsPages();
      toast('自动更新间隔改为 ' + planLabel() + (v < 6 ? '（偏短，注意风控）' : '') + ' · 下次 ' + nextTime());
    };
    $('#ivOk', m).addEventListener('click', function (e){ e.stopPropagation(); apply(); });
    inp.addEventListener('keydown', function (e){ if (e.key === 'Enter'){ e.stopPropagation(); apply(); } });
  }

  /* 添加订阅：列表底部虚线行触发内联表单 */
  function openAddForm(editIndex){
    var edit=typeof editIndex==='number',s=edit?SUBS[editIndex]:null;
    var d=showUiDialog(edit?'编辑订阅':'添加订阅','<label class="field">名称<input id="addName" value="'+(s?esc(s.name):'')+'" placeholder="为订阅起个名字" maxlength="50"></label><label class="field">订阅地址<input id="addUrl" type="url" value="'+(s?esc('https://'+s.url):'')+'" placeholder="https://" autocomplete="off" spellcheck="false"></label><p id="addError" class="form-error" role="alert"></p><div class="dialog-actions"><button class="btn primary" id="addOk">'+(edit?'保存':'添加')+'</button></div>');
    $('#addOk',d).onclick=function(){
      var url=$('#addUrl',d).value.trim(),name=$('#addName',d).value.trim();
      try{var parsed=new URL(url);if(!/^https?:$/.test(parsed.protocol)||!parsed.hostname)throw new Error();}catch(e){$('#addError',d).textContent='请输入有效的 HTTP 或 HTTPS 订阅地址';$('#addUrl',d).focus();return;}
      if(!name)name=parsed.hostname;
      if(edit){s.name=name;s.url=url.replace(/^https?:\/\//,'');}else SUBS.push({name:name,url:url.replace(/^https?:\/\//,''),nodes:0,used:0,cap:null,exp:null,state:'待更新',tone:'a',enabled:true,last:'从未更新',cost:'—',regions:'—'});
      d.close();renderSubsPages();toast(edit?'已保存预览中的订阅':'已添加到预览订阅列表');
    };
    $('#addUrl',d).addEventListener('keydown',function(e){if(e.key==='Enter')$('#addOk',d).click();});
  }

  /* 全局：点菜单外关闭（只挂一次） */
  function bindOutsideClose(){
    document.addEventListener('pointerdown', function (e){
      if (!menuEl) return;
      if (menuEl.contains(e.target)) return;
      if (menuAnchor && menuAnchor.contains(e.target)) return;   /* 按钮自己负责开/关 */
      closeMenu();
    }, true);
    document.addEventListener('keydown', function (e){
      if (e.key === 'Escape') closeMenu();
    });
  }
  bindOutsideClose();

  /* ---------------- 启动 ---------------- */
  $$('.ringButton, .bring').forEach(function (b){
    b.addEventListener('click', toggleConn);
    if (b.tagName !== 'BUTTON') b.addEventListener('keydown', function (e){
      if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggleConn(); }
    });
  });
  $$('.toNodes').forEach(function (b){ b.addEventListener('click', function (){ go('nodes'); }); });
  $$('.toLog').forEach(function (b){ b.addEventListener('click', function (){ go('log'); }); });

  $$('.btnDiag').forEach(function (b){ b.addEventListener('click', function (){ openDiag(true); }); });
  $$('.btnCheck').forEach(function (b){ b.addEventListener('click', function (){ openDiag(true); }); });
  $$('.nodePick').forEach(function (el){
    el.addEventListener('click', function (e){
      if (e.target.closest('.sw') || e.target.closest('button')) return;
      var page = $('.page[data-page="conn"]');
      if (page && page.classList.contains('sheet-open')) closeNodeSheet();
      else openNodeSheet();
    });
  });

  /* 收起面板：按钮 / 点空白 / Esc */
  if ($('#sheetClose')) $('#sheetClose').addEventListener('click', function (e){ e.stopPropagation(); closeNodeSheet(); });
  // Consume outside gestures before any underlying control can receive them.
  // Visibility stays visible while closing, so a second click cannot pass through.
  var nodeSheetOutsideGesture = false;
  function dismissNodeSheetOutside(e){
    var outside = !$('#nodeSheet').contains(e.target);
    var visibleOutside = nodeSheetIsVisible() && outside;
    if (e.type === 'pointerdown') nodeSheetOutsideGesture = visibleOutside;
    var consume = visibleOutside || (e.type === 'click' && nodeSheetOutsideGesture);
    if (e.type === 'click') nodeSheetOutsideGesture = false;
    if (!consume) return;
    e.preventDefault();
    e.stopImmediatePropagation();
    if (e.type === 'click') closeNodeSheet();
  }
  document.addEventListener('pointerdown', dismissNodeSheetOutside, true);
  document.addEventListener('pointercancel', function (){ nodeSheetOutsideGesture = false; }, true);
  document.addEventListener('click', dismissNodeSheetOutside, true);
  document.addEventListener('dblclick', dismissNodeSheetOutside, true);
  document.addEventListener('keydown', function (e){
    if (!nodeSheetIsVisible()) return;
    if (e.key === 'Escape') {
      e.preventDefault();
      e.stopImmediatePropagation();
      closeNodeSheet();
    } else if (e.key === 'Tab') {
      var buttons = $$('#nodeSheet button:not([disabled])');
      if (!buttons.length) return;
      var index = buttons.indexOf(document.activeElement);
      var next = e.shiftKey ? (index <= 0 ? buttons.length - 1 : index - 1) : (index + 1) % buttons.length;
      e.preventDefault();
      e.stopImmediatePropagation();
      buttons[next].focus({ preventScroll: true });
    }
  }, true);
  $$('.modeSeg button').forEach(function (b){
    b.addEventListener('click', function (){
      $$('.modeSeg button').forEach(function (x){ x.classList.toggle('on', x.dataset.m === b.dataset.m); });
      state.mode = b.dataset.m;
      var desc = { rule:'国内域名与 IP 直连，其余走节点', global:'所有流量经节点', direct:'全部直连，不使用节点' }[state.mode];
      $$('.modeDesc').forEach(function (d){ d.textContent = desc; });
      resetDiagnostics(); if(state.conn) runDiagnostics();
    });
  });
  ringInit();
  stateTexts();
  paintCurrent();
  setRings(false);
  tick.draw();

  renderNodes();

  renderSubsPages();

  /* 订阅页：启用开关 / 单条更新 / 全部更新 / 添加 / 日志展开 */
  function bindSubs(){
    var body=$('#subsBody1');
    $$('[data-subsw]',body).forEach(function(b){
      b.onclick=function(e){
        e.stopPropagation();
        var i=+b.dataset.subsw, s=SUBS[i], on=(s.enabled!==false);
        s.enabled=!on;                       /* 新状态 */
        b.classList.toggle('on', s.enabled);
        b.setAttribute('aria-checked', String(s.enabled));
        var row=b.closest('.srow');
        row.classList.toggle('off', !s.enabled);
        row.classList.toggle('on', !!(s.primary && s.enabled));
        /* 就地更新两处派生文字，不重建 DOM */
        var cells=row.querySelectorAll('.s-bot .cell');
        if(cells[1]) cells[1].textContent = s.enabled ? s.nodes+' 个节点' : '—';
        var nm=row.querySelector('.nm');
        var dead=nm.querySelector('.badge.r');
        if(!s.enabled && !dead){ var sp=document.createElement('span'); sp.className='badge r'; sp.textContent='已停用'; nm.appendChild(sp); }
        if(s.enabled && dead) dead.remove();
        toast((s.enabled?'已启用 ':'已停用 ')+s.name);
      };
    });
    $$('[data-upd]',body).forEach(function(b){b.onclick=function(){updateSubscription(+b.dataset.upd);};});
    $$('[data-submore]',body).forEach(function(b){b.onclick=function(){var i=+b.dataset.submore;var isPrimary=!!SUBS[i].primary;
      openPicker(b,'订阅管理',[{v:'primary',label:isPrimary?'取消主用':'设为主用'},{v:'edit',label:'编辑订阅'},{v:'copy',label:'复制订阅地址'},{v:'delete',label:'删除订阅'}],'',function(v){
        if(v==='primary'){
          if(SUBS[i].primary){ SUBS[i].primary=false; $$('#subsBody1 .badge.a').forEach(function(b){ b.remove(); }); paintSubPill(true); toast('已取消主用'); }
          else setPrimarySub(i);
          return;
        }
        if(v==='edit')openAddForm(i);else if(v==='copy')copyText('https://'+SUBS[i].url);else{var d=showUiDialog('删除订阅','<p class="dialog-copy">从预览中删除「'+esc(SUBS[i].name)+'」？</p><div class="dialog-actions"><button class="btn primary" id="subDelete">删除</button></div>');$('#subDelete',d).onclick=function(){SUBS.splice(i,1);d.close();renderSubsPages();};}});};});
    $$('.addrow',body).forEach(function(b){b.onclick=function(){openAddForm();};});
    $('#planAuto',body).onclick=function(e){e.stopPropagation();PLAN.auto=!PLAN.auto;renderSubsPages();};
    $('#planInterval',body).onclick=function(){if(PLAN.auto)openIntervalMenu(this);};
    $('#planAll',body).onclick=function(){SUBS.forEach(function(s,i){if(s.enabled)updateSubscription(i);});};
    $('#usageExport',body).onclick=function(){saveText('neo-用量-界面预览.csv',csv([['日期','用量 GB']].concat(DAYS.map(function(v,i){var d=new Date();d.setDate(d.getDate()-29+i);return [d.toLocaleDateString('sv-SE'),v];}))),'text/csv;charset=utf-8');};
    $('#logExport',body).onclick=function(){var e2=$('#slExport'); if(e2) e2.click(); else saveText('neo-订阅日志-界面预览.csv',csv([['时间','订阅','状态','结果','耗时']].concat(visibleSublogs())),'text/csv;charset=utf-8');};
    /* 订阅日志展开：max-height 过渡。用 inline !important 写，绕开样式层叠打架；
       展开状态记在 subLogOpen 里，重绘后按记录恢复 */
    $$('.lgrow',body).forEach(function(row){
      if(NATIVE){nativeBindLogRow(row);return;}
      var det = row.querySelector('.lgdetail');
      if (!det) return;
      var idx = +row.dataset.log;
      var setH = function (open){
        row.classList.toggle('open', open);
        det.style.setProperty('max-height', 'none', 'important');   /* 让高度自己算 */
        det.style.setProperty('height', (open ? det.scrollHeight : 0) + 'px', 'important');
        det.style.setProperty('opacity', open ? '1' : '0', 'important');
      };
      setH(!!subLogOpen[idx]);
      row.onclick = function (){
        var open = !row.classList.contains('open');
        subLogOpen[idx] = open;
        setH(open);
      };
    });
  }
  function updateSubscription(i){
    if(NATIVE)return host("subscription.update",{id:SUBS[i].id});
    var s=SUBS[i];if(!s||s.updating)return;
    s.updating=true;var rec=['刚刚',s.name,'进行中','正在请求','—'];SUBLOG.unshift(rec);renderSublogs();renderSubsPages();
    setTimeout(function(){s.updating=false;rec[2]=s.name.indexOf('试用')>=0?'失败':'成功';rec[3]=rec[2]==='成功'?s.nodes+' 个节点':'订阅返回 403';rec[4]='0.8 s';s.last='刚刚';renderSublogs();renderSubsPages();},800);
  }
  function syncSubscriptionUi(){
    var count=SUBS.reduce(function(a,s){return a+s.nodes;},0);
    $('#sCnt').textContent=SUBS.length+' 个订阅 · '+count+' 个节点';
    $$('.sub-list-count').forEach(function(e){e.textContent=SUBS.length+' 个 · '+count+' 个节点';});
    $$('.nav[data-page="subs"] .nmeta em').forEach(function(e){e.textContent=SUBS.length;});
    $$('.sublog-summary').forEach(function(e){e.textContent=SUBLOG.length+' 次 · '+SUBLOG.filter(function(r){return r[2]==='成功';}).length+' 成功';});
    if(!SUBS.length){$('#subsBody1 .s1list .rows').innerHTML='<div class="empty-state"><b>还没有订阅</b><span>添加订阅后，节点会显示在节点页</span></div>';}
    $$('[data-upd]').forEach(function(b){var s=SUBS[+b.dataset.upd];b.textContent=s.updating?'更新中':'更新';b.disabled=Boolean(s.updating);});
    var sub=SUBS.find(function(s){return s.enabled&&(NATIVE?s.name===currentNode().src:s.name.indexOf(currentNode().src)===0);});
    $$('.subNm').forEach(function(e){e.textContent=sub?sub.name:'暂无对应订阅';});
    $$('.subExp').forEach(function(e){e.textContent=sub&&sub.exp?sub.exp+' 到期':'—';});
    $$('.subUse').forEach(function(e){e.textContent=sub&&sub.cap?sub.used+' / '+sub.cap+' GB':'未提供额度';});
    $$('.subBar').forEach(function(e){e.parentElement.hidden=!(sub&&sub.cap);e.style.width=sub&&sub.cap?Math.min(100,sub.used/sub.cap*100)+'%':'0';});
    if($('#connSubPct'))$('#connSubPct').textContent=sub&&sub.cap?Math.round(sub.used/sub.cap*100)+'%':'—';
    if($('#connSubRemaining'))$('#connSubRemaining').textContent=sub&&sub.cap?(sub.cap-sub.used).toFixed(1)+' GB':'—';
    if($('#connSubUpdated'))$('#connSubUpdated').textContent=sub?sub.last:'—';
    if($('#connSubNodes'))$('#connSubNodes').textContent=sub?sub.nodes+' 个节点':'—';
  }

  renderLogs();


  function esc(v){var e=document.createElement('span');e.textContent=String(v==null?'':v);return e.innerHTML.replace(/"/g,'&quot;').replace(/'/g,'&#39;');}
  /* 记录最后一次指针位置：弹窗圆形扩散要用它当圆心 */
  document.addEventListener('pointerdown', function (e){
    window.__lastClick = { x: e.clientX, y: e.clientY, t: Date.now() };
  }, true);

  function showUiDialog(title,content){
    closeMenu();var prev=$('#uiDialog');if(prev)prev.remove();
    var d=document.createElement('dialog');d.id='uiDialog';d.className='ui-dialog';d.setAttribute('aria-labelledby','uiDialogTitle');
    d.innerHTML='<header><h2 id="uiDialogTitle">'+title+'</h2><button class="btn sm ghost" data-close-dialog aria-label="关闭对话框">关闭</button></header><div class="dialog-body">'+content+'</div><footer class="preview-note">界面预览 · 更改仅保留在当前页面</footer>';
    document.body.appendChild(d);d.showModal();d.querySelector('[data-close-dialog]').onclick=function(){d.close();};
    d.addEventListener('click',function(e){if(e.target===d){var r=d.getBoundingClientRect();if(e.clientX<r.left||e.clientX>r.right||e.clientY<r.top||e.clientY>r.bottom)d.close();}var a=e.target.closest('[data-preview-action]');if(a)toast(a.dataset.previewAction+' · 仅预览，未执行系统操作');});
    d.addEventListener('close',function(){d.remove();});
    /* 进场：从被点按钮位置做圆形扩散（实验页第 5 版） */
    if(!matchMedia('(prefers-reduced-motion:reduce)').matches)queueMicrotask(function(){
      if(!d.isConnected||!d.open)return;
      var r0 = d.getBoundingClientRect();
      var lc = window.__lastClick || { x: r0.left + r0.width/2, y: r0.top + r0.height/2 };
      var ox = Math.max(0, Math.min(r0.width,  lc.x - r0.left));
      var oy = Math.max(0, Math.min(r0.height, lc.y - r0.top));
      var maxR = Math.hypot(Math.max(ox, r0.width - ox), Math.max(oy, r0.height - oy));
      d.animate(
        [ { clipPath: 'circle(0px at ' + ox + 'px ' + oy + 'px)', opacity: .55 },
          { clipPath: 'circle(' + Math.ceil(maxR) + 'px at ' + ox + 'px ' + oy + 'px)', opacity: 1 } ],
        { duration: 460, easing: 'cubic-bezier(.3,0,.2,1)' }
      );
    });
    return d;
  }
  function saveText(name,value,type){if(NATIVE)return host('file.saveText',{name:name.replace('-界面预览',''),text:String(value),type:type||'text/plain'});var a=document.createElement('a'),url=URL.createObjectURL(new Blob(['\ufeff'+value],{type:type||'text/plain;charset=utf-8'}));a.href=url;a.download=name;a.click();setTimeout(function(){URL.revokeObjectURL(url);},1000);}
  function csv(rows){return rows.map(function(r){return r.map(function(c){return '"'+String(c).replace(/"/g,'""')+'"';}).join(',');}).join('\r\n');}
  function copyText(value){if(NATIVE)return host('clipboard.copy',{text:String(value)});if(navigator.clipboard)navigator.clipboard.writeText(value).then(function(){toast('已复制');}).catch(function(){showUiDialog('复制内容','<textarea class="ui-textarea" readonly>'+esc(value)+'</textarea>');});else showUiDialog('复制内容','<textarea class="ui-textarea" readonly>'+esc(value)+'</textarea>');}
  function openNodeImport(){var d=showUiDialog('手动添加节点','<label class="field">分享链接<textarea id="nodeLinks" class="ui-textarea" placeholder="每行一个：vless://、vmess://、ss://、trojan://、hysteria2://"></textarea></label><p id="nodeImportError" class="form-error" role="alert"></p><div class="dialog-actions"><button class="btn primary" id="nodeImportOk">添加</button></div>');$('#nodeImportOk',d).onclick=function(){var lines=$('#nodeLinks',d).value.trim().split(/\s+/).filter(Boolean);if(!lines.length||lines.some(function(v){return !/^(vless|vmess|ss|trojan|hysteria2|hy2):\/\//i.test(v);})){ $('#nodeImportError',d).textContent='请输入支持的节点分享链接';return;}toast('已识别 '+lines.length+' 条分享链接 · 导入流程预览');d.close();};}
  var slQuery='',slFilter='all';
  function visibleSublogs(){return SUBLOG.filter(function(r){return(slFilter==='all'||r[2]===slFilter)&&(!slQuery||r.join(' ').toLowerCase().indexOf(slQuery)>=0);});}
  function renderSublogs(){
    if(NATIVE){nativeSyncSublogs();return;}
    var c0=$('#slCount'), l0=$('#slList');
    if (!c0 || !l0) return;          // 订阅日志页已并进订阅页右栏，独立页不再存在
    var rows=visibleSublogs();c0.textContent=rows.length+' 条';
    l0.innerHTML=rows.map(function(r){var ok=r[2]==='成功';return '<details class="record"><summary><span class="record-time">'+r[0]+'</span><b>'+esc(r[1])+'</b><span class="record-status '+(ok?'ok':r[2]==='失败'?'bad':'dim')+'">'+r[2]+'</span><span class="record-message">'+esc(r[3])+'</span><span class="record-duration">'+r[4]+'</span></summary><div class="record-detail"><span>返回</span><b>'+(ok?'HTTP 200':'HTTP 403 Forbidden')+'</b><span>变更</span><b>'+esc(r[3])+'</b><span>来源</span><b>订阅更新</b></div></details>';}).join('')||'<div class="empty-state"><b>没有更新记录</b><span>更新订阅后，结果会显示在这里</span></div>';
  }
  if ($('#slSearch')) $('#slSearch').addEventListener('input',function(){slQuery=this.value.trim().toLowerCase();renderSublogs();});
  if ($('#slFilter')) $('#slFilter').addEventListener('change',function(){slFilter=this.value;renderSublogs();});
  if ($('#slExport')) $('#slExport').onclick=function(){saveText('neo-订阅日志-界面预览.csv',csv([['时间','订阅','状态','结果','耗时']].concat(visibleSublogs())),'text/csv;charset=utf-8');};
  if ($('#slCopy')) $('#slCopy').onclick=function(){copyText(visibleSublogs().map(function(r){return r.join(' · ');}).join('\n'));};
  if ($('#slMore')) $('#slMore').onclick=function(){openPicker(this,'订阅日志',[{v:'clear',label:'清空记录'}],'',function(){var d=showUiDialog('清空订阅日志','<p class="dialog-copy">清空当前预览中的更新记录？</p><div class="dialog-actions"><button class="btn primary" id="slClear">清空</button></div>');$('#slClear',d).onclick=function(){SUBLOG.length=0;renderSublogs();renderSubsPages();d.close();};});};
  $('#lExport').onclick=function(){saveText('neo-运行日志-界面预览.log',LOGS.filter(function(r){return(lFilter==='all'||r[1]===lFilter)&&(!lQuery||r[2].toLowerCase().indexOf(lQuery)>=0);}).map(function(r){return r.join(' ');}).join('\n'));};
  $('#lMore').onclick=function(){openPicker(this,'运行日志',[{v:'dir',label:'打开日志目录'},{v:'copy',label:'复制记录'},{v:'clear',label:'清空记录'}],'',function(v){if(v==='copy'){copyText($('#logList').innerText);}else if(v==='dir'){showUiDialog('日志目录','<p class="dialog-copy">客户端日志位置</p><input class="ui-input" value="data\\logs" readonly>');}else{var d=showUiDialog('清空运行日志','<p class="dialog-copy">清空当前预览中的运行记录？</p><div class="dialog-actions"><button class="btn primary" id="lClear">清空</button></div>');$('#lClear',d).onclick=function(){LOGS.length=0;renderLogs();d.close();};}});};
  $('#coreDetails').onclick=function(){showUiDialog('内核运行','<div class="detail-grid"><span>版本</span><b>xray 25.9.6</b><span>内存占用</span><b>'+(state.conn?'84 MB':'—')+'</b><span>策略</span><b>单节点</b><span>参与节点</span><b>'+(state.conn?'1':'0')+' / '+NODES.length+'</b><span>本次上传</span><b>'+(state.conn?'12.4 MB':'—')+'</b><span>本次下载</span><b>'+(state.conn?'172.2 MB':'—')+'</b><span>活跃连接</span><b>'+(state.conn?'12':'0')+'</b></div><div class="dialog-actions"><button class="btn ghost" id="coreSettings">内核设置</button><button class="btn primary" id="coreLogs">查看日志</button></div>');$('#coreSettings').onclick=function(){$('#uiDialog').close();go('settings');switchSetGroup('kernel',80);};$('#coreLogs').onclick=function(){$('#uiDialog').close();go('log');};};
  renderSublogs();


  function enhanceSwitches(){
    $$('.sw').forEach(function(el){el.setAttribute('role','switch');el.setAttribute('aria-checked',String(el.classList.contains('on')));if(el.tagName!=='BUTTON')el.tabIndex=0;if(!el.getAttribute('aria-label')){var row=el.closest('.row,.switem,.h1sw,.ps-l');el.setAttribute('aria-label',row?row.innerText.trim().split('\n')[0]:'自动更新');}});
  }
  document.addEventListener('keydown',function(e){if(e.target.matches('.sw:not(button)')&&(e.key==='Enter'||e.key===' ')){e.preventDefault();e.target.click();}});
  var kernelAssignments={},gameProcesses='',defaultSettings=JSON.stringify(SETTINGS);
  $('#connSubUpdate').onclick=function(){var i=SUBS.findIndex(function(s){return s.enabled&&(NATIVE?s.name===currentNode().src:s.name.indexOf(currentNode().src)===0);});if(i>=0)updateSubscription(i);else go('subs');};
  NODES.forEach(function(n){n.server=n.server.replace('neo.im','example.invalid');});
  SUBS.forEach(function(s){s.nodes=NODES.filter(function(n){return s.name.indexOf(n.src)===0;}).length;s.url=s.url.replace(/^[^/]+/,'subscription.example.invalid');});
  renderSubsPages();enhanceSwitches();
  requestAnimationFrame(function(){ paintSubPill(false); });

  renderSettings();
  renderDiag();
  nSelect(0);
  go('conn');

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
      else if(item[2]==='btn'){if(ctl.getAttribute('aria-busy')!=='true'&&ctl.textContent!==String(item[3]))ctl.textContent=item[3];}
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
  window.__app = {go:go,nSelect:nSelect,nodes:NODES,subscriptions:SUBS,state:state,openDiag:openDiag,
    previewDiagnostics:DIAG,renderDiag:renderDiag,settings:SETTINGS,setOptions:SET_OPTS,
    renderSettings:renderSettings,renderSetBody:renderSetBody,renderSubs:renderSubsPages,
    openPicker:openPicker,openIntervalMenu:openIntervalMenu,showDialog:showUiDialog,toast:toast,switchSetGroup:switchSetGroup,
    paintSubPill:paintSubPill,openExitRegionTest:nativeOpenExitTest,openFullSublog:nativeOpenFullSublog,
    applySnapshot:applyNativeSnapshot,currentNode:currentNode,editPort:editPort};
  if(NATIVE)window.neoNative.attach(window.__app);
})();
