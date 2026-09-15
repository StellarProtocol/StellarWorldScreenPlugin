#!/usr/bin/env bash
# Fetch the bundled Windows media tools (ffmpeg.exe, yt-dlp.exe, deno.exe) into vendor/.
# These are git-ignored (vendor/*.exe) — fetched, never committed. Run once (or to update).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VENDOR="$ROOT/vendor"
mkdir -p "$VENDOR"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

echo "=== yt-dlp.exe ==="
if [ ! -f "$VENDOR/yt-dlp.exe" ]; then
    curl -sSfL --retry 3 -o "$VENDOR/yt-dlp.exe" \
        https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe
fi
ls -la "$VENDOR/yt-dlp.exe"

echo "=== ffmpeg.exe + ffplay.exe (BtbN static win64 gpl) ==="
if [ ! -f "$VENDOR/ffmpeg.exe" ] || [ ! -f "$VENDOR/ffplay.exe" ]; then
    curl -sSfL --retry 3 -o "$TMP/ffmpeg.zip" \
        https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip
    # extract ffmpeg.exe (video decode) + ffplay.exe (audio playback) from bin/, flattening into vendor/
    (cd "$TMP" && unzip -o -j ffmpeg.zip '*/bin/ffmpeg.exe' '*/bin/ffplay.exe' -d "$VENDOR" >/dev/null)
fi
ls -la "$VENDOR/ffmpeg.exe" "$VENDOR/ffplay.exe" 2>/dev/null || true

echo "=== deno.exe (yt-dlp's JS runtime — the SUPPORTED path that reaches 1080p+) ==="
if [ ! -f "$VENDOR/deno.exe" ]; then
    curl -sSfL --retry 3 -o "$TMP/deno.zip" \
        https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip
    (cd "$TMP" && unzip -o -j deno.zip 'deno.exe' -d "$VENDOR" >/dev/null)
fi
ls -la "$VENDOR/deno.exe" 2>/dev/null || true
echo "=== done ==="
