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
