// Live-muxes YouTube's SEPARATE video-only + audio-only DASH streams into ONE progressive fragmented MP4
// that AVPro can stream. >360p needs this: YouTube serves 360p as one combined URL, but every higher
// resolution is two separate streams that must be muxed. ffmpeg stream-COPIES them (no re-encode → cheap +
// fast, and MP4 edit lists keep A/V in sync) into a single growing MP4 with its moov at the front
// (empty_moov) so it plays while still being written. A loopback HTTP server (ProgressiveServer) serves that
// one file with byte-range support, and AVPro opens http://127.0.0.1:PORT/stream.mp4 — the remote-mp4 path MF
// decodes with correct colour. SEEK works YouTube-style: to jump to time T the plugin restarts the mux with
// `-ss T` (ffmpeg range-fetches from T's byte offset via the source index), so it never downloads the whole
// video to seek. BCL only — no UnityEngine.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Stellar.WorldScreen.Net;

/// <summary>
/// Runs the bundled <c>ffmpeg.exe</c> to live-mux a (video-only, audio-only) URL pair — optionally starting at
/// a seek offset — into one growing fragmented MP4, served over loopback by a <see cref="ProgressiveServer"/>.
/// One mux runs at a time; <see cref="StartServe"/> and <see cref="Stop"/> kill any previous ffmpeg + server
/// and delete the temp dir. Thread-safe; started off the main thread.
/// </summary>
public sealed class StreamMuxer
{
    private const long ReadyThresholdBytes = 96 * 1024; // ftyp + moov + first fragment written → openable

    private readonly string _ffmpegPath;
    private readonly string _workDir;
    private readonly object _gate = new();
    private Process? _proc;
    private ProgressiveServer? _server;
    private string? _dir;
    private string? _file;

    public StreamMuxer(string ffmpegPath, string workDir)
    {
        _ffmpegPath = ffmpegPath ?? throw new ArgumentNullException(nameof(ffmpegPath));
        _workDir = workDir ?? ".";
    }

    /// <summary>Whether the bundled ffmpeg.exe is actually present.</summary>
    public bool Available => File.Exists(_ffmpegPath);

    /// <summary>Deletes leftover mux temp dirs from previous sessions (a crash/kill can strand one, and they
    /// each hold a cached video). Safe at startup — no mux is running yet. Runs off-thread.</summary>
    public static void SweepOldTempDirs()
    {
        var t = new System.Threading.Thread(() =>
        {
            try
            {
                var tmp = Path.GetTempPath();
                foreach (var pat in new[] { "stellar-mux-*", "stellar-hls-*" })
                    foreach (var d in Directory.EnumerateDirectories(tmp, pat))
                        try { Directory.Delete(d, recursive: true); } catch (Exception) { }
            }
            catch (Exception) { }
        }) { IsBackground = true, Name = "StellarWorldScreen.MuxSweep" };
        t.Start();
    }

