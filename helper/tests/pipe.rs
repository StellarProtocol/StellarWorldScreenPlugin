//! Integration test: starts the real frame server on an ephemeral port and drives it as a real
//! plugin client would — connect, send HELLO, read STREAM_INFO, read one correctly-sized FRAME.
//!
//! Runs the server and the client concurrently on the SAME task via `tokio::select!` rather than
//! `tokio::spawn` — `Source::next_frame` is a native `async fn` in a trait, and spawning a future
//! that owns a generic `Source` would require the compiler to prove that trait method's returned
//! future is `Send` polymorphically, which isn't expressible without object-safety/RTN gymnastics
//! `select!`/`block_on`-style local polling need no such bound, so this sidesteps the issue.

use stellar_castbox::server;
use stellar_castbox::source::testpattern::TestPattern;
use stellar_castbox::wire::{self, Msg};
use tokio::io::AsyncWriteExt;
use tokio::net::{TcpListener, TcpStream};

#[tokio::test]
async fn stream_info_then_correctly_sized_frame() {
    let listen = free_local_addr().await;
    let source = TestPattern::new(640, 360, 30);
    let (control_tx, _control_rx) = tokio::sync::mpsc::channel(1);

    let server_fut = server::serve(listen, source, control_tx);
    let client_fut = async {
        let mut client = connect_retrying(listen).await;

        let hello = wire::encode(&Msg::Hello { proto: 1, flags: 0 });
        client.write_all(&hello).await.unwrap();

        match wire::read_msg(&mut client).await.unwrap() {
            Msg::StreamInfo { w, h, pixfmt, .. } => {
                assert_eq!(w, 640);
                assert_eq!(h, 360);
                assert_eq!(pixfmt, 0);
            }
            other => panic!("expected StreamInfo, got {other:?}"),
        }

        match wire::read_msg(&mut client).await.unwrap() {
            Msg::Frame { bytes, .. } => assert_eq!(bytes.len(), 640 * 360 * 4),
            other => panic!("expected Frame, got {other:?}"),
        }
        // `client` drops here, closing the socket — that's enough to let the test conclude;
        // `select!` below cancels `server_fut` once this branch completes.
    };

    tokio::select! {
        _ = server_fut => panic!("server exited before the client finished its checks"),
        _ = client_fut => {}
    }
}

/// Binds an OS-assigned ephemeral port on 127.0.0.1 and returns its address, then drops the
/// listener so `serve` can bind it itself (required since `serve`'s signature takes a `SocketAddr`
/// and does its own bind). Small unavoidable race with something else grabbing the port between
/// the drop and `serve`'s bind — acceptable for a local single-process test.
async fn free_local_addr() -> std::net::SocketAddr {
    let probe = TcpListener::bind("127.0.0.1:0").await.unwrap();
    probe.local_addr().unwrap()
}

/// Connects to `addr`, retrying briefly — `serve`'s listener may not have bound yet.
async fn connect_retrying(addr: std::net::SocketAddr) -> TcpStream {
    for _ in 0..50 {
        if let Ok(s) = TcpStream::connect(addr).await {
            return s;
        }
        tokio::time::sleep(std::time::Duration::from_millis(20)).await;
    }
    panic!("could not connect to {addr}");
}

/// Pins the disconnect contract: an ordinary client disconnect (the plugin closing its socket) must
/// make `serve()` return `Ok(())`, not propagate the resulting broken-pipe/reset error as an `Err`
/// — that error would otherwise surface through `main.rs`'s `anyhow::Result<()>` as a printed error
/// and a non-zero exit for what is just the plugin window closing.
///
/// `serve()`'s three concurrent branches (`produce_frames`/`write_frames`/`forward_control`) race to
/// notice a disconnect: whichever one runs the still-pending `forward_control` read of a client that
/// simply drops with nothing unread wins near-instantly on a clean FIN, before `write_frames` ever
/// gets a chance to hit a write error — which would mask a regression in `write_frames`'s own error
/// handling. So each trial here holds frames unread in the client's kernel receive buffer before
/// dropping, which turns the close into an abortive RST (standard BSD-socket behavior, no special
/// socket option needed) and lets `write_frames`'s in-flight `write_all` race to notice the
/// disconnect too. This makes any one trial an honest (if not 100%-guaranteed) chance of exercising
/// the write path, so the loop below repeats it — before the fix this reliably surfaces at least one
/// `Err` (see the fix-pass report for observed repro rates); after the fix every branch swallows the
/// disconnect, so every trial is `Ok(())` regardless of which branch wins the race.
#[tokio::test]
async fn serve_returns_ok_on_ordinary_client_disconnect() {
    const TRIALS: usize = 8;
    for trial in 0..TRIALS {
        let result = disconnect_trial().await;
        assert!(
            result.is_ok(),
            "serve() must return Ok(()) on an ordinary client disconnect (trial {trial}/{TRIALS}), got {result:?}"
        );
    }
}

/// Runs one connect → read STREAM_INFO → let frames pile up unread → disconnect → observe `serve()`
/// trial, returning whatever `serve()`'s own future resolved to.
///
/// Runs `serve()` on a real `tokio::spawn` (rather than the `select!` trick the test above uses) so
/// this observes exactly what `main.rs` observes: the `Result` the `serve()` future itself resolves
/// to.
async fn disconnect_trial() -> anyhow::Result<()> {
    let listen = free_local_addr().await;
    let source = TestPattern::new(640, 360, 30);
    let (control_tx, _control_rx) = tokio::sync::mpsc::channel(1);

    let handle = tokio::spawn(server::serve(listen, source, control_tx));

    let mut client = connect_retrying(listen).await;
    let hello = wire::encode(&Msg::Hello { proto: 1, flags: 0 });
    client.write_all(&hello).await.unwrap();
    match wire::read_msg(&mut client).await.unwrap() {
        Msg::StreamInfo { .. } => {}
        other => panic!("expected StreamInfo, got {other:?}"),
    }

    // Let the source produce (and the server write) several ~900 KB FRAMEs into the socket that the
    // client never reads, then disconnect without draining any of them.
    tokio::time::sleep(std::time::Duration::from_millis(200)).await;
    drop(client);

    tokio::time::timeout(std::time::Duration::from_secs(2), handle)
        .await
        .expect("serve() did not complete within 2s of the client disconnecting")
        .expect("serve() task panicked")
}
