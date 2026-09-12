//! TCP frame server — accepts one client, sends STREAM_INFO once, then streams FRAMEs from a
//! `Source` while forwarding any CONTROL messages the client sends back to `control_tx`.
//!
//! Milestone A: a single client, no re-accept on disconnect (a later task adds reconnect).

use std::net::SocketAddr;
use std::sync::Arc;

use anyhow::Context;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::tcp::{OwnedReadHalf, OwnedWriteHalf};
use tokio::net::TcpListener;
use tokio::process::ChildStdout;
use tokio::sync::{mpsc, watch};

use crate::source::Source;
use crate::wire::{self, Control, Msg};

/// The one in-flight frame slot a `watch` channel carries: `None` until the producer emits its
/// first frame. Wrapped in an `Arc` so handing a frame from the `watch::Ref` to the writer (past the
/// `.await` boundary) is an O(1) refcount bump rather than cloning the ~900 KB pixel buffer — see
/// `produce_frames`/`write_frames`.
type FrameSlot = Option<Arc<(u64, Vec<u8>)>>;

/// Accepts one client on `listen`, then streams frames from `source` to it until it disconnects.
///
/// # Latest-wins mechanism
/// FRAME delivery goes through a `tokio::sync::watch` channel rather than a raw non-blocking
/// `try_write`: [`produce_frames`] writes each frame into the channel and never blocks on the
/// socket; [`write_frames`] does a normal (awaiting) `write_all` of whatever is *current* each time
/// it becomes free. A `watch` channel only ever holds the newest value, so if the producer
/// overwrites it several times while the writer is still flushing a slow client's socket, the
/// writer's next read sees only the latest frame — the skipped ones are dropped. This keeps a slow
/// client from ever backing up `source.next_frame()` (the requirement), and — unlike a raw
/// non-blocking `try_write` of a ~900 KB FRAME message, which can easily write only part of the
/// message when the socket buffer is smaller than the frame — never risks a partial write that
/// would corrupt the byte-oriented framing for later messages.
pub async fn serve(
    listen: SocketAddr,
    mut source: impl Source + 'static,
    control_tx: mpsc::Sender<Control>,
) -> anyhow::Result<()> {
    let listener = TcpListener::bind(listen).await.with_context(|| format!("bind {listen}"))?;
    let (stream, _peer) = listener.accept().await.context("accept")?;
    let (mut rd, mut wr) = stream.into_split();

    // 1. Read (and ignore) the client's HELLO.
    wire::read_msg(&mut rd).await.context("read HELLO")?;

    // 2. Send STREAM_INFO from the source.
    let info = source.info();
    let stream_info =
        Msg::StreamInfo { w: info.w, h: info.h, pixfmt: info.pixfmt, fps: info.fps, title: info.title };
    wr.write_all(&wire::encode(&stream_info)).await.context("write STREAM_INFO")?;

    // 3+4. Frame production/writing and CONTROL forwarding run concurrently on this one task
    // (no `tokio::spawn` — see the module doc); whichever ends first (client disconnected on
    // either half) ends the session.
    // Whichever branch notices the client disconnect (either half) simply ends its own loop and
    // returns/breaks — none of them propagate the resulting I/O error through `?`, so an ordinary
    // disconnect always ends `serve()` with `Ok(())` (see each function's own doc comment). Only
    // the bind/accept/HELLO/STREAM_INFO setup above this point still propagates as a genuine error.
    // Audio (if the source has any) streams alongside video over the same socket.
    let audio_stdout = source.take_audio();
    let (frame_tx, frame_rx) = watch::channel::<FrameSlot>(None);
    let (audio_tx, audio_rx) = mpsc::channel::<Vec<u8>>(64);
    tokio::select! {
        _ = produce_frames(source, frame_tx) => {}
        _ = produce_audio(audio_stdout, audio_tx) => {}
        _ = write_media(wr, frame_rx, audio_rx) => {}
        _ = forward_control(rd, control_tx) => {}
    }
    Ok(())
}

