// Embedded mode (?embed=1): the page as an Archipelago substrate. main.js imports this module only in that mode, after the
// runtime is up; the game itself (C#: CloudberryKingdom.EmbedMode) idles on an empty level until configure.
//
// The __swfBridge contract (Archipelago-CC frontend/modules/flashSubstrate/bridge.js, injected into this page by the host):
//   game side, here:
//     configure({params, ap_items, ap_locations, regionId, checkedLocations, arrivedFrom, …})
//         params = the region payload, PAYLOAD_VERSION (see PAYLOAD below). The page regenerates the level from
//         params.generate, refuses LOUDLY (console.error + the status line + __cloudberryEmbed.refusal) when the document's
//         canonical sha256 is not params.docSha256, or when a check coin is not in the document, then plays it.
//         arrivedFrom (OPTIONAL, P2b): how the player arrived — { exit_id, source_region? } (Archipelago-CC's
//         procgenPlayer) or the exit id alone: exit_id names THIS region's exit, else (a world whose exits are not
//         linked both ways) the exit leading back to source_region is the one. The start door then offers that exit's
//         side too — the way back by the side you came in (see entrySide below).
//     pollItems(names)              the AP item names held → platform items + Jetpack (params.items / ap_items map names)
//     setGateStates({portals, pickups})  per side / per check: false = closed (a closed check's collection is held
//                                   until it opens; a closed side is not offered at the door)
//     botWalkTo(goal, options)      {kind:'pickup', id:<check name>} = respawn at the coin's piece checkpoint and play that
//                                   piece's recording to the coin; {kind:'portal', id:<side>} = respawn at the last piece's
//                                   checkpoint and play to the end door (then leave by that side) — except a side the
//                                   START door offers (startDoorSides: the way back, P3a): then respawn at the level's start,
//                                   at the start door, and leave by it (no item needed). options: {snap, door:'start'|'end'}
//                                   (door forces one; feature test: __cloudberryEmbed.features.botStartDoor).
//     botStop(), reset(), setHostState(state) (kept; nothing reads it yet)
//     setCheckedLocations(names)    OPTIONAL (P1b): the check names the host has as checked; their coins are drawn as
//                                   outlines (configure's checkedLocations seeds the same set)
//   host side, called by the page:
//     sendLocation(<check name>)    when a check coin is collected (once per configure; the bridge maps it to the AP name)
//     sendExit(<exitName>, <side>)  chosen in the exit menu (Up at the end door: every exit; Up at the start door: the
//                                   way back — the exit the player arrived by, and the exit on entrySide), or the bot's
//                                   side when the bot walked to the door
//     setPlayClock(running, {gameSeconds})  every second and on change; running is false while the game is paused
//                                   (the exit menu is open, or the page is hidden)
//
// PAYLOAD (params), version 'cloudberry-region/1':
//   { payload: 'cloudberry-region/1',
//     generate: { …the RESOLVED ck-level/2 request: the document's args.request (rerolls explicit, no reroll search)… },
//     docSha256: '<hex>',          canonical sha256 of generateText(generate) (canonicalDocText: engine.build blanked)
//     checks: { '<check name>': <coin id>, … },   the coins that are AP location checks (names are the ap_locations keys)
//     items: { '<AP item name>': '<game item>' }  optional; game items = the 9 platform items + 'Jetpack'; else ap_items,
//                                  else the AP item name is the game item name
//     exits: [{ side, exitName, exitId?, targetRegion?, label? }],  the region's exits, offered at the end door; label
//                                  (optional, short) is shown under the name, e.g. what a closed exit needs; a closed exit
//                                  without one says "locked"; exitId and targetRegion (optional, P2b) are the host's id of
//                                  the exit and the region it leads to, which configure's arrivedFrom is matched against
//     sidePortals: { <side>: <side> }   the bridge's walkTo translation (portal id = side)
//     entrySide: <side> | null }   the region's way back: the start door offers that exit (never gated by the level;
//                                  setGateStates may still close it). With configure's arrivedFrom, the start door offers
//                                  the side arrived by AS WELL (one entry when they are the same side). Neither = the
//                                  start door does nothing
// Page parameter packs=<url>: the art library's base URL (default check-look.js DEFAULT_PACKS_BASE) for the check
// coins' Archipelago logo; when it cannot be read the check coins are larger purple coins instead.
// Test surface (not the contract): window.__cloudberryEmbed.

