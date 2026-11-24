using System.Buffers.Binary;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Modules.Delirium;

namespace GameServer.Gateway.Tests;

public sealed class SimulacrumTests
{
    [Fact]
    public void Selected_recipe_uses_only_its_fragment_and_keeps_six_entries()
    {
        var slot = new MapDevice.Slot(InstanceEntities.ObjectTypeHash(SimulacrumRules.Fragment), "MapFragment", "Simulacrum", 3065, [], 0);
        var player = new Player(new AreaLayout(1, "test", 32, 32, 16, 16, (_, _) => true, []), 1, 1);
        var request = new MapOpening(player, [slot], false, null, "unresolved") { Target = 45986 };
        SimulacrumRules.Resolve(request, new Random(42));
        var opening = Assert.IsType<MapDevice.Opening>(request.Opening);
        Assert.Contains(opening.Area.Id, SimulacrumRules.ArenaIds);
        Assert.NotEqual("AnyAfflictionEndgameArea", opening.Area.Id);
        Assert.Equal(5, SimulacrumRules.Arenas().Select(a => a.Hash16).Distinct().Count());
        Assert.True(request.ReturnToSource);
        var mixed = new MapOpening(player, [slot with { TypeHash = 123, Class = "MapFragment" }, slot], false, null, "") { Target = 45986 };
        SimulacrumRules.Resolve(mixed, new Random(42));
        Assert.NotNull(mixed.Opening);
        Assert.Equal(new[] { 1 }, mixed.ConsumeSlots);
        Assert.Same(MapMods.None, mixed.Opening!.Mods);
        var missing = new MapOpening(player, [], false, null, "") { Target = 45986 };
        SimulacrumRules.Resolve(missing, new Random(42));
        Assert.Null(missing.Opening);
        const string owner = "SimulacrumPortalRegression";
        try
        {
            MapDevice.Activate(owner, opening.Area, returnTo: new(1, 123, 10, 10));
            MapDevice.Entered(owner, opening.Area, 456);
            for (int i = 0; i < 6; i++)
            {
                var portal = MapDevice.TakePortal(owner, i);
                Assert.NotNull(portal);
                Assert.Equal(456ul, portal.Value.Map.Instance);
                Assert.Equal(5 - i, portal.Value.Left);
            }
            Assert.NotNull(MapDevice.InInstance(opening.Area.Hash16, 456));
        }
        finally { MapDevice.Close(owner); }
    }

    [Fact]
    public void Simulacrum_portals_do_not_apply_the_owners_atlas_passives()
    {
        const string owner = "SimulacrumAtlasRegression";
        TestEnvironment.OpenOwnSaves("simulacrum-atlas");
        try
        {
            uint harvest = Program.Stats.Key("map_chance_for_area_%_to_contain_harvest")!.Value;
            var harvestNode = Game.AtlasPassiveNodes.Nodes.First(node => node.Stats.Any(stat => stat.Key == harvest && stat.Value > 0));
            CharacterSaves.SetAtlasPassives(owner, [[harvestNode.GraphId]], 1);

            var map = new MapDevice.MapArea(0x1234, "MapWorldsStrand", "Strand Map", 3);
            var mapRun = MapDevice.Run.Of(owner, MapDevice.Activate(owner, map));
            var mapStats = Game.MapAreaStats(new(owner, "Standard", map.Hash16, mapRun))!;
            Assert.True(mapStats.GetValueOrDefault(harvest) > 0);

            var arena = SimulacrumRules.Arenas().Single(area => area.Id == "AfflictionTown6");
            var arenaRun = MapDevice.Run.Of(owner, MapDevice.Activate(owner, arena));
            var arenaStats = Game.MapAreaStats(new(owner, "Standard", arena.Hash16, arenaRun))!;
            Assert.Equal(0, arenaStats.GetValueOrDefault(harvest));
        }
        finally
        {
            MapDevice.Close(owner);
            TestEnvironment.ReleaseOwnSaves();
        }
    }

