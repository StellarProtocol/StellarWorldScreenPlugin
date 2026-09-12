// Background-thread TCP client for the plugin<->helper connection (docs/protocol.md).
//
// NOT unit-tested here — a real socket/thread integration is covered by the in-game slice (task A9).
// May reference System.* only — no UnityEngine. Runs entirely on its own background thread; the plugin
// marshals event callbacks onto Unity's main thread via IFramework.Post.
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using Stellar.WorldScreen.Screen;

namespace Stellar.WorldScreen.Net;

/// <summary>
/// Connects to the stellar-castbox helper over localhost TCP, sends HELLO, then decodes H→P messages via
/// <see cref="WireReader"/>: FRAME copies into the shared <see cref="FrameSink"/>; STREAM_INFO/STATUS
/// raise events. One background thread owns the connect/HELLO/read lifecycle and retries with a bounded
/// backoff on any failure until <see cref="Dispose"/>. TCP is full-duplex, so <see cref="Send"/> writes
/// directly from the caller's thread under a write-lock rather than queuing onto the read thread — see
/// the type-level remarks on <c>_writeLock</c>/<c>_writeStream</c> below.
/// </summary>
public sealed class HelperClient : IDisposable
{
    private const int InitialBackoffMs = 1000;
    private const int MaxBackoffMs = 5000;

    private readonly FrameSink _sink;

    // Guards _writeStream: Send() (any caller thread) and the bg thread's HELLO write both take this
    // lock before touching the stream, so a write from either side is never interleaved with the other.
    // TCP allows one concurrent read and one concurrent write on the same NetworkStream, so this lock
    // only ever contends with itself — never with the (unlocked) bg read loop.
    private readonly object _writeLock = new();
    private NetworkStream? _writeStream; // guarded by _writeLock; null whenever not connected

    private Thread? _thread;
    private volatile bool _disposed;
    private volatile TcpClient? _activeClient;

    /// <summary>Raised on the background thread when a STREAM_INFO message is decoded.</summary>
    public event Action<StreamInfoMsg>? OnStreamInfo;

    /// <summary>Raised on the background thread when a STATUS message is decoded.</summary>
    public event Action<StatusMsg>? OnStatus;

    /// <summary>Raised on the background thread right after the TCP connection succeeds.</summary>
    public event Action? OnConnected;

    /// <summary>Raised on the background thread when the connection ends (error or clean EOF).</summary>
    public event Action? OnDisconnected;

    public HelperClient(FrameSink sink)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    /// <summary>Spawns the background connect/read thread. Call once; a second call is ignored.</summary>
    public void Start(string host, int port)
    {
        if (_thread != null) return;
        _thread = new Thread(() => RunLoop(host, port))
        {
            IsBackground = true,
            Name = "StellarWorldScreen.HelperClient",
        };
        _thread.Start();
    }

    /// <summary>
    /// Encodes and writes a CONTROL message directly to the live connection under <c>_writeLock</c>. Safe
    /// to call from any thread concurrently with the background read loop (full-duplex TCP). If not
    /// currently connected, or the write itself fails (e.g. the peer just dropped), the message is
    /// dropped — logged, never thrown on the caller's thread and never queued for a later connection.
    /// </summary>
    public void Send(ControlOp op, ulong seekMs = 0, byte volume = 0, string? url = null)
    {
        if (_disposed) return;
        byte[] encoded;
        try
        {
            encoded = WireCodec.EncodeControl(op, seekMs, volume, url);
        }
        catch (ArgumentOutOfRangeException)
        {
            return; // unknown/malformed op — drop rather than throw on the caller's thread
        }

        lock (_writeLock)
        {
            if (_writeStream == null)
            {
                Trace.WriteLine($"[HelperClient] Send({op}): dropped — not connected");
                return;
            }

            try
            {
                _writeStream.Write(encoded, 0, encoded.Length);
            }
            catch (Exception ex)
            {
                // Peer dropped the connection between the null-check and this write — drop, never throw.
                // The read loop will independently observe the same disconnect and raise OnDisconnected.
                _writeStream = null;
                Trace.WriteLine($"[HelperClient] Send({op}): dropped — write failed: {ex.Message}");
            }
        }
    }

