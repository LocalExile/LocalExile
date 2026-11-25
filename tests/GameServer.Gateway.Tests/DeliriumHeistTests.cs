using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using GameServer.Core;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;
using GameServer.Modules.Delirium;
using Xunit;

namespace GameServer.Gateway.Tests;

public class DeliriumHeistTests
{
    [Fact]
    public void Delirium_mirror_ruleset_encoding_progress_and_fog_math()
    {
        AfflictionRuleset.LoadGameData();
        Assert.Equal((byte)102, AfflictionRuleset.EncounterRow);
        Assert.Equal((byte)52, AfflictionRuleset.PreloadGroup);
        Assert.Equal((byte)103, AfflictionRuleset.Row);
        Assert.Equal(Convert.FromHexString("00DF0104A90040000000"), AfflictionRuleset.FogEnvironment(true));
        Assert.Equal(Convert.FromHexString("00DF0104A900C0000000"), AfflictionRuleset.FogEnvironment(false));

        var beach = new AreaStatsRequest("Exile", "Standard", AreaTransitionCatalog.HashOf("MapWorldsBeach")!.Value, null)
        {
            Seed = 314159,
            AreaStats = new Dictionary<uint, int> { [Stat.MapAfflictionLeague] = 1 },
        };
        var terrain = Assert.Single(DeliriumMirrorEncounter.PlanTerrain(beach)!);
        Assert.Equal(2161, terrain.Plugin);
        Assert.Equal(new byte[] { 0 }, terrain.State);
        Assert.True(GameServer.Data.Terrain.Generation.LevelGenerator.SupportsPluginState(terrain.Plugin, terrain.State, null, null));
        Assert.False(GameServer.Data.Terrain.Generation.LevelGenerator.SupportsPluginState(terrain.Plugin, [1], null, null));
        Assert.Null(DeliriumMirrorEncounter.PlanTerrain(beach with
            { AreaStats = new Dictionary<uint, int> { [Stat.MapDisableDeliriumFromChance] = 1 } }));
        Assert.Null(DeliriumMirrorEncounter.PlanTerrain(beach with
            { AreaStats = new Dictionary<uint, int> { [Stat.MapAfflictionLeague] = 1, [Stat.MapDisableLeagues] = 1 } }));

        var islands = new AreaLayout(beach.Area, "MapWorldsBeach", 138, 69, 20, 24,
            (x, y) => x >= 0 && x < 138 && y >= 0 && y < 69 && (x < 30 || x > 75), []);
        var regions = MirrorFogRegions.Build(islands, 20, 24);
        Assert.Equal(2, regions.Length);
        Assert.Equal((20, 24, 0f), (regions[0].OriginX, regions[0].OriginY, regions[0].DistanceOffset));
        Assert.True(regions[0].MaxX < regions[1].MinX && regions[1].DistanceOffset > 0);

        var encounter = new DeliriumMirrorEncounter();
        var player = new Player(islands, 1, beach.Seed) { Instance = new() };
        Assert.Empty(encounter.BeforeEnterMessages(player));
        Assert.Null(encounter.EntryObject(player));
        player.Instance.TerrainPluginStates(() => [terrain]);
        player.Instance.AreaStats(() => beach.AreaStats!);
        var entry = Assert.Single(encounter.BeforeEnterMessages(player));
        var mirror = encounter.For(player);
        Assert.NotEqual(0u, mirror.EntityId);
        var birth = encounter.EntryObject(player)!.Value;
        Assert.Equal((mirror.EntityId, DeliriumMirrorEncounter.MirrorPath), (birth.Id, birth.Type));
        Assert.Equal(2, birth.Object.Blob()[0]);
        Assert.Empty(birth.Object.StateMachine!.Overridden);
        Assert.Equal(AfflictionRuleset.EncounterSnapshot(false, false, -1.8f, -1.8f, float.PositiveInfinity,
            0, false, mirror.MirrorX, mirror.MirrorY, mirror.EntityId, mirror.Regions!, []), entry.Message);
        var peer = new Player(islands, 2, beach.Seed) { Instance = player.Instance };
        Assert.Equal(entry.Message, Assert.Single(encounter.BeforeEnterMessages(peer)).Message);
        Assert.Equal(birth.Id, encounter.EntryObject(peer)!.Value.Id);
        var reloaded = new DeliriumMirrorEncounter();
        Assert.Equal(entry.Message, Assert.Single(reloaded.BeforeEnterMessages(peer)).Message);
        Assert.Equal(birth.Id, reloaded.EntryObject(peer)!.Value.Id);

        var saved = System.Text.Json.Nodes.JsonNode.Parse(player.Instance.ContentState["Delirium.Mirror"])!;
        saved["Stage"] = (int)DeliriumMirrorEncounter.MirrorStage.Active;
        saved["OuterElapsed"] = 27f;
        saved["InnerElapsed"] = 2f;
        saved["InnerWaveSent"] = true; // Maps saved by the previous implementation.
        player.Instance.ContentState["Delirium.Mirror"] = saved.ToJsonString();
        var resumed = Assert.Single(new DeliriumMirrorEncounter().BeforeEnterMessages(peer)).Message;
        Assert.Equal(27f, BinaryPrimitives.ReadSingleLittleEndian(resumed.AsSpan(7)));
        Assert.Equal(float.PositiveInfinity, BinaryPrimitives.ReadSingleLittleEndian(resumed.AsSpan(15)));

        var region = new AfflictionRuleset.FogRegion(0, 0, 200, 200, 50, 60, 0f);
        var tile = new AfflictionRuleset.RewardTile(Type: 0, Current: 12, Max: 18, Count: 2);
        byte[] snap = AfflictionRuleset.EncounterSnapshot(
            active: true, ended: false, outerElapsed: 3.5f, innerElapsed: 0f,
            innerDelay: float.PositiveInfinity, pauseTimer: 0f, paused: false,
            mirrorX: 50, mirrorY: 60, mirrorEntityId: 0x42, [region], [tile])!;

        // S->C 0x02c9 header: u16 BE 0x02c9, u8 row (102), compact-uint payload length (75 = 33 + 28 + 1 + 13).
        Assert.Equal(InstanceRulesets.DataId, BinaryPrimitives.ReadUInt16BigEndian(snap.AsSpan(0, 2)));
        Assert.Equal(102, snap[2]);
        Assert.Equal(75, snap[3]);
        Assert.Equal(0, snap[4]); // mode 0
        Assert.Equal(1, snap[5]); // active
        Assert.Equal(0, snap[6]); // ended
        Assert.Equal(3.5f, BinaryPrimitives.ReadSingleLittleEndian(snap.AsSpan(7, 4)));
        Assert.Equal(float.PositiveInfinity, BinaryPrimitives.ReadSingleLittleEndian(snap.AsSpan(15, 4)));
        Assert.Equal(50, BinaryPrimitives.ReadInt32LittleEndian(snap.AsSpan(24, 4)));
        Assert.Equal(60, BinaryPrimitives.ReadInt32LittleEndian(snap.AsSpan(28, 4)));
        Assert.Equal(0x42u, BinaryPrimitives.ReadUInt32LittleEndian(snap.AsSpan(32, 4)));
        Assert.Equal(1, snap[36]); // 1 region
        Assert.Equal(1, snap[65]); // 1 reward tile
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(snap.AsSpan(75, 4))); // count