import { createExitMenu, SIDES } from './exit-menu.js';
import { loadCheckLook, DEFAULT_PACKS_BASE } from './check-look.js';

export const PAYLOAD_VERSION = 'cloudberry-region/1';
const PLATFORM_ITEMS = ['MovingBlock', 'FallingBlock', 'BouncyBlock', 'GhostBlock', 'Cloud', 'Elevator', 'Pendulum', 'Conveyor', 'FlyBlob'];
const JETPACK_ITEM = 'Jetpack';
const PLAY_CLOCK_PERIOD_MS = 1000;
const PHYSICS_HZ = 60;

/** The document with engine.build blanked: a redeploy that does not change generation keeps every hash. */
export function canonicalDocText(text) {
  return text.replace(/"engine":\{"build":"[^"]*"/, '"engine":{"build":""');
}

export async function docSha256(text) {
  const bytes = new TextEncoder().encode(canonicalDocText(text));
  const digest = await crypto.subtle.digest('SHA-256', bytes);
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

export function start({ exports, generateText, ready, setPaused = () => {} }) {
  const app = {
    config: null, payload: null, playResult: null, refusal: null, configures: 0,
    items: [], gates: { portals: {}, pickups: {} }, heldLocations: new Set(), sent: [], exits: [],
    pendingBot: null, lastBotGoal: null, hostState: null, events: [], physics0: 0, clockRunning: null,
    checked: new Set(), menu: null, paused: new Set(), look: null,
  };
  let configureChain = Promise.resolve();
  let configureSeq = 0;

  const statusEl = document.createElement('div');
  statusEl.id = 'embed-status';
  statusEl.style.cssText = 'position:absolute;left:4px;bottom:4px;max-width:calc(100% - 8px);padding:2px 6px;background:rgba(0,0,0,.65);color:#ddd;font:11px/1.4 monospace;pointer-events:none;white-space:pre-wrap';
  document.getElementById('stage')?.appendChild(statusEl);
  const show = (text, bad = false) => { statusEl.textContent = text; statusEl.style.color = bad ? '#f88' : '#ddd'; };
  show('Cloudberry Kingdom — waiting for a region…');

  // Pausing (the page stops stepping the game: main.js): while the exit menu is open, and while the page is hidden.
  const pause = (reason, on) => {
    if (on) app.paused.add(reason); else app.paused.delete(reason);
    setPaused(reason, on);
    if (app.config) playClock();
  };

  const menu = createExitMenu({
    container: document.getElementById('stage') ?? document.body,
    before: document.getElementById('touch'),
    onChoose: (entry) => {
      show(`region ${app.config?.regionId ?? '?'} — leaving by the ${entry.side} exit: ${entry.exitName}`);
      sendExit(entry.exitName, entry.side, entry.door);
    },
    onClose: () => {
      app.menu = null;
      pause('menu', false);
      // Up at a door opens the menu again (once Up is released).
      try { exports.EmbedMode.RearmDoorsNow(); } catch (e) { /* no level */ }
    },
  });

  // The check coins' look: the art library's Archipelago logo, or the purple fallback (check-look.js).
  const packsBase = new URLSearchParams(location.search).get('packs') || DEFAULT_PACKS_BASE;
  const lookReady = loadCheckLook(packsBase).then(async (look) => {
    await ready();
    const r = JSON.parse(exports.EmbedMode.SetCheckLookJson(JSON.stringify({ w: look.w, h: look.h, size: look.size, fill: look.fill, outline: look.outline })));
    app.look = { source: look.source, reason: look.reason, sheet: look.sheet, base: look.base, size: look.size, applied: r };
    if (look.source === 'fallback') console.warn('[cloudberry-embed] check coins: the art library could not be read (' + look.reason + '); purple fallback coins');
    else console.log('[cloudberry-embed] check coins: the Archipelago logo from ' + look.sheet);
    return app.look;
  }).catch((e) => { console.error('[cloudberry-embed] check-coin look failed', e); app.look = { source: 'default', reason: String(e) }; });

  const refuse = (why, detail) => {
    app.refusal = why;
    console.error('[cloudberry-embed] configure REFUSED: ' + why, detail ?? '');
    show('This region cannot be played: ' + why, true);
  };

  const bridge = () => window.__swfBridge ?? {};
  const call = (name, ...args) => {
    const f = bridge()[name];
    if (typeof f !== 'function') { console.warn(`[cloudberry-embed] __swfBridge.${name} missing`, ...args); return; }
    try { f(...args); } catch (e) { console.error(`[cloudberry-embed] __swfBridge.${name} threw`, e); }
  };

  function itemMap() {
    return app.payload?.items ?? app.config?.ap_items ?? {};
  }

  function applyItems() {
    if (!app.playResult?.ok) return;
    const map = itemMap();
    const game = new Set(app.items.map((name) => (typeof map[name] === 'string' ? map[name] : name)));
    const r = JSON.parse(exports.EmbedMode.SetItemsJson(JSON.stringify({
      platforms: PLATFORM_ITEMS.filter((p) => game.has(p)), jetpack: game.has(JETPACK_ITEM),
    })));
    if (r.error) console.error('[cloudberry-embed] items refused:', r.error);
    app.itemsState = r;
  }

  async function configureNow(config, seq) {
    app.refusal = null;
    app.playResult = null;
    menu.close('replaced');
    // The level being replaced stops at once (back to the empty level): nothing more from it reaches the host, which
    // already maps names by the new region's payload.
    exports.EmbedMode.StopNow();
    const params = config?.params ?? {};
    if (params.payload !== PAYLOAD_VERSION) return refuse(`payload version ${JSON.stringify(params.payload)}, this page reads ${PAYLOAD_VERSION}`);
    if (!params.generate || typeof params.generate !== 'object') return refuse('the payload has no generate request');
    if (typeof params.docSha256 !== 'string') return refuse('the payload has no docSha256');
    show(`region ${config.regionId ?? '?'} — generating…`);
    await ready();
    const text = await generateText(params.generate);
    if (seq !== configureSeq) return;           // a newer configure arrived
    let doc;
    try { doc = JSON.parse(text); } catch (e) { return refuse('the generator returned no document', e); }
    if (doc.error) return refuse('the generator refused the request: ' + doc.error);
    const sha = await docSha256(text);
    if (sha !== params.docSha256) return refuse(`document hash mismatch: the payload says ${params.docSha256}, this page generates ${sha} (generator ${doc.engine?.generator}, build ${doc.engine?.build})`);
    if (!doc.coins?.list) return refuse('the request makes no coins section (coins: false?)');

    const coins = new Map(doc.coins.list.map((c) => [c.id, c]));
    const objects = new Map((doc.objects ?? []).map((o) => [o.id, o]));
    const checks = [];
    for (const [name, id] of Object.entries(params.checks ?? {})) {
      const coin = coins.get(id);
      if (!coin) return refuse(`check ${name}: the document has no coin ${id}`);
      if (!coin.witness) return refuse(`check ${name}: coin ${id} has no witness (no recording collects it)`);
      checks.push({ name, id, pos: objects.get(id)?.pos ?? coin.pos, piece: coin.witness.replayOfPiece, tick: coin.witness.tick });
    }
    const checkedNames = new Set(config.checkedLocations ?? []);
    const result = JSON.parse(exports.EmbedMode.PlayJson(JSON.stringify({
      generate: params.generate, checks, checked: [...checkedNames], coinCount: doc.coins.count,
      camStartFix: params.camStartFix === true,
    })));
    if (seq !== configureSeq) return;
    if (!result.ok) return refuse('the level could not be played: ' + result.error);
    app.config = config;
    app.payload = params;
    app.playResult = result;
    app.doc = { sha, pieces: doc.level?.numPieces, coins: doc.coins.count, door: doc.goal?.door };
    app.exits = Array.isArray(params.exits) ? params.exits : [];
    app.heldLocations = new Set();
    app.checked = checkedNames;
    app.sent = [];
    app.configures++;
    app.physics0 = exports.BrowserMainLoop.PhysicsCount();
    applyItems();
    show(`region ${config.regionId ?? '?'} — ${result.pieces} pieces, ${checks.length} checks, ${result.coins} coins`);
    if (app.pendingBot) { const p = app.pendingBot; app.pendingBot = null; botWalkTo(p.goal, p.options); }
  }

  function exitFor(side) {
    return app.exits.find((e) => e.side === side) ?? null;
  }

  const portalOpen = (side) => app.gates.portals?.[side] !== false;

  function sendExit(exitName, side, door) {
    app.events.push({ kind: 'sendExit', exitName, side, door: door ?? null });
    call('sendExit', exitName, side);
  }

  /** The side the player arrived by (configure's arrivedFrom → that exit's side), or null. */
  function arrivedSide() {
    const from = app.config?.arrivedFrom;
    const id = typeof from === 'string' ? from : from?.exit_id;
    const source = typeof from === 'object' && from ? from.source_region : null;
    const exit = (id && (app.exits.find((e) => e.exitId === id) ?? app.exits.find((e) => e.exitName === id)))
      || (source && app.exits.find((e) => e.targetRegion === source))
      || null;
    return exit && SIDES.includes(exit.side) ? exit.side : null;
  }

  /** The start door's sides: the side arrived by, then the payload's entrySide (each once). */
  function startDoorSides() {
    return [...new Set([arrivedSide(), app.payload?.entrySide].filter((s) => SIDES.includes(s)))];
  }

  /** The menu's entries for a door: the end door lists every exit; the start door the way back (startDoorSides). */
  function menuEntries(door) {
    const back = door === 'start' ? startDoorSides() : null;
    const list = back ? back.map((s) => exitFor(s)).filter(Boolean) : app.exits.filter((e) => SIDES.includes(e.side));
    return list.map((e) => ({ door, side: e.side, exitName: e.exitName, open: portalOpen(e.side), label: typeof e.label === 'string' && e.label ? e.label : undefined }));
  }

  function openMenu(door) {
    app.menu = { door };
    const entries = menuEntries(door);
    const title = door !== 'start' ? `Leave ${app.config?.regionId ?? 'this region'}`
      : entries.length > 1 ? 'Go back' : 'Go back the way you came';
    menu.open({ title, entries });
    pause('menu', true);
  }

  function onDoor(ev) {
    const region = app.config?.regionId ?? '?';
    if (ev.bot) {
      // The bot leaves by the side it walked to, without the menu: by the start door when it walked there (the way back),
      // else by the end door.
      const botSide = app.lastBotGoal?.kind === 'portal' ? app.lastBotGoal.id : null;
      if (ev.door === 'start') {
        if (!botSide || !startDoorSides().includes(botSide)) { show(`region ${region} — the bot is at the start door, but ${botSide} is not a way back`); return; }
        const back = exitFor(botSide);
        if (!back || !portalOpen(back.side)) { show(`region ${region} — the ${botSide} way back is closed`); return; }
        sendExit(back.exitName, back.side, 'start');
        return;
      }
      const exit = botSide ? exitFor(botSide) : null;
      if (!exit) { show(`region ${region} — the bot is at the door, but there is no ${botSide} exit`); return; }
      if (!portalOpen(exit.side)) { show(`region ${region} — the ${exit.side} exit is closed`); return; }
      sendExit(exit.exitName, exit.side, 'end');
      return;
    }
    if (ev.door === 'start' && startDoorSides().length === 0) return;   // no way back offered: the start door does nothing
    openMenu(ev.door === 'start' ? 'start' : 'end');
  }

  function sendLocation(name) {
    app.sent.push(name);
    app.events.push({ kind: 'sendLocation', name });
    call('sendLocation', name);
  }

  function pump() {
    requestAnimationFrame(pump);
    let text = '';
    try { text = exports.EmbedMode.TakeEventsJson(); } catch (e) { return; }
    if (text && app.playResult?.ok) {
      for (const ev of JSON.parse(text)) {
        app.events.push(ev);
        if (ev.kind === 'location') {
          if (app.gates.pickups?.[ev.name] === false) app.heldLocations.add(ev.name);
          else sendLocation(ev.name);
        } else if (ev.kind === 'door') onDoor(ev);
        else if (ev.kind === 'bot') console.log('[cloudberry-embed] bot:', JSON.stringify(ev));
      }
    }
  }
  requestAnimationFrame(pump);

  function playClock() {
    const running = !!app.playResult?.ok && !document.hidden && app.paused.size === 0;
    const gameSeconds = app.playResult?.ok ? (exports.BrowserMainLoop.PhysicsCount() - app.physics0) / PHYSICS_HZ : 0;
    app.clockRunning = running;
    call('setPlayClock', running, { gameSeconds });
  }
  setInterval(() => { if (app.config) playClock(); }, PLAY_CLOCK_PERIOD_MS);
  // A hidden page (another tab, a minimised window) pauses the game; the clock reports it stopped.
  document.addEventListener('visibilitychange', () => pause('hidden', document.hidden));
  if (document.hidden) pause('hidden', true);

  /** The door a bot walk to a side uses: the start door for a way back (startDoorSides: no item needed, it is at the level's
   *  start), else the end door. options.door ('start' | 'end') forces one. */
  function botDoor(goal, options) {
    if (goal?.kind !== 'portal') return null;
    const forced = options?.door;
    if (forced === 'end' || forced === 'start') return forced;
    return startDoorSides().includes(goal.id) && app.playResult?.startDoor !== false ? 'start' : 'end';
  }

  function botWalkTo(goal, options) {
    if (!app.playResult?.ok) { app.pendingBot = { goal, options }; return; }
    app.lastBotGoal = goal;
    const door = botDoor(goal, options);
    app.lastBotDoor = door;
    const gameGoal = door === 'start' ? { kind: 'startdoor', id: goal.id } : goal;
    const r = JSON.parse(exports.EmbedMode.BotWalkToJson(JSON.stringify({ goal: gameGoal, options: options ?? null })));
    if (!r.ok) console.error('[cloudberry-embed] botWalkTo refused:', r.error, goal);
    return r;
  }

  const gameSide = {
    configure(config) {
      const seq = ++configureSeq;
      configureChain = configureChain.then(() => configureNow(config, seq)).catch((e) => refuse('configure threw: ' + (e?.message ?? e), e));
      return configureChain;
    },
    pollItems(names) {
      app.items = Array.isArray(names) ? names.slice() : [];
      applyItems();
    },
    setGateStates(states) {
      app.gates = { portals: { ...(states?.portals ?? {}) }, pickups: { ...(states?.pickups ?? {}) } };
      for (const name of [...app.heldLocations]) {
        if (app.gates.pickups[name] !== false) { app.heldLocations.delete(name); sendLocation(name); }
      }
      // An open menu shows the new states.
      if (app.menu) menu.open({ title: menu.element.querySelector('h2')?.textContent ?? '', entries: menuEntries(app.menu.door) });
    },
    setCheckedLocations(names) {
      app.checked = new Set(Array.isArray(names) ? names : []);
      if (app.playResult?.ok) app.checkedState = JSON.parse(exports.EmbedMode.SetCheckedJson(JSON.stringify([...app.checked])));
    },
    botWalkTo: (goal, options) => botWalkTo(goal, options),
    botStop() { app.pendingBot = null; exports.EmbedMode.BotStopNow(); },
    reset() { if (app.config) gameSide.configure(app.config); },
    setHostState(state) { app.hostState = state ?? null; },
  };
  window.__swfBridge = Object.assign(window.__swfBridge ?? {}, gameSide);

  window.__cloudberryEmbed = {
    app,
    menu,
    lookReady,
    rearmDoors: () => exports.EmbedMode.RearmDoorsNow(),
    startDoorSides: () => startDoorSides(),
    features: Object.freeze({ botStartDoor: true }),
    botDoor: (goal, options) => botDoor(goal, options),
    physicsCount: () => exports.BrowserMainLoop.PhysicsCount(),
    pause: (reason, on) => pause(reason, on),
    status: () => JSON.parse(exports.EmbedMode.Status()),
    configured: () => app.configures,
    docSha256: async (args) => docSha256(await generateText(args)),
    canonicalDocText,
  };
  window.dispatchEvent(new CustomEvent('cloudberry-embed-ready'));
  console.log('[cloudberry-embed] ready: window.__swfBridge (configure, pollItems, setGateStates, setCheckedLocations, botWalkTo, botStop, reset, setHostState)');
}
