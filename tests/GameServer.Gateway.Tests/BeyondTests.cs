using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain.Generation;
using GameServer.Modding;
using GameServer.Modules.Survivors;

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
        Assert.InRange(opening.Opening!.Mods.Get(Stat.MapRareMonstersSpawnXBeyondPortals), 8, 12);
        Assert.InRange(opening.Opening.Mods.Get(Stat.MapUniqueMonstersSpawnXBeyondPortals), 8, 12);

        // Active Beyond map with 100% portal chance (+600%) and fast boss trigger (+1200% after 6 packs).
        var mods = new MapMods(new Dictionary<uint, int>
        {
            [Stat.MapBeyondRules] = 1,
            [Stat.MapBeyondPortalChancePlusPct] = 600,
            [Stat.MapBeyondBossSpawnChancePlusPct] = 1200,
        });
        var instance = new InstanceState();
        var stats = instance.AreaStats(() => Game.ComposeMapAreaStats(mods, 3, new Dictionary<uint, int>(), new HashSet<string>(), -1));
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
        instance.World.Runner = owner.Player;
        try
        {
            MapBeyond.State ReadState() => JsonSerializer.Deserialize<MapBeyond.State>(instance.ContentState["Beyond/state"])!;
            await MapBeyond.Enter(owner.Player);
            await MapBeyond.Enter(peer.Player);
            var initial = ReadState();
            Assert.True(initial.Active);
            Assert.InRange(initial.FactionRow, 0, 2);
            var faction = BeyondCatalog.ByRow(initial.FactionRow)!;

            var packs = Game.Packs.For(area.Id)!;
            var natural = packs.Scattered.SelectMany(p => p.Pack.Entries)
                .Select(e => Game.Monsters.Find(e.Variety.ToString())).First(v => v is { Supported: true })!;
            var spot = layout.Near((layout.SpawnX + 10, layout.SpawnY));

            // 3 kills spawn 3 small orbs (no merge yet); peer tick does not drive spawns.
            async Task KillNaturalAt((int X, int Y) at)
            {
                uint id = await Game.SpawnMonster(owner.Player, natural, 70, MonsterRarity.Normal,
                    at.X, at.Y, unrisen: false, placement: Game.MonsterPlacement.Established);
                var m = owner.Player.Area.Monsters[id];
                m.Dead = true;
                MapBeyond.Kill(new(owner.Player, m, natural.Id, at));
            }
            for (int i = 0; i < 3; i++) await KillNaturalAt(spot);
            await MapBeyond.Tick(peer.Player, 0.25);
            Assert.Empty(ReadState().Portals);
            await MapBeyond.Tick(owner.Player, 0.25);
            Assert.Equal(3, ReadState().Portals.Count(p => p.Kind == MapBeyond.PortalKind.Small && !p.Consumed));
            Assert.Equal(0, ReadState().PacksSpawned);

            // 4th kill merges the 4 orbs into a Pack portal; after 0.5s it opens and spawns faction Beyond demons.
            await KillNaturalAt(spot);
            await MapBeyond.Tick(owner.Player, 0.25);
            Assert.Equal(1, ReadState().PacksSpawned);
            Assert.Contains(ReadState().Portals, p => p.Kind == MapBeyond.PortalKind.Pack);
            await MapBeyond.Tick(owner.Player, 0.5);
            var spawnedDemon = owner.Player.Area.Monsters.Values.FirstOrDefault(m => !m.Dead && BeyondCatalog.IsBeyondVariety(m.Variety?.Id));
            Assert.NotNull(spawnedDemon);
            Assert.Equal(BeyondCatalog.LifePct(70), spawnedDemon!.Effects[MapBeyond.LifeStat]);

            // Killing a Beyond demon NEVER spawns portals (no self-chaining).
            int orbsBefore = ReadState().Portals.Count(p => p.Kind == MapBeyond.PortalKind.Small && !p.Consumed);
            spawnedDemon.Dead = true;
            MapBeyond.Kill(new(owner.Player, spawnedDemon, spawnedDemon.Variety!.Id, spot));
            await MapBeyond.Tick(owner.Player, 0.25);
            Assert.Equal(orbsBefore, ReadState().Portals.Count(p => p.Kind == MapBeyond.PortalKind.Small && !p.Consumed));

            // Spawn 5 more packs (reaching BossMinPacks = 6), then 1 more merge triggers the Boss portal and shuts down further portals.
            for (int pack = 0; pack < 6; pack++)
            {
                for (int k = 0; k < 4; k++) await KillNaturalAt(spot);
                await MapBeyond.Tick(owner.Player, 0.25);
            }
            var afterBossMerge = ReadState();
            Assert.True(afterBossMerge.BossSpawned);
            Assert.Contains(afterBossMerge.Portals, p => p.Kind == MapBeyond.PortalKind.Boss);
            await MapBeyond.Tick(owner.Player, 0.6);
            Assert.Contains(owner.Player.Area.Monsters.Values, m => !m.Dead && m.Rarity == MonsterRarity.Unique && m.Variety?.Id == faction.BossVariety);

            // Once BossSpawned is true, further non-Beyond kills spawn zero new orbs.
            for (int k = 0; k < 4; k++) await KillNaturalAt(spot);
            await MapBeyond.Tick(owner.Player, 0.25);
            Assert.DoesNotContain(ReadState().Portals, p => p.Kind == MapBeyond.PortalKind.Small && !p.Consumed);

            // Re-entry preserves Beyond state from Instance.ContentState.
            MapBeyond.Leave(owner.Player);
            await MapBeyond.Enter(owner.Player);
            Assert.True(ReadState().BossSpawned);
        }
        finally
        {
            PlayerInventories.Forget(owner.Player.Character.Name!);
            PlayerInventories.Forget(peer.Player.Character.Name!);
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
        // A module's own final percent (GameEvents.BeyondMergeRadiusFinal) reaches a player's radius: 45 * 0.6.
        using (var radius = new TriggeredSummonTests.Fixture())
        {
            using (GameEvents.BeyondMergeRadiusFinal.Add("Game", (_, _) => -40))
                Assert.Equal(27.0, MapBeyond.MergeRadius(radius.Player), 6);
            Assert.Equal(45.0, MapBeyond.MergeRadius(radius.Player), 6);
        }

        var atlas = new Dictionary<uint, int>
        {
            [Stat.MapBeyondBossGrantsSpecialBuffOnDeath] = 1,
            [Stat.MapBeyondMonsterTaintedCurrencyQuantityPlusPct] = 50,
            [Stat.MapBeyondMonsterExperiencePlusPct] = 50,
            [Stat.MapBeyondBossSpawnChancePlusPct] = 1200,
        };
        var mods = new MapMods(new Dictionary<uint, int>
        {
            [Stat.MapBeyondRules] = 1,
            [Stat.MapBeyondDemonCorruptedDrops] = 1,
            [Stat.MapBeyondSpawnThreeBossesInsteadOfOne] = 1,
            [Stat.MapRareBeyondMonsterGivesModsToKillerChancePct] = 100,
            [Stat.MapRareMonstersSpawnXBeyondPortals] = 8,
            [Stat.MapBeyondPortalChancePlusPct] = -100,
        });

        var area = AreaInfo.Load(GamePack.Default.TryRead, "MapWorldsStrand", 70);
        var instance = new InstanceState();
        var stats = instance.AreaStats(() => Game.ComposeMapAreaStats(mods, 3, atlas, new HashSet<string>(), -1));
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
        instance.World.Runner = owner.Player;
        try
        {
            await MapBeyond.Enter(owner.Player);
            var spot = layout.Near((layout.SpawnX + 10, layout.SpawnY));
            var natural = Game.Packs.For(area.Id)!.Scattered.SelectMany(p => p.Pack.Entries)
                .Select(e => Game.Monsters.Find(e.Variety.ToString())).First(v => v is { Supported: true })!;

            // Scarab of the Invasion: 1 rare kill spawns 8 orbs -> immediately merges into 2 pack portals.
            for (int r = 0; r < 4; r++)
            {
                uint rareId = await Game.SpawnMonster(owner.Player, natural, 70, MonsterRarity.Rare,
                    spot.X, spot.Y, unrisen: false, placement: Game.MonsterPlacement.Established);
                var rare = owner.Player.Area.Monsters[rareId];
                rare.Dead = true;
                MapBeyond.Kill(new(owner.Player, rare, natural.Id, spot));
                await MapBeyond.Tick(owner.Player, 0.25);
            }
            // 4 rare kills × 8 orbs = 32 orbs -> 6 packs + boss portal (Resurgence spawns all 3 bosses!).
            var state = JsonSerializer.Deserialize<MapBeyond.State>(instance.ContentState["Beyond/state"])!;
            Assert.True(state.BossSpawned);
            await MapBeyond.Tick(owner.Player, 0.6);
            foreach (var faction in BeyondCatalog.All)
                Assert.Contains(owner.Player.Area.Monsters.Values, m => !m.Dead && m.Variety?.Id == faction.BossVariety);

            // Haemophilia: killing a rare Beyond demon steals its mods into player.StolenMods for 20 s.
            var eliteVariety = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/LeagueHellscape/DemonFaction/HellscapeDemonElite1"));
            uint rareDemonId = await Game.SpawnMonster(owner.Player, eliteVariety, 70, MonsterRarity.Rare,
                spot.X, spot.Y, unrisen: false, placement: Game.MonsterPlacement.Established);
            var rareDemon = owner.Player.Area.Monsters[rareDemonId];
            Assert.NotEmpty(rareDemon.Mods.Affixes);
            rareDemon.Dead = true;
            owner.Player.Clock.Start();
            MapBeyond.Kill(new(owner.Player, rareDemon, eliteVariety.Id, spot));
            Assert.True(owner.Player.StolenMods.Count > 0);
            Assert.NotNull(owner.Player.StolenMods.Step(Game.AreaTime(owner.Player) + 21, dead: false));
            Assert.Equal(0, owner.Player.StolenMods.Count);

            // Drops (Tainted currency + Scarab of Corruption) and Experience (+50% on Beyond kills only).
            var ktash = owner.Player.Area.Monsters.Values.First(m => m.Variety?.Id == BeyondCatalog.ByRow(0)!.BossVariety);
            var dropRoll = new DropRoll(owner.Player, Game.ModMonster(ktash, spot));
            MapBeyond.Drops(dropRoll);
            Assert.True(MapBeyond.CorruptsDrops(owner.Player, ktash.Variety?.Id));
            Assert.False(MapBeyond.CorruptsDrops(owner.Player, natural.Id));
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

            var beyondXp = new ExperienceGain(owner.Player, Game.ModMonster(ktash, spot), 1000);
            MapBeyond.Experience(beyondXp);
            Assert.Equal(1500u, beyondXp.Experience);
            var naturalXp = new ExperienceGain(owner.Player, new MonsterInfo(natural.Id, natural.Name, 70, MonsterRarity.Normal, spot.X, spot.Y), 1000);
            MapBeyond.Experience(naturalXp);
            Assert.Equal(1000u, naturalXp.Experience);

            // Demonic Power (3.29): killing K'Tash, Ghorr and Beidat grants their Magnificence buffs for the rest of
            // the map instance (no fixed 60 s timer, CancelOnDeath = 0, cleared when leaving to hideout / another map,
            // restored on re-entering the same map instance for the same character only).
            foreach (var faction in BeyondCatalog.All)
            {
                var boss = owner.Player.Area.Monsters.Values.First(m => m.Variety?.Id == faction.BossVariety);
                boss.Dead = true;
                MapBeyond.Kill(new(owner.Player, boss, faction.BossVariety, spot));
            }
            void AssertAllBossBuffs(Player c)
            {
                var buffStats = MapBeyond.PlayerStats(c, []);
                Assert.Equal(100, buffStats[Stat.PhysicalDamagePctToAddAsFire]);
                Assert.Equal(40, buffStats[Stat.ExplodeEnemiesFor10PctLifeAsFireOnKillChancePct]);
                Assert.Equal(100, buffStats[Stat.PhysicalDamagePctToAddAsLightning]);
                Assert.Equal(-15, buffStats[Stat.ActionSpeedMinusPct]);
                Assert.Equal(-10, buffStats[MapBeyond.BossLessDamageStat]);
            }
            AssertAllBossBuffs(owner.Player);

            // Persists past 60 s and across player death in the same map instance.
            await MapBeyond.Tick(owner.Player, 0.25);
            await MapBeyond.Tick(owner.Player, 120.0);
            AssertAllBossBuffs(owner.Player);
            owner.Player.PlayerLife = 0;
            await MapBeyond.Tick(owner.Player, 5.0);
            AssertAllBossBuffs(owner.Player);

            // Leaving the map clears the leaving connection's active buffs; hideout / another area has none.
            string ownerChar = owner.Player.Character.Name!;
            MapBeyond.Leave(owner.Player);
            Assert.Empty(MapBeyond.PlayerStats(owner.Player, []));

            await using var inHideout = await ScarabEncounterTests.Wire.Open(new InstanceState(), layout, seed, ownerChar);
            await MapBeyond.Enter(inHideout.Player);
            Assert.Empty(MapBeyond.PlayerStats(inHideout.Player, []));

            // Re-entering the same map instance restores all three Magnificence buffs; another character does not get them.
            await using var returned = await ScarabEncounterTests.Wire.Open(instance, layout, seed, ownerChar);
            await MapBeyond.Enter(returned.Player);
            AssertAllBossBuffs(returned.Player);

            await using var guest = await ScarabEncounterTests.Wire.Open(instance, layout, seed);
            await MapBeyond.Enter(guest.Player);
            Assert.Empty(MapBeyond.PlayerStats(guest.Player, []));
            PlayerInventories.Forget(guest.Player.Character.Name!);
        }
        finally
        {
            PlayerInventories.Forget(owner.Player.Character.Name!);
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
        var atlas = new Dictionary<uint, int>
        {
            [Stat.MapBeyondBossGrantsSpecialBuffOnDeath] = 1,
        };
        var mods = new MapMods(new Dictionary<uint, int> { [Stat.MapBeyondRules] = 1 });
        var layout = new AreaLayout(1, "MapWorldsStrand", 200, 200, 50, 50, (_, _) => true, []);
        var instance = new InstanceState();
        instance.AreaStats(() => Game.ComposeMapAreaStats(mods, 3, atlas, new HashSet<string>(), -1));
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
        string ownerChar = owner.Player.Character.Name!;
        try
        {
            void AssertAllBossBuffs(Player c)
            {
                var buffStats = MapBeyond.PlayerStats(c, []);
                Assert.Equal(100, buffStats[Stat.PhysicalDamagePctToAddAsFire]);
                Assert.Equal(40, buffStats[Stat.ExplodeEnemiesFor10PctLifeAsFireOnKillChancePct]);
                Assert.Equal(100, buffStats[Stat.PhysicalDamagePctToAddAsLightning]);
                Assert.Equal(-15, buffStats[Stat.ActionSpeedMinusPct]);
                Assert.Equal(-10, buffStats[MapBeyond.BossLessDamageStat]);
            }

            // Kill K'Tash, Ghorr and Beidat -> gain all 3 Magnificence buffs with infinite duration (flags bit1 == 0).
            foreach (var faction in BeyondCatalog.All)
            {
                var boss = new SpawnedMonster(faction.Id, new(50, 50, 10), 40, 80, 100)
                {
                    Rarity = MonsterRarity.Unique,
                    Dead = true,
                };
                MapBeyond.Kill(new(owner.Player, boss, faction.BossVariety, (50, 50)));
            }
            AssertAllBossBuffs(owner.Player);
            await MapBeyond.Tick(owner.Player, 0.25);

            var expectedRows = BeyondCatalog.All.Select(f => f.BossBuffRow).Order().ToArray();
            Assert.Equal(expectedRows, sentAddBuffs.Select(b => b.Row).Order().ToArray());
            Assert.All(sentAddBuffs, b => Assert.Equal(0, b.Flags & 0x02));

            // No fixed 60 s expiry and no cancellation on player death (CancelOnDeath = 0 in 3.29 BuffDefinitions).
            await MapBeyond.Tick(owner.Player, 120.0);
            AssertAllBossBuffs(owner.Player);
            owner.Player.PlayerLife = 0;
            await MapBeyond.Tick(owner.Player, 5.0);
            AssertAllBossBuffs(owner.Player);

            // Leaving to hideout clears active buffs on the leaving connection; hideout / other maps do not grant them.
            MapBeyond.Leave(owner.Player);
            Assert.Empty(MapBeyond.PlayerStats(owner.Player, []));

            sentAddBuffs.Clear();
            await using var inHideout = await ScarabEncounterTests.Wire.Open(new InstanceState(), layout, 0x7053, ownerChar);
            await MapBeyond.Enter(inHideout.Player);
            Assert.Empty(MapBeyond.PlayerStats(inHideout.Player, []));
            Assert.Empty(sentAddBuffs);

            // Re-entering the same map instance restores all 3 Magnificence buffs with infinite duration.
            await using var returned = await ScarabEncounterTests.Wire.Open(instance, layout, 0x7053, ownerChar);
            await MapBeyond.Enter(returned.Player);
            AssertAllBossBuffs(returned.Player);
            Assert.Equal(expectedRows, sentAddBuffs.Select(b => b.Row).Order().ToArray());
            Assert.All(sentAddBuffs, b => Assert.Equal(0, b.Flags & 0x02));

            // Restoring from ContentState alone (after a hot-reload) also brings all 3 buffs back.
            sentAddBuffs.Clear();
            var reloadedInstance = new InstanceState();
            reloadedInstance.ContentState["Beyond/boss-buffs"] = instance.ContentState["Beyond/boss-buffs"];
            await using var afterReload = await ScarabEncounterTests.Wire.Open(reloadedInstance, layout, 0x7053, ownerChar);
            await MapBeyond.Enter(afterReload.Player);
            AssertAllBossBuffs(afterReload.Player);
            Assert.Equal(expectedRows, sentAddBuffs.Select(b => b.Row).Order().ToArray());

            // Another character entering the same map instance does not receive the killer's Demonic Power buffs.
            sentAddBuffs.Clear();
            await using var guest = await ScarabEncounterTests.Wire.Open(instance, layout, 0x7053);
            await MapBeyond.Enter(guest.Player);
            Assert.Empty(MapBeyond.PlayerStats(guest.Player, []));
            Assert.Empty(sentAddBuffs);
            PlayerInventories.Forget(guest.Player.Character.Name!);
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

    [Fact]
    public void Ghorr_magnificence_reduces_all_incoming_damage_paths_without_double_applying()
    {
        TestEnvironment.OpenOwnSaves("ghorr-incoming-damage");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
        using var statsHook = GameEvents.PlayerStatSources.Add("Game", MapBeyond.PlayerStats);
        using var finalTaken = IncomingResourceDefences.HandOverFinalTaken([MapBeyond.BossLessDamageStat]);

        var instance = new InstanceState();
        instance.AreaStats(() => new Dictionary<uint, int>
        {
            [Stat.MapBeyondBossGrantsSpecialBuffOnDeath] = 1,
        });
        var player = new Player(new(1, "ghorr-test", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = { Name = "GhorrTester" },
            PlayerLife = 1000,
            PlayerMana = 100,
            Instance = instance,
            Pools = new(1, 1000, 100, 0, 0),
            EvasionEntropy = 100,
            IncomingHitRandom = new Random(7),
        };
        var hitter = new SpawnedMonster("hitter", new(0, 0, 0), 0, 0, 1000) { Level = 1 };
        var melee = new MonsterAttack(0, 1, 10, DamagePacket.Of(DamageType.Physical, 100, 100));
        var ranged = new MonsterAttack(0, 1, 50, DamagePacket.Of(DamageType.Fire, 100, 100)) { Ranged = true };
        var spell = new MonsterAttack(0, 1, 50, DamagePacket.Of(DamageType.Fire, 100, 100)) { Spell = true };

        uint TakeCommit(MonsterAttack attack)
        {
            player.PlayerLife = 1000;
            player.EvasionEntropy = 100;
            var hit = Game.CommitMonsterHitLocked(player, 1, hitter, attack, Game.AreaTime(player), (0, 0), (0, 0), checkRange: false);
            Assert.NotNull(hit);
            return 1000u - hit!.Life;
        }

        uint TakeSyntheticSpell()
        {
            player.PlayerLife = 1000;
            Assert.True(Game.TrySyntheticEnemyHit(player, DamageType.Fire, 100, block: false, spell: true, suppress: false, out var hit));
            return 1000u - hit.Life;
        }

        uint TakeIgnite(double from)
        {
            player.PlayerLife = 1000;
            player.ElementalAilments.Clear();
            player.NonChaosDotTakenCarry = player.NonChaosDotBypassCarry = 0;
            player.ElementalAilments.Inflict(PlayerElementalAilments.Kind.Ignite, 100, 4, from, _ => 0, new Random(1));
            Game.AdvancePlayerAilmentsLocked(player, from, from + 1.0, Game.CharacterPlayerStats(player));
            return 1000u - player.PlayerLife;
        }

        double CurrentFinalTakenFactor()
        {
            var stats = Game.CharacterPlayerStats(player);
            return IncomingResourceDefences.FinalTakenFactor(player.Pools, k => stats.GetValueOrDefault(k));
        }

        try
        {
            // Baseline (Ghorr inactive): every path deals full 100 damage and factor is 1.0.
            Assert.Equal(100u, TakeCommit(melee));
            Assert.Equal(100u, TakeCommit(ranged));
            Assert.Equal(100u, TakeCommit(spell));
            Assert.Equal(100u, TakeSyntheticSpell());
            Assert.Equal(100, Game.DamagePlayerHitTyped(player, 100, 0, 0, 0, Game.CharacterPlayerStats(player),
                DamagePacket.Of(DamageType.Physical, 100, 100)));
            Assert.Equal(100u, TakeIgnite(10));
            Assert.Equal(1.0, CurrentFinalTakenFactor(), 6);

            // Grant Ghorr's Magnificence by killing the Flesh faction boss.
            var ghorrFaction = Assert.IsType<BeyondCatalog.Faction>(BeyondCatalog.ByRow(1));
            var ghorr = new SpawnedMonster("Ghorr", new(0, 0, 0), 0, 0, 1000) { Dead = true, Rarity = MonsterRarity.Unique };
            MapBeyond.Kill(new(player, ghorr, ghorrFaction.BossVariety, (0, 0)));

            // GameEvents.IncomingDamage is NOT used by Ghorr (no double-application).
            Assert.Equal(100, GameEvents.AdjustIncomingDamage(player, 100));

            // Every incoming damage path takes 10% less (90, never 81): melee, ranged/projectile, monster spell,
            // synthetic spell (shrine/strongbox), direct typed hit (labyrinth trap), ignite DoT, and RF/ground factor.
            Assert.Equal(90u, TakeCommit(melee));
            Assert.Equal(90u, TakeCommit(ranged));
            Assert.Equal(90u, TakeCommit(spell));
            Assert.Equal(90u, TakeSyntheticSpell());
            Assert.Equal(90, Game.DamagePlayerHitTyped(player, 100, 0, 0, 0, Game.CharacterPlayerStats(player),
                DamagePacket.Of(DamageType.Physical, 100, 100)));
            Assert.Equal(90u, TakeIgnite(30));
            Assert.Equal(0.9, CurrentFinalTakenFactor(), 6);

            // SurvivorsRun compatibility: GameEvents.IncomingDamage (60%) composes cleanly with Ghorr (0.9) -> 54.
            using (GameEvents.IncomingDamage.Add("Game", (_, dmg) => SurvivorsRules.IncomingDamage(dmg, SurvivorsRules.IncomingDamagePercent)))
            {
                Assert.Equal(60, GameEvents.AdjustIncomingDamage(player, 100));
                Assert.Equal(54u, TakeCommit(melee));
            }

            // Clearing Ghorr restores full damage.
            MapBeyond.Leave(player);
            Assert.Equal(100u, TakeCommit(melee));
            Assert.Equal(1.0, CurrentFinalTakenFactor(), 6);
        }
        finally
        {
            MapBeyond.Leave(player);
            PlayerInventories.Forget(player.Character.Name!);
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseOwnSaves();
        }
    }
}
