//! Real-video frame source: spawns the bundled `ffmpeg.exe` to decode a file/URL into raw RGBA
//! frames and feeds them through the same `Source`/server/wire pipe `TestPattern` uses. The decode
//! command is proven working under Wine — this module wraps the spawn + the frame-reading loop.

use super::{Source, StreamInfo};
use anyhow::Context;
use std::path::PathBuf;
use std::process::Stdio;
use tokio::io::{AsyncRead, AsyncReadExt};
use tokio::process::{Child, ChildStdout, Command};

/// Reads exactly one `w*h*4`-byte RGBA frame from `r` into `buf` (resized to that length; the same
/// `Vec` is reused call over call, so only the caller's own `.clone()` of a full frame allocates).
///
/// Uses `read` in a loop rather than `read_exact`, which errors on any EOF (whether clean — nothing
/// read yet — or mid-frame) and doesn't let a caller cleanly tell the two apart. Here both cases are
/// treated identically: `Ok(false)`, with a mid-frame (torn) chunk's bytes dropped rather than
/// emitted as a short frame. `Ok(true)` means `buf` holds a genuine full frame.
pub async fn read_rgba_frame<R: AsyncRead + Unpin>(
    r: &mut R,
    w: u16,
    h: u16,
    buf: &mut Vec<u8>,
) -> std::io::Result<bool> {
    let frame_len = w as usize * h as usize * 4;
    buf.resize(frame_len, 0);
    let mut filled = 0usize;
    while filled < frame_len {
        let n = r.read(&mut buf[filled..]).await?;
        if n == 0 {
            // EOF: either clean (filled == 0) or a torn trailing chunk (0 < filled < frame_len).
            // Both mean "no full frame here" — the caller must not emit a short frame.
            return Ok(false);
        }
        filled += n;
    }
    Ok(true)
}

/// What `FfmpegSource` decodes. Both variants feed ffmpeg's `-i <value>` identically — the enum
/// exists purely so `derive_title` (and callers) can tell a local file apart from a remote URL.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum FfmpegInput {
    File(String),
    Url(String),
}

/// Longest title `derive_title` will produce, so a pathological path/URL segment can't make
/// `StreamInfo.title` unreasonably large.
const MAX_TITLE_LEN: usize = 80;

/// A short human-readable label for `StreamInfo.title`: the file's base name (`File`) or the URL's
/// last path segment with any query string stripped (`Url`), truncated to `MAX_TITLE_LEN` chars.
fn derive_title(input: &FfmpegInput) -> String {
    let raw = match input {
        FfmpegInput::File(path) => std::path::Path::new(path)
            .file_name()
            .map(|s| s.to_string_lossy().into_owned())
            .unwrap_or_else(|| path.clone()),
        FfmpegInput::Url(url) => {
            let segment = url.rsplit('/').find(|s| !s.is_empty()).unwrap_or(url.as_str());
            segment.split('?').next().unwrap_or(segment).to_string()
        }
    };
    truncate_title(&raw)
}

/// Truncates `s` to at most `MAX_TITLE_LEN` `char`s (not bytes, to stay UTF-8-safe).
fn truncate_title(s: &str) -> String {
    if s.chars().count() <= MAX_TITLE_LEN {
        s.to_string()
    } else {
        s.chars().take(MAX_TITLE_LEN).collect()
    }
}

/// Decodes a file/URL into raw RGBA frames by spawning `ffmpeg.exe` and reading its stdout, same
/// `Source` contract `TestPattern` implements.
pub struct FfmpegSource {
    /// Kept alive purely so the spawned process is killed when this source is dropped
    /// (`Command::kill_on_drop(true)` acts on `Child`'s own `Drop`) — never read again after spawn.
    #[allow(dead_code)]
    child: Child,
    stdout: ChildStdout,
    w: u16,
    h: u16,
    fps: u8,
    title: String,
    pts_ms: u64,
    buf: Vec<u8>,
    /// Audio-decode ffmpeg process (input's audio → PCM); kept alive for `kill_on_drop`. Never read.
    #[allow(dead_code)]
    audio_child: Option<Child>,
    /// The audio ffmpeg's stdout (S16LE 48kHz stereo PCM); taken once by the server via `take_audio`.
    audio_stdout: Option<ChildStdout>,
}

