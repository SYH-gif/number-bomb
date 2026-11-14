/* =========================================================
   social.js —— 好友、信箱、道具商店
   ========================================================= */
(function () {
  'use strict';

  const { $, $$, api, toast, closeModal, nameHtml, S } = window.NB;

  const SOC = {
    friends: [],
    incoming: [],
    outgoing: [],
    mails: [],
    box: 'in',
    detailId: 0,
    unread: 0,
    coins: 0,
    items: [],
    inv: {},
    booted: false
  };

  const esc = (v) => String(v ?? '').replace(/[&<>"']/g, (c) => (
    { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]
  ));

  const avatar = (p, cls) => window.NB.avatarMarkup(p, cls || 'avatar avatar-sm');

  /** SQLite 存的是 UTC 的 "2026-11-14 08:12:00"，转成本地时间给人看 */
  function timeText(raw) {
    if (!raw) return '';
    const d = new Date(String(raw).replace(' ', 'T') + 'Z');
    if (isNaN(d)) return raw;
    const now = Date.now();
    const diff = (now - d.getTime()) / 1000;
    if (diff < 60) return '刚刚';
    if (diff < 3600) return Math.floor(diff / 60) + ' 分钟前';
    if (diff < 86400) return Math.floor(diff / 3600) + ' 小时前';
    const mm = d.getMonth() + 1;
    const dd = d.getDate();
    return `${mm}月${dd}日`;
  }

  // ------------------------------------------------------------------ 徽标 / 金币

  function paintCoins(n) {
    SOC.coins = n;
    if (typeof n === 'number') {
      $('#coinsNum').textContent = n;
      const shopCoins = $('#shopCoins');
      if (shopCoins) shopCoins.textContent = n;
      const lobby = $('#shopCoinsLobby');
      if (lobby) lobby.textContent = n + ' 金币';
    }
  }

  function paintBadges() {
    const fb = $('#friendBadge');
    if (fb) {
      fb.hidden = SOC.incoming.length === 0;
      fb.textContent = SOC.incoming.length;
    }
    const mb = $('#mailBadge');
    if (mb) {
      mb.hidden = SOC.unread <= 0;
      mb.textContent = SOC.unread > 9 ? '9+' : SOC.unread;
    }
    const meta = $('#mailMeta');
    if (meta) meta.textContent = SOC.unread > 0 ? `${SOC.unread} 封未读` : '没有新信';

    const online = SOC.friends.filter((f) => f.online).length;
    const fmeta = $('#friendsMeta');
    if (fmeta) fmeta.textContent = SOC.friends.length ? `${online}/${SOC.friends.length} 在线` : '还没加人';
  }

  // ------------------------------------------------------------------ 好友

  async function refreshFriends() {
    const r = await api('/api/friends');
    if (!r.ok) return;
    SOC.friends = r.data.friends || [];
    SOC.incoming = r.data.incoming || [];
    SOC.outgoing = r.data.outgoing || [];
    renderFriends();
    paintBadges();
  }

  function personRow(p, actions) {
    return `
      <div class="row-item">
        <span class="presence-dot ${p.online ? 'on' : ''}"></span>
        ${avatar(p)}
        <div class="grow">
          <div class="row-name">${nameHtml(p.name, p.badges)}</div>
          <div class="row-sub">${p.online ? '在线' : '不在线'} · ${p.points} 分 · ${p.coins} 🪙</div>
        </div>
        ${actions}
      </div>`;
  }

  function renderFriends() {
    const list = $('#friendList');
    if (list) {
      list.innerHTML = SOC.friends.length
        ? SOC.friends.map((f) => personRow(f, `
            <button class="btn btn-ghost mini" data-chat="${f.id}" type="button">聊</button>
            <button class="btn btn-ghost mini" data-mail="${esc(f.name)}" type="button">写信</button>
            <button class="btn btn-ghost mini" data-unfriend="${f.id}" type="button">删</button>`)).join('')
        : '<div class="list-empty">还没有好友。上面输入对方的名字，让他点一下同意就行。</div>';
    }

    const reqBox = $('#friendRequestsBlock');
    const reqList = $('#friendRequests');
    if (reqBox && reqList) {
      reqBox.hidden = SOC.incoming.length === 0;
      reqList.innerHTML = SOC.incoming.map((p) => personRow(p, `
        <button class="btn btn-primary mini" data-accept="${p.id}" type="button">同意</button>
        <button class="btn btn-ghost mini" data-decline="${p.id}" type="button">算了</button>`)).join('');
    }

    const count = $('#friendCount');
    if (count) count.textContent = SOC.friends.length;

    paintBadges();
  }

  async function addFriend(name) {
    const hint = $('#friendHint');
    const r = await api('/api/friends/request', { method: 'POST', body: { name } });
    hint.classList.toggle('warn', !r.ok);
    if (!r.ok) { hint.textContent = r.data?.error || '没加上，再试一次？'; return; }
    hint.textContent = `已经跟 ${name} 打了招呼，等他点同意`;
    $('#friendName').value = '';
    $('#friendSearch').innerHTML = '';
    await refreshFriends();
  }

  // 边打边找人，省得名字记不全
  let searchTimer = null;
  function wireSearch() {
    const input = $('#friendName');
    if (!input) return;
    input.addEventListener('input', () => {
      clearTimeout(searchTimer);
      const q = input.value.trim();
      const box = $('#friendSearch');
      if (q.length < 1) { box.innerHTML = ''; return; }
      searchTimer = setTimeout(async () => {
        const r = await api('/api/people?q=' + encodeURIComponent(q));
        if (!r.ok) return;
        const people = r.data.people || [];
        box.innerHTML = people.length
          ? people.map((p) => `
              <div class="row-item">
                ${avatar(p)}
                <div class="grow">
                  <div class="row-name">${nameHtml(p.name, p.badges)}</div>
                  <div class="row-sub">${p.points} 分</div>
                </div>
                <button class="btn btn-primary mini" data-add="${esc(p.name)}" type="button">加好友</button>
              </div>`).join('')
          : '<div class="list-empty">没找到这个人。名字得一模一样。</div>';
      }, 200);
    });
  }

  // ------------------------------------------------------------------ 信箱

  async function refreshMail() {
    const r = await api('/api/mail?box=' + (SOC.box === 'out' ? 'out' : 'in'));
    if (!r.ok) return;
    SOC.mails = r.data.mails || [];
    SOC.unread = r.data.unread || 0;
    renderMail();
    paintBadges();
  }

  function renderMail() {
    const list = $('#mailList');
    if (!list) return;

    if (!SOC.mails.length) {
      list.innerHTML = `<div class="list-empty">${SOC.box === 'out'
        ? '还没寄出过信。点上面的「写信」。'
        : '收件箱是空的。朋友来信会出现在这里，顶栏的信封也会冒小红点。'}</div>`;
      return;
    }

    list.innerHTML = SOC.mails.map((m) => {
      const out = SOC.box === 'out';
      return `
      <button class="mail-item ${!m.isRead && SOC.box === 'in' ? 'is-unread' : ''}" data-mail-id="${m.id}" type="button">
        <div class="mail-top">
          <span class="mail-from">${nameHtml(out ? m.toName : m.fromName, out ? m.toBadges : m.fromBadges)}</span>
          <span class="mail-time">${timeText(m.createdAt)}</span>
        </div>
        <div class="mail-subj">${esc(m.subject)}</div>
      </button>`;
    }).join('');
  }

  async function showMail(id) {
    const r = await api('/api/mail/' + id);
    if (!r.ok) { toast('这封信打不开了'); return; }
    const m = r.data.mail;
    SOC.detailId = m.id;
    SOC.unread = r.data.unread || 0;

    $('#mailDetailSubject').textContent = m.subject;
    $('#mailDetailMeta').innerHTML =
      `${nameHtml(m.fromName, m.fromBadges)} → ${nameHtml(m.toName, m.toBadges)} · ${timeText(m.createdAt)}`;
    $('#mailDetailBody').textContent = m.body;
    $('#mailReply').hidden = m.mine;

    $('#mailList').hidden = true;
    $('#mailCompose').hidden = true;
    $('#mailDetail').hidden = false;
    paintBadges();

    // 读完了，列表里的未读标记也得跟着变
    const item = SOC.mails.find((x) => x.id === m.id);
    if (item) item.isRead = true;
    renderMail();
    $('#mailList').hidden = true;
  }

  function openCompose(to, subject, body) {
    SOC.box = 'new';
    $$('#mailTabs .tab').forEach((t) => t.classList.toggle('is-active', t.dataset.box === 'new'));
    $('#mailList').hidden = true;
    $('#mailDetail').hidden = true;
    $('#mailCompose').hidden = false;
    $('#mailTo').value = to || '';
    $('#mailSubject').value = subject || '';
    $('#mailBody').value = body || '';
    setTimeout(() => $('#mailTo').focus(), 80);
  }

  async function sendMail() {
    const to = $('#mailTo').value.trim();
    const subject = $('#mailSubject').value.trim();
    const body = $('#mailBody').value.trim();
    if (!to) { toast('写给谁呀？'); return; }
    if (!body) { toast('信纸还是空的'); return; }

    const r = await api('/api/mail', { method: 'POST', body: { to, subject, body } });
    if (!r.ok) { toast(r.data?.error || '没寄出去，再试一次？'); return; }

    toast('寄出去了 ✉️');
    SOC.box = 'out';
    $$('#mailTabs .tab').forEach((t) => t.classList.toggle('is-active', t.dataset.box === 'out'));
    $('#mailCompose').hidden = true;
    $('#mailList').hidden = false;
    await refreshMail();
  }

  // ------------------------------------------------------------------ 商店

  async function refreshShop() {
    const r = await api('/api/shop');
    if (!r.ok) return;
    SOC.items = r.data.items || [];
    paintCoins(r.data.coins || 0);
    renderShop();
  }

  function renderShop() {
    const list = $('#shopList');
    if (!list) return;
    list.innerHTML = SOC.items.map((i) => `
      <div class="shop-item">
        <span class="shop-emoji">${i.emoji}</span>
        <div class="shop-info">
          <div class="shop-name">${esc(i.name)}</div>
          <div class="shop-desc">${esc(i.desc)}</div>
          <div class="shop-owned">已有 ${i.owned} 个 · ${esc(i.where)}</div>
        </div>
        <button class="shop-buy" data-buy="${i.kind}" type="button" ${SOC.coins < i.price ? 'disabled' : ''}>
          ${i.price} 🪙
        </button>
      </div>`).join('');
  }

  function syncInventory() {
    SOC.inv = {};
    SOC.items.forEach((i) => { SOC.inv[i.kind] = i.owned; });
    if (window.BombGame?.setInventory) window.BombGame.setInventory(SOC.inv);
  }

  async function buy(kind) {
    const r = await api('/api/shop/buy', { method: 'POST', body: { kind } });
    if (!r.ok) { toast(r.data?.error || '买不了'); return; }
    paintCoins(r.data.coins);
    const item = SOC.items.find((i) => i.kind === kind);
    if (item) item.owned = r.data.owned;
    toast(`到手了：${item ? item.name : '道具'} ${item ? item.emoji : ''}`);
    renderShop();
    syncInventory();
  }

  // ------------------------------------------------------------------ 打开 / 关闭

  function openFriends() {
    $('#modalFriends').hidden = false;
    refreshFriends();
    setTimeout(() => $('#friendName').focus(), 120);
  }

  async function openMail(box) {
    $('#modalMail').hidden = false;
    $('#mailDetail').hidden = true;
    $('#mailCompose').hidden = true;
    $('#mailList').hidden = false;
    SOC.box = box || 'in';
    $$('#mailTabs .tab').forEach((t) => t.classList.toggle('is-active', t.dataset.box === SOC.box));
    await refreshMail();
  }

  async function openShop() {
    $('#modalShop').hidden = false;
    await refreshShop();
  }

  // ------------------------------------------------------------------ 事件

  function wire() {
    wireSearch();

    $('#btnFriends')?.addEventListener('click', openFriends);
    $('#btnFriendsLobby')?.addEventListener('click', openFriends);
    $('#btnMail')?.addEventListener('click', () => openMail('in'));
    $('#btnMailLobby')?.addEventListener('click', () => openMail('in'));
    $('#btnShopLobby')?.addEventListener('click', openShop);

    $('#friendForm')?.addEventListener('submit', (e) => {
      e.preventDefault();
      const name = $('#friendName').value.trim();
      if (!name) return;
      addFriend(name);
    });

    $('#friendList')?.addEventListener('click', async (e) => {
      const chat = e.target.closest('[data-chat]');
      if (chat) { closeModal($('#modalFriends')); window.NBChat?.openWith(Number(chat.dataset.chat)); return; }

      const mail = e.target.closest('[data-mail]');
      if (mail) { closeModal($('#modalFriends')); openMail('new'); openCompose(mail.dataset.mail); return; }

      const unfriend = e.target.closest('[data-unfriend]');
      if (unfriend) {
        const r = await api('/api/friends/remove', { method: 'POST', body: { id: Number(unfriend.dataset.unfriend) } });
        if (r.ok) { toast('删掉了'); refreshFriends(); }
        return;
      }
    });

    $('#friendSearch')?.addEventListener('click', (e) => {
      const add = e.target.closest('[data-add]');
      if (add) addFriend(add.dataset.add);
    });

    $('#friendRequests')?.addEventListener('click', async (e) => {
      const yes = e.target.closest('[data-accept]');
      const no = e.target.closest('[data-decline]');
      if (!yes && !no) return;
      const id = Number((yes || no).dataset.accept || (yes || no).dataset.decline);
      const r = await api('/api/friends/answer', { method: 'POST', body: { id, accept: !!yes } });
      if (!r.ok) { toast(r.data?.error || '没处理成'); return; }
      toast(yes ? '成好友了，可以互相看到在线状态' : '算了就算了');
      refreshFriends();
    });

    $('#mailTabs')?.addEventListener('click', (e) => {
      const tab = e.target.closest('.tab');
      if (!tab) return;
      const box = tab.dataset.box;
      if (box === 'new') { openCompose(''); return; }
      SOC.box = box;
      $$('#mailTabs .tab').forEach((t) => t.classList.toggle('is-active', t === tab));
      $('#mailList').hidden = false;
      $('#mailCompose').hidden = true;
      $('#mailDetail').hidden = true;
      refreshMail();
    });

    $('#mailList')?.addEventListener('click', (e) => {
      const item = e.target.closest('[data-mail-id]');
      if (item) showMail(Number(item.dataset.mailId));
    });

    $('#mailBack')?.addEventListener('click', () => {
      $('#mailDetail').hidden = true;
      $('#mailList').hidden = false;
    });

    $('#mailCancel')?.addEventListener('click', () => {
      $('#mailCompose').hidden = true;
      $('#mailList').hidden = false;
      SOC.box = 'in';
      $$('#mailTabs .tab').forEach((t) => t.classList.toggle('is-active', t.dataset.box === 'in'));
      refreshMail();
    });

    $('#mailSend')?.addEventListener('click', sendMail);

    $('#mailReply')?.addEventListener('click', async () => {
      const r = await api('/api/mail/' + SOC.detailId);
      if (!r.ok) return;
      openCompose(r.data.mail.fromName, '回：' + r.data.mail.subject, '');
    });

    $('#mailDelete')?.addEventListener('click', async () => {
      if (!SOC.detailId) return;
      const r = await api('/api/mail/' + SOC.detailId, { method: 'DELETE' });
      if (!r.ok) { toast('没删掉'); return; }
      SOC.unread = r.data.unread || 0;
      $('#mailDetail').hidden = true;
      $('#mailList').hidden = false;
      toast('删掉了');
      await refreshMail();
    });

    $('#shopList')?.addEventListener('click', (e) => {
      const btn = e.target.closest('[data-buy]');
      if (btn && !btn.disabled) buy(btn.dataset.buy);
    });

    // 好友/邮件的实时小广播（连上 hub 之后才有）
    window.BombGame?.on?.('presence', (p) => {
      const f = SOC.friends.find((x) => x.id === p.id);
      if (f) {
        f.online = p.online;
        renderFriends();
        paintBadges();
      }
    });
    window.BombGame?.on?.('friend', () => refreshFriends());
    window.BombGame?.on?.('mail', () => {
      refreshMail();
      toast('来了一封新邮件 ✉️');
    });
  }

  /** 进大厅时调一次：三样都刷一遍，顶栏的小红点也就位了 */
  async function boot() {
    if (!S.me) return;
    paintCoins(S.me.coins || 0);
    await Promise.all([refreshFriends(), refreshMail(), refreshShop()]);
    syncInventory();
    SOC.booted = true;
  }

  function reset() {
    SOC.friends = [];
    SOC.incoming = [];
    SOC.mails = [];
    SOC.unread = 0;
    paintBadges();
    renderFriends();
  }

  document.addEventListener('DOMContentLoaded', wire);

  window.NBSocial = {
    boot, reset,
    openFriends, openMail, openShop,
    refreshFriends, refreshMail, refreshShop,
    setCoins: paintCoins,
    inventory: () => SOC.inv,
    unread: () => SOC.unread
  };
})();
