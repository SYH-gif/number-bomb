/* =========================================================
   chat.js —— 对话模式：私聊 + 大厅公屏
   私聊只对好友开放；公屏所有在线的人都能看见。
   实时走同一条 SignalR 连接（BombGame.invoke），不另开。
   ========================================================= */
(function () {
  'use strict';

  const NB = window.NB;
  const { $, $$, api, toast, closeModal, nameHtml, avatarMarkup, S } = NB;

  const EMOJIS = ['😀', '😂', '🥺', '😎', '🤔', '👍', '🎉', '💣', '🪙', '🔥', '❤️', '🐱'];

  const C = {
    wired: false,
    realtime: false,
    booted: false,
    threads: [],
    unread: 0,
    peer: null,
    msgs: [],
    lobby: [],
    lobbyLoaded: false,
    muted: false,
    lastTyping: 0,
    typingTimer: 0
  };

  const esc = (v) => String(v ?? '').replace(/[&<>"']/g, (c) => (
    { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]
  ));

  /** 后端存的是 UTC 的 "2026-11-14 08:12:00" */
  function parseAt(raw) {
    if (!raw) return new Date();
    const d = new Date(String(raw).replace(' ', 'T') + 'Z');
    return isNaN(d) ? new Date() : d;
  }

  const hhmm = (raw) => {
    const d = parseAt(raw);
    return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
  };

  const dayKey = (raw) => {
    const d = parseAt(raw);
    return `${d.getFullYear()}-${d.getMonth() + 1}-${d.getDate()}`;
  };

  function dayLabel(raw) {
    const d = parseAt(raw);
    const today = new Date();
    const same = (a, b) => a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
    if (same(d, today)) return '今天';
    const y = new Date(today.getTime() - 86400000);
    if (same(d, y)) return '昨天';
    return `${d.getMonth() + 1} 月 ${d.getDate()} 日`;
  }

  function threadTime(raw) {
    const d = parseAt(raw);
    const diff = (Date.now() - d.getTime()) / 1000;
    if (diff < 60) return '刚刚';
    if (diff < 3600) return Math.floor(diff / 60) + ' 分钟';
    if (dayKey(raw) === dayKey(new Date().toISOString())) return hhmm(raw);
    return `${d.getMonth() + 1}/${d.getDate()}`;
  }

  // ------------------------------------------------------------------ 画消息

  function msgHtml(m, mine, peerInfo) {
    const who = mine ? null : { id: m.fromId, avatar: m.avatar, isCustomAvatar: m.isCustomAvatar };
    const wrap = mine
      ? { id: S.me?.id, avatar: S.me?.avatar, isCustomAvatar: S.me?.isCustomAvatar, emoji: S.me?.emoji }
      : (who || peerInfo || {});
    const name = mine ? '你' : nameHtml(m.fromName, m.badges);
    return `
      <div class="chat-row ${mine ? 'mine' : ''}" data-cid="${m.id}">
        ${avatarMarkup(wrap, 'avatar avatar-sm')}
        <div class="chat-cols">
          <div class="chat-who">${name}<span class="chat-at">${hhmm(m.at)}</span></div>
          <div class="chat-bubble">${esc(m.body)}</div>
        </div>
      </div>`;
  }

  /** 一天的聊天加一个日期分隔，翻记录时不会糊成一片 */
  function renderMessages(list, container, peerInfo) {
    const parts = [];
    let lastDay = '';
    list.forEach((m) => {
      const k = dayKey(m.at);
      if (k !== lastDay) {
        parts.push(`<div class="chat-day">${dayLabel(m.at)}</div>`);
        lastDay = k;
      }
      parts.push(msgHtml(m, m.fromId === S.me?.id, peerInfo));
    });
    container.innerHTML = parts.join('');
    container.scrollTop = container.scrollHeight;
  }

  function appendMessage(list, container, m, peerInfo) {
    const last = container.lastElementChild;
    const k = dayKey(m.at);
    const prev = list.length > 1 ? list[list.length - 2] : null;
    if (!prev || dayKey(prev.at) !== k) {
      container.insertAdjacentHTML('beforeend', `<div class="chat-day">${dayLabel(m.at)}</div>`);
    }
    container.insertAdjacentHTML('beforeend', msgHtml(m, m.fromId === S.me?.id, peerInfo));
    container.scrollTop = container.scrollHeight;
    void last;
  }

  // ------------------------------------------------------------------ 未读

  function paintUnread(total) {
    C.unread = total || 0;
    const badge = $('#chatBadge');
    if (badge) {
      badge.hidden = C.unread <= 0;
      badge.textContent = C.unread > 9 ? '9+' : C.unread;
    }
    const meta = $('#chatMeta');
    if (meta) meta.textContent = C.unread > 0 ? `${C.unread} 条未读` : '没有新消息';
  }

  // ------------------------------------------------------------------ 会话列表

  async function refreshThreads(silent) {
    const r = await api('/api/chat/threads');
    if (!r.ok) return;
    C.threads = r.data.threads || [];
    C.muted = !!r.data.muted;
    paintUnread(r.data.unread);
    renderThreads();
    paintMuted();
  }

  function renderThreads() {
    const list = $('#chatThreads');
    if (!list) return;

    if (!C.threads.length) {
      list.innerHTML = '<div class="list-empty">还没有对话。点上面的「找好友聊」，或者去好友列表点「聊」。</div>';
      return;
    }

    list.innerHTML = C.threads.map((t) => `
      <button class="thread-row ${C.peer && C.peer.id === t.id ? 'is-active' : ''}" data-thread="${t.id}" type="button">
        <span class="presence-dot ${t.online ? 'on' : ''}"></span>
        ${avatarMarkup(t, 'avatar avatar-sm')}
        <span class="thread-main">
          <span class="thread-top">
            <span class="thread-name">${nameHtml(t.name, t.badges)}</span>
            <span class="thread-time">${threadTime(t.at)}</span>
          </span>
          <span class="thread-preview">${t.mine ? '你：' : ''}${esc(t.last)}</span>
        </span>
        ${t.unread > 0 ? `<span class="thread-unread">${t.unread > 9 ? '9+' : t.unread}</span>` : ''}
      </button>`).join('');
  }

  // ------------------------------------------------------------------ 私聊窗口

  async function openThread(id, fallbackPeer) {
    const r = await api('/api/chat/' + id);
    if (!r.ok) {
      toast(r.data?.error || '这个对话打不开');
      return;
    }
    C.peer = r.data.peer || fallbackPeer || { id, name: '对方' };
    C.msgs = r.data.messages || [];
    paintPeer();

    $('#chatMain').hidden = false;
    renderMessages(C.msgs, $('#chatStream'), C.peer);
    renderThreads();

    const input = $('#chatInput');
    if (input) {
      input.value = '';
      input.placeholder = C.muted ? '你被禁言了，先看别人聊' : '说点什么…';
      input.disabled = C.muted;
      if (window.matchMedia('(min-width: 641px)').matches) input.focus();
    }
    if (r.data.unread != null) paintUnread(r.data.unread);
    refreshThreads(true);
  }

  function paintPeer() {
    const box = $('#chatPeer');
    if (!box || !C.peer) return;
    box.innerHTML = `
      <span class="peer-name">${nameHtml(C.peer.name, C.peer.badges)}</span>
      <span class="peer-state">${C.peer.online ? '在线' : '不在线'}</span>`;
  }

  function backToList() {
    C.peer = null;
    $('#chatMain').hidden = true;
    $('#chatTyping').hidden = true;
    renderThreads();
  }

  async function sendChat() {
    const input = $('#chatInput');
    const text = input.value.trim();
    if (!text || !C.peer) return;
    if (C.muted) { toast('你被禁言了，先安静一会儿'); return; }

    const btn = $('#chatSend');
    btn.disabled = true;
    try {
      const r = await window.BombGame.invoke('SendChat', C.peer.id, text);
      if (!r || !r.ok) { toast(r?.error || '没发出去'); return; }
      C.msgs.push(r.message);
      appendMessage(C.msgs, $('#chatStream'), r.message, C.peer);
      input.value = '';
      C.lastTyping = 0;
      refreshThreads(true);
    } catch (e) {
      toast(e.message || '没发出去，等一秒再试');
    } finally {
      btn.disabled = false;
      input.focus();
    }
  }

  function paintMuted() {
    const hint = $('#lobbyChatHint');
    const input = $('#lobbyInput');
    const send = $('#lobbySend');
    if (input) {
      input.disabled = C.muted;
      input.placeholder = C.muted ? '你被禁言了，先听别人说' : '说点什么…';
    }
    if (send) send.disabled = C.muted;
    if (hint) hint.textContent = C.muted ? '你被禁言了，暂时只能看' : '路过都能说一句，别刷屏';
    if (C.peer) {
      const ci = $('#chatInput');
      if (ci) {
        ci.disabled = C.muted;
        ci.placeholder = C.muted ? '你被禁言了，先看别人聊' : '说点什么…';
      }
    }
  }

  // ------------------------------------------------------------------ 公屏

  async function loadLobby() {
    const r = await api('/api/chat/lobby');
    if (!r.ok) return;
    C.lobby = r.data.messages || [];
    C.muted = !!r.data.muted;
    C.lobbyLoaded = true;
    renderMessages(C.lobby, $('#lobbyStream'), null);
    paintMuted();
  }

  async function sendLobby() {
    const input = $('#lobbyInput');
    const text = input.value.trim();
    if (!text) return;
    if (C.muted) { toast('你被禁言了，先安静一会儿'); return; }

    const btn = $('#lobbySend');
    btn.disabled = true;
    try {
      const r = await window.BombGame.invoke('SendLobby', text);
      if (!r || !r.ok) { toast(r?.error || '没发出去'); return; }
      input.value = '';
    } catch (e) {
      toast(e.message || '没发出去，等一秒再试');
    } finally {
      btn.disabled = false;
    }
  }

  function onLobbyMessage(m) {
    if (!C.lobbyLoaded) return;
    C.lobby.push(m);
    if (C.lobby.length > 80) C.lobby.shift();
    const stream = $('#lobbyStream');
    if (!stream) return;
    const atBottom = stream.scrollHeight - stream.scrollTop - stream.clientHeight < 60;
    appendMessage(C.lobby, stream, m, null);
    if (!atBottom) stream.scrollTop = stream.scrollHeight;
  }

  // ------------------------------------------------------------------ 事件绑定

  function openModal(peerId) {
    $('#modalChat').hidden = false;
    $('#chatMain').hidden = true;
    refreshThreads();
    if (peerId) openThread(peerId);
  }

  function onChatMessage(m) {
    const mine = m.fromId === S.me?.id;
    if (C.peer && !mine && m.fromId === C.peer.id && !$('#modalChat').hidden) {
      C.msgs.push(m);
      appendMessage(C.msgs, $('#chatStream'), m, C.peer);
      api('/api/chat/' + C.peer.id + '/read', { method: 'POST' });
      return;
    }
    if (!mine) {
      const t = C.threads.find((x) => x.id === m.fromId);
      if (t) {
        t.last = m.body;
        t.at = m.at;
        t.mine = false;
        t.unread = (t.unread || 0) + 1;
      }
      paintUnread((C.unread || 0) + 1);
      const who = m.fromName || '有人';
      toast(`💬 ${who}：${m.body.length > 16 ? m.body.slice(0, 16) + '…' : m.body}`);
    }
    // 列表顺序按最后一条重排
    refreshThreads(true);
  }

  function bind() {
    ['#btnChat', '#btnChatLobby'].forEach((sel) => {
      const el = $(sel);
      if (el) el.addEventListener('click', () => openModal());
    });

    const pick = $('#chatPickFriend');
    if (pick) {
      pick.addEventListener('click', () => {
        closeModal($('#modalChat'));
        window.NBSocial?.openFriends?.();
      });
    }

    const list = $('#chatThreads');
    if (list) {
      list.addEventListener('click', (e) => {
        const btn = e.target.closest('[data-thread]');
        if (btn) openThread(Number(btn.dataset.thread));
      });
    }

    const back = $('#chatBack');
    if (back) back.addEventListener('click', backToList);

    const form = $('#chatForm');
    if (form) {
      form.addEventListener('submit', (e) => { e.preventDefault(); sendChat(); });
    }

    const input = $('#chatInput');
    if (input) {
      input.addEventListener('input', () => {
        if (!C.peer || C.muted) return;
        const now = Date.now();
        if (now - C.lastTyping < 2200) return;
        C.lastTyping = now;
        window.BombGame?.invoke('Typing', C.peer.id).catch(() => { });
      });
    }

    const lobbyForm = $('#lobbyForm');
    if (lobbyForm) {
      lobbyForm.addEventListener('submit', (e) => { e.preventDefault(); sendLobby(); });
    }

    // 表情：点一下塞到光标位置
    const emojiBtn = $('#chatEmojiBtn');
    const emojiBar = $('#chatEmojiBar');
    if (emojiBar) {
      emojiBar.innerHTML = EMOJIS.map((e) => `<button type="button" data-emoji="${e}">${e}</button>`).join('');
      emojiBar.addEventListener('click', (e) => {
        const b = e.target.closest('[data-emoji]');
        if (!b) return;
        const i = $('#chatInput');
        i.value += b.dataset.emoji;
        i.focus();
      });
    }
    if (emojiBtn && emojiBar) {
      emojiBtn.addEventListener('click', () => { emojiBar.hidden = !emojiBar.hidden; });
    }
  }

  function watchRealtime() {
    if (!window.BombGame) return;
    window.BombGame.on('chat', onChatMessage);
    window.BombGame.on('lobbyChat', onLobbyMessage);
    window.BombGame.on('typing', (p) => {
      if (!C.peer || p.fromId !== C.peer.id) return;
      const el = $('#chatTyping');
      if (el) el.hidden = false;
      clearTimeout(C.typingTimer);
      C.typingTimer = setTimeout(() => { el.hidden = true; }, 2600);
    });
    window.BombGame.on('role', (p) => {
      if (!S.me) return;
      S.me.role = p.role;
      S.me.badges = p.badges || [];
      NB.renderTopbar();
      toast('🛡️ ' + (p.text || '你的身份变了'));
    });
    window.BombGame.on('lobbyCleared', () => {
      C.lobby = [];
      const stream = $('#lobbyStream');
      if (stream) stream.innerHTML = '';
    });

    window.BombGame.on('announce', (p) => {
      toast(`📢 ${p.text}`);
      const stream = $('#lobbyStream');
      if (stream) {
        const row = document.createElement('div');
        row.className = 'chat-day';
        row.textContent = `📢 ${p.by}：${p.text}`;
        stream.appendChild(row);
        stream.scrollTop = stream.scrollHeight;
      }
    });
    // 被禁言/解禁：立刻把输入框锁上或放开，不用等下一次发送失败
    window.BombGame.on('mute', (p) => {
      if (!S.me) return;
      S.me.mutedUntil = p.minutes > 0 ? p.until : null;
      C.muted = p.minutes > 0;
      paintMuted();
    });
  }

  function boot() {
    if (!C.wired) { C.wired = true; bind(); }
    if (!C.realtime) { C.realtime = true; watchRealtime(); }
    C.booted = true;
    refreshThreads();
    loadLobby();
  }

  /** 换账号时把上一任的对话清干净 */
  function reset() {
    C.booted = false;
    C.threads = [];
    C.msgs = [];
    C.lobby = [];
    C.lobbyLoaded = false;
    C.peer = null;
    C.muted = false;
    paintUnread(0);
    backToList();
    const stream = $('#lobbyStream');
    if (stream) stream.innerHTML = '';
    closeModal($('#modalChat'));
  }

  document.addEventListener('DOMContentLoaded', () => {
    // 登录后由 app.js 调 boot；这里先把按钮挂上，未登录点了也没反应
    if (!C.wired) { C.wired = true; bind(); }
  });

  window.NBChat = {
    boot, reset,
    open: openModal,
    openWith: (id) => openModal(id),
    refresh: refreshThreads,
    loadLobby,
    unread: () => C.unread
  };
})();
