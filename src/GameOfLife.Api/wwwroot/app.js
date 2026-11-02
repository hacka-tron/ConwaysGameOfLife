// Browser client for the Game of Life REST API. No build step and no dependencies.
// All server data is written with textContent, never innerHTML.

const MAX_SIDE = 256;
const LOG_LIMIT = 50;
// Requests faster than this never show a busy state, so quick clicks do not flash every control.
const BUSY_DELAY_MS = 300;
const MAX_CELL_PX = 40;
const GRID_MIN_CELL_PX = 6;
const RANDOM_DENSITY = 0.3;
// Keep in sync with the 900px breakpoint in styles.css.
const WIDE_QUERY = window.matchMedia('(min-width: 900px)');
const DARK_QUERY = window.matchMedia('(prefers-color-scheme: dark)');

// Play speeds in boards per second. The server's MaxBoardsPerSecond is always the top speed, and
// slower speeds above it are dropped, so the UI never asks for more than the server allows.
const BASE_SPEEDS = [1, 2, 4, 10, 20];
const DEFAULT_SPEED = 4;

// Playback buffering. Play asks for boards in batches with GET /boards/{id}/sequence and keeps the ones
// it has not shown yet in a buffer. The refill point and the batch size are set in time, then turned
// into boards at the chosen speed, so playback never waits on the network at any speed:
//   refill when buffered boards <= speed x BUFFER_ROUND_TRIPS x round trip
//   batch size = speed x BATCH_SECONDS, between MIN_BATCH and the server's MaxBatchSize
// The round trip is the slowest of the last few batches, so one slow answer raises the refill point.
// A failed batch is retried once before playback stops.
const MAX_FAILED_BATCHES = 2;
const BUFFER_ROUND_TRIPS = 2;
const BATCH_SECONDS = 3;
// The first batch after Play is smaller, so the first frame does not wait for a full batch.
const FIRST_BATCH_SECONDS = 1;
const MIN_BATCH = 10;
const MIN_REFILL_AT = 2;
const DEFAULT_ROUND_TRIP_MS = 300;
const ROUND_TRIP_SAMPLES = 5;

// Patterns use O for a live cell and . for a dead one.
const PRESETS = [
  { key: 'glider', name: 'Glider', rows: ['.O.', '..O', 'OOO'] },
  { key: 'blinker', name: 'Blinker', rows: ['OOO'] },
  { key: 'block', name: 'Block', rows: ['OO', 'OO'] },
  { key: 'r-pentomino', name: 'R-pentomino', rows: ['.OO', 'OO.', '.O.'] },
  {
    key: 'pulsar',
    name: 'Pulsar',
    rows: [
      '..OOO...OOO..',
      '.............',
      'O....O.O....O',
      'O....O.O....O',
      'O....O.O....O',
      '..OOO...OOO..',
      '.............',
      '..OOO...OOO..',
      'O....O.O....O',
      'O....O.O....O',
      'O....O.O....O',
      '.............',
      '..OOO...OOO..',
    ],
  },
  {
    key: 'glider-gun',
    name: 'Glider gun',
    rows: [
      '........................O...........',
      '......................O.O...........',
      '............OO......OO............OO',
      '...........O...O....OO............OO',
      'OO........O.....O...OO..............',
      'OO........O...O.OO....O.O...........',
      '..........O.....O.......O...........',
      '...........O...O....................',
      '............OO......................',
    ],
  },
];

const FALLBACK_PROBLEMS = {
  400: { title: 'Request rejected', detail: 'The server did not accept this request.' },
  404: { title: 'Not found', detail: 'The server has no board with this ID.' },
  413: { title: 'Board too large', detail: 'The request body is larger than the server accepts.' },
  429: { title: 'Too many boards requested', detail: 'The server limits how many boards each client receives per second.' },
  422: { title: 'Board did not settle', detail: 'The board did not reach a still life or a repeating cycle within the server step limit.' },
  500: { title: 'Server error', detail: 'The server failed while handling the request.' },
  507: { title: 'Storage is full', detail: 'The server has no room left to store boards.' },
};

const $ = (id) => document.getElementById(id);

const el = {
  health: $('health'),
  healthText: $('health-text'),
  stateBadge: $('state-badge'),
  boardId: $('board-id'),
  copyId: $('copy-id'),
  copyIdLabel: $('copy-id-label'),
  edgeInputs: document.querySelectorAll('#edge-modes input'),
  load: $('load'),
  metaAlive: $('meta-alive'),
  metaGenWrap: $('meta-gen-wrap'),
  metaGen: $('meta-gen'),
  stage: $('stage'),
  canvas: $('board'),
  boardFoot: document.querySelector('.board-foot'),
  draftNote: $('draft-note'),
  coords: $('coords'),
  width: $('width'),
  height: $('height'),
  clear: $('clear'),
  random: $('random'),
  presets: $('presets'),
  boardMsg: $('board-msg'),
  loadForm: $('load-form'),
  loadId: $('load-id'),
  loadMsg: $('load-msg'),
  next: $('next'),
  final: $('final'),
  jumpForm: $('jump-form'),
  jumpN: $('jump-n'),
  jump: $('jump'),
  play: $('play'),
  playLabel: $('play-label'),
  speed: $('speed'),
  speedOut: $('speed-out'),
  opsMsg: $('ops-msg'),
  finalCard: $('final-card'),
  finalCanvas: $('final-canvas'),
  finalKind: $('final-kind'),
  finalSummary: $('final-summary'),
  finalSteps: $('final-steps'),
  finalPeriod: $('final-period'),
  finalFrom: $('final-from'),
  logCount: $('log-count'),
  logBody: $('log-body'),
  logEmpty: $('log-empty'),
};

