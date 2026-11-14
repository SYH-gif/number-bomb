/* =========================================================
   admin.js —— 后台：看数、看房、改分、删号
   所有数据都走 /admin/api/*；没票的时候那边一律 404，
   所以这里只要收到 404 就直接回登录界面。
   ========================================================= */
(function () {
  'use strict';

  const $ = (sel, root = document) => root.querySelector(sel);

  const S = {
    skip: 0,
    take: 25,
    q: '',
    total: 0,
    users: []
  };

  // 是否已经拿着有效的票。页面加载瞬间的 404 和"登录成功后迟到的 404"
  // 都会走同一段代码，靠这个标记分清谁该弹登录框。
  let authed = false;

  // ------------------------------------------------------------ 基础

  const esc = (v) => String(v ?? '').replace(/[&<>"']/g, (c) => (
    { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]
  ));

  async function api(path, options = {}) {
    const opts = {
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      ...options
    };
    if (opts.body && typeof opts.body !== 'string') opts.body = JSON.stringify(opts.body);

    const res = await fetch(path, opts);
    let data = null;
    try { data = await res.json(); } catch (_) { /* 可能没有 body */ }

    if (res.status === 404) {
      const err = new Error('not-found');
      err.unauthorized = true;
      throw err;
    }
    return { ok: res.ok, status: res.status, data };
  }

  function toast(text) {
    const wrap = $('#toastWrap');
    const el = document.createElement('div');
    el.className = 'toast';
    el.textContent = text;
    wrap.appendChild(el);
    setTimeout(() => el.remove(), 2800);
  }

  function showGate() {
    if (authed) return;                 // 已经进来了，别把人再拽回登录页
    $('#dash').hidden = true;
    $('#gate').hidden = false;
    setTimeout(() => $('#keyInput').focus(), 120);
  }

  // 票失效 / 还没登录：先承认没登录，再回登录界面
  function handleUnauthorized() {
    authed = false;
    showGate();
  }

  function showDash() {
    $('#gate').hidden = true;
    $('#dash').hidden = false;
  }

  // 数据库里存的是 UTC，这里换成本机时间显示
  function fmtTime(raw) {
    if (!raw) return '—';
    const iso = raw.replace(' ', 'T') + 'Z';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return raw;
    const p = (n) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
  }

  // ------------------------------------------------------------ 渲染

  async function loadOverview() {
    const r = await api('/admin/api/overview');
    const s = r.data.stats;

    $('#stats').innerHTML = [
      { n: s.total, label: '账号' },
      { n: s.points, label: '积分总和', accent: true },
      { n: s.newToday, label: '今天新来' },
      { n: s.customAvatars, label: '自画头像' },
      { n: s.rooms, label: '房间' },
      { n: s.playing, label: '正在玩' }
    ].map((it) => `
      <div class="stat">
        <div class="stat-num ${it.accent ? 'accent' : ''}">${it.n}</div>
        <div class="stat-label">${it.label}</div>
      </div>`).join('');

    const rooms = r.data.rooms || [];
    $('#rooms').innerHTML = rooms.length
      ? rooms.map((room) => {
        const names = room.players.map((p) => p.isBot ? `${esc(p.name)}（机器人）` : esc(p.name));
        return `
          <div class="room">
            <span class="room-code">${esc(room.code)}</span>
            <span class="room-state ${esc(room.state)}">${stateText(room.state)}</span>
            <span class="room-players">${room.humans} 人${room.bots ? ` + ${room.bots} 个机器人` : ''} · ${names.join('、')}</span>
          </div>`;
      }).join('')
      : '<div class="room-empty">现在没人开局，大厅空着。</div>';
  }

  function stateText(s) {
    return s === 'playing' ? '进行中' : (s === 'waiting' ? '等人' : '已结束');
  }

  async function loadUsers() {
    const r = await api(`/admin/api/users?q=${encodeURIComponent(S.q)}&skip=${S.skip}&take=${S.take}`);
    S.users = r.data.users || [];
    S.total = r.data.total || 0;

    $('#userRows').innerHTML = S.users.length
      ? S.users.map((u) => `
        <tr data-id="${u.id}">
          <td class="cell-id">${u.id}</td>
          <td class="cell-name">${esc(u.username)}</td>
          <td class="cell-num">${u.points}</td>
          <td class="cell-avatar">${u.isCustomAvatar ? '自己画的' : `<span class="emoji">${esc(avatarEmoji(u.avatar))}</span>`}</td>
          <td class="cell-time">${fmtTime(u.createdAt)}</td>
          <td class="cell-time">${fmtTime(u.lastLoginAt)}</td>
          <td>
            <div class="acts">
              <button class="btn btn-ghost" data-act="plus">+10</button>
              <button class="btn btn-ghost" data-act="minus">−10</button>
              <button class="btn btn-ghost" data-act="set">设成…</button>
              <button class="btn btn-danger" data-act="del">删号</button>
            </div>
          </td>
        </tr>`).join('')
      : '<tr><td colspan="7" class="room-empty">没找到这个名字的账号。</td></tr>';

    const from = S.total === 0 ? 0 : S.skip + 1;
    const to = Math.min(S.skip + S.take, S.total);
    $('#pagerInfo').textContent = `${from}–${to} / 共 ${S.total}`;
    $('#prevBtn').disabled = S.skip <= 0;
    $('#nextBtn').disabled = S.skip + S.take >= S.total;
  }

  function avatarEmoji(avatar) {
    return typeof avatar === 'string' && avatar.startsWith('preset:') ? avatar.slice(7) : '🙂';
  }

  async function refresh() {
    try {
      await Promise.all([loadOverview(), loadUsers()]);
    } catch (e) {
      if (e.unauthorized) { handleUnauthorized(); return; }
      toast('刷新没成功，等一下再点');
    }
  }

  // ------------------------------------------------------------ 登录

  $('#loginForm').addEventListener('submit', async (e) => {
    e.preventDefault();
    const key = $('#keyInput').value.trim();
    const hint = $('#loginHint');
    const btn = $('#loginBtn');

    if (!key) {
      hint.textContent = '先把密钥填上';
      hint.classList.add('warn');
      return;
    }

    btn.disabled = true;
    btn.textContent = '看看…';
    const r = await api('/admin/api/login', { method: 'POST', body: { key } });
    btn.disabled = false;
    btn.textContent = '进去';

    if (r.ok) {
      hint.textContent = '';
      hint.classList.remove('warn');
      $('#keyInput').value = '';
      authed = true;
      showDash();
      await refresh();
      return;
    }

    hint.classList.add('warn');
    if (r.status === 429) {
      const sec = r.data?.locked ?? 0;
      hint.textContent = sec > 0 ? `试得有点勤，${Math.ceil(sec / 60)} 分钟后再来` : '试得有点勤，歇一会儿';
    } else {
      const left = r.data?.left ?? 0;
      hint.textContent = left > 0 ? `没对上，还能试 ${left} 次` : '没对上';
      $('#keyInput').select();
    }
  });

  $('#logoutBtn').addEventListener('click', async () => {
    try { await api('/admin/api/logout', { method: 'POST' }); } catch (_) { }
    authed = false;
    showGate();
  });

  // ------------------------------------------------------------ 表格操作

  $('#userRows').addEventListener('click', async (e) => {
    const btn = e.target.closest('button[data-act]');
    if (!btn) return;

    const tr = btn.closest('tr');
    const id = Number(tr.dataset.id);
    const user = S.users.find((u) => u.id === id);
    if (!user) return;

    const act = btn.dataset.act;

    if (act === 'del') {
      if (!confirm(`删掉「${user.username}」？积分和账号一起没了，删不回来。`)) return;
      try {
        const r = await api(`/admin/api/users/${id}`, { method: 'DELETE' });
        if (r.ok) { toast(`「${user.username}」已经删了`); await refresh(); }
        else toast('没删成，再看一眼？');
      } catch (err) {
        if (err.unauthorized) handleUnauthorized(); else toast('没删成，再看一眼？');
      }
      return;
    }

    let body = null;
    if (act === 'plus') body = { delta: 10 };
    else if (act === 'minus') body = { delta: -10 };
    else if (act === 'set') {
      const raw = prompt(`把「${user.username}」的积分设成多少？`, String(user.points));
      if (raw === null) return;
      const value = Number(raw);
      if (!Number.isFinite(value) || value < 0) { toast('填个 0 以上的数吧'); return; }
      body = { set: Math.trunc(value) };
    }
    if (!body) return;

    try {
      const r = await api(`/admin/api/users/${id}/points`, { method: 'POST', body });
      if (r.ok) {
        toast(`「${user.username}」现在是 ${r.data.points} 分`);
        await loadUsers();
      } else {
        toast('没改成，再看一眼？');
      }
    } catch (err) {
      if (err.unauthorized) handleUnauthorized(); else toast('没改成，再看一眼？');
    }
  });

  // ------------------------------------------------------------ 搜索 / 翻页

  let searchTimer = null;
  $('#searchInput').addEventListener('input', (e) => {
    clearTimeout(searchTimer);
    const value = e.target.value;
    searchTimer = setTimeout(() => {
      S.q = value.trim();
      S.skip = 0;
      loadUsers().catch((err) => { if (err.unauthorized) handleUnauthorized(); });
    }, 260);
  });

  $('#refreshBtn').addEventListener('click', () => { refresh(); toast('刷新好了'); });

  $('#prevBtn').addEventListener('click', () => {
    S.skip = Math.max(0, S.skip - S.take);
    loadUsers().catch((err) => { if (err.unauthorized) handleUnauthorized(); });
  });

  $('#nextBtn').addEventListener('click', () => {
    S.skip += S.take;
    loadUsers().catch((err) => { if (err.unauthorized) handleUnauthorized(); });
  });

  // ------------------------------------------------------------ 启动

  (async function boot() {
    try {
      await loadOverview();
      await loadUsers();
      showDash();
    } catch (e) {
      handleUnauthorized();   // 没票就是 404，当没登录处理
    }
  })();
})();
