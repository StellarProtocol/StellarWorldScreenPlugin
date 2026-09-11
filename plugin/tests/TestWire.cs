using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using Stellar.WorldScreen.Net;

namespace Stellar.WorldScreen.Tests;

/// <summary>
/// Test-only H→P wire writer, hand-rolled directly against docs/protocol.md rather than reusing any
/// of <see cref="WireCodec"/>/<see cref="WireReader"/>'s own machinery — so a bug shared between an
/// encoder and its own test fixture can't hide a break in <see cref="WireReader"/>'s decoding.
/// </summary>
internal static class TestWire
{
    /// <summary>Writes STREAM_INFO (0x02): `u16 w`, `u16 h`, `u8 pixfmt`, `u8 fps`, `u16 titleLen`, UTF-8 title.</summary>
    public static void WriteStreamInfo(Stream s, ushort w, ushort h, byte pixfmt, byte fps, string title)
    {
        var titleBytes = Encoding.UTF8.GetBytes(title);
        var body = new byte[1 + 2 + 2 + 1 + 1 + 2 + titleBytes.Length];
        var pos = 0;
        body[pos++] = (byte)WireType.StreamInfo;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(pos, 2), w); pos += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(pos, 2), h); pos += 2;
        body[pos++] = pixfmt;
        body[pos++] = fps;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(pos, 2), (ushort)titleBytes.Length); pos += 2;
        Buffer.BlockCopy(titleBytes, 0, body, pos, titleBytes.Length);
        WriteEnvelope(s, body);
    }

    /// <summary>Writes FRAME (0x03): `u64 ptsMs`, then the raw pixel bytes.</summary>
    public static void WriteFrame(Stream s, long ptsMs, byte[] pixels)
    {
        var body = new byte[1 + 8 + pixels.Length];
        var pos = 0;
        body[pos++] = (byte)WireType.Frame;
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(pos, 8), unchecked((ulong)ptsMs)); pos += 8;
        Buffer.BlockCopy(pixels, 0, body, pos, pixels.Length);
        WriteEnvelope(s, body);
    }

    /// <summary>Writes STATUS (0x04): `u8 state`, `u64 positionMs`, `u64 durationMs`, `u16 errLen`, UTF-8 err.</summary>
    public static void WriteStatus(Stream s, byte state, long pos, long dur, string err)
    {
        var errBytes = Encoding.UTF8.GetBytes(err);
        var body = new byte[1 + 1 + 8 + 8 + 2 + errBytes.Length];
        var i = 0;
        body[i++] = (byte)WireType.Status;
        body[i++] = state;
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(i, 8), unchecked((ulong)pos)); i += 8;
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(i, 8), unchecked((ulong)dur)); i += 8;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(i, 2), (ushort)errBytes.Length); i += 2;
        Buffer.BlockCopy(errBytes, 0, body, i, errBytes.Length);
        WriteEnvelope(s, body);
    }

    /// <summary>Wraps a message body (`type` byte + payload) in the `[u32 len_le]` envelope.</summary>
    private static void WriteEnvelope(Stream s, byte[] body)
    {
        Span<byte> lenBuf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(lenBuf, (uint)body.Length);
        s.Write(lenBuf);
        s.Write(body);
    }
}
