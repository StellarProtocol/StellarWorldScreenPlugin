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
use stellar_castbox::source::resolve::{
    is_direct_media_url, resolve_via_ytdlp, scheme_and_host, video_selector, YTDLP_AUDIO_SELECTOR,
};
use stellar_castbox::source::testpattern::TestPattern;
use stellar_castbox::wire::Control;

const DEFAULT_LISTEN: &str = "127.0.0.1:47800";
const DEFAULT_W: u16 = 640;
const DEFAULT_H: u16 = 360;
const DEFAULT_FPS: u8 = 30;

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    let args = Args::parse(std::env::args().skip(1));
    eprintln!("stellar-castbox {} — listening on {} (source: {})", env!("CARGO_PKG_VERSION"), args.listen, redact_source(&args.source));

    // Playback gain in [0,1] (f32 bits), shared with the audio pump. The plugin drives it via CONTROL
    // Volume from the player's in-world distance to the screen (the game's audio is Wwise, so Unity's own
    // AudioSource is silent — attenuation is applied here, to the PCM we play through ffplay).
    let gain = std::sync::Arc::new(std::sync::atomic::AtomicU32::new(1.0f32.to_bits()));
    let gain_ctl = std::sync::Arc::clone(&gain);
    let (control_tx, mut control_rx) = tokio::sync::mpsc::channel::<Control>(16);
    tokio::spawn(async move {
        while let Some(c) = control_rx.recv().await {
            if let Control::Volume(v) = c {
                let g = (v as f32 / 100.0).clamp(0.0, 1.0);
                gain_ctl.store(g.to_bits(), std::sync::atomic::Ordering::Relaxed);
            }
            eprintln!("[control] {c:?}");
        }
    });

    match args.source.as_str() {
        "testpattern" => {
            let source = TestPattern::new(args.width, args.height, DEFAULT_FPS);
            server::serve(args.listen, source, control_tx).await
        }
        other if other.starts_with("file:") => {
            let path = other["file:".len()..].to_string();
            let ffmpeg_path = resolve_ffmpeg_path(args.ffmpeg.as_deref())?;
            // A file carries its own audio, decoded by a second ffmpeg from the same file.
            let audio_out = Some((ffmpeg_path.clone(), path.clone()));
            let source = FfmpegSource::spawn(
                FfmpegInput::File(path), args.width, args.height, DEFAULT_FPS, ffmpeg_path, audio_out, gain,
            )?;
            server::serve(args.listen, source, control_tx).await
        }
        other if other.starts_with("url:") => {
            let raw_url = other["url:".len()..].to_string();
            let ffmpeg_path = resolve_ffmpeg_path(args.ffmpeg.as_deref())?;
            let (video_url, audio_input) = if is_direct_media_url(&raw_url) {
                // a direct media URL (mp4/HLS) carries its own audio — ffmpeg + ffplay both use it.
                (raw_url.clone(), Some(raw_url))
            } else {
                // page URL: resolve a low-res H.264 VIDEO stream for ffmpeg and, separately, the best
                // AUDIO stream for ffplay (many YouTube videos have no combined format).
                let ytdlp_path = resolve_ytdlp_path(args.ytdlp.as_deref())?;
                let video = resolve_via_ytdlp(&raw_url, &ytdlp_path, &video_selector(args.height)).await?;
                let audio = resolve_via_ytdlp(&raw_url, &ytdlp_path, YTDLP_AUDIO_SELECTOR).await.ok();
                eprintln!(
                    "[resolve] yt-dlp {} -> video {} ({})",
                    scheme_and_host(&raw_url),
                    scheme_and_host(&video),
                    if audio.is_some() { "with audio" } else { "no audio" }
                );
                (video, audio)
            };
            let audio_out = audio_input.map(|inp| (ffmpeg_path.clone(), inp));
            let source = FfmpegSource::spawn(
                FfmpegInput::Url(video_url),
                args.width,
                args.height,
                DEFAULT_FPS,
                ffmpeg_path,
                audio_out,
                gain,
            )?;
            server::serve(args.listen, source, control_tx).await
        }
        other => anyhow::bail!("unknown --source '{other}' (expected: testpattern, file:<path>, url:<u>)"),
    }
}

/// Redacts a `--source` value for the startup banner: a `url:` source can carry an auth token in its
/// query/path (B2 review), so we log only its scheme+host. `file:`/`testpattern` are logged as-is.
fn redact_source(source: &str) -> String {
    match source.strip_prefix("url:") {
        Some(rest) => format!("url:{}", scheme_and_host(rest)),
        None => source.to_string(),
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

/// Resolves the `yt-dlp.exe` to run when a `url:` source isn't a direct media URL (per
/// `is_direct_media_url`): `--ytdlp <path>` if given, else `stellar-castbox.exe`'s own sibling
/// `yt-dlp.exe` (the bundled layout). Only called when a resolve is actually needed, so a direct
/// `url:`/`file:` source never requires yt-dlp to be present. Errors clearly when the resolved path
/// doesn't exist, so a missing bundle is obvious rather than surfacing as an opaque spawn failure.
fn resolve_ytdlp_path(override_path: Option<&str>) -> anyhow::Result<PathBuf> {
    let path = match override_path {
        Some(p) => PathBuf::from(p),
        None => std::env::current_exe()
            .context("resolve current exe path")?
            .parent()
            .context("current exe has no parent directory")?
            .join("yt-dlp.exe"),
    };
    if !path.exists() {
        anyhow::bail!(
            "yt-dlp not found at {} (bundle is missing yt-dlp.exe next to stellar-castbox.exe, \
             or pass --ytdlp <path>) — required to resolve a non-direct url: source",
            path.display()
        );
    }
    Ok(path)
}

/// Minimal hand-rolled CLI (`--listen ADDR`, `--source NAME`, `--ffmpeg/--ytdlp/--ffplay PATH`) —
/// not worth an extra dependency for a handful of flags.
struct Args {
    listen: SocketAddr,
    source: String,
    width: u16,
    height: u16,
    ffmpeg: Option<String>,
    ytdlp: Option<String>,
}

impl Args {
    fn parse(args: impl Iterator<Item = String>) -> Self {
        let mut listen: SocketAddr = DEFAULT_LISTEN.parse().expect("default listen addr is valid");
        let mut source = "testpattern".to_string();
        let mut width = DEFAULT_W;
        let mut height = DEFAULT_H;
        let mut ffmpeg = None;
        let mut ytdlp = None;
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
                "--width" => {
                    let v = it.next().expect("--width requires a number");
                    width = v.parse().unwrap_or_else(|e| panic!("invalid --width '{v}': {e}"));
                }
                "--height" => {
                    let v = it.next().expect("--height requires a number");
                    height = v.parse().unwrap_or_else(|e| panic!("invalid --height '{v}': {e}"));
                }
                "--ffmpeg" => {
                    ffmpeg = Some(it.next().expect("--ffmpeg requires a path"));
                }
                "--ytdlp" => {
                    ytdlp = Some(it.next().expect("--ytdlp requires a path"));
                }
                other => eprintln!("warning: ignoring unknown argument '{other}'"),
            }
        }
        Self { listen, source, width, height, ffmpeg, ytdlp }
    }
}
