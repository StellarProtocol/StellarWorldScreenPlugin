// Live-muxes YouTube's SEPARATE video-only + audio-only DASH streams into one MP4 that AVPro can play,
// by running the bundled ffmpeg.exe as a one-shot localhost HTTP server. This is what makes >360p
// possible: YouTube only serves 360p as a single combined URL — every higher resolution is two separate
// streams (video-only + audio-only) that must be muxed. ffmpeg stream-COPIES them (no re-encode → cheap +
// fast) into a fragmented MP4 and serves it on http://127.0.0.1:PORT, which AVPro opens exactly like any
// remote MP4. Genuine streaming: playback starts as ffmpeg produces bytes; nothing is fully downloaded
// first. BCL only — no UnityEngine.
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace Stellar.WorldScreen.Net;

/// <summary>
/// Runs the bundled <c>ffmpeg.exe</c> as a single-connection localhost HTTP server (<c>-listen 1</c>) that
/// live-muxes a (video-only, audio-only) URL pair into a fragmented MP4 AVPro streams. One mux runs at a
/// time — <see cref="StartServe"/> and <see cref="Stop"/> both kill any previous ffmpeg (freeing its port
/// and stopping the remote fetch). Thread-safe; started off the main thread from the resolve callback.
/// </summary>
public sealed class StreamMuxer
{
    private readonly string _ffmpegPath;
    private readonly string _workDir;
    private readonly object _gate = new();
    private Process? _proc;

    public StreamMuxer(string ffmpegPath, string workDir)
    {
        _ffmpegPath = ffmpegPath ?? throw new ArgumentNullException(nameof(ffmpegPath));
        _workDir = workDir ?? ".";
    }

    /// <summary>Whether the bundled ffmpeg.exe is actually present.</summary>
    public bool Available => File.Exists(_ffmpegPath);

    /// <summary>
    /// Kills any previous mux, then starts ffmpeg muxing <paramref name="videoUrl"/> + <paramref name="audioUrl"/>
    /// and serving on a fresh ephemeral localhost port. Returns the <c>http://127.0.0.1:PORT</c> URL for AVPro
    /// to open (the caller must delay the open until ffmpeg is listening — it binds only after opening both
    /// remote inputs), or null if ffmpeg is missing or couldn't be started.
    /// </summary>
    public string? StartServe(string videoUrl, string audioUrl)
    {
        Stop();
        if (!Available || string.IsNullOrEmpty(videoUrl) || string.IsNullOrEmpty(audioUrl)) return null;
        int port = FreeTcpPort();
        var url = $"http://127.0.0.1:{port}";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _workDir,
            };
            // -c copy: stream-copy both tracks (no re-encode) into a fragmented MP4 (frag_keyframe+empty_moov
            // so it plays while still being produced). -listen 1: serve exactly one HTTP client (AVPro).
            foreach (var a in new[]
            {
                "-hide_banner", "-loglevel", "warning",
                "-i", videoUrl,
                "-i", audioUrl,
                "-c", "copy",
                "-movflags", "+frag_keyframe+empty_moov+default_base_moof",
                "-f", "mp4",
                "-listen", "1",
                url,
            })
            {
                psi.ArgumentList.Add(a);
            }

            var p = Process.Start(psi);
            if (p == null) return null;
            lock (_gate) _proc = p;
            return url;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Kills the running mux process (if any), freeing its port and stopping the remote fetch.</summary>
    public void Stop()
    {
        Process? p;
        lock (_gate) { p = _proc; _proc = null; }
        if (p == null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (Exception) { }
        try { p.Dispose(); } catch (Exception) { }
    }

    // Grabs an ephemeral free TCP port by binding :0 and reading the assigned port. There is a tiny window
    // between releasing it and ffmpeg binding it, but on loopback for a just-freed ephemeral port that is
    // acceptable for this local bridge.
    private static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