    [Fact]
    public void Scaled_wave_releases_its_pending_boss_after_spending_the_spawn_budget()
    {
        var run = new DeliriumModule.Run
        {
            Wave = 10, Active = true, ProgressGoal = 70, SpawnPoints = 70, KillPoints = 70,
            Spawned = 36, OmniThisWave = true, KosisThisWave = true, NextSpawnAt = 2
        };
        Assert.Equal(0, run.SpawnAllowance(2, 15, 100));
        Assert.False(run.CanSpawnDueBoss(1, 100));
        Assert.True(run.CanSpawnDueBoss(2, 100));
        Assert.Equal(SimulacrumRules.Kosis, run.DueBoss());
        Assert.False(run.TryComplete());

        run.Monsters.Add(77);
        run.Spawned++;
        run.KosisSpawnedThisWave = true;
        run.KosisCount++;
        var restored = JsonSerializer.Deserialize<DeliriumModule.Run>(JsonSerializer.Serialize(run))!;
        Assert.Null(restored.DueBoss());
        Assert.False(restored.TryComplete());
        Assert.True(restored.RecordKill(77));
        Assert.True(restored.TryComplete());
        Assert.False(restored.TryComplete());
    }

    [Fact]
    public void Wave_waits_for_the_last_spawn_and_enemy_and_cannot_pay_twice_after_reentry()
    {
        var run = new DeliriumModule.Run { Wave = 15, Active = true, Budget = 3, Spawned = 2, Monsters = [42] };
        Assert.False(run.TryComplete());
        run.Spawned = 3;
        Assert.False(run.TryComplete());
        var instance = new InstanceState();
        instance.ContentState["test"] = JsonSerializer.Serialize(run);
        var restored = JsonSerializer.Deserialize<DeliriumModule.Run>(instance.ContentState["test"])!;
        Assert.False(restored.TryComplete());
        restored.Monsters.Remove(42);
        Assert.True(restored.TryComplete());
        Assert.False(restored.TryComplete());
        var again = JsonSerializer.Deserialize<DeliriumModule.Run>(JsonSerializer.Serialize(restored))!;
        Assert.False(again.TryComplete());
        Assert.False(again.Active);
        Assert.True(again.Rewarded);
        Assert.Equal(75, SimulacrumRules.Level(1));
        Assert.Equal(83, SimulacrumRules.Level(15));
        Assert.Equal(84, SimulacrumRules.RewardLevel(15));
        Assert.Equal(100, SimulacrumRules.Delirious(15));
        Assert.Equal(7, SimulacrumRules.RewardTypes(15));

        var idle = DeliriumModule.StarterStates(new() { Wave = 8 });
        var active = DeliriumModule.StarterStates(new() { Wave = 9, Active = true });
        Assert.Equal(new[] { "active", "wave", "hello", "goodbye", "play_interlude" }, idle.Names);
        Assert.Equal(new[] { 0, 8, 0, 0, 0 }, idle.SharedStates); // Native prompt adds one: next wave 9/15.
        Assert.Equal(new[] { 1, 8, 0, 0, 0 }, active.SharedStates);
        Assert.Equal(new[] { 0, 14, 0, 1, 1 }, DeliriumModule.StarterStates(again).SharedStates);
        Assert.False(QuestObject.CanSerialize(DeliriumModule.StarterComponents));
        Assert.True(QuestObject.CanSerialize(DeliriumModule.StarterComponents, haveStateMachine: true));
        var blob = new QuestObject(10, 20, DeliriumModule.StarterComponents) { StateMachine = idle }.Blob();
        Assert.Equal(5, blob[19]); // Five shared states, not the local affliction_endgame_initiator selector.
        Assert.Equal(0, blob[20]); // No shared timers.
        Assert.All(idle.Overridden, i => Assert.InRange(i, 0, 4));
        Assert.Equal(5, SimulacrumRules.ArenaIds.Select(DeliriumModule.StarterType).Distinct().Count());

        var hidden = new StateMachineData(new[] { 0, 0 }, 0, new[] { "visible", "delayed_death" });
        var shown = DeliriumModule.MonsterStates(hidden)!;
        Assert.Equal(new[] { 1, 0 }, shown.SharedStates);
        Assert.Contains(0, shown.Overridden);
        Assert.Equal(0, hidden.SharedStates[0]); // Catalog defaults stay immutable.
        var population = new DeliriumModule.Run { Active = true, Budget = 150, NextSpawnAt = 1 };
        Assert.Equal(0, population.SpawnAllowance(.99, 15, 100));
        Assert.Equal(15, population.SpawnAllowance(1, 15, 100));
        population.Monsters = Enumerable.Range(1, 95).Select(i => (uint)i).ToHashSet();
        Assert.Equal(5, population.SpawnAllowance(1, 15, 100));
        population.Monsters.Add(96); population.Monsters.Add(97); population.Monsters.Add(98);
        population.Monsters.Add(99); population.Monsters.Add(100);
        Assert.Equal(0, population.SpawnAllowance(1, 15, 100));
        population.Monsters.Clear(); population.Spawned = 147;
        Assert.Equal(3, population.SpawnAllowance(1, 15, 100));
        var progress = new DeliriumModule.Run { Active = true, ProgressGoal = 5, SpawnPoints = 5, Monsters = [1, 2], MonsterPoints = new() { [1] = 1, [2] = 4 } };
        Assert.Equal(0, progress.SpawnAllowance(0, 15, 100));
        Assert.False(progress.TryComplete());
        Assert.True(progress.RecordKill(2));
        Assert.False(progress.RecordKill(2));
        Assert.Equal(4, progress.KillPoints);
        Assert.False(progress.TryComplete());
        var resumed = JsonSerializer.Deserialize<DeliriumModule.Run>(JsonSerializer.Serialize(progress))!;
        Assert.True(resumed.RecordKill(1));
        Assert.True(resumed.TryComplete());
        Assert.False(resumed.TryComplete());
        Assert.Equal(7190, Enumerable.Range(1, 15).Sum(SimulacrumRules.Population));

        // The next wave destroys every item on the ground: rewards, a player's own drop and one only its owner sees.
        var ground = new InstanceState().Ground;
        var loot = new Item(1, [], 0);
        ground.Add(new WorldItemObject(10, 10, loot));
        ground.Add(new WorldItemObject(11, 10, loot), new object());
        ground.Add(new WorldItemObject(12, 10, loot), new object(), shared: false);
        Assert.Equal(3, DeliriumModule.ClearGround(ground).Count);
        Assert.Empty(ground.All());
        Assert.Empty(DeliriumModule.ClearGround(ground));
    }

