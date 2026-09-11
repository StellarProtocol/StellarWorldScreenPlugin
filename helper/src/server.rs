//! TCP frame server — accepts one client, sends STREAM_INFO once, then streams FRAMEs from a
//! `Source` while forwarding any CONTROL messages the client sends back to `control_tx`.
//!
//! Milestone A: a single client, no re-accept on disconnect (a later task adds reconnect).

use std::net::SocketAddr;

use anyhow::Context;
use tokio::io::AsyncWriteExt;
use tokio::net::tcp::{OwnedReadHalf, OwnedWriteHalf};
use tokio::net::TcpListener;
use tokio::sync::{mpsc, watch};

use crate::source::Source;
use crate::wire::{self, Control, Msg};

/// The one in-flight frame slot a `watch` channel carries: `None` until the producer emits its
/// first frame.
type FrameSlot = Option<(u64, Vec<u8>)>;

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
    source: impl Source + 'static,
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
    let (frame_tx, frame_rx) = watch::channel::<FrameSlot>(None);
    tokio::select! {
        _ = produce_frames(source, frame_tx) => {}
        res = write_frames(wr, frame_rx) => { res?; }
        _ = forward_control(rd, control_tx) => {}
    }
    Ok(())
}

/// Pulls frames from `source` forever, publishing each to `tx`. Never blocks on the socket —
/// `tx.send` only overwrites the watched value and notifies waiters (see `serve` docs).
async fn produce_frames(mut source: impl Source, tx: watch::Sender<FrameSlot>) {
    while let Some(frame) = source.next_frame().await {
        if tx.send(Some(frame)).is_err() {
            break; // writer side gone (client disconnected) — nothing left to feed.
        }
    }
}

/// Writes each newest frame reported by `rx` to `wr`. If several frames land in `rx` while a write
/// is still in flight, only the latest is seen on the next iteration — the rest are dropped.
async fn write_frames(mut wr: OwnedWriteHalf, mut rx: watch::Receiver<FrameSlot>) -> anyhow::Result<()> {
    loop {
        rx.changed().await.context("frame channel closed")?;
        let frame = rx.borrow_and_update().clone();
        if let Some((pts_ms, bytes)) = frame {
            let framed = wire::encode(&Msg::Frame { pts_ms, bytes });
            wr.write_all(&framed).await.context("write FRAME")?;
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
