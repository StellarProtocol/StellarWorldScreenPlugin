#!/usr/bin/env bash
# Build + deploy the WorldScreen plugin and its helper to the test prefix's user-plugin slot.
# Deploys Stellar.WorldScreen.dll + stellar-castbox.exe to the LOWERCASE slot (a capitalized sibling
# would shadow it — see CLAUDE.md). Does NOT touch the framework (already deployed at 2.7.0).
set -euo pipefail
export PATH="$HOME/.cargo/bin:$HOME/.local/bin:$PATH"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DOTNET=/home/dorasu/.dotnet/dotnet

GM="$(ls -d /opt/game/BlueProtocol2/drive_c/Star/StarLauncher/game/release_*/game_mini 2>/dev/null | sort -V | tail -1)"
[ -n "$GM" ] || { echo "no game_mini found" >&2; exit 1; }

echo "=== vendor AVPro interop stub (game's video engine; not in InteropRefs) ==="
mkdir -p "$ROOT/plugin/refs"
cp -f "$GM/BepInEx/interop/AVProVideo.Runtime.dll" "$ROOT/plugin/refs/AVProVideo.Runtime.dll"

echo "=== build plugin (Release) ==="
"$DOTNET" build "$ROOT/plugin/Stellar.WorldScreen.csproj" -c Release --nologo
DLL="$ROOT/plugin/bin/Release/Stellar.WorldScreen.dll"

echo "=== build helper (Windows .exe) ==="
bash "$ROOT/scripts/build-helper.sh" >/dev/null
EXE="$ROOT/helper/target/x86_64-pc-windows-gnu/release/stellar-castbox.exe"

echo "=== ensure vendor media (ffmpeg.exe / ffplay.exe / yt-dlp.exe) ==="
{ [ -f "$ROOT/vendor/ffmpeg.exe" ] && [ -f "$ROOT/vendor/ffplay.exe" ]; } || bash "$ROOT/scripts/fetch-vendor.sh"

SLOT="$GM/stellar/plugins/worldscreen"   # lowercase — never capitalize

mkdir -p "$SLOT"
cp -f "$DLL" "$SLOT/Stellar.WorldScreen.dll"
cp -f "$EXE" "$SLOT/stellar-castbox.exe"
cp -f "$ROOT/vendor/ffmpeg.exe" "$SLOT/ffmpeg.exe"                       # decodes video + audio (as sibling)
cp -f "$ROOT/vendor/ffplay.exe" "$SLOT/ffplay.exe"                       # plays the decoded audio (as sibling)
[ -f "$ROOT/vendor/yt-dlp.exe" ] && cp -f "$ROOT/vendor/yt-dlp.exe" "$SLOT/yt-dlp.exe" || true
[ -f "$ROOT/vendor/test-clip.mp4" ] && cp -f "$ROOT/vendor/test-clip.mp4" "$SLOT/test-clip.mp4" || true
# AVPro spike test clip (720p + 440Hz tone), generated into vendor/ by hand; keep whatever is in the slot.
[ -f "$ROOT/vendor/avpro-test.mp4" ] && cp -f "$ROOT/vendor/avpro-test.mp4" "$SLOT/avpro-test.mp4" || true

echo "=== deployed to $SLOT ==="
ls -la "$SLOT"
echo "framework version deployed: $(cat "$GM/BepInEx/plugins/Stellar.Framework/.stellar-version" 2>/dev/null)"
