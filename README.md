# StellarWorldScreen (experiment)

A placeable **video screen in the game world** for Blue Protocol: Star Resonance, fed by a bundled
Windows helper that decodes/receives video and streams raw frames to the plugin over localhost TCP.

> **Experiment plugin — NEVER published.** Not in the public `plugins` registry, not in any release
> train, no CDN manifest. Same class as the never-publish plugins.

## Shape (current — AVPro)

```
Stellar.WorldScreen (C# BepInEx/IL2CPP plugin)
  ├─ AVPro MediaPlayer  ──▶ DisplayUGUI on a world-space Canvas (the screen) + a full-screen "cinema" canvas
  │    (HW decode + A/V sync; audio via AudioOutput.System, since the game's Unity audio is disabled/Wwise)
  ├─ yt-dlp.exe (Process.Start): YouTube → download+mux to a local mp4 AVPro opens (no combined formats)
  └─ overlay window (Load/quality) + proximity control bar + full-screen player bar (seek/volume/auto-hide)
```

> **Architecture pivot (2026-09-13):** playback moved from the original raw-frames-over-wire pipeline to
> the game's own **AVPro** engine. Raw RGBA over TCP + per-frame texture upload can't do smooth 1080p
> (~250 MB/s) and the video/audio ran on two clocks (drift). AVPro hardware-decodes with internal A/V sync
> and correct colour (via `DisplayUGUI`). **The Rust helper (`stellar-castbox` + ffmpeg/ffplay + the wire
> protocol) is now DORMANT** — kept behind `UseAvPro = true` in `WorldScreenPlugin` for rollback, still
> built/deployed but not launched. The WHY behind every AVPro/Wwise/IL2CPP-uGUI decision is banked in the
> devkit doc `../../docs/rendering-images-in-game.md` § "Playing VIDEO + AUDIO in-game (AVPro + Wwise)".

- Design spec: `../../docs/superpowers/specs/2026-09-11-world-screen-video-cast-design.md`
- Implementation plan: `../../docs/superpowers/plans/2026-09-11-world-screen-video-cast.md`
- Wire protocol (plugin ↔ helper contract): [`docs/protocol.md`](docs/protocol.md)

## Layout

- `helper/` — Rust `stellar-castbox` → cross-compiled to `stellar-castbox.exe`.
- `plugin/` — C# `Stellar.WorldScreen` (references `Stellar.Abstractions` + `UnityEngine.*`).
- `vendor/` — bundled `ffmpeg.exe`, `yt-dlp.exe` (Windows; fetched, git-ignored).
- `scripts/` — build/deploy/run helpers.

## Toolchain (sudo-free cross-compile)

Windows builds cross-link via zig:

```sh
export PATH="$HOME/.cargo/bin:$HOME/.local/bin:$PATH"   # rustup + zig
cargo zigbuild --release --target x86_64-pc-windows-gnu # in helper/
```

Native unit/integration tests use the host toolchain: `cargo test` (no zig needed).

## Status (2026-09-13) — owner-confirmed working

A working in-world video player via AVPro. All owner-verified in-game:

- **Video + audio, in sync, up to 1080p** (720p/1080p both confirmed). Sources: local file, direct URL,
  **YouTube** (download+mux to a local mp4 via `yt-dlp` — YouTube has no combined formats, and 1080p needs
  this too). Colour is smooth via `DisplayUGUI` (a plain RawImage of AVPro's raw texture bands — YCbCr/colour
  space). Quality dropdown 360/480/720/1080p.
- **Positional volume** off the world screen: full within 3 m, fading to silent by 40 m (distance measured
  from the PLAYER, not the camera — the camera is far in a zoomed-out view).
- **Proximity control bar** appears within ~12 m of the screen (framework window; `ShouldRender` gated).
- **Full-screen "cinema"**: video canvas at sortingOrder 32756 covers ALL HUD/panels (game + framework +
  other plugins' meters — matching what the game menu does); a custom bottom **player bar** (play/pause,
  restart, mute, **draggable volume slider**, **draggable seek bar with knob + elapsed/total time**, exit)
  that **auto-hides after 3 idle seconds** and returns on mouse movement. Esc also exits.

### Try it in-game (owner)

1. Deploy: `scripts/deploy.sh` (builds plugin + the dormant helper, vendors AVPro refs + ffmpeg/yt-dlp,
   copies to the lowercase `stellar/plugins/worldscreen/` slot).
2. Launch the test client, reach the world. A silent bundled clip auto-plays on the screen.
3. Open the **World Screen** overlay, paste a URL/path + pick quality, **Load**. Walk up to the screen →
   the control bar appears → **Full screen** for cinema mode.

### Not done yet / ideas

- Round seek/volume knobs; hover-highlight; more bar functions (loop, playlist next/prev, quality in-bar).
- Near-instant playback for YouTube (currently pre-downloads) via local HLS/HTTP streaming from a helper.
- Milestone D (real Chromecast receiver), Phase 2 (P2P WebRTC live video) — not started.
- The Rust helper/wire pipeline is dormant (`UseAvPro=true`); remove it if AVPro proves sufficient long-term.

Reusable game facts + IL2CPP-uGUI techniques discovered here are banked in
`../../docs/rendering-images-in-game.md` § "Playing VIDEO + AUDIO in-game (AVPro + Wwise)".
