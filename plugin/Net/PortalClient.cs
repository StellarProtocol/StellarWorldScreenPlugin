// HTTP client for the World Portal backend (SP-1c Task 4; services/stellar-portal, contract in
// services/stellar-portal/docs/api.md). Wires together the pieces SP-1c Tasks 1-3 already built:
// InstallKey (plugin/Identity) signs; PortalCanonical + the request DTOs (plugin/Net/PortalDtos.cs,
// PortalCanonical.cs) build the exact signed string for each write. This file is the ONLY place that
// touches the network, builds the actual wire JSON bodies, and drives the background heartbeat loop.
//
// Threading model mirrors HelperClient.cs: HTTP work runs off the Unity main thread (on the .NET
// thread-pool, via async/await — no dedicated thread needed since HttpClient is itself async), and
// every result that must reach game/UI state is marshaled back via the injected `post` callback (in
// the real plugin, `_services.Framework.Post`; see WorldScreenPlugin.cs's existing
// `_client.OnConnected += () => _services.Framework.Post(...)` pattern). PortalClient itself never
// touches Unity/UnityEngine types — callers do that inside their `post`-marshaled continuation.
//
// Every public method returns an ordinary result value (see PortalClientDtos.cs) instead of throwing:
// a non-2xx response, a malformed response body, and a transport failure (DNS/refused/timeout) all
// become `Ok = false` data. This is what lets Start()'s background loop survive indefinitely — a
// failed tick logs and moves on to the next one, it never crashes the loop or the process.
//
// NOT a full round-trip fidelity check on its own: the canonical strings this file feeds to
// InstallKey.SignInstall are byte-for-byte pinned against the real backend by PortalCanonicalTests
// (SP-1c Task 3); this file's own tests (PortalClientTests.cs) instead pin that the SAME nonce/body
// PortalClient sends over the wire is the SAME one it canonicalized and signed.
//
// PURE BCL — no UnityEngine/Stellar.Abstractions types — compiled directly into the off-game unit-test
// project (tests/Stellar.WorldScreen.Tests.csproj) alongside PortalCanonical.cs/PortalDtos.cs, so it
// builds and runs under plain `dotnet test` with no game, no framework, and (via a stub
// HttpMessageHandler in tests) no live network.
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stellar.WorldScreen.Identity;

namespace Stellar.WorldScreen.Net;

/// <summary>
/// Talks to the stellar-portal backend: signed place/update/remove/watch/block/report writes, plus a
/// background heartbeat loop. See the file header above for the threading/error model.
/// </summary>
internal sealed class PortalClient
{
    /// <summary>Default heartbeat cadence per SP-1c's spec ("every ~5s"). <see cref="Start"/> exposes
    /// an override so tests don't have to wait multiple real-world seconds per tick.</summary>
    internal const int DefaultHeartbeatIntervalMs = 5000;

    // Bounded per-request timeout, independent of whatever Timeout the injected HttpClient itself
    // carries (a caller-misconfigured HttpClient.Timeout of Infinite must never hang the heartbeat
    // loop or a write call indefinitely).
    private const int RequestTimeoutMs = 10_000;

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly InstallKey _key;
    private readonly Func<long> _nowMs;
    private readonly Action<Action> _post;

