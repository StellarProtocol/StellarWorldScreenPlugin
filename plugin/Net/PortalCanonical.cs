// Canonical pipe-delimited payload builders — the C# half of a CROSS-REPO INVARIANT. Must mirror
// stellar-portal's `src/domain/canonical.ts` (canonicalHeartbeat/canonicalPlace/canonicalModeration/
// canonicalDelete + the shared field()/esc() helpers) EXACTLY — field order, escaping order, and
// number formatting. This is the exact UTF-8 byte string InstallKey.SignInstall's ECDSA signature
// covers (see services/stellar-portal/docs/api.md "Canonical payloads"); a single differing byte on
// either side makes the backend's `verifyInstallSig` disagree with what the plugin actually signed —
// no error on the wire, just a silent 401 on every signed write.
//
// PURE BCL — no UnityEngine/Stellar.Abstractions types — compiled directly into the off-game unit-test
// project (tests/Stellar.WorldScreen.Tests.csproj), so this file (and its ground-truth test vectors,
// captured from the real canonical.ts via `bun`) builds and runs without the game or the framework
// present. No signing/HTTP here — that is SP-1c Task 4's PortalClient.
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Stellar.WorldScreen.Net;

/// <summary>
/// Builds the canonical pipe-delimited strings signed for each World Portal write/read, byte-matching
/// stellar-portal's <c>src/domain/canonical.ts</c>.
/// </summary>
internal static class PortalCanonical
{
    /// <summary>
    /// `POST /heartbeat` canonical field order (CROSS-REPO INVARIANT):
    /// <c>charId|region|mapId|sceneId|lineId|posX|posY|posZ|visibleCharIds|nonce</c>.
    /// <c>visibleCharIds</c> is comma-joined in the list's own given order (no sort/dedupe); an empty
    /// list renders as the empty string, same as any other null/absent field.
    /// </summary>
    internal static string Heartbeat(HeartbeatBody body) => string.Join(
        "|",
        Field(body.CharId),
        Field(body.Region),
        Field(body.MapId),
        Field(body.SceneId),
        Field(body.LineId),
        Field(body.PosX),
        Field(body.PosY),
        Field(body.PosZ),
        JoinCharIds(body.VisibleCharIds),
        Field(body.Nonce));

    /// <summary>
    /// `POST /portal` (place) and `PATCH /portal/{id}` (update) canonical field order (CROSS-REPO
    /// INVARIANT):
    /// <c>region|mapId|sceneId|lineId|posX|posY|posZ|yaw|ownerCharId|sourceKind|sourceUrl|nonce</c>.
    /// </summary>
    internal static string Place(PlaceBody body) => string.Join(
        "|",
        Field(body.Region),
        Field(body.MapId),
        Field(body.SceneId),
        Field(body.LineId),
        Field(body.PosX),
        Field(body.PosY),
        Field(body.PosZ),
        Field(body.Yaw),
        Field(body.OwnerCharId),
        Field(body.SourceKind),
        Field(body.SourceUrl),
        Field(body.Nonce));

    /// <summary>
    /// `POST /portal/{id}/block` and `POST /portal/{id}/report` canonical field order (CROSS-REPO
    /// INVARIANT): <c>action|portalId|charId|reason|nonce</c>.
    /// </summary>
    internal static string Moderation(ModerationBody body) => string.Join(
        "|",
        Field(body.Action),
        Field(body.PortalId),
        Field(body.CharId),
        Field(body.Reason),
        Field(body.Nonce));

    /// <summary>
    /// `DELETE /portal/{id}` canonical field order (CROSS-REPO INVARIANT):
    /// <c>delete|portalId|ownerCharId|nonce</c>. The leading literal <c>"delete"</c> action tag keeps
    /// this canonical from colliding with any other builder's output — it is NOT itself escaped
    /// (canonical.ts emits it as a raw string-literal array element, not through <c>field()</c>).
    /// </summary>
    internal static string Delete(DeleteBody body) => string.Join(
        "|",
        "delete",
        Field(body.PortalId),
        Field(body.OwnerCharId),
        Field(body.Nonce));

    /// <summary><c>visibleCharIds.map(String).join(",")</c> — canonical.ts joins the RAW (unescaped)
    /// values; numeric char ids never contain `\`/`|`, so this is safe.</summary>
    private static string JoinCharIds(IReadOnlyList<long> ids) =>
        string.Join(",", ids.Select(id => id.ToString(CultureInfo.InvariantCulture)));

    private static string Field(string? value) => value is null ? string.Empty : Esc(value);

    private static string Field(long value) => Esc(value.ToString(CultureInfo.InvariantCulture));

    private static string Field(int value) => Esc(value.ToString(CultureInfo.InvariantCulture));

    private static string Field(int? value) =>
        value is null ? string.Empty : Field(value.Value);

    private static string Field(double value) => Esc(FormatNumber(value));

    /// <summary>Matches JS `String(n)` for the value ranges this protocol uses (world-space positions,
    /// yaw degrees): a `.` decimal separator always, no thousands separators, no scientific notation,
    /// standard IEEE-754 double round-trip. Two deliberate corrections over a bare
    /// <c>ToString("R", CultureInfo.InvariantCulture)</c>, both verified against captured `bun` vectors
    /// (see PortalCanonicalTests): negative zero — .NET's round-trippable format prints
    /// <c>-0.0</c> as <c>"-0"</c> while JS `String(-0)` is `"0"` (ECMAScript's ToString(Number)
    /// algorithm special-cases both zeros to `"0"`) — guarded explicitly below since `value == 0` is
    /// true for both signs of zero in IEEE-754 double comparison.</summary>
    private static string FormatNumber(double value) =>
        value == 0 ? "0" : value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Escapes a field VALUE so it cannot forge the `|` field delimiter: `\`→`\\` FIRST, then
    /// `|`→`\|`. Order matters (the escape char itself must be escaped before the delimiter is) — the
    /// backend's `esc()` in canonical.ts applies the same two replacements in the same order.</summary>
    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("|", "\\|");
}
