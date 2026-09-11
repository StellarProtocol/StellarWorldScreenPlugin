#!/usr/bin/env bash
# Manual/debug launch of stellar-castbox.exe under Wine (dev box).
#
# On a player's machine the plugin auto-launches the .exe natively; on the dev box the plugin
# (inside the game's Proton prefix) will do the same. This script is only for standalone testing
# of the .exe without the game — it runs the same Windows binary under system Wine.
#
# Usage: scripts/run-helper.sh [--listen 127.0.0.1:47800] [--source testpattern]
set -euo pipefail
EXE="$(dirname "$0")/../helper/target/x86_64-pc-windows-gnu/release/stellar-castbox.exe"
[ -f "$EXE" ] || { echo "build first: scripts/build-helper.sh" >&2; exit 1; }

# Use a dedicated throwaway Wine prefix so we never touch the game's prefix for a smoke test.
export WINEPREFIX="${WINEPREFIX:-$HOME/.wine-castbox}"
export WINEDEBUG="${WINEDEBUG:-fixme-all,err-all}"   # quiet Wine's chatter

ARGS=("$@")
[ ${#ARGS[@]} -gt 0 ] || ARGS=(--listen 127.0.0.1:47800 --source testpattern)
exec wine "$EXE" "${ARGS[@]}"
