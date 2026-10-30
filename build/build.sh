#!/usr/bin/env bash
# build.sh — Builds the engine-only browser page from this repository.
#
# Usage:  build/build.sh --out <site-dir>
#             [--fna <FNA clone>] [--natives <dir>] [--debug-log] [--api] [--build-id <text>]
#             [--fxc <fxc.exe>] [--shaders <dir of .fxb>] [--dotnet <dotnet>]
#
#   --out        the site directory to (re)create: _framework/, Content/, index.html, main.js, touch.js,
#                content-manifest.json, build-info.json. NEVER commit or publish it: the .wasm and the
#                assemblies in _framework/ are the game's compiled code.
#   --fna        a clone of FNA-XNA/FNA with its submodules (default: the external/FNA submodule)
#   --natives    where FNA-WASM-Build's archives live; downloaded there if absent
#                (default: ${XDG_CACHE_HOME:-~/.cache}/cloudberry-browser/natives/<release>)
#   --debug-log  define DEBUG in the Release/AOT build (the game's Tools.Write log lines, e.g. "Level made!")
#   --api        the page starts in generator-API mode without ?api=1 (?api=0 turns it off)
#   --build-id   the text the page reports as engine.build (default: this repository's commit, + "-dirty"
#                if the tree has changes)
#   --shaders    a directory of compiled .fxb files (default: the committed Game/ContentPC/Shaders/*.fxb)
#   --fxc        recompile the .fx sources with this fxc.exe (Windows SDK) instead of using --shaders;
#                only needed after editing a shader
#   --dotnet     the dotnet executable (default: $DOTNET, ~/.dotnet/dotnet, dotnet on PATH); it needs
#                the .NET 9 SDK and the wasm-tools workload
#
# The AOT publish reruns on every build and takes minutes (about 6 on an 8-core machine).
set -euo pipefail
export LC_ALL=C

# FNA-WASM-Build release the page is known to run on (2026-07-25; emsdk 3.1.56 = the .NET 9 workload's).
# The single-threaded (ST-) archives, pinned by sha256, are saved under their DllImport names.
readonly NATIVES_REPO="r58Playz/FNA-WASM-Build"
readonly NATIVES_RELEASE="5ecb4294-8cbb-42f1-a73b-476bb46ddbb6"
readonly -A NATIVES_SHA256=(
  [SDL3.a]=9dd42e8642c409de6f0441b7a2bc7b156e61675ba56a8d5a2009011e377e8e08
  [FNA3D.a]=4619e3aeef4d492a6780feb70e0f315f0ea515fb302b7a98996b6bdfece66e7a
  [libmojoshader.a]=ec482b00526301fdf17dfa3ad701b9de6f6f3d1d1d412531f2cdd00754afd355
  [FAudio.a]=1b0f28739c837db4702304dcd169c46dc5ebd9d3b0eeec0f859f085f31d6dc59
)
readonly NATIVES_PREFIX="ST-"

# Engine-only content: what LoadResources = false still reads (no art, sound, music or video).
readonly CONTENT_SRC_REL="Game/ContentPC"
readonly CONTENT_ROOT_FILES=(White.dds Circle.dds Smooth.dds Transparent.dds)
readonly CONTENT_DIRS=(Campaign Localization Objects)
readonly CONTENT_FONT_GLOB="*.fnt"
readonly SHADER_INCLUDE_ONLY="RootEffect"
readonly FXC_PROFILE="fx_2_0"

readonly TFM="net9.0"
readonly OBJ_DIR="obj-browser"
readonly BIN_DIR="bin-browser"

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$HERE/.." && pwd)"

usage() { sed -n '2,25p' "$0" | sed 's/^# \{0,1\}//' >&2; exit 2; }
GAME="$REPO_ROOT" OUT="" FNA="" NATIVES="" DEBUG_LOG=0 API=0 BUILD_ID="" FXC_EXE="" SHADERS="" DOTNET_EXE="${DOTNET:-}"
while [ $# -gt 0 ]; do
  case "$1" in
    --out) OUT="$2"; shift 2 ;;
    --fna) FNA="$2"; shift 2 ;;
    --natives) NATIVES="$2"; shift 2 ;;
    --debug-log) DEBUG_LOG=1; shift ;;
    --api) API=1; shift ;;
    --build-id) BUILD_ID="$2"; shift 2 ;;
    --fxc) FXC_EXE="$2"; shift 2 ;;
    --shaders) SHADERS="$2"; shift 2 ;;
    --dotnet) DOTNET_EXE="$2"; shift 2 ;;
    -h|--help) usage ;;
    *) echo "unknown argument: $1" >&2; usage ;;
  esac
done
[ -n "$OUT" ] || usage
FNA="$(cd "${FNA:-$REPO_ROOT/external/FNA}" && pwd)"
NATIVES="${NATIVES:-${XDG_CACHE_HOME:-$HOME/.cache}/cloudberry-browser/natives/$NATIVES_RELEASE}"
[ -f "$FNA/FNA.Core.csproj" ] || { echo "no FNA.Core.csproj in $FNA: run git submodule update --init --recursive (or pass --fna)" >&2; exit 1; }
[ -n "$FXC_EXE" ] || SHADERS="${SHADERS:-$GAME/$CONTENT_SRC_REL/Shaders}"

