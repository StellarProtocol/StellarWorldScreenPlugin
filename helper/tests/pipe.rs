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
