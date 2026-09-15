# Streaming YouTube (>360p) onto the World Screen

How the World Screen plays a YouTube URL at 1080p inside the game, and — more importantly — the
**hard-won facts about the game's video backend** that dictate why the pipeline is shaped the way it is.
Read this before touching `Net/StreamMuxer.cs`, `Net/ProgressiveServer.cs`, `Net/YtDlpResolver.cs`, or the
`LoadSource` url branch in `WorldScreenPlugin.cs`. Every rejected approach below was actually built, deployed,
and tested in-game — don't re-try them.

## The runtime constraint that shapes everything

Video plays through **AVPro → Windows MediaFoundation**, and the game runs under **Proton/wine**, so it's
*wine's/Proton's* MediaFoundation. That MF is a **plain single-file player**, not a browser DASH engine:

- It plays ONE source. It cannot fetch-by-index or mux two streams live the way a browser (MSE/DASH) does.
- It needs a **seekable** source (a complete MP4 with an index) to seek. It cannot seek a live/growing stream.
- Its **mpegts** H.264 path decodes the **wrong colour matrix** on some streams (magenta/green); its **MP4**
  path is always colour-correct.

YouTube, meanwhile, serves **360p as one combined URL** (itag 18) but **every higher resolution as two
separate DASH streams** (video-only + audio-only). So >360p *requires* combining two streams for a player
that can only take one — that tension drives the whole design.

## The pipeline (as built)

1. **Resolve** (`YtDlpResolver`, background thread): `yt-dlp -g -f <MergeSelectorForHeight(H)>` returns the
   direct video-only + audio-only URLs (+ `--print DUR=` for the duration). Result marshalled back to the main
   thread via `Framework.Post`.
2. **Mux** (`StreamMuxer`, main thread → spawns ffmpeg): `ffmpeg -i V -i A -map 0:v:0 -map 1:a:0 -c copy
   -movflags +frag_keyframe+empty_moov+default_base_moof -f mp4 stream.mp4` — one **progressive fragmented
   MP4** (moov at the FRONT → plays while still being written), in a temp dir.
3. **Serve** (`ProgressiveServer`): a loopback HTTP server serves that one growing file with **byte-range +
   wait-for-data** (a read past the current EOF blocks until ffmpeg writes it, or the mux finishes).
4. **Play**: AVPro opens `http://127.0.0.1:PORT/stream.mp4` — MF's normal remote-MP4 path (colour-correct).
5. **Seek**: the growing fragmented MP4 has no index, so MF can't seek it. Instead `DoSeek` **re-streams**:
   restart the mux with `-ss T` (ffmpeg range-fetches from T's byte offset via the source index, YouTube-style,
   without downloading the earlier part). `AvProPlayer.SetPositionOffset(T)` makes the bar read T + local time.

### The resolve clients — `tv_embedded` fast, `deno` fallback (a ~3s latency lever)

