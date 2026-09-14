// Prepares a page URL (YouTube, etc.) for AVPro by running the bundled yt-dlp.exe to DOWNLOAD + MUX the
// video and audio streams into one local mp4 AVPro can open. YouTube no longer serves combined
// (progressive) formats, and AVPro plays a single source, so a local muxed file is the reliable path
// (and cleanly supports 1080p+). Runs off-thread; the result is delivered via callback for the caller to
// marshal onto Unity's main thread. BCL only — no UnityEngine.
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Stellar.WorldScreen.Net;

/// <summary>
/// Downloads+muxes a page URL to a local mp4 via <c>yt-dlp.exe</c> (which shells the sibling ffmpeg for the
/// merge). The format selector biases to H.264 video + AAC audio — the combo MediaFoundation/AVPro decode
/// most reliably — capped at the requested height, with fallbacks to any codec.
/// </summary>
public sealed class YtDlpResolver
{
    private const int TimeoutMs = 300000; // 5 min — long clips download slowly; a stuck job is abandoned
    private const int ResolveTimeoutMs = 30000; // resolving a direct URL is fast (no download)
    private readonly string _ytdlpPath;
    private readonly string _ffmpegDir;

    public YtDlpResolver(string ytdlpPath, string ffmpegDir)
    {
        _ytdlpPath = ytdlpPath ?? throw new ArgumentNullException(nameof(ytdlpPath));
        _ffmpegDir = ffmpegDir ?? ".";
    }

    /// <summary>Whether the bundled yt-dlp.exe is actually present.</summary>
    public bool Available => File.Exists(_ytdlpPath);

    /// <summary>yt-dlp format selector for the given max height, preferring H.264+AAC for AVPro playback.</summary>
    public static string SelectorForHeight(int maxHeight) =>
        $"bv*[height<={maxHeight}][vcodec^=avc1]+ba[acodec^=mp4a]/" +
        $"bv*[height<={maxHeight}][vcodec^=avc1]+ba/" +
        $"bv*[height<={maxHeight}]+ba/b[height<={maxHeight}]/b";

    /// <summary>Selector for STREAMING (no download): the best SINGLE format that already carries both audio and
    /// video — a progressive MP4 or an HLS/DASH variant AVPro can open directly — auto-picking the highest such
    /// quality (never a separate-stream merge, which can't stream as one URL). Falls back to 18 (360p progressive,
    /// nearly always present). Prefers avc1+mp4a, which MediaFoundation/AVPro decode most reliably.</summary>
    public const string BestStreamableSelector =
        "b[vcodec^=avc1][acodec^=mp4a]/b[acodec!=none][vcodec!=none]/18";

    /// <summary>
    /// Resolves <paramref name="url"/> to a DIRECT stream URL via <c>yt-dlp -g</c> (fast — no download). A
    /// single-format selector yields exactly one URL; <paramref name="onDone"/> fires on a background thread
    /// with that URL, or null on any failure. AVPro then streams it (buffering as it plays).
    /// </summary>
    public void ResolveStreamUrlAsync(string url, string formatSelector, Action<string?> onDone)
    {
        var t = new Thread(() => onDone(ResolveStreamUrl(url, formatSelector)))
        {
            IsBackground = true,
            Name = "StellarWorldScreen.YtDlpResolve",
        };
        t.Start();
    }

    private string? ResolveStreamUrl(string url, string formatSelector)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ytdlpPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true, // -g prints the URL(s) to stdout; small output, safe to drain
                WorkingDirectory = _ffmpegDir,
            };
            // player_client=android exposes YouTube's progressive format 18 (360p, combined audio+video)
            // WITHOUT needing a JS runtime — the web client requires one and otherwise serves only separate
            // (unstreamable-as-one-URL) DASH streams. Harmless for non-YouTube extractors (ignored).
            foreach (var a in new[]
            {
                "-g", "-f", formatSelector, "--no-playlist",
                "--extractor-args", "youtube:player_client=android",
                url,
            })
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi);
            if (p == null) return null;
            string outText = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(ResolveTimeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { }
                return null;
            }
            if (p.ExitCode != 0) return null;
            // A single-format selector yields one URL; take the first non-empty line.
            foreach (var line in outText.Split('\n'))
            {
                var u = line.Trim();
                if (u.Length > 0) return u;
            }
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads+muxes <paramref name="url"/> to <paramref name="outPath"/> (overwritten). <paramref name="onDone"/>
    /// fires on a background thread with true on success (the file is ready to open), false on any failure.
    /// </summary>
    public void DownloadAsync(string url, string formatSelector, string outPath, Action<bool> onDone)
    {
        var t = new Thread(() => onDone(Download(url, formatSelector, outPath)))
        {
            IsBackground = true,
            Name = "StellarWorldScreen.YtDlp",
        };
        t.Start();
    }

    private bool Download(string url, string formatSelector, string outPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ytdlpPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                // Do NOT redirect: yt-dlp writes lots of progress to both pipes during a long download, and
                // draining them sequentially can deadlock. We only need the exit code + the output file.
                WorkingDirectory = _ffmpegDir,
            };
            foreach (var a in new[]
            {
                "-f", formatSelector,
                "--merge-output-format", "mp4",
                "--ffmpeg-location", _ffmpegDir,
                "--no-playlist",
                "--force-overwrites",
                "--no-part",
                "-o", outPath,
                url,
            })
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(TimeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { }
                return false;
            }
            return p.ExitCode == 0 && File.Exists(outPath);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
