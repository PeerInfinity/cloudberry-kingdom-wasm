// The exit menu (embedded mode, ⚖ R5): a web overlay over the game that lists a door's exits on the arrow keys by grid
// side (↑ north, → east, ↓ south, ← west; W A S D too). A closed side is greyed out ("locked", or the payload's own
// label) and cannot be chosen. Esc / Backspace close it; on a touch screen the on-screen arrows choose (they send the
// same keys), a tap on an entry chooses, and the × closes. The game is paused while it is open (embed.js).
//
// While it is open the page takes every key press first (a capturing listener on window, registered before the game
// starts, so ahead of SDL's own): nothing reaches the game, and the game's keyboard is back as it was once it closes.
// Key releases always pass through, so the game never sees a key stuck down. The overlay sits under the on-screen
// controls (it is inserted before them and has no z-index), so their arrows stay usable. Its buttons never take focus
// (SDL reads the keyboard from the whole window; a focused button would also take Space and Enter).

export const SIDES = ['north', 'east', 'south', 'west'];
const SIDE_KEYS = {
  ArrowUp: 'north', KeyW: 'north', ArrowRight: 'east', KeyD: 'east',
  ArrowDown: 'south', KeyS: 'south', ArrowLeft: 'west', KeyA: 'west',
};
const CLOSE_KEYS = new Set(['Escape', 'Backspace']);
const ARROWS = { north: '↑', east: '→', south: '↓', west: '←' };
const GRID_CELL = { north: '1 / 2', west: '2 / 1', east: '2 / 3', south: '3 / 2' };

const STYLE = `
#exit-menu { position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; background: rgba(0, 0, 0, .55);
  font: 13px/1.35 sans-serif; color: #eee; user-select: none; -webkit-user-select: none; -webkit-tap-highlight-color: transparent; }
#exit-menu[hidden] { display: none; }
#exit-menu .box { position: relative; background: rgba(24, 24, 32, .94); border: 1px solid #556; border-radius: 10px; padding: 12px 14px 10px;
  width: min(560px, calc(100% - 32px)); box-sizing: border-box; box-shadow: 0 4px 24px rgba(0, 0, 0, .6); }
#exit-menu h2 { margin: 0 32px 8px 0; font-size: 15px; font-weight: bold; }
#exit-menu .grid { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); grid-template-rows: repeat(3, auto); gap: 6px; }
#exit-menu .hint { grid-area: 2 / 2; display: flex; align-items: center; justify-content: center; text-align: center; color: #99a; font-size: 11px; }
#exit-menu .exit { display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 2px; min-height: 58px; padding: 6px 4px;
  border: 2px solid #8ac; border-radius: 8px; background: #23324a; color: #fff; font: inherit; cursor: pointer; touch-action: manipulation; overflow-wrap: anywhere; }
#exit-menu .exit:hover { background: #2e4466; }
#exit-menu .exit .arrow { font-size: 20px; line-height: 1; }
#exit-menu .exit .name { font-weight: bold; }
#exit-menu .exit .label { font-size: 11px; color: #bcd; }
#exit-menu .exit.closed { border-color: #444; border-style: dashed; background: #1c1c1c; color: #777; cursor: not-allowed; }
#exit-menu .exit.closed .label { color: #a77; }
#exit-menu .exit.flash { animation: exit-menu-flash .35s; }
@keyframes exit-menu-flash { 0%, 100% { transform: none; } 25% { transform: translateX(-4px); } 75% { transform: translateX(4px); } }
#exit-menu .close { position: absolute; top: 6px; right: 6px; width: 32px; height: 32px; border: 1px solid #556; border-radius: 6px; background: #2a2a34;
  color: #ddd; font: bold 18px/1 sans-serif; cursor: pointer; touch-action: manipulation; }
@media (max-width: 420px) { #exit-menu .exit { min-height: 48px; } #exit-menu .hint { font-size: 10px; } }
`;

