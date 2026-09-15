// Off-game unit tests for YtDlpResolver.MergeSelectorForHeight — the format selector that makes >360p
// YouTube streaming possible. Pins the load-bearing shape discovered by the live spike: a height-capped
// DIRECT-https video+audio merge (avc1+mp4a preferred), then any https merge, then a single combined
// format ending in 18 (360p) so resolve never comes back empty. protocol^=https keeps out the SABR/HLS
// (m3u8) variants that need a JS runtime the plugin doesn't ship.
//
// PURE BCL — no live network, no game/framework types.
using Stellar.WorldScreen.Net;
using Xunit;

namespace Stellar.WorldScreen.Tests;

public class YtDlpSelectorTests
{
    [Fact]
    public void MergeSelector_caps_video_and_audio_at_the_requested_height()
    {
        var s = YtDlpResolver.MergeSelectorForHeight(720);
        Assert.Contains("bv*[height<=720]", s);
        // The preferred branch requires BOTH tracks be direct https (no m3u8/SABR).
        Assert.Contains("[vcodec^=avc1][protocol^=https]+ba[acodec^=mp4a][protocol^=https]", s);
    }

    [Fact]
    public void MergeSelector_prefers_avc1_and_mp4a_for_avpro()
    {
        var s = YtDlpResolver.MergeSelectorForHeight(1080);
        // avc1 (H.264) video + mp4a (AAC) audio decode most reliably on MediaFoundation/AVPro; that branch
        // must come FIRST so it wins when present.
        int avc1 = s.IndexOf("vcodec^=avc1");
        int anyHttps = s.IndexOf("[height<=1080][protocol^=https]+ba[protocol^=https]");
        Assert.True(avc1 >= 0 && anyHttps >= 0 && avc1 < anyHttps,
            "avc1+mp4a branch must precede the any-codec https branch");
    }

    [Fact]
    public void MergeSelector_falls_back_to_a_single_combined_format()
    {
        var s = YtDlpResolver.MergeSelectorForHeight(480);
        // A trailing single-format fallback (18 = 360p combined) guarantees resolve returns ONE url rather
        // than nothing when no separate-stream merge is available.
        Assert.EndsWith("/18", s);
        Assert.Contains("b[vcodec^=avc1][acodec^=mp4a]", s);
    }

    [Theory]
    [InlineData(360)]
    [InlineData(480)]
    [InlineData(720)]
    [InlineData(1080)]
    public void MergeSelector_embeds_the_exact_height(int h)
    {
        Assert.Contains($"height<={h}", YtDlpResolver.MergeSelectorForHeight(h));
    }
}
