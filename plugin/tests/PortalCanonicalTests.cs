// Off-game unit tests for PortalCanonical (SP-1c Task 3). Every expected string below is a GROUND-TRUTH
// VECTOR captured by running the REAL backend builders (services/stellar-portal/src/domain/canonical.ts)
// under `bun`, not hand-derived — see the exact `bun -e '...'` invocation and its raw output pasted in
// this session's task-3-report.md. If PortalCanonical's output ever diverges from these strings by even
// one byte, every ECDSA signature the plugin produces would fail the backend's verification silently
// (see services/stellar-portal/docs/api.md "Canonical payloads").
using System.Collections.Generic;
using Stellar.WorldScreen.Net;
using Xunit;

namespace Stellar.WorldScreen.Tests;

public class PortalCanonicalTests
{
    // ---- Heartbeat ----

    [Fact]
    public void Heartbeat_Normal_MatchesCapturedVector()
    {
        // Pins field order + fractional/negative/zero numbers (posX=1.5, posY=-2, posZ=0) +
        // multi-element visibleCharIds (comma-joined, given order preserved).
        var body = new HeartbeatBody(
            CharId: 1234,
            Region: "sea",
            MapId: 42,
            SceneId: 7,
            LineId: 1,
            PosX: 1.5,
            PosY: -2,
            PosZ: 0,
            VisibleCharIds: new List<long> { 10, 20, 30 },
            Nonce: "n1");

        Assert.Equal("1234|sea|42|7|1|1.5|-2|0|10,20,30|n1", PortalCanonical.Heartbeat(body));
    }

    [Fact]
    public void Heartbeat_NullOptionalsAndEmptyVisibleCharIds_RenderAsEmptyString()
    {
        // Pins null/absent -> "" (lineId, nonce) and an empty visibleCharIds list -> "" (not "0" / "null").
        var body = new HeartbeatBody(
            CharId: 999,
            Region: "jp",
            MapId: 1,
            SceneId: 2,
            LineId: null,
            PosX: 0,
            PosY: 0,
            PosZ: 0,
            VisibleCharIds: new List<long>(),
            Nonce: null);

        Assert.Equal("999|jp|1|2||0|0|0||", PortalCanonical.Heartbeat(body));
    }

    [Fact]
    public void Heartbeat_LineIdZero_RendersLiteralZero_NotEmpty()
    {
        // lineId=0 (the "no line id / AOI-fallback" case — the COMMON runtime value when SceneLineId
        // is unknown) must render as literal "0", DISTINCT from an absent/null lineId which renders "".
        // canonical.ts documents this distinction as meaningful; ground-truth vector captured from the
        // real backend via bun. (SP-1c Task 3 review nit.)
        var body = new HeartbeatBody(
            CharId: 1234,
            Region: "sea",
            MapId: 42,
            SceneId: 7,
            LineId: 0,
            PosX: 1.5,
            PosY: 2.5,
            PosZ: 3.5,
            VisibleCharIds: new List<long> { 10, 20 },
            Nonce: "n1");

        Assert.Equal("1234|sea|42|7|0|1.5|2.5|3.5|10,20|n1", PortalCanonical.Heartbeat(body));
    }

    // ---- Place ----

    [Fact]
    public void Place_Normal_MatchesCapturedVector()
    {
        var body = new PlaceBody(
            Region: "sea",
            MapId: 42,
            SceneId: 7,
            LineId: 1,
            PosX: 1.5,
            PosY: 2.5,
            PosZ: 3.5,
            Yaw: 90,
            OwnerCharId: 1234,
            SourceKind: "url",
            SourceUrl: "https://x/y",
            Nonce: "n1");

        Assert.Equal("sea|42|7|1|1.5|2.5|3.5|90|1234|url|https://x/y|n1", PortalCanonical.Place(body));
    }

    [Fact]
    public void Place_LineIdZero_RendersLiteralZero_NotEmpty()
    {
        // A signed place with lineId=0 (AOI-fallback / no readable line id) — the common runtime case —
        // renders literal "0", distinct from an absent/null lineId (""). Ground-truth from the backend.
        var body = new PlaceBody(
            Region: "sea", MapId: 42, SceneId: 7, LineId: 0,
            PosX: 1.5, PosY: 2.5, PosZ: 3.5, Yaw: 90, OwnerCharId: 1234,
            SourceKind: "url", SourceUrl: "https://x/y", Nonce: "n1");

        Assert.Equal("sea|42|7|0|1.5|2.5|3.5|90|1234|url|https://x/y|n1", PortalCanonical.Place(body));
    }

    [Fact]
    public void Place_PipeInSourceUrl_IsEscaped()
    {
        // Pins `|` -> `\|` escaping in a free-text field: original sourceUrl "a|b" becomes "a\|b".
        var body = new PlaceBody(
            Region: "sea", MapId: 1, SceneId: 1, LineId: 1,
            PosX: 1, PosY: 1, PosZ: 1, Yaw: 0, OwnerCharId: 1,
            SourceKind: "url", SourceUrl: "a|b", Nonce: "n");

        Assert.Equal("sea|1|1|1|1|1|1|0|1|url|a\\|b|n", PortalCanonical.Place(body));
    }

