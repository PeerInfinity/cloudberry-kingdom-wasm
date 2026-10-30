import { dotnet } from './_framework/dotnet.js';

// Engine-only Content (about 1.08 MB: fonts .fnt, 4 blank .dds, 17 .fxb, .smo, data), staged by build.sh.
// emcc --preload-file does not survive the .NET ES6 loader (dotnet.es6.pre.js
// replaces Module after the packager registered its preRun), so the page writes
// the files into the Emscripten FS itself, between create() and runMain().
const canvas = document.getElementById('canvas');
// ?mono_log=<mask> turns on Mono's own logging (e.g. dll = P/Invoke resolution).
const params = new URLSearchParams(location.search);
// ?embed=1: the page as an Archipelago substrate (embed.js): no attract mode or menus; the game waits for the host's
// __swfBridge.configure. It implies the generator API.
const embedMode = params.get('embed') === '1';

// The Controls panel: SDL reads the keyboard from the whole window, so a focused <summary> would also take the
// game's Space and Enter as "toggle the panel". A click toggles it without focusing it. Open or closed is remembered.
const PANEL_OPEN_KEY = 'ck.panelOpen';
const PANEL_CLOSED_BELOW_HEIGHT_PX = 600;   // a phone in landscape: the game gets the whole height by default
const panel = document.getElementById('panel');
if (panel) {
  try { const saved = localStorage.getItem(PANEL_OPEN_KEY); panel.open = embedMode ? false : saved !== null ? saved === '1' : innerHeight >= PANEL_CLOSED_BELOW_HEIGHT_PX; } catch (e) { /* storage blocked */ }
  const summary = panel.querySelector('summary');
  summary.addEventListener('mousedown', (e) => e.preventDefault());
  summary.addEventListener('keydown', (e) => { if (e.key === ' ' || e.key === 'Enter') e.preventDefault(); });
  panel.addEventListener('toggle', () => {
    summary.blur();
    if (embedMode) return;   // an embedded page starts collapsed and does not change the remembered choice
    try { localStorage.setItem(PANEL_OPEN_KEY, panel.open ? '1' : '0'); } catch (e) { /* storage blocked */ }
  });
}

// ?fpsbeacon=1: every BEACON_PERIOD_MS, report rAF frames/s and the count of the game's
// "Level made!" lines as a GET to ./__beacon (a static server logs the query string).
const BEACON_PERIOD_MS = 5000;
if (params.get('fpsbeacon')) {
  let levels = 0, frames = 0, t0 = performance.now();
  const log = console.log.bind(console);
  console.log = (...a) => { if (String(a[0]).includes('Level made!')) levels++; log(...a); };
  const tick = () => { frames++; requestAnimationFrame(tick); };
  requestAnimationFrame(tick);
  setInterval(() => {
    const now = performance.now();
    const fps = (1000 * frames / (now - t0)).toFixed(1);
    frames = 0; t0 = now;
    fetch(`./__beacon?t=${(now / 1000).toFixed(0)}&fps=${fps}&levels=${levels}&ua=${encodeURIComponent(navigator.userAgent.slice(-40))}`).catch(() => {});
  }, BEACON_PERIOD_MS);
}
// WebGL 2 has no BGRA pixel format (desktop GL's GL_BGRA, 0x80E1). FNA's raw DDS loader reads the
// game's uncompressed 32-bit .dds files (masks R=0xFF0000 … A=0xFF000000) as SurfaceFormat.ColorBgraEXT,
// and FNA3D's OpenGL driver uploads that as internalformat GL_RGBA8 + format GL_BGRA, which WebGL
// refuses (INVALID_ENUM): the texture stays incomplete and samples black, so every textured quad
// (the blocks, drawn on White/Smooth) vanishes on the black background. The page converts those
// uploads to GL_RGBA, swapping B and R in a copy of the pixels, which is what desktop GL does itself.
const GL_BGRA = 0x80E1, GL_RGBA = 0x1908, GL_UNSIGNED_BYTE = 0x1401;
const bgraCounts = { texImage2D: 0, texSubImage2D: 0 };
for (const [name, wIdx, hIdx] of [['texImage2D', 3, 4], ['texSubImage2D', 4, 5]]) {
  const original = WebGL2RenderingContext.prototype[name];
  WebGL2RenderingContext.prototype[name] = function (...args) {
    // Both calls put format at 6, type at 7, pixels at 8 and srcOffset at 9 in their 9/10-argument forms.
    if (args.length >= 9 && args[6] === GL_BGRA && args[7] === GL_UNSIGNED_BYTE) {
      args[6] = GL_RGBA;
      const src = args[8];
      if (ArrayBuffer.isView(src)) {
        const n = args[wIdx] * args[hIdx] * 4;
        const start = src.byteOffset + (args.length >= 10 ? args[9] : 0) * (src.BYTES_PER_ELEMENT || 1);
        const px = new Uint8Array(src.buffer, start, n).slice();
        for (let i = 0; i < n; i += 4) { const b = px[i]; px[i] = px[i + 2]; px[i + 2] = b; }
        args.length = 9;
        args[8] = px;
      }
      if (bgraCounts[name]++ < 20) console.log(`[host] ${name} BGRA -> RGBA ${args[wIdx]}x${args[hIdx]}${ArrayBuffer.isView(src) ? ' (pixels swapped)' : ''}`);
    }
    return original.apply(this, args);
  };
}

