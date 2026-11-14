/* =========================================================
   app.js —— 门禁、账号、积分、头像
   ========================================================= */
(function () {
  'use strict';

  const $ = (sel, root = document) => root.querySelector(sel);
  const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));

  const PRESETS = ['🐱', '🐶', '🦊', '🐼', '🐸', '🐙', '🦄', '🐳', '🍄', '👾', '🦉', '🐝'];

  const esc = (v) => String(v ?? '').replace(/[&<>"']/g, (c) => (
    { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]
  ));

  // 名字后面的身份标志，后端只发代号，文案和颜色都在这
  const ROLE_META = {
    dev: { text: '游戏开发者', cls: 'role-dev' },
    admin: { text: '管理员', cls: 'role-admin' }
  };

  function roleTags(badges) {
    if (!Array.isArray(badges) || badges.length === 0) return '';
    return badges
      .map((k) => ROLE_META[k])
      .filter(Boolean)
      .map((m) => `<span class="role-tag ${m.cls}">${m.text}</span>`)
      .join('');
  }

  function nameHtml(name, badges) {
    return esc(name) + roleTags(badges);
  }

  const S = {
    device: localStorage.getItem('nb.device') || null,
    me: null,
    view: 'gate',
    authMode: 'register',
    authAvatar: 'preset:🐱',
    draftAvatarUrl: null,
    lockTimer: null,
    pointsRaf: 0
  };

  // ------------------------------------------------------------ 工具

  async function api(path, options = {}) {
    const opts = {
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      ...options
    };
    if (opts.body && typeof opts.body !== 'string') opts.body = JSON.stringify(opts.body);
    const res = await fetch(path, opts);
    let data = null;
    try { data = await res.json(); } catch (_) { /* 有些响应没 body */ }
    // 账号被删/被清场之后，旧 cookie 还能发请求，但服务端已经不认了。
    // 一次刷新里可能有好几个请求同时 401，只提示一次、只刷新一次，别连成一片。
    if (res.status === 401 && S.me && !S.sessionGone) {
      S.sessionGone = true;
      S.me = null;
      toast('登录状态没有了，重新进一次');
      setTimeout(() => location.reload(), 900);
    }
    return { status: res.status, ok: res.ok, data };
  }

  function toast(text) {
    const wrap = $('#toastWrap');
    const el = document.createElement('div');
    el.className = 'toast';
    el.textContent = text;
    wrap.appendChild(el);
    setTimeout(() => el.remove(), 3000);
  }

  function shake(el) {
    if (!el) return;
    el.classList.remove('shake');
    void el.offsetWidth;
    el.classList.add('shake');
  }

  function showView(name) {
    const changed = S.view !== name;
    ['gate', 'auth', 'lobby', 'room', 'game'].forEach((v) => {
      const el = document.getElementById('view-' + v);
      if (el) el.hidden = v !== name;
    });
    S.view = name;
    $('#topbar').hidden = !['lobby', 'room', 'game'].includes(name);
    if (changed) window.scrollTo({ top: 0, behavior: 'smooth' });
  }

  // ------------------------------------------------------------ 头像

  function avatarMarkup(p, cls = 'avatar avatar-sm') {
    const isCustom = p.avatar === 'custom' || p.isCustomAvatar === true;
    if (isCustom) {
      const id = p.id != null ? p.id : (S.me ? S.me.id : 0);
      return `<span class="${cls}"><img src="/api/avatar/${id}" alt=""></span>`;
    }
    const emoji = typeof p.avatar === 'string' && p.avatar.startsWith('preset:')
      ? p.avatar.slice(7)
      : (p.emoji || '🙂');
    return `<span class="${cls}">${emoji}</span>`;
  }

  function buildAvatarGrid(container, selected, onPick, extraCustomUrl) {
    const items = PRESETS.map((e) => {
      const value = 'preset:' + e;
      return `<button type="button" class="avatar-option ${value === selected ? 'is-active' : ''}" data-avatar="${value}">${e}</button>`;
    });

    if (extraCustomUrl) {
      items.push(`<button type="button" class="avatar-option ${selected === 'custom' ? 'is-active' : ''}" data-avatar="custom"><img src="${extraCustomUrl}" alt=""></button>`);
    }

    container.innerHTML = items.join('');
    $$('.avatar-option', container).forEach((btn) => {
      btn.addEventListener('click', () => {
        $$('.avatar-option', container).forEach((b) => b.classList.remove('is-active'));
        btn.classList.add('is-active');
        onPick(btn.dataset.avatar);
      });
    });
  }

  // ------------------------------------------------------------ 积分

  function renderPoints(animate, prevHint) {
    if (!S.me) return;
    const el = $('#pointsNum');
    const pill = $('#pointsPill');
    const next = S.me.points;
    const prev = prevHint != null ? prevHint : next;

    if (!animate || prev === next) {
      el.textContent = next;
      return;
    }

    cancelAnimationFrame(S.pointsRaf);
    const start = performance.now();
    const dur = 460;
    const step = (t) => {
      const p = Math.min(1, (t - start) / dur);
      const eased = 1 - Math.pow(1 - p, 3);
      el.textContent = Math.round(prev + (next - prev) * eased);
      if (p < 1) S.pointsRaf = requestAnimationFrame(step);
      else el.textContent = next;
    };
    S.pointsRaf = requestAnimationFrame(step);

    el.classList.remove('bump');
    void el.offsetWidth;
    el.classList.add('bump');

    pill.classList.remove('is-up', 'is-down');
    pill.classList.add(next > prev ? 'is-up' : 'is-down');
    setTimeout(() => pill.classList.remove('is-up', 'is-down'), 1200);

    const d = $('#pointsDelta');
    d.textContent = (next > prev ? '+' : '') + (next - prev);
    d.classList.remove('show');
    void d.offsetWidth;
    d.classList.add('show');
  }

  function setPoints(next, animate) {
    if (!S.me) return;
    const prev = S.me.points;
    if (next === prev) return;
    S.me.points = next;
    renderPoints(animate, prev);
  }

  // ------------------------------------------------------------ 顶栏

  function renderTopbar() {
    if (!S.me) return;
    const nameEl = $('#meName');
    nameEl.innerHTML = nameHtml(S.me.name, S.me.badges);
    nameEl.classList.toggle('has-roles', roleTags(S.me.badges) !== '');
    // 管理员/开发者才看得到「管理」
    const adminBtn = $('#btnAdminLobby');
    if (adminBtn) adminBtn.hidden = !(S.me.role === 'dev' || S.me.role === 'admin');
    const el = $('#meAvatar');
    if (S.me.isCustomAvatar) el.innerHTML = `<img src="/api/avatar/${S.me.id}?t=${Date.now()}" alt="">`;
    else el.textContent = S.me.emoji || '🙂';
    $('#pointsNum').textContent = S.me.points;
  }

  // ------------------------------------------------------------ 门禁

  function applyDeviceClass() {
    document.body.classList.toggle('device-phone', S.device === 'phone');
    document.body.classList.toggle('device-pc', S.device === 'pc');
  }

  /** 不再问「电脑还是手机」了，自己猜一下：触屏 + 窄屏 = 手机 */
  function guessDevice() {
    const ua = navigator.userAgent || '';
    if (/Android|iPhone|iPod|Mobile|HarmonyOS/i.test(ua)) return 'phone';
    const touch = (navigator.maxTouchPoints || 0) > 1;
    const narrow = window.matchMedia('(max-width: 760px)').matches;
    return touch && narrow ? 'phone' : 'pc';
  }

  function showGateStep(step) {
    $('#gateStepDevice').hidden = step !== 'device';
    $('#gateStepPassword').hidden = step !== 'password';
    if (step === 'password') setTimeout(() => $('#gatePassword').focus(), 120);
  }

  function startLockCountdown(seconds) {
    clearInterval(S.lockTimer);
    const input = $('#gatePassword');
    const btn = $('#gateSubmit');
    const hint = $('#gateHint');
    let left = Math.max(1, Math.floor(seconds));

    input.disabled = true;
    btn.disabled = true;
    hint.classList.add('warn');

    const tick = () => {
      if (left <= 0) {
        clearInterval(S.lockTimer);
        input.disabled = false;
        btn.disabled = false;
        hint.textContent = '可以再试啦，慢慢来';
        hint.classList.remove('warn');
        input.focus();
        return;
      }
      const m = Math.floor(left / 60);
      const s = left % 60;
      hint.textContent = `手有点快，先歇 ${m}:${String(s).padStart(2, '0')} 再来～`;
      left--;
    };
    tick();
    S.lockTimer = setInterval(tick, 1000);
  }

  function initGate() {
    $$('.device-btn').forEach((btn) => {
      btn.addEventListener('click', () => {
        S.device = btn.dataset.device;
        localStorage.setItem('nb.device', S.device);
        applyDeviceClass();
        showGateStep('password');
      });
    });

    $('#gateForm').addEventListener('submit', async (e) => {
      e.preventDefault();
      const pwd = $('#gatePassword').value.trim();
      if (!pwd) { shake($('.gate-card')); return; }

      const btn = $('#gateSubmit');
      btn.disabled = true;
      btn.textContent = '看看…';

      const r = await api('/api/gate/check', { method: 'POST', body: { password: pwd } });

      btn.disabled = false;
      btn.textContent = '进去';

      const hint = $('#gateHint');

      if (r.ok) {
        hint.textContent = '暗号对的，进吧';
        hint.classList.remove('warn');
        await afterGate();
        return;
      }

      shake($('.gate-card'));
      if (r.status === 429) {
        startLockCountdown(r.data?.locked ?? 600);
        return;
      }

      const left = r.data?.left ?? 0;
      hint.classList.add('warn');
      hint.textContent = left > 0 ? `不太对哦，还能试 ${left} 次` : '不太对哦，再想想';
      $('#gatePassword').select();
    });
  }

  // ------------------------------------------------------------ 注册 / 登录

  function setAuthMode(mode) {
    S.authMode = mode;
    $$('#authTabs .tab').forEach((t) => t.classList.toggle('is-active', t.dataset.tab === mode));
    $('#avatarPick').hidden = mode !== 'register';
    $('#authSubmit').textContent = mode === 'register' ? '进来玩' : '进去';
    $('#authHint').textContent = mode === 'register'
      ? '先给你 100 分当本钱，输了也不心疼'
      : '欢迎回来，分数都给你留着';
    $('#authHint').classList.remove('warn');
  }

  function initAuth() {
    $$('#authTabs .tab').forEach((t) => {
      t.addEventListener('click', () => setAuthMode(t.dataset.tab));
    });

    buildAvatarGrid($('#avatarGrid'), S.authAvatar, (v) => { S.authAvatar = v; });

    $('#btnAlbumAuth')?.addEventListener('click', () => {
      pickAlbum((dataUrl) => {
        S.draftAvatarUrl = dataUrl;
        S.authAvatar = 'custom';
        buildAvatarGrid($('#avatarGrid'), 'custom', (v) => { S.authAvatar = v; }, dataUrl);
        toast('这张不错，一会跟着账号一起存 🖼');
      });
    });

    $('#btnDrawAvatar').addEventListener('click', () => {
      openAvatarStudio(async (dataUrl) => {
        S.draftAvatarUrl = dataUrl;
        S.authAvatar = 'custom';
        buildAvatarGrid($('#avatarGrid'), 'custom', (v) => { S.authAvatar = v; }, dataUrl);
        toast('画好啦，一会跟着账号一起存 ✍️');
      });
    });

    $('#authForm').addEventListener('submit', async (e) => {
      e.preventDefault();
      const name = $('#authName').value.trim();
      const pass = $('#authPass').value;
      const hint = $('#authHint');

      if (name.length < 2) { hint.textContent = '名字再长一点点，两个字起'; hint.classList.add('warn'); return; }
      if (pass.length < 4) { hint.textContent = '密码至少四位，别太好猜'; hint.classList.add('warn'); return; }

      const btn = $('#authSubmit');
      btn.disabled = true;
      const old = btn.textContent;
      btn.textContent = S.authMode === 'register' ? '开个号…' : '进…';

      const path = S.authMode === 'register' ? '/api/auth/register' : '/api/auth/login';
      const body = { username: name, password: pass, avatar: S.authAvatar };
      const r = await api(path, { method: 'POST', body });

      btn.disabled = false;
      btn.textContent = old;

      if (!r.ok) {
        hint.textContent = r.data?.error || '差一点点，再试一次？';
        hint.classList.add('warn');
        shake($('.auth-card'));
        return;
      }

      let me = r.data.me;

      // 注册时如果画了头像，这时候把它贴到账号上
      if (S.authMode === 'register' && S.authAvatar === 'custom' && S.draftAvatarUrl) {
        const up = await api('/api/me/avatar', { method: 'POST', body: { dataUrl: S.draftAvatarUrl } });
        if (up.ok) me = up.data.me;
        S.draftAvatarUrl = null;
      }

      enterApp(me);
      toast(S.authMode === 'register' ? `欢迎，${me.name}！先送你 100 分` : `回来啦，${me.name}`);
    });
  }

  // ------------------------------------------------------------ 进站

  function enterApp(me) {
    S.me = me;
    renderTopbar();
    showView('lobby');
    if (window.BombGame) window.BombGame.connect();
    // 好友、信箱、金币、对话，进大厅就拉一次
    window.NBSocial?.boot();
    window.NBChat?.boot();
  }

  // 门禁解开之后走进来。me 顺手带过来，省掉一次 /api/me 的往返。
  async function afterGate(meMaybe) {
    if (meMaybe) { enterApp(meMaybe); return; }
    const r = await api('/api/me');
    if (r.ok) enterApp(r.data.me);
    else showAuth();
  }

  function showAuth() {
    setAuthMode('register');
    showView('auth');
    setTimeout(() => $('#authName').focus(), 150);
  }

  function initTopbar() {
    $('#meChip').addEventListener('click', openProfile);
    $('#btnLogout').addEventListener('click', async () => {
      await api('/api/auth/logout', { method: 'POST' });
      if (window.BombGame) window.BombGame.disconnect();
      window.NBSocial?.reset();
      window.NBChat?.reset?.();
      S.me = null;
      showAuth();
      toast('换个号也行');
    });
  }

  // ------------------------------------------------------------ 我的资料（头像 / 名字 / 签名）

  /** 相册里挑一张：居中裁成正方形，缩到 256，够清楚又不占地方 */
  function toSquarePng(file, size) {
    return new Promise((resolve, reject) => {
      const url = URL.createObjectURL(file);
      const img = new Image();
      img.onload = () => {
        const side = Math.min(img.width, img.height);
        const sx = (img.width - side) / 2;
        const sy = (img.height - side) / 2;
        const canvas = document.createElement('canvas');
        canvas.width = canvas.height = size;
        const g = canvas.getContext('2d');
        g.drawImage(img, sx, sy, side, side, 0, 0, size, size);
        URL.revokeObjectURL(url);
        resolve(canvas.toDataURL('image/png'));
      };
      img.onerror = () => { URL.revokeObjectURL(url); reject(new Error('bad image')); };
      img.src = url;
    });
  }

  /** 打开系统相册/文件选择器，选完给回调一个方形 PNG dataUrl */
  let albumHandler = null;
  function pickAlbum(onPick) {
    const input = $('#albumFile');
    if (!input) return;
    albumHandler = onPick;
    input.value = '';
    input.click();
  }

  // 监听挂在 input 上，弹窗开多少次都只绑一次
  $('#albumFile')?.addEventListener('change', async (e) => {
    const file = e.target.files && e.target.files[0];
    if (!file || !albumHandler) return;
    if (!/^image\//.test(file.type)) { toast('挑一张图片吧 🖼'); return; }
    try {
      albumHandler(await toSquarePng(file, 256));
    } catch (_) {
      toast('这张图读不出来，换一张试试？');
    } finally {
      albumHandler = null;
      e.target.value = '';
    }
  });

  const P = { pending: null };

  function paintProfileAvatar(avatar, customUrl) {
    const el = $('#profileAvatar');
    if (!el) return;
    if (avatar === 'custom' && customUrl) el.innerHTML = `<img src="${customUrl}" alt="">`;
    else if (typeof avatar === 'string' && avatar.startsWith('preset:')) el.textContent = avatar.slice(7);
    else el.textContent = S.me?.emoji || '🙂';
  }

  function openProfile() {
    if (!S.me) return;
    P.pending = null;
    const me = S.me;
    const customUrl = me.isCustomAvatar ? `/api/avatar/${me.id}?t=${Date.now()}` : null;

    $('#profileName').value = me.name || '';
    $('#profileBio').value = me.bio || '';
    $('#profileNamePreview').textContent = me.name || '';
    $('#profileBioPreview').textContent = me.bio || '还没有签名';
    paintProfileAvatar(me.isCustomAvatar ? 'custom' : me.avatar, customUrl);

    buildAvatarGrid($('#profileAvatarGrid'), me.isCustomAvatar ? 'custom' : me.avatar, async (value) => {
      if (value === 'custom') return;
      // 选预设立刻生效，不用等保存
      const r = await api('/api/me/avatar', { method: 'POST', body: { preset: value } });
      if (!r.ok) { toast(r.data?.error || '没换成'); return; }
      S.me = r.data.me;
      P.pending = null;
      renderTopbar();
      paintProfileAvatar(value, null);
      $('#profileName').value = S.me.name;
      $('#profileBio').value = S.me.bio || '';
      window.BombGame?.refreshMe?.(S.me);
      toast('换好啦');
    }, customUrl);

    $('#modalProfile').hidden = false;
  }

  async function saveProfile() {
    if (!S.me) return;
    const btn = $('#profileSave');
    const name = $('#profileName').value.trim();
    const bio = $('#profileBio').value.trim();
    if (name.length < 2) { toast('名字至少两个字'); return; }

    btn.disabled = true;
    const old = btn.textContent;
    btn.textContent = '存…';

    try {
      if (P.pending) {
        const up = await api('/api/me/avatar', { method: 'POST', body: { dataUrl: P.pending } });
        if (!up.ok) { toast(up.data?.error || '头像没存上'); return; }
        S.me = up.data.me;
        P.pending = null;
      }
      const r = await api('/api/me/profile', { method: 'POST', body: { username: name, bio } });
      if (!r.ok) { toast(r.data?.error || '没存上，再试一次？'); return; }

      S.me = r.data.me;
      renderTopbar();
      closeModal($('#modalProfile'));
      window.BombGame?.refreshMe?.(S.me);
      window.NBSocial?.refreshFriends?.();
      toast('资料更新好了');
    } finally {
      btn.disabled = false;
      btn.textContent = old;
    }
  }

  // 资料弹窗里的按钮
  document.addEventListener('DOMContentLoaded', () => {
    $('#btnAlbumPick')?.addEventListener('click', () => {
      pickAlbum((dataUrl) => {
        P.pending = dataUrl;
        paintProfileAvatar('custom', dataUrl);
        toast('选好啦，点保存生效 🖼');
      });
    });

    $('#btnProfileDraw')?.addEventListener('click', () => {
      if (!S.me) return;
      openAvatarStudio((dataUrl) => {
        P.pending = dataUrl;
        paintProfileAvatar('custom', dataUrl);
        toast('画好啦，点保存生效 ✍️');
      });
    });

    $('#profileSave')?.addEventListener('click', saveProfile);
    $('#profileName')?.addEventListener('input', (e) => {
      $('#profileNamePreview').textContent = e.target.value || '';
    });
    $('#profileBio')?.addEventListener('input', (e) => {
      $('#profileBioPreview').textContent = e.target.value || '还没有签名';
    });
  });

  /** 服务端让下线（删号、清场、重置）时，把人送回登录页 */
  function wireServerEvents() {
    if (!window.BombGame) return;
    window.BombGame.on('bye', (p) => {
      if (S.sessionGone) return;              // 重复的 bye 只认第一条，免得连弹带刷
      S.sessionGone = true;
      toast(p && p.text ? p.text : '你被请下线了');
      S.me = null;
      setTimeout(() => location.reload(), 1800);
    });
    window.BombGame.on('accountsChanged', () => window.NBMod?.refresh?.());
    window.BombGame.on('gameReset', () => {
      toast('游戏被重置了，刷新一下');
      setTimeout(() => location.reload(), 1500);
    });
  }

  // ------------------------------------------------------------ 弹窗通用

  function closeModal(modal) {
    if (modal) modal.hidden = true;
  }

  document.addEventListener('click', (e) => {
    const closer = e.target.closest('[data-close]');
    if (closer) {
      const modal = closer.closest('.modal');
      if (modal) closeModal(modal);
      return;
    }
    if (e.target.classList.contains('modal')) closeModal(e.target);
  });

  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') $$('.modal:not([hidden])').forEach(closeModal);
  });

  // ------------------------------------------------------------ 版本检查

  // 版本号只认 /version.json 这一处。以前页面里还内联了一个 <meta name="nb-version">，
  // 两份版本号一旦不同步（改代码时忘了改 meta），就会 1.8.1 → 1.8.0 → 1.8.1 来回弹，
  // 刷新一次弹一次。现在干脆不用 meta 了。
  function verParts(v) {
    return String(v || '').trim().split('.').map((n) => {
      const x = parseInt(n, 10);
      return Number.isFinite(x) ? x : 0;
    });
  }

  /** remote 比 local 新才算更新；同号、旧号一律不算（避免弹回旧版本） */
  function isNewer(remote, local) {
    const a = verParts(remote);
    const b = verParts(local);
    const len = Math.max(a.length, b.length);
    for (let i = 0; i < len; i++) {
      const x = a[i] || 0;
      const y = b[i] || 0;
      if (x !== y) return x > y;
    }
    return false;
  }

  let versionBusy = false;

  async function checkVersion() {
    if (versionBusy) return;
    versionBusy = true;
    try {
      const res = await fetch('/version.json?t=' + Date.now(), { cache: 'no-store' });
      if (!res.ok) return;
      const data = await res.json();
      const remote = String(data.version || '').trim();
      if (!remote) return;

      const local = localStorage.getItem('nb.version');
      if (!local) {
        localStorage.setItem('nb.version', remote);
        return;
      }

      // 只有真的更新了才提示，而且提示过就记下来，不会再弹第二遍
      if (!isNewer(remote, local)) return;
      localStorage.setItem('nb.version', remote);

      const banner = $('#versionBanner');
      if (!banner) return;
      $('#versionText').textContent = `更新到 ${remote} 啦，刷新一下就是新的`;
      banner.hidden = false;
    } catch (_) { /* 网络抽风就算了，下次轮询再说 */ }
    finally { versionBusy = false; }
  }

  // ------------------------------------------------------------ 启动

  async function boot() {
    // 老用户 localStorage 里可能已经选过，没选过就自己猜
    if (!S.device) {
      S.device = guessDevice();
      localStorage.setItem('nb.device', S.device);
    }
    applyDeviceClass();
    initGate();
    initAuth();
    initTopbar();
    setAuthMode('register');

    $('#versionRefresh').addEventListener('click', () => location.reload());
    $('#versionLater')?.addEventListener('click', () => { $('#versionBanner').hidden = true; });
    checkVersion();
    setInterval(() => checkVersion(), 60000);
    wireServerEvents();

    // 一次请求拿到「门禁解没解开 + 我是谁」，外网能省一个来回
    const g = await api('/api/boot');
    if (!g.data?.unlocked) {
      showView('gate');
      showGateStep(S.device ? 'password' : 'device');
      return;
    }
    await afterGate(g.data.me);
  }

  document.addEventListener('DOMContentLoaded', boot);

  // 给 game.js 用
  window.NB = {
    api, toast, showView, avatarMarkup, setPoints, renderTopbar,
    enterApp, closeModal, roleTags, nameHtml, S, $$, $
  };
})();