/// Reads S16LE PCM chunks from the source's audio stream and forwards them to `tx` for the writer to
/// send as AUDIO messages. When the source has no audio, or the audio stream ends/fails, this does NOT
/// end the session (video plays on) — it parks forever, holding `tx` open. The bounded `tx` gives
/// backpressure so the (`-re`-paced) audio ffmpeg can't outrun a slow socket.
async fn produce_audio(audio: Option<ChildStdout>, tx: mpsc::Sender<Vec<u8>>) {
    if let Some(mut stdout) = audio {
        let mut buf = vec![0u8; 8192];
        loop {
            match stdout.read(&mut buf).await {
                Ok(0) | Err(_) => break, // audio ended/failed — stop audio, keep the session going
                Ok(n) => {
                    if tx.send(buf[..n].to_vec()).await.is_err() {
                        break; // writer side gone (client disconnected)
                    }
                }
            }
        }
    }
    std::future::pending::<()>().await; // absence/EOF of audio must never end the session
}

/// Pulls frames from `source` forever, publishing each to `tx`. Never blocks on the socket —
/// `tx.send` only overwrites the watched value and notifies waiters (see `serve` docs). Each frame
/// is wrapped in an `Arc` once here so `write_frames` can clone it out of the `watch::Ref` as an
/// O(1) refcount bump instead of an ~900 KB copy.
async fn produce_frames(mut source: impl Source, tx: watch::Sender<FrameSlot>) {
    while let Some(frame) = source.next_frame().await {
        if tx.send(Some(Arc::new(frame))).is_err() {
            break; // writer side gone (client disconnected) — nothing left to feed.
        }
    }
}

/// Writes video FRAMEs (latest-wins, from `frame_rx`) and audio AUDIO chunks (queued, from `audio_rx`)
/// to the one client socket, whichever is ready. Video keeps its latest-wins behaviour (only the newest
/// frame is written when several land during a slow write); audio is a bounded queue so samples aren't
/// dropped. Ends the session (returns) on any write error or when the video producer is gone — never
/// propagating the error, so an ordinary disconnect ends `serve()` with `Ok` (matching `forward_control`).
async fn write_media(
    mut wr: OwnedWriteHalf,
    mut frame_rx: watch::Receiver<FrameSlot>,
    mut audio_rx: mpsc::Receiver<Vec<u8>>,
) {
    loop {
        tokio::select! {
            changed = frame_rx.changed() => {
                if changed.is_err() {
                    break; // video producer gone — session over
                }
                let frame = frame_rx.borrow_and_update().clone(); // Arc clone: O(1) refcount bump.
                if let Some(p) = frame {
                    if let Err(e) = wr.write_all(&wire::encode_frame(p.0, &p.1)).await {
                        eprintln!("[server] client disconnected (write FRAME: {e}) — ending session");
                        break;
                    }
                }
            }
            chunk = audio_rx.recv() => {
                // `produce_audio` holds its sender open forever, so `None` never actually arrives.
                if let Some(pcm) = chunk {
                    if wr.write_all(&wire::encode_audio(&pcm)).await.is_err() {
                        break; // client gone
                    }
                }
            }
        }
    }
}

/// Reads messages from `rd` and forwards any CONTROL to `tx`, until the client disconnects (or
/// sends something unparsable), at which point this returns.
async fn forward_control(mut rd: OwnedReadHalf, tx: mpsc::Sender<Control>) {
    loop {
        match wire::read_msg(&mut rd).await {
            Ok(Msg::Control(c)) => {
                if tx.send(c).await.is_err() {
                    break; // receiver dropped
                }
            }
            Ok(_other) => {} // the plugin only ever sends HELLO/CONTROL; ignore anything else
            Err(_) => break, // disconnected or malformed — end the session
        }
    }
}