// FNA3D asks glGetInternalformativ(GL_RENDERBUFFER, fmt, GL_SAMPLES) for each of its first 21 formats at device
// creation. WebGL 2 rejects the non-renderable ones with INVALID_ENUM and returns null, and Emscripten then leaves
// FNA3D's value untouched. The page returns that same null up front for those formats, without the console warning:
// S3TC compressed (DXT1/3/5 and their sRGB twins), unsized GL_ALPHA, and the norm16 formats (EXT_texture_norm16).
const NOT_RENDERABLE = new Set([0x83F0, 0x83F1, 0x83F2, 0x83F3, 0x8C4C, 0x8C4D, 0x8C4E, 0x8C4F, 0x1906, 0x822A, 0x822C, 0x805B]);
{
  const original = WebGL2RenderingContext.prototype.getInternalformatParameter;
  WebGL2RenderingContext.prototype.getInternalformatParameter = function (target, internalformat, pname) {
    if (NOT_RENDERABLE.has(internalformat)) return null;
    return original.call(this, target, internalformat, pname);
  };
}

// ?logbeacon=1: every console line is also sent as a GET to ./__log (the static server's access
// log records it), plus a heartbeat every second, so a run on another machine's browser can be read here.
if (params.get('logbeacon')) {
  let seq = 0;
  const send = (kind, text) => fetch(`./__log?n=${seq++}&t=${(performance.now() / 1000).toFixed(1)}&k=${kind}&m=${encodeURIComponent(String(text).slice(0, 300))}`).catch(() => {});
  for (const kind of ['log', 'warn', 'error']) {
    const orig = console[kind].bind(console);
    console[kind] = (...a) => { send(kind, a.join(' ')); orig(...a); };
  }
  let frames = 0; const tick = () => { frames++; requestAnimationFrame(tick); }; requestAnimationFrame(tick);
  setInterval(() => { send('beat', `frames=${frames}`); frames = 0; }, 1000);
  window.addEventListener('error', (e) => send('pageerror', e.message));
  window.addEventListener('unhandledrejection', (e) => send('rejection', e.reason && (e.reason.message || e.reason)));
}
// build-info.json (written by build.sh at stage time) names the Cloudberry-Kingdom commit the page was built from,
// and "api": true when the site was built with --api.
let buildInfo = {};
try { buildInfo = await (await fetch('./build-info.json')).json(); } catch { /* optional */ }
// ?api=1 (or a site built with --api, unless ?api=0): the game loads content, then idles on an empty level instead of
// starting the attract-mode ScreenSaver, and window.cloudberry.generate({seed, difficulty, hero, length, geometry, tileset})
// returns the generator's level + the computer's recording as a ck-level/1 document (C#: CloudberryKingdom.GeneratorApi).
const apiMode = embedMode || (params.has('api') ? params.get('api') === '1' : buildInfo.api === true);
const API_READY_POLL_MS = 100;
let builder = dotnet.withModuleConfig({ canvas }).withApplicationArguments(...(embedMode ? ['--embed'] : apiMode ? ['--api'] : []));
if (buildInfo.build) builder = builder.withEnvironmentVariable('CK_BUILD', buildInfo.build);
// Mono's interpreter option PRECISE_GC (on by default in .NET 9) makes every collection walk the thread's LMF /
// interpreter-frame chain (interp_mark_stack -> interp_mark_no_ref_slots). In this single-threaded AOT page that walk
// never returns once the game is on FNA's emscripten main loop: the first collection at a ScreenSaver level swap
// froze the tab. '-precise' turns only that option off (the interpreter stack is then scanned conservatively).
// ?interp_opts=<MONO_INTERPRETER_OPTIONS> overrides it (an empty value restores the default).
if (params.get('loop') !== 'fna') builder = builder.withEnvironmentVariable('CK_MAIN_LOOP', 'js');
// Display -> Resolution (or ?res=<width>): the game's drawing buffer, 16:9; the canvas is scaled to fit either way.
// A smaller buffer is less for a phone's GPU to fill. The game reads it at start, so a change reloads the page.
const RESOLUTION_KEY = 'ck.resolution';
const RESOLUTIONS = ['1280', '960', '640'];
let resolution = '1280';
try { const saved = localStorage.getItem(RESOLUTION_KEY); if (RESOLUTIONS.includes(saved)) resolution = saved; } catch (e) { /* storage blocked */ }
if (params.has('res')) resolution = params.get('res');
builder = builder.withEnvironmentVariable('CK_BACKBUFFER_WIDTH', resolution);
const resolutionSelect = document.getElementById('resolution');
if (resolutionSelect) {
  resolutionSelect.value = RESOLUTIONS.includes(resolution) ? resolution : '1280';
  resolutionSelect.addEventListener('change', () => {
    try { localStorage.setItem(RESOLUTION_KEY, resolutionSelect.value); } catch (e) { /* storage blocked */ }
    location.reload();
  });
}
// ?fire=1: the fireball's animated textures (three offscreen passes a frame that the box drawing never shows).
if (params.get('fire') === '1') builder = builder.withEnvironmentVariable('CK_FIRE_TEXTURES', '1');
builder = builder.withEnvironmentVariable('MONO_INTERPRETER_OPTIONS', params.has('interp_opts') ? params.get('interp_opts') : '-precise');
if (params.get('mono_log')) builder = builder.withEnvironmentVariable('MONO_LOG_LEVEL', 'debug').withEnvironmentVariable('MONO_LOG_MASK', params.get('mono_log'));
// Loading progress: the runtime's downloads (and the Content files below) go through a fetch that counts the bytes it
// reads, against build-info.json's downloadBytes (build.sh sums the files, uncompressed, as the page reads them).
const loadingEl = document.getElementById('loading');
const loadingText = loadingEl && loadingEl.querySelector('.text');
const loadingBar = loadingEl && loadingEl.querySelector('.bar > div');
const totalBytes = buildInfo.downloadBytes || 0;
let loadedBytes = 0;
const MB = 1024 * 1024;
function showLoading(text, fraction) {
  if (!loadingEl) return;
  loadingText.textContent = text;
  if (fraction !== undefined) loadingBar.style.width = `${Math.min(100, fraction * 100).toFixed(1)}%`;
}
function showDownload() {
  if (totalBytes) showLoading(`Downloading the game: ${(loadedBytes / MB).toFixed(1)} of ${(totalBytes / MB).toFixed(1)} MB`, loadedBytes / totalBytes);
  else showLoading(`Downloading the game: ${(loadedBytes / MB).toFixed(1)} MB`);
}
async function countingFetch(url, init) {
  const res = await fetch(url, init);
  if (!res.ok || !res.body) return res;
  const reader = res.body.getReader();
  const body = new ReadableStream({
    async pull(controller) {
      const { done, value } = await reader.read();
      if (done) { controller.close(); return; }
      loadedBytes += value.byteLength;
      showDownload();
      controller.enqueue(value);
    },
    cancel(reason) { return reader.cancel(reason); },
  });
  // Same status and headers (application/wasm included, which WebAssembly.instantiateStreaming needs).
  return new Response(body, { status: res.status, statusText: res.statusText, headers: res.headers });
}
// JavaScript modules are import()ed by the runtime itself (undefined = its default loading).
const COUNTED_RESOURCE_TYPES = new Set(['dotnetwasm', 'assembly', 'pdb', 'icu', 'globalization', 'manifest', 'configuration']);
builder = builder.withResourceLoader((type, name, defaultUri, integrity) =>
  COUNTED_RESOURCE_TYPES.has(type) ? countingFetch(defaultUri, integrity ? { integrity } : undefined) : undefined);
