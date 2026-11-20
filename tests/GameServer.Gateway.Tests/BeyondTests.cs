using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain.Generation;
using GameServer.Modding;

namespace GameServer.Gateway.Tests;

public sealed class BeyondTests
{
    static MapDevice.Slot Slot(string leaf)
    {
        var type = Assert.IsType<ItemCatalog.BaseType>(Program.Items.Find("Metadata/Items/Scarabs/" + leaf));
        return new(type.Hash, type.Class, type.Name, type.Row, [], 0) { BaseId = type.Id };
    }

    [GameFilesFact]
    public async Task Beyond_portals_merge_spawn_faction_packs_and_boss_and_shut_down_further_portals()
    {
        // Catalog sanity: 3 factions (Demon, Flesh, Pale), Hellscape life scaling, StateMachine overrides, and portal blobs.
        Assert.Equal(3, BeyondCatalog.All.Count);
        Assert.Equal(["Demon", "Flesh", "Pale"], BeyondCatalog.All.Select(f => f.Id));
        Assert.All(BeyondCatalog.All, f =>
        {
            Assert.True(f.FodderPacks.Count > 0);
            Assert.True(f.ElitePacks.Count > 0);
            Assert.True(f.BossBuffRow > 0);
        });
        Assert.Equal((50, 318, 378), (BeyondCatalog.LifePct(1), BeyondCatalog.LifePct(68), BeyondCatalog.LifePct(83)));
        // The monsters' own portal mods: Breach/Legion/Harvest kills -66% final, CannotSpawnBeyondPortals none at all.
        Assert.Equal(-66, BeyondCatalog.PortalChanceFinal(Game.Monsters.Find("Metadata/Monsters/Hellion/HellionBreach")));
        Assert.Null(BeyondCatalog.PortalChanceFinal(Game.Monsters.Find("Metadata/Monsters/SummonedPhantasm/SummonedPhantasmPenanceMark")));
        var paleBoss = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/LeagueHellscape/PaleFaction/HellscapePaleBoss"));
        var paleStates = Assert.IsType<StateMachineData>(MapBeyond.StartStates(paleBoss));
        Assert.Equal(1, paleStates.SharedStates[paleStates.IndexOf("spawned")]);
        Assert.Equal(0, paleStates.SharedStates[paleStates.IndexOf("souler_coasters")]);
        foreach (string path in new[] { BeyondCatalog.SmallPortalType, BeyondCatalog.PackPortalType, BeyondCatalog.BossPortalType })
        {
            var spec = Assert.IsType<BeyondCatalog.PortalSpec>(BeyondCatalog.Portal(path));
            Assert.NotEmpty(new QuestObject(10, 10, spec.Components, TransitionState: 1) { StateMachine = spec.StateMachine }.Blob());
        }

        // MapOpening with Beyond Scarab + Resurgence + Invasion: handled cleanly, Invasion count rolled 8..12.
        var catalog = MapRunCatalog.Load("data");
        MapDevice.Slot[] slots = [Slot("ScarabBeyond1"), Slot("ScarabBeyond4"), Slot("ScarabBeyond5")];
        var scarabMods = catalog.Of(slots, Program.Items.Mods);
        var area = AreaInfo.Load(GamePack.Default.TryRead, "MapWorldsStrand", 70);
        var opening = new MapOpening(new(new(1, "beyond", 100, 100, 10, 10, null, []), 1, 1), slots, false,
            new(new(1, area.Id, "Strand Map", 3) { Level = 70 }, scarabMods), "");
        MapScarabContent.Describe(opening);
        MapBeyond.Opening(opening);
        Assert.Contains(MapBeyond.Content, opening.HandledContent);
        Assert.Empty(opening.UnhandledContent);
        Assert.InRange(opening.Opening!.Mods.Get("map_rare_monsters_spawn_X_beyond_portals"), 8, 12);
        Assert.InRange(opening.Opening.Mods.Get("map_unique_monsters_spawn_X_beyond_portals"), 8, 12);

        // Active Beyond map with 100% portal chance (+600%) and fast boss trigger (+1200% after 6 packs).
        var mods = new MapMods(new Dictionary<string, int>
        {
            ["map_beyond_rules"] = 1,
            ["map_beyond_portal_chance_+%"] = 600,
            ["map_beyond_boss_spawn_chance_+%"] = 1200,
        });
        var instance = new InstanceState();
        var stats = instance.AreaStats(() => Game.ComposeMapAreaStats(mods, 3, new Dictionary<uint, int>(), new HashSet<string>(), -1, false));
        uint seed = ServerLevels.PlayableSeed(area.Id, 0x7051, 70, stats);
        Assert.IsType<ServerLevels.Level>(ServerLevels.For(area.Id, seed, 70));
        var layout = AreaLayouts.For(area.Hash16, seed);
        instance.OpenedWith(new("BeyondOwner", new(layout.Hash, area.Id, "Strand", 3) { Level = 70 }, mods));

        TestEnvironment.OpenOwnSaves("beyond-core");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
        using var life = MonsterModEffects.HandOverMore([MapBeyond.LifeStat], []);
        await using var owner = await ScarabEncounterTests.Wire.Open(instance, layout, seed);
        await using var peer = await ScarabEncounterTests.Wire.Open(instance, layout, seed);
        instance.World.Runner = owner.Conn;
        try
        {
            MapBeyond.State ReadState() => JsonSerializer.Deserialize<MapBeyond.State>(instance.ContentState["Beyond/state"])!;
            await MapBeyond.Enter(owner.Conn);
            await MapBeyond.Enter(peer.Conn);
            var initial = ReadState();
            Assert.True(initial.Active);
            Assert.InRange(initial.FactionRow, 0, 2);
            var faction = BeyondCatalog.ByRow(initial.FactionRow)!;

            var packs = Game.Packs.For(area.Id)!;
            var natural = packs.Scattered.SelectMany(p => p.Pack.Entries)
                .Select(e => Game.Monsters.Find(e.Variety.ToString())).First(v => v is { Supported: true })!;
            var w = owner.Conn.Wire;
            var spot = layout.Near((layout.SpawnX + 10, layout.SpawnY));

            // 3 kills spawn 3 small orbs (no merge yet); peer tick does not drive spawns.
            async Task KillNaturalAt((int X, int Y) at)
            {
                uint id = await Game.SpawnMonster(w.Remote, w.Stream, w.Ciphers, owner.Conn, natural, 70, MonsterRarity.Normal,
                    at.X, at.Y, unrisen: false, placement: Game.MonsterPlacement.Established);
                var m = owner.Conn.Monsters[id];
                m.Dead = true;
                MapBeyond.Kill(new(owner.Conn, m, natural.Id, at));
            }
            for (int i = 0; i < 3; i++) await KillNaturalAt(spot);
            await MapBeyond.Tick(peer.Conn, 0.25);
            Assert.Empty(ReadState().Portals);
            await MapBeyond.Tick(owner.Conn, 0.25);
            Assert.Equal(3, ReadState().Portals.Count(p => p.Kind == MapBeyond.PortalKind.Small && !p.Consumed));
            Assert.Equal(0, ReadState().PacksSpawned);

            // 4th kill merges the 4 orbs into a Pack portal; after 0.5s it opens and spawns faction Beyond demons.
            await KillNaturalAt(spot);
            await MapBeyond.Tick(owner.Conn, 0.25);
            Assert.Equal(1, ReadState().PacksSpawned);
            Assert.Contains(ReadState().Portals, p => p.Kind == MapBeyond.PortalKind.Pack);
            await MapBeyond.Tick(owner.Conn, 0.5);
            var spawnedDemon = owner.Conn.Monsters.Values.FirstOrDefault(m => !m.Dead && BeyondCatalog.IsBeyondVariety(m.Variety?.Id));
            Assert.NotNull(spawnedDemon);
            Assert.Equal(BeyondCatalog.LifePct(70), spawnedDemon!.Effects[MapBeyond.LifeStat]);

            // Killing a Beyond demon NEVER spawns portals (no self-chaining).
            int orbsBefore = ReadState().Portals.Count(p => p.Kind == MapBeyond.PortalKind.Small && !p.Consumed);
            spawnedDemon.Dead = true;
            MapBeyond.Kill(new(owner.Conn, spawnedDemon, spawnedDemon.Variety!.Id, spot));
            await MapBeyond.Tick(owner.Conn, 0.25);
            Assert.Equal(orbsBefore, ReadState().Portals.Count(p => p.Kind == MapBeyond.PortalKind.Small && !p.Consumed));

            // Spawn 5 more packs (reaching BossMinPacks = 6), then 1 more merge triggers the Boss portal and shuts down further portals.
            for (int pack = 0; pack < 6; pack++)
            {
                for (int k = 0; k < 4; k++) await KillNaturalAt(spot);
                await MapBeyond.Tick(owner.Conn, 0.25);
            }
            var afterBossMerge = ReadState();
            Assert.True(afterBossMerge.BossSpawned);
            Assert.Contains(afterBossMerge.Portals, p => p.Kind == MapBeyond.PortalKind.Boss);
            await MapBeyond.Tick(owner.Conn, 0.6);
            Assert.Contains(owner.Conn.Monsters.Values, m => !m.Dead && m.Rarity == MonsterRarity.Unique && m.Variety?.Id == faction.BossVariety);

            // Once BossSpawned is true, further non-Beyond kills spawn zero new orbs.
            for (int k = 0; k < 4; k++) await KillNaturalAt(spot);
            await MapBeyond.Tick(owner.Conn, 0.25);
            Assert.DoesNotContain(ReadState().Portals, p => p.Kind == MapBeyond.PortalKind.Small && !p.Consumed);

            // Re-entry preserves Beyond state from Instance.ContentState.
            MapBeyond.Leave(owner.Conn);
            await MapBeyond.Enter(owner.Conn);
            Assert.True(ReadState().BossSpawned);
        }
        finally
        {
            PlayerInventories.Forget(owner.Conn.Character!);
            PlayerInventories.Forget(peer.Conn.Character!);
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseOwnSaves();
        }
    }

