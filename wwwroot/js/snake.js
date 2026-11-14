/* =========================================================
   snake.js —— 贪吃蛇大乱斗
   服务器每 130ms 走一步并把快照推下来，前端只管画和转向。
   ========================================================= */
(function () {
  'use strict';

  const { $, $$, toast, nameHtml, S, avatarMarkup } = window.NB;
  const invoke = (...args) => window.BombGame.invoke(...args);

  /** 挂了身份标志的名字放不下，得给容器开换行 */
  const roleCls = (p) => (Array.isArray(p.badges) && p.badges.length ? ' has-roles' : '');

  const K = {
    lastSent: '',       // 上次发出去的方向，防重复
    lastRender: 0
  };

  const canvas = () => $('#snakeCanvas');

  // ------------------------------------------------------------------ 渲染

  function renderPlayers(s) {
    const meId = S.me?.id;
    const snakes = new Map((s.snake?.snakes || []).map((x) => [+x.id, x]));

    $('#gamePlayers').innerHTML = s.players.map((p) => {
      const isMe = p.id === meId;
      const sp = snakes.get(p.id);
      const alive = sp?.alive;
      const score = sp?.score || 0;
      return `
        <div class="player ${isMe ? 'is-me' : ''} ${!alive && s.state === 'playing' ? 'is-out' : ''}" style="--dot:${sp?.color || 'var(--dim)'}">
          ${alive === false ? '<span class="bomb-mark">💀</span>' : ''}
          ${avatarMarkup(p, 'avatar avatar-lg')}
          <span class="pname${roleCls(p)}">${nameHtml(p.name, p.badges)}${isMe ? ' · 你' : ''}</span>
          <span class="ppoints">${p.points} 分 · 🍎${score}</span>
          ${sp && alive ? '<span class="ptag snake-dot"></span>' : ''}
        </div>`;
    }).join('');
  }

  function draw(s) {
    const c = canvas();
    const ctx = c.getContext('2d');
    const d = s.snake || {};
    const W = d.w || 30;
    const H = d.h || 22;
    const cell = Math.floor(Math.min(c.width / W, c.height / H));
    const ox = Math.floor((c.width - cell * W) / 2);
    const oy = Math.floor((c.height - cell * H) / 2);

    // 背景
    ctx.fillStyle = '#151a13';
    ctx.fillRect(0, 0, c.width, c.height);
    ctx.fillStyle = '#1c231a';
    for (var gy = 0; gy < H; gy++)
      for (var gx = 0; gx < W; gx++)
        if ((gx + gy) % 2 === 0) ctx.fillRect(ox + gx * cell, oy + gy * cell, cell, cell);

    // 场地边框
    ctx.strokeStyle = '#2f3d2c';
    ctx.lineWidth = 2;
    ctx.strokeRect(ox - 1, oy - 1, cell * W + 2, cell * H + 2);

    // 食物
    (d.foods || []).forEach((f) => {
      ctx.fillStyle = '#e03131';
      ctx.beginPath();
      ctx.arc(ox + f[0] * cell + cell / 2, oy + f[1] * cell + cell / 2, cell * 0.32, 0, Math.PI * 2);
      ctx.fill();
      ctx.fillStyle = '#fff';
      ctx.fillRect(ox + f[0] * cell + cell / 2 - 1, oy + f[1] * cell + cell * 0.12, 2, cell * 0.16);
    });

    // 蛇（死的已经没 cells 了）
    (d.snakes || []).forEach((sp) => {
      const cells = sp.cells || [];
      if (cells.length === 0) return;
      const isMe = +sp.id === S.me?.id;

      // 身子
      for (var i = cells.length - 1; i >= 1; i--) {
        ctx.fillStyle = sp.color;
        ctx.globalAlpha = i === 0 ? 1 : Math.max(0.45, 1 - (cells.length - i) / (cells.length + 6));
        roundCell(ctx, ox + cells[i][0] * cell, oy + cells[i][1] * cell, cell, 4);
      }
      // 头
      ctx.globalAlpha = 1;
      ctx.fillStyle = isMe ? '#ffd43b' : sp.color;
      roundCell(ctx, ox + cells[0][0] * cell, oy + cells[0][1] * cell, cell, 6);
      // 眼睛（大概朝着走的方向）
      ctx.fillStyle = '#1a1a1a';
      const hx = ox + cells[0][0] * cell;
      const hy = oy + cells[0][1] * cell;
      ctx.fillRect(hx + cell * 0.25, hy + cell * 0.25, cell * 0.14, cell * 0.14);
      ctx.fillRect(hx + cell * 0.61, hy + cell * 0.25, cell * 0.14, cell * 0.14);
      ctx.globalAlpha = 1;
    });
  }

  function roundCell(ctx, x, y, size, r) {
    ctx.beginPath();
    if (ctx.roundRect) ctx.roundRect(x + 1, y + 1, size - 2, size - 2, r);
    else ctx.rect(x + 1, y + 1, size - 2, size - 2);
    ctx.fill();
  }

  function render(s) {
    renderPlayers(s);
    draw(s);

    const d = s.snake || {};
    const mine = (d.snakes || []).find((x) => +x.id === S.me?.id);
    const pool = $('#snakePool');
    if (pool) pool.textContent = `🪙 ${d.pool || 0}`;

    const line = $('#snakeStatusLine');
    if (line) {
      if (s.state === 'finished') {
        line.textContent = '这一局结束啦';
      } else if (!mine) {
        line.textContent = '看戏模式';
      } else if (mine.alive) {
        line.textContent = `你的蛇 · 🍎${mine.score} 分`;
        line.classList.add('is-me');
      } else {
        line.textContent = '你的蛇挂了，看别人表演吧';
        line.classList.remove('is-me');
      }
    }
  }

  // ------------------------------------------------------------------ 操作

  function sendDir(dx, dy) {
    const key = `${dx},${dy}`;
    if (key === K.lastSent) return;
    K.lastSent = key;
    invoke('SnakeDir', dx, dy).catch(() => { K.lastSent = ''; });
  }

  function bindControls() {
    // 键盘
    document.addEventListener('keydown', (e) => {
      if (S.view !== 'game' || !$('#snakePanel') || $('#snakePanel').hidden) return;
      const map = {
        ArrowUp: [0, -1], ArrowDown: [0, 1], ArrowLeft: [-1, 0], ArrowRight: [1, 0],
        w: [0, -1], s: [0, 1], a: [-1, 0], d: [1, 0],
        W: [0, -1], S: [0, 1], A: [-1, 0], D: [1, 0]
      };
      const dir = map[e.key];
      if (!dir) return;
      e.preventDefault();
      sendDir(dir[0], dir[1]);
    });

    // 手机滑动
    const c = canvas();
    c.style.touchAction = 'none';
    let sx = 0, sy = 0, tracking = false;
    c.addEventListener('pointerdown', (e) => {
      sx = e.clientX; sy = e.clientY; tracking = true;
    });
    c.addEventListener('pointermove', (e) => {
      if (!tracking) return;
      const dx = e.clientX - sx;
      const dy = e.clientY - sy;
      if (Math.abs(dx) < 24 && Math.abs(dy) < 24) return;
      tracking = false;
      if (Math.abs(dx) > Math.abs(dy)) sendDir(dx > 0 ? 1 : -1, 0);
      else sendDir(0, dy > 0 ? 1 : -1);
    });
    c.addEventListener('pointerup', () => { tracking = false; });
    c.addEventListener('pointercancel', () => { tracking = false; });
  }

  function reset() {
    K.lastSent = '';
  }

  document.addEventListener('DOMContentLoaded', bindControls);

  window.NBSnake = { render, reset };
})();
