// The check coins' look (embedded mode, ⚖ R4/R12/R13): the Archipelago logo, fetched at run time from Archipelago-CC's
// art library, or — when that fetch fails — a larger purple coin. Each look comes in two textures: the coin as it is, and
// an OUTLINE for a check the host says is already checked. The game draws them (C#: EmbedMode.DrawCheckCoin) at the
// coin's own position, so they follow the camera exactly and cost one textured quad per check coin.
//
// The art library is read the way its own loader reads it (Archipelago-CC frontend/modules/contentPacks): the registry
// <base>packs.json lists the pack → <base><pack>/pack.json names its art layer → that manifest's sprite names a sheet
// and a frame size → the sheet PNG, relative to the manifest. Nothing here names the PNG itself. Pages serves the packs
// with access-control-allow-origin: *, so a page on another origin (a local build) can read them too.

export const DEFAULT_PACKS_BASE = 'https://peerinfinity.github.io/Archipelago-CC/packs/';
const PACK_ID = 'archipelago';
const SPRITE_KEY = 'archipelago:pickup/item';
const FETCH_TIMEOUT_MS = 8000;

// Texture side (texels) and the size the game draws it at (world units; an ordinary coin is a 76 wide circle).
const TEX = 64;
const LOGO_SIZE = 128;
const FALLBACK_SIZE = 104;               // C#'s own default look is the same purple coin, radius 52
const FALLBACK_FILL = '#9646e6', FALLBACK_RIM = '#d2a8ff';
const OUTLINE_TEXELS = 5;                // the outline's width inside the shape's edge
const ALPHA_SOLID = 40;                  // a logo texel at least this opaque is part of its shape

async function fetchJson(url) {
  const ctl = new AbortController();
  const timer = setTimeout(() => ctl.abort(), FETCH_TIMEOUT_MS);
  try {
    const res = await fetch(url, { signal: ctl.signal, mode: 'cors' });
    if (!res.ok) throw new Error(`${res.status} ${res.statusText} for ${url}`);
    return await res.json();
  } finally { clearTimeout(timer); }
}

async function fetchBitmap(url) {
  const ctl = new AbortController();
  const timer = setTimeout(() => ctl.abort(), FETCH_TIMEOUT_MS);
  try {
    const res = await fetch(url, { signal: ctl.signal, mode: 'cors' });
    if (!res.ok) throw new Error(`${res.status} ${res.statusText} for ${url}`);
    return await createImageBitmap(await res.blob());
  } finally { clearTimeout(timer); }
}

/** The logo's sheet and frame, following the art library from its registry. */
export async function resolveLogo(base) {
  const root = new URL(base, location.href);
  const registry = await fetchJson(new URL('packs.json', root));
  if (!Array.isArray(registry.packs) || !registry.packs.some((p) => p.id === PACK_ID)) throw new Error(`packs.json lists no "${PACK_ID}" pack`);
  const packUrl = new URL(`${PACK_ID}/pack.json`, root);
  const pack = await fetchJson(packUrl);
  const artPath = pack.layers?.art;
  if (typeof artPath !== 'string') throw new Error(`${PACK_ID}/pack.json has no art layer`);
  const manifestUrl = new URL(artPath, packUrl);
  const manifest = await fetchJson(manifestUrl);
  const sprite = manifest.sprites?.[SPRITE_KEY];
  if (!sprite) throw new Error(`the art manifest has no sprite ${SPRITE_KEY}`);
  const sheet = manifest.sheets?.[sprite.sheet];
  if (!sheet?.file) throw new Error(`the art manifest has no sheet ${sprite.sheet}`);
  return {
    url: new URL(sheet.file, manifestUrl).href,
    frame: { x: 0, y: 0, w: sprite.fw ?? sheet.w, h: sprite.fh ?? sheet.h },
    licence: pack.licence ?? null,
  };
}

function canvas2d() {
  const c = new OffscreenCanvas(TEX, TEX);
  return [c, c.getContext('2d', { willReadFrequently: true })];
}

