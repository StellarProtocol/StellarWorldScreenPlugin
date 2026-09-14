// Off-game unit tests for PortalClient (SP-1c Task 4). Verifies: (1) every signed write's `sig` is
// computed over the EXACT canonical of the EXACT body sent, with the SAME nonce in both places; (2)
// pubkey/sig/nonce are attached to write bodies and method+path+body match
// services/stellar-portal/docs/api.md byte-for-byte; (3) WatchAsync is unsigned/bodyless; (4) a
// non-2xx (or a transport failure) surfaces as a typed failure and never throws; (5) Start's ~5s loop
// (here run at a short test interval) survives a failed tick and keeps ticking, delivering every
// result through the injected `post` (never inline on the HTTP thread); (6) the float->double
// coordinate rounding helper produces a cross-language-stable string.
//
// A stub HttpMessageHandler intercepts every request — no live network, no real backend.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stellar.WorldScreen.Identity;
using Stellar.WorldScreen.Net;
using Xunit;

namespace Stellar.WorldScreen.Tests;

public class PortalClientTests
{
    // ---- shared test scaffolding ----

    private static InstallKey NewKey()
    {
        var store = new Dictionary<string, string>();
        return InstallKey.LoadOrCreate(
            k => store.TryGetValue(k, out var v) ? v : null,
            (k, v) => store[k] = v);
    }

    private sealed record CapturedRequest(HttpMethod Method, string Url, string? Body);

    /// <summary>Intercepts every request PortalClient sends; never touches the network.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<CapturedRequest, HttpResponseMessage> _respond;
        internal readonly List<CapturedRequest> Requests = new();