showDownload();
const runtime = await builder.create();
const { Module, runMain } = runtime;

// The game was written for Windows. ~40 literal paths (and composed ones) use '\'
// ("Objects\\Arrow.smo", "Content\\Campaign\\CampaignList.txt"), and some differ in case
// from the files ("Objects\\MeatBoy.smo" vs meatboy.smo). The Emscripten FS is POSIX:
// '\' is a filename character and names are case-sensitive. So the page gives the FS
// Windows path semantics: every FS entry point Mono's syscalls reach normalises '\' to '/',
// and a path matching a shipped Content file or directory case-insensitively is mapped to it.
const FS_PATH_ARGS = {
  lookupPath: [0], open: [0], mknod: [0], mkdir: [0], stat: [0], lstat: [0], unlink: [0], rmdir: [0],
  readdir: [0], readlink: [0], chmod: [0], chown: [0], truncate: [0], utime: [0], rename: [0, 1], symlink: [0, 1],
};
const manifest = await (await fetch('./content-manifest.json')).json();
const contentCase = new Map();
for (const rel of manifest) {
  const parts = rel.split('/');
  for (let n = 1; n <= parts.length; n++) {
    const actual = '/' + parts.slice(0, n).join('/');
    contentCase.set(actual.toLowerCase(), actual);
  }
}
const FS = Module.FS;
const windowsPath = (path) => {
  path = path.replace(/\\/g, '/');
  const absolute = path.startsWith('/') ? path : (FS.cwd().replace(/\/$/, '') + '/' + path);
  return contentCase.get(absolute.replace(/\/+/g, '/').toLowerCase()) ?? path;
};

