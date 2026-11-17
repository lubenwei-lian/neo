/* 节点页四版 · 交互与动效（motion 14） */
(function () {
  'use strict';
  var animate = Motion.animate;

  var NODES = [
    { f:'hk', name:'香港 01 · IPLC',       region:'香港',   proto:'VLESS',       trans:'TCP + TLS', real:32,  tcp:18, server:'hk01.neo.im',   port:443,  src:'机场 A' },
    { f:'jp', name:'日本东京 02 · 0.1 倍', region:'日本',   proto:'VLESS',       trans:'TLS',       real:132, tcp:78, server:'jp02.neo.im',   port:443,  src:'机场 A' },
    { f:'sg', name:'新加坡 03 · NAT',      region:'新加坡', proto:'Hysteria2',   trans:'UDP',       real:58,  tcp:0,  server:'sg03.neo.im',   port:8443, src:'机场 A' },
    { f:'us', name:'美国洛杉矶 04 · CN2',   region:'美国',   proto:'VLESS',       trans:'gRPC',      real:188, tcp:142,server:'us04.neo.im',   port:443,  src:'机场 B' },
    { f:'tw', name:'台湾台北 05',          region:'台湾',   proto:'Shadowsocks', trans:'TCP',       real:54,  tcp:31, server:'tw05.neo.im',   port:8388, src:'机场 B' },
    { f:'kr', name:'韩国首尔 08',          region:'韩国',   proto:'Trojan',      trans:'TLS',       real:78,  tcp:46, server:'kr08.neo.im',   port:443,  src:'机场 B' },
    { f:'de', name:'德国法兰克福 09',      region:'德国',   proto:'VLESS',       trans:'TCP + TLS', real:212, tcp:168,server:'de09.neo.im',   port:443,  src:'机场 C' },
    { f:'jp', name:'日本大阪 07',          region:'日本',   proto:'VMess',       trans:'WS + TLS',  real:52,  tcp:29, server:'jp07.neo.im',   port:443,  src:'机场 A' },
    { f:'sg', name:'新加坡 10 · 流媒体',    region:'新加坡', proto:'Hysteria2',   trans:'UDP',       real:66,  tcp:0,  server:'sg10.neo.im',   port:8443, src:'机场 C' },
    { f:'us', name:'美国圣何塞 11',        region:'美国',   proto:'VMess',       trans:'WS + TLS',  real:0,   tcp:0,  server:'us11.neo.im',   port:443,  src:'机场 C' },
    { f:'hk', name:'香港 06 · IPLC 备用',  region:'香港',   proto:'VLESS',       trans:'REALITY',   real:35,  tcp:19, server:'hk06.neo.im',   port:443,  src:'机场 A' },
    { f:'tw', name:'台湾新北 12',          region:'台湾',   proto:'Trojan',      trans:'TCP',       real:71,  tcp:40, server:'tw12.neo.im',   port:443,  src:'机场 B' },
    { f:'kr', name:'韩国釜山 13',          region:'韩国',   proto:'VLESS',       trans:'TCP + TLS', real:96,  tcp:57, server:'kr13.neo.im',   port:443,  src:'机场 C' },
    { f:'jp', name:'日本东京 14 · 家宽',   region:'日本',   proto:'Trojan',      trans:'TLS',       real:44,  tcp:24, server:'jp14.neo.im',   port:443,  src:'机场 A' }
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

  var selected = 0, filter = 'all', query = '', region = 'all';
  var els = { A:[], C:[], B:[], D:[] };

  function kind(ms){ return ms > 0 ? (ms <= 800 ? 'ok' : ms <= 2000 ? 'warn' : 'bad') : 'dim'; }
  function dtext(ms){ return ms > 0 ? ms + ' ms' : ms === -1 ? '超时' : '未测速'; }

  function cells(str, atZero){
    var frag = document.createDocumentFragment();
    for (var i = 0; i < str.length; i++){
      var d = document.createElement('span'); d.className = 'd';
      var col = document.createElement('span'); col.className = 'col';
      for (var n = 0; n <= 9; n++){ var s = document.createElement('span'); s.textContent = n; col.appendChild(s); }
      col.style.transform = 'translateY(' + (atZero ? 0 : -(parseInt(str[i],10) * 16)) + 'px)';
      d.appendChild(col); frag.appendChild(d);
    }
    return frag;
  }

  function latNode(ms){
    var w = document.createElement('div'); w.className = 'lat ' + kind(ms);
    var dot = document.createElement('i'); dot.className = 'dot'; w.appendChild(dot);
    if (ms > 0){
      var dg = document.createElement('span'); dg.className = 'digits'; dg.appendChild(cells(String(ms), false)); w.appendChild(dg);
      var u = document.createElement('span'); u.className = 'unit'; u.textContent = 'ms'; w.appendChild(u);
    } else {
      var t = document.createElement('span'); t.className = 'txt'; t.textContent = dtext(ms); w.appendChild(t);
    }
    return w;
  }

  /* 只改这一格：滚位 / 换字 */
  function setLat(lat, ms){
    lat.className = 'lat ' + kind(ms);
    var str = String(ms);
    var dg = lat.querySelector('.digits');
    if (ms <= 0){
      if (dg) dg.remove();
      var u0 = lat.querySelector('.unit'); if (u0) u0.remove();
      var t = lat.querySelector('.txt');
      if (!t){ t = document.createElement('span'); t.className = 'txt'; lat.appendChild(t); }
      t.textContent = dtext(ms);
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
        dg.children[k].firstChild.style.transform = 'translateY(-' + (parseInt(str[k],10) * 16) + 'px)';
      return;
    }
    for (var i = 0; i < str.length; i++)
      dg.children[i].firstChild.style.transform = 'translateY(-' + (parseInt(str[i],10) * 16) + 'px)';
  }

  /* ---------- 选中 ---------- */
  function paint(list, i, idleBg, onBg){
    list.forEach(function (el, k){
      var on = k === i;
      if (on === el.classList.contains('on')) return;
      el.classList.toggle('on', on);
      animate(el, { background: on ? onBg : idleBg, borderColor: on ? 'rgba(90,169,255,.30)' : 'rgba(255,255,255,.055)' },
        { duration: .18 });
    });
  }

  function select(i){
    selected = i;
    paint(els.A, i,
      'linear-gradient(180deg, rgba(255,255,255,.045), rgba(255,255,255,.018))',
      'linear-gradient(180deg, rgba(90,169,255,.16), rgba(90,169,255,.09))');
    paint(els.C, i,
      'linear-gradient(180deg, rgba(255,255,255,.045), rgba(255,255,255,.018))',
      'linear-gradient(180deg, rgba(90,169,255,.16), rgba(90,169,255,.09))');
    paint(els.B, i,
      'rgba(255,255,255,.03)',
      'linear-gradient(180deg, rgba(90,169,255,.16), rgba(90,169,255,.09))');
    paint(els.D, i,
      'linear-gradient(180deg, rgba(255,255,255,.045), rgba(255,255,255,.018))',
      'linear-gradient(180deg, rgba(90,169,255,.16), rgba(90,169,255,.09))');
    document.getElementById('footCenter').textContent = NODES[i].name;
    if (document.body.dataset.v === 'D') fillDrawer(i);
  }

  /* ---------- A / C 行 ---------- */
  function row(n, i, showProto){
    var r = document.createElement('div');
    r.className = 'r';
    r.innerHTML = '<span class="flag">' + (FLAGS[n.f] || '') + '</span>' +
      '<div class="mid"><div class="nm">' + n.name + '</div>' +
      (showProto ? '<div class="pr">' + n.proto + (n.trans ? ' · ' + n.trans : '') + '</div>' : '') + '</div>';
    var lat = latNode(n.real);
    r.appendChild(lat);
    var act = document.createElement('button');
    act.className = 'act'; act.innerHTML = '&#xE9D9;'; act.title = '测速';
    r.appendChild(act);

    r.addEventListener('pointerenter', function () {
      if (r.classList.contains('on')) return;
      animate(r, { transform: 'translateY(-1.5px)' }, { type: 'spring', stiffness: 380, damping: 30, mass: .6 });
      animate(r, { borderColor: 'rgba(255,255,255,.12)' }, { duration: .16 });
    });
    r.addEventListener('pointerleave', function () {
      if (r.classList.contains('on')) return;
      animate(r, { transform: 'translateY(0px)' }, { type: 'spring', stiffness: 380, damping: 30, mass: .6 });
      animate(r, { borderColor: 'rgba(255,255,255,.055)' }, { duration: .2 });
    });
    r.addEventListener('click', function (e) {
      if (e.target.closest('.act')) { e.stopPropagation(); test(i, lat); return; }
      select(i);
    });
    r.__lat = lat;
    return r;
  }

  /* ---------- B 分组 ---------- */
  function buildB(){
    var host = document.getElementById('listB');
    host.innerHTML = '';
    els.B = [];
    var order = [], map = {};
    NODES.forEach(function (n, i){
      if (!map[n.region]){ map[n.region] = []; order.push(n.region); }
      map[n.region].push(i);
    });
    order.forEach(function (region){
      var idx = map[region];
      var g = document.createElement('div');
      g.className = 'grp';
      var ok = idx.filter(function (i){ return NODES[i].real > 0; });
      var avg = ok.length ? Math.round(ok.reduce(function (a,i){ return a + NODES[i].real; }, 0) / ok.length) : 0;
      g.innerHTML = '<div class="gh"><b>' + region + '</b><em>' + idx.length + ' 个节点</em>' +
        '<span class="gavg">' + (avg ? '平均 ' + avg + ' ms' : '未测速') + '</span></div>';
      var rows = document.createElement('div');
      rows.className = 'grows';
      idx.forEach(function (i){
        var n = NODES[i];
        var g2 = document.createElement('div');
        g2.className = 'grow';
        g2.innerHTML = '<span class="flag">' + (FLAGS[n.f] || '') + '</span><span class="nm">' + n.name + '</span>';
        var lat = latNode(n.real);
        g2.appendChild(lat);
        g2.__lat = lat;
        g2.addEventListener('pointerenter', function () {
          if (!g2.classList.contains('on')) animate(g2, { borderColor: 'rgba(255,255,255,.12)' }, { duration: .16 });
        });
        g2.addEventListener('pointerleave', function () {
          if (!g2.classList.contains('on')) animate(g2, { borderColor: 'rgba(255,255,255,.04)' }, { duration: .2 });
        });
        g2.addEventListener('click', function () { select(i); });
        rows.appendChild(g2);
        els.B[i] = g2;
      });
      g.appendChild(rows);
      host.appendChild(g);
    });
  }

  /* ---------- C 地区索引 ---------- */
  function buildC(){
    var host = document.getElementById('regionsC');
    host.innerHTML = '';
    var counts = {};
    NODES.forEach(function (n){ counts[n.region] = (counts[n.region] || 0) + 1; });
    var rowsData = [['all', '全部', NODES.length]].concat(Object.keys(counts).map(function (r){ return [r, r, counts[r]]; }));
    rowsData.forEach(function (item){
      var b = document.createElement('button');
      b.className = 'rg' + (item[0] === region ? ' on' : '');
      b.innerHTML = '<span>' + item[1] + '</span><em>' + item[2] + '</em>';
      b.addEventListener('click', function (){
        region = item[0];
        Array.prototype.forEach.call(host.children, function (x){ x.classList.toggle('on', x === b); });
        renderC();
      });
      host.appendChild(b);
    });
  }

  function renderC(){
    var host = document.getElementById('listC');
    host.innerHTML = '';
    els.C = [];
    NODES.forEach(function (n, i){
      if (region !== 'all' && n.region !== region) return;
      if (!pass(n)) return;
      var r = row(n, i, false);
      host.appendChild(r);
      els.C[i] = r;
    });
    paint(els.C, selected,
      'linear-gradient(180deg, rgba(255,255,255,.045), rgba(255,255,255,.018))',
      'linear-gradient(180deg, rgba(90,169,255,.16), rgba(90,169,255,.09))');
  }

  /* ---------- D 网格 + 详情 ---------- */
  function buildD(){
    var host = document.getElementById('listD');
    host.innerHTML = '';
    els.D = [];
    NODES.forEach(function (n, i){
      var c = document.createElement('div');
      c.className = 'mini';
      c.innerHTML = '<div class="m1"><span class="flag">' + (FLAGS[n.f] || '') + '</span><span class="nm">' + n.name + '</span></div>';
      var m2 = document.createElement('div'); m2.className = 'm2';
      var pr = document.createElement('span'); pr.className = 'pr'; pr.textContent = n.proto;
      var lat = latNode(n.real);
      m2.appendChild(pr); m2.appendChild(lat);
      c.appendChild(m2);
      c.__lat = lat;
      c.addEventListener('click', function (){ select(i); });
      host.appendChild(c);
      els.D[i] = c;
    });
    fillDrawer(selected);
  }

  function fillDrawer(i){
    var n = NODES[i];
    var d = document.getElementById('drawer');
    d.innerHTML =
      '<div class="dh"><span class="flag">' + (FLAGS[n.f] || '') + '</span>' +
        '<div><div class="nm">' + n.name + '</div><div class="sub">' + n.region + ' · ' + n.proto + (n.trans ? ' · ' + n.trans : '') + '</div></div></div>' +
      '<div class="dkv">' +
        '<div class="kv"><b>服务器</b><span class="mono">' + n.server + ':' + n.port + '</span></div>' +
        '<div class="kv"><b>真实出口</b><span class="mono">' + (n.real > 0 ? n.region + ' · 与标记一致' : '未检测') + '</span></div>' +
        '<div class="kv"><b>真连延迟</b><span>' + (n.real > 0 ? n.real + ' ms' : '未测速') + '</span></div>' +
        '<div class="kv"><b>TCP 握手</b><span>' + (n.tcp > 0 ? n.tcp + ' ms' : '跳过（UDP 无 TCP 端口）') + '</span></div>' +
        '<div class="kv"><b>来源</b><span>' + n.src + '</span></div>' +
      '</div>' +
      '<div class="dact"><button class="btn">测速</button><button class="btn primary">连接</button></div>';

    /* 内容错位入场：只在新选中时播 */
    Array.prototype.forEach.call(d.querySelectorAll('.kv'), function (el, k){
      animate(el, { opacity: [0, 1], transform: ['translateY(4px)', 'translateY(0px)'] },
        { duration: .26, delay: .02 * k, easing: 'ease-out' });
    });
  }

  /* ---------- 过滤 ---------- */
  function pass(n){
    if (filter === 'ok' && !(n.real > 0)) return false;
    if (filter === 'timeout' && n.real !== -1) return false;
    if (filter === 'untested' && n.real !== 0) return false;
    if (query && (n.name + ' ' + n.proto + ' ' + n.trans + ' ' + n.region).toLowerCase().indexOf(query) < 0) return false;
    return true;
  }

  function renderA(){
    var host = document.getElementById('listA');
    host.innerHTML = '';
    els.A = [];
    NODES.forEach(function (n, i){
      if (!pass(n)) return;
      var r = row(n, i, true);
      host.appendChild(r);
      els.A[i] = r;
    });
    paint(els.A, selected,
      'linear-gradient(180deg, rgba(255,255,255,.045), rgba(255,255,255,.018))',
      'linear-gradient(180deg, rgba(90,169,255,.16), rgba(90,169,255,.09))');
    entrance(els.A);
  }

  function entrance(list){
    list.forEach(function (el, i){
      if (!el) return;
      animate(el, { opacity: [0, 1], transform: ['translateY(5px)', 'translateY(0px)'] },
        { duration: .28, delay: Math.min(i, 12) * .018, easing: 'ease-out' });
    });
  }

  function renderAll(){ renderA(); renderC(); }

  function footer(){
    var ok = NODES.filter(function (n){ return n.real > 0; });
    var avg = ok.length ? Math.round(ok.reduce(function (a, n){ return a + n.real; }, 0) / ok.length) : 0;
    var best = ok.length ? Math.min.apply(null, ok.map(function (n){ return n.real; })) : 0;
    document.getElementById('footTime').textContent = ok.length ? '已测 ' + ok.length + ' / ' + NODES.length : '尚未测速';
    document.getElementById('footAvg').textContent = ok.length ? '· 平均 ' + avg + ' ms · 最快 ' + best + ' ms' : '';
  }

  /* ---------- 测速 ---------- */
  function test(i, lat){
    var n = NODES[i];
    lat.className = 'lat dim';
    lat.innerHTML = '<i class="dot"></i>';
    var busy = document.createElement('span'); busy.className = 'txt'; busy.textContent = '测速中…';
    lat.appendChild(busy);
    animate(busy, { opacity: [.35, 1, .35] }, { duration: 1.1, repeat: Infinity, easing: 'ease-in-out' });
    setTimeout(function (){
      var ms = 26 + Math.round(Math.random() * 240);
      n.real = ms;
      lat.innerHTML = '<i class="dot"></i>';
      setLat(lat, ms);
      /* 四版里同一个节点的延迟一起更新 */
      ['A','C'].forEach(function (v){ var el = els[v][i]; if (el && el.__lat && el.__lat !== lat) setLat(el.__lat, ms); });
      if (els.B[i] && els.B[i].__lat !== lat) setLat(els.B[i].__lat, ms);
      if (els.D[i] && els.D[i].__lat !== lat) setLat(els.D[i].__lat, ms);
      if (document.body.dataset.v === 'D') fillDrawer(i);
      footer();
    }, 560 + Math.random() * 380);
  }

  function testAll(){
    ['A','C'].forEach(function (v){
      els[v].forEach(function (el, i){
        if (el && el.__lat) setTimeout(function (){ test(i, el.__lat); }, i * 45);
      });
    });
  }

  /* ---------- 变体切换 ---------- */
  function setVariant(v){
    document.body.dataset.v = v;
    Array.prototype.forEach.call(document.querySelectorAll('.vs'), function (b){ b.classList.toggle('on', b.dataset.v === v); });
    Array.prototype.forEach.call(document.querySelectorAll('.vp'), function (p){ p.classList.toggle('on', p.dataset.v === v); });
    var hints = {
      A:'点击一行切换 · 行尾悬停出现测速',
      B:'按地区分组，点条目切换',
      C:'左侧地区常驻索引，右侧行可扫',
      D:'小卡只留名称与延迟，右侧看详情'
    };
    document.getElementById('vhint').textContent = hints[v];
    /* 切过去时把内容错位重播一次 */
    var host = document.querySelector('.vp.on');
    var firsts = host.querySelectorAll('.r, .grow, .mini, .grp');
    Array.prototype.forEach.call(firsts, function (el, i){
      animate(el, { opacity: [0, 1], transform: ['translateY(5px)', 'translateY(0px)'] },
        { duration: .26, delay: Math.min(i, 14) * .016, easing: 'ease-out' });
    });
  }

  document.getElementById('vsbar').addEventListener('click', function (e){
    var b = e.target.closest('.vs');
    if (b) setVariant(b.dataset.v);
  });

  /* ---------- 工具栏 ---------- */
  document.getElementById('search').addEventListener('input', function (e){
    query = e.target.value.trim().toLowerCase();
    renderA(); renderC();
  });
  Array.prototype.forEach.call(document.querySelectorAll('.chip'), function (c){
    c.addEventListener('click', function (){
      Array.prototype.forEach.call(document.querySelectorAll('.chip'), function (x){ x.classList.toggle('on', x === c); });
      filter = c.dataset.f;
      renderA(); renderC();
    });
  });
  document.getElementById('btnTcp').addEventListener('click', testAll);
  document.getElementById('btnReal').addEventListener('click', function (){
    var b = this; b.textContent = '停止';
    setTimeout(function (){ b.textContent = '真连测速'; }, 1600);
    testAll();
  });

  /* ---------- 侧栏设置展开（220ms，源码 SidebarReveal 的曲线） ---------- */
  var navSet = document.getElementById('navSettings');
  var reveal = document.getElementById('setReveal');
  var open = false, anim = null;
  navSet.addEventListener('click', function (){
    open = !open;
    navSet.classList.toggle('open', open);
    var inner = reveal.firstElementChild;
    var from = reveal.getBoundingClientRect().height;
    var to = open ? inner.getBoundingClientRect().height + 4 : 0;
    if (anim) anim.cancel();
    reveal.style.height = from + 'px';
    anim = reveal.animate([{ height: from + 'px' }, { height: to + 'px' }],
      { duration: 220, easing: 'cubic-bezier(.16,.84,.24,1)', fill: 'forwards' });
    anim.onfinish = function (){ reveal.style.height = to + 'px'; anim.cancel(); anim = null; };
    animate(inner, { opacity: open ? [0, 1] : [1, 0] }, { duration: open ? .18 : .12, delay: open ? .06 : 0 });
  });

  /* ---------- 启动 ---------- */
  renderA();
  buildB();
  buildC();
  renderC();
  buildD();
  select(0);
  footer();
  setVariant('A');

  window.__v2 = { setVariant: setVariant, select: select, test: test, nodes: NODES, els: els };
})();