const state = {
  width: 40,
  height: 28,
  cells: new Uint8Array(40 * 28),
  // The board last returned by the server. Null until the first save or load.
  // gen is a BigInt counting generations since the last save or load, or null when unknown.
  stored: null,
  edited: false,
  // busy blocks new actions at once. showBusy greys out the controls, only once a request is slow.
  busy: false,
  showBusy: false,
  busyTimer: 0,
  playing: false,
  playTimer: 0,
  // Buffered boards for Play. Replaced on every Play and dropped by stopPlay, so answers that arrive
  // after the board changed are ignored.
  playback: null,
  // The buffer kept by Pause, with the board it was paused on. Play on the same board picks it up again.
  parked: null,
  // From GET /settings. These defaults match the server's and apply until it answers.
  limits: { maxBoardsPerSecond: 30, maxBatchSize: 120 },
  speeds: [],
  // The board stepping started from, with its generation and its final state once fetched. Next, Jump
  // and Play share it until the board is edited or another board is loaded, and the Final state panel
  // appears once stepping reaches the final state.
  run: null,
  logTotal: 0,
  // Edge rule of the drawn board, sent with every save. It is part of the board's ID.
  edgeMode: 'dead',
  // True when the browser cannot work out IDs, because hashing is unavailable.
  idUnavailable: false,
  // ID of the drawn board, worked out in the browser. Null while it is being computed.
  localId: null,
  idRequest: 0,
};

// ---------------------------------------------------------------- API

async function api(method, path, body) {
  const started = performance.now();
  const headers = { Accept: 'application/json, application/problem+json, text/plain' };
  if (body !== undefined) headers['Content-Type'] = 'application/json';

  let response;
  let text = '';
  try {
    response = await fetch(path, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
      cache: 'no-store',
    });
    text = await response.text();
  } catch {
    logRequest(method, path, 0, performance.now() - started);
    return {
      ok: false,
      status: 0,
      problem: {
        status: 0,
        title: 'Could not reach the API',
        detail: 'The request failed before the server answered. Check that the API is running.',
        errors: null,
      },
    };
  }

  const ms = performance.now() - started;
  let data = null;
  if (text) {
    try {
      data = JSON.parse(text);
    } catch {
      data = null;
    }
  }
  const returnedId = data?.id ?? data?.board?.id;
  logRequest(method, path, response.status, ms, typeof returnedId === 'string' ? returnedId : null);

  if (response.ok) return { ok: true, status: response.status, data, text };
  return { ok: false, status: response.status, problem: toProblem(response, data) };
}

function toProblem(response, data) {
  const fallback = FALLBACK_PROBLEMS[response.status] ?? {
    title: `HTTP ${response.status}${response.statusText ? ` ${response.statusText}` : ''}`,
    detail: 'The server returned an unexpected error.',
  };
  const body = data && typeof data === 'object' && !Array.isArray(data) ? data : {};
  const messages = [];
  if (body.errors && typeof body.errors === 'object') {
    for (const value of Object.values(body.errors)) {
      for (const message of Array.isArray(value) ? value : [value]) {
        if (typeof message === 'string' && message.trim()) messages.push(message);
      }
    }
  }
  const title = typeof body.title === 'string' && body.title.trim() ? body.title : fallback.title;
  let detail = typeof body.detail === 'string' && body.detail.trim() ? body.detail : '';
  if (!detail && messages.length === 0) detail = fallback.detail;
  const retryAfterSeconds = Number.isInteger(body.retryAfterSeconds) ? body.retryAfterSeconds : null;
  return { status: response.status, title, detail, errors: messages.length ? messages : null, retryAfterSeconds };
}

function badResponse(status) {
  return {
    status,
    title: 'Unexpected response',
    detail: 'The server answered, but the body was not the board the API contract describes.',
    errors: null,
  };
}

function isBoardResponse(data) {
  return (
    data &&
    typeof data.id === 'string' &&
    Number.isInteger(data.width) &&
    Number.isInteger(data.height) &&
    data.width > 0 &&
    data.height > 0 &&
    Array.isArray(data.cells) &&
    data.cells.length === data.height &&
    data.cells.every((row) => Array.isArray(row) && row.length === data.width)
  );
}

// ---------------------------------------------------------------- Board IDs

// Mirrors Board.ComputeId on the server, so a drawn board shows the ID the server will give it.
// Every board the server returns is checked against this, so the two cannot drift apart unnoticed.
const ID_VERSION = 'gol-v2';
const ID_ALPHABET = '0123456789abcdefghjkmnpqrstvwxyz';
const ID_LENGTH = 26;

async function computeId(width, height, edgeMode, cells) {
  const header = new TextEncoder().encode(`${ID_VERSION}|${edgeMode}|${width}x${height}|`);
  const data = new Uint8Array(header.length + Math.ceil((width * height) / 8));
  data.set(header);
  // Row-major bitset, least significant bit first, matching the server's packed cells.
  for (let i = 0; i < cells.length; i++) {
    if (cells[i]) data[header.length + (i >> 3)] |= 1 << (i & 7);
  }
  const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', data));
  let value = 0n;
  for (let i = 0; i < 16; i++) value = (value << 8n) | BigInt(digest[i]);
  let id = '';
  for (let i = 0; i < ID_LENGTH; i++) {
    id = ID_ALPHABET[Number(value & 31n)] + id;
    value >>= 5n;
  }
  return id;
}

async function refreshLocalId() {
  const request = ++state.idRequest;
  const { width, height, edgeMode } = state;
  const cells = state.cells.slice();
  // Cleared before hashing so Copy ID never offers the previous drawing's ID.
  state.localId = null;
  let id;
  try {
    id = await computeId(width, height, edgeMode, cells);
  } catch {
    // crypto.subtle only exists on https and localhost. Operations still work, since the server assigns IDs.
    state.idUnavailable = true;
    updateUI();
    return null;
  }
  // A later edit started its own computation. Keep only the newest.
  if (request !== state.idRequest) return null;
  state.localId = id;
  updateUI();
  return id;
}

async function checkServerId(board) {
  let local;
  try {
    local = await computeId(board.width, board.height, board.edgeMode, fromRows(board.cells, board.width, board.height));
  } catch {
    return;
  }
  // Skip boards that are no longer current by the time the hash finishes.
  if (state.stored?.id !== board.id) return;
  if (local !== board.id) {
    showInfo(
      el.boardMsg,
      `The browser worked out ID ${local} for this board but the server says ${board.id}. The two ID formats are out of step.`,
      'error',
    );
  }
}

// ---------------------------------------------------------------- Board model

function index(x, y) {
  return y * state.width + x;
}

function population(cells) {
  let count = 0;
  for (let i = 0; i < cells.length; i++) count += cells[i];
  return count;
}

function toRows() {
  const rows = [];
  for (let y = 0; y < state.height; y++) {
    rows.push(Array.from(state.cells.subarray(y * state.width, (y + 1) * state.width)));
  }
  return rows;
}

