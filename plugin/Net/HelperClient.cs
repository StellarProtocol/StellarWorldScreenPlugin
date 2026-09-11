// Background-thread TCP client for the plugin<->helper connection (docs/protocol.md).
//
// NOT unit-tested here — a real socket/thread integration is covered by the in-game slice (task A9).
// May reference System.* only — no UnityEngine. Runs entirely on its own background thread; the plugin
// marshals event callbacks onto Unity's main thread via IFramework.Post.
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using Stellar.WorldScreen.Screen;

namespace Stellar.WorldScreen.Net;

/// <summary>
/// Connects to the stellar-castbox helper over localhost TCP, sends HELLO, then decodes H→P messages via
/// <see cref="WireReader"/>: FRAME copies into the shared <see cref="FrameSink"/>; STREAM_INFO/STATUS
/// raise events. One background thread owns the whole lifecycle (connect, HELLO, read, queued CONTROL
/// sends) and retries with a bounded backoff on any failure until <see cref="Dispose"/>.
/// </summary>
public sealed class HelperClient : IDisposable
{
    private const int InitialBackoffMs = 1000;
    private const int MaxBackoffMs = 5000;
    private const int ReadPollTimeoutMs = 200;

    private readonly FrameSink _sink;
    private readonly ConcurrentQueue<byte[]> _outbox = new();

    private Thread? _thread;
    private volatile bool _disposed;
    private TcpClient? _activeClient;

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

    /// <summary>Spawns the background connect/read/write thread. Call once; a second call is ignored.</summary>
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
    /// Enqueues a CONTROL message for the background thread to send on its next loop iteration. Safe to
    /// call from any thread concurrently with the read loop. A control that races a disconnect (queued
    /// but never sent before the socket drops) is silently dropped, never thrown.
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

        _outbox.Enqueue(encoded);
    }

    /// <summary>Stops the background thread and closes the socket. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
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
    /// Connects once and runs the send/receive loop until failure or disposal. Never throws — every
    /// failure (connect refused, socket error, malformed message) is caught and treated as "disconnected,
    /// retry later". Returns whether a connection was actually established.
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
            stream.ReadTimeout = ReadPollTimeoutMs;
            stream.Write(WireCodec.EncodeHello(1, 0));

            ReadLoop(client, stream);
        }
        catch (Exception)
        {
            // Connect failure / socket error / protocol error — uniformly "disconnected, retry later".
        }
        finally
        {
            CloseActiveSocket();
        }

        return connected;
    }

    private void ReadLoop(TcpClient client, NetworkStream stream)
    {
        var reader = new WireReader();
        ushort lastW = 0;
        ushort lastH = 0;
        byte lastPixfmt = 0;

        while (!_disposed)
        {
            DrainOutbox(stream);

            var outcome = TryReadOneMessage(reader, stream, out var msg);
            if (outcome == ReadOutcome.Timeout) continue; // poll tick — service the outbox again
            if (outcome == ReadOutcome.Eof) return; // disconnect — outer loop retries with backoff

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

    private enum ReadOutcome
    {
        Message,
        Timeout,
        Eof,
    }

    /// <summary>
    /// Wraps <see cref="WireReader.TryReadMessage"/> so a read-timeout poll tick (expected —
    /// <see cref="NetworkStream.ReadTimeout"/> is set so the loop can also service the outbox while
    /// idle) is distinguished from a real EOF/disconnect.
    /// </summary>
    private static ReadOutcome TryReadOneMessage(WireReader reader, NetworkStream stream, out WireMessage msg)
    {
        try
        {
            return reader.TryReadMessage(stream, out msg) ? ReadOutcome.Message : ReadOutcome.Eof;
        }
        catch (IOException io) when (IsTimeout(io))
        {
            msg = default;
            return ReadOutcome.Timeout;
        }
    }

    private static bool IsTimeout(IOException io) =>
        io.InnerException is SocketException se && se.SocketErrorCode == SocketError.TimedOut;

    private void DrainOutbox(NetworkStream stream)
    {
        while (_outbox.TryDequeue(out var encoded))
        {
            stream.Write(encoded, 0, encoded.Length);
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