- `player_client=tv_embedded` exposes **direct https DASH URLs up to 4K with NO JS runtime**, and resolves in
  **~3.3s**. yt-dlp flags it "unsupported" and warns no-JS extraction is deprecated (YouTube's SABR rollout),
  but it works today for real content.
- `player_client=android` is now **SABR-capped to 360p** (itag 18 only) — useless for >360p.
- The **`deno` JS runtime** (bundled `deno.exe`, ~97MB) is yt-dlp's *supported* path via the default web
  client — reaches everything cleanly — but **~6.6s** (deno spin-up + decipher), roughly DOUBLE.
- So `ResolveUrls` runs **`tv_embedded` (no-JS) FIRST** and only pays for deno when the fast attempt can't get
  the video+audio pair (`< 2` URLs — SABR-stripped). Measured startup dropped from ~8s to ~5s. **Do not put
  deno on the common path.** (`NoJsFallbackClients = "tv_embedded,android"`.)

### Content-Length for the progressive server

`-c copy` copies the exact sample bytes, so the served total ≈ `video clen + audio clen` (both exact, parsed
from the URLs' `clen=` param); box overhead is a few tens of KB. The server advertises that sum (slightly
UNDER the real file), and is **mux-done aware** (`Func<bool> muxDone`) so it stops at the true EOF instead of
hanging when the estimate is off. For a `-ss T` seek the total is scaled by the remaining fraction.

## What DOESN'T work in-game (all built + tested — do not re-try)

| Approach | Result |
| --- | --- |
| **mpegts HLS** (`-hls_segment_type mpegts`) | Streams fully, but MF decodes it with the **wrong colour matrix** — magenta/green. The mux output metadata is correct (bt709); MF ignores the SPS VUI in mpegts and defaults wrong. Unfixable from the mux side. |
| **fMP4 HLS** (`-hls_segment_type fmp4`) | Correct colour, but MF **stalls after the first segment** — it doesn't continue an fMP4 live playlist under Proton. (mpegts HLS *does* continue; container-specific MF bug.) |
| **Live fragmented-MP4 over ffmpeg `-listen 1` HTTP** | AVPro "opens" it but gets **no frames** (VideoWidth stays 0). MF needs a seekable/known-length source; a non-seekable chunked stream won't parse. |
| **Two players (video-only + audio-only), synced in-plugin** | Fast start + native colour, BUT the game's MF **can't keep two separate streams in sync or seek them** — audio leads, and the video-only DASH URL doesn't seek natively (its index is DASH-structured, not a simple faststart moov). Reverted. |
| **`-copyts` / `-copyts -start_at_zero` to fix seek A/V sync** | Produces an **empty/broken** fragmented MP4 with these DASH inputs under this ffmpeg. Don't use. |

The **single progressive fragmented-MP4 over a range HTTP server** is the one that works: MP4 container
(colour-correct), served with range+Content-Length (MF's proven remote-mp4 path), `moov` at the front (plays
while growing).

## Known, accepted limitations (owner chose "keep streaming" over "prepare then play", 2026-09-15)

- **~5s load** (resolve ~3s + ffmpeg first-bytes ~2s). Largely inherent to fetching + combining YouTube
  streams for a non-DASH player; YouTube's own speed comes from a purpose-built web DASH engine.
- **Seek re-buffers ~2s** (a fresh mux from T) and shows a **brief A/V catch-up**: `-ss` snaps the video to
  its nearest keyframe (a few seconds before T) while audio starts at T, so the picture catches up to the
  sound for a moment after a jump. `-c copy` can't do frame-accurate seek; the only artifact-free seek is a
  complete indexed file (the rejected "prepare then play": mux the whole file, then native seek — a
  faststart/two-output variant was built and reverted because it meant an upfront wait).
- **A/V sync on straight playback is correct** because `-c copy` into MP4 preserves it via **edit lists** —
  mpegts does NOT, which is why the earlier mpegts mux desynced and needed `-c:a aac` re-encode.

## Perf / cleanup

- The mux CPU is **~0%** (`-c copy`, no re-encode). The cost is **disk**: each load/seek caches the video to
  a temp dir (~video size), and a seek re-downloads from T.
- `StreamMuxer.Stop()` kills ffmpeg + the server and deletes the temp dir **off-thread** (a seek's partial
  download can be large; a synchronous delete on the game frame would hitch). `SweepOldTempDirs()` runs at
  plugin startup to clear any dir stranded by a crash/kill (these had leaked to ~1.3GB across sessions).

## Off-game validation

Everything except MF's actual decode is verifiable with the bundled Windows tools under
`WINEPREFIX=/home/dorasu/.wine-castbox` (yt-dlp.exe/ffmpeg.exe/ffprobe.exe/deno.exe in `vendor/`):
resolve, the mux's colour/sync/moov-order, and the HTTP server (real-socket xUnit tests:
`ProgressiveServerTests`, `YtDlpSelectorTests`). The **MF-plays-it** step only reveals itself in-game — which
is why every color/stall/seek bug above needed a live round to find.