    [Fact]
    public void Per_type_reward_bars_and_affliction_ruleset_tiles_encode_expected_wire_bytes()
    {
        // AfflictionEndGameRules resolves to Rulesets row 103 (0x67).
        Assert.Equal((byte)103, AfflictionRuleset.Row);

        // Pre-PR saved run (empty RewardPoints/RewardCounts) still advances bars on RecordKill via SizeRewards.
        var run = new DeliriumModule.Run
        {
            Wave = 1, Active = true, ProgressGoal = 120, SpawnPoints = 120,
            Rewards = [0, 1], // Currency (pace 6 -> goal 20), Uniques (pace 2 -> goal 60)
            Monsters = [10, 11],
            MonsterPoints = new() { [10] = 25, [11] = 40 },
        };
        Assert.True(run.RecordKill(10));
        Assert.Equal([5, 25], run.RewardPoints);
        Assert.Equal([1, 0], run.RewardCounts);

        Assert.True(run.RecordKill(11));
        Assert.Equal([5, 5], run.RewardPoints);
        Assert.Equal([3, 1], run.RewardCounts);

        // 0x02c9 wire layout: u16 BE 0x02c9, u8 103, compact-uint len (2 + 2*13 = 28), u8 mode 1, u8 count 2, 13-byte LE entries.
        var packet = AfflictionRuleset.RewardTiles([
            new(0, (uint)run.RewardPoints[0], (uint)DeliriumModule.RewardGoal(run, 0), (uint)run.RewardCounts[0]),
            new(1, (uint)run.RewardPoints[1], (uint)DeliriumModule.RewardGoal(run, 1), (uint)run.RewardCounts[1]),
        ]);
        Assert.NotNull(packet);
        Assert.Equal(4 + 28, packet!.Length);
        Assert.Equal(0x02, packet[0]);
        Assert.Equal(0xc9, packet[1]);
        Assert.Equal(103, packet[2]);
        Assert.Equal(28, packet[3]);
        Assert.Equal(1, packet[4]); // mode 1
        Assert.Equal(2, packet[5]); // count 2

        // Entry 0: type 0, cur 5, max 20, count 3
        Assert.Equal(0, packet[6]);
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(7, 4)));
        Assert.Equal(20u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(11, 4)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(15, 4)));

        // Entry 1: type 1, cur 5, max 60, count 1
        Assert.Equal(1, packet[19]);
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(20, 4)));
        Assert.Equal(60u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(24, 4)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(28, 4)));

        // ClearRewardTiles encodes mode 1, count 0 (payload length 2).
        var clear = AfflictionRuleset.ClearRewardTiles();
        Assert.Equal(new byte[] { 0x02, 0xc9, 103, 0x02, 0x01, 0x00 }, clear);
    }

    [Fact]
    public void Area_waypoint_event_and_level_tile_lookup_handle_suppression_and_bounds()
    {
        const string owner = "SimulacrumWaypointTest";
        var league = new LeagueRuntime(new GameServer.Core.Accounts.League("test", "Standard", false, false), new HashSet<string>([owner]));
        var original = new WaypointObject(10, 20);
        var moved = new WaypointObject(30, 40);
        var arena = new Player(new AreaLayout(1, "AfflictionTown6", 32, 32, 16, 16, (_, _) => true, []), 1, 1) { LeagueModule = league };
        var town = new Player(new AreaLayout(2, "1_1_town", 32, 32, 16, 16, (_, _) => true, []), 2, 1) { LeagueModule = league };
        using (InstanceEvents.AreaWaypoint.Add(owner, (p, wp) => SimulacrumRules.IsArena(p.Layout.Id) ? null : moved))
        {
            Assert.Null(InstanceEvents.RunAreaWaypoint(arena, original));
            Assert.Equal(moved, InstanceEvents.RunAreaWaypoint(town, original));
        }
        Assert.Equal(original, InstanceEvents.RunAreaWaypoint(arena, original));

        // TileLookup maps 23x23 cell slots to tile paths without retaining TileGrid or needing game-files.zip.
        var grid = new GameServer.Data.Terrain.Generation.TileGrid(2, 2);
        const string ship = "Metadata/Terrain/Beach/Town_v3_AfflictionLeague/AfflictionShip_v01_01.tdt";
        grid.Place(new(ship, "ship", new() { Width = 1, Height = 1 }), 0, 1, 0, 0);
        var tileAt = ServerLevels.TileLookup(grid);
        Assert.Null(tileAt(-1, 0));
        Assert.Null(tileAt(0, -23));
        Assert.Null(tileAt(46, 0));
        Assert.Null(tileAt(0, 46));
        Assert.Null(tileAt(0, 0));
        Assert.Equal(ship, tileAt(23, 0));
        Assert.Equal(ship, tileAt(45, 22));
        Assert.Null(tileAt(23, 23));
    }
}