        internal StubHandler(Func<CapturedRequest, HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content != null
                ? await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                : null;
            var captured = new CapturedRequest(request.Method, request.RequestUri!.ToString(), body);
            lock (Requests) Requests.Add(captured);
            return _respond(captured);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static PortalClient MakeClient(StubHandler stub, InstallKey key, Action<Action>? post = null, Func<long>? now = null, Action<string>? log = null) =>
        new(new HttpClient(stub), "http://localhost:9999", key, now ?? (() => 0), post ?? (a => a()), log);

    // ---- PlaceAsync ----

    [Fact]
    public async Task PlaceAsync_SignsCanonicalPlace_AndSendsPubkeySigNonce_ToCorrectUrl()
    {
        var key = NewKey();
        var stub = new StubHandler(_ => Json(HttpStatusCode.OK, "{\"portalId\":\"p-1\"}"));
        var client = MakeClient(stub, key);

        var body = new PlaceBody(
            Region: "sea", MapId: 42, SceneId: 7, LineId: 1,
            PosX: 1.5, PosY: 2.5, PosZ: 3.5, Yaw: 90, OwnerCharId: 1234,
            SourceKind: "url", SourceUrl: "https://x/y", Nonce: null);

        var result = await client.PlaceAsync(body, "Some Owner");

        Assert.True(result.Ok);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("p-1", result.PortalId);

        var req = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("http://localhost:9999/portal", req.Url);

        using var doc = JsonDocument.Parse(req.Body!);
        var root = doc.RootElement;
        Assert.Equal("sea", root.GetProperty("region").GetString());
        Assert.Equal(42, root.GetProperty("mapId").GetInt32());
        Assert.Equal(7, root.GetProperty("sceneId").GetInt32());
        Assert.Equal(1, root.GetProperty("lineId").GetInt32());
        Assert.Equal(1.5, root.GetProperty("pos").GetProperty("x").GetDouble());
        Assert.Equal(2.5, root.GetProperty("pos").GetProperty("y").GetDouble());
        Assert.Equal(3.5, root.GetProperty("pos").GetProperty("z").GetDouble());
        Assert.Equal(90, root.GetProperty("yaw").GetDouble());
        Assert.Equal(1234, root.GetProperty("ownerCharId").GetInt64());
        Assert.Equal("Some Owner", root.GetProperty("ownerName").GetString());
        Assert.Equal("url", root.GetProperty("sourceKind").GetString());
        Assert.Equal("https://x/y", root.GetProperty("sourceUrl").GetString());
        Assert.Equal(key.PubKeySpkiBase64, root.GetProperty("pubkey").GetString());

        var nonce = root.GetProperty("nonce").GetString();
        Assert.False(string.IsNullOrEmpty(nonce));

        // The load-bearing assertion: sig is over the EXACT canonical of the EXACT body sent, built
        // with the SAME nonce that traveled in the body.
        var expectedCanonical = PortalCanonical.Place(body with { Nonce = nonce });
        var expectedSig = key.SignInstall(expectedCanonical);
        Assert.Equal(expectedSig, root.GetProperty("sig").GetString());
    }

    [Fact]
    public async Task PlaceAsync_NonSuccessStatus_ReturnsTypedFailure_NeverThrows()
    {
        var key = NewKey();
        var stub = new StubHandler(_ => Json(HttpStatusCode.Forbidden, "{\"error\":\"not the established owner\"}"));
        var client = MakeClient(stub, key);
        var body = new PlaceBody("sea", 1, 1, 1, 1, 1, 1, 0, 1, "url", "u", null);

        var ex = await Record.ExceptionAsync(() => client.PlaceAsync(body, "Owner"));
        Assert.Null(ex);

        var result = await client.PlaceAsync(body, "Owner");
        Assert.False(result.Ok);
        Assert.Equal(403, result.StatusCode);
        Assert.Null(result.PortalId);
    }

    // ---- HeartbeatAsync ----

    [Fact]
    public async Task HeartbeatAsync_SignsCanonicalHeartbeat_IncludesPubkeyAndSig_ParsesResult()
    {
        var key = NewKey();
        const string respJson =
            "{\"instanceId\":\"inst-1\",\"portals\":[{\"portalId\":\"p1\",\"ownerName\":\"Bob\"," +
            "\"pos\":{\"x\":1,\"y\":2,\"z\":3},\"yaw\":45,\"source\":\"https://a\",\"sourceKind\":\"url\",\"watcherCount\":0}]}";
        var stub = new StubHandler(_ => Json(HttpStatusCode.OK, respJson));
        var client = MakeClient(stub, key);

        var body = new HeartbeatBody(
            CharId: 1234, Region: "sea", MapId: 42, SceneId: 7, LineId: 1,
            PosX: 1.5, PosY: -2, PosZ: 0, VisibleCharIds: new List<long> { 10, 20 }, Nonce: null);

        var result = await client.HeartbeatAsync(body);

        Assert.True(result.Ok);
        Assert.Equal("inst-1", result.InstanceId);
        var portal = Assert.Single(result.Portals);
        Assert.Equal("p1", portal.PortalId);
        Assert.Equal("Bob", portal.OwnerName);
        Assert.Equal(1, portal.PosX);
        Assert.Equal(2, portal.PosY);
        Assert.Equal(3, portal.PosZ);
        Assert.Equal(45, portal.Yaw);
        Assert.Equal("url", portal.SourceKind);
        Assert.Equal(0, portal.WatcherCount);

        var req = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("http://localhost:9999/heartbeat", req.Url);

        using var doc = JsonDocument.Parse(req.Body!);
        var root = doc.RootElement;
        Assert.Equal(key.PubKeySpkiBase64, root.GetProperty("pubkey").GetString());
        var nonce = root.GetProperty("nonce").GetString();
        var expectedCanonical = PortalCanonical.Heartbeat(body with { Nonce = nonce });
        Assert.Equal(key.SignInstall(expectedCanonical), root.GetProperty("sig").GetString());
    }

    [Fact]
    public async Task HeartbeatAsync_ParsesServerNowMsAndPerPortalPlayback()
    {
        var key = NewKey();
        const string respJson =
            "{\"instanceId\":\"inst-1\",\"serverNowMs\":1725900000000,\"portals\":[{\"portalId\":\"p1\",\"ownerName\":\"Bob\"," +
            "\"pos\":{\"x\":1,\"y\":2,\"z\":3},\"yaw\":45,\"source\":\"https://a\",\"sourceKind\":\"url\",\"watcherCount\":0," +
            "\"playback\":{\"index\":2,\"positionMs\":45000,\"playing\":true,\"updatedMs\":1725899999000}}]}";
        var stub = new StubHandler(_ => Json(HttpStatusCode.OK, respJson));
        var client = MakeClient(stub, key);

        var body = new HeartbeatBody(
            CharId: 1234, Region: "sea", MapId: 42, SceneId: 7, LineId: 1,
            PosX: 1.5, PosY: -2, PosZ: 0, VisibleCharIds: new List<long> { 10, 20 }, Nonce: null);

        var result = await client.HeartbeatAsync(body);

        Assert.True(result.Ok);
        Assert.Equal(1725900000000L, result.ServerNowMs);
        var portal = Assert.Single(result.Portals);
        Assert.Equal(2, portal.Playback.Index);
        Assert.Equal(45000L, portal.Playback.PositionMs);
        Assert.True(portal.Playback.Playing);
        Assert.Equal(1725899999000L, portal.Playback.UpdatedMs);
    }

    [Fact]
    public async Task HeartbeatAsync_MissingServerNowMsAndPlayback_DefaultToZeroFalse()
    {
        var key = NewKey();
        const string respJson =
            "{\"instanceId\":\"inst-1\",\"portals\":[{\"portalId\":\"p1\",\"ownerName\":\"Bob\"," +
            "\"pos\":{\"x\":1,\"y\":2,\"z\":3},\"yaw\":45,\"source\":\"https://a\",\"sourceKind\":\"url\",\"watcherCount\":0}]}";
        var stub = new StubHandler(_ => Json(HttpStatusCode.OK, respJson));
        var client = MakeClient(stub, key);

        var body = new HeartbeatBody(1, "sea", 1, 1, 1, 0, 0, 0, new List<long>(), null);
        var result = await client.HeartbeatAsync(body);

        Assert.True(result.Ok);
        Assert.Equal(0L, result.ServerNowMs);
        var portal = Assert.Single(result.Portals);
        Assert.Equal(0, portal.Playback.Index);
        Assert.Equal(0L, portal.Playback.PositionMs);
        Assert.False(portal.Playback.Playing);
        Assert.Equal(0L, portal.Playback.UpdatedMs);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task HeartbeatAsync_NonSuccessStatus_ReturnsTypedFailure_NeverThrows(HttpStatusCode status)
    {
        var key = NewKey();
        var stub = new StubHandler(_ => Json(status, "{\"error\":\"nope\"}"));
        var client = MakeClient(stub, key);
        var body = new HeartbeatBody(1, "sea", 1, 1, 1, 0, 0, 0, new List<long>(), null);

        var ex = await Record.ExceptionAsync(() => client.HeartbeatAsync(body));
        Assert.Null(ex);

        var result = await client.HeartbeatAsync(body);
        Assert.False(result.Ok);
        Assert.Equal((int)status, result.StatusCode);
        Assert.Empty(result.Portals);
        Assert.Null(result.InstanceId);
    }

    [Fact]
    public async Task HeartbeatAsync_TransportFailure_ReturnsTypedFailure_NeverThrows()
    {
        var key = NewKey();
        var stub = new StubHandler(_ => throw new HttpRequestException("boom"));
        var client = MakeClient(stub, key);
        var body = new HeartbeatBody(1, "sea", 1, 1, 1, 0, 0, 0, new List<long>(), null);

        var result = await client.HeartbeatAsync(body);

        Assert.False(result.Ok);
        Assert.Equal(0, result.StatusCode);
        Assert.NotNull(result.Error);
    }

    // ---- UpdateAsync / RemoveAsync ----

    [Fact]
    public async Task UpdateAsync_PatchesPortalId_SignsFullPlaceCanonical()
    {
        var key = NewKey();
        var stub = new StubHandler(_ => Json(HttpStatusCode.OK, "{\"ok\":true}"));
        var client = MakeClient(stub, key);

        var body = new PlaceBody(
            Region: "sea", MapId: 1, SceneId: 1, LineId: null,
            PosX: 1, PosY: 1, PosZ: 1, Yaw: 0, OwnerCharId: 1,
            SourceKind: null, SourceUrl: null, Nonce: null);

        var result = await client.UpdateAsync("portal-xyz", body, ownerName: "Renamed");

        Assert.True(result.Ok);
        var req = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Patch, req.Method);
        Assert.Equal("http://localhost:9999/portal/portal-xyz", req.Url);

        using var doc = JsonDocument.Parse(req.Body!);
        var root = doc.RootElement;
        Assert.Equal("Renamed", root.GetProperty("ownerName").GetString());
        var nonce = root.GetProperty("nonce").GetString();
        var expected = PortalCanonical.Place(body with { Nonce = nonce });
        Assert.Equal(key.SignInstall(expected), root.GetProperty("sig").GetString());
    }

    [Fact]
    public async Task RemoveAsync_SignsDeleteCanonical_WithUrlPortalId_NotBodyPortalId()
    {
        var key = NewKey();
        var stub = new StubHandler(_ => Json(HttpStatusCode.OK, "{\"ok\":true}"));
        var client = MakeClient(stub, key);

        // A deliberately mismatched body.PortalId — RemoveAsync must sign/URL against the EXPLICIT
        // portalId argument, never trust a possibly-stale value carried on the body.
        var body = new DeleteBody(PortalId: "stale-id", OwnerCharId: 1234, Nonce: null);
        var result = await client.RemoveAsync("portal-xyz", body);

        Assert.True(result.Ok);
        var req = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Delete, req.Method);
        Assert.Equal("http://localhost:9999/portal/portal-xyz", req.Url);

        using var doc = JsonDocument.Parse(req.Body!);
        var root = doc.RootElement;
        Assert.Equal(1234, root.GetProperty("ownerCharId").GetInt64());
        Assert.False(root.TryGetProperty("portalId", out _)); // portalId is a URL segment, not a body field
        var nonce = root.GetProperty("nonce").GetString();
        var expected = PortalCanonical.Delete(new DeleteBody("portal-xyz", 1234, nonce));
        Assert.Equal(key.SignInstall(expected), root.GetProperty("sig").GetString());
    }