/**
 * createExitMenu({container, before, onChoose(entry), onClose(reason)}) → {open({title, entries}), close(reason), isOpen()}.
 * entries: [{side, exitName, open, label}] (label: optional, shown under the name; a closed entry without one says "locked").
 * onChoose gets the chosen open entry (the menu has already closed); onClose('chosen' | 'closed' | the caller's reason).
 */
export function createExitMenu({ container, before = null, onChoose, onClose }) {
  const style = document.createElement('style');
  style.textContent = STYLE;
  document.head.appendChild(style);
  const root = document.createElement('div');
  root.id = 'exit-menu';
  root.hidden = true;
  root.setAttribute('role', 'dialog');
  container.insertBefore(root, before);
  // No focus for anything inside (mouse, touch or keyboard).
  root.addEventListener('mousedown', (e) => e.preventDefault());
  root.addEventListener('pointerdown', (e) => { if (e.target === root) e.preventDefault(); });

  let entries = [];
  let isOpen = false;

  function button(className, html, onClick) {
    const b = document.createElement('button');
    b.type = 'button';
    b.className = className;
    b.tabIndex = -1;
    b.innerHTML = html;
    b.addEventListener('click', (e) => { e.preventDefault(); b.blur(); onClick(); });
    return b;
  }

  const esc = (t) => String(t).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

  function render(title) {
    const box = document.createElement('div');
    box.className = 'box';
    const h = document.createElement('h2');
    h.textContent = title;
    const grid = document.createElement('div');
    grid.className = 'grid';
    for (const side of SIDES) {
      const entry = entries.find((e) => e.side === side);
      if (!entry) continue;
      const label = entry.label ?? (entry.open ? '' : 'locked');
      const b = button(`exit${entry.open ? '' : ' closed'}`,
        `<span class="arrow">${ARROWS[side]}</span><span class="name">${esc(entry.exitName)}</span>`
        + (label ? `<span class="label">${esc(label)}</span>` : ''), () => choose(side));
      b.dataset.side = side;
      b.style.gridArea = GRID_CELL[side];
      if (!entry.open) b.setAttribute('aria-disabled', 'true');
      grid.appendChild(b);
    }
    const hint = document.createElement('div');
    hint.className = 'hint';
    hint.textContent = entries.length ? 'arrow keys / W A S D choose · Esc closes' : 'no exits here · Esc closes';
    grid.appendChild(hint);
    box.append(h, grid, button('close', '×', () => close('closed')));
    box.lastChild.setAttribute('aria-label', 'Close');
    root.replaceChildren(box);
  }

  function choose(side) {
    if (!isOpen) return;
    const entry = entries.find((e) => e.side === side);
    if (!entry) return;
    if (!entry.open) {
      const b = root.querySelector(`[data-side="${side}"]`);
      if (b) { b.classList.remove('flash'); void b.offsetWidth; b.classList.add('flash'); }
      return;
    }
    close('chosen');
    onChoose?.(entry);
  }

  function close(reason = 'closed') {
    if (!isOpen) return;
    isOpen = false;
    root.hidden = true;
    root.replaceChildren();
    onClose?.(reason);
  }

  /** Opens the menu, or (already open) shows it again with these entries. */
  function open({ title, entries: list }) {
    entries = Array.isArray(list) ? list.slice() : [];
    render(title);
    root.hidden = false;
    isOpen = true;
  }

  // Ahead of the game's own key listener (see the header): while open, every key press is the menu's.
  window.addEventListener('keydown', (e) => {
    if (!isOpen) return;
    e.preventDefault();
    e.stopImmediatePropagation();
    if (e.repeat) return;
    if (CLOSE_KEYS.has(e.code) || CLOSE_KEYS.has(e.key)) { close('closed'); return; }
    const side = SIDE_KEYS[e.code];
    if (side) choose(side);
  }, true);
  window.addEventListener('keypress', (e) => { if (isOpen) { e.preventDefault(); e.stopImmediatePropagation(); } }, true);

  return { open, close, isOpen: () => isOpen, element: root, choose };
}
