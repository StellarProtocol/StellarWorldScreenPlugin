// Off-game tests for HlsServer — the loopback HTTP server that feeds the HLS mux to AVPro. Exercises the
// real socket path with a real HttpClient: content types, full GET, byte-range (206), and 404. This is the
// half of the >360p streaming path verifiable without the game (the MediaFoundation side needs in-game).
//
// PURE BCL — real loopback sockets, no game/framework types.
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Stellar.WorldScreen.Net;
using Xunit;

namespace Stellar.WorldScreen.Tests;

public class HlsServerTests
{
    private static (string dir, string m3u8Body, byte[] seg) MakeHlsDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stellar-hls-test-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var m3u8 = "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:2.0,\nseg00000.ts\n";
        File.WriteAllText(Path.Combine(dir, "stream.m3u8"), m3u8);
        var seg = new byte[] { 0x47, 0x40, 0x00, 0x10, 1, 2, 3, 4, 5, 6, 7, 8 }; // 0x47 = TS sync byte
        File.WriteAllBytes(Path.Combine(dir, "seg00000.ts"), seg);
        return (dir, m3u8, seg);
    }

    [Fact]
    public async Task Serves_playlist_and_segment_with_correct_content_types()
    {
        var (dir, m3u8Body, seg) = MakeHlsDir();
        var server = new HlsServer(dir);
        server.Start();
        try
        {
            using var http = new HttpClient();
            var baseUrl = $"http://127.0.0.1:{server.Port}";

            var pl = await http.GetAsync($"{baseUrl}/stream.m3u8");
            Assert.Equal(HttpStatusCode.OK, pl.StatusCode);
            Assert.Equal("application/vnd.apple.mpegurl", pl.Content.Headers.ContentType!.MediaType);
            Assert.Equal(m3u8Body, await pl.Content.ReadAsStringAsync());

            var sg = await http.GetAsync($"{baseUrl}/seg00000.ts");
            Assert.Equal(HttpStatusCode.OK, sg.StatusCode);
            Assert.Equal("video/mp2t", sg.Content.Headers.ContentType!.MediaType);
            Assert.Equal(seg, await sg.Content.ReadAsByteArrayAsync());
        }
        finally { server.Stop(); Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Honors_a_byte_range_request_with_206()
    {
        var (dir, _, seg) = MakeHlsDir();
        var server = new HlsServer(dir);
        server.Start();
        try
        {
            using var http = new HttpClient();
            var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{server.Port}/seg00000.ts");
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 3); // first 4 bytes
            var res = await http.SendAsync(req);
            Assert.Equal(HttpStatusCode.PartialContent, res.StatusCode);
            var body = await res.Content.ReadAsByteArrayAsync();
            Assert.Equal(4, body.Length);
            Assert.Equal(new byte[] { seg[0], seg[1], seg[2], seg[3] }, body);
        }
        finally { server.Stop(); Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Missing_file_is_404()
    {
        var (dir, _, _) = MakeHlsDir();
        var server = new HlsServer(dir);
        server.Start();
        try
        {
            using var http = new HttpClient();
            var res = await http.GetAsync($"http://127.0.0.1:{server.Port}/nope.ts");
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }
        finally { server.Stop(); Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Path_traversal_is_refused()
    {
        var (dir, _, _) = MakeHlsDir();
        var server = new HlsServer(dir);
        server.Start();
        try
        {
            using var http = new HttpClient();
            // The server maps by basename only, so a traversal resolves to a filename that doesn't exist here.
            var res = await http.GetAsync($"http://127.0.0.1:{server.Port}/../../secret");
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }
        finally { server.Stop(); Directory.Delete(dir, true); }
    }
}
