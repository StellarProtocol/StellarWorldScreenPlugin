// Live-muxes YouTube's SEPARATE video-only + audio-only DASH streams into an HLS stream that AVPro can
// play. This is what makes >360p possible: YouTube only serves 360p as a single combined URL — every
// higher resolution is two separate streams (video-only + audio-only) that must be muxed. ffmpeg
// stream-COPIES them (no re-encode → cheap + fast) into an HLS playlist of small .ts segments in a temp
// directory; a tiny loopback HTTP server (HlsServer) serves that directory, and AVPro opens
// http://127.0.0.1:PORT/stream.m3u8. HLS (many small complete segments) streams reliably on
// MediaFoundation where a live non-seekable fragmented-MP4 does NOT. Genuine streaming: playback starts
// after the first segment; keeping every segment also allows backward seeking. BCL only — no UnityEngine.
using System;
using System.Diagnostics;
using System.IO;

namespace Stellar.WorldScreen.Net;

/// <summary>
/// Runs the bundled <c>ffmpeg.exe</c> to live-mux a (video-only, audio-only) URL pair into an HLS playlist,
/// served over loopback by an <see cref="HlsServer"/>. One mux runs at a time — <see cref="StartServe"/>
/// and <see cref="Stop"/> both kill any previous ffmpeg + server and delete the temp dir. Thread-safe;
/// started off the main thread from the resolve callback.
/// </summary>
public sealed class StreamMuxer
{
    private readonly string _ffmpegPath;
    private readonly string _workDir;
    private readonly object _gate = new();
    private Process? _proc;
    private HlsServer? _server;
    private string? _hlsDir;

    public StreamMuxer(string ffmpegPath, string workDir)
    {
        _ffmpegPath = ffmpegPath ?? throw new ArgumentNullException(nameof(ffmpegPath));
        _workDir = workDir ?? ".";
    }

    /// <summary>Whether the bundled ffmpeg.exe is actually present.</summary>
    public bool Available => File.Exists(_ffmpegPath);

    /// <summary>
    /// Kills any previous mux, then starts ffmpeg muxing <paramref name="videoUrl"/> + <paramref name="audioUrl"/>
    /// into a fresh HLS temp dir and serves it. Returns the <c>http://127.0.0.1:PORT/stream.m3u8</c> URL for
    /// AVPro (the caller must wait for <see cref="PlaylistReady"/> before opening it), or null on failure.
    /// </summary>
    public string? StartServe(string videoUrl, string audioUrl)
    {
        Stop();
        if (!Available || string.IsNullOrEmpty(videoUrl) || string.IsNullOrEmpty(audioUrl)) return null;
        string dir = Path.Combine(Path.GetTempPath(), "stellar-hls-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var server = new HlsServer(dir);
            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                // Working dir = the HLS output dir so the RELATIVE init/segment/playlist names all land here and
                // the playlist carries bare basename URIs (a full path would become a broken HTTP URL).
                WorkingDirectory = dir,
            };
            // -c:v copy keeps the (expensive) 1080p video as-is; -c:a aac RE-ENCODES the audio (cheap) so it is
            // re-timed to the video timeline (YouTube's separate audio DASH carries its own offset — a naive
            // `-c copy` desynced it). -hls_segment_type fmp4: MP4 segments, NOT mpegts — the game's MediaFoundation
            // decodes mpegts H.264 with the wrong colour matrix on some streams (magenta/green), but the MP4 path
            // is always correct (same as the direct 360p stream). event + list_size 0: keep every segment (so seek
            // works). temp_file: never expose a half-written file.
            foreach (var a in new[]
            {
                "-hide_banner", "-loglevel", "warning",
                "-i", videoUrl,
                "-i", audioUrl,
                "-map", "0:v:0", "-map", "1:a:0",
                "-c:v", "copy", "-c:a", "aac", "-b:a", "160k",
                "-f", "hls",
                "-hls_time", "2",
                "-hls_list_size", "0",
                "-hls_playlist_type", "event",
                "-hls_flags", "temp_file",
                "-hls_segment_type", "fmp4",
                "-hls_fmp4_init_filename", "init.mp4",
                "-hls_segment_filename", "seg%05d.m4s",
                "stream.m3u8",
            })
            {
                psi.ArgumentList.Add(a);
            }

            var p = Process.Start(psi);
            if (p == null) { server.Stop(); TryDeleteDir(dir); return null; }
            server.Start();
            lock (_gate) { _proc = p; _server = server; _hlsDir = dir; }
            return $"http://127.0.0.1:{server.Port}/stream.m3u8";
        }
        catch (Exception)
        {
            TryDeleteDir(dir);
            return null;
        }
    }

    /// <summary>True once ffmpeg has written the playlist AND at least one full segment — i.e. AVPro can open
    /// it. Opening before this yields an empty/missing playlist and a failed load.</summary>
    public bool PlaylistReady
    {
        get
        {
            string? dir;
            lock (_gate) dir = _hlsDir;
            if (dir == null) return false;
            try
            {
                var m3u8 = Path.Combine(dir, "stream.m3u8");
                if (!File.Exists(m3u8)) return false;
                var text = File.ReadAllText(m3u8);
                return text.IndexOf(".m4s", StringComparison.OrdinalIgnoreCase) >= 0; // references ≥1 fMP4 segment
            }
            catch (Exception) { return false; }
        }
    }

    /// <summary>Kills the running mux + server (if any), freeing the port and deleting the temp dir.</summary>
    public void Stop()
    {
        Process? p; HlsServer? s; string? dir;
        lock (_gate) { p = _proc; s = _server; dir = _hlsDir; _proc = null; _server = null; _hlsDir = null; }
        if (p != null)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (Exception) { }
            try { p.Dispose(); } catch (Exception) { }
        }
        try { s?.Stop(); } catch (Exception) { }
        if (dir != null) TryDeleteDir(dir);
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (Exception) { }
    }
}
