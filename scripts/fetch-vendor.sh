#!/usr/bin/env bash
# Fetch the bundled Windows media tools (ffmpeg.exe, yt-dlp.exe) into vendor/.
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

echo "=== ffmpeg.exe (BtbN static win64 gpl) ==="
if [ ! -f "$VENDOR/ffmpeg.exe" ]; then
    curl -sSfL --retry 3 -o "$TMP/ffmpeg.zip" \
        https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip
    # extract only ffmpeg.exe (+ ffprobe.exe) from the bin/ dir, flattening into vendor/
    (cd "$TMP" && unzip -o -j ffmpeg.zip '*/bin/ffmpeg.exe' '*/bin/ffprobe.exe' -d "$VENDOR" >/dev/null)
fi
ls -la "$VENDOR/ffmpeg.exe" "$VENDOR/ffprobe.exe" 2>/dev/null || true
echo "=== done ==="
