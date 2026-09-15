// A minimal loopback HTTP/1.1 file server for one HLS output directory, so AVPro (MediaFoundation) can
// stream the live-muxed playlist. MediaFoundation streams HLS (a playlist of small complete .ts segments)
// reliably where it CANNOT stream a live non-seekable fragmented-MP4 (that failed in-game). ffmpeg writes
// stream.m3u8 + seg*.ts into the directory; this serves them on http://127.0.0.1:PORT/. BCL only — plain
// TcpListener sockets (no HttpListener/http.sys, which isn't dependable under wine).
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Stellar.WorldScreen.Net;

/// <summary>Serves a single directory's <c>.m3u8</c>/<c>.ts</c> files over loopback HTTP for AVPro. One
/// request per connection (Connection: close); a thread per accepted client. Start once, Stop to tear down.</summary>
public sealed class HlsServer
{
    private readonly string _dir;
    private readonly TcpListener _listener;
    private volatile bool _running;

    /// <summary>The port the server bound to (assigned by the OS).</summary>
    public int Port { get; }

    public HlsServer(string dir)
    {
        _dir = dir ?? throw new ArgumentNullException(nameof(dir));
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    /// <summary>Starts the accept loop on a background thread.</summary>
    public void Start()
    {
        _running = true;
        var t = new Thread(AcceptLoop) { IsBackground = true, Name = "StellarWorldScreen.HlsServer" };
        t.Start();
    }

    /// <summary>Stops the server (closes the listener; in-flight responses just end).</summary>
    public void Stop()
    {
        _running = false;
        try { _listener.Stop(); } catch (Exception) { }
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            TcpClient client;
            try { client = _listener.AcceptTcpClient(); }
            catch (Exception) { break; } // listener stopped
            var t = new Thread(() => HandleClient(client)) { IsBackground = true };
            t.Start();
        }
    }

    private void HandleClient(TcpClient client)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var (method, path, rangeStart, rangeEnd) = ReadRequest(stream);
                if (path == null) return;
                ServeFile(stream, method == "HEAD", path, rangeStart, rangeEnd);
            }
        }
        catch (Exception) { /* client went away — ignore */ }
    }

    // Reads the request line + headers; returns (method, url-path, rangeStart, rangeEnd). rangeEnd = -1 = open.
    private static (string? method, string? path, long rangeStart, long rangeEnd) ReadRequest(NetworkStream stream)
    {
        var sb = new StringBuilder();
        int b, prevNl = 0;
        // Read headers up to the blank line (\r\n\r\n). Bounded to avoid an unbounded read.
        while (sb.Length < 8192 && (b = stream.ReadByte()) != -1)
        {
            sb.Append((char)b);
            if (b == '\n') { if (prevNl == 1) break; prevNl = 1; }
            else if (b != '\r') prevNl = 0;
        }
        var text = sb.ToString();
        var lines = text.Split('\n');
        if (lines.Length == 0) return (null, null, 0, -1);
        var parts = lines[0].Split(' ');
        if (parts.Length < 2) return (null, null, 0, -1);
        string method = parts[0];
        string rawPath = parts[1];
        int q = rawPath.IndexOf('?');
        if (q >= 0) rawPath = rawPath.Substring(0, q);
        long start = 0, end = -1;
        foreach (var line in lines)
        {
            if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
            {
                var v = line.Substring(6).Trim();          // e.g. "bytes=0-1023"
                int eq = v.IndexOf('=');
                if (eq >= 0) v = v.Substring(eq + 1);
                var rng = v.Split('-');
                if (rng.Length >= 1) long.TryParse(rng[0].Trim(), out start);
                if (rng.Length >= 2 && rng[1].Trim().Length > 0 && long.TryParse(rng[1].Trim(), out var e)) end = e;
            }
        }
        return (method, rawPath, start, end);
    }

    private void ServeFile(NetworkStream stream, bool headOnly, string urlPath, long rangeStart, long rangeEnd)
    {
        // Map to a file in _dir by BASENAME only (no directory traversal).
        var name = Path.GetFileName(urlPath.TrimStart('/'));
        if (string.IsNullOrEmpty(name)) { WriteStatus(stream, 404, "Not Found"); return; }
        var full = Path.Combine(_dir, name);
        if (!File.Exists(full)) { WriteStatus(stream, 404, "Not Found"); return; }

        byte[] data;
        try
        {
            // FileShare.ReadWrite: ffmpeg may still be writing/rotating files.
            using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            data = ms.ToArray();
        }
        catch (Exception) { WriteStatus(stream, 404, "Not Found"); return; }

        string ctype = name.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ? "application/vnd.apple.mpegurl"
                     : name.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ? "video/mp2t"
                     : "application/octet-stream";

        long total = data.Length;
        long from = rangeStart < 0 ? 0 : rangeStart;
        long to = (rangeEnd < 0 || rangeEnd >= total) ? total - 1 : rangeEnd;
        bool partial = rangeStart > 0 || rangeEnd >= 0;
        if (from > to || from >= total) { WriteStatus(stream, 416, "Range Not Satisfiable"); return; }
        long len = to - from + 1;

        var head = new StringBuilder();
        head.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
        head.Append("Content-Type: ").Append(ctype).Append("\r\n");
        head.Append("Content-Length: ").Append(len).Append("\r\n");
        head.Append("Accept-Ranges: bytes\r\n");
        if (partial) head.Append("Content-Range: bytes ").Append(from).Append('-').Append(to).Append('/').Append(total).Append("\r\n");
        head.Append("Cache-Control: no-cache\r\n");
        head.Append("Connection: close\r\n\r\n");
        var headBytes = Encoding.ASCII.GetBytes(head.ToString());
        stream.Write(headBytes, 0, headBytes.Length);
        if (!headOnly) stream.Write(data, (int)from, (int)len);
        stream.Flush();
    }

    private static void WriteStatus(NetworkStream stream, int code, string reason)
    {
        var msg = $"HTTP/1.1 {code} {reason}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        var bytes = Encoding.ASCII.GetBytes(msg);
        try { stream.Write(bytes, 0, bytes.Length); stream.Flush(); } catch (Exception) { }
    }
}
