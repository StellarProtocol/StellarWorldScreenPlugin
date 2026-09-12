// Wire codec for the plugin <-> helper protocol — the C# half.
//
// Mirrors `docs/protocol.md` (and its Rust twin, `helper/src/wire.rs`) byte-for-byte. Any field change
// updates both in the same commit. Little-endian throughout. Envelope: `[u32 len_le][u8 type][payload...]`
// where `len` counts the bytes of `(type + payload)`.
//
// PURE BCL — System.* only. No UnityEngine, no Stellar.Abstractions types — this file is compiled
// directly into the off-game unit-test project (see tests/Stellar.WorldScreen.Tests.csproj), so it must
// build and run without the game or the framework present.
using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Stellar.WorldScreen.Net;

/// <summary>Message type byte — docs/protocol.md table. Mirrors the Rust helper's TYPE_* consts.</summary>
public enum WireType : byte
{
    Hello = 0x01,
    StreamInfo = 0x02,
    Frame = 0x03,
    Status = 0x04,
    Control = 0x05,
    Audio = 0x06,
}

/// <summary>CONTROL (0x05) op byte — docs/protocol.md § CONTROL ops.</summary>
public enum ControlOp : byte
{
    Play = 0,
    Pause = 1,
    Stop = 2,
    Seek = 3,
    Volume = 4,
    Load = 5,
}

/// <summary>STREAM_INFO (0x02) payload, decoded from the helper.</summary>
public readonly struct StreamInfoMsg
{
    public readonly ushort W;
    public readonly ushort H;
    public readonly byte Pixfmt;
    public readonly byte Fps;
    public readonly string Title;

    public StreamInfoMsg(ushort w, ushort h, byte pixfmt, byte fps, string title)
    {
        W = w;
        H = h;
        Pixfmt = pixfmt;
        Fps = fps;
        Title = title;
    }
}

/// <summary>
/// FRAME (0x03) payload, decoded from the helper. <see cref="Pixels"/> is an <see cref="ArraySegment{T}"/>
/// over <see cref="WireReader"/>'s internally reused buffer — see the contract note on
/// <see cref="WireReader.TryReadMessage"/> before holding onto it past the next read.
/// </summary>
public readonly struct FrameMsg
{
    public readonly long PtsMs;
    public readonly ArraySegment<byte> Pixels;

    public FrameMsg(long ptsMs, ArraySegment<byte> pixels)
    {
        PtsMs = ptsMs;
        Pixels = pixels;
    }
}

/// <summary>STATUS (0x04) payload, decoded from the helper.</summary>
public readonly struct StatusMsg
{
    public readonly byte State;
    public readonly long PositionMs;
    public readonly long DurationMs;
    public readonly string Err;

    public StatusMsg(byte state, long positionMs, long durationMs, string err)
    {
        State = state;
        PositionMs = positionMs;
        DurationMs = durationMs;
        Err = err;
    }
}

/// <summary>
/// AUDIO (0x06) payload: interleaved S16LE PCM, 48000 Hz, 2 channels. <see cref="Pcm"/> is an
/// <see cref="ArraySegment{T}"/> over <see cref="WireReader"/>'s reused buffer — consume/copy it before
/// the next read (same contract as <see cref="FrameMsg.Pixels"/>).
/// </summary>
public readonly struct AudioMsg
{
    public readonly ArraySegment<byte> Pcm;
    public AudioMsg(ArraySegment<byte> pcm) => Pcm = pcm;
}

/// <summary>
/// One decoded message from the helper. Only the field matching <see cref="Type"/> is meaningful; the
/// other two carry their default value. Built exclusively via <see cref="WireReader"/>'s internal
/// factories — plugin code only ever receives one as an <c>out</c> parameter of
/// <see cref="WireReader.TryReadMessage"/>.
/// </summary>
public readonly struct WireMessage
{
    public readonly WireType Type;
    public readonly StreamInfoMsg StreamInfo;
    public readonly FrameMsg Frame;
    public readonly StatusMsg Status;
    public readonly AudioMsg Audio;

    private WireMessage(WireType type, StreamInfoMsg streamInfo, FrameMsg frame, StatusMsg status, AudioMsg audio)
    {
        Type = type;
        StreamInfo = streamInfo;
        Frame = frame;
        Status = status;
        Audio = audio;
    }

    internal static WireMessage OfStreamInfo(StreamInfoMsg info) => new(WireType.StreamInfo, info, default, default, default);
    internal static WireMessage OfFrame(FrameMsg frame) => new(WireType.Frame, default, frame, default, default);
    internal static WireMessage OfStatus(StatusMsg status) => new(WireType.Status, default, default, status, default);
    internal static WireMessage OfAudio(AudioMsg audio) => new(WireType.Audio, default, default, default, audio);
    internal static WireMessage OfBareType(WireType type) => new(type, default, default, default, default);
}

