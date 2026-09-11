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

## Status

Phase 1, Milestone A (de-risk spike) — in progress. See the plan's milestone checklist.
