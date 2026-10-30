// Embedded mode (?embed=1): the page as an Archipelago substrate. main.js imports this module only in that mode, after the
// runtime is up; the game itself (C#: CloudberryKingdom.EmbedMode) idles on an empty level until configure.
//
// The __swfBridge contract (Archipelago-CC frontend/modules/flashSubstrate/bridge.js, injected into this page by the host):
//   game side, here:
//     configure({params, ap_items, ap_locations, regionId, checkedLocations, …})
//         params = the region payload, PAYLOAD_VERSION (see PAYLOAD below). The page regenerates the level from
//         params.generate, refuses LOUDLY (console.error + the status line + __cloudberryEmbed.refusal) when the document's
//         canonical sha256 is not params.docSha256, or when a check coin is not in the document, then plays it.
//     pollItems(names)              the AP item names held → platform items + Jetpack (params.items / ap_items map names)
//     setGateStates({portals, pickups})  per side / per check: false = closed (a closed check's collection is held
//                                   until it opens; a closed side is not offered at the door)
//     botWalkTo(goal, options)      {kind:'pickup', id:<check name>} = respawn at the coin's piece checkpoint and play that
//                                   piece's recording to the coin; {kind:'portal', id:<side>} = respawn at the last piece's
//                                   checkpoint and play to the end door (then leave by that side). options: {snap}.
//     botStop(), reset(), setHostState(state) (kept; nothing in P1a reads it)
//   host side, called by the page:
//     sendLocation(<check name>)    when a check coin is collected (once per configure; the bridge maps it to the AP name)
//     sendExit(<exitName>, <side>)  at the end door (P1a: the first open exit, or the bot's side; P1b: the exit menu)
//     setPlayClock(running, {gameSeconds})  every second and on change
//
// PAYLOAD (params), version 'cloudberry-region/1':
//   { payload: 'cloudberry-region/1',
//     generate: { …the RESOLVED ck-level/2 request: the document's args.request (rerolls explicit, no reroll search)… },
//     docSha256: '<hex>',          canonical sha256 of generateText(generate) (canonicalDocText: engine.build blanked)
//     checks: { '<check name>': <coin id>, … },   the coins that are AP location checks (names are the ap_locations keys)
//     items: { '<AP item name>': '<game item>' }  optional; game items = the 9 platform items + 'Jetpack'; else ap_items,
//                                  else the AP item name is the game item name
//     exits: [{ side, exitName }],  the region's exits, offered at the end door
//     sidePortals: { <side>: <side> }   the bridge's walkTo translation (portal id = side)
//     entrySide: <side> | null }   the side entered from (P1b: the start door's way back)
// Test surface (not the contract): window.__cloudberryEmbed.

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

export function start({ exports, generateText, ready }) {
  const app = {
    config: null, payload: null, playResult: null, refusal: null, configures: 0,
    items: [], gates: { portals: {}, pickups: {} }, heldLocations: new Set(), sent: [], exits: [],
    pendingBot: null, lastBotGoal: null, hostState: null, events: [], physics0: 0, clockRunning: null,
  };
  let configureChain = Promise.resolve();
  let configureSeq = 0;

  const statusEl = document.createElement('div');
  statusEl.id = 'embed-status';
  statusEl.style.cssText = 'position:absolute;left:4px;bottom:4px;max-width:calc(100% - 8px);padding:2px 6px;background:rgba(0,0,0,.65);color:#ddd;font:11px/1.4 monospace;pointer-events:none;white-space:pre-wrap';
  document.getElementById('stage')?.appendChild(statusEl);
  const show = (text, bad = false) => { statusEl.textContent = text; statusEl.style.color = bad ? '#f88' : '#ddd'; };
  show('Cloudberry Kingdom — waiting for a region…');

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

  function openExits() {
    return app.exits.filter((e) => app.gates.portals?.[e.side] !== false);
  }

  function onDoor(ev) {
    // P1a: where P1b's exit menu will hang. The bot leaves by the side it walked to; the player by the first open exit.
    const botSide = ev.bot && app.lastBotGoal?.kind === 'portal' ? app.lastBotGoal.id : null;
    const exit = botSide ? exitFor(botSide) : openExits()[0] ?? null;
    if (!exit) { show(`region ${app.config?.regionId ?? '?'} — at the door, but no exit is open`); return; }
    if (app.gates.portals?.[exit.side] === false) { show(`region ${app.config?.regionId ?? '?'} — the ${exit.side} exit is closed`); return; }
    app.events.push({ kind: 'sendExit', exitName: exit.exitName, side: exit.side });
    call('sendExit', exit.exitName, exit.side);
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
    const running = !!app.playResult?.ok && !document.hidden;
    const gameSeconds = app.playResult?.ok ? (exports.BrowserMainLoop.PhysicsCount() - app.physics0) / PHYSICS_HZ : 0;
    app.clockRunning = running;
    call('setPlayClock', running, { gameSeconds });
  }
  setInterval(() => { if (app.config) playClock(); }, PLAY_CLOCK_PERIOD_MS);
  document.addEventListener('visibilitychange', () => { if (app.config) playClock(); });

  function botWalkTo(goal, options) {
    if (!app.playResult?.ok) { app.pendingBot = { goal, options }; return; }
    app.lastBotGoal = goal;
    const r = JSON.parse(exports.EmbedMode.BotWalkToJson(JSON.stringify({ goal, options: options ?? null })));
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
    },
    botWalkTo: (goal, options) => botWalkTo(goal, options),
    botStop() { app.pendingBot = null; exports.EmbedMode.BotStopNow(); },
    reset() { if (app.config) gameSide.configure(app.config); },
    setHostState(state) { app.hostState = state ?? null; },
  };
  window.__swfBridge = Object.assign(window.__swfBridge ?? {}, gameSide);

  window.__cloudberryEmbed = {
    app,
    status: () => JSON.parse(exports.EmbedMode.Status()),
    configured: () => app.configures,
    docSha256: async (args) => docSha256(await generateText(args)),
    canonicalDocText,
  };
  window.dispatchEvent(new CustomEvent('cloudberry-embed-ready'));
  console.log('[cloudberry-embed] ready: window.__swfBridge (configure, pollItems, setGateStates, botWalkTo, botStop, reset, setHostState)');
}
