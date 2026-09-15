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
    private const int ResolveTimeoutMs = 45000; // resolve is fast, but spinning up the deno JS runtime adds a little
    private readonly string _ytdlpPath;
    private readonly string _ffmpegDir;
    private readonly string? _jsRuntimePath; // bundled deno.exe (null ⇒ no-JS fallback client)

    public YtDlpResolver(string ytdlpPath, string ffmpegDir, string? jsRuntimePath = null)
    {
        _ytdlpPath = ytdlpPath ?? throw new ArgumentNullException(nameof(ytdlpPath));
        _ffmpegDir = ffmpegDir ?? ".";
        _jsRuntimePath = !string.IsNullOrEmpty(jsRuntimePath) && File.Exists(jsRuntimePath) ? jsRuntimePath : null;
    }

    /// <summary>Whether a JS runtime (deno) is bundled — the SUPPORTED yt-dlp path that reaches 1080p+.</summary>
    public bool HasJsRuntime => _jsRuntimePath != null;

    /// <summary>A resolve result: the direct stream URL(s) (one combined, or two [video, audio] to mux) plus the
    /// video's total duration in seconds (0 if unknown — e.g. a live stream). The duration is needed because an
    /// HLS mux reports no duration to the player, so the plugin supplies it for the seek bar + playlist advance.</summary>
    public sealed record ResolveResult(string[] Urls, int DurationSec);

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

    // No-JS FALLBACK client list (used only when deno isn't bundled): tv_embedded still exposes DIRECT
    // (protocol=https) DASH URLs up to 4K without a JS runtime, but yt-dlp flags it "unsupported" and warns
    // that no-JS YouTube extraction is deprecated — so it's a fallback, not the default. android is the last
    // resort (SABR-capped to 360p / itag 18). Harmless for non-YouTube extractors (ignored). With deno present
    // we instead use yt-dlp's DEFAULT (web) client — the supported path — which reaches 1080p+ cleanly.
    private const string NoJsFallbackClients = "tv_embedded,android";

    /// <summary>Selector for a STREAM-and-MUX pair: the best DIRECT-https video-only track ≤ <paramref name="maxHeight"/>
    /// (avc1 preferred for AVPro) plus the best direct-https m4a audio — resolved as TWO urls that <c>StreamMuxer</c>
    /// muxes live. Above 360p YouTube only serves separate video/audio, so a real >360p stream MUST be a merge.
    /// Falls back to any https video+audio, then to a single combined format (18 = 360p) which yields ONE url
    /// (played directly, no mux). <c>protocol^=https</c> avoids the SABR/HLS (m3u8) variants that need a JS runtime.</summary>
    public static string MergeSelectorForHeight(int maxHeight) =>
        $"bv*[height<={maxHeight}][vcodec^=avc1][protocol^=https]+ba[acodec^=mp4a][protocol^=https]/" +
        $"bv*[height<={maxHeight}][protocol^=https]+ba[protocol^=https]/" +
        $"b[vcodec^=avc1][acodec^=mp4a]/b[acodec!=none][vcodec!=none]/18";

    /// <summary>
    /// Resolves <paramref name="url"/> to a DIRECT stream URL via <c>yt-dlp -g</c> (fast — no download). A
    /// single-format selector yields exactly one URL; <paramref name="onDone"/> fires on a background thread
    /// with that URL, or null on any failure. AVPro then streams it (buffering as it plays).
    /// </summary>
    public void ResolveStreamUrlAsync(string url, string formatSelector, Action<string?> onDone)
    {
        var t = new Thread(() =>
        {
            var res = ResolveUrls(url, formatSelector);
            onDone(res != null && res.Urls.Length > 0 ? res.Urls[0] : null);
        })
        {
            IsBackground = true,
            Name = "StellarWorldScreen.YtDlpResolve",
        };
        t.Start();
    }

    /// <summary>
    /// Resolves <paramref name="url"/> to a DIRECT stream via <c>yt-dlp -g</c> (fast — no download), returning
    /// the resolved URL(s) + duration: ONE url for a combined format (play it directly) or TWO — [video, audio]
    /// — for a merge selector (mux them with <c>StreamMuxer</c>). <paramref name="onDone"/> fires on a background
    /// thread with the result, or null on any failure.
    /// </summary>
    public void ResolveMergeUrlsAsync(string url, string formatSelector, Action<ResolveResult?> onDone)
    {
        var t = new Thread(() => onDone(ResolveUrls(url, formatSelector)))
        {
            IsBackground = true,
            Name = "StellarWorldScreen.YtDlpMerge",
        };
        t.Start();
    }

    // Resolves via yt-dlp, trying the FAST no-JS clients first and only paying for the deno (supported) path when
    // the fast attempt can't get the video+audio PAIR. deno reaches formats SABR strips from the no-JS clients but
    // roughly DOUBLES the resolve time (~6.6s vs ~3.3s measured), so it must not be on the common path.
    private ResolveResult? ResolveUrls(string url, string formatSelector)
    {
        var fast = RunResolve(url, formatSelector, useDeno: false);
        if (_jsRuntimePath != null && (fast == null || fast.Urls.Length < 2))
        {
            var slow = RunResolve(url, formatSelector, useDeno: true);
            if (slow != null && slow.Urls.Length >= (fast?.Urls.Length ?? 0)) return slow;
        }
        return fast;
    }

    private ResolveResult? RunResolve(string url, string formatSelector, bool useDeno)
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
            // --print DUR emits the duration first; -g then emits the selected format's URL(s). We parse both.
            psi.ArgumentList.Add("--print");
            psi.ArgumentList.Add("DUR=%(duration)s");
            psi.ArgumentList.Add("-g");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(formatSelector);
            psi.ArgumentList.Add("--no-playlist");
            if (useDeno && _jsRuntimePath != null)
            {
                // SUPPORTED path: hand yt-dlp the bundled deno so its default (web) client can decipher and
                // expose the full DASH ladder incl. 1080p+ as direct https URLs — no player_client override.
                psi.ArgumentList.Add("--js-runtimes");
                psi.ArgumentList.Add("deno:" + _jsRuntimePath);
            }
            else
            {
                // FAST no-JS clients: tv_embedded still serves direct https 1080p and resolves in ~half the time.
                psi.ArgumentList.Add("--extractor-args");
                psi.ArgumentList.Add("youtube:player_client=" + NoJsFallbackClients);
            }
            psi.ArgumentList.Add(url);

            using var p = Process.Start(psi);
            if (p == null) return null;
            string outText = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(ResolveTimeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { }
                return null;
            }
            if (p.ExitCode != 0) return null;
            // Output: a "DUR=<seconds>" line (from --print) plus one URL line per selected format — ONE for a
            // combined format, TWO ([video, audio]) for a merge selector. Pull the duration out; the rest are URLs.
            var urls = new System.Collections.Generic.List<string>(2);
            int durationSec = 0;
            foreach (var line in outText.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length == 0) continue;
                if (t.StartsWith("DUR=", StringComparison.Ordinal))
                {
                    var v = t.Substring(4);
                    if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0)
                        durationSec = (int)d;
                    continue;
                }
                urls.Add(t);
            }
            return urls.Count > 0 ? new ResolveResult(urls.ToArray(), durationSec) : null;
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