impl FfmpegSource {
    /// Spawns `ffmpeg_path` to decode `input` at `w`x`h`/`fps`, piping raw RGBA frames on stdout:
    /// `ffmpeg.exe -hide_banner -loglevel error -re -i <INPUT> -an -vf scale=W:H,fps=FPS -f rawvideo
    /// -pix_fmt rgba pipe:1`. `-re` paces ffmpeg's own output at realtime rate, so the blocking
    /// stdout read in `next_frame` paces playback with no manual sleep needed.
    pub fn spawn(
        input: FfmpegInput,
        w: u16,
        h: u16,
        fps: u8,
        ffmpeg_path: PathBuf,
        // (ffplay path, audio input) — the input ffplay plays for sound. For a file it's the file path;
        // for a URL it's a SEPARATELY-resolved audio-stream URL (many YouTube videos have no combined
        // format, so the video URL is silent). None → silent video.
        audio_out: Option<(PathBuf, String)>,
    ) -> anyhow::Result<Self> {
        let title = derive_title(&input);
        let input_value: &str = match &input {
            FfmpegInput::File(p) => p,
            FfmpegInput::Url(u) => u,
        };
        let vf = format!("scale={w}:{h},fps={fps}");

        let mut cmd = Command::new(&ffmpeg_path);
        cmd.arg("-hide_banner").arg("-loglevel").arg("error");
        if matches!(input, FfmpegInput::File(_)) {
            // Loop local files forever so the screen never goes dark when a clip ends: without this,
            // EOF ends the source → the helper exits → the plugin reconnects to a dead helper.
            cmd.arg("-stream_loop").arg("-1");
        }
        cmd.arg("-re")
            .arg("-i")
            .arg(input_value)
            .arg("-an")
            .arg("-vf")
            .arg(&vf)
            .arg("-f")
            .arg("rawvideo")
            .arg("-pix_fmt")
            .arg("rgba")
            .arg("pipe:1")
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .kill_on_drop(true);

        let mut child = cmd
            .spawn()
            .with_context(|| format!("spawn ffmpeg at {}", ffmpeg_path.display()))?;
        let stdout = child.stdout.take().context("ffmpeg child produced no stdout pipe")?;

        let is_file = matches!(input, FfmpegInput::File(_));
        let (audio_child, audio_stdout) = match audio_out {
            Some((ffmpeg, inp)) => match spawn_audio_ffmpeg(ffmpeg, &inp, is_file) {
                Some((c, so)) => (Some(c), Some(so)),
                None => (None, None),
            },
            None => (None, None),
        };

        Ok(Self {
            child,
            stdout,
            w,
            h,
            fps,
            title,
            pts_ms: 0,
            buf: vec![0u8; w as usize * h as usize * 4],
            audio_child,
            audio_stdout,
        })
    }
}

