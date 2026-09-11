//! stellar-castbox — video helper for StellarWorldScreen.
//!
//! Streams raw video frames to the in-game plugin over localhost TCP (see `../docs/protocol.md`).
//! Milestone A: a generated test-pattern source, no real decoder yet. Module tree (`wire`, `source`,
//! `server`) lives in `lib.rs` — see its doc comment for why (the `tests/pipe.rs` integration test
//! needs to reach these as a library, not as private binary modules).

use std::net::SocketAddr;
use std::path::PathBuf;

use anyhow::Context;
use stellar_castbox::server;
use stellar_castbox::source::ffmpeg::{FfmpegInput, FfmpegSource};
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
        other if other.starts_with("file:") => {
            let path = other["file:".len()..].to_string();
            let ffmpeg_path = resolve_ffmpeg_path(args.ffmpeg.as_deref())?;
            let source =
                FfmpegSource::spawn(FfmpegInput::File(path), DEFAULT_W, DEFAULT_H, DEFAULT_FPS, ffmpeg_path)?;
            server::serve(args.listen, source, control_tx).await
        }
        other if other.starts_with("url:") => {
            let url = other["url:".len()..].to_string();
            let ffmpeg_path = resolve_ffmpeg_path(args.ffmpeg.as_deref())?;
            let source =
                FfmpegSource::spawn(FfmpegInput::Url(url), DEFAULT_W, DEFAULT_H, DEFAULT_FPS, ffmpeg_path)?;
            server::serve(args.listen, source, control_tx).await
        }
        other => anyhow::bail!("unknown --source '{other}' (expected: testpattern, file:<path>, url:<u>)"),
    }
}

/// Resolves the `ffmpeg.exe` to spawn: `--ffmpeg <path>` if given, else `stellar-castbox.exe`'s own
/// sibling `ffmpeg.exe` (the bundled layout). Errors clearly (rather than letting `spawn` fail with
/// an opaque "not found") when the resolved path doesn't exist, so a missing bundle is obvious.
fn resolve_ffmpeg_path(override_path: Option<&str>) -> anyhow::Result<PathBuf> {
    let path = match override_path {
        Some(p) => PathBuf::from(p),
        None => std::env::current_exe()
            .context("resolve current exe path")?
            .parent()
            .context("current exe has no parent directory")?
            .join("ffmpeg.exe"),
    };
    if !path.exists() {
        anyhow::bail!(
            "ffmpeg not found at {} (bundle is missing ffmpeg.exe next to stellar-castbox.exe, \
             or pass --ffmpeg <path>)",
            path.display()
        );
    }
    Ok(path)
}

/// Minimal hand-rolled CLI (`--listen ADDR`, `--source NAME`, `--ffmpeg PATH`) — not worth an extra
/// dependency for three flags.
struct Args {
    listen: SocketAddr,
    source: String,
    ffmpeg: Option<String>,
}

impl Args {
    fn parse(args: impl Iterator<Item = String>) -> Self {
        let mut listen: SocketAddr = DEFAULT_LISTEN.parse().expect("default listen addr is valid");
        let mut source = "testpattern".to_string();
        let mut ffmpeg = None;
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
                "--ffmpeg" => {
                    ffmpeg = Some(it.next().expect("--ffmpeg requires a path"));
                }
                other => eprintln!("warning: ignoring unknown argument '{other}'"),
            }
        }
        Self { listen, source, ffmpeg }
    }
}