/// <summary>
/// Stateless encoder for the messages the plugin SENDS to the helper (HELLO, CONTROL — the P→H
/// direction of docs/protocol.md). Decoding the H→P direction (STREAM_INFO/FRAME/STATUS) lives on
/// <see cref="WireReader"/> instead, which owns the per-connection reused read buffer — keeping that
/// mutable state off this static class avoids a hidden global buffer.
/// </summary>
public static class WireCodec
{
    /// <summary>
    /// Mirrors the Rust helper's <c>wire::MAX_MSG_LEN</c>. A receiver-side sanity cap on the `[u32 len]`
    /// envelope prefix — not part of the wire format — so a corrupt/hostile length prefix can't trigger
    /// an unbounded allocation. See docs/protocol.md § Constants.
    /// </summary>
    public const int MaxMsgLen = 64 * 1024 * 1024;

    /// <summary>Encodes HELLO (0x01): `u16 protoVersion`, `u8 flags`.</summary>
    public static byte[] EncodeHello(ushort protoVersion, byte flags)
    {
        var body = new byte[1 + 2 + 1];
        body[0] = (byte)WireType.Hello;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1, 2), protoVersion);
        body[3] = flags;
        return Envelope(body);
    }

    /// <summary>Encodes CONTROL (0x05): `u8 op`, then that op's own payload (docs/protocol.md § CONTROL ops).</summary>
    public static byte[] EncodeControl(ControlOp op, ulong seekMs = 0, byte volume = 0, string? url = null)
    {
        using var buf = new MemoryStream();
        buf.WriteByte((byte)WireType.Control);
        buf.WriteByte((byte)op);
        WriteControlPayload(buf, op, seekMs, volume, url);
        return Envelope(buf.ToArray());
    }

    private static void WriteControlPayload(Stream s, ControlOp op, ulong seekMs, byte volume, string? url)
    {
        switch (op)
        {
            case ControlOp.Play:
            case ControlOp.Pause:
            case ControlOp.Stop:
                break;
            case ControlOp.Seek:
                WriteU64(s, seekMs);
                break;
            case ControlOp.Volume:
                s.WriteByte(volume);
                break;
            case ControlOp.Load:
                WriteStr16(s, url ?? string.Empty);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(op), op, "unknown control op");
        }
    }

    /// <summary>Wraps a message body (`type` byte + payload) in the `[u32 len_le]` envelope.</summary>
    private static byte[] Envelope(byte[] body)
    {
        var outBuf = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(outBuf, (uint)body.Length);
        Buffer.BlockCopy(body, 0, outBuf, 4, body.Length);
        return outBuf;
    }

    private static void WriteU64(Stream s, ulong v)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, v);
        s.Write(b);
    }

    private static void WriteStr16(Stream s, string str)
    {
        var bytes = Encoding.UTF8.GetBytes(str);
        if (bytes.Length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(str), "string exceeds u16 length prefix");
        Span<byte> lenBuf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(lenBuf, (ushort)bytes.Length);
        s.Write(lenBuf);
        s.Write(bytes);
    }
}

/// <summary>
/// Stateful decoder for the messages the plugin RECEIVES from the helper (STREAM_INFO, FRAME, STATUS —
/// the H→P direction of docs/protocol.md). Not thread-safe; use one instance per connection.
/// </summary>
/// <remarks>
/// <see cref="TryReadMessage"/> reuses an internal byte buffer across calls to avoid a per-frame
/// allocation on the hot FRAME path. A returned <see cref="WireMessage"/>'s <c>Frame.Pixels</c> is an
/// <see cref="ArraySegment{T}"/> over THAT buffer — the caller MUST fully consume or copy it before the
/// next <see cref="TryReadMessage"/> call, since that call may resize or overwrite the underlying array.
/// </remarks>
public sealed class WireReader
{
    private byte[] _buffer = Array.Empty<byte>();