const bytes = await Promise.all(manifest.map(async (rel) => new Uint8Array(await (await countingFetch('./' + rel)).arrayBuffer())));
showLoading('Starting the game…', 1);
manifest.forEach((rel, i) => {
  const slash = rel.lastIndexOf('/');
  const dir = '/' + rel.slice(0, slash);
  Module.FS_createPath('/', dir.slice(1), true, true);
  Module.FS_createDataFile(dir, rel.slice(slash + 1), bytes[i], true, true, true);
});
for (const [name, argIndexes] of Object.entries(FS_PATH_ARGS)) {
  const original = FS[name];
  FS[name] = function (...args) {
    for (const i of argIndexes) if (typeof args[i] === 'string') args[i] = windowsPath(args[i]);
    return original.apply(this, args);
  };
}
// Engine-only mode has no font art: the game samples every glyph out of one font texture, at the rectangles its
// Content/Fonts/<font>.fnt lists, and without the texture every label is a solid bar. The page draws a placeholder
// atlas with the browser's own font into those same rectangles and writes it as Content/Fonts/<font>.png, which the
// game loads when present (Localization.LoadFont). The text shaders read one channel each: R = fill (Text_NoOutline),
// G = thin outline (Text_ThinOutline), B = thick outline (Text_ThickOutline). Only the Western font is drawn by default
// (all eight European languages use it; ~0.4 s): each CJK atlas costs ~2.5 s, and a language switch to one without its
// atlas falls back to the bars, so ?glyphs=all draws them too, and ?glyphs=0 skips drawing (labels are bars again).
const GLYPH_FONT_FAMILY = 'Arial, Helvetica, sans-serif';
const GLYPH_FONT_WEIGHT = 'bold';
const GLYPH_PAD = 9;                        // HackFont widens every glyph rectangle by 19 texels; the ink sits inside that margin
const GLYPH_OUTLINE_THIN = 6, GLYPH_OUTLINE_THICK = 12;   // stroke widths (texels) of the G and B channels
const FNT_HEADER_LINES = 2;
const GLYPH_FONTS_DEFAULT = new Set(['Grobold_Western']);
async function drawGlyphAtlas(fntText) {
  const lines = fntText.split(/\r?\n/);
  const glyphs = [];
  let width = 1, height = 1;
  for (const line of lines.slice(FNT_HEADER_LINES)) {
    const f = line.split('\t');
    if (f.length < 9) continue;
    const [code, x, y, w, h] = f.slice(0, 5).map(Number);
    glyphs.push({ code, x, y, w, h });
    width = Math.max(width, x + w + 2 * GLYPH_PAD); height = Math.max(height, y + h + 2 * GLYPH_PAD);
  }
  // Power-of-two sides, as the original atlases (the game divides texel rectangles by the texture's size).
  width = 2 ** Math.ceil(Math.log2(width - 1)); height = 2 ** Math.ceil(Math.log2(height - 1));
  const layer = (strokeWidth) => {
    const c = new OffscreenCanvas(width, height), g = c.getContext('2d');
    g.fillStyle = '#000'; g.fillRect(0, 0, width, height);
    g.fillStyle = g.strokeStyle = '#fff'; g.lineJoin = 'round'; g.lineWidth = strokeWidth; g.textBaseline = 'alphabetic';
    for (const { code, x, y, w, h } of glyphs) {
      const ch = String.fromCodePoint(code);
      if (!ch.trim() || w < 2 || h < 2) continue;
      g.font = `${GLYPH_FONT_WEIGHT} ${h}px ${GLYPH_FONT_FAMILY}`;
      const m = g.measureText(ch);
      const inkW = m.actualBoundingBoxLeft + m.actualBoundingBoxRight, inkH = m.actualBoundingBoxAscent + m.actualBoundingBoxDescent;
      if (!(inkW > 0 && inkH > 0)) continue;
      // Stretch the ink box onto the .fnt rectangle so the game's spacing tables still fit the letters.
      g.save();
      g.translate(x + GLYPH_PAD, y + GLYPH_PAD);
      g.scale(w / inkW, h / inkH);
      g.translate(m.actualBoundingBoxLeft, m.actualBoundingBoxAscent);
      if (strokeWidth > 0) { g.lineWidth = strokeWidth * inkH / h; g.strokeText(ch, 0, 0); }
      g.fillText(ch, 0, 0);
      g.restore();
    }
    return g.getImageData(0, 0, width, height).data;
  };
  const [fill, thin, thick] = [layer(0), layer(GLYPH_OUTLINE_THIN), layer(GLYPH_OUTLINE_THICK)];
  const out = new OffscreenCanvas(width, height), og = out.getContext('2d');
  const img = og.createImageData(width, height);
  for (let i = 0; i < img.data.length; i += 4) { img.data[i] = fill[i]; img.data[i + 1] = thin[i]; img.data[i + 2] = thick[i]; img.data[i + 3] = 255; }
  og.putImageData(img, 0, 0);
  return { png: new Uint8Array(await (await out.convertToBlob({ type: 'image/png' })).arrayBuffer()), width, height, glyphs: glyphs.length };
}
if (params.get('glyphs') !== '0') {
  const t = performance.now();
  for (const [i, rel] of manifest.entries()) {
    const match = rel.match(/^(Content\/Fonts)\/([^/]+)\.fnt$/);
    if (!match || (params.get('glyphs') !== 'all' && !GLYPH_FONTS_DEFAULT.has(match[2]))) continue;
    const atlas = await drawGlyphAtlas(new TextDecoder().decode(bytes[i]));
    Module.FS_createDataFile('/' + match[1], match[2] + '.png', atlas.png, true, true, true);
    console.log(`[host] glyph atlas ${match[2]}.png ${atlas.width}x${atlas.height}, ${atlas.glyphs} glyphs, ${atlas.png.length} bytes`);
  }
  console.log(`[host] glyph atlases drawn in ${(performance.now() - t).toFixed(0)} ms`);
}

