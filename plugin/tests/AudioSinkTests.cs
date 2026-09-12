using System;
using Stellar.WorldScreen.Screen;
using Xunit;

namespace Stellar.WorldScreen.Tests;

public class AudioSinkTests
{
    // Encodes float samples in [-1,1) as interleaved little-endian S16, the wire's AUDIO payload format.
    private static byte[] S16(params float[] samples)
    {
        var b = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short s = (short)Math.Round(Math.Clamp(samples[i], -1f, 0.999969f) * 32768f);
            b[i * 2] = (byte)(s & 0xFF);
            b[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return b;
    }

    [Fact]
    public void Underrun_reads_silence()
    {
        var sink = new AudioSink(1024);
        var buf = new float[8];
        Array.Fill(buf, 0.5f);
        sink.ReadInto(buf);                       // nothing submitted yet
        Assert.All(buf, v => Assert.Equal(0f, v)); // filled with silence, not left dirty
    }

    [Fact]
    public void Submit_then_read_round_trips_samples_in_order()
    {
        var sink = new AudioSink(1024);
        sink.Submit(S16(0f, 0.5f, -0.5f, 0.25f));
        var buf = new float[4];
        sink.ReadInto(buf);
        Assert.Equal(0f, buf[0], 3);
        Assert.Equal(0.5f, buf[1], 3);
        Assert.Equal(-0.5f, buf[2], 3);
        Assert.Equal(0.25f, buf[3], 3);
    }

    [Fact]
    public void Partial_read_fills_available_then_silence()
    {
        var sink = new AudioSink(1024);
        sink.Submit(S16(0.5f, 0.5f)); // only 2 samples available
        var buf = new float[4];
        sink.ReadInto(buf);
        Assert.Equal(0.5f, buf[0], 3);
        Assert.Equal(0.5f, buf[1], 3);
        Assert.Equal(0f, buf[2]); // underrun tail
        Assert.Equal(0f, buf[3]);
    }

    [Fact]
    public void Reads_drain_in_fifo_order_across_calls()
    {
        var sink = new AudioSink(1024);
        sink.Submit(S16(0.1f, 0.2f, 0.3f, 0.4f));
        var a = new float[2];
        var b = new float[2];
        sink.ReadInto(a);
        sink.ReadInto(b);
        Assert.Equal(0.1f, a[0], 3);
        Assert.Equal(0.2f, a[1], 3);
        Assert.Equal(0.3f, b[0], 3);
        Assert.Equal(0.4f, b[1], 3);
    }

    [Fact]
    public void Overrun_drops_newest_and_keeps_latency_bounded()
    {
        // The ctor floors ring size at 1024 samples; submit past that to force an overrun.
        const int ring = 1024;
        var sink = new AudioSink(ring);
        var over = new float[ring + 6];
        Array.Fill(over, 0.5f);
        over[ring] = 0.9f; over[ring + 5] = 0.9f; // the last 6 samples must be dropped, not stored
        sink.Submit(S16(over));

        var buf = new float[ring + 6];
        sink.ReadInto(buf);
        Assert.Equal(0.5f, buf[0], 3);
        Assert.Equal(0.5f, buf[ring - 1], 3);  // first `ring` samples kept
        Assert.Equal(0f, buf[ring]);           // overrun tail dropped -> silence, never wraps onto old data
        Assert.Equal(0f, buf[ring + 5]);
    }

    [Fact]
    public void Clear_drops_buffered_audio()
    {
        var sink = new AudioSink(1024);
        sink.Submit(S16(0.5f, 0.5f, 0.5f, 0.5f));
        sink.Clear();
        var buf = new float[4];
        sink.ReadInto(buf);
        Assert.All(buf, v => Assert.Equal(0f, v)); // nothing left after Clear
    }

    [Fact]
    public void Odd_byte_count_ignores_trailing_half_sample()
    {
        var sink = new AudioSink(1024);
        sink.Submit(new byte[] { 0x00, 0x40, 0x7F }); // 3 bytes = one whole S16 (0x4000) + a stray byte
        var buf = new float[2];
        sink.ReadInto(buf);
        Assert.Equal(0.5f, buf[0], 3); // 0x4000 / 32768 = 0.5
        Assert.Equal(0f, buf[1]);      // the stray byte produced no sample
    }
}