    // ---- SetPlaybackAsync ----

    [Fact]
    public async Task SetPlaybackAsync_PatchesPortalId_SignsPlaybackCanonical()
    {
        var key = NewKey();
        var stub = new StubHandler(_ => Json(HttpStatusCode.OK, "{\"ok\":true}"));
        var client = MakeClient(stub, key);

        // A deliberately mismatched body.PortalId — SetPlaybackAsync must sign/URL against the
        // EXPLICIT portalId argument, never trust a possibly-stale value carried on the body (mirrors
        // RemoveAsync's same guarantee).
        var body = new PlaybackBody(PortalId: "stale-id", Index: 3, PositionMs: 128500, Playing: true, Nonce: null);
        var result = await client.SetPlaybackAsync("p1", body);

        Assert.True(result.Ok);
        var req = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Patch, req.Method);
        Assert.Equal("http://localhost:9999/portal/p1/playback", req.Url);

        using var doc = JsonDocument.Parse(req.Body!);
        var root = doc.RootElement;
        Assert.Equal(3, root.GetProperty("index").GetInt32());
        Assert.Equal(128500, root.GetProperty("positionMs").GetInt64());
        Assert.True(root.GetProperty("playing").GetBoolean());
        Assert.Equal(key.PubKeySpkiBase64, root.GetProperty("pubkey").GetString());
        Assert.False(root.TryGetProperty("portalId", out _)); // portalId is a URL segment, not a body field