/// Spawns an ffmpeg that decodes the input's audio to raw interleaved S16LE PCM at 48000 Hz, 2 channels
/// on stdout, for the server to stream to the plugin as AUDIO messages (played by a 3D AudioSource in
/// Unity — so the sound is spatialised by the player's distance from the screen). `-re` paces it at
/// realtime so it stays in step with the `-re`-paced video; local files loop (`-stream_loop -1`) to
/// match the looping video. Best-effort: returns `None` on spawn failure (video plays silent).
fn spawn_audio_ffmpeg(ffmpeg_path: PathBuf, input: &str, is_file: bool) -> Option<(Child, ChildStdout)> {
    let mut cmd = Command::new(&ffmpeg_path);
    cmd.arg("-hide_banner").arg("-loglevel").arg("error");
    if is_file {
        cmd.arg("-stream_loop").arg("-1");
    }
    cmd.arg("-re")
        .arg("-i")
        .arg(input)
        .arg("-vn")
        .arg("-f")
        .arg("s16le")
        .arg("-ar")
        .arg("48000")
        .arg("-ac")
        .arg("2")
        .arg("pipe:1")
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .kill_on_drop(true);
    match cmd.spawn() {
        Ok(mut child) => {
            let stdout = child.stdout.take()?;
            Some((child, stdout))
        }
        Err(e) => {
            eprintln!("[ffmpeg source] audio decode failed to start: {e}");
            None
        }
    }
}

impl Source for FfmpegSource {
    fn info(&self) -> StreamInfo {
        StreamInfo { w: self.w, h: self.h, pixfmt: 0, fps: self.fps, title: self.title.clone() }
    }

    fn take_audio(&mut self) -> Option<ChildStdout> {
        self.audio_stdout.take()
    }

    async fn next_frame(&mut self) -> Option<(u64, Vec<u8>)> {
        let got_full_frame = match read_rgba_frame(&mut self.stdout, self.w, self.h, &mut self.buf).await {
            Ok(v) => v,
            Err(e) => {
                // Distinguish "something broke" from a clean finish — stderr is nulled, so this is
                // the only signal ffmpeg failed rather than the video simply ending (parity with
                // server.rs's disconnect logging).
                eprintln!("[ffmpeg source] read error, ending stream: {e}");
                return None;
            }
        };
        if !got_full_frame {
            return None; // clean EOF or a torn trailing chunk — video ended either way.
        }
        let pts = self.pts_ms;
        self.pts_ms += 1000 / self.fps as u64;
        Some((pts, self.buf.clone()))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn read_rgba_frame_three_full_frames_then_clean_eof() {
        let (w, h) = (2u16, 2u16);
        let frame_len = 2usize * 2 * 4; // 16 bytes/frame
        let mut data = Vec::new();
        data.extend(std::iter::repeat(1u8).take(frame_len));
        data.extend(std::iter::repeat(2u8).take(frame_len));
        data.extend(std::iter::repeat(3u8).take(frame_len));
        let mut cur = std::io::Cursor::new(data);
        let mut buf = Vec::new();

        assert!(read_rgba_frame(&mut cur, w, h, &mut buf).await.unwrap());
        assert!(buf.iter().all(|&b| b == 1), "frame 1 must be all 1s");

        assert!(read_rgba_frame(&mut cur, w, h, &mut buf).await.unwrap());
        assert!(buf.iter().all(|&b| b == 2), "frame 2 must be all 2s");

        assert!(read_rgba_frame(&mut cur, w, h, &mut buf).await.unwrap());
        assert!(buf.iter().all(|&b| b == 3), "frame 3 must be all 3s");

        assert!(
            !read_rgba_frame(&mut cur, w, h, &mut buf).await.unwrap(),
            "clean EOF at a frame boundary must report false, not error"
        );
        assert!(
            buf.iter().all(|&b| b == 3),
            "buf is reused: after a clean-EOF call it must still hold the last full frame"
        );
    }

    #[tokio::test]
    async fn read_rgba_frame_torn_trailing_chunk_is_dropped_not_emitted() {
        let (w, h) = (2u16, 2u16);
        let frame_len = 2usize * 2 * 4; // 16 bytes/frame
        let mut data = Vec::new();
        data.extend(std::iter::repeat(7u8).take(frame_len));
        data.extend(std::iter::repeat(9u8).take(frame_len));
        data.extend(std::iter::repeat(0xAAu8).take(5)); // torn trailing chunk, < one frame
        let mut cur = std::io::Cursor::new(data);
        let mut buf = Vec::new();

        assert!(read_rgba_frame(&mut cur, w, h, &mut buf).await.unwrap());
        assert!(buf.iter().all(|&b| b == 7), "frame 1 must be all 7s");

        assert!(read_rgba_frame(&mut cur, w, h, &mut buf).await.unwrap());
        assert!(buf.iter().all(|&b| b == 9), "frame 2 must be all 9s");

        assert!(
            !read_rgba_frame(&mut cur, w, h, &mut buf).await.unwrap(),
            "a torn (partial) trailing chunk must report false, not be emitted as a short frame"
        );
    }

    #[test]
    fn derive_title_file_uses_the_base_file_name() {
        let title = derive_title(&FfmpegInput::File("/videos/clips/intro.mp4".into()));
        assert_eq!(title, "intro.mp4");
    }

    #[test]
    fn derive_title_url_uses_the_last_path_segment_without_query() {
        let title = derive_title(&FfmpegInput::Url(
            "https://cdn.example.com/streams/clip.mp4?token=abc123".into(),
        ));
        assert_eq!(title, "clip.mp4");
    }
}

/// Spawn-level coverage substituting a FAKE ffmpeg (a tiny shell script that ignores its argv and
/// just emits N frames of zero bytes to stdout, then exits) for the real `ffmpeg.exe`, to prove
/// `FfmpegSource` wires a spawned child's stdout through `read_rgba_frame` end-to-end and ends the
/// stream on the child's EOF — the real `ffmpeg.exe` spawn itself is a Windows PE verified
/// out-of-band (Wine + in-game), not here. Unix-only (`sh` script + file permissions); never
/// compiled into the release build (only under `#[cfg(test)]`).
#[cfg(all(test, unix))]
mod fake_ffmpeg_tests {
    use super::*;
    use std::io::Write;
    use std::os::unix::fs::PermissionsExt;

