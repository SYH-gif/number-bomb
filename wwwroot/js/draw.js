/* =========================================================
   draw.js —— 你画我猜（画板 + 猜词）
   服务端只认归一化坐标 0~1000，前端自己按画布尺寸缩放。
   ========================================================= */
(function () {
  'use strict';

  const { $, $$, toast, nameHtml, S, avatarMarkup } = window.NB;

  /** 本地转义 + 转发到共享连接 */
  const esc = (v) => String(v ?? '').replace(/[&<>"']/g, (c) => (
    { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]
  ));
  const invoke = (...args) => window.BombGame.invoke(...args);

  /** 挂了身份标志的名字放不下，得给容器开换行 */
  const roleCls = (p) => (Array.isArray(p.badges) && p.badges.length ? ' has-roles' : '');

  const D = {
    strokes: new Map(),     // sid -> stroke
    word: '',               // 我知道答案时才有值（画手）
    myChoices: null,        // 挑词候选（画手）
    phase: '',
    phaseUntil: 0,
    timer: null,
    drawing: false,         // 正在落笔
    curStroke: null,        // 正在画的一笔 {pts:[...]}
    curColor: '#20242c',
    curSize: 6,
    dirty: false            // 画布要不要重画
  };

  const canvas = () => $('#drawCanvas');
  const ctx = () => canvas().getContext('2d');

  // ------------------------------------------------------------------ 画布

  function setupCanvas() {
    const c = canvas();
    // 高分屏清晰一点：内部分辨率放大
    const rect = c.getBoundingClientRect();
    const ratio = Math.min(2, window.devicePixelRatio || 1);
    const w = Math.round(rect.width * ratio);
    const h = Math.round(rect.height * ratio);
    if (c.width !== w || c.height !== h) {
      c.width = w;
      c.height = h;
    }
    return { w, h, ratio };
  }

  function paintStroke(st) {
    const { w, h, ratio } = setupCanvas();
    const c = ctx();
    const pts = st.pts || st.Points || [];
    if (pts.length < 2) return;

    c.strokeStyle = st.color || st.Color;
    c.lineWidth = (st.size || st.Size) * ratio * (w / 840);
    c.lineCap = 'round';
    c.lineJoin = 'round';

    c.beginPath();
    for (var i = 0; i + 1 < pts.length; i += 2) {
      var x = (pts[i] / 1000) * w;
      var y = (pts[i + 1] / 1000) * h;
      if (i === 0) c.moveTo(x, y);
      else c.lineTo(x, y);
    }
    c.stroke();
  }

  function repaintAll() {
    const { w, h } = setupCanvas();
    const c = ctx();
    c.clearRect(0, 0, w, h);
    // 背景纸色
    c.fillStyle = '#fbf8f2';
    c.fillRect(0, 0, w, h);
    D.strokes.forEach((st) => paintStroke(st));
    // 正在画的一笔（自己的即时反馈）
    if (D.curStroke && D.curStroke.pts.length >= 2) {
      paintStroke({ color: D.curColor, size: D.curSize, pts: D.curStroke.pts });
    }
    D.dirty = false;
  }

  /** 把指针事件换成归一化坐标 */
  function normPos(e) {
    const rect = canvas().getBoundingClientRect();
    const x = Math.round(Math.max(0, Math.min(1000, ((e.clientX - rect.left) / rect.width) * 1000)));
    const y = Math.round(Math.max(0, Math.min(1000, ((e.clientY - rect.top) / rect.height) * 1000)));
    return [x, y];
  }

  // ------------------------------------------------------------------ 指针

  function onPointerDown(e) {
    if (!amDrawer() || D.phase !== 'drawing') return;
    if (!e.isPrimary) return;
    e.preventDefault();
    canvas().setPointerCapture?.(e.pointerId);
    D.drawing = true;
    D.curStroke = { pts: normPos(e).slice() };
    D.dirty = true;
  }

  function onPointerMove(e) {
    if (!D.drawing || !D.curStroke) return;
    if (!e.isPrimary) return;
    e.preventDefault();
    const p = normPos(e);
    const pts = D.curStroke.pts;
    const last = pts[pts.length - 2];
    const lastY = pts[pts.length - 1];
    // 离上一个点太近就先不记，省流量
    if (Math.abs(p[0] - last) + Math.abs(p[1] - lastY) < 6) return;
    pts.push(p[0], p[1]);
    D.dirty = true;

    // 攒一笔最多 60ms 就发出去
    if (pts.length >= 4) flushStroke(false);
  }

  function onPointerUp(e) {
    if (!D.drawing || !D.curStroke) return;
    flushStroke(true);
    D.drawing = false;
    D.curStroke = null;
  }

  let flushTimer = null;
  function flushStroke(final) {
    if (!D.curStroke || D.curStroke.pts.length < 2) return;
    const pts = D.curStroke.pts.slice();
    clearTimeout(flushTimer);
    flushTimer = null;
    if (final) {
      D.curStroke = null;
    } else {
      // 继续攒：下一批从这一批的最后一个点接着画，线不断
      D.curStroke = { pts: [pts[pts.length - 2], pts[pts.length - 1]] };
    }
    invoke('DrawStroke', D.curSize, D.curColor, pts).catch(() => { /* 掉线了就算了 */ });
  }

  // ------------------------------------------------------------------ 渲染

  function amDrawer() {
    return window.BombGame?.room?.draw?.drawerId === S.me?.id;
  }

  function renderPlayers(s) {
    const meId = S.me?.id;
    const scores = new Map((s.draw?.scores || []).map((x) => [+x.id, x.pts]));
    const drawerId = s.draw?.drawerId;
    const guessed = new Set(s.draw?.guessedIds || []);

    $('#gamePlayers').innerHTML = s.players.map((p) => {
      const isMe = p.id === meId;
      const isDrawer = p.id === drawerId;
      const got = guessed.has(p.id);
      const score = scores.get(p.id) || 0;
      return `
        <div class="player ${isMe ? 'is-me' : ''} ${isDrawer ? 'is-turn' : ''}">
          ${isDrawer ? '<span class="bomb-mark">🖌️</span>' : (got ? '<span class="bomb-mark good">✅</span>' : '')}
          ${avatarMarkup(p, 'avatar avatar-lg')}
          <span class="pname${roleCls(p)}">${nameHtml(p.name, p.badges)}${isMe ? ' · 你' : ''}</span>
          <span class="ppoints">${p.points} 分 · 本轮 ${score}</span>
          ${isDrawer ? '<span class="ptag">正在画</span>' : ''}
        </div>`;
    }).join('');
  }

  function renderWordLine(s) {
    const line = $('#drawWordLine');
    const d = s.draw || {};
    if (s.state === 'finished') {
      line.textContent = `答案是「${d.word || D.word}」`;
      return;
    }
    if (d.phase === 'choosing') {
      line.textContent = amDrawer() ? '挑一个词来画' : `等 ${playerName(s, d.drawerId)} 挑词…`;
      return;
    }
    // drawing
    if (amDrawer() && D.word) {
      line.textContent = `画这个：${D.word}`;
    } else {
      const n = d.wordLen || 0;
      line.textContent = n > 0 ? `答案 ${n} 个字：${'＿ '.repeat(n).trim()}` : '看你发挥';
    }
  }

  function playerName(s, id) {
    return s.players.find((p) => p.id === id)?.name || '画手';
  }

  function startTimer(s) {
    clearInterval(D.timer);
    D.phaseUntil = Date.now() + (s.draw?.phaseLeft || 0) * 1000;
    const paint = () => {
      const left = Math.max(0, Math.round((D.phaseUntil - Date.now()) / 1000));
      const el = $('#drawTimer');
      if (el) {
        el.textContent = left > 0 ? `⏰ ${left}s` : '';
        el.classList.toggle('urgent', left > 0 && left <= 10);
      }
    };
    paint();
    D.timer = setInterval(paint, 500);
  }

  function render(s) {
    const d = s.draw || {};
    const phaseChanged = d.phase !== D.phase || s.round !== (D.round || -1);
    D.phase = d.phase;
    D.round = s.round;

    renderPlayers(s);
    renderWordLine(s);
    startTimer(s);

    // 换了阶段/换了一轮：画布重建（增量事件平时自己画，这里兜底防漂移）
    if (phaseChanged) {
      D.strokes.clear();
      (d.strokes || []).forEach((st) => D.strokes.set(st.sid, st));
      D.dirty = true;
    } else if (D.strokes.size !== (d.strokes || []).length) {
      // 和服务端对个数（撤销/清空没收到事件时兜底）
      (d.strokes || []).forEach((st) => D.strokes.set(st.sid, st));
      if (D.strokes.size > (d.strokes || []).length) D.strokes.clear();
      (d.strokes || []).forEach((st) => D.strokes.set(st.sid, st));
      D.dirty = true;
    }

    // 工具栏只有画手看得到，而且画的时候才用
    $('#drawTools').hidden = !(amDrawer() && d.phase === 'drawing');
    // 挑词卡片
    renderChoices(s);
    // 猜词输入
    const form = $('#drawGuessForm');
    const input = $('#drawInput');
    const canGuess = d.phase === 'drawing' && !amDrawer() && !(d.guessedIds || []).includes(S.me?.id);
    input.disabled = !canGuess;
    $('#drawGuessBtn').disabled = !canGuess;
    input.placeholder = amDrawer()
      ? '你是画手，好好画'
      : (d.guessedIds || []).includes(S.me?.id) ? '猜对啦，等别人' : '猜猜画的是啥';
    if (canGuess && document.body.classList.contains('device-pc')) input.focus();

    repaintAll();
  }

  function renderChoices(s) {
    const box = $('#drawChoices');
    const row = $('#drawChoiceRow');
    const d = s.draw || {};
    const show = d.phase === 'choosing' && amDrawer() && D.myChoices && D.myChoices.length > 0;
    box.hidden = !show;
    if (!show) return;
    row.innerHTML = D.myChoices.map((w) => `<button type="button" class="choice-btn">${esc(w)}</button>`).join('');
    $$('.choice-btn', row).forEach((btn) => {
      btn.addEventListener('click', async () => {
        try {
          const r = await invoke('ChooseWord', D.myChoices.indexOf(btn.textContent));
          if (!r.ok) toast(r.error || '挑不了这个');
          else { D.myChoices = null; box.hidden = true; }
        } catch (_) { }
      });
    });
  }

  // ------------------------------------------------------------------ 事件

  function bindEvents() {
    window.BombGame.on('drawAdd', (st) => {
      D.strokes.set(st.sid, st);
      paintStroke(st);
    });
    window.BombGame.on('drawDel', (p) => {
      D.strokes.delete(p.sid);
      repaintAll();
    });
    window.BombGame.on('drawClear', () => {
      D.strokes.clear();
      repaintAll();
    });
    window.BombGame.on('drawWord', (p) => {
      D.word = p.word || '';
      if (window.BombGame.room) render(window.BombGame.room);
    });
    window.BombGame.on('drawChoices', (p) => {
      D.myChoices = p.words || [];
      if (window.BombGame.room) render(window.BombGame.room);
    });
    window.BombGame.on('drawCorrect', (p) => {
      toast(`🎉 ${p.name} 猜对啦`);
      if (window.BombGame.room) render(window.BombGame.room);
    });
    window.BombGame.on('drawWrong', (p) => {
      addDrawLog(`${p.name}：${p.text}`, 'wrong');
    });
  }

  function addDrawLog(text, cls) {
    const list = $('#drawLog');
    const item = document.createElement('div');
    item.className = `log-item ${cls || ''}`;
    item.textContent = text;
    list.appendChild(item);
    list.scrollTop = list.scrollHeight;
    while (list.children.length > 60) list.removeChild(list.firstChild);
  }

  // ------------------------------------------------------------------ 工具栏

  function bindTools() {
    $('#drawTools').addEventListener('click', (e) => {
      const sw = e.target.closest('.swatch');
      if (sw) {
        D.curColor = sw.dataset.c;
        $$('#drawTools .swatch').forEach((s) => s.classList.toggle('is-active', s === sw));
        return;
      }
      const sz = e.target.closest('.sizer');
      if (sz) {
        D.curSize = Number(sz.dataset.s);
        $$('#drawTools .sizer').forEach((s) => s.classList.toggle('is-active', s === sz));
        return;
      }
      if (e.target.closest('#btnUndo')) invoke('DrawUndo').catch(() => { });
      if (e.target.closest('#btnClearCanvas')) invoke('DrawClear').catch(() => { });
    });

    // 指针事件
    const c = canvas();
    c.addEventListener('pointerdown', onPointerDown);
    c.addEventListener('pointermove', onPointerMove);
    c.addEventListener('pointerup', onPointerUp);
    c.addEventListener('pointercancel', onPointerUp);
    c.addEventListener('pointerleave', (e) => { if (D.drawing) onPointerUp(e); });

    // 防止手机上手滑把整页拖走
    c.style.touchAction = 'none';

    // 猜词
    $('#drawGuessForm').addEventListener('submit', async (e) => {
      e.preventDefault();
      const text = $('#drawInput').value.trim();
      if (!text) return;
      try {
        const r = await invoke('GuessWord', text);
        if (!r.ok) { toast(r.error || '没猜上'); return; }
        $('#drawInput').value = '';
      } catch (_) { }
    });

    bindEvents();
  }

  function reset() {
    clearInterval(D.timer);
    D.timer = null;
    D.strokes.clear();
    D.curStroke = null;
    D.myChoices = null;
    D.word = '';
    D.phase = '';
    D.round = -1;
    $('#drawLog').innerHTML = '';
    $('#drawChoices').hidden = true;
  }

  document.addEventListener('DOMContentLoaded', bindTools);

  window.NBDraw = { render, reset, repaintAll };
})();
