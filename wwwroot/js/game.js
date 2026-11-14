/* =========================================================
   game.js —— 大厅、房间、对局（SignalR）
   ========================================================= */
(function () {
  'use strict';

  const { $, $$, api, toast, showView, avatarMarkup, setPoints, closeModal, nameHtml, S } = window.NB;

  const G = {
    conn: null,
    room: null,
    connecting: false,
    leaving: false,
    waitDeadline: 0,
    tick: null,
    logCount: 0,
    logRound: -1,
    resultRound: -1,
    capacity: 3,
    gameType: 'bomb',   // 本次要建的房间玩法
    handlers: [],       // 好友/邮件/对话这些外部事件，连上以后一起挂上去
    bound: new Set(),   // 当前这条连接已经挂过哪些回调，防止重复
    inv: {}             // 我的道具数量
  };

  const esc = (v) => String(v ?? '').replace(/[&<>"']/g, (c) => (
    { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]
  ));

  /** 挂了身份标志的名字放不下，得给容器开换行 */
  const roleCls = (p) => (Array.isArray(p.badges) && p.badges.length ? ' has-roles' : '');

  // ------------------------------------------------------------------ 连接

  async function connect() {
    if (!S.me) return;
    if (G.conn && (G.conn.state === 'Connected' || G.connecting)) return;

    G.connecting = true;
    try {
      const conn = new signalR.HubConnectionBuilder()
        .withUrl('/hubs/game')
        .withAutomaticReconnect([0, 1000, 2500, 5000, 10000])
        .configureLogging(signalR.LogLevel.None)
        .build();

      conn.on('state', (snapshot) => applyState(snapshot));
      conn.on('notice', (text) => toast(text));
      // 被管理员请出房间：回到大厅，别让人卡在空房间里
      conn.on('kicked', (p) => {
        if (!G.room) return;
        G.room = null;
        stopTick();
        hideResult();
        showView('lobby');
        toast(`被 ${p?.by || '管理员'} 请出房间了`);
        window.NBSocial?.refreshMe?.();
      });
      G.bound = new Set();
      G.handlers.forEach(([evt, fn]) => bindOne(conn, evt, fn));

      conn.onreconnecting(() => toast('网有点晃，正在连回来…'));
      conn.onreconnected(async () => {
        toast('接上啦');
        await resume();
      });
      conn.onclose(() => {
        if (!S.me) return;
        setTimeout(() => connect(), 2500);
      });

      await conn.start();
      G.conn = conn;
      // 连接建好之前注册的回调（对话、公告）在这里补挂一次，bindOne 会去重
      G.handlers.forEach(([evt, fn]) => bindOne(conn, evt, fn));
      await resume();
    } catch (err) {
      console.warn('连接没成功', err);
      setTimeout(() => connect(), 3000);
    } finally {
      G.connecting = false;
    }
  }

  async function resume() {
    if (!G.conn) return;
    const r = await G.conn.invoke('Resume');
    if (r?.ok && r.code) {
      const joined = await G.conn.invoke('JoinRoom', r.code);
      if (!joined.ok) {
        G.room = null;
        showView('lobby');
      }
    } else if (S.view === 'room' || S.view === 'game') {
      G.room = null;
      showView('lobby');
    }
  }

  // 刚进大厅那一两秒，连接可能还在握手中（走公网时更明显）。
  // 这期间点按钮不该一声不响地失败，等一小会儿再发。
  async function whenReady(timeoutMs = 12000) {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
      if (G.conn && G.conn.state === 'Connected') return true;
      await new Promise((r) => setTimeout(r, 120));
    }
    return false;
  }

  function disconnect() {
    try { G.conn?.stop(); } catch (_) { }
    G.conn = null;
    G.room = null;
    stopTick();
    closeModal($('#modalResult'));
  }

  // ------------------------------------------------------------------ 状态分发

  function applyState(s) {
    if (G.leaving) return;                    // 正在退出，别被迟到的推送拽回房间
    if (!s.players.some((p) => p.id === S.me?.id)) {
      G.room = null;
      if (S.view === 'room' || S.view === 'game') showView('lobby');
      return;
    }

    G.room = s;
    const type = s.type || 'bomb';

    const mePlayer = s.players.find((p) => p.id === S.me?.id);
    if (mePlayer && S.me) {
      if (mePlayer.points !== S.me.points) setPoints(mePlayer.points, true);
      if (mePlayer.coins !== S.me.coins) {
        S.me.coins = mePlayer.coins;
        window.NBSocial?.setCoins(mePlayer.coins);
      }
    }

    if (s.state === 'waiting') {
      stopTick();
      renderRoom(s);
      showView('room');
      startTick(s.waitLeft);
      return;
    }

    stopTick();
    showView('game');

    // 三个玩法各画各的，先切面板再渲染
    $('#bombPanel').hidden = type !== 'bomb';
    $('#drawPanel').hidden = type !== 'draw';
    $('#snakePanel').hidden = type !== 'snake';
    if (type === 'draw') window.NBDraw?.render(s);
    else if (type === 'snake') window.NBSnake?.render(s);
    else renderGame(s);

    if (s.state === 'finished') showResult(s);
    else hideResult();
  }

  // ------------------------------------------------------------------ 房间等候

  const GAME_NAMES = { bomb: '数字炸弹 💣', draw: '你画我猜 🎨', snake: '贪吃蛇大乱斗 🐍' };

  function renderRoom(s) {
    $('#roomCode').textContent = s.code;

    const slots = [];
    s.players.forEach((p) => {
      slots.push(`
        <div class="player-chip filled">
          ${avatarMarkup(p, 'avatar avatar-lg')}
          <span class="pname${roleCls(p)}">${nameHtml(p.name, p.badges)}</span>
          ${p.isBot ? '<span class="pbot">机器人</span>' : (p.isHost ? '<span class="pbot">房主</span>' : '')}
        </div>`);
    });
    for (let i = s.players.length; i < s.capacity; i++) {
      slots.push('<div class="player-chip"><span class="avatar avatar-lg" style="opacity:.35">＋</span><span class="pname" style="color:var(--dim)">等一位</span></div>');
    }
    $('#roomPlayers').innerHTML = slots.join('');

    const isHost = +s.hostId === S.me?.id;
    const startBtn = $('#btnStart');
    const left = s.capacity - s.players.length;
    const gameName = GAME_NAMES[s.type || 'bomb'] || GAME_NAMES.bomb;

    $('#roomMsg').textContent = left > 0
      ? `${gameName} · 已经 ${s.players.length} 个人了，再等 ${left} 位`
      : `${gameName} · 人齐了，可以开啦`;

    startBtn.hidden = !isHost;
    startBtn.disabled = s.players.length < 2;
    startBtn.textContent = s.players.length < 2 ? '再等等人' : `开始（${s.players.length} 人）`;

    if (!isHost) $('#waitHint').textContent = '等房主点开始，最多等 1 分钟';
  }

  function startTick(seconds) {
    G.waitDeadline = Date.now() + Math.max(0, seconds) * 1000;

    const paint = () => {
      const left = Math.max(0, Math.round((G.waitDeadline - Date.now()) / 1000));
      const fill = $('#waitFill');
      if (fill) fill.style.width = Math.max(0, Math.min(100, (left / 60) * 100)) + '%';

      const hint = $('#waitHint');
      if (hint && G.room && G.room.state === 'waiting') {
        const isHost = +G.room.hostId === S.me?.id;
        if (left > 0) hint.textContent = left > 30
          ? `还有 ${left} 秒。房号发群里了吗`
          : `还有 ${left} 秒，凑不齐就让机器人顶上`;
        else hint.textContent = '机器人正在搓手，马上就好…';
        if (!isHost) hint.textContent = left > 0 ? `还有 ${left} 秒，房主随时可以开始` : '马上开始…';
      }
    };

    paint();
    stopTick();
    G.tick = setInterval(paint, 300);
  }

  function stopTick() {
    clearInterval(G.tick);
    G.tick = null;
  }

  // ------------------------------------------------------------------ 对局

  function renderGame(s) {
    const meId = S.me?.id;

    $('#gamePlayers').innerHTML = s.players.map((p) => {
      const isMe = p.id === meId;
      const isTurn = s.turnUserId === p.id;
      return `
        <div class="player ${isMe ? 'is-me' : ''} ${isTurn ? 'is-turn' : ''} ${p.isOut ? 'is-out' : ''}">
          ${p.isOut ? '<span class="bomb-mark">💥</span>' : ''}
          ${avatarMarkup(p, 'avatar avatar-lg')}
          <span class="pname${roleCls(p)}">${nameHtml(p.name, p.badges)}${isMe ? ' · 你' : ''}</span>
          <span class="ppoints">${p.points} 分</span>
          ${p.shielded ? '<span class="ptag">🛡️ 免死</span>' : ''}
        </div>`;
    }).join('');

    $('#rangeLow').textContent = s.low;
    $('#rangeHigh').textContent = s.high;

    const span = Math.max(1, s.max - s.min);
    const width = Math.max(4, ((s.high - s.low + 1) / (span + 1)) * 100);
    $('#rangeFill').style.width = Math.min(100, width) + '%';

    const current = s.players.find((p) => p.id === s.turnUserId);
    const myTurn = s.turnUserId === meId;
    const line = $('#turnLine');

    if (myTurn) {
      line.textContent = '轮到你了，别踩到那颗';
      line.classList.add('is-me');
    } else {
      line.textContent = current ? (current.isBot ? `${current.name} 在想…` : `轮到 ${current.name} 了`) : '等一等…';
      line.classList.remove('is-me');
    }

    const input = $('#guessInput');
    const btn = $('#guessBtn');
    input.disabled = !myTurn;
    btn.disabled = !myTurn;
    input.min = s.low;
    input.max = s.high;
    input.placeholder = myTurn ? `${s.low} ~ ${s.high} 之间` : '等别人猜';
    if (myTurn && document.body.classList.contains('device-pc')) input.focus();

    renderLog(s);
    renderItems(s);
  }

  /** 道具栏：轮到你、有货、这一把没用过，三个条件都满足才能点 */
  function renderItems(s) {
    const bar = $('#itemBar');
    if (!bar) return;

    if (s.state !== 'playing') {
      bar.hidden = true;
      $('#itemHint').hidden = true;
      return;
    }
    bar.hidden = false;

    const me = s.players.find((p) => p.id === S.me?.id);
    const myTurn = s.turnUserId === S.me?.id && !me?.isOut;
    const used = new Set(me?.items || []);

    $$('#itemBar .item-btn').forEach((btn) => {
      const kind = btn.dataset.kind;
      const qty = G.inv[kind] || 0;
      const qtyEl = btn.querySelector('.iqty');
      if (qtyEl) qtyEl.textContent = qty;

      const already = used.has(kind);
      btn.classList.toggle('is-empty', qty === 0);
      btn.disabled = !myTurn || qty === 0 || already;
    });
  }

  function setInventory(inv) {
    G.inv = inv || {};
    if (G.room && G.room.state === 'playing') renderItems(G.room);
  }

  /** 给好友/邮件这些模块挂事件用：连接建好就一起挂上 */
  function bindOne(conn, evt, fn) {
    if (G.bound.has(fn)) return;
    try { conn.on(evt, fn); G.bound.add(fn); } catch (_) { /* 连接刚断，下次重连再挂 */ }
  }

  function on(evt, fn) {
    G.handlers.push([evt, fn]);
    // 模块可能比连接晚一步注册，也可能早一步；两种都接得住
    if (G.conn) bindOne(G.conn, evt, fn);
  }

  function renderLog(s) {
    const list = $('#guessLog');

    if (!s.guesses.length) {
      list.innerHTML = '<div class="log-empty">还没人开口，要不要第一个来？</div>';
      G.logCount = 0;
      G.logRound = s.round;
      return;
    }

    if (G.logRound !== s.round || s.guesses.length < G.logCount) {
      list.innerHTML = '';
      G.logCount = 0;
      G.logRound = s.round;
    }

    for (let i = G.logCount; i < s.guesses.length; i++) {
      const g = s.guesses[i];
      // 踩中那颗的那一行，配个 💥，比干巴巴两个字有画面
      if (g.dir === 'skip') {
        list.insertAdjacentHTML('beforeend', `
          <div class="log-item skip">
            <span class="lname">${esc(g.name)}</span>
            <span>用了 💨 金蝉脱壳，跳过这一轮</span>
          </div>`);
        continue;
      }
      const label = g.dir === 'boom' ? '💥 踩到了' : (g.dir === 'low' ? '小了' : '大了');
      list.insertAdjacentHTML('beforeend', `
        <div class="log-item ${esc(g.dir)}">
          <span class="lname">${esc(g.name)}</span>
          <span>猜</span>
          <span class="lval">${g.value}</span>
          <span class="ltag">${label}</span>
        </div>`);
    }
    G.logCount = s.guesses.length;
    list.scrollTop = list.scrollHeight;
  }

  // ------------------------------------------------------------------ 结果

  function showResult(s) {
    if (G.resultRound === s.round) return;
    G.resultRound = s.round;

    const modal = $('#modalResult');
    const isBomb = (s.type || 'bomb') === 'bomb';
    const isHost = +s.hostId === S.me?.id;

    if (!isBomb) {
      // 画猜 / 贪吃蛇：没有"输家"，比的是谁分高
      const winnerId = s.type === 'draw' ? s.draw?.winnerId : s.snake?.winnerId;
      const winner = s.players.find((p) => p.id === winnerId);
      const iWon = winnerId != null && winnerId === S.me?.id;
      const name = s.type === 'draw' ? '画王' : '蛇王';

      $('#resultEmoji').textContent = winnerId == null ? '🫥' : (iWon ? '🎉' : '🏆');
      $('#resultTitle').textContent = winnerId == null
        ? '这局散啦'
        : (iWon ? `你就是 ${name}！` : `${winner ? winner.name : '有人'} 是 ${name}`);
      $('#resultSub').textContent = winnerId == null
        ? '人都走光了，回大厅再开一桌？'
        : (s.type === 'draw' ? '比的是这一轮谁得分多。再来一轮见真章' : '金币池平分完毕，下一把继续抢吃的');

      $('#resultList').innerHTML = s.players.map((p) => {
        const d = s.deltas.find((x) => x.id === p.id);
        const delta = d ? d.delta : 0;
        const cls = delta > 0 ? 'up' : (delta < 0 ? 'down' : '');
        const mark = p.id === winnerId ? '<span class="rmark">第一名</span>' : '';
        return `
          <div class="result-row ${p.id === winnerId ? 'loser' : ''}">
            ${avatarMarkup(p, 'avatar avatar-sm')}
            <span class="rname${roleCls(p)}">${nameHtml(p.name, p.badges)}${p.id === S.me?.id ? ' · 你' : ''}</span>${mark}
            <span class="rpoints">${p.points} 分</span>
            <span class="rdelta ${cls}">${delta > 0 ? '+' : ''}${delta || '±0'}${d && d.coins ? ` · ${d.coins > 0 ? '+' : '-'}${Math.abs(d.coins)}🪙` : ''}</span>
          </div>`;
      }).join('');

      $('#btnResultAgain').hidden = !isHost || winnerId == null;
      $('#btnResultAgain').textContent = '再来一把';
      if (!isHost && winnerId != null) {
        $('#resultSub').textContent += '。想再来就等房主点一下';
      }
      modal.hidden = false;
      return;
    }

    const loser = s.players.find((p) => p.id === s.loserId);
    const iLost = s.loserId != null && s.loserId === S.me?.id;

    $('#resultEmoji').textContent = s.loserId == null ? '🫥' : (iLost ? '💥' : '🎉');

    if (s.loserId == null) {
      $('#resultTitle').textContent = '这局散啦';
      $('#resultSub').textContent = '人都走光了，回大厅再开一桌？';
    } else if (iLost) {
      const mine = s.deltas.find((x) => x.id === S.me?.id);
      const lostCoins = mine && mine.coins < 0 ? Math.abs(mine.coins) : 0;
      $('#resultTitle').textContent = '你踩到啦';
      $('#resultSub').textContent = `炸弹是 ${s.bomb}。掉 5 分${lostCoins ? `、扣 ${lostCoins} 金币` : ''}，下一把还能捞回来`;
    } else {
      $('#resultTitle').textContent = `${loser ? loser.name : '有人'} 踩到啦`;
      $('#resultSub').textContent = `炸弹是 ${s.bomb}，${loser ? loser.name : '他'} 替你挡了一下`;
    }

    $('#resultList').innerHTML = s.players.map((p) => {
      const d = s.deltas.find((x) => x.id === p.id);
      const delta = d ? d.delta : 0;
      const cls = delta > 0 ? 'up' : (delta < 0 ? 'down' : '');
      // +15 的那位是这局缩范围缩得最狠的，标出来，省得大家猜为什么多 5 分
      const mark = delta === 15 ? '<span class="rmark">猜得最准</span>' : '';
      return `
        <div class="result-row ${p.id === s.loserId ? 'loser' : ''}">
          ${avatarMarkup(p, 'avatar avatar-sm')}
          <span class="rname${roleCls(p)}">${nameHtml(p.name, p.badges)}${p.id === S.me?.id ? ' · 你' : ''}</span>${mark}
          <span class="rpoints">${p.points} 分</span>
          <span class="rdelta ${cls}">${delta > 0 ? '+' : ''}${delta || '±0'}${d && d.coins ? ` · ${d.coins > 0 ? '+' : '-'}${Math.abs(d.coins)}🪙` : ''}</span>
        </div>`;
    }).join('');

    $('#btnResultAgain').hidden = !isHost || s.loserId == null;
    $('#btnResultAgain').textContent = '再来一把';
    if (!isHost && s.loserId != null) {
      $('#resultSub').textContent += '。想再来就等房主点一下';
    }
    modal.hidden = false;
  }

  function hideResult() {
    G.resultRound = -1;
    closeModal($('#modalResult'));
  }

  // ------------------------------------------------------------------ 大厅交互

  function initLobby() {
    // 建房：人数
    const countRow = $('#countRow');
    countRow.innerHTML = [2, 3, 4, 5, 6, 7, 8]
      .map((n) => `<button type="button" class="count-btn ${n === G.capacity ? 'is-active' : ''}" data-n="${n}">${n}</button>`)
      .join('');
    $$('.count-btn', countRow).forEach((btn) => {
      btn.addEventListener('click', () => {
        G.capacity = Number(btn.dataset.n);
        $$('.count-btn', countRow).forEach((b) => b.classList.toggle('is-active', b === btn));
      });
    });

    // 建房：玩法
    const HINTS = {
      bomb: '最多一万个数，别太狠',
      draw: '每人画一轮，其他人抢答，猜对就有分',
      snake: '每人一条蛇，撞到别人肚子他就下线，金币全桌共享'
    };
    $$('#gamePickRow .gamepick').forEach((btn) => {
      btn.addEventListener('click', () => {
        G.gameType = btn.dataset.g;
        $$('#gamePickRow .gamepick').forEach((b) => b.classList.toggle('is-active', b === btn));
        $('#rangeWrap').hidden = G.gameType !== 'bomb';
        $('#createHint').textContent = HINTS[G.gameType];
        $('#createHint').classList.remove('warn');
      });
    });

    $('#btnCreate').addEventListener('click', () => {
      $('#modalCreate').hidden = false;
      $('#rangeWrap').hidden = G.gameType !== 'bomb';
      $('#createHint').textContent = HINTS[G.gameType];
      $('#createHint').classList.remove('warn');
    });

    $('#btnCreateConfirm').addEventListener('click', async () => {
      const hint = $('#createHint');
      let min = 1, max = 100;

      if (G.gameType === 'bomb') {
        min = parseInt($('#createMin').value, 10);
        max = parseInt($('#createMax').value, 10);

        if (!Number.isFinite(min) || !Number.isFinite(max)) {
          hint.textContent = '两个格子都要填数字哦';
          hint.classList.add('warn');
          return;
        }
        if (min === max) {
          hint.textContent = '两个数得不一样哦';
          hint.classList.add('warn');
          return;
        }
        if (Math.abs(max - min) > 10000) {
          hint.textContent = '范围太大啦，收一收（最多一万个数）';
          hint.classList.add('warn');
          return;
        }
        if (Math.min(min, max) < 0) {
          hint.textContent = '从 0 开始数比较舒服';
          hint.classList.add('warn');
          return;
        }
      }

      if (!(await whenReady())) {
        hint.textContent = '网还没接上，喘口气再点一次？';
        hint.classList.add('warn');
        return;
      }

      const r = await G.conn.invoke('CreateGameRoom', G.capacity, min, max, G.gameType);
      if (!r.ok) { hint.textContent = r.error || '没开成，再点一次？'; hint.classList.add('warn'); return; }
      closeModal($('#modalCreate'));
    });

    // 加入
    $('#btnJoin').addEventListener('click', () => {
      $('#joinCode').value = '';
      $('#joinHint').textContent = '房号是朋友创建房间时看到的数字';
      $('#joinHint').classList.remove('warn');
      $('#modalJoin').hidden = false;
      setTimeout(() => $('#joinCode').focus(), 120);
    });

    $('#joinCode').addEventListener('input', (e) => {
      e.target.value = e.target.value.replace(/\D/g, '').slice(0, 4);
    });

    $('#btnJoinConfirm').addEventListener('click', async () => {
      const code = $('#joinCode').value.trim();
      if (code.length !== 4) {
        $('#joinHint').textContent = '房号是 4 个数字';
        $('#joinHint').classList.add('warn');
        return;
      }

      if (!(await whenReady())) {
        $('#joinHint').textContent = '网还没接上，喘口气再点一次？';
        $('#joinHint').classList.add('warn');
        return;
      }

      const r = await G.conn.invoke('JoinRoom', code);
      if (!r.ok) {
        $('#joinHint').textContent = r.error || '没进去，再试一次？';
        $('#joinHint').classList.add('warn');
        return;
      }
      closeModal($('#modalJoin'));
    });

    $('#joinCode').addEventListener('keydown', (e) => {
      if (e.key === 'Enter') { e.preventDefault(); $('#btnJoinConfirm').click(); }
    });

    // 房间
    $('#btnCopyCode').addEventListener('click', async () => {
      const code = $('#roomCode').textContent;
      try {
        await navigator.clipboard.writeText(code);
        toast('房号复制好了，发给朋友吧');
      } catch (_) {
        toast(`房号是 ${code}`);
      }
    });

    $('#btnStart').addEventListener('click', async () => {
      const r = await G.conn.invoke('StartGame');
      if (!r.ok) toast(r.error || '还开不了');
    });

    $('#btnLeave').addEventListener('click', leaveRoom);
    $('#btnResultLeave').addEventListener('click', leaveRoom);
    $('#btnGameLeave').addEventListener('click', leaveRoom);

    $('#btnResultAgain').addEventListener('click', async () => {
      const r = await G.conn.invoke('PlayAgain', null, null);
      if (!r.ok) toast(r.error || '开不了下一把');
      else hideResult();
    });

    // 用道具
    $('#itemBar')?.addEventListener('click', async (e) => {
      const btn = e.target.closest('.item-btn');
      if (!btn || btn.disabled) return;
      if (!(await whenReady())) { toast('网还没接上，等一下再点'); return; }

      const kind = btn.dataset.kind;
      const r = await G.conn.invoke('UseItem', kind);
      if (!r.ok) { toast(r.error || '这个用不了'); return; }

      G.inv[kind] = Math.max(0, (G.inv[kind] || 0) - 1);
      if (r.hint) {
        const hint = $('#itemHint');
        hint.textContent = '🔍 ' + r.hint;
        hint.hidden = false;
      }
      if (G.room) renderItems(G.room);
      window.NBSocial?.refreshShop?.();
    });

    // 猜数字
    $('#guessForm').addEventListener('submit', async (e) => {
      e.preventDefault();
      const raw = $('#guessInput').value.trim();
      if (!raw) return;
      const value = Math.trunc(Number(raw));
      if (!Number.isFinite(value)) { toast('填个数字吧'); return; }

      const r = await G.conn.invoke('Guess', value);
      if (!r.ok) toast(r.error || '这一下没猜上');
      else $('#guessInput').value = '';
    });
  }

  async function leaveRoom() {
    G.leaving = true;
    try { await G.conn?.invoke('LeaveRoom'); } catch (_) { }
    G.room = null;
    stopTick();
    hideResult();
    window.NBDraw?.reset?.();
    window.NBSnake?.reset?.();
    showView('lobby');
    setTimeout(() => { G.leaving = false; }, 600);

    const me = await api('/api/me');
    if (me.ok && S.me) {
      const before = S.me.points;
      S.me = me.data.me;
      window.NB.renderTopbar();
      if (S.me.points !== before) setPoints(S.me.points, true);
    }
  }

  // ------------------------------------------------------------------ 外部调用

  function refreshMe(me) {
    if (!me || !G.room) return;
    const p = G.room.players.find((x) => x.id === me.id);
    if (p) {
      p.avatar = me.avatar;
      p.emoji = me.emoji;
    }
    if (G.room.state === 'waiting') renderRoom(G.room);
    else if ((G.room.type || 'bomb') === 'bomb') renderGame(G.room);
  }

  document.addEventListener('DOMContentLoaded', initLobby);

  /** 给对话/管理面板用：复用同一条 SignalR 连接，不另开 */
  async function invoke(method, ...args) {
    if (!G.conn || G.conn.state !== 'Connected') throw new Error('还没连上，等一秒再试');
    return G.conn.invoke(method, ...args);
  }

  window.BombGame = { connect, disconnect, refreshMe, on, setInventory, invoke };
})();