function fromRows(rows, width, height) {
  const cells = new Uint8Array(width * height);
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) cells[y * width + x] = rows[y][x] ? 1 : 0;
  }
  return cells;
}

function sameCells(a, b) {
  if (a.length !== b.length) return false;
  for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return false;
  return true;
}

function resize(width, height) {
  if (width === state.width && height === state.height) return;
  const next = new Uint8Array(width * height);
  const w = Math.min(width, state.width);
  const h = Math.min(height, state.height);
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) next[y * width + x] = state.cells[y * state.width + x];
  }
  state.width = width;
  state.height = height;
  state.cells = next;
  syncSizeInputs();
}

// Called after any local change to the drawn cells.
function boardEdited() {
  const s = state.stored;
  state.edited =
    !s ||
    s.width !== state.width ||
    s.height !== state.height ||
    s.edgeMode !== state.edgeMode ||
    !sameCells(s.cells, state.cells);
  scheduleDraw();
  refreshLocalId();
  updateUI();
}

function setCurrent(board, { gen }) {
  state.width = board.width;
  state.height = board.height;
  state.cells = fromRows(board.cells, board.width, board.height);
  state.stored = {
    id: board.id,
    edgeMode: board.edgeMode,
    width: board.width,
    height: board.height,
    cells: state.cells.slice(),
    gen,
  };
  state.edgeMode = board.edgeMode;
  syncEdgeInputs();
  state.edited = false;
  state.localId = board.id;
  state.idRequest++;
  checkServerId(board);
  syncSizeInputs();
  layoutCanvas();
  updateUI();
}

// ---------------------------------------------------------------- Drawing

const ctx = el.canvas.getContext('2d');
let colors = readColors();
let view = { cell: 10 };
let drawQueued = false;
// The last final board, kept so a theme change can repaint its thumbnail.
let finalBoard = null;

function readColors() {
  const css = getComputedStyle(document.documentElement);
  const read = (name, fallback) => css.getPropertyValue(name).trim() || fallback;
  return {
    alive: read('--cell-alive', '#2557d6'),
    dead: read('--cell-dead', '#ffffff'),
    grid: read('--cell-grid', '#e3e6eb'),
  };
}

function availableSize() {
  const styles = getComputedStyle(el.stage);
  const padX = parseFloat(styles.paddingLeft) + parseFloat(styles.paddingRight);
  const padY = parseFloat(styles.paddingTop) + parseFloat(styles.paddingBottom);
  const width = Math.max(40, el.stage.clientWidth - padX);
  const height = WIDE_QUERY.matches
    ? Math.max(40, el.stage.clientHeight - padY)
    : Math.max(160, Math.round(window.innerHeight * 0.62));
  return { width, height };
}

function layoutCanvas() {
  const avail = availableSize();
  const fit = Math.min(avail.width / state.width, avail.height / state.height);
  // Whole pixels keep cells even. Below 2px per cell, fractional sizes let 256 columns fit a phone.
  view.cell = fit >= 2 ? Math.min(MAX_CELL_PX, Math.floor(fit)) : Math.max(fit, 0.25);
  draw();
}

function paint(canvas, width, height, cells, cell, withGrid) {
  const dpr = window.devicePixelRatio || 1;
  const cssW = width * cell;
  const cssH = height * cell;
  const pxW = Math.max(1, Math.round(cssW * dpr));
  const pxH = Math.max(1, Math.round(cssH * dpr));
  if (canvas.width !== pxW) canvas.width = pxW;
  if (canvas.height !== pxH) canvas.height = pxH;
  canvas.style.width = `${pxW / dpr}px`;
  canvas.style.height = `${pxH / dpr}px`;

  const g = canvas.getContext('2d');
  g.setTransform(1, 0, 0, 1, 0, 0);
  g.fillStyle = colors.dead;
  g.fillRect(0, 0, pxW, pxH);

  // Cell edges are rounded to device pixels so neighbors never overlap or leave gaps.
  const xs = new Array(width + 1);
  const ys = new Array(height + 1);
  for (let x = 0; x <= width; x++) xs[x] = Math.round((x * pxW) / width);
  for (let y = 0; y <= height; y++) ys[y] = Math.round((y * pxH) / height);

  g.fillStyle = colors.alive;
  for (let y = 0; y < height; y++) {
    const row = y * width;
    for (let x = 0; x < width; x++) {
      if (cells[row + x]) g.fillRect(xs[x], ys[y], xs[x + 1] - xs[x], ys[y + 1] - ys[y]);
    }
  }

  if (withGrid && cell >= GRID_MIN_CELL_PX) {
    g.fillStyle = colors.grid;
    const line = Math.max(1, Math.round(dpr));
    for (let x = 1; x < width; x++) g.fillRect(xs[x], 0, line, pxH);
    for (let y = 1; y < height; y++) g.fillRect(0, ys[y], pxW, line);
  }
}

function draw() {
  drawQueued = false;
  paint(el.canvas, state.width, state.height, state.cells, view.cell, true);
}

function scheduleDraw() {
  if (drawQueued) return;
  drawQueued = true;
  requestAnimationFrame(draw);
}

// ---------------------------------------------------------------- Editing

let drag = null;

function cellFromEvent(event, clamp) {
  const rect = el.canvas.getBoundingClientRect();
  let x = Math.floor(((event.clientX - rect.left) / rect.width) * state.width);
  let y = Math.floor(((event.clientY - rect.top) / rect.height) * state.height);
  if (clamp) {
    x = Math.min(state.width - 1, Math.max(0, x));
    y = Math.min(state.height - 1, Math.max(0, y));
  } else if (x < 0 || y < 0 || x >= state.width || y >= state.height) {
    return null;
  }
  return { x, y };
}

// Fills every cell on the line between two points so fast drags leave no gaps.
function paintLine(x0, y0, x1, y1, value) {
  const dx = Math.abs(x1 - x0);
  const dy = -Math.abs(y1 - y0);
  const sx = x0 < x1 ? 1 : -1;
  const sy = y0 < y1 ? 1 : -1;
  let err = dx + dy;
  for (;;) {
    state.cells[index(x0, y0)] = value;
    if (x0 === x1 && y0 === y1) return;
    const e2 = 2 * err;
    if (e2 >= dy) {
      err += dy;
      x0 += sx;
    }
    if (e2 <= dx) {
      err += dx;
      y0 += sy;
    }
  }
}

