//! stellar-castbox — video helper for StellarWorldScreen.
//!
//! Streams raw video frames to the in-game plugin over localhost TCP (see `../docs/protocol.md`).
//! Milestone A: a generated test-pattern source, no real decoder yet. Module tree (`wire`, `source`,
//! `server`) lives in `lib.rs` — see its doc comment for why (the `tests/pipe.rs` integration test
//! needs to reach these as a library, not as private binary modules).

use std::net::SocketAddr;

use stellar_castbox::server;
use stellar_castbox::source::testpattern::TestPattern;
use stellar_castbox::wire::Control;

const DEFAULT_LISTEN: &str = "127.0.0.1:47800";
const DEFAULT_W: u16 = 640;
const DEFAULT_H: u16 = 360;
const DEFAULT_FPS: u8 = 30;

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    let args = Args::parse(std::env::args().skip(1));
    eprintln!("stellar-castbox {} — listening on {} (source: {})", env!("CARGO_PKG_VERSION"), args.listen, args.source);

    let (control_tx, mut control_rx) = tokio::sync::mpsc::channel::<Control>(16);
    tokio::spawn(async move {
        while let Some(c) = control_rx.recv().await {
            eprintln!("[control] {c:?}");
        }
    });

    match args.source.as_str() {
        "testpattern" => {
            let source = TestPattern::new(DEFAULT_W, DEFAULT_H, DEFAULT_FPS);
            server::serve(args.listen, source, control_tx).await
        }
        other => anyhow::bail!("unknown --source '{other}' (expected: testpattern)"),
    }
}

/// Minimal hand-rolled CLI (`--listen ADDR`, `--source NAME`) — not worth an extra dependency for
/// two flags.
struct Args {
    listen: SocketAddr,
    source: String,
}

impl Args {
    fn parse(args: impl Iterator<Item = String>) -> Self {
        let mut listen: SocketAddr = DEFAULT_LISTEN.parse().expect("default listen addr is valid");
        let mut source = "testpattern".to_string();
        let mut it = args;
        while let Some(arg) = it.next() {
            match arg.as_str() {
                "--listen" => {
                    let v = it.next().expect("--listen requires an address");
                    listen = v.parse().unwrap_or_else(|e| panic!("invalid --listen address '{v}': {e}"));
                }
                "--source" => {
                    source = it.next().expect("--source requires a name");
                }
                other => eprintln!("warning: ignoring unknown argument '{other}'"),
            }
        }
        Self { listen, source }
    }
}
