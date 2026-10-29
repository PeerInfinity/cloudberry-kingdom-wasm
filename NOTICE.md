# Notice and attribution

**Cloudberry Kingdom** © Pwnee Studios — <https://github.com/PwneeStudios/Cloudberry-Kingdom>. No licence:
see [`LICENSE.md`](LICENSE.md) § 1.

**FNA revival patches** © 2026 Christian Bittencort (fera89) —
<https://github.com/fera89/cloudberry-kingdom-fna-revival>, MIT.

| Component | Where it is | Source | Licence |
|---|---|---|---|
| **FNA** | `external/FNA` (git submodule, FNA-XNA/FNA @ `4ab7c47`), compiled into a built page | flibitijibibo & contributors — <https://fna-xna.github.io> | Ms-PL |
| **SDL2-CS, SDL3-CS, FAudio, Theorafile, dav1dfile** (C# bindings) | FNA's own submodules | flibitijibibo, FNA-XNA, MoonsideGames | zlib |
| **SDL3, FNA3D, MojoShader, FAudio** (wasm archives `SDL3.a`, `FNA3D.a`, `libmojoshader.a`, `FAudio.a`) | downloaded by `build/build.sh`, pinned by sha256, never committed; linked into a built page | builds by r58Playz — <https://github.com/r58Playz/FNA-WASM-Build>, release `5ecb4294-8cbb-42f1-a73b-476bb46ddbb6` | the components are zlib; the build repository states no licence |
| **.NET runtime + `wasm-tools` workload** (Mono on WebAssembly) | in a built page's `_framework/` | .NET Foundation / Microsoft | MIT |
| **Emscripten** (runtime code in a built page) | in a built page's `_framework/` | Emscripten contributors | MIT / University of Illinois NCSA |
