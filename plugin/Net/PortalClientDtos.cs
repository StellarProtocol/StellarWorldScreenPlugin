// Wire-facing result/DTO types consumed by PortalClient (SP-1c Task 4). Distinct from PortalDtos.cs's
// canonical-only request bodies (HeartbeatBody/PlaceBody/ModerationBody/DeleteBody, SP-1c Task 3):
// those exist ONLY to feed PortalCanonical's signing strings and deliberately omit fields the backend's
// canonical builders don't sign (see PortalDtos.cs's header) — e.g. PlaceBody has no OwnerName because
// canonicalPlace doesn't include it, even though the wire body the server actually reads DOES require
// ownerName (services/stellar-portal/docs/api.md's `POST /portal` row). These types are the OTHER half:
// what PortalClient hands back to its caller after a request completes (or fails).
//
// Every *Result/*Outcome type below folds "did this succeed" (Ok/StatusCode/Error) directly into the
// return value instead of throwing — a non-2xx, a transport failure, or a malformed response body all
// become ordinary data. This is what lets PortalClient.Start's background loop treat every tick
// uniformly: gather → call → (always) deliver a result via `post`, never crash on a bad response.
//
// PURE BCL — no UnityEngine/Stellar.Abstractions types — compiled directly into the off-game unit-test
// project (tests/Stellar.WorldScreen.Tests.csproj) alongside PortalClient.cs.
using System;
using System.Collections.Generic;

namespace Stellar.WorldScreen.Net;

/// <summary>
/// One portal entry from a `/heartbeat` response's <c>portals[]</c> array
/// (services/stellar-portal/docs/api.md). <see cref="Source"/>/<see cref="SourceKind"/> mirror the
/// placer's own <c>sourceUrl</c>/<c>sourceKind</c>; <see cref="WatcherCount"/> is currently always 0
/// server-side (no per-viewer watcher ledger exists yet — see api.md "Known deferrals" #3).
/// <see cref="Playback"/> (SP-2b) is the owner-DJ's last-reported playback state; a portal with no DJ
/// reporting yet (pre-SP-2 backend, or the owner hasn't sent one) parses to the all-zero/false default.
/// </summary>
internal sealed record PortalInfo(
    string PortalId,
    string? OwnerName,
    long OwnerCharId,
    double PosX,
    double PosY,
    double PosZ,
    double Yaw,
    string? Source,
    string? SourceKind,
    int WatcherCount,
    PlaybackState Playback);

/// <summary>The owner-DJ's playback state for one portal (SP-2b), as carried on a `/heartbeat`
/// response's per-portal <c>playback</c> object. <see cref="UpdatedMs"/> is the backend's own clock
/// (server epoch ms) at the moment this state was last reported — combined with
/// <see cref="HeartbeatResult.ServerNowMs"/> a viewer can extrapolate the DJ's live position.</summary>
internal sealed record PlaybackState(int Index, long PositionMs, bool Playing, long UpdatedMs);

/// <summary>
/// Result of <see cref="PortalClient.HeartbeatAsync"/>. On any failure (non-2xx, transport error, or a
/// response body that didn't parse as expected) <see cref="Ok"/> is false, <see cref="Portals"/> is
/// empty, and <see cref="Error"/> carries a diagnostic string — the caller never has to catch an
/// exception to tell success from failure, which is what lets <see cref="PortalClient.Start"/>'s
/// background loop deliver every tick's outcome uniformly.
/// </summary>
internal sealed record HeartbeatResult(
    bool Ok,
    int StatusCode,
    string? Error,
    string? InstanceId,
    long ServerNowMs,
    IReadOnlyList<PortalInfo> Portals)
{
    internal static HeartbeatResult Failure(int statusCode, string? error) =>
        new(false, statusCode, error, null, 0, Array.Empty<PortalInfo>());
}

/// <summary>Result of <see cref="PortalClient.PlaceAsync"/> (`POST /portal`). <see cref="PortalId"/> is
/// null on any failure.</summary>
internal sealed record PlaceResult(bool Ok, int StatusCode, string? Error, string? PortalId)
{
    internal static PlaceResult Failure(int statusCode, string? error) => new(false, statusCode, error, null);
}

/// <summary>
/// Result of every write endpoint whose success response is a bare <c>{ ok: true }</c>: `PATCH
/// /portal/{id}` (update), `DELETE /portal/{id}` (remove), `POST /portal/{id}/watch`,
/// `POST /portal/{id}/block`, and `POST /portal/{id}/report`.
/// </summary>
internal sealed record PortalWriteResult(bool Ok, int StatusCode, string? Error)
{
    internal static PortalWriteResult Success(int statusCode) => new(true, statusCode, null);
    internal static PortalWriteResult Failure(int statusCode, string? error) => new(false, statusCode, error);
}
