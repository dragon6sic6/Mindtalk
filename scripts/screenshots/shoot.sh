#!/bin/zsh
# Screenshots for the README, from the Debug build in demo mode (example data in
# memory only — your own history, stats and settings are never read or written).
#
#   make build && scripts/screenshots/shoot.sh en     # or: sv
#
# Writes docs/images/<lang>/<view>-<light|dark>.png, then the hero images.
# Needs Screen Recording permission for the terminal, and the screen awake.
set -u
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
lang=${1:-en}
BIN="$ROOT/build/Build/Products/Debug/Mindtalk.app/Contents/MacOS/Mindtalk"
OUT="$ROOT/docs/images/$lang"
WFIND="$ROOT/build/wfind"
mkdir -p "$OUT"
[ -x "$WFIND" ] || swiftc -O -o "$WFIND" "$ROOT/scripts/screenshots/wfind.swift"
was_running=$(pgrep -x Mindtalk >/dev/null && echo 1 || echo 0)

shot() { # name mode minW minH layer args...
  local name=$1 mode=$2 w=$3 h=$4 layer=$5; shift 5
  local id=""
  for try in 1 2 3; do
    pkill -x Mindtalk; sleep 0.8
    "$BIN" --demo -appearance $mode -AppleLanguages "($lang)" "$@" >/dev/null 2>&1 &
    sleep 3.5
    id=$("$WFIND" $w $h $layer)
    [ -n "$id" ] && screencapture -x -l $id "$OUT/$name-$mode.png" 2>/dev/null && { echo "✓ $name-$mode"; return; }
  done
  echo "✗ $name-$mode"
}

for mode in light dark; do
  for page in dictation recent vocabulary settings; do shot $page $mode 800 500 0 -reopenPage $page; done
  shot onboarding-welcome $mode 600 500 0 --onboarding-step welcome
  shot onboarding-language $mode 600 500 0 --onboarding-step language --fresh
  for step in permissions key tryIt; do shot onboarding-$step $mode 600 500 0 --onboarding-step $step; done
  shot panel $mode 250 300 25 --show-panel
  shot hud $mode 120 30 25 --demo-hud
done
pkill -x Mindtalk
[ "$was_running" = 1 ] && open -a /Applications/Mindtalk.app

swift "$ROOT/scripts/screenshots/hero.swift" "$OUT"
