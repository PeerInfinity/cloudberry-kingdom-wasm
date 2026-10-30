# Cloudberry Kingdom — engine-only browser build

Cloudberry Kingdom (Pwnee Studios, 2013) in a browser tab. It runs the game's own engine and level generator with
**no art, sound, music or video**: levels, hazards and the hero are drawn as collision boxes (Pwnee's own
placeholder mode). It uses FNA on .NET 9 browser-wasm (Mono AOT, single-threaded, WebGL 2).

**Play it:** <https://peerinfinity.github.io/cloudberry-kingdom-wasm/> (about 25 MB; keyboard, or on-screen buttons on a touch screen).
The page is built and deployed by [`.github/workflows/pages.yml`](.github/workflows/pages.yml) on every push to `main`;
the built game exists only in that deployment, never in this repository.

**Read [`LICENSE.md`](LICENSE.md) first.** The game's files here belong to Pwnee Studios and carry no licence.
This repository holds only the minimum the browser build needs, so that the game's code stays in one place.

## What is here

| Path | What it is |
|---|---|
| `Game/` | Pwnee's files: 405 C# files (the compile set of `Game/Game.Browser.csproj`) and the engine-only data in `Game/ContentPC/` (4 tiny `.dds`, `Campaign/`, `Localization/`, `Objects/`, the `.fnt` glyph tables, the shader sources and their compiled `.fxb`) |
| `build/` | `build.sh` (builds the page), `serve.sh` (serves it on 127.0.0.1), `web/` (the page and its host shims), `native/__Native.c`, `tools/generate.cjs` (calls the generator API from headless Chromium) |
| `external/FNA` | FNA, as a git submodule |

The history keeps the layers apart: Pwnee's files unmodified → fera89's FNA revival patches → the browser changes → the compiled shaders → the build tooling.

## Build

Linux or WSL, with the **.NET 9 SDK** and the **`wasm-tools` workload**. A user-local install works (about 1.6 GB):
`dotnet-install.sh --channel 9.0 --install-dir ~/.dotnet && ~/.dotnet/dotnet workload install wasm-tools`.

```bash
git clone <this repository> cloudberry-kingdom-wasm && cd cloudberry-kingdom-wasm
git submodule update --init external/FNA
git -C external/FNA submodule update --init lib/SDL2-CS lib/SDL3-CS lib/FAudio lib/Theorafile lib/dav1dfile
build/build.sh --out site            # about 7 minutes (AOT); downloads 2.7 MB of pinned native archives on the first run
build/serve.sh site 8765             # http://127.0.0.1:8765/   (?api=1 for the generator API)
```

No shader compiler is needed: the 17 shaders are committed already compiled. `build/build.sh --fxc <fxc.exe>`
recompiles them from the `.fx` sources (Windows SDK) after a shader edit. `--debug-log` turns on the game's own log
lines, `--api` starts the page in generator-API mode. `build/build.sh --help` lists every option.

## Playing

The game's own PC keys, unchanged. Menus: arrows (or W/S) choose, Enter or Space confirm, Esc or Backspace back.
In a level: ←/→ or A/D run, ↑ or W jump, ↓ or S duck, ↑ or W at the exit door finish the level, Space quick
respawn, Enter the power-up menu, Esc or Backspace pause. Free Play is inside The Arcade. The page's
collapsible Controls panel lists the same keys.

**Touch screens:** on-screen buttons (← ↓ →, a big ↑ for jump, and Esc / Space / Enter) press the same keys, so they
work in menus too: ↑ ↓ choose, Enter confirms, Esc goes back. A finger can slide between buttons, and two fingers
work at once (run and jump). The Controls panel switches them Auto (touch screens only) / On / Off
(`build/web/touch.js`).

This build loads no art, so where the game would draw a key's picture (the door's "Press ↑", the hints) it writes
the key's name instead. The drawing buffer is fixed at 1280×720 (the game would otherwise size it to the screen,
which squashes it on a phone); the page scales it to fit.

## Generator API

`?api=1` adds `window.cloudberry.generate({seed, difficulty, hero, length, geometry, tileset})`. It returns a
`ck-level/1` JSON document: blocks, obstacles, the goal, and the computer player's recorded input and trace.
`node build/tools/generate.cjs http://127.0.0.1:8765/ '{"seed":7,"difficulty":4,"hero":"Normal","length":6700}' doc.json`
calls it from headless Chromium (needs Playwright).

## More detail

The engineering record (the walls met, the page's shims, the generator API, `ck-level/1`) is
[`docs/BROWSER.md`](https://github.com/PeerInfinity/cloudberry-kingdom-fna-revival/blob/browser/docs/BROWSER.md) in
[PeerInfinity/cloudberry-kingdom-fna-revival](https://github.com/PeerInfinity/cloudberry-kingdom-fna-revival). That fork
holds the same changes as patches that you apply to your own clone of Pwnee's repository.

## How this was made

The browser changes, the page, the scripts and the docs were written by Claude (Anthropic) sessions directed by
PeerInfinity.
