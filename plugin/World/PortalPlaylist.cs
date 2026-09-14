// Playlist model for a World Portal (SP-2b). A portal's placement wire body has no dedicated playlist
// field — a playlist rides inside the existing PlaceBody.SourceUrl as a compact JSON array, gated by
// SourceKind == "playlist" (see docs/superpowers/plans/2026-09-14-world-portal-sp2b-plugin.md Task 4).
// This keeps every pre-SP-2b (SP-1) portal — sourceKind "url"/"file" with a bare source string — working
// unchanged: PortalPlaylist.Parse treats any non-"playlist" sourceKind as a 1-item playlist built from
// that bare source, so viewer sync (Task 6) and DJ playback (Task 5) can always index into `playlist[0]`
// for an old-style portal.
//
// PURE BCL (System.Text.Json only, no UnityEngine/Stellar.Abstractions) — compiled directly into the
// off-game unit-test project (tests/Stellar.WorldScreen.Tests.csproj) alongside the other World/Net
// wire files, so it builds and runs under plain `dotnet test` with no game/framework present.
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Stellar.WorldScreen.World;

/// <summary>One entry in a portal's playlist. <see cref="Url"/> is the playable source (a URL or a
/// local file path, per <see cref="Kind"/>); <see cref="Title"/> is an optional display label the
/// placement editor may attach (Task 5) — never required for playback.</summary>
internal sealed record PlaylistItem(string Url, string Kind, string? Title);

/// <summary>
/// Parses/serializes a portal's playlist to and from the JSON stored in its <c>sourceUrl</c> field
/// (only meaningful when <c>sourceKind == "playlist"</c>). See the file header for the backwards-compat
/// contract with SP-1 single-source portals.
/// </summary>
internal static class PortalPlaylist
{
    /// <summary>
    /// <paramref name="sourceKind"/> <c>"playlist"</c> parses <paramref name="source"/> as a JSON array
    /// of <c>{url,kind,title}</c> objects (see <see cref="Serialize"/> for the exact shape); any other
    /// <paramref name="sourceKind"/> (including null) treats <paramref name="source"/> itself as the one
    /// bare source, wrapped as a single-item playlist — this is the SP-1 backwards-compat path. A null or
    /// empty <paramref name="source"/>, or malformed JSON under <c>"playlist"</c>, yields an empty list —
    /// never a throw.
    /// </summary>
    internal static IReadOnlyList<PlaylistItem> Parse(string? sourceKind, string? source)
    {
        if (string.IsNullOrEmpty(source)) return Array.Empty<PlaylistItem>();

        if (sourceKind != "playlist")
            return new[] { new PlaylistItem(source!, sourceKind ?? string.Empty, null) };

        try
        {
            using var doc = JsonDocument.Parse(source!);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<PlaylistItem>();

            var items = new List<PlaylistItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var url = GetStr(el, "url");
                var kind = GetStr(el, "kind");
                if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(kind)) continue; // skip malformed entries
                items.Add(new PlaylistItem(url!, kind!, GetStr(el, "title")));
            }
            return items;
        }
        catch (JsonException)
        {
            return Array.Empty<PlaylistItem>();
        }
    }

    /// <summary>Serializes a playlist to the compact JSON array stored in <c>sourceUrl</c> when placing
    /// with <c>sourceKind = "playlist"</c> — an array of <c>{"url":…,"kind":…,"title":…}</c> objects
    /// (title omitted when null). This exact shape is what <see cref="Parse"/> reads back.</summary>
    internal static string Serialize(IReadOnlyList<PlaylistItem> items)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var item in items)
            {
                writer.WriteStartObject();
                writer.WriteString("url", item.Url);
                writer.WriteString("kind", item.Kind);
                if (item.Title != null) writer.WriteString("title", item.Title);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? GetStr(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
