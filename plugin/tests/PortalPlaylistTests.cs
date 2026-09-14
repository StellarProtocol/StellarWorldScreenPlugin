// Off-game unit tests for PortalPlaylist (SP-2b Task 4). Pins backwards-compat (a non-"playlist"
// sourceKind is a 1-item playlist — SP-1 portals still work), the playlist JSON round-trip stored in
// sourceUrl, and the null/empty/malformed -> empty-list guards.
//
// PURE BCL — no live network, no game/framework types.
using System.Collections.Generic;
using Stellar.WorldScreen.World;
using Xunit;

namespace Stellar.WorldScreen.Tests;

public class PortalPlaylistTests
{
    [Fact]
    public void SingleSource_is_a_one_item_playlist()
    {
        var items = PortalPlaylist.Parse("url", "https://x/v.mp4");
        Assert.Single(items);
        Assert.Equal(("https://x/v.mp4", "url"), (items[0].Url, items[0].Kind));
    }

    [Fact]
    public void SingleSource_file_kind_is_a_one_item_playlist()
    {
        var items = PortalPlaylist.Parse("file", "/some/local/path.mp4");
        Assert.Single(items);
        Assert.Equal(("/some/local/path.mp4", "file"), (items[0].Url, items[0].Kind));
        Assert.Null(items[0].Title);
    }

    [Fact]
    public void Playlist_json_round_trips()
    {
        var list = new[] { new PlaylistItem("u1", "url", "T1"), new PlaylistItem("f2", "file", null) };
        var json = PortalPlaylist.Serialize(list);
        var back = PortalPlaylist.Parse("playlist", json);
        Assert.Equal(2, back.Count);
        Assert.Equal(("u1", "url", "T1"), (back[0].Url, back[0].Kind, back[0].Title));
        Assert.Equal(("f2", "file", (string?)null), (back[1].Url, back[1].Kind, back[1].Title));
    }

    [Fact]
    public void Empty_or_null_is_empty() => Assert.Empty(PortalPlaylist.Parse(null, null));

    [Fact]
    public void Empty_source_with_non_playlist_kind_is_empty()
    {
        Assert.Empty(PortalPlaylist.Parse("url", null));
        Assert.Empty(PortalPlaylist.Parse("url", ""));
    }

    [Fact]
    public void Playlist_kind_with_null_or_empty_source_is_empty()
    {
        Assert.Empty(PortalPlaylist.Parse("playlist", null));
        Assert.Empty(PortalPlaylist.Parse("playlist", ""));
    }

    [Fact]
    public void Playlist_kind_with_malformed_json_is_empty()
    {
        Assert.Empty(PortalPlaylist.Parse("playlist", "{not valid json"));
        Assert.Empty(PortalPlaylist.Parse("playlist", "\"just a string\""));
    }

    [Fact]
    public void Serialize_empty_list_round_trips_to_empty()
    {
        var json = PortalPlaylist.Serialize(new List<PlaylistItem>());
        Assert.Empty(PortalPlaylist.Parse("playlist", json));
    }
}