function editingLocked() {
  return state.busy && !state.playing;
}

el.canvas.addEventListener('pointerdown', (event) => {
  if (event.button !== 0 || editingLocked()) return;
  const cell = cellFromEvent(event, false);
  if (!cell) return;
  event.preventDefault();
  if (state.playing) stopPlay('Playback paused because the board was edited.');
  el.canvas.setPointerCapture(event.pointerId);
  const value = state.cells[index(cell.x, cell.y)] ? 0 : 1;
  drag = { pointerId: event.pointerId, value, x: cell.x, y: cell.y };
  state.cells[index(cell.x, cell.y)] = value;
  boardEdited();
});

el.canvas.addEventListener('pointermove', (event) => {
  const hover = cellFromEvent(event, false);
  el.coords.textContent = hover ? `x ${hover.x}  y ${hover.y}` : '';
  if (!drag || event.pointerId !== drag.pointerId) return;
  if (event.pointerType === 'mouse' && (event.buttons & 1) === 0) {
    // The button was released somewhere the canvas never heard about.
    drag = null;
    return;
  }
  const cell = cellFromEvent(event, true);
  if (cell.x === drag.x && cell.y === drag.y) return;
  paintLine(drag.x, drag.y, cell.x, cell.y, drag.value);
  drag.x = cell.x;
  drag.y = cell.y;
  boardEdited();
});

function endDrag(event) {
  if (drag && event.pointerId === drag.pointerId) drag = null;
}

el.canvas.addEventListener('pointerup', endDrag);
el.canvas.addEventListener('pointercancel', endDrag);
el.canvas.addEventListener('lostpointercapture', endDrag);
el.canvas.addEventListener('pointerleave', () => {
  el.coords.textContent = '';
});

function readSide(input) {
  const value = Number.parseInt(input.value, 10);
  if (!Number.isFinite(value)) return null;
  return Math.min(MAX_SIDE, Math.max(1, value));
}

function syncSizeInputs() {
  if (document.activeElement !== el.width) el.width.value = String(state.width);
  if (document.activeElement !== el.height) el.height.value = String(state.height);
}

function applySizeInputs() {
  const width = readSide(el.width);
  const height = readSide(el.height);
  if (width === null || height === null) return;
  if (width === state.width && height === state.height) return;
  if (state.playing) stopPlay('Playback paused because the board was resized.');
  resize(width, height);
  layoutCanvas();
  boardEdited();
}

for (const input of [el.width, el.height]) {
  // Applied on change, not on every keystroke, so typing 40 over 32 never passes through 4 and drops cells.
  input.addEventListener('change', () => {
    if (!editingLocked()) applySizeInputs();
    // Snap whatever was typed to the 1 to 256 range the inputs allow.
    input.value = String(input === el.width ? state.width : state.height);
  });
}

function syncEdgeInputs() {
  for (const input of el.edgeInputs) input.checked = input.value === state.edgeMode;
}

// Switching edges makes a different board, with its own ID.
for (const input of el.edgeInputs) {
  input.addEventListener('change', () => {
    localEdit(() => {
      state.edgeMode = input.value;
    });
    syncEdgeInputs();
  });
}

function localEdit(action) {
  if (editingLocked()) return;
  if (state.playing) stopPlay('Playback paused because the board was edited.');
  hideMsg(el.boardMsg);
  hideMsg(el.loadMsg);
  hideFinal();
  action();
  boardEdited();
}

el.clear.addEventListener('click', () => localEdit(() => state.cells.fill(0)));

el.random.addEventListener('click', () =>
  localEdit(() => {
    for (let i = 0; i < state.cells.length; i++) state.cells[i] = Math.random() < RANDOM_DENSITY ? 1 : 0;
  }),
);

for (const preset of PRESETS) {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'btn btn-chip';
  button.textContent = preset.name;
  button.addEventListener('click', () => placePreset(preset));
  el.presets.append(button);
}

function placePreset(preset) {
  localEdit(() => {
    state.cells.fill(0);
    const pw = preset.rows[0].length;
    const ph = preset.rows.length;
    if (pw > state.width || ph > state.height) {
      resize(Math.max(pw, state.width), Math.max(ph, state.height));
      layoutCanvas();
      showInfo(el.boardMsg, `The board grew to ${state.width} by ${state.height} so the ${preset.name.toLowerCase()} fits.`);
    }
    const ox = Math.floor((state.width - pw) / 2);
    const oy = Math.floor((state.height - ph) / 2);
    for (let y = 0; y < ph; y++) {
      for (let x = 0; x < pw; x++) {
        if (preset.rows[y][x] === 'O') state.cells[index(ox + x, oy + y)] = 1;
      }
    }
  });
}

// ---------------------------------------------------------------- Messages

function hideMsg(target) {
  target.hidden = true;
  target.replaceChildren();
  delete target.dataset.kind;
}

function showInfo(target, text, kind = 'info') {
  const p = document.createElement('p');
  p.textContent = text;
  target.replaceChildren(p);
  target.dataset.kind = kind;
  target.hidden = false;
}

function showProblem(target, problem) {
  const head = document.createElement('div');
  head.className = 'msg-title';
  const title = document.createElement('span');
  title.textContent = problem.title;
  head.append(title);
  if (problem.status) {
    const status = document.createElement('span');
    status.className = 'msg-status';
    status.textContent = `HTTP ${problem.status}`;
    head.append(status);
  }
  const parts = [head];
  if (problem.detail) {
    const p = document.createElement('p');
    p.textContent = problem.detail;
    parts.push(p);
  }
  if (problem.errors) {
    const list = document.createElement('ul');
    for (const message of problem.errors) {
      const item = document.createElement('li');
      item.textContent = message;
      list.append(item);
    }
    parts.push(list);
  }
  target.replaceChildren(...parts);
  target.dataset.kind = 'error';
  target.hidden = false;
}

// ---------------------------------------------------------------- Formatting

function shortId(id) {
  return id.length > 12 ? `${id.slice(0, 8)}…${id.slice(-4)}` : id;
}

function formatBig(value) {
  return BigInt(value).toLocaleString('en-US');
}