    [GameFilesFact]
    public async Task Beyond_scarabs_atlas_passives_drops_and_demonic_power_boss_buffs_act_from_composed_stats()
    {
        // Merge radius math: Endless Tide (-50% final) + Haemophilia (+50% increased) = 45 * 1.5 * 0.5 = 33.75.
        Assert.Equal(45.0, MapBeyond.MergeRadius(0, 0, false), 6);
        Assert.Equal(22.5, MapBeyond.MergeRadius(0, 0, true), 6);
        Assert.Equal(33.75, MapBeyond.MergeRadius(50, 0, true), 6);

        int Key(string stat) => (int)Program.Stats.Key(stat)!.Value;
        var atlas = new Dictionary<uint, int>
        {
            [(uint)Key("map_beyond_boss_grants_special_buff_on_death")] = 1,
            [(uint)Key("map_beyond_monster_tainted_currency_quantity_+%")] = 50,
            [(uint)Key("map_beyond_monster_experience_+%")] = 50,
            [(uint)Key("map_beyond_boss_spawn_chance_+%")] = 1200,
        };
        var mods = new MapMods(new Dictionary<string, int>
        {
            ["map_beyond_rules"] = 1,
            ["map_beyond_demon_corrupted_drops"] = 1,
            ["map_beyond_spawn_three_bosses_instead_of_one"] = 1,
            ["map_rare_beyond_monster_gives_mods_to_killer_chance_%"] = 100,
            ["map_rare_monsters_spawn_X_beyond_portals"] = 8,
            ["map_beyond_portal_chance_+%"] = -100,
        });

        var area = AreaInfo.Load(GamePack.Default.TryRead, "MapWorldsStrand", 70);
        var instance = new InstanceState();
        var stats = instance.AreaStats(() => Game.ComposeMapAreaStats(mods, 3, atlas, new HashSet<string>(), -1, false));
        uint seed = ServerLevels.PlayableSeed(area.Id, 0x7052, 70, stats);
        Assert.IsType<ServerLevels.Level>(ServerLevels.For(area.Id, seed, 70));
        var layout = AreaLayouts.For(area.Hash16, seed);
        instance.OpenedWith(new("BeyondAtlas", new(layout.Hash, area.Id, "Strand", 3) { Level = 70 }, mods));

        TestEnvironment.OpenOwnSaves("beyond-atlas");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
        using var life = MonsterModEffects.HandOverMore([MapBeyond.LifeStat], []);
        await using var owner = await ScarabEncounterTests.Wire.Open(instance, layout, seed);
        instance.World.Runner = owner.Conn;
        try
        {
            await MapBeyond.Enter(owner.Conn);
            var spot = layout.Near((layout.SpawnX + 10, layout.SpawnY));
            var w = owner.Conn.Wire;
            var natural = Game.Packs.For(area.Id)!.Scattered.SelectMany(p => p.Pack.Entries)
                .Select(e => Game.Monsters.Find(e.Variety.ToString())).First(v => v is { Supported: true })!;

            // Scarab of the Invasion: 1 rare kill spawns 8 orbs -> immediately merges into 2 pack portals.
            for (int r = 0; r < 4; r++)
            {
                uint rareId = await Game.SpawnMonster(w.Remote, w.Stream, w.Ciphers, owner.Conn, natural, 70, MonsterRarity.Rare,
                    spot.X, spot.Y, unrisen: false, placement: Game.MonsterPlacement.Established);
                var rare = owner.Conn.Monsters[rareId];
                rare.Dead = true;
                MapBeyond.Kill(new(owner.Conn, rare, natural.Id, spot));
                await MapBeyond.Tick(owner.Conn, 0.25);
            }
            // 4 rare kills × 8 orbs = 32 orbs -> 6 packs + boss portal (Resurgence spawns all 3 bosses!).
            var state = JsonSerializer.Deserialize<MapBeyond.State>(instance.ContentState["Beyond/state"])!;
            Assert.True(state.BossSpawned);
            await MapBeyond.Tick(owner.Conn, 0.6);
            foreach (var faction in BeyondCatalog.All)
                Assert.Contains(owner.Conn.Monsters.Values, m => !m.Dead && m.Variety?.Id == faction.BossVariety);

            // Haemophilia: killing a rare Beyond demon steals its mods into conn.StolenMods for 20 s.
            var eliteVariety = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/LeagueHellscape/DemonFaction/HellscapeDemonElite1"));
            uint rareDemonId = await Game.SpawnMonster(w.Remote, w.Stream, w.Ciphers, owner.Conn, eliteVariety, 70, MonsterRarity.Rare,
                spot.X, spot.Y, unrisen: false, placement: Game.MonsterPlacement.Established);
            var rareDemon = owner.Conn.Monsters[rareDemonId];
            Assert.NotEmpty(rareDemon.Mods.Affixes);
            rareDemon.Dead = true;
            owner.Conn.Clock.Start();
            MapBeyond.Kill(new(owner.Conn, rareDemon, eliteVariety.Id, spot));
            Assert.True(owner.Conn.StolenMods.Count > 0);
            Assert.NotNull(owner.Conn.StolenMods.Step(owner.Conn.Clock.Elapsed.TotalSeconds + 21, dead: false));
            Assert.Equal(0, owner.Conn.StolenMods.Count);

            // Drops (Tainted currency + Scarab of Corruption) and Experience (+50% on Beyond kills only).
            var ktash = owner.Conn.Monsters.Values.First(m => m.Variety?.Id == BeyondCatalog.ByRow(0)!.BossVariety);
            var dropRoll = new DropRoll(owner.Conn, Game.ModMonster(ktash, spot));
            MapBeyond.Drops(dropRoll);
            Assert.True(MapBeyond.CorruptsDrops(owner.Conn, ktash.Variety?.Id));
            Assert.False(MapBeyond.CorruptsDrops(owner.Conn, natural.Id));
            Assert.NotEmpty(dropRoll.Stacks);
            Assert.All(dropRoll.Stacks, s => Assert.StartsWith("Metadata/Items/Currency/CurrencyHellscape", s.Item));

            var equipType = Assert.IsType<ItemCatalog.BaseType>(Program.Items.Find("Metadata/Items/Armours/Helmets/HelmetStr1"));
            var currencyType = Assert.IsType<ItemCatalog.BaseType>(Program.Items.Find("Metadata/Items/Currency/CurrencyHellscapeArmourQuality"));
            var mixedDrops = new List<ItemDrops.Drop>
            {
                new(ItemCatalog.Create(equipType), equipType),
                new(ItemCatalog.Create(currencyType), currencyType),
            };
            Game.CorruptDrops(mixedDrops);
            Assert.True(mixedDrops[0].Item.Corrupted);
            Assert.False(mixedDrops[1].Item.Corrupted);

            var beyondXp = new ExperienceGain(owner.Conn, Game.ModMonster(ktash, spot), 1000);
            MapBeyond.Experience(beyondXp);
            Assert.Equal(1500u, beyondXp.Experience);
            var naturalXp = new ExperienceGain(owner.Conn, new MonsterInfo(natural.Id, natural.Name, 70, MonsterRarity.Normal, spot.X, spot.Y), 1000);
            MapBeyond.Experience(naturalXp);
            Assert.Equal(1000u, naturalXp.Experience);

            // Demonic Power (3.29): killing K'Tash, Ghorr and Beidat grants their Magnificence buffs for the rest of
            // the map instance (no fixed 60 s timer, CancelOnDeath = 0, cleared when leaving to hideout / another map,
            // restored on re-entering the same map instance for the same character only).
            foreach (var faction in BeyondCatalog.All)
            {
                var boss = owner.Conn.Monsters.Values.First(m => m.Variety?.Id == faction.BossVariety);
                boss.Dead = true;
                MapBeyond.Kill(new(owner.Conn, boss, faction.BossVariety, spot));
            }
            void AssertAllBossBuffs(ClientState c)
            {
                var buffStats = MapBeyond.PlayerStats(c, []);
                Assert.Equal(100, buffStats[(uint)Key("physical_damage_%_to_add_as_fire")]);
                Assert.Equal(40, buffStats[(uint)Key("explode_enemies_for_10%_life_as_fire_on_kill_chance_%")]);
                Assert.Equal(100, buffStats[(uint)Key("physical_damage_%_to_add_as_lightning")]);
                Assert.Equal(-15, buffStats[(uint)Key("action_speed_-%")]);
                Assert.Equal(90, MapBeyond.IncomingDamage(c, 100));
            }
            AssertAllBossBuffs(owner.Conn);

            // Persists past 60 s and across player death in the same map instance.
            await MapBeyond.Tick(owner.Conn, 0.25);
            await MapBeyond.Tick(owner.Conn, 120.0);
            AssertAllBossBuffs(owner.Conn);
            owner.Conn.PlayerLife = 0;
            await MapBeyond.Tick(owner.Conn, 5.0);
            AssertAllBossBuffs(owner.Conn);

            // Leaving the map clears the leaving connection's active buffs; hideout / another area has none.
            string ownerChar = owner.Conn.Character!;
            MapBeyond.Leave(owner.Conn);
            Assert.Empty(MapBeyond.PlayerStats(owner.Conn, []));
            Assert.Equal(100, MapBeyond.IncomingDamage(owner.Conn, 100));

            await using var inHideout = await ScarabEncounterTests.Wire.Open(new InstanceState(), layout, seed, ownerChar);
            await MapBeyond.Enter(inHideout.Conn);
            Assert.Empty(MapBeyond.PlayerStats(inHideout.Conn, []));
            Assert.Equal(100, MapBeyond.IncomingDamage(inHideout.Conn, 100));

            // Re-entering the same map instance restores all three Magnificence buffs; another character does not get them.
            await using var returned = await ScarabEncounterTests.Wire.Open(instance, layout, seed, ownerChar);
            await MapBeyond.Enter(returned.Conn);
            AssertAllBossBuffs(returned.Conn);

            await using var guest = await ScarabEncounterTests.Wire.Open(instance, layout, seed);
            await MapBeyond.Enter(guest.Conn);
            Assert.Empty(MapBeyond.PlayerStats(guest.Conn, []));
            Assert.Equal(100, MapBeyond.IncomingDamage(guest.Conn, 100));
            PlayerInventories.Forget(guest.Conn.Character!);
        }
        finally
        {
            PlayerInventories.Forget(owner.Conn.Character!);
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseOwnSaves();
        }
    }

