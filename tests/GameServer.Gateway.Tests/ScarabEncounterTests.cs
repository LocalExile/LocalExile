using System.Net;
using System.Net.Sockets;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain.Generation;
using GameServer.Modding;
using GameServer.Modules.Shrines;

namespace GameServer.Gateway.Tests;

public sealed class ScarabEncounterTests
{
    static MapDevice.Slot Slot(string leaf)
    {
        var type = Assert.IsType<ItemCatalog.BaseType>(Program.Items.Find("Metadata/Items/Scarabs/" + leaf));
        return new(type.Hash, type.Class, type.Name, type.Row, [], 0) { BaseId = type.Id };
    }

    [GameFilesFact]
    public async Task Scarab_encounters_use_generated_shrines_and_additional_packs_with_shared_lifecycles()
    {
        var slot = Slot("ScarabDomination1");
        var catalog = MapRunCatalog.Load("data");
        var mods = catalog.Of([slot], Program.Items.Mods);
        Assert.Equal(3, mods.Get("map_leaguestone_additional_shrines"));
        Dictionary<uint, int> Compose(MapMods input, Dictionary<uint, int>? atlas = null) =>
            Game.ComposeMapAreaStats(input, 3, atlas ?? [], new HashSet<string>(), -1, false);
        var stats = Compose(mods);
        var area = AreaInfo.Load(GamePack.Default.TryRead, "MapWorldsStrand", 70);
        int Base(IReadOnlyDictionary<uint, int> input) => ExtraTerrainFeatures.LeagueStruct(area with
            { Stats = input.ToDictionary(s => (int)s.Key, s => s.Value) }, 1).Base;
        Assert.Equal(0, Base(Compose(MapMods.None)));
        Assert.Equal(3, Base(stats));
        Assert.Equal(6, Base(Compose(catalog.Of([slot, slot], Program.Items.Mods))));
        Assert.Equal(5, Base(Compose(mods, new()
        {
            [Program.Stats.Key("map_num_extra_shrines")!.Value] = 2,
            [Program.Stats.Key("map_mod_effect_+%_for_non_unique_maps")!.Value] = 100,
        })));
        Assert.Equal(3, mods.Get("map_leaguestone_additional_shrines"));
        Assert.Equal(0, mods.Get("map_num_extra_shrines"));

        var cloister = Slot("ScarabDivinationCardsNew1");
        var cloisterMods = catalog.Of([cloister], Program.Items.Mods);
        Assert.Equal(8, cloisterMods.Get(MapScarabPacks.CountStat));
        Assert.Equal(5, MapScarabPacks.Packs.Count);
        var rolledCounts = new HashSet<int>();
        MapOpening Open(int seed, params MapDevice.Slot[] slots)
        {
            var request = new MapOpening(new(new(1, "scarab", 100, 100, 10, 10, null, []), 1, 1), slots, false,
                new(new(1, "MapWorldsStrand", "Strand Map", 3), catalog.Of(slots, Program.Items.Mods)), "");
            MapScarabContent.Describe(request);
            MapScarabPacks.Opening(request, new Random(seed));
            Assert.Empty(request.UnhandledContent);
            return request;
        }
        for (int i = 0; i < 64; i++)
        {
            var single = Open(i, cloister).Opening!.Mods;
            Assert.Equal(1, single.ScarabCount);
            int count = single.Get(MapScarabPacks.CountStat);
            Assert.InRange(count, 8, 12);
            rolledCounts.Add(count);
            Assert.Equal(count, Compose(single)[Program.Stats.Key(MapScarabPacks.CountStat)!.Value]);
            Assert.InRange(Open(i, cloister, cloister).Opening!.Mods.Get(MapScarabPacks.CountStat), 16, 24);
        }
        Assert.Equal(Enumerable.Range(8, 5), rolledCounts.Order());
        Assert.Empty(MapScarabPacks.Requests(MapMods.None));
        string member = Game.Monsters.Find(MapScarabPacks.Packs[0].Entries[0].Variety.ToString())!.Id;
        Assert.True(MapScarabPacks.DropsDeck(cloisterMods, member, new FixedRoll(0)));
        Assert.False(MapScarabPacks.DropsDeck(cloisterMods, member, new FixedRoll(1)));
        Assert.False(MapScarabPacks.DropsDeck(cloisterMods, "ordinary monster", new FixedRoll(0)));
        Assert.False(MapScarabPacks.DropsDeck(MapMods.None, member, new FixedRoll(0)));
        var original = new MapMods(new Dictionary<string, int> { [MapScarabPacks.CountStat] = 10 }, 20)
        {
            Implicit = new Dictionary<string, int> { [MapScarabPacks.CountStat] = 2 },
            Fragments = new Dictionary<string, int> { [MapScarabPacks.CountStat] = 8 },
            ExplicitMods = [new Dictionary<string, int> { ["map_monsters_life_+%"] = 20 }],
            ScarabCount = 1, FragmentCount = 1, MapRarity = GameServer.Core.Protocol.Instance.ItemRarity.Rare, Corrupted = true,
        };
        var replaced = original.WithFragmentStat(MapScarabPacks.CountStat, 12);
        Assert.Equal(14, replaced.Get(MapScarabPacks.CountStat));
        Assert.Same(original.ExplicitMods, replaced.ExplicitMods);
        Assert.Same(original.Implicit, replaced.Implicit);
        Assert.Equal((20, 1, 1, original.MapRarity, true),
            (replaced.Quality, replaced.ScarabCount, replaced.FragmentCount, replaced.MapRarity, replaced.Corrupted));

        var instance = new InstanceState();
        var kept = instance.AreaStats(() => stats);
        uint seed = ServerLevels.PlayableSeed(area.Id, 0x505, 70, kept);
        var generated = Assert.IsType<ServerLevels.Level>(ServerLevels.For(area.Id, seed, 70));
        var layout = AreaLayouts.For(area.Hash16, seed);
        var features = ExtraTerrainFeatures.Roll(area with { Stats = kept.ToDictionary(s => (int)s.Key, s => s.Value) },
            new LevelRandom(seed, false));
        int requested = Assert.Single(features, f => f.Name == "(shrines)").Count;
        var plan = ShrinePlacement.Plan(generated.RoomObjects, layout.Walkable!, seed);
        Assert.True(requested >= 3);
        Assert.Equal(requested, plan.Count);
        var packNotes = new List<string>();
        var cloisterRequest = Assert.Single(MapScarabPacks.Requests(Open(5, cloister).Opening!.Mods));
        var ordinary = Game.Packs.For(area.Id)!;
        var extra = AreaPopulation.Plan(ordinary, generated.Width, generated.Height, layout.Walkable!,
            (layout.SpawnX, layout.SpawnY), [], [], 0x505, _ => true, packNotes, additionalPacks: [cloisterRequest]);
        var packIds = MapScarabPacks.Packs.Select(p => p.Id).ToHashSet();
        var added = extra.Where(s => packIds.Contains(s.Pack)).ToArray();
        Assert.InRange(added.Length, cloisterRequest.Count * 4, cloisterRequest.Count * 9);
        Assert.Contains(packNotes, n => n.Contains($"placed {cloisterRequest.Count}/{cloisterRequest.Count}"));
        Assert.All(added, s => Assert.True(layout.Walkable!(s.X, s.Y)));
        Assert.Equal(extra, AreaPopulation.Plan(ordinary, generated.Width, generated.Height, layout.Walkable!,
            (layout.SpawnX, layout.SpawnY), [], [], 0x505, _ => true, [], additionalPacks: [cloisterRequest]));
        var noOrdinary = AreaPopulation.Plan(ordinary, generated.Width, generated.Height, layout.Walkable!,
            (layout.SpawnX, layout.SpawnY), [], [], 0x505, _ => true, [], additionalPacks: [cloisterRequest], ordinaryPacks: false);
        Assert.Contains(noOrdinary, s => packIds.Contains(s.Pack));
        Assert.DoesNotContain(noOrdinary, s => s.Rarity != GameServer.Core.Protocol.Instance.MonsterRarity.Unique
            && ordinary.Scattered.Any(p => p.Pack.Id == s.Pack) && !packIds.Contains(s.Pack));
        Assert.Same(kept, instance.AreaStats(() => throw new InvalidOperationException("recomposed the map")));

        TestEnvironment.OpenOwnSaves("scarab-shrines");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
        ShrineRegistry.RegisterDefaults();
        await using var owner = await Wire.Open(instance, layout, seed);
        await using var peer = await Wire.Open(instance, layout, seed);
        using var sources = GameEvents.PlayerStatSources.Add("scarab-regression", ShrineManager.AddStats);
        try
        {
            await ShrineManager.SpawnArea(owner.Conn);
            string[] Ids() => instance.ContentState.Where(p => p.Key.StartsWith("Shrines/id/"))
                .Select(p => p.Value).Order().ToArray();
            var ids = Ids();
            Assert.Equal(requested, ids.Length);
            await ShrineManager.SpawnArea(peer.Conn);
            Assert.Equal(ids, Ids());
            var first = plan[0];
            uint id = uint.Parse(instance.ContentState[$"Shrines/id/{first.X}/{first.Y}"]);
            owner.Conn.Walk.Teleport(first.X, first.Y, 0);
            Assert.True(await ShrineManager.Use(owner.Conn, new(id, 0, 1, 0), 0));
            string claimed = $"Shrines/used/{first.X}/{first.Y}";
            Assert.True(instance.HasState(claimed));
            await ShrineManager.LeaveArea(owner.Conn);
            await ShrineManager.LeaveArea(peer.Conn);
            await ShrineManager.SpawnArea(owner.Conn);
            Assert.Equal(ids, Ids());
            Assert.True(instance.HasState(claimed));
            Assert.Empty(ShrineManager.AddStats(owner.Conn, []));
            var ambushSlot = Slot("ScarabStrongboxNew1");
            var key = Program.Items.Find("Metadata/Items/Maps/MapKeyReliquary")!;
            var vesselMod = Program.Items.Mods.ById("MapSupporterVaalEmpoweredChests")!.Roll(ModSection.Explicit, new Random(5));
            var mapSlot = new MapDevice.Slot(key.Hash, key.Class, key.Name, key.Row, [vesselMod], 0)
                { BaseId = key.Id, Rarity = ItemRarity.Rare };
            var opening = Assert.IsType<MapDevice.Opening>(Game.MapDevices.Value.Check([mapSlot, ambushSlot], null, Program.Items.Mods, out _));
            Assert.Equal((17, 84), (opening.Area.Tier, opening.Area.Level));
            var ambushMods = opening.Mods;
            Assert.Equal(4, ambushMods.Get(MapStrongboxes.CountStat));
            var boxInstance = new InstanceState();
            boxInstance.OpenedWith(new("StrongboxOwner", opening.Area, ambushMods));
            var boxStats = boxInstance.AreaStats(() => Game.ComposeMapAreaStats(ambushMods, opening.Area.Tier, new Dictionary<uint, int>(), new HashSet<string>(), -1, false));
            uint boxSeed = ServerLevels.PlayableSeed(opening.Area.Id, 0x56a104, opening.Area.Level, boxStats);
            var boxLayout = AreaLayouts.For(opening.Area.Hash16, boxSeed);
            await using var boxOwner = await Wire.Open(boxInstance, boxLayout, boxSeed);
            await using var boxPeer = await Wire.Open(boxInstance, boxLayout, boxSeed);
            await MapStrongboxes.Enter(boxOwner.Conn);
            MapStrongboxes.Box[] Boxes() => System.Text.Json.JsonSerializer.Deserialize<MapStrongboxes.Box[]>(boxInstance.ContentState["Ambush/boxes"])!;
            var boxes = Boxes();
            var vessels = boxes.Where(b => b.Type == MapStrongboxes.ExquisiteVessel).ToArray();
            Assert.Equal(10, vessels.Length);
            Assert.All(vessels, b => { Assert.True(b.Corrupted); Assert.Equal(2, b.Rarity); Assert.NotEmpty(b.Mods); });
            int ordinaryBoxes = boxes.Length - vessels.Length;
            Assert.Equal(MapStrongboxes.Count(boxOwner.Conn, opening.Area.Level).Count, ordinaryBoxes);
            Assert.True(ordinaryBoxes >= 4);
            await MapStrongboxes.Enter(boxPeer.Conn);
            Assert.Equal(boxes.Select(b => b.Id), Boxes().Select(b => b.Id));
            Assert.All(boxes, b => Assert.True(boxLayout.Walkable!(b.X, b.Y)));
            var firestorm = Program.Items.Mods.ById("ChestSupporterVaalFirestorm")!;
            var firstBox = vessels.First(b => b.Mods.Any(m => m.Hash == firestorm.Hash));
            boxOwner.Conn.Walk.Teleport(firstBox.X, firstBox.Y, 0);
            Assert.True(await MapStrongboxes.Use(boxOwner.Conn, new(firstBox.Id, 0, 1, 0), 0));
            var active = Boxes()[firstBox.Index];
            Assert.Equal(1, active.Phase);
            Assert.NotEmpty(active.Guards);
            Assert.Empty(active.Pending);
            var storm = Assert.Single(active.Firestorms);
            Assert.Equal(24, storm.Ground.Distinct().Count());
            Assert.Equal("Metadata/Monsters/Daemon/ChestDaemonVaalFirestorm", boxOwner.Conn.Monsters[storm.Actor].Variety!.Id);
            Assert.DoesNotContain(storm.Actor, active.Guards);
            var firstImpact = TimeSpan.FromTicks(storm.Started) + TimeSpan.FromMilliseconds(750);
            await MapStrongboxes.Tick(boxPeer.Conn, firstImpact - TimeSpan.FromTicks(1));
            Assert.Equal(0, Boxes()[firstBox.Index].Firestorms[0].Landed);
            await MapStrongboxes.Tick(boxOwner.Conn, firstImpact);
            await MapStrongboxes.Tick(boxPeer.Conn, firstImpact);
            Assert.Equal(1, Boxes()[firstBox.Index].Firestorms[0].Landed);
            int guardCount = boxOwner.Conn.Monsters.Count;
            Assert.True(await MapStrongboxes.Use(boxPeer.Conn, new(firstBox.Id, 0, 1, 0), 0));
            Assert.Equal(guardCount, boxOwner.Conn.Monsters.Count);
            var sideBosses = ClientTables.Table("WorldAreas").Where(a => a.Bool("IsVaalArea"))
                .SelectMany(a => a.Ints("Bosses_MonsterVarietiesKeys")).ToHashSet();
            uint vesselBoss = Assert.Single(active.Guards, id => boxOwner.Conn.Monsters[id] is
                { Rarity: MonsterRarity.Unique, Variety: { } v } && sideBosses.Contains(v.Row));
            Assert.Contains(active.Guards, id => boxOwner.Conn.Monsters[id].Rarity == MonsterRarity.Normal);
            lock (boxOwner.Conn.Monsters)
                foreach (uint guard in active.Guards.Where(id => id != vesselBoss)) { boxOwner.Conn.Monsters[guard].Dead = true; boxOwner.Conn.Monsters[guard].Life = 0; }
            await MapStrongboxes.Tick(boxOwner.Conn);
            Assert.Equal(1, Boxes()[firstBox.Index].Phase);
            Assert.Empty(boxInstance.Ground.All());
            boxOwner.Conn.Monsters[vesselBoss].Dead = true;
            boxOwner.Conn.Monsters[vesselBoss].Life = 0;
            await MapStrongboxes.Tick(boxOwner.Conn);
            Assert.Equal(2, Boxes()[firstBox.Index].Phase);
            int rewardCount = boxInstance.Ground.All().Count();
            Assert.True(rewardCount > 0);
            await MapStrongboxes.Tick(boxPeer.Conn);
            Assert.Equal(rewardCount, boxInstance.Ground.All().Count());
            MapStrongboxes.Leave(boxOwner.Conn);
            await MapStrongboxes.Enter(boxOwner.Conn);
            Assert.Equal(firstBox.Id, Boxes()[firstBox.Index].Id);
            Assert.Equal(2, Boxes()[firstBox.Index].Phase);
            Assert.Equal(rewardCount, boxInstance.Ground.All().Count());
            var lastImpact = firstImpact + TimeSpan.FromMilliseconds(23 * 500);
            await MapStrongboxes.Tick(boxPeer.Conn, lastImpact);
            Assert.Equal(24, Boxes()[firstBox.Index].Firestorms[0].Landed);
            Assert.DoesNotContain(storm.Actor, boxInstance.World.MonsterOrigins.Keys);
            await MapStrongboxes.Tick(boxPeer.Conn, lastImpact + TimeSpan.FromSeconds(6));
            Assert.Empty(Boxes()[firstBox.Index].Firestorms);
            Assert.True(MapStrongboxes.ClosesAgain(15, new FixedRoll(14)));
            Assert.False(MapStrongboxes.ClosesAgain(15, new FixedRoll(15)));
            var essenceMods = catalog.Of([Slot("ScarabEssence1"), Slot("ScarabEssence2"), Slot("ScarabEssence4")], Program.Items.Mods);
            var essenceInstance = new InstanceState();
            essenceInstance.OpenedWith(new("EssenceOwner", new(layout.Hash, area.Id, "Strand", 3) { Level = 70 }, essenceMods));
            essenceInstance.AreaStats(() => Compose(essenceMods));
            await using var essenceOwner = await Wire.Open(essenceInstance, layout, seed);
            await using var essencePeer = await Wire.Open(essenceInstance, layout, seed);
            Assert.True(MapEssences.Count(essenceOwner.Conn) >= 3);
            await MapEssences.Enter(essenceOwner.Conn);
            MapEssences.Monolith[] Monoliths() => System.Text.Json.JsonSerializer.Deserialize<MapEssences.Monolith[]>(
                essenceInstance.ContentState["Essence/monoliths"])!;
            var sealedMonoliths = Monoliths();
            Assert.Equal(MapEssences.Count(essenceOwner.Conn), sealedMonoliths.Length);
            await MapEssences.Enter(essencePeer.Conn);
            Assert.Equal(sealedMonoliths.Select(m => m.Id), Monoliths().Select(m => m.Id));
            var firstMonolith = sealedMonoliths[0];
            var naturalRare = extra.First(s => s.Rarity == GameServer.Core.Protocol.Instance.MonsterRarity.Rare
                && ordinary.Scattered.Any(p => p.Pack.Id == s.Pack));
            Assert.True(await MapEssences.Imprison(essenceOwner.Conn, naturalRare));
            Assert.Equal(sealedMonoliths.Length + 1, Monoliths().Length);
            var converted = Monoliths()[^1];
            Assert.Equal(Game.Monsters.Find(naturalRare.Variety.ToString())!.Id, converted.Variety);
            Assert.Empty(essenceOwner.Conn.Monsters);
            Assert.True(await MapEssences.Imprison(essencePeer.Conn, naturalRare));
            Assert.Equal(sealedMonoliths.Length + 1, Monoliths().Length);
            Assert.False(await MapEssences.Imprison(essenceOwner.Conn, naturalRare with { Rarity = GameServer.Core.Protocol.Instance.MonsterRarity.Unique }));
            Assert.False(await MapEssences.Imprison(essenceOwner.Conn, naturalRare with { Pack = MapScarabPacks.Packs[0].Id }));
            essenceOwner.Conn.Walk.Teleport(firstMonolith.X + 8, firstMonolith.Y, 0);
            for (byte stage = 2; stage <= 4; stage++)
            {
                Assert.True(await MapEssences.Use(essenceOwner.Conn, new(firstMonolith.Id, 0, stage, 0), 0));
                Assert.Equal(stage, Monoliths()[0].Stage);
                Assert.Equal(stage == 4 ? 1 : 0, essenceOwner.Conn.Monsters.Count);
            }
            var released = Monoliths()[0];
            Assert.True(released.Monster > 0);
            var monster = essenceOwner.Conn.Monsters[released.Monster];
            Assert.Equal(released.Monster, monster.ObjectId);
            Assert.Equal(GameServer.Core.Protocol.Instance.MonsterRarity.Rare, monster.Rarity);
            Assert.Contains(monster.Mods.Mods, m => firstMonolith.Essences.Select(MapEssenceCatalog.Find)
                .OfType<MapEssenceCatalog.Essence>().SelectMany(e => e.MonsterMods)
                .Any(row => Program.Items.Mods.Row(row)!.Hash == m.Hash));
            var wrongMonster = new DropRoll(essenceOwner.Conn, Game.ModMonster(monster, (released.X, released.Y)) with { ObjectId = released.Monster + 1 });
            MapEssences.Drops(wrongMonster);
            Assert.Empty(wrongMonster.Items);
            var rewards = new DropRoll(essenceOwner.Conn, Game.ModMonster(monster, (released.X, released.Y)));
            MapEssences.Drops(rewards);
            Assert.Equal(released.Essences.Select(e => MapEssenceCatalog.Find(e)!.Item), rewards.Items);
            var duplicate = new DropRoll(essencePeer.Conn, rewards.Monster);
            MapEssences.Drops(duplicate);
            Assert.Empty(duplicate.Items);
            await MapEssences.Use(essencePeer.Conn, new(firstMonolith.Id, 0, 5, 0), 0);
            Assert.Single(essenceOwner.Conn.Monsters);
            MapEssences.Leave(essenceOwner.Conn);
            await MapEssences.Enter(essenceOwner.Conn);
            Assert.Equal((released.Id, released.Stage, released.Monster), (Monoliths()[0].Id, Monoliths()[0].Stage, Monoliths()[0].Monster));
            Assert.Equal(released.Essences, Monoliths()[0].Essences);
            var terrorStats = new Dictionary<string, int>(catalog.Of([slot, Slot("ScarabDomination4")], Program.Items.Mods).Stats)
            {
                ["map_boss_maximum_life_+%"] = 40, ["map_boss_damage_+%"] = 30,
                ["map_boss_attack_and_cast_speed_+%"] = 20,
            };
            var terrorMods = new MapMods(terrorStats);
            var terrorInstance = new InstanceState();
            terrorInstance.OpenedWith(new("TerrorsOwner", new(layout.Hash, area.Id, "Strand", 3) { Level = 70 }, terrorMods));
            terrorInstance.AreaStats(() => Compose(terrorMods));
            await using var terrors = await Wire.Open(terrorInstance, layout, seed);
            var terrorGuards = MapScarabBosses.Guards(terrors.Conn, [.. plan.Select(p => (p.X, p.Y))], []);
            Assert.Equal(plan.Count, terrorGuards.Count);
            Assert.All(terrorGuards, s =>
            {
                Assert.Equal(GameServer.Core.Protocol.Instance.MonsterRarity.Unique, s.Rarity);
                Assert.Equal(MapScarabBosses.PackId, s.Pack);
                Assert.Contains("Standalone", Game.Monsters.Find(s.Variety.ToString())!.Id);
            });
            Assert.Equal(terrorGuards, MapScarabBosses.Guards(terrors.Conn, [.. plan.Select(p => (p.X, p.Y))], []));
            var previousSettings = SystemSettings.Default.Values;
            try
            {
                foreach (double size in new[] { 0.0, 0.5, 2.0 })
                {
                    SystemSettings.Default.Replace(new Dictionary<string, double>(previousSettings) { ["monster_pack_size_multiplier"] = size });
                    Assert.Equal(terrorGuards, MapScarabBosses.Guards(terrors.Conn, [.. plan.Select(p => (p.X, p.Y))], []));
                }
            }
            finally { SystemSettings.Default.Replace(previousSettings); }
            var guardPlan = terrorGuards[0];
            var guardVariety = Game.Monsters.Find(guardPlan.Variety.ToString())!;
            uint bossId = await Game.SpawnMonster(terrors.Conn.Wire.Remote, terrors.Conn.Wire.Stream, terrors.Conn.Wire.Ciphers,
                terrors.Conn, guardVariety, 70, guardPlan.Rarity, guardPlan.X, guardPlan.Y, unrisen: false);
            var boss = terrors.Conn.Monsters[bossId];
            Assert.Equal(40, boss.Mods.Effects["maximum_life_+%"]);
            Assert.Equal(30, boss.Mods.Effects["damage_+%"]);
            Assert.Equal(20, boss.Mods.Effects["attack_speed_+%"]);
            Assert.Equal(20, boss.Mods.Effects["base_cast_speed_+%"]);
            PlayerInventories.Forget(terrors.Conn.Character!);
            PlayerInventories.Forget(essenceOwner.Conn.Character!);
            PlayerInventories.Forget(essencePeer.Conn.Character!);
            PlayerInventories.Forget(boxOwner.Conn.Character!);
            PlayerInventories.Forget(boxPeer.Conn.Character!);
        }
        finally
        {
            await ShrineManager.LeaveArea(owner.Conn);
            await ShrineManager.LeaveArea(peer.Conn);
            PlayerInventories.Forget(owner.Conn.Character!);
            PlayerInventories.Forget(peer.Conn.Character!);
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseOwnSaves();
        }
    }