function plural(count, one, many) {
  return `${formatBig(count)} ${BigInt(count) === 1n ? one : many}`;
}

// The ID shown for the current board. The stored ID, or the one worked out for an unsaved drawing.
function shownId() {
  return canOperate() ? state.stored.id : state.localId;
}

// ---------------------------------------------------------------- UI state

function canOperate() {
  return Boolean(state.stored) && !state.edited;
}

function updateUI() {
  const s = state.stored;
  const operable = canOperate();

  if (!operable) {
    el.stateBadge.dataset.kind = s ? 'edited' : 'draft';
    el.stateBadge.textContent = 'Not saved yet';
    el.boardId.textContent = state.localId ? shortId(state.localId) : state.idUnavailable ? 'ID unavailable' : 'Working out ID';
    el.boardId.title = state.localId ? `${state.localId}. Saved automatically when you run an operation.` : '';
    el.boardId.classList.remove('is-stored');
  } else {
    el.stateBadge.dataset.kind = 'stored';
    el.stateBadge.textContent = 'Stored';
    el.boardId.textContent = shortId(s.id);
    el.boardId.title = s.id;
    el.boardId.classList.add('is-stored');
  }
  el.copyId.hidden = !shownId();

  el.metaAlive.textContent = population(state.cells).toLocaleString('en-US');
  el.metaGenWrap.hidden = !operable || s.gen === null;
  if (operable && s.gen !== null) el.metaGen.textContent = formatBig(s.gen);

  el.boardFoot.classList.toggle('is-edited', state.edited && Boolean(s));
  el.draftNote.textContent =
    state.edited && s
      ? 'This board has changed since it was stored. Running an operation saves it first.'
      : 'Click or drag to toggle cells. Dragging paints the opposite of the first cell you touch.';

  const idle = !state.showBusy && !state.playing;
  // Read-only rather than disabled, so the field keeps focus while a load runs.
  el.loadId.readOnly = state.showBusy && !state.playing;
  el.next.disabled = !idle;
  el.final.disabled = !idle;
  el.jump.disabled = !idle;
  el.play.disabled = !idle && !state.playing;
  el.play.setAttribute('aria-pressed', String(state.playing));
  el.playLabel.textContent = state.playing ? 'Pause' : 'Play';

  const locked = state.showBusy && !state.playing;
  // Loading during Play is allowed. It stops playback first.
  el.load.disabled = state.showBusy && !state.playing;
  for (const control of [el.clear, el.random, el.width, el.height, ...el.presets.children, ...el.edgeInputs]) {
    control.disabled = locked;
  }
  el.canvas.classList.toggle('is-locked', locked);

}

// Runs one request-driven action with the controls locked and a spinner on its button.
async function runBusy(button, action) {
  if (state.busy) return;
  state.busy = true;
  state.busyTimer = setTimeout(() => {
    state.showBusy = true;
    button?.classList.add('is-busy');
    updateUI();
  }, BUSY_DELAY_MS);
  try {
    await action();
  } finally {
    clearTimeout(state.busyTimer);
    state.busy = false;
    state.showBusy = false;
    button?.classList.remove('is-busy');
    updateUI();
  }
}

// ---------------------------------------------------------------- Operations

// Sends the drawn board to POST /boards and makes the stored copy current. Returns the result, or null after
// showing the problem in target.
async function saveBoard(target) {
  const result = await api('POST', '/boards', { cells: toRows(), edgeMode: state.edgeMode });
  if (!result.ok) {
    showProblem(target, result.problem);
    return null;
  }
  if (!isBoardResponse(result.data)) {
    showProblem(target, badResponse(result.status));
    return null;
  }
  setCurrent(result.data, { gen: 0n });
  return result;
}

// Operations run on stored boards. Saves the drawn board first when it is not stored yet.
async function ensureStored(target) {
  if (canOperate()) return state.stored;
  return (await saveBoard(target)) ? state.stored : null;
}

// IDs are lowercase, but pasted ones may not be. Crockford base32 treats case as insignificant.
function enteredId() {
  return el.loadId.value.trim().toLowerCase();
}

function loadById() {
  if (state.playing) stopPlay();
  hideMsg(el.loadMsg);
  hideFinal();
  hideMsg(el.opsMsg);
  const id = enteredId();
  if (!id) {
    el.loadId.setAttribute('aria-invalid', 'true');
    showProblem(el.loadMsg, { status: 0, title: 'No ID entered', detail: 'Paste the ID of a stored board.', errors: null });
    return;
  }
  el.loadId.removeAttribute('aria-invalid');
  return runBusy(null, async () => {
    const result = await api('GET', `/boards/${encodeURIComponent(id)}`);
    if (result.status === 404) {
      result.problem.detail =
        'No board with this ID is stored. An ID is a one-way hash of a board, so the board cannot be rebuilt from it. Draw the board and run an operation to store it.';
    }
    if (!result.ok) return showProblem(el.loadMsg, result.problem);
    if (!isBoardResponse(result.data)) return showProblem(el.loadMsg, badResponse(result.status));
    setCurrent(result.data, { gen: 0n });
    showInfo(el.loadMsg, `Loaded board ${shortId(result.data.id)} from the server.`, 'success');
  });
}

async function fetchNext() {
  const s = state.stored;
  const result = await api('GET', `/boards/${encodeURIComponent(s.id)}/next`);
  if (result.ok && !isBoardResponse(result.data)) return { ok: false, problem: badResponse(result.status) };
  return result;
}

function nextGeneration() {
  hideMsg(el.opsMsg);
  hideMsg(el.loadMsg);
  return runBusy(el.next, async () => {
    const s = await ensureStored(el.opsMsg);
    if (!s) return;
    startRun();
    const result = await fetchNext();
    if (!result.ok) return showProblem(el.opsMsg, result.problem);
    setCurrent(result.data, { gen: s.gen === null ? null : s.gen + 1n });
    showFinalIfReached();
    if (result.data.id === s.id) {
      showInfo(el.opsMsg, 'The next generation is identical, so this board is a still life.');
    }
  });
}