    /// <summary>Stops the background thread and closes the socket. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearWriteStream();
        // Closing the socket aborts an in-progress blocking Connect/Read on the bg thread (localhost
        // connect is effectively instant-or-refused, so there's no realistic long-running Connect to
        // race here); Join(2s) bounds how long the disposer can be stalled waiting for that to unwind.
        CloseActiveSocket();
        _thread?.Join(TimeSpan.FromSeconds(2));
    }

    private void RunLoop(string host, int port)
    {
        var backoffMs = InitialBackoffMs;
        while (!_disposed)
        {
            var connected = RunOneConnection(host, port);
            if (_disposed) return;

            OnDisconnected?.Invoke();
            backoffMs = connected ? InitialBackoffMs : Math.Min(backoffMs * 2, MaxBackoffMs);
            SleepBackoff(backoffMs);
        }
    }

    /// <summary>
    /// Connects once and runs the blocking read loop until failure, clean EOF, or disposal. Never throws —
    /// every failure (connect refused, HELLO write error, socket error, malformed message) is caught and
    /// treated as "disconnected, retry later". Returns whether a connection was actually established.
    /// </summary>
    private bool RunOneConnection(string host, int port)
    {
        var connected = false;
        try
        {
            using var client = new TcpClient();
            _activeClient = client;
            client.Connect(host, port);
            if (_disposed) return false;

            connected = true;
            OnConnected?.Invoke();

            var stream = client.GetStream();
            lock (_writeLock)
            {
                // Same _writeLock path Send() uses: write HELLO, then publish the stream so Send() can
                // never race ahead of the handshake (_writeStream is null until this line runs).
                stream.Write(WireCodec.EncodeHello(1, 0));
                _writeStream = stream;
            }

            ReadLoop(stream);
        }
        catch (Exception)
        {
            // Connect failure / HELLO write failure / socket error / protocol error — uniformly
            // "disconnected, retry later".
        }
        finally
        {
            ClearWriteStream();
            CloseActiveSocket();
        }

        return connected;
    }

    /// <summary>
    /// Blocking read loop: no <see cref="NetworkStream.ReadTimeout"/> is set, so a slow/chunked sender
    /// never causes a mid-message timeout that would desync <see cref="WireReader"/>'s framing. The only
    /// ways out are a clean EOF/partial-at-disconnect (<see cref="WireReader.TryReadMessage"/> returns
    /// <c>false</c>) or an exception (e.g. <see cref="Dispose"/> closing the socket), both handled by the
    /// caller.
    /// </summary>
    private void ReadLoop(NetworkStream stream)
    {
        var reader = new WireReader();
        ushort lastW = 0;
        ushort lastH = 0;
        byte lastPixfmt = 0;

        while (!_disposed)
        {
            if (!reader.TryReadMessage(stream, out var msg)) return; // EOF/disconnect — outer loop retries

            Dispatch(msg, ref lastW, ref lastH, ref lastPixfmt);
        }
    }

    private void Dispatch(in WireMessage msg, ref ushort lastW, ref ushort lastH, ref byte lastPixfmt)
    {
        switch (msg.Type)
        {
            case WireType.StreamInfo:
                lastW = msg.StreamInfo.W;
                lastH = msg.StreamInfo.H;
                lastPixfmt = msg.StreamInfo.Pixfmt;
                OnStreamInfo?.Invoke(msg.StreamInfo);
                break;
            case WireType.Frame:
                _sink.Submit(lastW, lastH, lastPixfmt, msg.Frame.Pixels);
                break;
            case WireType.Status:
                OnStatus?.Invoke(msg.Status);
                break;
            default:
                break; // ignore any other/unexpected H→P type — never crash the read thread on it
        }
    }

    private void ClearWriteStream()
    {
        lock (_writeLock)
        {
            _writeStream = null;
        }
    }

    private void CloseActiveSocket()
    {
        var client = _activeClient;
        _activeClient = null;
        try
        {
            client?.Close();
        }
        catch (Exception)
        {
            // already closing/closed — nothing to do
        }
    }

    private void SleepBackoff(int ms)
    {
        const int step = 100;
        var elapsed = 0;
        while (elapsed < ms && !_disposed)
        {
            Thread.Sleep(step);
            elapsed += step;
        }
    }
}