// window.cloudberry.state(): what the player is looking at (C#: CloudberryKingdom.BrowserPlayState) — the game type,
// frame/physics counters, each bob's position/velocity/dying/dead, and each visible menu's items and selection.
let exportsPromise = null;
const assemblyExports = () => (exportsPromise ??= runtime.getAssemblyExports('CloudberryKingdom.dll')).then((e) => e.CloudberryKingdom);
window.cloudberry = {
  state: async () => JSON.parse((await assemblyExports()).BrowserPlayState.StateJson()),
  // Platform items (C#: CloudberryKingdom.PlatformSuppression): setHeld(['MovingBlock', …]) draws every other item's
  // platforms faint and makes them unlandable; setHeld(null) = everything solid (the default). Resolves to {enabled, held}.
  setHeld: async (items = null) => JSON.parse((await assemblyExports()).GeneratorApi.SetHeldJson(JSON.stringify(items))),
  // A throw caught inside the next game frame; resolves to the count of probes that came back (0 = the page died).
  throwProbe: async (waitMs = THROW_PROBE_WAIT_MS) => {
    const loop = (await assemblyExports()).BrowserMainLoop;
    loop.QueueThrowProbe();
    await new Promise((r) => setTimeout(r, waitMs));
    return loop.ThrowProbeCount();
  },
};
const THROW_PROBE_WAIT_MS = 500;
// ?held=MovingBlock,Cloud starts with only those platform items held (?held= : none held); absent = everything solid.
if (params.has('held')) {
  const held = params.get('held');
  window.cloudberry.setHeld(held === '' ? [] : held.split(',')).then((r) => console.log('[host] platform items: ' + JSON.stringify(r)));
}