    /// <summary>
    /// Reads one framed message: a 4-byte LE length, then exactly that many bytes. Returns
    /// <c>false</c> on a clean EOF before any byte of the next message, or a partial read (the stream
    /// ended mid-message) — both mean "no complete message yet", not an error. Throws
    /// <see cref="InvalidDataException"/> on a malformed body: a length prefix over
    /// <see cref="WireCodec.MaxMsgLen"/> (rejected BEFORE allocating a buffer for the body) or an
    /// unrecognized type byte.
    /// </summary>
    public bool TryReadMessage(Stream s, out WireMessage msg)
    {
        msg = default;
        Span<byte> lenBuf = stackalloc byte[4];
        if (!TryFillExact(s, lenBuf)) return false;

        var len = BinaryPrimitives.ReadUInt32LittleEndian(lenBuf);
        if (len > WireCodec.MaxMsgLen)
            throw new InvalidDataException($"message length {len} exceeds MaxMsgLen ({WireCodec.MaxMsgLen})");
        if (len == 0)
            throw new InvalidDataException("message body is empty (missing type byte)");

        EnsureCapacity((int)len);
        var body = _buffer.AsSpan(0, (int)len);
        if (!TryFillExact(s, body)) return false;

        msg = ParseBody(_buffer, (int)len);
        return true;
    }

    private void EnsureCapacity(int size)
    {
        if (_buffer.Length < size) _buffer = new byte[size];
    }

    /// <summary>
    /// Fills <paramref name="buf"/> completely from <paramref name="s"/>, looping over short reads.
    /// Returns <c>false</c> if the stream ends before <paramref name="buf"/> is full.
    /// </summary>
    private static bool TryFillExact(Stream s, Span<byte> buf)
    {
        var total = 0;
        while (total < buf.Length)
        {
            var n = s.Read(buf.Slice(total));
            if (n == 0) return false;
            total += n;
        }
        return true;
    }

    private static WireMessage ParseBody(byte[] buf, int len)
    {
        var type = (WireType)buf[0];
        var pos = 1;
        return type switch
        {
            WireType.StreamInfo => WireMessage.OfStreamInfo(ParseStreamInfo(buf, len, ref pos)),
            WireType.Frame => WireMessage.OfFrame(ParseFrame(buf, len, ref pos)),
            WireType.Status => WireMessage.OfStatus(ParseStatus(buf, len, ref pos)),
            WireType.Audio => WireMessage.OfAudio(new AudioMsg(new ArraySegment<byte>(buf, pos, len - pos))),
            WireType.Hello or WireType.Control => WireMessage.OfBareType(type),
            _ => throw new InvalidDataException($"unknown message type 0x{(byte)type:x2}"),
        };
    }

    private static StreamInfoMsg ParseStreamInfo(byte[] buf, int len, ref int pos)
    {
        var w = ReadU16(buf, len, ref pos);
        var h = ReadU16(buf, len, ref pos);
        var pixfmt = ReadU8(buf, len, ref pos);
        var fps = ReadU8(buf, len, ref pos);
        var title = ReadStr16(buf, len, ref pos);
        return new StreamInfoMsg(w, h, pixfmt, fps, title);
    }

    private static FrameMsg ParseFrame(byte[] buf, int len, ref int pos)
    {
        var pts = unchecked((long)ReadU64(buf, len, ref pos));
        var pixels = new ArraySegment<byte>(buf, pos, len - pos);
        pos = len;
        return new FrameMsg(pts, pixels);
    }

    private static StatusMsg ParseStatus(byte[] buf, int len, ref int pos)
    {
        var state = ReadU8(buf, len, ref pos);
        var position = unchecked((long)ReadU64(buf, len, ref pos));
        var duration = unchecked((long)ReadU64(buf, len, ref pos));
        var err = ReadStr16(buf, len, ref pos);
        return new StatusMsg(state, position, duration, err);
    }

    private static void Need(int len, int pos, int n)
    {
        if (pos + n > len) throw new InvalidDataException("message body too short");
    }

    private static byte ReadU8(byte[] buf, int len, ref int pos)
    {
        Need(len, pos, 1);
        return buf[pos++];
    }

    private static ushort ReadU16(byte[] buf, int len, ref int pos)
    {
        Need(len, pos, 2);
        var v = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(pos, 2));
        pos += 2;
        return v;
    }

    private static ulong ReadU64(byte[] buf, int len, ref int pos)
    {
        Need(len, pos, 8);
        var v = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(pos, 8));
        pos += 8;
        return v;
    }

    private static string ReadStr16(byte[] buf, int len, ref int pos)
    {
        var strLen = ReadU16(buf, len, ref pos);
        Need(len, pos, strLen);
        var s = Encoding.UTF8.GetString(buf, pos, strLen);
        pos += strLen;
        return s;
    }
}
