using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Abstractions.Services;
using Stellar.WorldScreen.Net;
using Stellar.WorldScreen.World;
using Xunit;

namespace Stellar.WorldScreen.Tests;

public class InstanceProbeTests
{
    // ───────────────────────── Assemble (pure) ─────────────────────────

    [Fact]
    public void Assemble_MapsAllFieldsAndRoundsPosition_ProducesExpectedHeartbeatBody()
    {
        var pos = new Position3D(1.1f, -2.35f, 3f);
        var visible = new List<long> { 111, 222 };

        var body = InstanceProbe.Assemble(
            charId: 1234,
            region: "sea",
            mapId: 42,
            sceneId: 7,
            lineId: 3,
            pos: pos,
            visibleCharIds: visible,
            nonce: "abc");

        Assert.Equal(1234, body.CharId);
        Assert.Equal("sea", body.Region);
        Assert.Equal(42, body.MapId);
        Assert.Equal(7, body.SceneId);
        Assert.Equal(3, body.LineId);
        // dirty floats round the SAME way PortalClient.RoundSignedCanonicalFloat does — no second
        // rounding implementation.
        Assert.Equal(PortalClient.RoundSignedCanonicalFloat(1.1f), body.PosX);
        Assert.Equal(1.1, body.PosX);
        Assert.Equal(PortalClient.RoundSignedCanonicalFloat(-2.35f), body.PosY);
        Assert.Equal(-2.35, body.PosY);
        Assert.Equal(3.0, body.PosZ);
        Assert.Same(visible, body.VisibleCharIds);
        Assert.Equal(new List<long> { 111, 222 }, body.VisibleCharIds);
        Assert.Equal("abc", body.Nonce);
    }

    [Fact]
    public void Assemble_NoNonceGiven_DefaultsToNull()
    {
        var body = InstanceProbe.Assemble(1, "sea", 1, 1, 0, Position3D.Zero, Array.Empty<long>());
        Assert.Null(body.Nonce);
    }

    [Fact]
    public void Assemble_EmptyVisibleCharIds_ProducesEmptyList_NoCrash()
    {
        var body = InstanceProbe.Assemble(1, "sea", 1, 1, 0, Position3D.Zero, Array.Empty<long>());
        Assert.Empty(body.VisibleCharIds);
    }

    // ───────────────────────── TryParseSceneId (pure) ─────────────────────────