    /// <param name="http">Caller-owned HttpClient (PortalClient never disposes it).</param>
    /// <param name="baseUrl">e.g. <c>http://127.0.0.1:8787</c> — trailing slash tolerated.</param>
    /// <param name="key">Per-install identity; signs every write's canonical payload.</param>
    /// <param name="nowMs">Injected clock (epoch ms) — used only for diagnostic log timestamps, so
    /// tests can supply a deterministic value instead of wall-clock time.</param>
    /// <param name="post">Marshals a callback onto the caller's "safe" thread — in the real plugin,
    /// <c>_services.Framework.Post</c>; in tests, typically a synchronous passthrough.</param>
    internal PortalClient(HttpClient http, string baseUrl, InstallKey key, Func<long> nowMs, Action<Action> post)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new ArgumentException("baseUrl is required", nameof(baseUrl));
        _baseUrl = baseUrl.TrimEnd('/');
        _key = key ?? throw new ArgumentNullException(nameof(key));
        _nowMs = nowMs ?? throw new ArgumentNullException(nameof(nowMs));
        _post = post ?? throw new ArgumentNullException(nameof(post));
    }

    // ---------------------------------------------------------------------------------------------
    // Number bridging (cross-repo invariant, load-bearing — see docs/api.md "Numbers are stringified
    // culture-INVARIANT"). The game hands positions to callers as `float` (IPlayerState.Position /
    // Position3D). Widening a "dirty" float straight to double (1.1f -> 1.100000023841858...) and then
    // formatting it would produce a string the backend's JS `String(n)` never produces for the same
    // logical value — a silent 401, since the signature covers a different byte string than the one
    // the server recomputes from the same JSON number. Rounding to a fixed, generous precision BEFORE
    // it becomes a double removes the float noise so the canonical string and the JSON body both carry
    // the SAME clean value (see PortalClientTests.RoundCoord_DirtyFloat_...). Callers (SP-1c Task 5/6)
    // must route every game-supplied coordinate through this ONE helper before constructing a
    // HeartbeatBody/PlaceBody — rounding twice, or rounding only one of canonical/JSON, is exactly the
    // bug this exists to prevent.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Rounds a game-supplied <c>float</c> coordinate to millimeter precision (3 decimal
    /// places) as a stable <c>double</c>, removing the float->double widening noise so the SAME value
    /// can be used for both the signed canonical string and the JSON wire body.</summary>
    internal static double RoundCoord(float value) => Math.Round((double)value, 3, MidpointRounding.AwayFromZero);

    // ---------------------------------------------------------------------------------------------
    // Signed writes
    // ---------------------------------------------------------------------------------------------

    /// <summary>`POST /heartbeat` — optional-signed per docs/api.md, but the plugin always signs it.
    /// A missing/malformed response, a non-2xx, or a transport failure all come back as
    /// <c>Ok == false</c>; this method never throws.</summary>
    internal async Task<HeartbeatResult> HeartbeatAsync(HeartbeatBody body)
    {
        string sig;
        HeartbeatBody signed;
        try
        {
            signed = body with { Nonce = NewNonce() };
            sig = _key.SignInstall(PortalCanonical.Heartbeat(signed));
        }
        catch (Exception ex)
        {
            return HeartbeatResult.Failure(0, $"sign error: {ex.Message}");
        }

        var wire = new
        {
            charId = signed.CharId,
            region = signed.Region,
            mapId = signed.MapId,
            sceneId = signed.SceneId,
            lineId = signed.LineId,
            pos = new { x = signed.PosX, y = signed.PosY, z = signed.PosZ },
            visibleCharIds = signed.VisibleCharIds,
            nonce = signed.Nonce,
            pubkey = _key.PubKeySpkiBase64,
            sig,
        };

        var outcome = await SendAsync(HttpMethod.Post, "/heartbeat", wire).ConfigureAwait(false);
        if (!outcome.Ok) return HeartbeatResult.Failure(outcome.Status, outcome.Error);
        return ParseHeartbeatBody(outcome.Status, outcome.Body);
    }

    /// <summary>`POST /portal` (place) — signed + established-owner-gated server-side. <paramref
    /// name="ownerName"/> is required by the wire body but deliberately excluded from
    /// <see cref="PlaceBody"/>/<see cref="PortalCanonical.Place"/> (it is not part of the signed
    /// canonical — see PortalDtos.cs's header), so it travels as a separate parameter here.</summary>
    internal Task<PlaceResult> PlaceAsync(PlaceBody body, string ownerName) =>
        SendPlaceAsync(HttpMethod.Post, "/portal", body, ownerName);

    /// <summary>`PATCH /portal/{id}` (update) — same wire shape as place, signed over the FULL intended
    /// post-update state via <see cref="PortalCanonical.Place"/> (not a partial diff). <paramref
    /// name="ownerName"/> is optional here per docs/api.md ("applied only when present").</summary>
    internal async Task<PortalWriteResult> UpdateAsync(string portalId, PlaceBody body, string? ownerName = null)
    {
        if (string.IsNullOrEmpty(portalId)) return PortalWriteResult.Failure(0, "portalId is required");
        var result = await SendPlaceAsync(HttpMethod.Patch, $"/portal/{Uri.EscapeDataString(portalId)}", body, ownerName)
            .ConfigureAwait(false);
        return result.Ok ? PortalWriteResult.Success(result.StatusCode) : PortalWriteResult.Failure(result.StatusCode, result.Error);
    }

    // Shared place/update wire-building + signing: identical body shape (docs/api.md: "PATCH reuses
    // this same shape signed over the FULL intended post-update state").
    private async Task<PlaceResult> SendPlaceAsync(HttpMethod method, string path, PlaceBody body, string? ownerName)
    {
        string sig;
        PlaceBody signed;
        try
        {
            signed = body with { Nonce = NewNonce() };
            sig = _key.SignInstall(PortalCanonical.Place(signed));
        }
        catch (Exception ex)
        {
            return PlaceResult.Failure(0, $"sign error: {ex.Message}");
        }

        var wire = new
        {
            region = signed.Region,
            mapId = signed.MapId,
            sceneId = signed.SceneId,
            lineId = signed.LineId,
            pos = new { x = signed.PosX, y = signed.PosY, z = signed.PosZ },
            yaw = signed.Yaw,
            ownerCharId = signed.OwnerCharId,
            ownerName,
            sourceKind = signed.SourceKind,
            sourceUrl = signed.SourceUrl,
            nonce = signed.Nonce,
            pubkey = _key.PubKeySpkiBase64,
            sig,
        };

        var outcome = await SendAsync(method, path, wire).ConfigureAwait(false);
        if (!outcome.Ok) return PlaceResult.Failure(outcome.Status, outcome.Error);
        return ParsePlaceBody(outcome.Status, outcome.Body);
    }

    /// <summary>`DELETE /portal/{id}` (remove) — owner-scoped. <paramref name="portalId"/> (the URL
    /// segment) always wins over whatever <paramref name="body"/>.PortalId happens to carry: both the
    /// canonical and the URL are rebuilt from the explicit <paramref name="portalId"/> argument so a
    /// stale/mismatched body field can never sign a different portal than the one actually
    /// addressed.</summary>
    internal async Task<PortalWriteResult> RemoveAsync(string portalId, DeleteBody body)
    {
        if (string.IsNullOrEmpty(portalId)) return PortalWriteResult.Failure(0, "portalId is required");

        string sig;
        DeleteBody signed;
        try
        {
            signed = body with { PortalId = portalId, Nonce = NewNonce() };
            sig = _key.SignInstall(PortalCanonical.Delete(signed));
        }
        catch (Exception ex)
        {
            return PortalWriteResult.Failure(0, $"sign error: {ex.Message}");
        }

        var wire = new
        {
            ownerCharId = signed.OwnerCharId,
            nonce = signed.Nonce,
            pubkey = _key.PubKeySpkiBase64,
            sig,
        };

        var outcome = await SendAsync(HttpMethod.Delete, $"/portal/{Uri.EscapeDataString(portalId)}", wire).ConfigureAwait(false);
        return outcome.Ok ? PortalWriteResult.Success(outcome.Status) : PortalWriteResult.Failure(outcome.Status, outcome.Error);
    }

    /// <summary>`POST /portal/{id}/watch` — deliberately UNAUTHENTICATED per docs/api.md ("Known
    /// deferrals" #2): no canonical, no pubkey/sig, no request body at all (the route doesn't read
    /// one).</summary>
    internal async Task<PortalWriteResult> WatchAsync(string portalId)
    {
        if (string.IsNullOrEmpty(portalId)) return PortalWriteResult.Failure(0, "portalId is required");
        var outcome = await SendAsync(HttpMethod.Post, $"/portal/{Uri.EscapeDataString(portalId)}/watch", jsonBody: null)
            .ConfigureAwait(false);
        return outcome.Ok ? PortalWriteResult.Success(outcome.Status) : PortalWriteResult.Failure(outcome.Status, outcome.Error);
    }

    /// <summary>`POST /portal/{id}/block` — signed, NOT established-owner-gated. <see
    /// cref="ModerationBody.Reason"/> is forced null (meaningful only for report) and <see
    /// cref="ModerationBody.Action"/> is forced <c>"block"</c> regardless of what the caller passed, so
    /// BlockAsync can never accidentally sign/send a "report" canonical.</summary>
    internal Task<PortalWriteResult> BlockAsync(string portalId, ModerationBody body) =>
        SendModerationAsync(portalId, body with { Action = "block", Reason = null }, "block", includeReason: false);

    /// <summary>`POST /portal/{id}/report` — same auth posture as block. <see cref="ModerationBody.Action"/>
    /// is forced <c>"report"</c> regardless of what the caller passed.</summary>
    internal Task<PortalWriteResult> ReportAsync(string portalId, ModerationBody body) =>
        SendModerationAsync(portalId, body with { Action = "report" }, "report", includeReason: true);

    private async Task<PortalWriteResult> SendModerationAsync(string portalId, ModerationBody body, string path, bool includeReason)
    {
        if (string.IsNullOrEmpty(portalId)) return PortalWriteResult.Failure(0, "portalId is required");

        string sig;
        ModerationBody signed;
        try
        {
            signed = body with { PortalId = portalId, Nonce = NewNonce() };
            sig = _key.SignInstall(PortalCanonical.Moderation(signed));
        }
        catch (Exception ex)
        {
            return PortalWriteResult.Failure(0, $"sign error: {ex.Message}");
        }

        object wire = includeReason
            ? new { charId = signed.CharId, reason = signed.Reason, nonce = signed.Nonce, pubkey = _key.PubKeySpkiBase64, sig }
            : new { charId = signed.CharId, nonce = signed.Nonce, pubkey = _key.PubKeySpkiBase64, sig };

        var outcome = await SendAsync(HttpMethod.Post, $"/portal/{Uri.EscapeDataString(portalId)}/{path}", wire).ConfigureAwait(false);
        return outcome.Ok ? PortalWriteResult.Success(outcome.Status) : PortalWriteResult.Failure(outcome.Status, outcome.Error);
    }

    private static string NewNonce() => Guid.NewGuid().ToString("N");

    // ---------------------------------------------------------------------------------------------
    // Background heartbeat loop
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Starts a background loop that, roughly every <paramref name="intervalMs"/> (default ~5s), calls
    /// <paramref name="gather"/> for a fresh <see cref="HeartbeatBody"/> (skipping the tick entirely
    /// when it returns null — i.e. not currently in-world), sends it via <see cref="HeartbeatAsync"/>,
    /// and delivers the (always-present, possibly-failed) result to <paramref name="onResult"/> via the
    /// constructor's <c>post</c> callback — never inline on the loop's own thread. A failing tick (bad
    /// response, exception anywhere in gather/send/parse) is logged and the loop reschedules its next
    /// tick regardless; nothing here can crash the loop or the caller's process. Dispose the returned
    /// handle to stop it.
    /// </summary>
    /// <param name="intervalMs">Overridable only for tests — production callers should use the
    /// default ~5s cadence.</param>
    internal IDisposable Start(Func<HeartbeatBody?> gather, Action<HeartbeatResult> onResult, int intervalMs = DefaultHeartbeatIntervalMs)
    {
        if (gather is null) throw new ArgumentNullException(nameof(gather));
        if (onResult is null) throw new ArgumentNullException(nameof(onResult));
        if (intervalMs <= 0) throw new ArgumentOutOfRangeException(nameof(intervalMs));

        var loop = new HeartbeatLoop(this, gather, onResult, intervalMs);
        loop.Start();
        return loop;
    }

    // Self-rescheduling timer (period = Infinite; the next tick is scheduled only after the current one
    // fully completes) rather than a periodic Timer — this guarantees exactly one tick in flight at a
    // time with no overlap-guard needed, and a slow/hanging tick simply pushes its own next tick later
    // instead of piling up concurrent HTTP calls.
    private sealed class HeartbeatLoop : IDisposable
    {
        private readonly PortalClient _owner;
        private readonly Func<HeartbeatBody?> _gather;
        private readonly Action<HeartbeatResult> _onResult;
        private readonly int _intervalMs;
        private Timer? _timer;
        private volatile bool _disposed;

        internal HeartbeatLoop(PortalClient owner, Func<HeartbeatBody?> gather, Action<HeartbeatResult> onResult, int intervalMs)
        {
            _owner = owner;
            _gather = gather;
            _onResult = onResult;
            _intervalMs = intervalMs;
        }

        internal void Start() => _timer = new Timer(OnTick, null, 0, Timeout.Infinite);

        private void OnTick(object? state) => _ = RunTickAsync();

        private async Task RunTickAsync()
        {
            try
            {
                HeartbeatBody? body;
                try
                {
                    body = _gather();
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[PortalClient] heartbeat gather() threw at t={_owner._nowMs()}: {ex.Message}");
                    body = null;
                }

                if (body != null)
                {
                    HeartbeatResult result;
                    try
                    {
                        result = await _owner.HeartbeatAsync(body).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // HeartbeatAsync already swallows HTTP/transport/sign failures into a typed
                        // result — this is a belt-and-braces guard so truly nothing can escape the loop.
                        Trace.WriteLine($"[PortalClient] heartbeat tick threw unexpectedly at t={_owner._nowMs()}: {ex.Message}");
                        result = HeartbeatResult.Failure(0, ex.Message);
                    }

                    if (!result.Ok)
                        Trace.WriteLine($"[PortalClient] heartbeat failed at t={_owner._nowMs()}: status={result.StatusCode} error={result.Error}");

                    if (!_disposed)
                        _owner._post(() => _onResult(result));
                }
            }
            catch (Exception ex)
            {
                // Absolutely nothing may escape this timer callback and kill the loop.
                Trace.WriteLine($"[PortalClient] heartbeat loop tick failed unexpectedly at t={_owner._nowMs()}: {ex.Message}");
            }
            finally
            {
                if (!_disposed) _timer?.Change(_intervalMs, Timeout.Infinite);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // HTTP + JSON plumbing
    // ---------------------------------------------------------------------------------------------

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

                    portals.Add(new PortalInfo(
                        PortalId: GetStr(p, "portalId") ?? string.Empty,
                        OwnerName: GetStr(p, "ownerName"),
                        PosX: px,
                        PosY: py,
                        PosZ: pz,
                        Yaw: GetNum(p, "yaw"),
                        Source: GetStr(p, "source"),
                        SourceKind: GetStr(p, "sourceKind"),
                        WatcherCount: GetIntOr(p, "watcherCount", 0)));
                }
            }

            return new HeartbeatResult(true, status, null, instanceId, portals);
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
}