function jumpAhead() {
  hideMsg(el.opsMsg);
  hideMsg(el.loadMsg);
  const raw = el.jumpN.value.trim().replace(/[,_\s]/g, '');
  if (!/^\d+$/.test(raw)) {
    el.jumpN.setAttribute('aria-invalid', 'true');
    showProblem(el.opsMsg, {
      status: 0,
      title: 'Not a valid number',
      detail: 'Enter a whole number of generations, zero or more.',
      errors: null,
    });
    return;
  }
  el.jumpN.removeAttribute('aria-invalid');
  const n = BigInt(raw);
  return runBusy(el.jump, async () => {
    const s = await ensureStored(el.opsMsg);
    if (!s) return;
    startRun();
    const result = await api('GET', `/boards/${encodeURIComponent(s.id)}/generations/${n.toString()}`);
    if (!result.ok) return showProblem(el.opsMsg, result.problem);
    if (!isBoardResponse(result.data)) return showProblem(el.opsMsg, badResponse(result.status));
    setCurrent(result.data, { gen: s.gen === null ? null : s.gen + n });
    showFinalIfReached();
  });
}

function isFinalResponse(data) {
  return (
    data &&
    isBoardResponse(data.board) &&
    Number.isInteger(data.steps) &&
    Number.isInteger(data.period) &&
    data.steps >= 0 &&
    data.period >= 1
  );
}

function findFinal() {
  hideMsg(el.opsMsg);
  hideMsg(el.loadMsg);
  hideFinal();
  return runBusy(el.final, async () => {
    const s = await ensureStored(el.opsMsg);
    if (!s) return;
    const result = await api('GET', `/boards/${encodeURIComponent(s.id)}/final`);
    if (!result.ok) return showProblem(el.opsMsg, result.problem);
    const data = result.data;
    if (!isFinalResponse(data)) return showProblem(el.opsMsg, badResponse(result.status));
    setCurrent(data.board, { gen: s.gen === null ? null : s.gen + BigInt(data.steps) });
    state.run = { fromId: s.id, startGen: s.gen, final: data };
    showFinal(data, s.id);
  });
}

// Fits the final board thumbnail into roughly 300 by 180 pixels.
function finalCellSize(board) {
  const fit = Math.min(300 / board.width, 180 / board.height);
  return fit >= 2 ? Math.min(12, Math.floor(fit)) : Math.max(0.5, fit);
}

// The Final state panel describes one Find final state result, so anything that changes the board hides it.
function hideFinal() {
  el.finalCard.hidden = true;
  finalBoard = null;
  state.run = null;
}

// Starts a run from the current board if none is going, and asks for its final state alongside the
// stepping. If the board never settles within the step limit, the panel simply never appears.
async function startRun() {
  if (state.run) return;
  const run = { fromId: state.stored.id, startGen: state.stored.gen, final: null };
  state.run = run;
  const result = await api('GET', `/boards/${encodeURIComponent(run.fromId)}/final`);
  // hideFinal replaces the run when the board changes, which drops answers that no longer apply.
  if (state.run !== run || !result.ok || !isFinalResponse(result.data)) return;
  run.final = result.data;
  // Stepping may already have reached the final state by the time the answer arrives.
  showFinalIfReached();
}

// Reached once the current board is the final board, or is at least the final state's number of
// steps past the start, which means it is inside the final cycle.
function showFinalIfReached() {
  const run = state.run;
  if (!run?.final || !el.finalCard.hidden || !canOperate()) return;
  const s = state.stored;
  const reached =
    s.id === run.final.board.id ||
    (s.gen !== null && run.startGen !== null && s.gen - run.startGen >= BigInt(run.final.steps));
  if (reached) showFinal(run.final, run.fromId);
}

function showFinal(data, fromId) {
  const oscillator = data.kind === 'oscillator' || (data.kind !== 'still-life' && data.period > 1);
  const steps = BigInt(data.steps);
  el.finalKind.textContent = oscillator ? `Oscillator with period ${data.period}` : 'Still life';

  let summary;
  if (oscillator) {
    summary =
      steps === 0n
        ? `The board is already in a cycle that repeats every ${plural(data.period, 'generation', 'generations')}.`
        : `The board enters a cycle after ${plural(steps, 'generation', 'generations')} and then repeats every ${plural(data.period, 'generation', 'generations')}.`;
    summary += ' The board shown is the cycle member with the smallest ID.';
  } else {
    summary =
      steps === 0n
        ? 'The board is already settled and never changes.'
        : `The board stops changing after ${plural(steps, 'generation', 'generations')}.`;
  }
  el.finalSummary.textContent = summary;
  el.finalSteps.textContent = formatBig(steps);
  el.finalPeriod.textContent = formatBig(data.period);
  el.finalFrom.textContent = shortId(fromId);
  el.finalFrom.title = fromId;

  const b = data.board;
  const cell = finalCellSize(b);
  paint(el.finalCanvas, b.width, b.height, fromRows(b.cells, b.width, b.height), cell, false);
  el.finalCanvas.setAttribute('aria-label', `Final board, ${b.width} by ${b.height}`);
  el.finalCard.hidden = false;
  finalBoard = b;
}

// ---------------------------------------------------------------- Play

function currentRate() {
  return state.speeds[Number(el.speed.value)] ?? DEFAULT_SPEED;
}

function currentDelay() {
  return 1000 / currentRate();
}

function clamp(value, min, max) {
  return Math.min(max, Math.max(min, value));
}

// How few buffered boards trigger the next batch request.
function refillAt(rate, roundTripMs) {
  return clamp(Math.ceil((rate * BUFFER_ROUND_TRIPS * roundTripMs) / 1000), MIN_REFILL_AT, state.limits.maxBatchSize);
}

// How many boards to ask for in one batch.
function batchSize(rate, seconds = BATCH_SECONDS) {
  const max = state.limits.maxBatchSize;
  return clamp(Math.ceil(rate * seconds), Math.min(MIN_BATCH, max), max);
}

function roundTripMs(playback) {
  return playback.roundTrips.length ? Math.max(...playback.roundTrips) : DEFAULT_ROUND_TRIP_MS;
}

function isSequenceResponse(data) {
  return (
    data &&
    Array.isArray(data.boards) &&
    data.boards.every(isBoardResponse) &&
    (data.repeatsFrom === null || (Number.isInteger(data.repeatsFrom) && data.repeatsFrom >= 0 && data.repeatsFrom <= data.boards.length))
  );
}

