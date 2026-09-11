//! Resolves a non-direct-media URL (a page URL like a YouTube watch link) to a direct stream URL
//! via the bundled `yt-dlp.exe`, so `FfmpegSource` (`helper/src/source/ffmpeg.rs`) — which only
//! understands direct media/stream URLs — can decode it. `helper/src/main.rs` calls
//! `is_direct_media_url` to decide whether a `url:` source needs this step, and `resolve_via_ytdlp`
//! to perform it before spawning `FfmpegSource`.

use anyhow::Context;
use std::path::Path;
use std::process::Stdio;
use tokio::process::Command;

/// Direct media/stream file extensions ffmpeg can decode without help. Anything else (e.g. a
/// YouTube watch page) is a page URL that needs `resolve_via_ytdlp` to find a URL ending in one of
/// these first.
const DIRECT_MEDIA_EXTENSIONS: &[&str] =
    &["mp4", "m3u8", "webm", "mkv", "ts", "mov", "flv", "mp3", "aac"];

/// True when `u`'s path — ignoring any `?query` string or `#fragment` — ends (case-insensitively)
/// in a direct media/stream extension ffmpeg can decode on its own. False means `u` needs
/// `resolve_via_ytdlp` first.
pub fn is_direct_media_url(u: &str) -> bool {
    let path = u.split('#').next().unwrap_or(u).split('?').next().unwrap_or(u).to_ascii_lowercase();
    DIRECT_MEDIA_EXTENSIONS.iter().any(|ext| path.ends_with(&format!(".{ext}")))
}

/// Returns the first non-empty (after trimming) line of `stdout`, or `None` when every line is
/// blank/whitespace-only (including empty `stdout`). Factored out of `resolve_via_ytdlp` so the
/// "which line is the resolved URL" parsing is unit-testable without a real yt-dlp process.
fn first_url_line(stdout: &str) -> Option<&str> {
    stdout.lines().map(str::trim).find(|line| !line.is_empty())
}

/// Returns just `u`'s scheme and host (e.g. `"https://cdn.example.com"`) for logging: a resolved
/// stream URL's path/query can carry a signing token, so callers must never log the whole URL.
/// Falls back to the literal `"<url>"` for input that isn't `scheme://host...`.
pub fn scheme_and_host(u: &str) -> String {
    match u.split_once("://") {
        Some((scheme, rest)) => {
            let host = rest.split(['/', '?', '#']).next().unwrap_or("");
            format!("{scheme}://{host}")
        }
        None => "<url>".to_string(),
    }
}

/// The yt-dlp format selector used for every resolve: prefer a combined (video+audio) stream at
/// ≤720p, else best video-only at ≤720p, else best combined, else best video-only — always exactly
/// one direct URL back. `FfmpegSource` decodes with `-an` (audio dropped) and scales to 640x360, so
/// a video-only URL is fine. Plain `-f b` was tried first and rejected: it fails with "Requested
/// format is not available" on many real YouTube videos (verified under Wine); this selector was
/// confirmed to resolve a real YouTube URL to a single direct googlevideo URL. Pinned by
/// `fake_ytdlp_tests::success_returns_first_stdout_line_and_invokes_expected_argv` — do not change
/// without re-verifying under Wine.
const YTDLP_FORMAT_SELECTOR: &str = "b[height<=?720]/bv[height<=?720]/b/bv";

