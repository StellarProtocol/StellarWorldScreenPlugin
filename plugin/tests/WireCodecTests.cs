using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using Stellar.WorldScreen.Net;
using Xunit;

namespace Stellar.WorldScreen.Tests;

public class WireCodecTests
{
    [Fact]
    public void Hello_len_prefix_covers_type_plus_payload()
    {
        var b = WireCodec.EncodeHello(1, 0);
        int len = BinaryPrimitives.ReadInt32LittleEndian(b);
        Assert.Equal(b.Length - 4, len);
        Assert.Equal((byte)0x01, b[4]);
    }

    [Fact]
    public void Hello_payload_is_proto_version_then_flags()
    {
        var b = WireCodec.EncodeHello(1, 7);
        // [len(4)][type(1)][protoVersion u16 LE(2)][flags u8(1)]
        Assert.Equal(8, b.Length);
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(5, 2)));
        Assert.Equal((byte)7, b[7]);
    }

    [Fact]
    public void Reads_stream_info_then_frame()
    {
        var ms = new MemoryStream();
        TestWire.WriteStreamInfo(ms, 640, 360, 0, 30, "clip");
        TestWire.WriteFrame(ms, 5, new byte[640 * 360 * 4]);
        ms.Position = 0;
        var r = new WireReader(); // or WireCodec.TryReadMessage if you kept it static
        Assert.True(r.TryReadMessage(ms, out var a)); Assert.Equal(WireType.StreamInfo, a.Type);
        Assert.Equal(640, a.StreamInfo.W); Assert.Equal("clip", a.StreamInfo.Title);
        Assert.True(r.TryReadMessage(ms, out var b)); Assert.Equal(WireType.Frame, b.Type);
        Assert.Equal(5, b.Frame.PtsMs); Assert.Equal(640 * 360 * 4, b.Frame.Pixels.Count);
    }

    [Theory]
    [InlineData(ControlOp.Play, (byte)0)]
    [InlineData(ControlOp.Pause, (byte)1)]
    [InlineData(ControlOp.Stop, (byte)2)]
    public void EncodeControl_bare_ops_carry_only_the_op_byte(ControlOp op, byte expectedOpByte)
    {
        var b = WireCodec.EncodeControl(op);
        int len = BinaryPrimitives.ReadInt32LittleEndian(b);
        Assert.Equal(2, len); // type + op byte, no payload
        Assert.Equal((byte)0x05, b[4]);
        Assert.Equal(expectedOpByte, b[5]);
        Assert.Equal(6, b.Length);
    }

    [Fact]
    public void EncodeControl_seek_payload_is_u64_le_ms()
    {
        var b = WireCodec.EncodeControl(ControlOp.Seek, seekMs: 42_000);
        Assert.Equal((byte)0x05, b[4]);
        Assert.Equal((byte)3, b[5]); // Seek op
        var ms = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(6, 8));
        Assert.Equal(42_000UL, ms);
        Assert.Equal(4 + 1 + 1 + 8, b.Length);
    }

    [Fact]
    public void EncodeControl_volume_payload_is_one_byte()
    {
        var b = WireCodec.EncodeControl(ControlOp.Volume, volume: 80);
        Assert.Equal((byte)4, b[5]); // Volume op
        Assert.Equal((byte)80, b[6]);
        Assert.Equal(4 + 1 + 1 + 1, b.Length);
    }

    [Fact]
    public void EncodeControl_load_payload_is_u16_len_prefixed_utf8_url()
    {
        const string url = "http://x/y.mp4";
        var b = WireCodec.EncodeControl(ControlOp.Load, url: url);
        Assert.Equal((byte)5, b[5]); // Load op
        var urlLen = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(6, 2));
        Assert.Equal((ushort)Encoding.UTF8.GetByteCount(url), urlLen);
        var decoded = Encoding.UTF8.GetString(b, 8, urlLen);
        Assert.Equal(url, decoded);
        Assert.Equal(4 + 1 + 1 + 2 + urlLen, b.Length);
    }

    [Fact]
    public void Reads_status_roundtrip()
    {
        var ms = new MemoryStream();
        TestWire.WriteStatus(ms, 3, 5_000, 60_000, "decode failed");
        ms.Position = 0;
        var r = new WireReader();
        Assert.True(r.TryReadMessage(ms, out var msg));
        Assert.Equal(WireType.Status, msg.Type);
        Assert.Equal((byte)3, msg.Status.State);
        Assert.Equal(5_000, msg.Status.PositionMs);
        Assert.Equal(60_000, msg.Status.DurationMs);
        Assert.Equal("decode failed", msg.Status.Err);
    }

    [Fact]
    public void Partial_stream_returns_false_not_throw()
    {
        var full = new MemoryStream();
        TestWire.WriteStreamInfo(full, 640, 360, 0, 30, "clip");
        var fullBytes = full.ToArray();
        var truncated = new MemoryStream(fullBytes, 0, fullBytes.Length - 3); // cut mid-body
        var r = new WireReader();
        Assert.False(r.TryReadMessage(truncated, out _));
    }

    [Fact]
    public void Clean_eof_before_any_message_returns_false()
    {
        var r = new WireReader();
        Assert.False(r.TryReadMessage(new MemoryStream(), out _));
    }

    [Fact]
    public void Oversized_length_prefix_is_rejected_before_reading_body()
    {
        // Only the 4-byte len prefix, no body — if TryReadMessage allocated `len` bytes and tried to
        // read them before checking the cap, this would block/return false on EOF instead of throwing.
        var ms = new MemoryStream();
        Span<byte> lenBuf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(lenBuf, 0x7FFFFFFF);
        ms.Write(lenBuf);
        ms.Position = 0;
        var r = new WireReader();
        Assert.Throws<InvalidDataException>(() => r.TryReadMessage(ms, out _));
    }

    [Fact]
    public void Reused_buffer_survives_a_second_smaller_read()
    {
        // Guards the ArraySegment/reused-buffer contract: a big FRAME then a small STREAM_INFO must not
        // leak stale bytes from the bigger read into the smaller one's ArraySegment bounds.
        var ms = new MemoryStream();
        var pixels = new byte[1024];
        new Random(1).NextBytes(pixels);
        TestWire.WriteFrame(ms, 1, pixels);
        TestWire.WriteStreamInfo(ms, 4, 4, 0, 1, "x");
        ms.Position = 0;
        var r = new WireReader();
        Assert.True(r.TryReadMessage(ms, out var frame));
        var copy = frame.Frame.Pixels.ToArray();
        Assert.Equal(pixels, copy);
        Assert.True(r.TryReadMessage(ms, out var info));
        Assert.Equal(WireType.StreamInfo, info.Type);
        Assert.Equal("x", info.StreamInfo.Title);
    }
}
