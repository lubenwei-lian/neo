/* neo 重构稿 · 节点页 + 外壳交互（motion 14） */
(function () {
  'use strict';
  var animate = Motion.animate;

  /* ---------- 数据（内部自洽：14 节点 / 5 地区 / 3 订阅） ---------- */
  var NODES = [
    { f:'hk', name:'香港 01 · IPLC',        region:'香港',   proto:'VLESS',      trans:'TCP + TLS',  real:32,  tcp:18, on:true },
    { f:'jp', name:'日本东京 02 · 0.1 倍',  region:'日本',   proto:'VLESS',      trans:'TLS',        real:132, tcp:78 },
    { f:'sg', name:'新加坡 03 · NAT',       region:'新加坡', proto:'Hysteria2',  trans:'UDP',        real:58,  tcp:0  },
    { f:'us', name:'美国洛杉矶 04 · CN2',    region:'美国',   proto:'VLESS',      trans:'gRPC',       real:188, tcp:142 },
    { f:'tw', name:'台湾台北 05',           region:'台湾',   proto:'Shadowsocks', trans:'TCP',       real:54,  tcp:31 },
    { f:'kr', name:'韩国首尔 08',           region:'韩国',   proto:'Trojan',     trans:'TLS',        real:78,  tcp:46 },
    { f:'de', name:'德国法兰克福 09',       region:'德国',   proto:'VLESS',      trans:'TCP + TLS',  real:212, tcp:168 },
    { f:'jp', name:'日本大阪 07',           region:'日本',   proto:'VMess',      trans:'WS + TLS',   real:52,  tcp:29 },
    { f:'sg', name:'新加坡 10 · 流媒体',     region:'新加坡', proto:'Hysteria2',  trans:'UDP',        real:66,  tcp:0  },
    { f:'us', name:'美国圣何塞 11',         region:'美国',   proto:'VMess',      trans:'WS + TLS',   real:0,   tcp:0  },
    { f:'hk', name:'香港 06 · IPLC 备用',   region:'香港',   proto:'VLESS',      trans:'REALITY',    real:35,  tcp:19 },
    { f:'tw', name:'台湾新北 12',           region:'台湾',   proto:'Trojan',     trans:'TCP',        real:71,  tcp:40 },
    { f:'kr', name:'韩国釜山 13',           region:'韩国',   proto:'VLESS',      trans:'TCP + TLS',  real:96,  tcp:57 },
    { f:'jp', name:'日本东京 14 · 家宽',    region:'日本',   proto:'Trojan',     trans:'TLS',        real:44,  tcp:24 }
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

  var grid = document.getElementById('nodeGrid');
  var cards = [];
  var selected = 0;
  var filter = 'all';
  var query = '';

  function delayKind(ms) { return ms > 0 ? (ms <= 800 ? 'ok' : ms <= 2000 ? 'warn' : 'bad') : 'dim'; }
  function delayText(ms) { return ms > 0 ? ms + ' ms' : ms === -1 ? '超时' : '未测速'; }

  /* ---------- 延迟数字：圆点 + 乐透轮 + ms ---------- */
  function latBlock(ms) {
    var wrap = document.createElement('div');
    wrap.className = 'lat ' + delayKind(ms);
    var dot = document.createElement('i');
    dot.className = 'dot';
    wrap.appendChild(dot);
    if (ms > 0) {
      var digits = document.createElement('span');
      digits.className = 'digits';
      wrap.appendChild(digits);
      digits.appendChild(digitCells(String(ms), false));
      var unit = document.createElement('span');
      unit.className = 'unit';
      unit.textContent = 'ms';
      wrap.appendChild(unit);
    } else {
      var t = document.createElement('span');
      t.className = 'txt';
      t.textContent = delayText(ms);
      wrap.appendChild(t);
    }
    return wrap;
  }

  function digitCells(str, atZero) {
    var frag = document.createDocumentFragment();
    for (var i = 0; i < str.length; i++) {
      var d = document.createElement('span');
      d.className = 'd';
      var col = document.createElement('span');
      col.className = 'col';
      for (var n = 0; n <= 9; n++) {
        var s = document.createElement('span');
        s.textContent = n;
        col.appendChild(s);
      }
      col.style.transform = 'translateY(' + (atZero ? 0 : -(parseInt(str[i], 10) * 16)) + 'px)';
      d.appendChild(col);
      frag.appendChild(d);
    }
    return frag;
  }

  /* 值变化：逐位滚；位数变化时重建 → 淡入 */
  function setLat(latEl, ms) {
    var kind = delayKind(ms);
    var old = latEl.dataset.ms ? parseInt(latEl.dataset.ms, 10) : 0;
    latEl.dataset.ms = String(ms);
    latEl.className = 'lat ' + kind;
    var digits = latEl.querySelector('.digits');
    var str = String(ms);

    if (ms <= 0) {
      if (digits) { digits.remove(); var u = latEl.querySelector('.unit'); if (u) u.remove(); }
      var t = latEl.querySelector('.txt');
      if (!t) { t = document.createElement('span'); t.className = 'txt'; latEl.appendChild(t); }
      t.textContent = delayText(ms);
      animate(t, { opacity: [0, 1] }, { duration: .14 });
      return;
    }
    var t2 = latEl.querySelector('.txt'); if (t2) t2.remove();

    if (!digits || digits.children.length !== str.length) {
      if (digits) digits.remove();
      digits = document.createElement('span');
      digits.className = 'digits';
      var unit = latEl.querySelector('.unit');
      if (!unit) {
        unit = document.createElement('span');
        unit.className = 'unit';
        unit.textContent = 'ms';
        latEl.appendChild(unit);
      }
      latEl.insertBefore(digits, unit);
      digits.appendChild(digitCells(str, true));
      /* 强制一次布局，让新插入的 0 位成为过渡起点（不用 rAF：后台标签页会被节流） */
      void latEl.offsetWidth;
      for (var k2 = 0; k2 < str.length; k2++)
        digits.children[k2].firstChild.style.transform =
          'translateY(-' + (parseInt(str[k2], 10) * 16) + 'px)';
      return;
    }
    for (var i = 0; i < str.length; i++) {
      var col = digits.children[i].firstChild;
      col.style.transform = 'translateY(-' + (parseInt(str[i], 10) * 16) + 'px)';
    }
  }

  /* ---------- 建卡片 ---------- */
  function build() {
    grid.innerHTML = '';
    cards = [];
    NODES.forEach(function (n, i) {
      var el = document.createElement('div');
      el.className = 'nc';
      el.dataset.i = i;
      el.innerHTML =
        '<div class="r1"><span class="flag">' + (FLAGS[n.f] || '') + '</span>' +
          '<span class="nm">' + n.name + '</span><span class="sel">已选中</span></div>' +
        '<div class="r2"><span class="meta"></span></div>';
      var r2 = el.querySelector('.r2');
      r2.appendChild(latBlock(n.real));
      grid.appendChild(el);
      cards.push(el);

      el.addEventListener('pointerenter', function () {
        if (el.classList.contains('on')) return;
        animate(el, { transform: 'translateY(-2px)' },
          { type: 'spring', stiffness: 380, damping: 30, mass: .6 });
        animate(el, { borderColor: 'rgba(255,255,255,.13)' }, { duration: .16 });
      });
      el.addEventListener('pointerleave', function () {
        if (el.classList.contains('on')) return;
        animate(el, { transform: 'translateY(0px)' },
          { type: 'spring', stiffness: 380, damping: 30, mass: .6 });
        animate(el, { borderColor: 'rgba(255,255,255,.055)' }, { duration: .2 });
      });
      el.addEventListener('click', function (e) {
        if (e.target.closest('.lat')) { testOne(i); return; }
        select(i);
      });
    });
    render();
    entrance();
  }

  function metaOf(n) {
    var s = n.proto + (n.trans ? ' · ' + n.trans : '');
    if (n.real > 0) s += ' · 真连 ' + n.real + 'ms';
    else if (n.real === -1) s += ' · 真连 超时';
    return s;
  }

  function render() {
    var shown = 0;
    cards.forEach(function (el, i) {
      var n = NODES[i];
      var okFilter =
        filter === 'all' ? true :
        filter === 'ok' ? n.real > 0 :
        filter === 'timeout' ? n.real === -1 :
        n.real === 0;
      var okQuery = !query || (n.name + ' ' + n.proto + ' ' + n.trans + ' ' + n.region).toLowerCase().indexOf(query) >= 0;
      var show = okFilter && okQuery;
      el.style.display = show ? '' : 'none';
      if (show) { shown++; el.querySelector('.meta').textContent = metaOf(n); }
    });
    document.getElementById('nodeCount').textContent = shown + ' 个节点 · 5 个地区';

    var okList = NODES.filter(function (n) { return n.real > 0; });
    var avg = okList.length ? Math.round(okList.reduce(function (a, n) { return a + n.real; }, 0) / okList.length) : 0;
    var best = okList.length ? Math.min.apply(null, okList.map(function (n) { return n.real; })) : 0;
    document.getElementById('footTime').textContent = okList.length ? '已测 ' + okList.length + ' / ' + NODES.length : '尚未测速';
    document.getElementById('footAvg').textContent = okList.length ? '· 平均 ' + avg + ' ms · 最快 ' + best + ' ms' : '';
  }

  function entrance() {
    cards.forEach(function (el, i) {
      animate(el, { opacity: [0, 1], transform: ['translateY(6px)', 'translateY(0px)'] },
        { duration: .3, delay: i * 0.022, easing: 'ease-out' });
    });
  }

  /* ---------- 选中 ---------- */
  function select(i) {
    if (i === selected && cards[i].classList.contains('on')) return;
    cards.forEach(function (el, k) {
      var on = k === i;
      var badge = el.querySelector('.sel');
      if (on) {
        el.classList.add('on');
        animate(el, {
          background: 'linear-gradient(180deg, rgba(90,169,255,.20), rgba(90,169,255,.12))',
          borderColor: 'rgba(90,169,255,.30)', transform: 'translateY(0px)'
        }, { duration: .18 });
        animate(badge, { opacity: [0, 1], transform: ['scale(.88)', 'scale(1)'] },
          { type: 'spring', stiffness: 420, damping: 24, mass: .5 });
      } else if (el.classList.contains('on')) {
        el.classList.remove('on');
        animate(el, {
          background: 'linear-gradient(180deg, rgba(255,255,255,.059), rgba(255,255,255,.024))',
          borderColor: 'rgba(255,255,255,.055)'
        }, { duration: .18 });
        animate(badge, { opacity: 0, transform: 'scale(.9)' }, { duration: .1 });
      }
    });
    selected = i;
    document.getElementById('footCenter').textContent = NODES[i].name;
  }

  /* ---------- 单条测速：三点扫描 → 滚出结果 ---------- */
  function testOne(i, done) {
    var el = cards[i], n = NODES[i];
    var lat = el.querySelector('.lat');

    /* 源码 NodeTile.TestAsync：先写「测速中…」（NeoText2），出结果再滚数字 */
    lat.className = 'lat dim';
    lat.dataset.ms = '0';
    var keep = lat.querySelector('.dot').cloneNode();
    lat.innerHTML = '';
    lat.appendChild(keep);
    var busy = document.createElement('span');
    busy.className = 'txt';
    busy.textContent = '测速中…';
    lat.appendChild(busy);
    animate(busy, { opacity: [.35, 1, .35] },
      { duration: 1.1, repeat: Infinity, easing: 'ease-in-out' });

    setTimeout(function () {
      var ms = 26 + Math.round(Math.random() * 240);
      n.real = ms;
      lat.innerHTML = '';
      var dot = document.createElement('i');
      dot.className = 'dot';
      lat.appendChild(dot);
      setLat(lat, ms);
      el.querySelector('.meta').textContent = metaOf(n);
      render();
      done && done();
    }, 560 + Math.random() * 380);
  }

  function testAll() {
    cards.forEach(function (el, i) {
      if (el.style.display === 'none') return;
      setTimeout(function () { testOne(i); }, i * 55);
    });
  }

  /* ---------- 工具栏 ---------- */
  document.getElementById('search').addEventListener('input', function (e) {
    query = e.target.value.trim().toLowerCase();
    render();
  });
  document.querySelectorAll('.chip').forEach(function (c) {
    c.addEventListener('click', function () {
      document.querySelectorAll('.chip').forEach(function (x) { x.classList.toggle('on', x === c); });
      filter = c.dataset.f;
      render();
    });
  });
  document.getElementById('btnTcp').addEventListener('click', testAll);
  document.getElementById('btnReal').addEventListener('click', function () {
    var b = this;
    b.textContent = '停止';
    setTimeout(function () { b.textContent = '真连测速'; }, 1600);
    testAll();
  });

  /* ---------- 导航 ---------- */
  var titles = { conn:'连接', nodes:'节点', subs:'订阅', sublog:'订阅日志', log:'日志' };
  document.querySelectorAll('.nav[data-page]').forEach(function (btn) {
    btn.addEventListener('click', function () {
      var page = btn.dataset.page;
      document.querySelectorAll('.nav[data-page]').forEach(function (x) { x.classList.toggle('on', x === btn); });
      document.getElementById('pageTitle').textContent = titles[page] || 'neo';
      document.querySelectorAll('.page').forEach(function (p) {
        var target = p.dataset.page === (page === 'nodes' ? 'nodes' : 'wip');
        p.classList.toggle('on', target);
      });
    });
  });

  /* ---------- 设置分组：弹簧展开 ---------- */
  var navSet = document.getElementById('navSettings');
  var reveal = document.getElementById('setReveal');
  var setOpen = false;
  /* 源码 SidebarReveal：220ms bezier(.16,.84)(.24,1)，clip + 位移 + 淡入。
     motion 的 animate 不写 height（只走 transform/opacity），高度这段用 WAAPI。 */
  var revealAnim = null;
  navSet.addEventListener('click', function () {
    setOpen = !setOpen;
    navSet.classList.toggle('open', setOpen);
    var inner = reveal.firstElementChild;
    var from = reveal.getBoundingClientRect().height;
    var to = setOpen ? inner.getBoundingClientRect().height + 4 : 0;
    if (revealAnim) revealAnim.cancel();
    reveal.style.height = from + 'px';
    revealAnim = reveal.animate(
      [{ height: from + 'px' }, { height: to + 'px' }],
      { duration: 220, easing: 'cubic-bezier(.16,.84,.24,1)', fill: 'forwards' }
    );
    revealAnim.onfinish = function () {
      reveal.style.height = to + 'px';
      revealAnim.cancel();
      revealAnim = null;
    };
    animate(inner, { opacity: setOpen ? [0, 1] : [1, 0] }, { duration: setOpen ? .18 : .12, delay: setOpen ? .06 : 0 });
  });

  build();
  select(0);
  window.__neo = { select: select, testOne: testOne, render: render, nodes: NODES };
})();