    [Fact]
    public async Task Demonic_power_boss_buffs_persist_for_map_instance_across_hideout_round_trips_and_death()
    {
        Assert.Equal(3, BeyondCatalog.All.Count);
        uint Key(string stat) => Program.Stats.Key(stat)!.Value;
        var atlas = new Dictionary<uint, int>
        {
            [Key("map_beyond_boss_grants_special_buff_on_death")] = 1,
        };
        var mods = new MapMods(new Dictionary<string, int> { ["map_beyond_rules"] = 1 });
        var layout = new AreaLayout(1, "MapWorldsStrand", 200, 200, 50, 50, (_, _) => true, []);
        var instance = new InstanceState();
        instance.AreaStats(() => Game.ComposeMapAreaStats(mods, 3, atlas, new HashSet<string>(), -1, false));
        instance.OpenedWith(new("BeyondOwner", new(layout.Hash, layout.Id, "Strand", 3) { Level = 70 }, mods));

        TestEnvironment.OpenOwnSaves("beyond-demonic-power");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var previousOutgoing = Program.Outgoing;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));

        var sentAddBuffs = new List<(ushort Row, byte Flags)>();
        Program.Outgoing = (_, bytes) =>
        {
            if (bytes.Length >= 23 && bytes[0] == (byte)(InstanceEntities.AddBuffId >> 8)
                && bytes[1] == (byte)(InstanceEntities.AddBuffId & 0xff))
            {
                ushort row = (ushort)(bytes[14] << 8 | bytes[15]);
                sentAddBuffs.Add((row, bytes[22]));
            }
            previousOutgoing?.Invoke(_, bytes);
        };

        await using var owner = await ScarabEncounterTests.Wire.Open(instance, layout, 0x7053);
        string ownerChar = owner.Conn.Character!;
        try
        {
            void AssertAllBossBuffs(ClientState c)
            {
                var buffStats = MapBeyond.PlayerStats(c, []);
                Assert.Equal(100, buffStats[Key("physical_damage_%_to_add_as_fire")]);
                Assert.Equal(40, buffStats[Key("explode_enemies_for_10%_life_as_fire_on_kill_chance_%")]);
                Assert.Equal(100, buffStats[Key("physical_damage_%_to_add_as_lightning")]);
                Assert.Equal(-15, buffStats[Key("action_speed_-%")]);
                Assert.Equal(90, MapBeyond.IncomingDamage(c, 100));
            }

            // Kill K'Tash, Ghorr and Beidat -> gain all 3 Magnificence buffs with infinite duration (flags bit1 == 0).
            foreach (var faction in BeyondCatalog.All)
            {
                var boss = new SpawnedMonster(faction.Id, new(50, 50, 10), 40, 80, 100)
                {
                    Rarity = MonsterRarity.Unique,
                    Dead = true,
                };
                MapBeyond.Kill(new(owner.Conn, boss, faction.BossVariety, (50, 50)));
            }
            AssertAllBossBuffs(owner.Conn);
            await MapBeyond.Tick(owner.Conn, 0.25);

            var expectedRows = BeyondCatalog.All.Select(f => f.BossBuffRow).Order().ToArray();
            Assert.Equal(expectedRows, sentAddBuffs.Select(b => b.Row).Order().ToArray());
            Assert.All(sentAddBuffs, b => Assert.Equal(0, b.Flags & 0x02));

            // No fixed 60 s expiry and no cancellation on player death (CancelOnDeath = 0 in 3.29 BuffDefinitions).
            await MapBeyond.Tick(owner.Conn, 120.0);
            AssertAllBossBuffs(owner.Conn);
            owner.Conn.PlayerLife = 0;
            await MapBeyond.Tick(owner.Conn, 5.0);
            AssertAllBossBuffs(owner.Conn);

            // Leaving to hideout clears active buffs on the leaving connection; hideout / other maps do not grant them.
            MapBeyond.Leave(owner.Conn);
            Assert.Empty(MapBeyond.PlayerStats(owner.Conn, []));
            Assert.Equal(100, MapBeyond.IncomingDamage(owner.Conn, 100));

            sentAddBuffs.Clear();
            await using var inHideout = await ScarabEncounterTests.Wire.Open(new InstanceState(), layout, 0x7053, ownerChar);
            await MapBeyond.Enter(inHideout.Conn);
            Assert.Empty(MapBeyond.PlayerStats(inHideout.Conn, []));
            Assert.Equal(100, MapBeyond.IncomingDamage(inHideout.Conn, 100));
            Assert.Empty(sentAddBuffs);

            // Re-entering the same map instance restores all 3 Magnificence buffs with infinite duration.
            await using var returned = await ScarabEncounterTests.Wire.Open(instance, layout, 0x7053, ownerChar);
            await MapBeyond.Enter(returned.Conn);
            AssertAllBossBuffs(returned.Conn);
            Assert.Equal(expectedRows, sentAddBuffs.Select(b => b.Row).Order().ToArray());
            Assert.All(sentAddBuffs, b => Assert.Equal(0, b.Flags & 0x02));

            // Restoring from ContentState alone (after a hot-reload) also brings all 3 buffs back.
            sentAddBuffs.Clear();
            var reloadedInstance = new InstanceState();
            reloadedInstance.ContentState["Beyond/boss-buffs"] = instance.ContentState["Beyond/boss-buffs"];
            await using var afterReload = await ScarabEncounterTests.Wire.Open(reloadedInstance, layout, 0x7053, ownerChar);
            await MapBeyond.Enter(afterReload.Conn);
            AssertAllBossBuffs(afterReload.Conn);
            Assert.Equal(expectedRows, sentAddBuffs.Select(b => b.Row).Order().ToArray());

            // Another character entering the same map instance does not receive the killer's Demonic Power buffs.
            sentAddBuffs.Clear();
            await using var guest = await ScarabEncounterTests.Wire.Open(instance, layout, 0x7053);
            await MapBeyond.Enter(guest.Conn);
            Assert.Empty(MapBeyond.PlayerStats(guest.Conn, []));
            Assert.Equal(100, MapBeyond.IncomingDamage(guest.Conn, 100));
            Assert.Empty(sentAddBuffs);
            PlayerInventories.Forget(guest.Conn.Character!);
        }
        finally
        {
            PlayerInventories.Forget(ownerChar);
            Program.Outgoing = previousOutgoing;
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseOwnSaves();
        }
    }
}
