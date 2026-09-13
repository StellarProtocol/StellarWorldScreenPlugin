using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.WorldScreen.Net;

namespace Stellar.WorldScreen.World;

/// <summary>
/// Assembles the backend heartbeat body (<see cref="HeartbeatBody"/>) that <c>PortalClient</c> sends
/// every ~5s (see <c>PortalClient.Start</c>'s <c>gather</c> callback), from the framework's read APIs
/// each tick.
///
/// Deliberately split in two so the interesting logic is unit-testable without any framework/game
/// service:
/// <list type="bullet">
/// <item><see cref="Assemble"/> (+ <see cref="TryParseSceneId"/>/<see cref="MapVisibleCharIds"/>) — pure,
/// takes already-resolved raw values, builds the DTO. This is the main unit-tested surface.</item>
/// <item><see cref="Gather"/> — thin glue: reads <see cref="IPluginServices"/> and calls the pure
/// helpers above. Straight-line, no branching beyond the not-in-world guard clauses.</item>
/// </list>
/// </summary>
internal static class InstanceProbe
{
    /// <summary>
    /// Builds a <see cref="HeartbeatBody"/> from already-resolved raw values. Pure (no service
    /// dependency). Positions are rounded via <see cref="PortalClient.RoundSignedCanonicalFloat"/> — the
    /// SAME rounding a signed Place request uses (no second rounding implementation) — so a heartbeat's
    /// reported position matches what a later signed placement would send for the same spot. (A
    /// heartbeat itself is not signature-critical for position the way Place is; rounding here is for
    /// consistency, not signature correctness.)
    /// </summary>
    internal static HeartbeatBody Assemble(
        long charId,
        string region,
        int mapId,
        int sceneId,
        int lineId,
        Position3D pos,
        IReadOnlyList<long> visibleCharIds,
        string? nonce = null)
    {
        return new HeartbeatBody(
            CharId: charId,
            Region: region,
            MapId: mapId,
            SceneId: sceneId,
            LineId: lineId,
            PosX: PortalClient.RoundSignedCanonicalFloat(pos.X),
            PosY: PortalClient.RoundSignedCanonicalFloat(pos.Y),
            PosZ: PortalClient.RoundSignedCanonicalFloat(pos.Z),
            VisibleCharIds: visibleCharIds,
            Nonce: nonce);
    }

    /// <summary>
    /// Parses <see cref="IClientState.CurrentSceneName"/> into the numeric scene id it currently carries
    /// ("currently a numeric scene id, not a friendly name" per that property's own doc comment).
    /// Defensive: null or any non-numeric value (e.g. a friendly name shipped later, or the field not yet
    /// populated at boot) yields null rather than a garbage sceneId.
    /// </summary>
    internal static int? TryParseSceneId(string? currentSceneName) =>
        int.TryParse(currentSceneName, out var sceneId) ? sceneId : null;

    /// <summary>
    /// Maps each visible player's <see cref="EntityId.Uid"/> (the entity id's high bits — see
    /// EntityId.cs; for a player entity this IS the CharId) to a plain long CharId list, preserving
    /// <paramref name="visiblePlayers"/>'s order.
    /// </summary>
    internal static IReadOnlyList<long> MapVisibleCharIds(IReadOnlyList<EntityId> visiblePlayers)
    {
        var result = new List<long>(visiblePlayers.Count);
        foreach (var entity in visiblePlayers) result.Add(entity.Uid);
        return result;
    }

    /// <summary>
    /// Reads the framework's read APIs and assembles this tick's heartbeat body, or null when not
    /// meaningfully in-world — never a garbage heartbeat from the title screen / char-select / the
    /// world origin. Thin glue only: every interesting decision lives in the pure helpers above.
    /// </summary>
    internal static HeartbeatBody? Gather(IPluginServices services)
    {
        var player = services.PlayerState;
        if (!player.IsAvailable || player.CharId == 0) return null;

        var clientState = services.ClientState;
        if (!clientState.IsWorldActive) return null;

        var sceneId = TryParseSceneId(clientState.CurrentSceneName);
        if (sceneId is null) return null;

        // mapId: IClientState has no direct mapId. IGameData.World.GetScene(sceneId) IS a real
        // scene→map lookup (SceneInfo.MapId) — use it. Fall back to 0 (consistently, for every client)
        // only when the scene table can't resolve this id (data not yet loaded / an unmapped scene) —
        // the backend's instance key `region:mapId:sceneId:lineId` still groups co-located players
        // correctly in that case, since sceneId alone already implies the map.
        var mapId = services.GameData.World.GetScene(sceneId.Value)?.MapId ?? 0;

        var visibleCharIds = MapVisibleCharIds(services.EntityDetail.GetVisiblePlayers());

        return Assemble(
            charId: player.CharId,
            region: services.GameEnvironment.RegionCode,
            mapId: mapId,
            sceneId: sceneId.Value,
            lineId: clientState.SceneLineId,
            pos: player.Position,
            visibleCharIds: visibleCharIds,
            nonce: null);
    }
}