        byte[] pause = AfflictionRuleset.EncounterPause(4.5f, paused: true)!;
        Assert.Equal(102, pause[2]);
        Assert.Equal(6, pause[3]);
        Assert.Equal(2, pause[4]); // mode 2
        Assert.Equal(4.5f, BinaryPrimitives.ReadSingleLittleEndian(pause.AsSpan(5, 4)));
        Assert.Equal(1, pause[9]);

        // MirrorState kill progression, reward tier cycling, tile unlock, and fog containment.
        var state = new DeliriumMirrorEncounter.MirrorState
        {
            Stage = DeliriumMirrorEncounter.MirrorStage.Active,
            MirrorX = 50,
            MirrorY = 60,
            OuterElapsed = 5f,
            Wave = MirrorFogWave.For(68, new Dictionary<uint, int>()),
            DepthPercent = 40,
            Rewards = [0],
        };
        Assert.True(state.IsCellInFog(70, 60));
        Assert.False(state.IsCellInFog(800, 800));
        Assert.Equal(679f, state.Wave.OuterRadius(7), 3);
        Assert.Equal(1559f, state.Wave.OuterRadius(27), 3);
        Assert.Equal(16.31f, state.Wave.InnerRadius(27), 3);
        state.OuterElapsed = 27;
        Assert.True(state.IsCellInFog(400, 400));
        Assert.False(state.IsCellInFog(55, 60));
        var delayed = MirrorFogWave.For(68, new Dictionary<uint, int>
            { [Stat.MapAfflictionSecondaryWaveDelayMsPlus] = 2000, [Stat.MapAfflictionSecondaryWaveAccelerationPlusPct] = 100 });
        Assert.Equal(14.5f, delayed.InnerRadius(27), 3);
        Assert.Equal(0f, MirrorFogWave.For(68, new Dictionary<uint, int> { [Stat.MapDeliriumFogNeverDissipates] = 1 }).InnerRadius(120));
        for (int i = 0; i < 20; i++) state.RecordKill(MonsterRarity.Rare, progressBonusPct: 0);
        Assert.True(state.RewardCounts[0] >= 3);
        Assert.True(state.Rewards.Length >= 2);
        Assert.True(state.Splinters > 0);
    }

    [Fact]
    public void Delirium_mirror_unlocks_reward_tiles_past_three_every_two_tiers()
    {
        var state = new DeliriumMirrorEncounter.MirrorState
        {
            Stage = DeliriumMirrorEncounter.MirrorStage.Active,
            MirrorX = 50,
            MirrorY = 60,
            OuterElapsed = 5f,
            Wave = MirrorFogWave.For(68, new Dictionary<uint, int>()),
            DepthPercent = 60,
            InitialRewards = 1,
            Rewards = [0],
        };
        for (int i = 0; i < 80; i++) state.RecordKill(MonsterRarity.Unique, progressBonusPct: 0);
        Assert.True(state.RewardCounts[0] >= 9);
        Assert.Equal(1 + (state.RewardCounts[0] - 1) / 2, state.Rewards.Length);
        Assert.Equal(state.Rewards.Length, state.Rewards.Distinct().Count());
        Assert.True(state.Rewards.Length >= 5);

        var boosted = new DeliriumMirrorEncounter.MirrorState
        {
            Stage = DeliriumMirrorEncounter.MirrorStage.Active,
            MirrorX = 50,
            MirrorY = 60,
            OuterElapsed = 5f,
            Wave = MirrorFogWave.For(68, new Dictionary<uint, int>()),
            DepthPercent = 40,
            InitialRewards = 3,
            Rewards = [0, 1, 2],
        };
        for (int i = 0; i < 40; i++) boosted.RecordKill(MonsterRarity.Unique, progressBonusPct: 0);
        Assert.True(boosted.RewardCounts[0] >= 5);
        Assert.Equal(3 + (boosted.RewardCounts[0] - 1) / 2, boosted.Rewards.Length);
        Assert.Equal(boosted.Rewards.Length, boosted.Rewards.Distinct().Count());
    }

    [Fact]
    public async Task Heist_module_registers_planning_packets_and_cleans_up_on_unload()
    {
        string moduleDir = Path.GetFullPath("Modules/Core/Content/Heist");
        Assert.True(File.Exists(Path.Combine(moduleDir, "bin", "heist.dll")), $"Expected built Heist module in {moduleDir}");

        // Load Heist (+ its Game dependency) via ModHost and verify its packet subscriptions register cleanly.
        using (var host = new ModHost("Modules", _ => { }, modules: true) { Wanted = () => new HashSet<string>(["Game", "Heist"]) })
        {
            host.Start(watch: false);
            Assert.True(host.IsLoaded("Heist"));
            Assert.True(GameEvents.WantsPacket(InstanceHeist.SignContractId));
            Assert.True(GameEvents.WantsPacket(InstanceHeist.RevealBlueprintId));
            Assert.True(GameEvents.WantsPacket(InstanceHeist.ConfirmBlueprintId));
            Assert.True(GameEvents.WantsPacket(InstanceHeist.SetBlueprintCrewId));

            using var fixture = new TriggeredSummonTests.Fixture();
            var player = fixture.Player;
            player.Use(player.Layout with { Id = "HeistHub" });
            player.LeagueModule = new(new GameServer.Core.Accounts.League("heist-review", "Standard", false, false), new HashSet<string>(["Game", "Heist"]));
            var items = Game.ItemsOf(player);
            const byte planningRow = 55;
            var blueprint = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Heist/HeistBlueprintMines")!) with
            {
                HeistBlueprint = new(1, 68, 0, false, [new HeistWing(true, [], [], [])])
            };
            var planned = items.Add(planningRow, 0, 0, blueprint);
            var markers = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Heist/HeistCoin")!) with { StackSize = 2000 };
            var coins = items.Add(PlayerInventories.MainInventory, 1, 0, markers);
            Assert.True(await GameEvents.AnyPacket(player, InstanceHeist.ConfirmBlueprintId, [0x02, 0x7e]));
            Assert.Equal(planned, Assert.Single(items.Items(planningRow)));
            Assert.Equal(coins, Assert.Single(items.Items(PlayerInventories.MainInventory), i => i.Id == coins.Id));
        }
        Assert.False(GameEvents.WantsPacket(InstanceHeist.SignContractId));
        Assert.False(GameEvents.WantsPacket(InstanceHeist.RevealBlueprintId));
        Assert.False(GameEvents.WantsPacket(InstanceHeist.ConfirmBlueprintId));
        Assert.False(GameEvents.WantsPacket(InstanceHeist.SetBlueprintCrewId));

        // Verify client.db Heist tables match the protocol and Stat.* assumptions.
        var npcs = ClientTables.Table("HeistNPCs").Take(InstanceHeist.RogueCount).ToArray();
        Assert.Equal(InstanceHeist.RogueCount, npcs.Length);
        Assert.All(npcs, r => Assert.False(string.IsNullOrEmpty(r.Text("AOFile"))));
        Assert.Equal(3, ClientTables.Table("HeistRevealingNPCs").Count);
        var jobs = ClientTables.Table("HeistJobs");
        Assert.Equal(9, jobs.Count);
        for (int j = 0; j < 9; j++)
            Assert.Equal((int)(Stat.HeistJobLockpickingLevelPlus + j) - 1, jobs[j].IntOrNull("Level_StatsKey"));
    }
}