// The game saves under Environment.SpecialFolder.MyDocuments, which is "" here: /Cloudberry Kingdom/{SaveData.bam,
// Options, Player Data}. Without the directory every save throws DirectoryNotFound; the page creates it (in memory:
// nothing persists across reloads).
const SAVE_DIR = '/Cloudberry Kingdom';
try { FS.mkdir(SAVE_DIR); } catch { /* exists */ }

// The main loop. Default (?loop=js): Main returns normally and the page steps frames from requestAnimationFrame
// (C#: CloudberryKingdom.BrowserMainLoop.Frame, which is Game.RunOneFrame). ?loop=fna: FNA's own Emscripten loop,
// emscripten_set_main_loop(..., simulate_infinite_loop 1), which leaves Main by throwing 'unwind'. Measured on one
// build with Mono's precise interpreter GC scan back on (?interp_opts=): FNA's loop froze the tab at the first
// collection after a level swap (heartbeat stopped at 22 s, 2 levels); the page loop made 9 levels in 75 s without
// a stall. So the stale frame chain left by the unwind is what the precise scan walked forever.
const jsLoop = params.get('loop') !== 'fna';
// A frame that throws out of the game is abandoned and the loop goes on; the first FRAME_ERROR_REPORT_LIMIT are logged.
const FRAME_ERROR_REPORT_LIMIT = 20;
// Pacing. The game is frame-locked (one physics step per drawn frame, written for a steady 60 Hz), so on its own it
// runs slow while a phone draws fewer frames and double speed on a 120 Hz screen. The page keeps physics at 60 steps
// a second: it skips draws when the display is faster and asks for several steps per draw when it is slower. After a
// long stall it drops the backlog (the game slows down rather than jumping ahead). ?pace=raw: one step per frame.
const STEP_MS = 1000 / 60;
const STEP_TOLERANCE = 0.25;      // of a step: 60 Hz frames that arrive a little early still draw (no 0-2-0-2 stutter)
const MAX_STEPS_PER_DRAW = 4;     // below 15 frames/s the game slows down
const MAX_FRAME_GAP_MS = 250;     // a longer gap (a stall, a background tab) counts as this
const rawPace = params.get('pace') === 'raw';

// FPS display (Controls panel "Show FPS", or ?fps=1): screen refreshes, drawn frames and physics steps per second,
// and the time the game's frames take.
const SHOW_FPS_KEY = 'ck.showFps';
const FPS_PERIOD_MS = 500;
const fpsEl = document.getElementById('fps');
const fpsBox = document.getElementById('show-fps');
let showFps = params.get('fps') === '1';
try { if (!params.has('fps')) showFps = localStorage.getItem(SHOW_FPS_KEY) === '1'; } catch (e) { /* storage blocked */ }
function applyShowFps() { if (fpsEl) fpsEl.hidden = !showFps; if (fpsBox) fpsBox.checked = showFps; }
if (fpsBox) fpsBox.addEventListener('change', () => {
  showFps = fpsBox.checked; fpsBox.blur(); applyShowFps();
  try { localStorage.setItem(SHOW_FPS_KEY, showFps ? '1' : '0'); } catch (e) { /* storage blocked */ }
});
applyShowFps();

