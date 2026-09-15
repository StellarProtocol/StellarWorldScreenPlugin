// Serves ONE growing file over loopback HTTP with byte-range support and a caller-supplied TOTAL size, so
// AVPro (MediaFoundation) can stream a fragmented MP4 that ffmpeg is still writing. This is the SAME
// progressive remote-mp4 path MF uses for a normal http mp4 — the one path that decodes with correct colour
// in-game (HLS, both mpegts and fMP4, hit Proton MF bugs). A range read that lands past what ffmpeg has
// written waits briefly for more. BCL only — plain TcpListener sockets (no http.sys).
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Stellar.WorldScreen.Net;

/// <summary>Loopback HTTP server for a single growing file with a fixed advertised total size. One request per
/// connection (Connection: close); a thread per client. The total is set slightly UNDER the real final size
/// (sample bytes only, no container overhead) so MF never requests past the real end — it stops cleanly a
/// few KB early instead of hanging waiting for bytes that never come.</summary>
public sealed class ProgressiveServer
{
    private const int WaitForDataTimeoutMs = 30000; // give up waiting for ffmpeg to reach an offset after this
    private readonly string _filePath;
    private readonly long _totalSize;
    private readonly TcpListener _listener;
    private volatile bool _running;

    public int Port { get; }

    public ProgressiveServer(string filePath, long totalSize)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _totalSize = totalSize;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public void Start()
    {
        _running = true;
        var t = new Thread(AcceptLoop) { IsBackground = true, Name = "StellarWorldScreen.ProgServer" };
        t.Start();
    }

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
            catch (Exception) { break; }
            var t = new Thread(() => Handle(client)) { IsBackground = true };
            t.Start();
        }
    }

    private void Handle(TcpClient client)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var (method, start, end, ok) = ReadRequest(stream);
                if (!ok) return;
                Serve(stream, method == "HEAD", start, end);
            }
        }
        catch (Exception) { /* client went away */ }
    }

    private static (string method, long start, long end, bool ok) ReadRequest(NetworkStream stream)
    {
        var sb = new StringBuilder();
        int b, prevNl = 0;
        while (sb.Length < 8192 && (b = stream.ReadByte()) != -1)
        {
            sb.Append((char)b);
            if (b == '\n') { if (prevNl == 1) break; prevNl = 1; }
            else if (b != '\r') prevNl = 0;
        }
        var lines = sb.ToString().Split('\n');
        if (lines.Length == 0) return ("", 0, -1, false);
        var parts = lines[0].Split(' ');
        if (parts.Length < 2) return ("", 0, -1, false);
        long start = 0, end = -1;
        foreach (var line in lines)
        {
            if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
            {
                var v = line.Substring(6).Trim();
                int eq = v.IndexOf('=');
                if (eq >= 0) v = v.Substring(eq + 1);
                var rng = v.Split('-');
                if (rng.Length >= 1) long.TryParse(rng[0].Trim(), out start);
                if (rng.Length >= 2 && rng[1].Trim().Length > 0 && long.TryParse(rng[1].Trim(), out var e)) end = e;
            }
        }
        return (parts[0], start, end, true);
    }

    private void Serve(NetworkStream stream, bool headOnly, long rangeStart, long rangeEnd)
    {
        long from = rangeStart < 0 ? 0 : rangeStart;
        long to = rangeEnd < 0 ? _totalSize - 1 : Math.Min(rangeEnd, _totalSize - 1);
        bool partial = rangeStart > 0 || rangeEnd >= 0;
        if (from >= _totalSize || from > to) { WriteStatus(stream, 416, "Range Not Satisfiable"); return; }
        long len = to - from + 1;

        var head = new StringBuilder();
        head.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
        head.Append("Content-Type: video/mp4\r\n");
        head.Append("Content-Length: ").Append(len).Append("\r\n");
        head.Append("Accept-Ranges: bytes\r\n");
        if (partial) head.Append("Content-Range: bytes ").Append(from).Append('-').Append(to).Append('/').Append(_totalSize).Append("\r\n");
        head.Append("Cache-Control: no-cache\r\n");
        head.Append("Connection: close\r\n\r\n");
        var hb = Encoding.ASCII.GetBytes(head.ToString());
        stream.Write(hb, 0, hb.Length);
        if (headOnly) { stream.Flush(); return; }

        // Stream [from, to] out of the growing file, waiting for ffmpeg to write past our read position.
        try
        {
            using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[64 * 1024];
            long pos = from;
            while (pos <= to && _running)
            {
                long avail = fs.Length; // current EOF (grows as ffmpeg appends)
                if (pos >= avail)
                {
                    if (!WaitForData(fs, pos)) break; // ffmpeg stopped short of here → end the response
                    continue;
                }
                long readEnd = Math.Min(to, avail - 1);
                fs.Seek(pos, SeekOrigin.Begin);
                int want = (int)Math.Min(buf.Length, readEnd - pos + 1);
                int n = fs.Read(buf, 0, want);
                if (n <= 0) { if (!WaitForData(fs, pos)) break; continue; }
                stream.Write(buf, 0, n);
                pos += n;
            }
            stream.Flush();
        }
        catch (Exception) { /* file gone / client closed */ }
    }

    private bool WaitForData(FileStream fs, long needBeyond)
    {
        int waited = 0;
        while (_running && waited < WaitForDataTimeoutMs)
        {
            if (fs.Length > needBeyond) return true;
            Thread.Sleep(20);
            waited += 20;
        }
        return false;
    }

    private static void WriteStatus(NetworkStream stream, int code, string reason)
    {
        var msg = $"HTTP/1.1 {code} {reason}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        var bytes = Encoding.ASCII.GetBytes(msg);
        try { stream.Write(bytes, 0, bytes.Length); stream.Flush(); } catch (Exception) { }
    }
}
