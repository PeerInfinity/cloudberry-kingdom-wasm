import { dotnet } from './_framework/dotnet.js';

// Engine-only Content (about 1.08 MB: fonts .fnt, 4 blank .dds, 17 .fxb, .smo, data), staged by build.sh.
// emcc --preload-file does not survive the .NET ES6 loader (dotnet.es6.pre.js
// replaces Module after the packager registered its preRun), so the page writes
// the files into the Emscripten FS itself, between create() and runMain().
const canvas = document.getElementById('canvas');
// ?mono_log=<mask> turns on Mono's own logging (e.g. dll = P/Invoke resolution).
const params = new URLSearchParams(location.search);

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
const apiMode = params.has('api') ? params.get('api') === '1' : buildInfo.api === true;
const API_READY_POLL_MS = 100;
let builder = dotnet.withModuleConfig({ canvas }).withApplicationArguments(...(apiMode ? ['--api'] : []));
if (buildInfo.build) builder = builder.withEnvironmentVariable('CK_BUILD', buildInfo.build);
// Mono's interpreter option PRECISE_GC (on by default in .NET 9) makes every collection walk the thread's LMF /
// interpreter-frame chain (interp_mark_stack -> interp_mark_no_ref_slots). In this single-threaded AOT page that walk
// never returns once the game is on FNA's emscripten main loop: the first collection at a ScreenSaver level swap
// froze the tab. '-precise' turns only that option off (the interpreter stack is then scanned conservatively).
// ?interp_opts=<MONO_INTERPRETER_OPTIONS> overrides it (an empty value restores the default).
if (params.get('loop') !== 'fna') builder = builder.withEnvironmentVariable('CK_MAIN_LOOP', 'js');
builder = builder.withEnvironmentVariable('MONO_INTERPRETER_OPTIONS', params.has('interp_opts') ? params.get('interp_opts') : '-precise');
if (params.get('mono_log')) builder = builder.withEnvironmentVariable('MONO_LOG_LEVEL', 'debug').withEnvironmentVariable('MONO_LOG_MASK', params.get('mono_log'));
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

const bytes = await Promise.all(manifest.map(async (rel) => new Uint8Array(await (await fetch('./' + rel)).arrayBuffer())));
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
  // A throw caught inside the next game frame; resolves to the count of probes that came back (0 = the page died).
  throwProbe: async (waitMs = THROW_PROBE_WAIT_MS) => {
    const loop = (await assemblyExports()).BrowserMainLoop;
    loop.QueueThrowProbe();
    await new Promise((r) => setTimeout(r, waitMs));
    return loop.ThrowProbeCount();
  },
};
const THROW_PROBE_WAIT_MS = 500;

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
async function startFrameLoop() {
  const loop = (await assemblyExports()).BrowserMainLoop;
  let frameErrors = 0;
  const frame = () => {
    try { loop.Frame(); } catch (e) {
      if (frameErrors++ < FRAME_ERROR_REPORT_LIMIT) console.error(`[host] frame threw (#${frameErrors}): ${e && e.message || e}`);
    }
    requestAnimationFrame(frame);
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
  });
  console.log('[host] api mode: window.cloudberry.generate({seed, difficulty, hero, length, geometry, tileset}) once ready');
  // With FNA's loop Main never returns here; with the page loop it returns and the frames start.
  runMain().then(() => { console.log('[host] Main returned'); if (jsLoop) return startFrameLoop(); }, (e) => console.log('[host] Main: ' + (e && e.message || e)));
} else {
  await runMain();
  console.log('[host] Main returned');
  if (jsLoop) await startFrameLoop();
}