// The stored board in the shape the API returns, so it can start a loop like any fetched board.
function storedAsResponse() {
  const s = state.stored;
  return { id: s.id, width: s.width, height: s.height, edgeMode: s.edgeMode, cells: toRows() };
}

async function startPlay() {
  if (state.busy) return;
  hideMsg(el.opsMsg);
  hideMsg(el.loadMsg);
  if (!canOperate()) {
    // Save the drawing first. Editing is locked while busy, so the board cannot change during the save.
    await runBusy(el.play, () => ensureStored(el.opsMsg));
    if (!canOperate() || state.busy) return;
  }
  state.playing = true;
  state.busy = true;
  // Boards are content-addressed, so a buffer parked on this exact board still holds its future.
  const parked = state.parked;
  state.parked = null;
  state.playback = parked?.boardId === state.stored.id ? parked.playback : newPlayback();
  updateUI();
  startRun();
  fillBuffer();
  state.playTimer = setTimeout(playStep, currentDelay());
}

// cursor is the last board fetched, which the next batch starts from. loop holds the boards to repeat
// once the server says the sequence repeats, after which no more requests are needed. fetchId tells a
// batch that answers after a pause apart from the current one.
function newPlayback() {
  return {
    queue: [],
    cursor: storedAsResponse(),
    fetching: false,
    fetchId: 0,
    fetched: false,
    roundTrips: [],
    loop: null,
    error: null,
    failures: 0,
    retryAt: 0,
    waitShown: false,
  };
}

// Pause passes keepBuffer, so pressing Play again on the same board does not fetch it all again.
// Every other stop drops the buffer.
function stopPlay(message, { keepBuffer = false } = {}) {
  if (!state.playing) return;
  const pb = state.playback;
  state.playing = false;
  state.busy = false;
  state.playback = null;
  state.parked = null;
  if (keepBuffer && pb) {
    // A batch still in flight is dropped when it answers, and fetched again after Play.
    pb.fetchId++;
    pb.fetching = false;
    state.parked = { playback: pb, boardId: state.stored.id };
  }
  clearTimeout(state.playTimer);
  state.playTimer = 0;
  if (message) showInfo(el.opsMsg, message);
  updateUI();
}

// Asks for the next batch once the buffer is down to the refill point. One request at a time, each
// starting from the last board fetched, so batches arrive in order with no gaps.
async function fillBuffer() {
  const pb = state.playback;
  if (!pb || pb.fetching || pb.loop || performance.now() < pb.retryAt) return;
  const rate = currentRate();
  if (pb.queue.length > refillAt(rate, roundTripMs(pb))) return;

  pb.fetching = true;
  const fetchId = ++pb.fetchId;
  const from = pb.cursor;
  const count = batchSize(rate, pb.fetched ? BATCH_SECONDS : FIRST_BATCH_SECONDS);
  const started = performance.now();
  const result = await api('GET', `/boards/${encodeURIComponent(from.id)}/sequence?count=${count}`);
  // Paused, edited or replaced while the request was in flight. Drop the result.
  if (state.playback !== pb || pb.fetchId !== fetchId) return;
  pb.fetching = false;
  pb.roundTrips.push(performance.now() - started);
  if (pb.roundTrips.length > ROUND_TRIP_SAMPLES) pb.roundTrips.shift();

  if (!result.ok || !isSequenceResponse(result.data)) {
    // Playback carries on from the buffer and tries again later. It only stops if the buffer runs out.
    pb.error = result.ok ? badResponse(result.status) : result.problem;
    pb.failures++;
    pb.retryAt = performance.now() + 1000 * (pb.error.retryAfterSeconds ?? 1);
    return;
  }
  pb.error = null;
  pb.failures = 0;
  pb.fetched = true;
  const { boards, repeatsFrom } = result.data;
  pb.queue.push(...boards);
  if (boards.length) pb.cursor = boards[boards.length - 1];
  // After the last board comes generation repeatsFrom of this batch, where 0 is the board it started from.
  if (repeatsFrom !== null) pb.loop = [from, ...boards].slice(repeatsFrom);
}

function nextBuffered(pb) {
  if (pb.queue.length === 0 && pb.loop) pb.queue.push(...pb.loop);
  return pb.queue.shift();
}

function playStep() {
  const pb = state.playback;
  if (!state.playing || !pb) return;
  const s = state.stored;
  const next = nextBuffered(pb);
  if (next) {
    if (next.id === s.id) {
      stopPlay('The next generation has the same ID as this one, so the board is a still life. Playback stopped.');
      return;
    }
    if (pb.waitShown) {
      hideMsg(el.opsMsg);
      pb.waitShown = false;
    }
    setCurrent(next, { gen: s.gen === null ? null : s.gen + 1n });
    showFinalIfReached();
  } else if (pb.error && !pb.fetching) {
    // The buffer ran out and the last batch failed. A rate limit answer waits for its retry time and
    // says so. Any other failure gets one retry, then playback stops.
    if (pb.error.status === 429) {
      if (!pb.waitShown) {
        const seconds = Math.max(1, Math.ceil((pb.retryAt - performance.now()) / 1000));
        showInfo(el.opsMsg, `The server's limit of ${state.limits.maxBoardsPerSecond} boards per second was reached. Playback resumes in about ${plural(seconds, 'second', 'seconds')}.`);
        pb.waitShown = true;
      }
    } else if (pb.failures >= MAX_FAILED_BATCHES) {
      const problem = pb.error;
      stopPlay();
      showProblem(el.opsMsg, problem);
      return;
    }
  }
  fillBuffer();
  state.playTimer = setTimeout(playStep, currentDelay());
}

el.play.addEventListener('click', () => {
  if (state.playing) stopPlay(undefined, { keepBuffer: true });
  else startPlay();
});

function speedLabel(rate) {
  return rate === state.limits.maxBoardsPerSecond ? `${rate} per second (max)` : `${rate} per second`;
}

function updateSpeedLabel() {
  el.speedOut.textContent = speedLabel(currentRate());
}

// Builds the speed slider from the server's cap, keeping the chosen speed where it still fits.
function applySpeeds() {
  const chosen = state.speeds.length ? currentRate() : DEFAULT_SPEED;
  const max = state.limits.maxBoardsPerSecond;
  state.speeds = [...BASE_SPEEDS.filter((rate) => rate < max), max];
  let index = state.speeds.findLastIndex((rate) => rate <= chosen);
  if (index < 0) index = 0;
  el.speed.max = String(state.speeds.length - 1);
  el.speed.value = String(index);
  updateSpeedLabel();
}

