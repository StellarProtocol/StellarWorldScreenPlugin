// DTOs consumed by PortalCanonical's builders — mirror stellar-portal's canonical.ts request-body
// interfaces field-for-field (HeartbeatBody, PlaceBody, ModerationBody, DeleteBody). These types exist
// ONLY to carry the values into the canonical-string builders (SP-1c Task 3); PortalClient's actual
// wire bodies (JSON, signing, HTTP — SP-1c Task 4) are a separate concern and may add fields (pubkey,
// sig, ownerName, …) that never enter the canonical string. See
// services/stellar-portal/src/domain/canonical.ts and services/stellar-portal/docs/api.md "Canonical
// payloads" for the cross-repo invariant these mirror.
//
// PURE BCL — no UnityEngine/Stellar.Abstractions types — compiled directly into the off-game unit-test
// project (tests/Stellar.WorldScreen.Tests.csproj) alongside PortalCanonical.cs.
using System.Collections.Generic;

namespace Stellar.WorldScreen.Net;

/// <summary>
/// `POST /heartbeat` body — mirrors canonical.ts's <c>HeartbeatBody</c>. <see cref="LineId"/> and
/// <see cref="Nonce"/> are optional at the type level only for canonical.ts's uniform null-handling
/// (a `null`/absent optional field canonicalizes to an empty string) — in practice the plugin always
/// sends definite values.
/// </summary>
/// <param name="VisibleCharIds">The caller's own AOI-visible char-id list, in the EXACT order it must
/// be sent — the canonical builder does NOT sort or dedupe. Callers must serialize this in one
/// consistent order (e.g. AOI-probe enumeration order) so the same heartbeat always canonicalizes
/// identically.</param>
internal sealed record HeartbeatBody(
    long CharId,
    string Region,
    int MapId,
    int SceneId,
    int? LineId,
    double PosX,
    double PosY,
    double PosZ,
    IReadOnlyList<long> VisibleCharIds,
    string? Nonce);

/// <summary>
/// `POST /portal` (place) body — mirrors canonical.ts's <c>PlaceBody</c>. `PATCH /portal/{id}` (update)
/// reuses this same shape, signed over the FULL intended post-update state (not a partial diff) — see
/// docs/api.md.
/// </summary>
internal sealed record PlaceBody(
    string Region,
    int MapId,
    int SceneId,
    int? LineId,
    double PosX,
    double PosY,
    double PosZ,
    double Yaw,
    long OwnerCharId,
    string? SourceKind,
    string? SourceUrl,
    string? Nonce);

/// <summary>
/// `POST /portal/{id}/block` and `POST /portal/{id}/report` body — mirrors canonical.ts's
/// <c>ModerationBody</c>. <see cref="Action"/> is one of the two literal values the backend accepts:
/// <c>"block"</c> or <c>"report"</c> (mirrors the TS union type <c>"block" | "report"</c> — kept as a
/// plain string here rather than an enum so the canonical builder cannot silently diverge from the
/// backend's exact literal spelling via an enum-to-string mapping). <see cref="Reason"/> is meaningful
/// only for <c>"report"</c> (empty/absent for <c>"block"</c>).
/// </summary>
internal sealed record ModerationBody(
    string Action,
    string PortalId,
    long CharId,
    string? Reason,
    string? Nonce);

/// <summary>
/// `DELETE /portal/{id}` body — mirrors canonical.ts's <c>DeleteBody</c>. <see cref="PortalId"/> comes
/// from the URL path on the wire; it is still signed here as part of the canonical string.
/// </summary>
internal sealed record DeleteBody(
    string PortalId,
    long OwnerCharId,
    string? Nonce);

/// <summary>`PATCH /portal/{id}/playback` body (SP-2b) — the owner-DJ's playback state. Mirrors
/// stellar-portal's `PlaybackBody`. <see cref="PortalId"/> comes from the URL path on the wire; it is
/// still signed here as part of the canonical string.</summary>
internal sealed record PlaybackBody(
    string PortalId,
    int Index,
    long PositionMs,
    bool Playing,
    string? Nonce);
