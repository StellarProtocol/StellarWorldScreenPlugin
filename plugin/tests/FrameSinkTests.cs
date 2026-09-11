using System;
using Stellar.WorldScreen.Screen;
using Xunit;

namespace Stellar.WorldScreen.Tests;

public class FrameSinkTests
{
    private static byte[] MakeBytes(int n, byte v)
    {
        var b = new byte[n];
        Array.Fill(b, v);
        return b;
    }

    [Fact]
    public void No_submit_returns_false_initially()
    {
        var s = new FrameSink();
        Assert.False(s.TryTakeLatest(out _, out _, out _));
    }

    [Fact]
    public void Latest_wins_returns_only_newest()
    {
        var s = new FrameSink();
        s.Submit(2, 2, 0, MakeBytes(2 * 2 * 4, 0x11));
        s.Submit(2, 2, 0, MakeBytes(2 * 2 * 4, 0x22)); // no take between
        Assert.True(s.TryTakeLatest(out var w, out var h, out var buf));
        Assert.Equal(2, w);
        Assert.Equal(2, h);
        Assert.All(new ArraySegment<byte>(buf, 0, 2 * 2 * 4), b => Assert.Equal((byte)0x22, b)); // newest
        Assert.False(s.TryTakeLatest(out _, out _, out _)); // dirty cleared
    }

    [Fact]
    public void Reallocs_on_size_change()
    {
        var s = new FrameSink();
        s.Submit(2, 2, 0, MakeBytes(2 * 2 * 4, 0x11));
        s.TryTakeLatest(out _, out _, out _);
        s.Submit(4, 4, 0, MakeBytes(4 * 4 * 4, 0x33));
        Assert.True(s.TryTakeLatest(out var w, out var h, out var buf));
        Assert.Equal(4, w);
        Assert.Equal(4, h);
        Assert.True(buf.Length >= 4 * 4 * 4);
    }

    [Fact]
    public void Untaken_frame_is_overwritten_by_next_submit()
    {
        // Submitting while a previous frame is untaken must silently overwrite it — the sink is a
        // mailbox, not a queue, so only the most recent Submit should ever surface.
        var s = new FrameSink();
        s.Submit(2, 2, 0, MakeBytes(2 * 2 * 4, 0xAA));
        s.Submit(2, 2, 0, MakeBytes(2 * 2 * 4, 0xBB));
        s.Submit(2, 2, 0, MakeBytes(2 * 2 * 4, 0xCC));
        Assert.True(s.TryTakeLatest(out _, out _, out var buf));
        Assert.All(new ArraySegment<byte>(buf, 0, 2 * 2 * 4), b => Assert.Equal((byte)0xCC, b));
    }
}