async function loadSettings() {
  const result = await api('GET', '/settings');
  const data = result.data;
  if (!result.ok || !data || !Number.isInteger(data.maxBoardsPerSecond) || !Number.isInteger(data.maxBatchSize)) return;
  if (data.maxBoardsPerSecond < 1 || data.maxBatchSize < 1) return;
  state.limits = { maxBoardsPerSecond: data.maxBoardsPerSecond, maxBatchSize: data.maxBatchSize };
  applySpeeds();
}

el.speed.addEventListener('input', () => {
  updateSpeedLabel();
  // A faster speed may put the buffer under its new refill point, so check straight away.
  fillBuffer();
});

// ---------------------------------------------------------------- API log

// A button showing a short ID that copies the full one.
function idButton(id) {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'log-id';
  button.dataset.id = id;
  button.title = `Copy ${id}`;
  button.append(el.copyId.querySelector('.icon-copy').cloneNode(true), document.createElement('span'));
  button.lastChild.textContent = shortId(id);
  return button;
}

function logRequest(method, path, status, ms, returnedId) {
  state.logTotal += 1;
  const row = document.createElement('tr');
  const now = new Date();
  const cells = [
    ['l-time', now.toLocaleTimeString('en-GB', { hour12: false })],
    ['l-method', method],
    ['l-path', ''],
    ['l-result', ''],
    ['r', ''],
    ['r', Math.round(ms).toLocaleString('en-US')],
  ];
  for (const [cls, text] of cells) {
    const td = document.createElement('td');
    td.className = cls;
    td.textContent = text;
    row.append(td);
  }
  // IDs in the path become copy buttons. The rest of the path stays plain text.
  const pathCell = row.children[2];
  pathCell.title = path;
  let last = 0;
  for (const match of path.matchAll(/(?<=\/boards\/)[0-9a-z]{26}/g)) {
    pathCell.append(path.slice(last, match.index), idButton(match[0]));
    last = match.index + match[0].length;
  }
  pathCell.append(path.slice(last));
  if (returnedId) row.children[3].append(idButton(returnedId));
  const pill = document.createElement('span');
  pill.className = 'status-pill';
  pill.dataset.range = String(Math.floor(status / 100));
  pill.textContent = status === 0 ? 'failed' : String(status);
  row.children[4].append(pill);

  el.logBody.prepend(row);
  while (el.logBody.children.length > LOG_LIMIT) el.logBody.lastElementChild.remove();
  el.logEmpty.hidden = true;
  el.logCount.textContent =
    state.logTotal > LOG_LIMIT
      ? `${state.logTotal.toLocaleString('en-US')} requests, last ${LOG_LIMIT} shown`
      : `${state.logTotal} ${state.logTotal === 1 ? 'request' : 'requests'}`;
}

// ---------------------------------------------------------------- Health and clipboard

async function checkHealth() {
  el.health.dataset.state = 'pending';
  el.healthText.textContent = 'Checking API';
  const result = await api('GET', '/health');
  const healthy = result.ok && result.text.trim() === 'Healthy';
  el.health.dataset.state = healthy ? 'ok' : 'down';
  el.healthText.textContent = healthy
    ? 'API healthy'
    : result.status === 0
      ? 'API unreachable'
      : `API unhealthy (${result.status})`;
}

el.health.addEventListener('click', checkHealth);

// Copies text, or when clipboard access is refused, shows it in target and selects it to copy by hand.
async function copyText(text, target) {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    target.textContent = text;
    const range = document.createRange();
    range.selectNodeContents(target);
    const selection = window.getSelection();
    selection.removeAllRanges();
    selection.addRange(range);
    return false;
  }
}

el.copyId.addEventListener('click', async () => {
  const id = shownId();
  if (!id) return;
  const copied = await copyText(id, el.boardId);
  el.copyIdLabel.textContent = copied ? 'Copied' : 'ID selected';
  setTimeout(() => {
    el.copyIdLabel.textContent = 'Copy ID';
  }, 1500);
});

el.logBody.addEventListener('click', async (event) => {
  const button = event.target.closest('.log-id');
  if (!button) return;
  const id = button.dataset.id;
  const label = button.lastChild;
  if (!(await copyText(id, label))) return;
  label.textContent = 'Copied';
  setTimeout(() => {
    label.textContent = shortId(id);
  }, 1200);
});

// ---------------------------------------------------------------- Wiring

el.next.addEventListener('click', nextGeneration);
el.final.addEventListener('click', findFinal);
el.jumpForm.addEventListener('submit', (event) => {
  event.preventDefault();
  if (!el.jump.disabled) jumpAhead();
});
el.jumpN.addEventListener('input', () => el.jumpN.removeAttribute('aria-invalid'));
el.loadForm.addEventListener('submit', (event) => {
  event.preventDefault();
  if (!el.load.disabled) loadById();
});
el.loadId.addEventListener('input', () => el.loadId.removeAttribute('aria-invalid'));

function retheme() {
  colors = readColors();
  draw();
  if (finalBoard && !el.finalCard.hidden) {
    const b = finalBoard;
    const cell = finalCellSize(b);
    paint(el.finalCanvas, b.width, b.height, fromRows(b.cells, b.width, b.height), cell, false);
  }
}

DARK_QUERY.addEventListener('change', retheme);
WIDE_QUERY.addEventListener('change', layoutCanvas);

let lastStageSize = '';
new ResizeObserver(() => {
  // Only refit when the stage width changes or, on wide screens, its height. Avoids a resize loop on phones.
  const key = WIDE_QUERY.matches ? `${el.stage.clientWidth}x${el.stage.clientHeight}` : `${el.stage.clientWidth}`;
  if (key === lastStageSize) return;
  lastStageSize = key;
  layoutCanvas();
}).observe(el.stage);
window.addEventListener('resize', layoutCanvas);

applySpeeds();
placePreset(PRESETS.find((p) => p.key === 'glider'));
hideMsg(el.boardMsg);
layoutCanvas();
updateUI();
checkHealth();
loadSettings();
