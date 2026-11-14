/* =========================================================
   avatar.js —— 自己画头像的小画板
   鼠标、手指都能画；有粗细、颜色、橡皮、清空
   ========================================================= */
(function () {
  'use strict';

  const COLORS = ['#111318', '#ffffff', '#ff6b6b', '#ffd15c', '#7ee0c0', '#8fb4ff', '#c792ff', '#ff9bb8'];
  const MAX_SIZE = 512;       // 画板原尺寸：画的时候手感细一点
  const EXPORT_SIZE = 256;    // 存下来的尺寸：头像显示最多几十像素，256 足够清楚

  let modal = null;
  let canvas = null;
  let ctx = null;
  let drawing = false;
  let hasDrawn = false;
  let last = null;
  let color = COLORS[0];
  let size = 10;
  let erasing = false;
  let callback = null;

  const TEMPLATE = `
    <div class="modal-card studio-card">
      <div class="studio-head">
        <h2 class="studio-title">接下来，展示画技吧 ✍️</h2>
        <p class="studio-sub">鼠标或者手指都行，随便画点什么</p>
      </div>
      <div class="canvas-wrap">
        <canvas id="studioCanvas" width="512" height="512"></canvas>
      </div>
      <div class="tools">
        <div class="tool-row">
          <span class="tool-label">颜色</span>
          <div class="swatches" id="studioSwatches"></div>
        </div>
        <div class="tool-row">
          <span class="tool-label">粗细</span>
          <input class="size-slider" id="studioSize" type="range" min="2" max="30" value="10" />
          <span class="size-preview"><span class="size-dot" id="studioDot"></span></span>
        </div>
        <div class="tool-row">
          <button class="btn btn-ghost btn-sm" id="studioClear" type="button">清空</button>
          <span class="hint" style="margin:0">画歪了就用橡皮擦一下</span>
        </div>
      </div>
      <div class="studio-actions">
        <button class="btn btn-ghost" id="studioCancel" type="button">先不画</button>
        <button class="btn btn-primary" id="studioDone" type="button">完成</button>
      </div>
    </div>`;

  function openAvatarStudio(onDone) {
    close();
    callback = onDone;
    hasDrawn = false;
    erasing = false;
    color = COLORS[0];
    size = 10;

    modal = document.createElement('div');
    modal.className = 'modal';
    modal.id = 'modalStudio';
    modal.innerHTML = TEMPLATE;
    document.body.appendChild(modal);

    canvas = modal.querySelector('#studioCanvas');
    ctx = canvas.getContext('2d');
    ctx.lineCap = 'round';
    ctx.lineJoin = 'round';

    buildSwatches();
    bindCanvas();
    bindTools();

    // 手机上别让页面跟着滑
    document.body.style.overflow = 'hidden';
  }

  function close() {
    if (modal) {
      modal.remove();
      modal = null;
      canvas = null;
      ctx = null;
    }
    document.body.style.overflow = '';
  }

  function buildSwatches() {
    const box = modal.querySelector('#studioSwatches');
    box.innerHTML = COLORS.map((c) =>
      `<button type="button" class="swatch ${c === color ? 'is-active' : ''}" data-color="${c}" style="background:${c}"></button>`
    ).join('') +
      `<button type="button" class="swatch is-eraser" data-eraser="1" title="橡皮擦">◻︎</button>`;

    box.querySelectorAll('.swatch').forEach((btn) => {
      btn.addEventListener('click', () => {
        box.querySelectorAll('.swatch').forEach((b) => b.classList.remove('is-active'));
        btn.classList.add('is-active');
        if (btn.dataset.eraser) {
          erasing = true;
        } else {
          erasing = false;
          color = btn.dataset.color;
        }
        updateDot();
      });
    });

    updateDot();
  }

  function updateDot() {
    const dot = modal.querySelector('#studioDot');
    if (!dot) return;
    const px = Math.max(3, Math.round(size * 0.55));
    dot.style.width = px + 'px';
    dot.style.height = px + 'px';
    dot.style.background = erasing ? 'rgba(255,255,255,.35)' : color;
    dot.style.border = erasing ? '1px dashed rgba(255,255,255,.5)' : 'none';
  }

  function bindCanvas() {
    canvas.addEventListener('pointerdown', (e) => {
      e.preventDefault();
      drawing = true;
      hasDrawn = true;
      last = point(e);
      canvas.setPointerCapture?.(e.pointerId);
      dot(last);
    });

    canvas.addEventListener('pointermove', (e) => {
      if (!drawing) return;
      e.preventDefault();
      const p = point(e);
      stroke(last, p);
      last = p;
    });

    const stop = () => { drawing = false; last = null; };
    canvas.addEventListener('pointerup', stop);
    canvas.addEventListener('pointercancel', stop);

    // 老浏览器兜底（没有 PointerEvent 的时候）
    if (!window.PointerEvent) {
      canvas.addEventListener('mousemove', (e) => {
        if (!drawing) return;
        const p = point(e);
        stroke(last || p, p);
        last = p;
      });
      canvas.addEventListener('mouseup', stop);
      canvas.addEventListener('mouseleave', stop);
    }

    // 触屏时不让页面滚动
    canvas.addEventListener('touchmove', (e) => e.preventDefault(), { passive: false });
  }

  function point(e) {
    const rect = canvas.getBoundingClientRect();
    const x = ((e.clientX - rect.left) / rect.width) * canvas.width;
    const y = ((e.clientY - rect.top) / rect.height) * canvas.height;
    return { x, y };
  }

  function applyStyle() {
    ctx.globalCompositeOperation = erasing ? 'destination-out' : 'source-over';
    ctx.strokeStyle = color;
    ctx.fillStyle = color;
    ctx.lineWidth = erasing ? size * 1.6 : size;
  }

  function dot(p) {
    applyStyle();
    ctx.beginPath();
    ctx.arc(p.x, p.y, (erasing ? size * 1.6 : size) / 2, 0, Math.PI * 2);
    ctx.fill();
  }

  function stroke(from, to) {
    applyStyle();
    ctx.beginPath();
    ctx.moveTo(from.x, from.y);
    ctx.lineTo(to.x, to.y);
    ctx.stroke();
  }

  function bindTools() {
    const slider = modal.querySelector('#studioSize');
    slider.addEventListener('input', () => {
      size = Number(slider.value);
      updateDot();
    });

    modal.querySelector('#studioClear').addEventListener('click', () => {
      ctx.clearRect(0, 0, canvas.width, canvas.height);
      hasDrawn = false;
    });

    modal.querySelector('#studioCancel').addEventListener('click', close);

    modal.querySelector('#studioDone').addEventListener('click', () => {
      if (!hasDrawn) {
        window.NB?.toast('先画一笔嘛，空白的头像有点神秘');
        return;
      }
      let dataUrl;
      try {
        // 深色界面里透明底的黑线会看不见，导出时铺一层浅色底。
        // 存 256×256：头像最大也就显示几十像素，512 存下来体积大三四倍，
        // 手机流量下每次别人加载你的头像都要多等一截。
        const out = document.createElement('canvas');
        out.width = out.height = EXPORT_SIZE;
        const octx = out.getContext('2d');
        octx.imageSmoothingEnabled = true;
        octx.imageSmoothingQuality = 'high';
        octx.fillStyle = '#f6f7fb';
        octx.fillRect(0, 0, EXPORT_SIZE, EXPORT_SIZE);
        octx.drawImage(canvas, 0, 0, EXPORT_SIZE, EXPORT_SIZE);
        dataUrl = out.toDataURL('image/png');
      } catch (err) {
        window.NB?.toast('这张没存上，再点一次完成？');
        return;
      }
      const cb = callback;
      close();
      if (cb) cb(dataUrl);
    });

    // 点遮罩 / ESC 退出
    modal.addEventListener('click', (e) => {
      if (e.target === modal) close();
    });
    const esc = (e) => {
      if (e.key === 'Escape') { close(); document.removeEventListener('keydown', esc); }
    };
    document.addEventListener('keydown', esc);
  }

  window.openAvatarStudio = openAvatarStudio;
})();
