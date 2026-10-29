#!/usr/bin/env bash
# serve.sh — Serves a built site on 127.0.0.1 only (the site holds the game's compiled code: keep it local).
#
# Usage:  build/serve.sh <site-dir> [port=8765]
#   then open http://127.0.0.1:<port>/            (attract mode: generated levels drawn as boxes)
#        or   http://127.0.0.1:<port>/?api=1      (generator API: window.cloudberry.generate({...}))
set -euo pipefail
readonly DEFAULT_PORT=8765
SITE="${1:?usage: serve.sh <site-dir> [port]}"
PORT="${2:-$DEFAULT_PORT}"
[ -f "$SITE/index.html" ] && [ -d "$SITE/_framework" ] || { echo "not a built site: $SITE (run build/build.sh)" >&2; exit 1; }
echo "http://127.0.0.1:$PORT/"
exec python3 -m http.server "$PORT" --bind 127.0.0.1 --directory "$SITE"
