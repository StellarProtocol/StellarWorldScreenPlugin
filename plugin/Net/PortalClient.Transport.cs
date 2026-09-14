// Low-level HTTP request/response + JSON plumbing for PortalClient — split out of PortalClient.cs
// (SP-1c Task 4 review fix) purely to keep both files under the project's 500-LoC file guardrail
// (CLAUDE.md § SOLID: "File > 500 LoC = major, > 800 = blocker. Split before adding more."). This is a
// mechanical extraction: SendAsync, ExtractError, ParseHeartbeatBody, ParsePlaceBody, and the small
// JsonElement getters (GetStr/GetNum/GetIntOr) moved here verbatim — no behavior differs from before
// the split. See PortalClient.cs's file header + class doc for the threading/error model and the
// public API this plumbing serves.
//
// PURE BCL — no UnityEngine/Stellar.Abstractions types — compiled directly into the off-game unit-test
// project (tests/Stellar.WorldScreen.Tests.csproj) alongside PortalClient.cs, so it builds and runs
// under plain `dotnet test` with no game, no framework, and (via a stub HttpMessageHandler in tests) no
// live network.
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Stellar.WorldScreen.Net;

internal sealed partial class PortalClient
{
    private readonly struct HttpOutcome
    {
        internal bool Ok { get; }
        internal int Status { get; }
        internal string? Error { get; }
        internal string? Body { get; }

        internal HttpOutcome(bool ok, int status, string? error, string? body)
        {
            Ok = ok;
            Status = status;
            Error = error;
            Body = body;
        }
    }

    // Every write/read funnels through here. Never throws: a non-2xx status, a malformed/absent
    // response body, or a transport-level exception (DNS failure, connection refused, TLS error,
    // request timeout) all become a HttpOutcome with Ok == false.
    private async Task<HttpOutcome> SendAsync(HttpMethod method, string path, object? jsonBody)
    {
        try
        {
            using var req = new HttpRequestMessage(method, _baseUrl + path);
            if (jsonBody != null)
            {
                var json = JsonSerializer.Serialize(jsonBody);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(RequestTimeoutMs));
            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            var status = (int)resp.StatusCode;

            string? text = null;
            try
            {
                text = await resp.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Body read failed after we already have a status — treat as an empty body rather than
                // masking the real HTTP status with a transport-failure shape.
            }

            return resp.IsSuccessStatusCode
                ? new HttpOutcome(true, status, null, text)
                : new HttpOutcome(false, status, ExtractError(text) ?? $"HTTP {status}", text);
        }
        catch (Exception ex)
        {
            return new HttpOutcome(false, 0, ex.Message, null);
        }
    }

    // docs/api.md: every write route's failure body is `Response.json({error}, {status})`.
    private static string? ExtractError(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body!);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error", out var err) &&
                err.ValueKind == JsonValueKind.String)
                return err.GetString();
        }
        catch
        {
            // Not JSON / malformed — the caller still has the raw body as a fallback error string.
        }
        return null;
    }

    private static HeartbeatResult ParseHeartbeatBody(int status, string? body)
    {
        if (string.IsNullOrEmpty(body)) return HeartbeatResult.Failure(status, "empty response body");
        try
        {
            using var doc = JsonDocument.Parse(body!);
            var root = doc.RootElement;
            var instanceId = GetStr(root, "instanceId");
            var serverNowMs = GetLongOr(root, "serverNowMs", 0);

            var portals = new System.Collections.Generic.List<PortalInfo>();
            if (root.TryGetProperty("portals", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in arr.EnumerateArray())
                {
                    double px = 0, py = 0, pz = 0;
                    if (p.TryGetProperty("pos", out var posEl) && posEl.ValueKind == JsonValueKind.Object)
                    {
                        px = GetNum(posEl, "x");
                        py = GetNum(posEl, "y");
                        pz = GetNum(posEl, "z");
                    }

                    int pbIndex = 0; long pbPos = 0, pbUpdated = 0; bool pbPlaying = false;
                    if (p.TryGetProperty("playback", out var pb) && pb.ValueKind == JsonValueKind.Object)
                    {
                        pbIndex = GetIntOr(pb, "index", 0);
                        pbPos = GetLongOr(pb, "positionMs", 0);
                        pbPlaying = pb.TryGetProperty("playing", out var pl) && pl.ValueKind == JsonValueKind.True;
                        pbUpdated = GetLongOr(pb, "updatedMs", 0);
                    }

                    portals.Add(new PortalInfo(
                        PortalId: GetStr(p, "portalId") ?? string.Empty,
                        OwnerName: GetStr(p, "ownerName"),
                        PosX: px,
                        PosY: py,
                        PosZ: pz,
                        Yaw: GetNum(p, "yaw"),
                        Source: GetStr(p, "source"),
                        SourceKind: GetStr(p, "sourceKind"),
                        WatcherCount: GetIntOr(p, "watcherCount", 0),
                        Playback: new PlaybackState(pbIndex, pbPos, pbPlaying, pbUpdated)));
                }
            }

            return new HeartbeatResult(true, status, null, instanceId, serverNowMs, portals);
        }
        catch (Exception ex)
        {
            return HeartbeatResult.Failure(status, $"parse error: {ex.Message}");
        }
    }

    private static PlaceResult ParsePlaceBody(int status, string? body)
    {
        if (string.IsNullOrEmpty(body)) return PlaceResult.Failure(status, "empty response body");
        try
        {
            using var doc = JsonDocument.Parse(body!);
            return new PlaceResult(true, status, null, GetStr(doc.RootElement, "portalId"));
        }
        catch (Exception ex)
        {
            return PlaceResult.Failure(status, $"parse error: {ex.Message}");
        }
    }

    private static string? GetStr(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static double GetNum(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? d : 0;

    private static int GetIntOr(JsonElement el, string name, int fallback) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : fallback;

    private static long GetLongOr(JsonElement el, string name, long fallback) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.TryGetInt64(out var i) ? i : fallback;
}
