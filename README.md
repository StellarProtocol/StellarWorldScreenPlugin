# StellarWorldScreen (experiment)

A placeable **video screen in the game world** for Blue Protocol: Star Resonance, fed by a bundled
Windows helper that decodes/receives video and streams raw frames to the plugin over localhost TCP.

> **Experiment plugin — NEVER published.** Not in the public `plugins` registry, not in any release
> train, no CDN manifest. Same class as the never-publish plugins.

## Shape

```
stellar-castbox.exe (Rust)  ──frames(TCP)──▶  Stellar.WorldScreen (C# BepInEx/IL2CPP plugin)
  cast receiver + URL/file       ◀──control──   world-space Canvas + RawImage + overlay
  FFmpeg decode, WASAPI audio
```

Both processes run in the **same runtime**: native Windows for players, inside the game's Proton/Wine
prefix on the dev box. The plugin auto-launches the helper (`Process.Start`).

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

## Status (2026-09-12)

**Milestone A (de-risk spike): DONE** — the world-space video screen renders in-game (owner-confirmed).
**Milestone B (real video): functionally complete (minus audio)** — the helper decodes real video and the
plugin plays it on the world screen. Sources supported, all drivable from the in-game overlay:

- **Local file** (loops forever) — a bundled `test-clip.mp4` auto-plays if present next to the helper.
- **Direct URL** (mp4 / HLS / …).
- **YouTube URL** (resolved via bundled `yt-dlp.exe`).

Verified: real H.264 decodes + loops under the game's Proton prefix (800+ frames past 25s); a YouTube URL
resolves + decodes end-to-end under Wine (800+ frames); the control overlay works in-game
(status line + URL/path input + Load/Stop). **Audio is not wired yet** (video only — `-an`).

### Try it in-game (owner)

1. Deploy: `scripts/deploy.sh` (builds plugin + helper, fetches ffmpeg/yt-dlp, copies to the lowercase
   `stellar/plugins/worldscreen/` slot). Already deployed to the **test prefix**.
2. Launch the test client and reach the world. The screen auto-plays the bundled demo clip (looping).
3. Open the **World Screen** overlay window, paste a video URL or file path, hit **Load**. **Stop / Pattern**
   returns to the test pattern.

### Not done yet

- **Audio** (Milestone B3) — needs your ears to verify sound under Proton; deliberately deferred.
- A clean in-world screenshot of a real clip on the 3D screen (a transient game login flake blocked the
  last capture; an earlier run placed the screen fine).
- Milestones C (placement move/rotate/scale), D (real Chromecast receiver), E (polish).

See `../../docs/superpowers/plans/2026-09-11-world-screen-video-cast.md` and the session ledger
`../../.superpowers/sdd/progress.md` for the full trail.