    [Fact]
    public void Place_BackslashInSourceUrl_IsEscaped()
    {
        // Pins `\` -> `\\` escaping: original sourceUrl "a\b" (one backslash) becomes "a\\b" (two).
        var body = new PlaceBody(
            Region: "sea", MapId: 1, SceneId: 1, LineId: 1,
            PosX: 1, PosY: 1, PosZ: 1, Yaw: 0, OwnerCharId: 1,
            SourceKind: "url", SourceUrl: "a\\b", Nonce: "n");

        Assert.Equal("sea|1|1|1|1|1|1|0|1|url|a\\\\b|n", PortalCanonical.Place(body));
    }

    [Fact]
    public void Place_BothBackslashAndPipeInSourceUrl_EscapesBackslashBeforePipe()
    {
        // Pins escape ORDER: original sourceUrl "a\|b" (one backslash, then pipe) must first double the
        // backslash (-> "a\\|b") and only THEN escape the pipe (-> "a\\\|b"). Escaping in the other
        // order would produce a different (and backend-incompatible) byte string.
        var body = new PlaceBody(
            Region: "sea", MapId: 1, SceneId: 1, LineId: 1,
            PosX: 1, PosY: 1, PosZ: 1, Yaw: 0, OwnerCharId: 1,
            SourceKind: "url", SourceUrl: "a\\|b", Nonce: "n");

        Assert.Equal("sea|1|1|1|1|1|1|0|1|url|a\\\\\\|b|n", PortalCanonical.Place(body));
    }

    [Fact]
    public void Place_NullAndAbsentOptionals_RenderAsEmptyString()
    {
        // Pins lineId=null, sourceKind=null, sourceUrl=absent, nonce=null all -> "" (never "null").
        var body = new PlaceBody(
            Region: "sea", MapId: 1, SceneId: 1, LineId: null,
            PosX: 1, PosY: 1, PosZ: 1, Yaw: 0, OwnerCharId: 1,
            SourceKind: null, SourceUrl: null, Nonce: null);

        Assert.Equal("sea|1|1||1|1|1|0|1|||", PortalCanonical.Place(body));
    }

    [Fact]
    public void Place_NegativeZeroPosition_RendersAsPlainZero_NotMinusZero()
    {
        // Pins the negative-zero trap: JS `String(-0) === "0"`, but .NET's round-trippable double
        // format prints -0.0 as "-0" — verified empirically (dotnet run probe) to differ from JS.
        // FormatNumber must special-case value==0 (true for both signs of zero) to match the backend.
        var body = new PlaceBody(
            Region: "sea", MapId: 1, SceneId: 1, LineId: 1,
            PosX: -0.0, PosY: 1, PosZ: 1, Yaw: 0, OwnerCharId: 1,
            SourceKind: "url", SourceUrl: "u", Nonce: "n");

        Assert.Equal("sea|1|1|1|0|1|1|0|1|url|u|n", PortalCanonical.Place(body));
    }

    // ---- Moderation ----

    [Fact]
    public void Moderation_Block_Normal_MatchesCapturedVector()
    {
        var body = new ModerationBody(Action: "block", PortalId: "portal-abc", CharId: 1234, Reason: null, Nonce: "n1");

        Assert.Equal("block|portal-abc|1234||n1", PortalCanonical.Moderation(body));
    }

    [Fact]
    public void Moderation_ReportPipeInReason_IsEscaped()
    {
        var body = new ModerationBody(Action: "report", PortalId: "portal-abc", CharId: 1234, Reason: "x|y", Nonce: "n1");

        Assert.Equal("report|portal-abc|1234|x\\|y|n1", PortalCanonical.Moderation(body));
    }

    [Fact]
    public void Moderation_ReportBothBackslashAndPipeInReason_NullNonce_EscapesInOrder()
    {
        var body = new ModerationBody(Action: "report", PortalId: "portal-abc", CharId: 1234, Reason: "a\\|b", Nonce: null);

        Assert.Equal("report|portal-abc|1234|a\\\\\\|b|", PortalCanonical.Moderation(body));
    }

    // ---- Delete ----

    [Fact]
    public void Delete_Normal_MatchesCapturedVector()
    {
        var body = new DeleteBody(PortalId: "portal-abc", OwnerCharId: 1234, Nonce: "n1");

        Assert.Equal("delete|portal-abc|1234|n1", PortalCanonical.Delete(body));
    }

    [Fact]
    public void Delete_NullNonce_RendersAsEmptyString()
    {
        var body = new DeleteBody(PortalId: "portal-abc", OwnerCharId: 1234, Nonce: null);

        Assert.Equal("delete|portal-abc|1234|", PortalCanonical.Delete(body));
    }
}
