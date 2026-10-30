// On-screen controls for touch screens. Each button presses one of the game's own keys: the page dispatches
// keydown/keyup on window, where SDL reads the keyboard, so the game sees exactly what a keyboard would send.
// A finger can slide from one button to the next (left -> right) and several fingers work at once.
// Shown when the "On-screen controls" setting in the Controls panel is On, or Auto on a touch screen.

const MIN_HOLD_MS = 120;   // the game reads the keyboard once per frame; a shorter tap could fall between two reads
const MODE_KEY = 'ck.touchControls';
const MODES = ['auto', 'on', 'off'];

const KEYS = {
  left: { code: 'ArrowLeft', key: 'ArrowLeft' },
  right: { code: 'ArrowRight', key: 'ArrowRight' },
  up: { code: 'ArrowUp', key: 'ArrowUp' },
  down: { code: 'ArrowDown', key: 'ArrowDown' },
  space: { code: 'Space', key: ' ' },
  enter: { code: 'Enter', key: 'Enter' },
  escape: { code: 'Escape', key: 'Escape' },
};

const layer = document.getElementById('touch');
const stage = document.getElementById('stage');

// Key state: a key stays down while any finger holds it, and for at least MIN_HOLD_MS.
const holders = new Map();   // key name -> number of fingers on it
const downAt = new Map();    // key name -> performance.now() of its keydown
const releaseTimers = new Map();
function send(type, name) {
  const k = KEYS[name];
  window.dispatchEvent(new KeyboardEvent(type, { code: k.code, key: k.key, bubbles: true, cancelable: true }));
}
function press(name) {
  holders.set(name, (holders.get(name) || 0) + 1);
  if (releaseTimers.has(name)) { clearTimeout(releaseTimers.get(name)); releaseTimers.delete(name); return; }
  if (holders.get(name) === 1) { downAt.set(name, performance.now()); send('keydown', name); }
  layer.querySelectorAll(`[data-key="${name}"]`).forEach((b) => b.classList.add('down'));
}
function release(name) {
  const n = (holders.get(name) || 0) - 1;
  holders.set(name, Math.max(0, n));
  if (n > 0) return;
  const up = () => { releaseTimers.delete(name); send('keyup', name); layer.querySelectorAll(`[data-key="${name}"]`).forEach((b) => b.classList.remove('down')); };
  const wait = MIN_HOLD_MS - (performance.now() - (downAt.get(name) || 0));
  if (wait > 0) releaseTimers.set(name, setTimeout(up, wait)); else up();
}

// Fingers: pointer id -> the key under it. A move onto another button hands the finger over.
const fingers = new Map();
const keyAt = (x, y) => { const el = document.elementFromPoint(x, y); const b = el && el.closest('#touch [data-key]'); return b ? b.dataset.key : null; };
function moveFinger(id, name) {
  const old = fingers.get(id);
  if (old === name) return;
  if (old) release(old);
  if (name) press(name);
  fingers.set(id, name);
}
function endFinger(e) { if (fingers.has(e.pointerId)) { moveFinger(e.pointerId, null); fingers.delete(e.pointerId); } }
layer.addEventListener('pointerdown', (e) => {
  const name = keyAt(e.clientX, e.clientY);
  if (!name) return;
  e.preventDefault();
  e.target.closest('[data-key]').setPointerCapture(e.pointerId);   // keep this finger's moves after it leaves the button
  moveFinger(e.pointerId, name);
});
layer.addEventListener('pointermove', (e) => { if (fingers.has(e.pointerId)) moveFinger(e.pointerId, keyAt(e.clientX, e.clientY)); });
layer.addEventListener('pointerup', endFinger);
layer.addEventListener('pointercancel', endFinger);
layer.addEventListener('contextmenu', (e) => e.preventDefault());
// A hidden page (app switch, screen lock) never sends the pointerup: let every key go.
document.addEventListener('visibilitychange', () => { if (document.hidden) for (const id of [...fingers.keys()]) endFinger({ pointerId: id }); });

// Auto / On / Off, remembered per browser.
const coarse = window.matchMedia('(pointer: coarse)');
let mode = 'auto';
try { const saved = localStorage.getItem(MODE_KEY); if (MODES.includes(saved)) mode = saved; } catch (e) { /* storage blocked */ }
function apply() {
  const on = mode === 'on' || (mode === 'auto' && coarse.matches);
  layer.hidden = !on;
  stage.classList.toggle('with-touch', on);
  document.querySelectorAll('input[name="touch-mode"]').forEach((r) => { r.checked = r.value === mode; });
}
document.querySelectorAll('input[name="touch-mode"]').forEach((r) => r.addEventListener('change', () => {
  mode = r.value;
  try { localStorage.setItem(MODE_KEY, mode); } catch (e) { /* storage blocked */ }
  r.blur();   // a focused radio button would also take the game's arrow keys
  apply();
}));
coarse.addEventListener('change', apply);
apply();
