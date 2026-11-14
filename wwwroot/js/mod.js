/* =========================================================
   mod.js —— 管理面板
   管理员：看在线、踢人出房间、禁言、发公告
   开发者：额外能任命/撤销管理员、调分、送金币
   ========================================================= */
(function () {
  'use strict';

  const NB = window.NB;
  const { $, $$, api, toast, closeModal, nameHtml, avatarMarkup, S } = NB;

  const M = { data: null, tab: 'online', found: null, accounts: null };

  const esc = (v) => String(v ?? '').replace(/[&<>"']/g, (c) => (
    { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]
  ));

  const isDev = () => S.me?.role === 'dev';
  const isStaff = () => isDev() || S.me?.role === 'admin';

  // ------------------------------------------------------------------ 拉数据

  async function refresh() {
    const r = await api('/api/mod/overview');
    if (!r.ok) {
      toast(r.data?.error || '打不开管理面板');
      return false;
    }
    M.data = r.data;
    renderOnline();
    renderRooms();
    renderStaff();
    ['#adminRolesTab', '#adminAccountsTab', '#adminDangerTab'].forEach((sel) => {
      const tab = $(sel);
      if (tab) tab.hidden = !isDev();
    });
    return true;
  }

  // ------------------------------------------------------------------ 在线

  function renderOnline() {
    const box = $('#adminOnline');
    if (!box) return;
    const online = M.data?.online || [];
    if (!online.length) {
      box.innerHTML = '<div class="list-empty">现在只有你自己在线。</div>';
      return;
    }

    box.innerHTML = online.map((p) => {
      const self = p.id === S.me?.id;
      const same = p.role !== 'user' && !isDev();
      const muteBtn = p.muted
        ? `<button class="mod-btn gold" data-unmute="${p.id}" type="button">解除禁言</button>`
        : `<button class="mod-btn" data-mute="${p.id}" data-min="10" type="button">禁言 10 分</button>
           <button class="mod-btn" data-mute="${p.id}" data-min="60" type="button">1 小时</button>`;
      return `
        <div class="row-item">
          <span class="presence-dot on"></span>
          ${avatarMarkup(p, 'avatar avatar-sm')}
          <div class="grow">
            <div class="row-name">${nameHtml(p.name, p.badges)}${p.muted ? ' <span class="admin-chip">禁言中</span>' : ''}</div>
            <div class="row-sub">${p.role === 'dev' ? '开发者' : (p.role === 'admin' ? '管理员' : '玩家')}${self ? ' · 你' : ''}</div>
          </div>
          <div class="mod-row-actions">
            ${self || same ? '' : `${muteBtn}
            <button class="mod-btn danger" data-kick="${p.id}" type="button">踢出房间</button>`}
          </div>
        </div>`;
    }).join('');
  }

  // ------------------------------------------------------------------ 房间

  function renderRooms() {
    const box = $('#adminRooms');
    if (!box) return;
    const rooms = M.data?.rooms || [];
    if (!rooms.length) {
      box.innerHTML = '<div class="list-empty">现在没有房间在开着。</div>';
      return;
    }

    const stateText = { waiting: '等人', playing: '对局中', finished: '刚结束' };
    box.innerHTML = rooms.map((r) => `
      <div class="admin-room">
        <div class="admin-room-head">
          <span class="admin-room-code">${esc(r.code)}</span>
          <span class="admin-chip">${stateText[r.state] || r.state}</span>
          <span class="admin-chip">${r.humans} 人${r.bots ? ` + ${r.bots} 机器人` : ''}</span>
          <span class="admin-chip">第 ${r.round} 局</span>
          <button class="mod-btn danger" data-close-room="${esc(r.code)}" type="button" style="margin-left:auto">解散</button>
        </div>
        <div class="admin-people">
          ${r.players.map((p) => `
            <span class="admin-person">
              ${esc(p.name)}${p.isBot ? ' 🤖' : ''}${p.isOut ? ' 💥' : ''}
              ${p.isBot ? '' : `<button class="mod-btn danger" data-room-kick="${esc(p.name)}" data-room-code="${esc(r.code)}" type="button">请走</button>`}
            </span>`).join('')}
        </div>
      </div>`).join('');
  }

  // ------------------------------------------------------------------ 管理者名单（开发者）

  function renderStaff() {
    const box = $('#roleStaff');
    if (!box) return;
    const staff = M.data?.staff || [];
    if (!staff.length) {
      box.innerHTML = '<div class="list-empty">还没有管理员。上面输入名字就能任命。</div>';
      return;
    }

    box.innerHTML = staff.map((p) => `
      <div class="row-item">
        ${avatarMarkup(p, 'avatar avatar-sm')}
        <div class="grow">
          <div class="row-name">${nameHtml(p.name, p.badges)}</div>
          <div class="row-sub">${p.role === 'dev' ? '开发者 · 名单里的人，改不了' : '管理员'}</div>
        </div>
        ${p.role === 'dev' || p.id === S.me?.id ? '' : `<button class="mod-btn danger" data-demote="${p.id}" type="button">撤销</button>`}
      </div>`).join('');
  }

  function renderFound() {
    const box = $('#roleFound');
    if (!box) return;
    const p = M.found;
    if (!p) { box.innerHTML = ''; return; }

    const roleText = p.role === 'dev' ? '开发者' : (p.role === 'admin' ? '管理员' : '普通玩家');
    box.innerHTML = `
      <div class="row-item">
        ${avatarMarkup(p, 'avatar avatar-md')}
        <div class="grow">
          <div class="row-name">${nameHtml(p.name, p.badges)}</div>
          <div class="row-sub">${roleText} · ${p.points} 分 · ${p.coins} 🪙</div>
        </div>
      </div>
      <div class="modal-actions" style="flex-wrap:wrap;gap:6px">
        ${p.role === 'admin'
          ? `<button class="mod-btn danger" data-set-role="user" type="button">撤销管理员</button>`
          : (p.role === 'dev' ? '<span class="hint">开发者身份写在配置里，改不了</span>'
            : `<button class="mod-btn gold" data-set-role="admin" type="button">设为管理员</button>`)}
        <button class="mod-btn" data-points="100" type="button">+100 分</button>
        <button class="mod-btn" data-points="-100" type="button">-100 分</button>
        <button class="mod-btn" data-coins="100" type="button">+100 🪙</button>
      </div>`;
  }

  // ------------------------------------------------------------------ 账号台账（开发者）

  function renderAccounts() {
    const box = $('#accountList');
    if (!box) return;
    const q = ($('#accountSearch')?.value || '').trim().toLowerCase();
    const list = (M.accounts || []).filter((a) => !q || a.name.toLowerCase().includes(q));

    if (!list.length) {
      box.innerHTML = '<div class="list-empty">没有匹配的账号。</div>';
      return;
    }

    box.innerHTML = list.map((a) => `
      <div class="row-item">
        <span class="presence-dot ${a.online ? 'on' : ''}"></span>
        ${avatarMarkup(a, 'avatar avatar-sm')}
        <div class="grow">
          <div class="row-name">${nameHtml(a.name, a.badges)}${a.self ? ' <span class="admin-chip">你</span>' : ''}</div>
          <div class="row-sub">${a.role === 'dev' ? '开发者' : (a.role === 'admin' ? '管理员' : '玩家')} · ${a.points} 分 · ${a.coins} 🪙 · ${a.lastLoginAt ? '上次 ' + a.lastLoginAt.slice(5, 16) : '没登录过'}</div>
        </div>
        <div class="mod-row-actions">
          ${a.self || a.dev ? '' : `<button class="mod-btn" data-role-quick="${a.id}" data-role-name="${esc(a.name)}" type="button">${a.role === 'admin' ? '撤管' : '设管'}</button>
          <button class="mod-btn danger" data-delete-user="${a.id}" data-user-name="${esc(a.name)}" type="button">删号</button>`}
        </div>
      </div>`).join('');
  }

  async function loadAccounts() {
    const r = await api('/api/mod/accounts');
    if (!r.ok) { toast(r.data?.error || '拉不到账号'); return; }
    M.accounts = r.data.accounts || [];
    renderAccounts();
  }

  // ------------------------------------------------------------------ 危险区（开发者）

  async function wipeUsers() {
    const word = prompt('这会删除除开发者以外的所有账号，删了没法恢复。\n确认的话请输入：确认清空');
    if (word === null) return;
    if (word.trim() !== '确认清空') { toast('确认词不对，什么也没做'); return; }

    const r = await api('/api/dev/wipe-users', { method: 'POST', body: { confirm: '确认清空' } });
    if (!r.ok) { toast(r.data?.error || '没成'); return; }
    toast(`清完了，删掉 ${r.data.deleted} 个账号`);
    refresh();
    loadAccounts();
  }

  async function resetGame() {
    const word = prompt('这会清空好友、信件、聊天，积分回到 100、金币清零。\n确认的话请输入：重置游戏');
    if (word === null) return;
    if (word.trim() !== '重置游戏') { toast('确认词不对，什么也没做'); return; }

    const r = await api('/api/dev/reset-game', { method: 'POST', body: { confirm: '重置游戏' } });
    if (!r.ok) { toast(r.data?.error || '没成'); return; }
    toast('重置完了');
    refresh();
  }

  async function deleteUser(id, name) {
    if (!confirm(`确定删掉「${name}」？账号和它的好友、信件、聊天都会清掉，恢复不了。`)) return;
    const r = await api('/api/mod/delete-user', { method: 'POST', body: { userId: id } });
    toast(r.ok ? `「${name}」删掉了` : (r.data?.error || '没成'));
    if (r.ok) { refresh(); loadAccounts(); }
  }

  async function closeRoom(code) {
    if (!confirm(`解散房间 ${code}？里面的人会回到大厅。`)) return;
    const r = await api('/api/mod/close-room', { method: 'POST', body: { code } });
    toast(r.ok ? '房间解散了' : (r.data?.error || '没成'));
    if (r.ok) refresh();
  }

  async function clearLobby() {
    if (!confirm('清空大厅公屏？所有人屏幕上的公屏也会清掉。')) return;
    const r = await api('/api/mod/clear-lobby', { method: 'POST' });
    toast(r.ok ? `清掉了 ${r.data.deleted} 条` : (r.data?.error || '没成'));
  }

  // ------------------------------------------------------------------ 交互

  async function mute(id, minutes) {
    const r = await api('/api/mod/mute', { method: 'POST', body: { userId: id, minutes } });
    toast(r.ok ? (minutes > 0 ? `禁言 ${minutes} 分钟` : '禁言解除了') : (r.data?.error || '没成'));
    if (r.ok) refresh();
  }

  async function kick(id, code) {
    const r = await api('/api/mod/kick', { method: 'POST', body: { userId: id, code } });
    toast(r.ok ? '请出去了' : (r.data?.error || '没成'));
    if (r.ok) refresh();
  }

  async function kickByName(name, code) {
    const f = await api('/api/mod/find?q=' + encodeURIComponent(name));
    if (!f.ok) { toast(f.data?.error || '找不到这个人'); return; }
    await kick(f.data.person.id, code);
  }

  async function announce() {
    const input = $('#announceText');
    const text = input.value.trim();
    if (!text) { toast('公告不能是空的'); return; }
    const r = await api('/api/mod/announce', { method: 'POST', body: { text } });
    if (!r.ok) { toast(r.data?.error || '没发出去'); return; }
    input.value = '';
    toast('公告发出去了');
  }

  async function findPerson() {
    const name = $('#roleName').value.trim();
    if (!name) return;
    const r = await api('/api/mod/find?q=' + encodeURIComponent(name));
    if (!r.ok) { M.found = null; renderFound(); toast(r.data?.error || '没找到这个人'); return; }
    M.found = r.data.person;
    renderFound();
  }

  async function setRole(role) {
    if (!M.found) return;
    const r = await api('/api/roles/set', { method: 'POST', body: { userId: M.found.id, role } });
    if (!r.ok) { toast(r.data?.error || '没成'); return; }
    toast(role === 'admin' ? `${M.found.name} 现在是管理员` : `${M.found.name} 回到普通玩家`);
    M.found.role = role;
    M.found.badges = role === 'admin' ? ['admin'] : [];
    renderFound();
    refresh();
  }

  async function setRoleById(id, role, name) {
    const r = await api('/api/roles/set', { method: 'POST', body: { userId: id, role } });
    toast(r.ok ? `${name} ${role === 'admin' ? '现在是管理员' : '回到普通玩家'}` : (r.data?.error || '没成'));
    if (r.ok) { refresh(); loadAccounts(); }
  }

  async function demote(id) {
    const r = await api('/api/roles/set', { method: 'POST', body: { userId: id, role: 'user' } });
    toast(r.ok ? '撤销了' : (r.data?.error || '没成'));
    if (r.ok) refresh();
  }

  async function adjust(kind, value) {
    if (!M.found) return;
    const body = { userId: M.found.id };
    if (kind === 'points') body.delta = value;
    else body.coins = value;
    const r = await api('/api/mod/points', { method: 'POST', body });
    if (!r.ok) { toast(r.data?.error || '没成'); return; }
    M.found.points = r.data.points;
    M.found.coins = r.data.coins;
    renderFound();
    toast(kind === 'points' ? `现在是 ${r.data.points} 分` : `现在有 ${r.data.coins} 金币`);
  }

  function switchTab(tab) {
    M.tab = tab;
    $$('#adminTabs .tab').forEach((t) => t.classList.toggle('is-active', t.dataset.atab === tab));
    const panes = {
      online: '#adminOnline', rooms: '#adminRooms', announce: '#adminAnnounce',
      roles: '#adminRoles', accounts: '#adminAccounts', danger: '#adminDanger'
    };
    Object.entries(panes).forEach(([k, sel]) => {
      const el = $(sel);
      if (el) el.hidden = k !== tab;
    });
    if (tab === 'accounts') loadAccounts();
  }

  async function open() {
    if (!isStaff()) { toast('这不是你的面板'); return; }
    $('#modalAdmin').hidden = false;
    switchTab('online');
    M.found = null;
    M.accounts = null;
    renderFound();
    await refresh();
  }

  function bind() {
    $('#btnAdminLobby')?.addEventListener('click', open);

    $('#adminTabs')?.addEventListener('click', (e) => {
      const tab = e.target.closest('[data-atab]');
      if (tab) switchTab(tab.dataset.atab);
    });

    $('#adminOnline')?.addEventListener('click', (e) => {
      const muteBtn = e.target.closest('[data-mute]');
      if (muteBtn) { mute(Number(muteBtn.dataset.mute), Number(muteBtn.dataset.min)); return; }
      const unmute = e.target.closest('[data-unmute]');
      if (unmute) { mute(Number(unmute.dataset.unmute), 0); return; }
      const kickBtn = e.target.closest('[data-kick]');
      if (kickBtn) kick(Number(kickBtn.dataset.kick));
    });

    $('#adminRooms')?.addEventListener('click', (e) => {
      const closeBtn = e.target.closest('[data-close-room]');
      if (closeBtn) { closeRoom(closeBtn.dataset.closeRoom); return; }
      const b = e.target.closest('[data-room-kick]');
      if (b) kickByName(b.dataset.roomKick, b.dataset.roomCode);
    });

    $('#accountRefresh')?.addEventListener('click', loadAccounts);
    $('#accountSearch')?.addEventListener('input', renderAccounts);
    $('#accountList')?.addEventListener('click', (e) => {
      const del = e.target.closest('[data-delete-user]');
      if (del) { deleteUser(Number(del.dataset.deleteUser), del.dataset.userName); return; }
      const roleBtn = e.target.closest('[data-role-quick]');
      if (roleBtn) {
        const toAdmin = !roleBtn.textContent.includes('撤管');
        setRoleById(Number(roleBtn.dataset.roleQuick), toAdmin ? 'admin' : 'user', roleBtn.dataset.roleName);
      }
    });

    $('#wipeUsers')?.addEventListener('click', wipeUsers);
    $('#resetGame')?.addEventListener('click', resetGame);
    $('#clearLobbyBtn')?.addEventListener('click', clearLobby);

    $('#announceSend')?.addEventListener('click', announce);
    $('#roleForm')?.addEventListener('submit', (e) => { e.preventDefault(); findPerson(); });

    $('#roleFound')?.addEventListener('click', (e) => {
      const roleBtn = e.target.closest('[data-set-role]');
      if (roleBtn) { setRole(roleBtn.dataset.setRole); return; }
      const pts = e.target.closest('[data-points]');
      if (pts) { adjust('points', Number(pts.dataset.points)); return; }
      const coins = e.target.closest('[data-coins]');
      if (coins) adjust('coins', Number(coins.dataset.coins));
    });

    $('#roleStaff')?.addEventListener('click', (e) => {
      const b = e.target.closest('[data-demote]');
      if (b) demote(Number(b.dataset.demote));
    });
  }

  document.addEventListener('DOMContentLoaded', bind);

  window.NBMod = { open, refresh };
})();