        var nonce = root.GetProperty("nonce").GetString();
        Assert.False(string.IsNullOrEmpty(nonce));

        // The load-bearing assertion: sig is over the EXACT canonical of the EXACT body sent (URL
        // portalId, not the stale body one), built with the SAME nonce that traveled in the body.
        var expectedCanonical = PortalCanonical.Playback(new PlaybackBody("p1", 3, 128500, true, nonce));
        var expectedSig = key.SignInstall(expectedCanonical);
        Assert.Equal(expectedSig, root.GetProperty("sig").GetString());
    }

    [Fact]
    public async Task SetPlaybackAsync_NonSuccessStatus_ReturnsTypedFailure_NeverThrows()
    {
        var key = NewKey();
        var stub = new StubHandler(_ => Json(HttpStatusCode.NotFound, "{\"error\":\"not found\"}"));
        var client = MakeClient(stub, key);
        var body = new PlaybackBody("p1", 0, 0, false, null);

        var ex = await Record.ExceptionAsync(() => client.SetPlaybackAsync("p1", body));
        Assert.Null(ex);

        var result = await client.SetPlaybackAsync("p1", body);
        Assert.False(result.Ok);
        Assert.Equal(404, result.StatusCode);
    }

    // ---- WatchAsync (unauthenticated) ----

    [Fact]
    public async Task WatchAsync_IsUnsigned_SendsNoBody_PostsToWatchPath()
    {
        var key = NewKey();
        var stub = new StubHandler(_ => Json(HttpStatusCode.OK, "{\"ok\":true}"));
        var client = MakeClient(stub, key);

        var result = await client.WatchAsync("portal-abc");

        Assert.True(result.Ok);
        var req = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("http://localhost:9999/portal/portal-abc/watch", req.Url);
        Assert.True(string.IsNullOrEmpty(req.Body));
    }

    // ---- BlockAsync / ReportAsync ----

    [Fact]
    public async Task BlockAsync_SignsModerationCanonical_CarriesNoReasonField()
    {
        var key = NewKey();
        var stub = new StubHandler(_ => Json(HttpStatusCode.OK, "{\"ok\":true}"));
        var client = MakeClient(stub, key);

        var body = new ModerationBody(Action: "ignored", PortalId: "ignored", CharId: 55, Reason: "should be dropped", Nonce: null);
        var result = await client.BlockAsync("portal-1", body);

        Assert.True(result.Ok);
        var req = Assert.Single(stub.Requests);
        Assert.Equal("http://localhost:9999/portal/portal-1/block", req.Url);

        using var doc = JsonDocument.Parse(req.Body!);
        var root = doc.RootElement;
        Assert.Equal(55, root.GetProperty("charId").GetInt64());
        Assert.False(root.TryGetProperty("reason", out _));
        var nonce = root.GetProperty("nonce").GetString();
        var expected = PortalCanonical.Moderation(new ModerationBody("block", "portal-1", 55, null, nonce));
        Assert.Equal(key.SignInstall(expected), root.GetProperty("sig").GetString());
    }

    [Fact]
    public async Task ReportAsync_SignsModerationCanonical_WithReason()
    {
        var key = NewKey();
        var stub = new StubHandler(_ => Json(HttpStatusCode.OK, "{\"ok\":true}"));
        var client = MakeClient(stub, key);

        var body = new ModerationBody(Action: "ignored", PortalId: "ignored", CharId: 55, Reason: "spam", Nonce: null);
        var result = await client.ReportAsync("portal-1", body);

        Assert.True(result.Ok);
        var req = Assert.Single(stub.Requests);
        Assert.Equal("http://localhost:9999/portal/portal-1/report", req.Url);

        using var doc = JsonDocument.Parse(req.Body!);
        var root = doc.RootElement;
        Assert.Equal("spam", root.GetProperty("reason").GetString());
        var nonce = root.GetProperty("nonce").GetString();
        var expected = PortalCanonical.Moderation(new ModerationBody("report", "portal-1", 55, "spam", nonce));
        Assert.Equal(key.SignInstall(expected), root.GetProperty("sig").GetString());
    }

    // ---- Start (background heartbeat loop) ----

    [Fact]
    public async Task Start_SurvivesFailedTick_KeepsCallingOnNextTick_DeliversResultsOnlyViaPost()
    {
        var key = NewKey();
        var callCount = 0;
        var stub = new StubHandler(_ =>
        {
            var n = Interlocked.Increment(ref callCount);
            // First tick fails (500); every later tick succeeds — proves the loop both survives the
            // failure AND keeps ticking afterward, rather than dying on the first error.
            return n == 1
                ? Json(HttpStatusCode.InternalServerError, "{\"error\":\"boom\"}")
                : Json(HttpStatusCode.OK, "{\"instanceId\":\"i\",\"portals\":[]}");
        });

        var insidePost = false;
        Action<Action> post = a =>
        {
            insidePost = true;
            try { a(); } finally { insidePost = false; }
        };
        var client = MakeClient(stub, key, post: post);

        HeartbeatBody Gather() => new(1, "sea", 1, 1, 1, 0, 0, 0, new List<long>(), null);

        var results = new List<HeartbeatResult>();
        void OnResult(HeartbeatResult r)
        {
            // The load-bearing assertion for "never inline on the HTTP thread": onResult must only ever
            // run while control is inside the injected post() delegate.
            Assert.True(insidePost);
            lock (results) results.Add(r);
        }

        using var handle = client.Start(Gather, OnResult, intervalMs: 20);

        // Bounded wait (not an unbounded poll): give the timer up to 2s to accumulate >= 2 delivered
        // results, checking every 20ms.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            int count;
            lock (results) count = results.Count;
            if (count >= 2) break;
            await Task.Delay(20);
        }

        List<HeartbeatResult> snapshot;
        lock (results) snapshot = new List<HeartbeatResult>(results);

        Assert.True(snapshot.Count >= 2, $"expected >= 2 delivered results within 2s, got {snapshot.Count}");
        Assert.False(snapshot[0].Ok);  // the first tick's failure surfaced as data, not an exception
        Assert.True(snapshot[1].Ok);   // and the loop kept going afterward
    }

    [Fact]
    public void Start_Dispose_StopsFurtherGathering()
    {
        var key = NewKey();
        var stub = new StubHandler(_ => Json(HttpStatusCode.OK, "{\"instanceId\":\"i\",\"portals\":[]}"));
        var client = MakeClient(stub, key);

        var gatherCount = 0;
        HeartbeatBody Gather()
        {
            Interlocked.Increment(ref gatherCount);
            return new HeartbeatBody(1, "sea", 1, 1, 1, 0, 0, 0, new List<long>(), null);
        }

        var handle = client.Start(Gather, _ => { }, intervalMs: 15);
        Thread.Sleep(60); // let a couple of ticks happen
        handle.Dispose();
        var countAtDispose = gatherCount;

        Thread.Sleep(150); // long enough for several more ticks if the loop were still alive

        Assert.Equal(countAtDispose, gatherCount);
    }

    [Fact]
    public async Task Start_FailingTick_InvokesInjectedLogCallback()
    {
        // Trace.WriteLine is NOT bridged to the plugin log under BepInEx/IL2CPP (this fix's motivation)
        // — this pins that a failing tick's failure-path message reaches an INJECTED sink instead, so a
        // real composition root can route it to the plugin's own logger.
        var key = NewKey();
        var stub = new StubHandler(_ => Json(HttpStatusCode.InternalServerError, "{\"error\":\"boom\"}"));

        var logMessages = new List<string>();
        void Log(string message) { lock (logMessages) logMessages.Add(message); }

        var client = MakeClient(stub, key, log: Log);
        HeartbeatBody Gather() => new(1, "sea", 1, 1, 1, 0, 0, 0, new List<long>(), null);

        using var handle = client.Start(Gather, _ => { }, intervalMs: 20);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            int count;
            lock (logMessages) count = logMessages.Count;
            if (count > 0) break;
            await Task.Delay(20);
        }

        List<string> snapshot;
        lock (logMessages) snapshot = new List<string>(logMessages);
        Assert.NotEmpty(snapshot);
        Assert.Contains(snapshot, m => m.Contains("heartbeat failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Start_GatherReturnsNull_SkipsTick_NoHttpCall_ThenNextNonNullTickProceeds()
    {
        var key = NewKey();
        var stub = new StubHandler(_ => Json(HttpStatusCode.OK, "{\"instanceId\":\"i\",\"portals\":[]}"));
        var client = MakeClient(stub, key);

        var gatherCount = 0;
        using var firstGatherDone = new ManualResetEventSlim(false);
        HeartbeatBody? Gather()
        {
            var n = Interlocked.Increment(ref gatherCount);
            if (n == 1)
            {
                firstGatherDone.Set();
                return null; // not currently in-world — this tick must be skipped entirely
            }
            return new HeartbeatBody(1, "sea", 1, 1, 1, 0, 0, 0, new List<long>(), null);
        }

        var results = new List<HeartbeatResult>();
        void OnResult(HeartbeatResult r) { lock (results) results.Add(r); }

        // A generous interval: the first (null) tick fires immediately (due time 0); the loop only
        // schedules its NEXT tick after this one completes, so a wide gap here gives plenty of room to
        // observe "no HTTP call yet" before the second tick could possibly fire.
        using var handle = client.Start(Gather, OnResult, intervalMs: 200);

        Assert.True(firstGatherDone.Wait(TimeSpan.FromSeconds(2)), "expected the first gather() call within 2s");
        await Task.Delay(50); // brief window; would catch a spurious HTTP call from a broken null-skip
        Assert.Empty(stub.Requests); // the null-gather tick made NO HTTP call — the stub was never invoked

        // Bounded wait for the second (non-null) tick to deliver a result — proves the loop stayed alive
        // and the next non-null tick proceeds normally.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            int count;
            lock (results) count = results.Count;
            if (count >= 1) break;
            await Task.Delay(20);
        }

        List<HeartbeatResult> snapshot;
        lock (results) snapshot = new List<HeartbeatResult>(results);
        Assert.True(snapshot.Count >= 1, "expected the non-null tick to deliver a result");
        Assert.True(snapshot[0].Ok);
        Assert.Single(stub.Requests); // exactly one HTTP call total, from the non-null tick only
    }

    // ---- Number rounding (cross-language stability for a "dirty" widened float) ----

    [Fact]
    public void RoundSignedCanonicalFloat_DirtyFloat_ProducesStableString_InCanonicalAndJson()
    {
        const float dirty = 1.1f; // widens to 1.100000023841858... as a raw double
        var rounded = PortalClient.RoundSignedCanonicalFloat(dirty);

        var body = new HeartbeatBody(1, "sea", 1, 1, 1, rounded, 0, 0, new List<long>(), "n");
        var canonical = PortalCanonical.Heartbeat(body);
        Assert.Contains("|1.1|", canonical); // posX segment renders as "1.1", never a long float tail

        var json = JsonSerializer.Serialize(new { x = rounded });
        Assert.Equal("{\"x\":1.1}", json); // the SAME rounded double serializes identically on the wire
    }

    [Fact]
    public void RoundSignedCanonicalFloat_NegativeDirtyFloat_RoundTrips()
    {
        const float dirty = -2.35f;
        var rounded = PortalClient.RoundSignedCanonicalFloat(dirty);
        Assert.Equal("-2.35", rounded.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
    }

    // Yaw is ALSO a signed float that enters PortalCanonical.Place — it must go through the SAME
    // rounding helper as positions, or a dirty widened yaw silently 401s exactly like a dirty widened
    // position would (the bug this helper's generalization exists to prevent).
    [Fact]
    public void RoundSignedCanonicalFloat_DirtyYawFloat_ProducesStableString_InCanonicalAndJson()
    {
        const float dirtyYaw = 1.1f; // widens to 1.100000023841858... as a raw double, same as a position
        var rounded = PortalClient.RoundSignedCanonicalFloat(dirtyYaw);

        var body = new PlaceBody(
            Region: "sea", MapId: 1, SceneId: 1, LineId: 1,
            PosX: 0, PosY: 0, PosZ: 0, Yaw: rounded, OwnerCharId: 1,
            SourceKind: null, SourceUrl: null, Nonce: "n");
        var canonical = PortalCanonical.Place(body);
        Assert.Contains("|1.1|", canonical); // yaw segment renders as "1.1", never a long float tail

        var json = JsonSerializer.Serialize(new { yaw = rounded });
        Assert.Equal("{\"yaw\":1.1}", json); // the SAME rounded double serializes identically on the wire
    }
}
