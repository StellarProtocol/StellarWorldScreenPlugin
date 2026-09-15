// Live-muxes YouTube's SEPARATE video-only + audio-only DASH streams into ONE progressive fragmented MP4
// that AVPro can stream. This is what makes >360p possible: YouTube only serves 360p as a single combined
// URL — every higher resolution is two separate streams that must be muxed. ffmpeg stream-COPIES them (no
// re-encode → cheap + fast, and MP4 edit lists keep A/V in sync) into a single growing MP4 with its moov at
// the front (empty_moov) so it plays while still being written. A loopback HTTP server (ProgressiveServer)
// serves that one file with byte-range support, and AVPro opens http://127.0.0.1:PORT/stream.mp4 — the same
// remote-mp4 path MF decodes with correct colour (HLS, mpegts and fMP4 alike, hit Proton MF bugs). BCL only.
using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace Stellar.WorldScreen.Net;

/// <summary>
/// Runs the bundled <c>ffmpeg.exe</c> to live-mux a (video-only, audio-only) URL pair into one growing
/// fragmented MP4, served over loopback by a <see cref="ProgressiveServer"/>. One mux runs at a time —
/// <see cref="StartServe"/> and <see cref="Stop"/> both kill any previous ffmpeg + server and delete the temp
/// dir. Thread-safe; started off the main thread from the resolve callback.
/// </summary>
public sealed class StreamMuxer
{
    // Enough of the head written that AVPro can open it: ftyp + empty moov + the first fragment.
    private const long ReadyThresholdBytes = 96 * 1024;

    private readonly string _ffmpegPath;
    private readonly string _workDir;
    private readonly object _gate = new();
    private Process? _proc;
    private ProgressiveServer? _server;
    private string? _dir;
    private string? _file;
    private string? _seekFile; // second output: a complete faststart (seekable) copy, finalized when ffmpeg exits

    public StreamMuxer(string ffmpegPath, string workDir)
    {
        _ffmpegPath = ffmpegPath ?? throw new ArgumentNullException(nameof(ffmpegPath));
        _workDir = workDir ?? ".";
    }

    /// <summary>Whether the bundled ffmpeg.exe is actually present.</summary>
    public bool Available => File.Exists(_ffmpegPath);

    /// <summary>
    /// Kills any previous mux, then starts ffmpeg muxing <paramref name="videoUrl"/> + <paramref name="audioUrl"/>
    /// into one growing fragmented MP4 and serves it. Returns the <c>http://127.0.0.1:PORT/stream.mp4</c> URL for
    /// AVPro (wait for <see cref="PlaylistReady"/> before opening), or null on failure. Needs the DASH streams'
    /// byte sizes (the <c>clen=</c> URL param) to advertise a total size to the player.
    /// </summary>
    public string? StartServe(string videoUrl, string audioUrl)
    {
        Stop();
        if (!Available || string.IsNullOrEmpty(videoUrl) || string.IsNullOrEmpty(audioUrl)) return null;
        long videoBytes = ParseClen(videoUrl), audioBytes = ParseClen(audioUrl);
        if (videoBytes <= 0 || audioBytes <= 0) return null; // need sizes for progressive serving
        // Advertise the SAMPLE-byte total (video + audio) — deliberately a hair UNDER the real file size (which
        // also carries small MP4 box overhead), so the player stops a few KB early instead of hanging at the end
        // waiting for bytes ffmpeg never writes.
        long total = videoBytes + audioBytes;

        string dir = Path.Combine(Path.GetTempPath(), "stellar-mux-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "stream.mp4");
            string seekFile = Path.Combine(dir, "seek.mp4");
            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _workDir,
            };
            // TWO outputs from one read of the inputs (no extra download):
            //  1) stream.mp4 — frag_keyframe+empty_moov (moov at FRONT) → plays WHILE being written (instant).
            //  2) seek.mp4   — +faststart (moov indexed, finalized when ffmpeg exits) → fully SEEKABLE once the
            //     whole video has arrived; the plugin swaps to it when the viewer scrubs.
            // -c copy on both: no re-encode, and MP4 edit lists keep A/V in sync.
            foreach (var a in new[]
            {
                "-hide_banner", "-loglevel", "warning",
                "-i", videoUrl,
                "-i", audioUrl,
                "-map", "0:v:0", "-map", "1:a:0", "-c", "copy",
                "-movflags", "+frag_keyframe+empty_moov+default_base_moof", "-f", "mp4", file,
                "-map", "0:v:0", "-map", "1:a:0", "-c", "copy",
                "-movflags", "+faststart", "-f", "mp4", seekFile,
            })
            {
                psi.ArgumentList.Add(a);
            }

            var p = Process.Start(psi);
            if (p == null) { TryDeleteDir(dir); return null; }
            var server = new ProgressiveServer(file, total);
            server.Start();
            lock (_gate) { _proc = p; _server = server; _dir = dir; _file = file; _seekFile = seekFile; }
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

    /// <summary>True once ffmpeg has EXITED cleanly and the faststart (seekable) copy is finalized — the point
    /// at which the plugin can swap to it so the viewer can scrub. (faststart's moov is written only on close,
    /// so a running/killed mux is never "ready".)</summary>
    public bool SeekableReady
    {
        get
        {
            Process? p; string? sf;
            lock (_gate) { p = _proc; sf = _seekFile; }
            if (p == null || sf == null) return false;
            try { return p.HasExited && p.ExitCode == 0 && new FileInfo(sf).Length > ReadyThresholdBytes; }
            catch (Exception) { return false; }
        }
    }

    /// <summary>The complete faststart (seekable) MP4 file path — valid only once <see cref="SeekableReady"/>.</summary>
    public string? SeekablePath { get { lock (_gate) return _seekFile; } }

    /// <summary>Kills the running mux + server (if any), freeing the port and deleting the temp dir.</summary>
    public void Stop()
    {
        Process? p; ProgressiveServer? s; string? dir;
        lock (_gate) { p = _proc; s = _server; dir = _dir; _proc = null; _server = null; _dir = null; _file = null; _seekFile = null; }
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

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (Exception) { }
    }
}
