# Wire protocol — plugin ↔ helper (authoritative contract)

`helper/src/wire.rs` and `plugin/Net/WireCodec.cs` MUST stay byte-identical to this document. Any field
change updates **both** implementations in the **same** commit.

Single localhost TCP connection. All integers **little-endian**. Envelope:

```
[u32 len][u8 type][payload...]      // len = number of bytes of (type + payload)
```

The reader reads a 4-byte little-endian `len`, then exactly `len` bytes; the first of those is the `type`.

| type | name | dir | payload |
| --- | --- | --- | --- |
| 0x01 | HELLO | P→H | `u16 protoVersion`, `u8 flags` |
| 0x02 | STREAM_INFO | H→P | `u16 w`, `u16 h`, `u8 pixfmt (0=RGBA)`, `u8 fps`, `u16 titleLen`, `titleLen` bytes UTF-8 |
| 0x03 | FRAME | H→P | `u64 ptsMs`, then `w*h*bpp` raw pixel bytes |
| 0x04 | STATUS | H→P | `u8 state (0 idle, 1 playing, 2 paused, 3 error)`, `u64 positionMs`, `u64 durationMs`, `u16 errLen`, `errLen` bytes UTF-8 |
| 0x05 | CONTROL | P→H | `u8 op`, then op payload |
| 0x06 | AUDIO | H→P | interleaved **S16LE PCM**, **48000 Hz**, **2 channels** — the payload is raw sample bytes (no header; the format is fixed). The plugin feeds these to a 3D AudioSource for spatial playback. |

### CONTROL ops

| op | name | payload |
| --- | --- | --- |
| 0 | play | — |
| 1 | pause | — |
| 2 | stop | — |
| 3 | seek | `u64 ms` |
| 4 | volume | `u8 (0..100)` |
| 5 | load | `u16 urlLen`, `urlLen` bytes UTF-8 |

### Constants

- `protoVersion = 1`
- `pixfmt`: `0 = RGBA` (bpp 4). (Room reserved for `1 = YUV420P`, bpp 1.5 — not used in Phase 1.)
- Default stream: `640 × 360`, RGBA, `30` fps.
- Default listen address: `127.0.0.1:47800`.
- Max message length (either direction): any reader of the `[u32 len]` prefix MUST reject a message
  whose `len` exceeds a sane cap (the Rust helper uses `wire::MAX_MSG_LEN = 64 MiB`) before allocating
  a buffer for the body — a corrupt/hostile length prefix must not trigger an unbounded allocation.
  Not a wire-format change; a receiver-side sanity check both implementations should apply.

### Latest-wins

Applies to **FRAME only**. The helper may drop a FRAME if the socket is not writable; the plugin keeps
only the newest FRAME (never queues). STREAM_INFO/STATUS/CONTROL are never dropped.