/// Runs the bundled `yt-dlp.exe` to resolve a page URL (e.g. a YouTube watch link) to a single
/// direct stream URL ffmpeg can decode: `yt-dlp.exe -g -f "<selector>" --no-playlist
/// --no-warnings <url>`. `-g` prints the resolved URL to stdout instead of downloading;
/// `--no-playlist` guarantees exactly one video's URL even when `url` also matches a playlist.
/// Returns the first non-empty trimmed stdout line, or an `Err` (including a short slice of
/// stderr) on a non-zero exit or empty stdout.
pub async fn resolve_via_ytdlp(url: &str, ytdlp_path: &Path) -> anyhow::Result<String> {
    let output = Command::new(ytdlp_path)
        .arg("-g")
        .arg("-f")
        .arg(YTDLP_FORMAT_SELECTOR)
        .arg("--no-playlist")
        .arg("--no-warnings")
        .arg(url)
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .output()
        .await
        .with_context(|| format!("spawn yt-dlp at {}", ytdlp_path.display()))?;

    if !output.status.success() {
        let stderr: String = String::from_utf8_lossy(&output.stderr).chars().take(300).collect();
        anyhow::bail!("yt-dlp exited with {}: {stderr}", output.status);
    }

    let stdout = String::from_utf8_lossy(&output.stdout);
    first_url_line(&stdout)
        .map(str::to_string)
        .ok_or_else(|| anyhow::anyhow!("yt-dlp produced no URL on stdout"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn direct_mp4_url_is_direct() {
        assert!(is_direct_media_url("https://x/y.mp4"));
    }

    #[test]
    fn direct_mp4_url_with_query_token_is_direct() {
        assert!(is_direct_media_url("https://x/y.mp4?token=1"));
    }

    #[test]
    fn youtube_watch_page_is_not_direct() {
        assert!(!is_direct_media_url("https://youtube.com/watch?v=abc"));
    }

    #[test]
    fn m3u8_stream_url_is_direct() {
        assert!(is_direct_media_url("https://x/stream.m3u8"));
    }

    #[test]
    fn uppercase_scheme_and_extension_is_direct() {
        assert!(is_direct_media_url("HTTPS://X/Y.MP4"));
    }

    #[test]
    fn bare_page_path_is_not_direct() {
        assert!(!is_direct_media_url("https://x/page"));
    }

    #[test]
    fn every_documented_extension_is_direct() {
        for ext in ["mp4", "m3u8", "webm", "mkv", "ts", "mov", "flv", "mp3", "aac"] {
            let u = format!("https://x/clip.{ext}");
            assert!(is_direct_media_url(&u), "expected {u} to be direct");
        }
    }

    #[test]
    fn fragment_is_ignored_like_query() {
        assert!(is_direct_media_url("https://x/y.mp4#t=10"));
        assert!(!is_direct_media_url("https://x/page#section"));
    }

    #[test]
    fn extension_must_be_the_final_path_segment_suffix() {
        // ".mp4" appearing mid-path (not as the trailing extension) must not count.
        assert!(!is_direct_media_url("https://x/y.mp4-ish/page"));
    }

    #[test]
    fn first_url_line_picks_first_line_of_multiline_output() {
        let out = "https://cdn.example.com/a.mp4\nhttps://cdn.example.com/b.m4a\n";
        assert_eq!(first_url_line(out), Some("https://cdn.example.com/a.mp4"));
    }

    #[test]
    fn first_url_line_skips_leading_blank_lines() {
        let out = "\n\n  \nhttps://cdn.example.com/a.mp4\n";
        assert_eq!(first_url_line(out), Some("https://cdn.example.com/a.mp4"));
    }

    #[test]
    fn first_url_line_trims_trailing_whitespace_on_the_line() {
        let out = "  https://cdn.example.com/a.mp4  \r\n";
        assert_eq!(first_url_line(out), Some("https://cdn.example.com/a.mp4"));
    }

    #[test]
    fn first_url_line_none_on_empty_output() {
        assert_eq!(first_url_line(""), None);
    }

    #[test]
    fn first_url_line_none_on_whitespace_only_output() {
        assert_eq!(first_url_line("\n  \n\t\n"), None);
    }

    #[test]
    fn first_url_line_ignores_trailing_blank_lines() {
        let out = "https://cdn.example.com/a.mp4\n\n\n";
        assert_eq!(first_url_line(out), Some("https://cdn.example.com/a.mp4"));
    }

    #[test]
    fn scheme_and_host_strips_path_query_and_fragment() {
        assert_eq!(
            scheme_and_host("https://cdn.example.com/streams/clip.mp4?sig=abc123&exp=999#t=10"),
            "https://cdn.example.com"
        );
    }

    #[test]
    fn scheme_and_host_bare_host_with_no_path() {
        assert_eq!(scheme_and_host("https://youtube.com"), "https://youtube.com");
    }

    #[test]
    fn scheme_and_host_falls_back_on_unparseable_input() {
        assert_eq!(scheme_and_host("not a url"), "<url>");
    }
}

/// Spawn-level coverage substituting a FAKE `yt-dlp.exe` (a tiny shell script) for the real one, to
/// prove `resolve_via_ytdlp` wires `Command`/`.output()` through `first_url_line` end-to-end: the
/// exact argv it invokes, the non-zero-exit error path (with a stderr slice), and the
/// empty-stdout-on-success error path. The real `yt-dlp.exe` spawn is a Windows PE verified
/// out-of-band under Wine, not here. Unix-only (`sh` script + file permissions); never compiled into
/// the release build (only under `#[cfg(test)]`).
#[cfg(all(test, unix))]
mod fake_ytdlp_tests {
    use super::*;
    use std::io::Write;
    use std::os::unix::fs::PermissionsExt;

    /// Writes an executable `sh` script standing in for `yt-dlp.exe`: it appends its own argv
    /// (space-joined) to `argv_capture_path`, then prints `stdout_line` and exits 0.
    fn write_fake_ytdlp_ok(
        tag: &str,
        argv_capture_path: &std::path::Path,
        stdout_line: &str,
    ) -> std::path::PathBuf {
        let script = format!(
            "#!/bin/sh\necho \"$@\" > '{cap}'\nprintf '%s\\n' '{line}'\n",
            cap = argv_capture_path.display(),
            line = stdout_line,
        );
        write_script(tag, &script)
    }

    /// Writes an executable `sh` script standing in for a FAILING `yt-dlp.exe`: it writes
    /// `stderr_msg` to stderr and exits 1 (empty stdout).
    fn write_fake_ytdlp_fail(tag: &str, stderr_msg: &str) -> std::path::PathBuf {
        let script = format!("#!/bin/sh\necho '{stderr_msg}' 1>&2\nexit 1\n");
        write_script(tag, &script)
    }

    /// Writes an executable `sh` script standing in for a yt-dlp that exits 0 but prints nothing.
    fn write_fake_ytdlp_empty_stdout(tag: &str) -> std::path::PathBuf {
        write_script(tag, "#!/bin/sh\nexit 0\n")
    }

    /// Writes `script` to a fresh temp path and marks it executable, then atomically renames it
    /// into place. Writing directly to the FINAL path and exec'ing it moments later is flaky under
    /// this container's overlay filesystem (`ETXTBSY` "Text file busy", observed intermittently
    /// under parallel `cargo test` — the write-then-close-then-exec sequence on one path races);
    /// staging under a distinct temp name and renaming means the final path's inode was never open
    /// for writing at all, which sidesteps the race.
    fn write_script(tag: &str, script: &str) -> std::path::PathBuf {
        let dir = std::env::temp_dir();
        let path = dir.join(format!("stellar-castbox-fake-ytdlp-{}-{tag}.sh", std::process::id()));
        let tmp_path = dir.join(format!("stellar-castbox-fake-ytdlp-{}-{tag}.sh.tmp", std::process::id()));
        {
            let mut f = std::fs::File::create(&tmp_path).expect("create fake yt-dlp script");
            f.write_all(script.as_bytes()).expect("write fake yt-dlp script");
            let mut perms = f.metadata().unwrap().permissions();
            perms.set_mode(0o755);
            std::fs::set_permissions(&tmp_path, perms).expect("chmod fake yt-dlp script");
        }
        std::fs::rename(&tmp_path, &path).expect("rename fake yt-dlp script into place");
        path
    }

    /// Calls `resolve_via_ytdlp`, retrying a few times on a bare `Text file busy` (`ETXTBSY`)
    /// failure to spawn — this sandbox's fork/exec occasionally reports that transiently when
    /// several tests exec freshly-written scripts within the same instant (confirmed unrelated to
    /// `resolve_via_ytdlp`'s own logic: single-threaded `cargo test -- --test-threads=1` never hits
    /// it). Any OTHER error (including a real ETXTBSY caused by an actual logic bug) is returned
    /// immediately on the first attempt, not swallowed.
    async fn resolve_via_ytdlp_tolerating_sandbox_etxtbsy(
        url: &str,
        ytdlp_path: &std::path::Path,
    ) -> anyhow::Result<String> {
        for attempt in 0..10 {
            match resolve_via_ytdlp(url, ytdlp_path).await {
                Ok(v) => return Ok(v),
                // `anyhow::Error`'s `Display`/`to_string()` shows only the outer "spawn yt-dlp at
                // ..." context, not the wrapped io::Error — check the whole cause chain instead, or
                // this filter matches nothing and every ETXTBSY falls through as a real failure.
                Err(e) if attempt < 9 && e.chain().any(|c| c.to_string().contains("Text file busy")) => {
                    tokio::time::sleep(std::time::Duration::from_millis(20)).await;
                }
                Err(e) => return Err(e),
            }
        }
        unreachable!("loop always returns on the last attempt")
    }

    #[tokio::test]
    async fn success_returns_first_stdout_line_and_invokes_expected_argv() {
        let capture = std::env::temp_dir()
            .join(format!("stellar-castbox-fake-ytdlp-argv-{}.txt", std::process::id()));
        let fake = write_fake_ytdlp_ok("ok", &capture, "https://cdn.example.com/direct.mp4");

        let resolved = resolve_via_ytdlp_tolerating_sandbox_etxtbsy("https://youtube.com/watch?v=abc", &fake)
            .await
            .expect("fake yt-dlp should resolve");
        assert_eq!(resolved, "https://cdn.example.com/direct.mp4");

        let argv = std::fs::read_to_string(&capture).expect("read captured argv");
        assert_eq!(
            argv.trim(),
            "-g -f b[height<=?720]/bv[height<=?720]/b/bv --no-playlist --no-warnings https://youtube.com/watch?v=abc",
            "yt-dlp argv is a pinned contract — do not change without re-verifying under Wine"
        );

        let _ = std::fs::remove_file(&fake);
        let _ = std::fs::remove_file(&capture);
    }

    #[tokio::test]
    async fn nonzero_exit_returns_error_containing_stderr_slice() {
        let fake = write_fake_ytdlp_fail("fail", "ERROR: ffmpeg exited with an error");

        let err = resolve_via_ytdlp_tolerating_sandbox_etxtbsy("https://youtube.com/watch?v=dead", &fake)
            .await
            .expect_err("non-zero exit must be an error");
        assert!(
            err.to_string().contains("ffmpeg exited with an error"),
            "error must include a stderr slice, got: {err}"
        );

        let _ = std::fs::remove_file(&fake);
    }

    #[tokio::test]
    async fn empty_stdout_on_success_is_an_error() {
        let fake = write_fake_ytdlp_empty_stdout("empty");

        let err = resolve_via_ytdlp_tolerating_sandbox_etxtbsy("https://youtube.com/watch?v=blank", &fake)
            .await
            .expect_err("empty stdout must be an error even on exit 0");
        assert!(err.to_string().contains("no URL"), "unexpected error: {err}");

        let _ = std::fs::remove_file(&fake);
    }
}