if [ -z "$DOTNET_EXE" ]; then
  if [ -x "$HOME/.dotnet/dotnet" ]; then DOTNET_EXE="$HOME/.dotnet/dotnet"; else DOTNET_EXE="$(command -v dotnet || true)"; fi
fi
[ -n "$DOTNET_EXE" ] || { echo "dotnet not found (--dotnet); install the .NET 9 SDK and 'dotnet workload install wasm-tools'" >&2; exit 1; }
export DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$(readlink -f "$DOTNET_EXE")")}" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

step() { echo "== $* ($(date +%H:%M:%S))"; }

# ---- 1) native archives ------------------------------------------------------
step "natives: $NATIVES_REPO release $NATIVES_RELEASE -> $NATIVES"
mkdir -p "$NATIVES"
for name in "${!NATIVES_SHA256[@]}"; do
  if [ ! -f "$NATIVES/$name" ]; then
    url="https://github.com/$NATIVES_REPO/releases/download/$NATIVES_RELEASE/$NATIVES_PREFIX$name"
    echo "  downloading $url"
    curl -fsSL -o "$NATIVES/$name.part" "$url"
    mv "$NATIVES/$name.part" "$NATIVES/$name"
  fi
  echo "${NATIVES_SHA256[$name]}  $NATIVES/$name" | sha256sum -c --quiet - || {
    echo "sha256 mismatch for $NATIVES/$name (expected the $NATIVES_PREFIX$name of release $NATIVES_RELEASE)" >&2; exit 1; }
done

# ---- 2) publish (AOT; Release is required for AOT) ----------------------------
step "dotnet publish Game.Browser.csproj (AOT)"
props=(-c Release -p:RunAOTCompilation=true "-p:BaseIntermediateOutputPath=$OBJ_DIR/" "-p:BaseOutputPath=$BIN_DIR/"
       "-p:CkNativeLibDir=$NATIVES" "-p:CkNativeSourceDir=$HERE/native" "-p:CkFnaDir=$FNA" -nodeReuse:false)
[ $DEBUG_LOG -eq 1 ] && props+=(-p:CkDebugLog=true)
publish_root="$GAME/Game/$BIN_DIR/Release/$TFM/publish"
rm -rf "$publish_root"   # the publish wwwroot otherwise accumulates fingerprinted copies of earlier builds
start=$(date +%s)
(cd "$GAME/Game" && "$DOTNET_EXE" publish Game.Browser.csproj "${props[@]}")
echo "  publish took $(( $(date +%s) - start )) s"

# ---- 3) stage the site --------------------------------------------------------
step "stage $OUT"
rm -rf "$OUT"; mkdir -p "$OUT/Content"
cp -rL "$publish_root/wwwroot/_framework" "$OUT/_framework"
cp "$HERE/web/index.html" "$HERE/web/main.js" "$HERE/web/touch.js" "$OUT/"

src="$GAME/$CONTENT_SRC_REL"
for f in "${CONTENT_ROOT_FILES[@]}"; do cp "$src/$f" "$OUT/Content/$f"; done
for d in "${CONTENT_DIRS[@]}"; do cp -r "$src/$d" "$OUT/Content/$d"; done
mkdir -p "$OUT/Content/Fonts" "$OUT/Content/Shaders"
cp "$src"/Fonts/$CONTENT_FONT_GLOB "$OUT/Content/Fonts/"

if [ -n "$SHADERS" ]; then
  cp "$SHADERS"/*.fxb "$OUT/Content/Shaders/"
  echo "  $(ls "$OUT/Content/Shaders" | wc -l) compiled shaders from $SHADERS"
else
  shader_out_win="$(wslpath -w "$OUT/Content/Shaders")"
  n=0
  for fx in "$src"/Shaders/*.fx; do
    base="$(basename "$fx" .fx)"
    [ "$base" = "$SHADER_INCLUDE_ONLY" ] && continue
    (cd "$src/Shaders" && "$FXC_EXE" /nologo /T "$FXC_PROFILE" /Fo "$shader_out_win\\$base.fxb" "$base.fx" > /dev/null 2>&1) || true
    [ -f "$OUT/Content/Shaders/$base.fxb" ] || { echo "shader failed: $base.fx ($FXC_EXE)" >&2; exit 1; }
    n=$((n + 1))
  done
  echo "  $n shaders compiled with $FXC_EXE"
fi

(cd "$OUT" && find Content -type f | sort | python3 -c 'import sys,json; print(json.dumps([l.strip() for l in sys.stdin]))' > content-manifest.json)

if [ -z "$BUILD_ID" ]; then
  dirty() { git -C "$1" diff --quiet HEAD -- . 2>/dev/null || echo -dirty; }
  BUILD_ID="$(git -C "$REPO_ROOT" rev-parse HEAD)$(dirty "$REPO_ROOT")"
fi
python3 - "$OUT/build-info.json" "$BUILD_ID" "$API" <<'EOF'
import json, sys
info = {"build": sys.argv[2]}
if sys.argv[3] == "1":
    info["api"] = True
open(sys.argv[1], "w").write(json.dumps(info) + "\n")
EOF

echo "  build-info: $(cat "$OUT/build-info.json")"
echo "  $(du -sb "$OUT" | cut -f1) bytes in $OUT; serve it with build/serve.sh $OUT <port>"
