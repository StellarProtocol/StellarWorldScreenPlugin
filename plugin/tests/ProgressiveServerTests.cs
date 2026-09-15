// Off-game tests for ProgressiveServer — the loopback HTTP server that streams the single growing MP4 mux
// to AVPro. Exercises the real socket path with a real HttpClient: full GET, byte-range (206) with the
// advertised total, and the wait-for-data behaviour when a range lands past what's been written yet (ffmpeg
// still muxing). This is the half of the >360p path verifiable without the game.
//
// PURE BCL — real loopback sockets, no game/framework types.
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Stellar.WorldScreen.Net;
using Xunit;

namespace Stellar.WorldScreen.Tests;

public class ProgressiveServerTests
{
    private static string WriteFile(byte[] data)
    {
        var path = Path.Combine(Path.GetTempPath(), "stellar-prog-" + System.Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllBytes(path, data);
        return path;
    }

    [Fact]
    public async Task Full_get_returns_the_file_as_video_mp4()
    {
        var data = new byte[5000];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 256);
        var path = WriteFile(data);
        var server = new ProgressiveServer(path, data.Length);
        server.Start();
        try
        {
            using var http = new HttpClient();
            var res = await http.GetAsync($"http://127.0.0.1:{server.Port}/stream.mp4");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal("video/mp4", res.Content.Headers.ContentType!.MediaType);
            Assert.Equal(data, await res.Content.ReadAsByteArrayAsync());
        }
        finally { server.Stop(); File.Delete(path); }
    }

    [Fact]
    public async Task Range_request_returns_206_with_the_advertised_total()
    {
        var data = new byte[5000];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 256);
        var path = WriteFile(data);
        long advertised = data.Length - 40; // deliberately a touch under the file size (as the muxer does)
        var server = new ProgressiveServer(path, advertised);
        server.Start();
        try
        {
            using var http = new HttpClient();
            var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{server.Port}/stream.mp4");
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1000, 1099);
            var res = await http.SendAsync(req);
            Assert.Equal(HttpStatusCode.PartialContent, res.StatusCode);
            Assert.Equal(advertised, res.Content.Headers.ContentRange!.Length);
            var body = await res.Content.ReadAsByteArrayAsync();
            Assert.Equal(100, body.Length);
            Assert.Equal((byte)(1000 % 256), body[0]);
        }
        finally { server.Stop(); File.Delete(path); }
    }

    [Fact]
    public async Task Waits_for_data_that_is_written_after_the_request_starts()
    {
        // Start with only 1000 bytes on disk but advertise 3000; request the tail that doesn't exist yet.
        var path = WriteFile(new byte[1000]);
        var server = new ProgressiveServer(path, 3000);
        server.Start();
        // After a delay, grow the file to 3000 bytes — the in-flight request must then complete.
        var grower = Task.Run(async () =>
        {
            await Task.Delay(400);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            fs.Seek(0, SeekOrigin.End);
            var more = new byte[2000];
            for (int i = 0; i < more.Length; i++) more[i] = 0x55;
            fs.Write(more, 0, more.Length);
            fs.Flush();
        });
        try
        {
            using var http = new HttpClient { Timeout = System.TimeSpan.FromSeconds(10) };
            var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{server.Port}/stream.mp4");
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1000, 2999); // all past current EOF at start
            var res = await http.SendAsync(req);
            Assert.Equal(HttpStatusCode.PartialContent, res.StatusCode);
            var body = await res.Content.ReadAsByteArrayAsync();
            Assert.Equal(2000, body.Length);      // it waited for the appended bytes
            Assert.Equal(0x55, body[0]);
            await grower;
        }
        finally { server.Stop(); File.Delete(path); }
    }
}