    [Fact]
    public void Named_content_diagnostics_keep_item_provenance_and_allow_enabled_providers_in_either_order()
    {
        var conn = new ClientState(new(1, "scarab-diagnostics", 100, 100, 10, 10, null, []), 1, 1)
            { Character = "ScarabDiagnostics" };
        var opening = new MapDevice.Opening(new(1, "MapWorldsStrand", "Strand Map", 3), MapMods.None);
        MapOpening Request(params string[] names) => new(conn, names.Select(Slot).ToArray(), false, opening, "");
        var unsupported = Request("ScarabBreachNew3", "ScarabLegionNew1");
        MapScarabContent.Describe(unsupported);
        Assert.Equal(2, unsupported.UnhandledContent.Count());
        Assert.Contains(unsupported.UnhandledContent, s => s.Contains("ScarabBreachNew3")
            && s.Contains("map_contains_additional_unstable_breaches=2") && s.Contains("area=MapWorldsStrand"));
        Assert.Contains(unsupported.UnhandledContent, s => s.Contains("ScarabLegionNew1") && s.Contains("content=Legion"));
        Assert.Same(opening, unsupported.Opening);
        unsupported.HandledContent.Add("Breach");
        Assert.Single(unsupported.UnhandledContent);
        var basic = Request("ScarabDomination1");
        ShrineMapAdmission.Check(basic);
        MapScarabContent.Describe(basic);
        Assert.Empty(basic.UnhandledContent);
        var withoutOwner = Request("ScarabDomination1");
        MapScarabContent.Describe(withoutOwner);
        Assert.Single(withoutOwner.UnhandledContent);
        var evolving = Request("ScarabDomination3");
        MapScarabContent.Describe(evolving);
        ShrineMapAdmission.Check(evolving);
        Assert.Contains("Shrines.Evolving", Assert.Single(evolving.UnhandledContent));
        var numeric = Request("ScarabMapsNew1", "ScarabDivinationCardsNew2");
        MapScarabContent.Describe(numeric);
        Assert.Empty(numeric.UnhandledContent);
        var rejected = Request("ScarabDomination2");
        rejected.Opening = opening with { Mods = MapRunCatalog.Load("data").Of(rejected.Slots, Program.Items.Mods) };
        ShrineMapAdmission.Check(rejected);
        Assert.Null(rejected.Opening);
        Assert.Contains("not been consumed", rejected.Reason);
        var arcanist = Assert.IsType<StrongboxCatalog.Definition>(StrongboxCatalog.Find("Metadata/Chests/StrongBoxes/Arcanist"));
        Assert.Equal(81, StrongboxCatalog.Weight(arcanist, 70, false, MapMods.None));
        Assert.Equal(0, StrongboxCatalog.Weight(arcanist, 25, false, MapMods.None));
        var rareBoxes = new MapMods(new Dictionary<string, int> { ["map_strongbox_rare_boxes_weighting_multiplier"] = 3 });
        Assert.Equal(243, StrongboxCatalog.Weight(arcanist, 70, false, rareBoxes));
        var cartographer = StrongboxCatalog.Find("Metadata/Chests/StrongBoxes/CartographerLowMaps")!;
        Assert.True(StrongboxCatalog.Weight(cartographer, 70, false, MapMods.None) > 0);
        Assert.Equal(0, StrongboxCatalog.Weight(cartographer, 72, false, MapMods.None));
        Assert.NotEqual(StrongboxCatalog.Blob(arcanist, 100, 100, false, false),
            StrongboxCatalog.Blob(arcanist, 100, 100, true, false));
        Assert.NotEqual(StrongboxCatalog.Blob(arcanist, 100, 100, true, false),
            StrongboxCatalog.Blob(arcanist, 100, 100, true, true));
        Assert.DoesNotContain(MapEssenceCatalog.Pool(70), e => e.Tier > 5 || e.Corrupted);
        Assert.Contains(MapEssenceCatalog.Pool(73), e => e.Tier == 6);
        var screaming = MapEssenceCatalog.All.First(e => e.Tier == 5);
        Assert.Equal(6, MapEssenceCatalog.Higher(screaming).Tier);
        var essenceRequest = Request("ScarabEssence1", "ScarabEssence2", "ScarabEssence3");
        essenceRequest.Opening = opening with { Area = opening.Area with { Level = 70 }, Mods = MapRunCatalog.Load("data").Of(essenceRequest.Slots, Program.Items.Mods) };
        MapScarabContent.Describe(essenceRequest);
        MapEssences.Opening(essenceRequest);
        Assert.DoesNotContain(essenceRequest.UnhandledContent, s => s.Contains("Essence.Stability"));
        Assert.Contains(essenceRequest.UnhandledContent, s => s.Contains("replica balance"));
        var ambush = Request("ScarabStrongboxNew1");
        ambush.Opening = opening with { Area = opening.Area with { Level = 70 }, Mods = MapRunCatalog.Load("data").Of(ambush.Slots, Program.Items.Mods) };
        MapScarabContent.Describe(ambush);
        MapStrongboxes.Opening(ambush);
        Assert.Empty(ambush.UnhandledContent);
        var terrors = Request("ScarabDomination4");
        MapScarabContent.Describe(terrors);
        MapScarabBosses.Opening(terrors);
        Assert.Empty(terrors.UnhandledContent);
    }