    /// <summary>
    /// Kills any previous mux, then starts ffmpeg muxing <paramref name="videoUrl"/> + <paramref name="audioUrl"/>
    /// (from <paramref name="startSec"/> if &gt; 0) into one growing fragmented MP4 and serves it. Returns the
    /// <c>http://127.0.0.1:PORT/stream.mp4</c> URL for AVPro (wait for <see cref="PlaylistReady"/> before opening),
    /// or null on failure. <paramref name="durationSec"/> is the full video length, used to size the remaining
    /// bytes when starting partway through.
    /// </summary>
    public string? StartServe(string videoUrl, string audioUrl, double startSec = 0, double durationSec = 0)
    {
        Stop();
        if (!Available || string.IsNullOrEmpty(videoUrl) || string.IsNullOrEmpty(audioUrl)) return null;
        long videoBytes = ParseClen(videoUrl), audioBytes = ParseClen(audioUrl);
        if (videoBytes <= 0 || audioBytes <= 0) return null; // need sizes for progressive serving
        long fullTotal = videoBytes + audioBytes;
        // Total sample bytes for the part we actually stream. From 0 this is exact; from a seek it's scaled by
        // the remaining fraction (bitrate ≈ uniform) — the ProgressiveServer also stops at the real EOF once the
        // mux is done, so an imperfect estimate never hangs.
        long total = fullTotal;
        if (startSec > 0.5 && durationSec > 1.0)
        {
            double frac = Math.Max(0.02, Math.Min(1.0, (durationSec - startSec) / durationSec));
            total = (long)(fullTotal * frac);
        }

        string dir = Path.Combine(Path.GetTempPath(), "stellar-mux-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "stream.mp4");
            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _workDir,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("warning");
            // -ss BEFORE each -i is an INPUT seek: ffmpeg jumps to the keyframe at startSec using the source's
            // own index and range-fetches from there — it does NOT download the earlier part.
            if (startSec > 0.5)
            {
                string ss = startSec.ToString("0.###", CultureInfo.InvariantCulture);
                psi.ArgumentList.Add("-ss"); psi.ArgumentList.Add(ss);
                psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(videoUrl);
                psi.ArgumentList.Add("-ss"); psi.ArgumentList.Add(ss);
                psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(audioUrl);
            }
            else
            {
                psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(videoUrl);
                psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(audioUrl);
            }
            // -c copy: no re-encode; MP4 edit lists keep A/V in sync. frag_keyframe+empty_moov: moov at the FRONT
            // so it plays while still being written.
            foreach (var a in new[]
            {
                "-map", "0:v:0", "-map", "1:a:0", "-c", "copy",
                "-movflags", "+frag_keyframe+empty_moov+default_base_moof", "-f", "mp4", file,
            })
            {
                psi.ArgumentList.Add(a);
            }

            var p = Process.Start(psi);
            if (p == null) { TryDeleteDir(dir); return null; }
            // muxDone lets the server stop waiting at the true EOF once ffmpeg has finished (so an over/under
            // estimate of `total` neither hangs nor needs to be exact).
            var server = new ProgressiveServer(file, total, muxDone: () => { try { return p.HasExited; } catch { return true; } });
            server.Start();
            lock (_gate) { _proc = p; _server = server; _dir = dir; _file = file; }
            return $"http://127.0.0.1:{server.Port}/stream.mp4";
        }
        catch (Exception)
        {
            TryDeleteDir(dir);
            return null;
        }
    }

    /// <summary>True once ffmpeg has written enough of the head (ftyp + moov + first fragment) that AVPro can
    /// open the URL. Opening earlier gives the player a truncated moov and a failed load.</summary>
    public bool PlaylistReady
    {
        get
        {
            string? file;
            lock (_gate) file = _file;
            if (file == null) return false;
            try { var fi = new FileInfo(file); return fi.Exists && fi.Length >= ReadyThresholdBytes; }
            catch (Exception) { return false; }
        }
    }

    /// <summary>Kills the running mux + server (if any), freeing the port and deleting the temp dir.</summary>
    public void Stop()
    {
        Process? p; ProgressiveServer? s; string? dir;
        lock (_gate) { p = _proc; s = _server; dir = _dir; _proc = null; _server = null; _dir = null; _file = null; }
        if (p != null)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (Exception) { }
            try { p.Dispose(); } catch (Exception) { }
        }
        try { s?.Stop(); } catch (Exception) { }
        if (dir != null) TryDeleteDir(dir);
    }

    // Extracts the byte size from a googlevideo URL's clen= parameter (exact for a -c copy remux's sample data).
    private static long ParseClen(string url)
    {
        var m = Regex.Match(url, @"[?&]clen=(\d+)");
        return m.Success && long.TryParse(m.Groups[1].Value, out var v) ? v : 0;
    }

    // Deletes the temp dir off-thread: on a seek this can hold a large partial download, and Stop() runs on the
    // game's main thread — a synchronous recursive delete there would hitch the frame.
    private static void TryDeleteDir(string dir)
    {
        var t = new System.Threading.Thread(() =>
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (Exception) { }
        }) { IsBackground = true, Name = "StellarWorldScreen.MuxCleanup" };
        t.Start();
    }
}