    [Fact]
    public void TryParseSceneId_NumericString_ReturnsParsedInt()
    {
        Assert.Equal(1234, InstanceProbe.TryParseSceneId("1234"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("MainCity")]
    [InlineData("12.5")]
    public void TryParseSceneId_NotNumeric_ReturnsNull(string? raw)
    {
        Assert.Null(InstanceProbe.TryParseSceneId(raw));
    }

    // ───────────────────────── MapVisibleCharIds (pure) ─────────────────────────

    [Fact]
    public void MapVisibleCharIds_MapsUidToLong_PreservesOrder()
    {
        // EntityId.Uid = (int)(Value >> 16); low 16 bits (640) mark it as a player, per EntityId.cs.
        var a = new EntityId((555L << 16) | 640);
        var b = new EntityId((777L << 16) | 640);

        var mapped = InstanceProbe.MapVisibleCharIds(new[] { a, b });

        Assert.Equal(new long[] { 555, 777 }, mapped);
    }

    [Fact]
    public void MapVisibleCharIds_Empty_ReturnsEmpty()
    {
        Assert.Empty(InstanceProbe.MapVisibleCharIds(Array.Empty<EntityId>()));
    }

    // ───────────────────────── Gather (thin glue over a fake IPluginServices) ─────────────────────────

    [Fact]
    public void Gather_PlayerNotAvailable_ReturnsNull()
    {
        var services = new FakePluginServices();
        services.Player.IsAvailable = false;
        services.Player.CharId = 1234;
        services.Client.IsWorldActive = true;
        services.Client.CurrentSceneName = "7";

        Assert.Null(InstanceProbe.Gather(services));
    }

    [Fact]
    public void Gather_CharIdZero_ReturnsNull()
    {
        var services = new FakePluginServices();
        services.Player.IsAvailable = true;
        services.Player.CharId = 0;
        services.Client.IsWorldActive = true;
        services.Client.CurrentSceneName = "7";

        Assert.Null(InstanceProbe.Gather(services));
    }

    [Fact]
    public void Gather_NotWorldActive_ReturnsNull()
    {
        var services = new FakePluginServices();
        services.Player.IsAvailable = true;
        services.Player.CharId = 1234;
        services.Client.IsWorldActive = false;
        services.Client.CurrentSceneName = "7";

        Assert.Null(InstanceProbe.Gather(services));
    }

    [Fact]
    public void Gather_CurrentSceneNameNotNumeric_ReturnsNull()
    {
        var services = new FakePluginServices();
        services.Player.IsAvailable = true;
        services.Player.CharId = 1234;
        services.Client.IsWorldActive = true;
        services.Client.CurrentSceneName = "MainCity";

        Assert.Null(InstanceProbe.Gather(services));
    }

    [Fact]
    public void Gather_HappyPath_MapsEverything_UsesSceneTableMapId()
    {
        var services = new FakePluginServices();
        services.Player.IsAvailable = true;
        services.Player.CharId = 1234;
        services.Player.Position = new Position3D(1.1f, 0f, -2.35f);
        services.Client.IsWorldActive = true;
        services.Client.CurrentSceneName = "7";
        services.Client.SceneLineId = 3;
        services.Env.RegionCode = "sea";
        services.Data.WorldFake.Scenes[7] = new SceneInfo(7, "Some Scene", 42, 0);
        services.Entities.VisiblePlayers = new[]
        {
            new EntityId((10L << 16) | 640),
            new EntityId((20L << 16) | 640),
        };

        var body = InstanceProbe.Gather(services);

        Assert.NotNull(body);
        Assert.Equal(1234, body!.CharId);
        Assert.Equal("sea", body.Region);
        Assert.Equal(42, body.MapId); // resolved from the fake scene table, not guessed
        Assert.Equal(7, body.SceneId);
        Assert.Equal(3, body.LineId);
        Assert.Equal(1.1, body.PosX);
        Assert.Equal(0.0, body.PosY);
        Assert.Equal(-2.35, body.PosZ);
        Assert.Equal(new long[] { 10, 20 }, body.VisibleCharIds);
        Assert.Null(body.Nonce); // PortalClient stamps its own nonce per-request
    }

    [Fact]
    public void Gather_SceneNotInTable_FallsBackToMapIdZero_Consistently()
    {
        var services = new FakePluginServices();
        services.Player.IsAvailable = true;
        services.Player.CharId = 1234;
        services.Client.IsWorldActive = true;
        services.Client.CurrentSceneName = "999";
        // services.Data.WorldFake.Scenes intentionally left empty — scene table doesn't resolve this id.

        var body = InstanceProbe.Gather(services);

        Assert.NotNull(body);
        Assert.Equal(0, body!.MapId);
        Assert.Equal(999, body.SceneId);
    }

    [Fact]
    public void Gather_NoVisiblePlayers_ReturnsEmptyList_NoCrash()
    {
        var services = new FakePluginServices();
        services.Player.IsAvailable = true;
        services.Player.CharId = 1234;
        services.Client.IsWorldActive = true;
        services.Client.CurrentSceneName = "7";
        services.Entities.VisiblePlayers = Array.Empty<EntityId>();

        var body = InstanceProbe.Gather(services);

        Assert.NotNull(body);
        Assert.Empty(body!.VisibleCharIds);
    }

    // ───────────────────────── Fake IPluginServices (minimal — only the facets Gather reads are real) ─────────────────────────

    private sealed class FakePlayerState : IPlayerState
    {
        public bool IsAvailable { get; set; }
        public string? Name => null;
        public int Level => 0;
        public int Profession => 0;
        public long CharId { get; set; }
        public int Health => 0;
        public int MaxHealth => 0;
        public int Stamina => 0;
        public int MaxStamina => 0;
        public Position3D Position { get; set; }
    }

    private sealed class FakeClientState : IClientState
    {
        public bool IsLoggedIn => true;
        public string? CurrentSceneName { get; set; }
        public event Action Login { add { } remove { } }
        public event Action Logout { add { } remove { } }
        public event Action<string?> SceneChanged { add { } remove { } }
        public GamePhase Phase => GamePhase.World;
        public event Action<PhaseChange> PhaseChanged { add { } remove { } }
        public bool IsWorldActive { get; set; }
        public GameUIState UiState => GameUIState.None;
        public int SceneLineId { get; set; }
    }

    private sealed class FakeGameEnvironment : IGameEnvironment
    {
        public GameRegion Region => GameRegion.Unknown;
        public string RegionCode { get; set; } = "unknown";
        public string GameVersion => "";
    }

    private sealed class FakeEntityDetail : IEntityDetail
    {
        public IReadOnlyList<EntityId> VisiblePlayers { get; set; } = Array.Empty<EntityId>();
        public IReadOnlyDictionary<int, long> GetAttributes(EntityId entity) => throw new NotImplementedException();
        public long GetAttribute(EntityId entity, int attrId) => throw new NotImplementedException();
        public IReadOnlyList<EquippedItem> GetEquipment(EntityId entity) => throw new NotImplementedException();
        public IReadOnlyList<FashionEntry> GetFashion(EntityId entity) => throw new NotImplementedException();
        public SocialSnapshot? GetSocialSnapshot(EntityId entity) => throw new NotImplementedException();
        public void RefreshSocialSnapshot(EntityId entity) => throw new NotImplementedException();
        public IReadOnlyList<EntityId> GetVisiblePlayers() => VisiblePlayers;
    }

    private sealed class FakeGameDataWorld : IGameDataWorld
    {
        public Dictionary<int, SceneInfo> Scenes { get; } = new();
        public MonsterInfo? GetMonster(int id) => throw new NotImplementedException();
        public MonsterInfo? GetMonsterByEntity(EntityId entityId) => throw new NotImplementedException();
        public NpcInfo? GetNpc(int id) => throw new NotImplementedException();
        public SceneInfo? GetScene(int id) => Scenes.TryGetValue(id, out var s) ? s : null;
        public MapInfo? GetMap(int id) => throw new NotImplementedException();
    }

    private sealed class FakeGameData : IGameData
    {
        public bool IsAvailable => true;
        public FakeGameDataWorld WorldFake { get; } = new();
        public IGameDataCombat Combat => throw new NotImplementedException();
        public IGameDataInventory Inventory => throw new NotImplementedException();
        public IGameDataEquip Equip => throw new NotImplementedException();
        public IGameDataWorld World => WorldFake;
        public IGameDataProgress Progress => throw new NotImplementedException();
    }

    /// <summary>
    /// Fakes ONLY the five facets InstanceProbe.Gather reads (PlayerState, ClientState,
    /// GameEnvironment, EntityDetail, GameData); every other IPluginServices member throws if
    /// touched — Gather must never need them, and a throw makes an accidental new dependency loud.
    /// </summary>
    private sealed class FakePluginServices : IPluginServices
    {
        public readonly FakePlayerState Player = new();
        public readonly FakeClientState Client = new();
        public readonly FakeGameEnvironment Env = new();
        public readonly FakeEntityDetail Entities = new();
        public readonly FakeGameData Data = new();

        public IPlayerState PlayerState => Player;
        public IClientState ClientState => Client;
        public IGameEnvironment GameEnvironment => Env;
        public IEntityDetail EntityDetail => Entities;
        public IGameData GameData => Data;

        public IPluginLog Log => throw new NotImplementedException();
        public IFramework Framework => throw new NotImplementedException();
        public IPlayerStats PlayerStats => throw new NotImplementedException();
        public IInventory Inventory => throw new NotImplementedException();
        public IModuleEquip ModuleEquip => throw new NotImplementedException();
        public ILoadout Loadout => throw new NotImplementedException();
        public IExchange Market => throw new NotImplementedException();
        public INotifications Notifications => throw new NotImplementedException();
        public IPluginConfig Config => throw new NotImplementedException();
        public IGameEvents GameEvents => throw new NotImplementedException();
        public IChat Chat => throw new NotImplementedException();
        public ICombatSnapshot CombatSnapshot => throw new NotImplementedException();
        public ICombatLookup CombatLookup => throw new NotImplementedException();
        public ICombatEvents CombatEvents => throw new NotImplementedException();
        public ICombatSpec CombatSpec => throw new NotImplementedException();
        public IPartySnapshot PartySnapshot => throw new NotImplementedException();
        public IPartyRoster PartyRoster => throw new NotImplementedException();
        public IPartyEvents PartyEvents => throw new NotImplementedException();
        public IPartyControl PartyControl => throw new NotImplementedException();
        public ITheme Theme => throw new NotImplementedException();
        public IHotkeys Hotkeys => throw new NotImplementedException();
        public INamedTheme NamedTheme => throw new NotImplementedException();
        public INativeUiHost NativeUi => throw new NotImplementedException();
        public IWindowHost Windows => throw new NotImplementedException();
        public ILauncher Launcher => throw new NotImplementedException();
        public IGameAssets GameAssets => throw new NotImplementedException();
        public IResonanceState Resonance => throw new NotImplementedException();
        public IGameDataResonance ResonanceData => throw new NotImplementedException();
        public IWardrobe Wardrobe => throw new NotImplementedException();
        public IWardrobePreview WardrobePreview => throw new NotImplementedException();
        public IEntityContextMenu EntityContextMenu => throw new NotImplementedException();
        public IEntityPortrait EntityPortrait => throw new NotImplementedException();
        public IProfileCardActions ProfileCardActions => throw new NotImplementedException();
        public IPluginExchange Exchange => throw new NotImplementedException();
        public INoticeTips NoticeTips => throw new NotImplementedException();
        public IDungeonState Dungeon => throw new NotImplementedException();
        public IRunTimer RunTimer => throw new NotImplementedException();
        public IEntityTransforms EntityTransforms => throw new NotImplementedException();
        IPluginDataStore IPluginServices.Data => throw new NotImplementedException();
        public ILua Lua => throw new NotImplementedException();
        public IHarmonyHost Harmony => throw new NotImplementedException();
        public ILocalization Localization => throw new NotImplementedException();
        public IDeepSlumber DeepSlumber => throw new NotImplementedException();
        public IBossVitals BossVitals => throw new NotImplementedException();
    }
}