    /// Writes an executable `sh` script to a fresh temp path that dumps exactly
    /// `frame_bytes * frames` zero bytes to stdout (ignoring whatever argv it's called with) and
    /// exits — standing in for `ffmpeg.exe` at the `FfmpegSource::spawn` boundary.
    fn write_fake_ffmpeg(frame_bytes: usize, frames: usize) -> std::path::PathBuf {
        let path = std::env::temp_dir().join(format!(
            "stellar-castbox-fake-ffmpeg-{}-{}.sh",
            std::process::id(),
            frames
        ));
        let total = frame_bytes * frames;
        let script = format!("#!/bin/sh\nhead -c {total} /dev/zero\n");
        let mut f = std::fs::File::create(&path).expect("create fake ffmpeg script");
        f.write_all(script.as_bytes()).expect("write fake ffmpeg script");
        let mut perms = f.metadata().unwrap().permissions();
        perms.set_mode(0o755);
        std::fs::set_permissions(&path, perms).expect("chmod fake ffmpeg script");
        path
    }

    #[tokio::test]
    async fn fake_ffmpeg_emits_n_frames_then_source_ends() {
        let (w, h, fps) = (2u16, 2u16, 10u8);
        let frame_bytes = w as usize * h as usize * 4;
        let fake = write_fake_ffmpeg(frame_bytes, 3);

        let mut source =
            FfmpegSource::spawn(FfmpegInput::File("ignored.mp4".into()), w, h, fps, fake.clone(), None)
                .expect("spawn fake ffmpeg");

        for i in 0..3u64 {
            let (pts, buf) = source.next_frame().await.expect("expected a frame");
            assert_eq!(pts, i * (1000 / fps as u64), "pts_ms must advance by 1000/fps per frame");
            assert_eq!(buf.len(), frame_bytes);
            assert!(buf.iter().all(|&b| b == 0));
        }
        assert!(
            source.next_frame().await.is_none(),
            "source must end once the fake ffmpeg's stdout hits EOF"
        );

        let _ = std::fs::remove_file(&fake);
    }
}