/** Straight RGBA → premultiplied (the game's blending), as base64 for the C# side. */
function packRgba(data) {
  const out = new Uint8Array(data.length);
  for (let i = 0; i < data.length; i += 4) {
    const a = data[i + 3];
    out[i] = (data[i] * a + 127) / 255; out[i + 1] = (data[i + 1] * a + 127) / 255; out[i + 2] = (data[i + 2] * a + 127) / 255; out[i + 3] = a;
  }
  let s = '';
  for (let i = 0; i < out.length; i += 0x8000) s += String.fromCharCode(...out.subarray(i, i + 0x8000));
  return btoa(s);
}

/** The outline of a shape: its texels within OUTLINE_TEXELS of a texel outside it, at full opacity; the rest clear. */
function outlineOf(data) {
  const solid = (x, y) => x >= 0 && y >= 0 && x < TEX && y < TEX && data[(y * TEX + x) * 4 + 3] >= ALPHA_SOLID;
  const out = new Uint8ClampedArray(data.length);
  const r = OUTLINE_TEXELS;
  for (let y = 0; y < TEX; y++) {
    for (let x = 0; x < TEX; x++) {
      if (!solid(x, y)) continue;
      let edge = false;
      for (let dy = -r; dy <= r && !edge; dy++) for (let dx = -r; dx <= r && !edge; dx++) if (dx * dx + dy * dy <= r * r && !solid(x + dx, y + dy)) edge = true;
      if (!edge) continue;
      const i = (y * TEX + x) * 4;
      out[i] = data[i]; out[i + 1] = data[i + 1]; out[i + 2] = data[i + 2]; out[i + 3] = 255;
    }
  }
  return out;
}

function logoLook(bitmap, frame) {
  const [, g] = canvas2d();
  g.imageSmoothingEnabled = false;   // the 16x16 pixels, scaled up whole
  g.drawImage(bitmap, frame.x, frame.y, frame.w, frame.h, 0, 0, TEX, TEX);
  const fill = g.getImageData(0, 0, TEX, TEX).data;
  // Solid where the logo is: the sheet's soft edge texels (alpha < ALPHA_SOLID) are dropped, the rest made opaque.
  for (let i = 0; i < fill.length; i += 4) fill[i + 3] = fill[i + 3] >= ALPHA_SOLID ? 255 : 0;
  return { fill, outline: outlineOf(fill), size: LOGO_SIZE };
}

function fallbackLook() {
  const [, g] = canvas2d();
  const c = TEX / 2;
  g.fillStyle = FALLBACK_FILL; g.beginPath(); g.arc(c, c, c - 1, 0, 2 * Math.PI); g.fill();
  g.strokeStyle = FALLBACK_RIM; g.lineWidth = 4; g.beginPath(); g.arc(c, c, c - 6, 0, 2 * Math.PI); g.stroke();
  const fill = g.getImageData(0, 0, TEX, TEX).data.slice();
  g.clearRect(0, 0, TEX, TEX);
  g.strokeStyle = FALLBACK_FILL; g.lineWidth = 2 * OUTLINE_TEXELS; g.beginPath(); g.arc(c, c, c - 1 - OUTLINE_TEXELS, 0, 2 * Math.PI); g.stroke();
  const outline = g.getImageData(0, 0, TEX, TEX).data.slice();
  return { fill, outline, size: FALLBACK_SIZE };
}

/**
 * The look to hand the game: {source: 'logo' | 'fallback', reason?, sheet?, w, h, size, fill, outline} (fill/outline:
 * base64 premultiplied RGBA). Never throws: a failed fetch gives the fallback, with the reason.
 */
export async function loadCheckLook(base = DEFAULT_PACKS_BASE) {
  let look, source = 'logo', reason = null, sheet = null;
  try {
    const logo = await resolveLogo(base);
    sheet = logo.url;
    look = logoLook(await fetchBitmap(logo.url), logo.frame);
  } catch (e) {
    source = 'fallback';
    reason = String(e?.message ?? e);
    look = fallbackLook();
  }
  return { source, reason, sheet, base, w: TEX, h: TEX, size: look.size, fill: packRgba(look.fill), outline: packRgba(look.outline) };
}