    internal sealed class Wire(TcpClient writer, TcpClient reader, ClientState conn, Task drain) : IAsyncDisposable
    {
        public ClientState Conn => conn;
        public static async Task<Wire> Open(InstanceState instance, AreaLayout layout, uint seed)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            var writer = new TcpClient();
            await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var reader = await accepting;
            var conn = new ClientState(layout, 1, seed)
            {
                Character = "ScarabMap" + Guid.NewGuid().ToString("N"), Instance = instance,
                PlayerLife = 1000, PlayerMana = 1000, PlayerSent = true, Pools = new(70, 1000, 1000, 1000, 0),
                LeagueModule = new(new GameServer.Core.Accounts.League("scarab-test", "Standard", false, false),
                    new HashSet<string>(["Game", "scarab-regression"])),
                Wire = ("scarab-regression", writer.GetStream(), new ConnectionCiphers(new byte[64], false)),
            };
            conn.Clock.Stop();
            return new(writer, reader, conn, reader.GetStream().CopyToAsync(Stream.Null));
        }
        public async ValueTask DisposeAsync()
        {
            Conn.Closed = true;
            writer.Dispose();
            await drain;
            reader.Dispose();
        }
    }

    sealed class FixedRoll(int roll) : Random
    {
        public override int Next(int maxValue) => roll;
    }
}
