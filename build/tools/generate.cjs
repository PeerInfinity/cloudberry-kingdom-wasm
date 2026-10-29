// generate.cjs — Calls the generator API of a served site in headless Chromium and writes the document.
//
// Usage:  node build/tools/generate.cjs <site-url> '<args-json>' [out.json]
//   e.g.  node build/tools/generate.cjs http://127.0.0.1:8765/ '{"seed":7,"difficulty":4,"hero":"Normal","length":6700}' doc.json
// Prints the document's byte length and sha256 (the document exactly as C# wrote it: tab, Windows and Linux builds of
// one commit return the same bytes). Needs Playwright with its Chromium: `npm i playwright && npx playwright install chromium`
// in any directory, then run with NODE_PATH=<that dir>/node_modules.
const { chromium } = require('playwright');
const crypto = require('crypto');
const fs = require('fs');

const READY_TIMEOUT_MS = 180000;
// Headless Chromium has no GPU: SwiftShader provides WebGL 2 (slow drawing, which generation does not need).
const CHROMIUM_ARGS = ['--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'];

const [siteUrl, argsJson, outPath] = process.argv.slice(2);
if (!siteUrl || !argsJson) { console.error('usage: node generate.cjs <site-url> <args-json> [out.json]'); process.exit(2); }
const url = new URL(siteUrl);
url.searchParams.set('api', '1');

(async () => {
  const browser = await chromium.launch({ args: CHROMIUM_ARGS });
  try {
    const page = await browser.newPage({ viewport: { width: 1280, height: 720 } });
    page.on('pageerror', (e) => { if (e.message !== 'unwind') console.error(`PAGEERROR: ${e.message}`); });
    const t0 = Date.now();
    await page.goto(url.href);
    await page.waitForFunction(() => window.cloudberry, null, { timeout: READY_TIMEOUT_MS });
    await page.evaluate(() => window.cloudberry.ready());
    const readyMs = Date.now() - t0;
    const text = await page.evaluate((a) => window.cloudberry.generateText(JSON.parse(a)), argsJson);
    const timings = await page.evaluate(() => window.cloudberry.timings());
    if (outPath) fs.writeFileSync(outPath, text);
    const sha = crypto.createHash('sha256').update(text, 'utf8').digest('hex');
    console.log(JSON.stringify({ bytes: Buffer.byteLength(text, 'utf8'), sha256: sha, readyMs, timings, error: JSON.parse(text).error }));
  } finally {
    await browser.close();
  }
})().catch((e) => { console.error(e); process.exit(1); });