async function startFrameLoop() {
  const loop = (await assemblyExports()).BrowserMainLoop;
  let frameErrors = 0, last = null, owed = 0;
  let periodStart = performance.now(), refreshes = 0, draws = 0, costMs = 0, worstMs = 0, physics0 = loop.PhysicsCount();
  const frame = (now) => {
    requestAnimationFrame(frame);
    refreshes++;
    let steps = 1;
    if (!rawPace) {
      owed += last === null ? STEP_MS : Math.min(now - last, MAX_FRAME_GAP_MS);
      steps = Math.floor(owed / STEP_MS + STEP_TOLERANCE);
      if (steps > MAX_STEPS_PER_DRAW) { steps = MAX_STEPS_PER_DRAW; owed = 0; } else owed -= steps * STEP_MS;
    }
    last = now;
    if (steps > 0) {
      const t = performance.now();
      try { loop.FrameSteps(steps); } catch (e) {
        if (frameErrors++ < FRAME_ERROR_REPORT_LIMIT) console.error(`[host] frame threw (#${frameErrors}): ${e && e.message || e}`);
      }
      const dt = performance.now() - t;
      draws++; costMs += dt; worstMs = Math.max(worstMs, dt);
      if (loadingEl && !loadingEl.hidden) loadingEl.hidden = true;
    }
    if (now - periodStart >= FPS_PERIOD_MS) {
      const secs = (now - periodStart) / 1000, physics = loop.PhysicsCount();
      if (showFps && fpsEl) {
        fpsEl.textContent = `screen ${Math.round(refreshes / secs)} Hz · drawn ${Math.round(draws / secs)}/s · physics ${Math.round((physics - physics0) / secs)}/s (60 = full speed)\n`
          + `frame ${draws ? (costMs / draws).toFixed(1) : '-'} ms avg, ${worstMs.toFixed(0)} ms worst${rawPace ? ' · pace=raw' : ''}`;
      }
      periodStart = now; refreshes = draws = 0; costMs = worstMs = 0; physics0 = physics;
    }
  };
  requestAnimationFrame(frame);
}
console.log(`[host] wrote ${manifest.length} content files (${bytes.reduce((n, b) => n + b.length, 0)} bytes) into the FS; calling Main`);
if (apiMode) {
  const api = () => assemblyExports().then((e) => e.GeneratorApi);
  const ready = () => api().then((g) => new Promise((resolve) => {
    const poll = () => (g.IsReady() ? resolve(true) : setTimeout(poll, API_READY_POLL_MS));
    poll();
  }));
  // generateText returns the document exactly as C# wrote it (for byte comparison); generate parses it.
  const generateText = async (args = {}) => { const g = await api(); await ready(); return g.GenerateJson(JSON.stringify(args)); };
  Object.assign(window.cloudberry, {
    ready,
    generateText,
    generate: async (args = {}) => JSON.parse(await generateText(args)),
    timings: async () => JSON.parse((await api()).LastTimings()),
    // Every argument generate() accepts (ck-level/2), its type and default; the settings groups are found by reflection.
    schema: async () => { const g = await api(); await ready(); return JSON.parse(g.SchemaJson()); },
  });
  console.log('[host] api mode: window.cloudberry.generate({seed, difficulty, hero, length, geometry, tileset}) once ready');
  if (embedMode) {
    const embed = await import('./embed.js');
    // The canonical document hash the region payload carries (embed.js: engine.build blanked).
    window.cloudberry.docSha256 = async (args) => embed.docSha256(await generateText(args));
    embed.start({ exports: await assemblyExports(), generateText, ready });
  }
  // With FNA's loop Main never returns here; with the page loop it returns and the frames start.
  runMain().then(() => { console.log('[host] Main returned'); if (jsLoop) return startFrameLoop(); }, (e) => console.log('[host] Main: ' + (e && e.message || e)));
} else {
  if (!jsLoop && loadingEl) loadingEl.hidden = true;   // FNA's loop never returns from Main
  await runMain();
  console.log('[host] Main returned');
  if (jsLoop) await startFrameLoop();
}
