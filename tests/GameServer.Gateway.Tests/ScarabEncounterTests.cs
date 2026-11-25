using System.Net;
using System.Net.Sockets;
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
        Assert.Equal(3, mods.Get(Stat.MapLeaguestoneAdditionalShrines));
        Dictionary<uint, int> Compose(MapMods input, Dictionary<uint, int>? atlas = null) =>
            Game.ComposeMapAreaStats(input, 3, atlas ?? [], new HashSet<string>(), -1);
        var stats = Compose(mods);
        var area = AreaInfo.Load(GamePack.Default.TryRead, "MapWorldsStrand", 70);
        int Base(IReadOnlyDictionary<uint, int> input) => ExtraTerrainFeatures.LeagueStruct(area with
            { Stats = input.ToDictionary(s => (int)s.Key, s => s.Value) }, 1).Base;
        Assert.Equal(0, Base(Compose(MapMods.None)));
        Assert.Equal(3, Base(stats));
        Assert.Equal(6, Base(Compose(catalog.Of([slot, slot], Program.Items.Mods))));
        Assert.Equal(5, Base(Compose(mods, new()
        {
            [Stat.MapNumExtraShrines] = 2,
            [Stat.MapModEffectPlusPctForNonUniqueMaps] = 100,
        })));
        Assert.Equal(3, mods.Get(Stat.MapLeaguestoneAdditionalShrines));
        Assert.Equal(0, mods.Get(Stat.MapNumExtraShrines));

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
            Assert.Equal(count, Compose(single)[MapScarabPacks.CountStat]);
            Assert.InRange(Open(i, cloister, cloister).Opening!.Mods.Get(MapScarabPacks.CountStat), 16, 24);
        }
        Assert.Equal(Enumerable.Range(8, 5), rolledCounts.Order());
        Assert.Empty(MapScarabPacks.Requests(MapMods.None));
        string member = Game.Monsters.Find(MapScarabPacks.Packs[0].Entries[0].Variety.ToString())!.Id;
        Assert.True(MapScarabPacks.DropsDeck(cloisterMods, member, new FixedRoll(0)));
        Assert.False(MapScarabPacks.DropsDeck(cloisterMods, member, new FixedRoll(1)));
        Assert.False(MapScarabPacks.DropsDeck(cloisterMods, "ordinary monster", new FixedRoll(0)));
        Assert.False(MapScarabPacks.DropsDeck(MapMods.None, member, new FixedRoll(0)));
        var original = new MapMods(new Dictionary<uint, int> { [MapScarabPacks.CountStat] = 10 }, 20)
        {
            Implicit = new Dictionary<uint, int> { [MapScarabPacks.CountStat] = 2 },
            Fragments = new Dictionary<uint, int> { [MapScarabPacks.CountStat] = 8 },
            ExplicitMods = [new Dictionary<uint, int> { [Stat.MapMonstersLifePlusPct] = 20 }],
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
        var ordinary = AreaPopulation.ForInstance(Game.Packs.For(area.Id)!, 0x505, _ => true);
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
            await ShrineManager.SpawnArea(owner.Player);
            string[] Ids() => instance.ContentState.Where(p => p.Key.StartsWith("Shrines/id/"))
                .Select(p => p.Value).Order().ToArray();
            var ids = Ids();
            Assert.Equal(requested, ids.Length);
            await ShrineManager.SpawnArea(peer.Player);
            Assert.Equal(ids, Ids());
            var first = plan[0];
            uint id = uint.Parse(instance.ContentState[$"Shrines/id/{first.X}/{first.Y}"]);
            owner.Player.Walk.Teleport(first.X, first.Y, 0);
            Assert.True(await ShrineManager.Use(owner.Player, new(id, 0, 1, 0), 0));
            string claimed = $"Shrines/used/{first.X}/{first.Y}";
            Assert.True(instance.HasState(claimed));
            await ShrineManager.LeaveArea(owner.Player);
            await ShrineManager.LeaveArea(peer.Player);
            await ShrineManager.SpawnArea(owner.Player);
            Assert.Equal(ids, Ids());
            Assert.True(instance.HasState(claimed));
            Assert.Empty(ShrineManager.AddStats(owner.Player, []));
            var ambushSlot = Slot("ScarabStrongboxNew1");
            var ambushMods = catalog.Of([ambushSlot], Program.Items.Mods);
            Assert.Equal(4, ambushMods.Get(MapStrongboxes.CountStat));
            var boxInstance = new InstanceState();
            boxInstance.OpenedWith(new("StrongboxOwner", new(layout.Hash, area.Id, "Strand", 3) { Level = 70 }, ambushMods));
            boxInstance.AreaStats(() => Compose(ambushMods));
            await using var boxOwner = await Wire.Open(boxInstance, layout, seed);
            await using var boxPeer = await Wire.Open(boxInstance, layout, seed);
            await MapStrongboxes.Enter(boxOwner.Player);
            MapStrongboxes.Box[] Boxes() => System.Text.Json.JsonSerializer.Deserialize<MapStrongboxes.Box[]>(boxInstance.ContentState["Ambush/boxes"])!;
            var boxes = Boxes();
            Assert.Equal(MapStrongboxes.Count(boxOwner.Player, 70).Count, boxes.Length);
            Assert.True(boxes.Length >= 4); // the four scarab boxes plus any natural Ambush roll
            await MapStrongboxes.Enter(boxPeer.Player);
            Assert.Equal(boxes.Select(b => b.Id), Boxes().Select(b => b.Id));
            Assert.All(boxes, b => Assert.True(layout.Walkable!(b.X, b.Y)));
            var firstBox = boxes[0];
            boxOwner.Player.Walk.Teleport(firstBox.X, firstBox.Y, 0);
            Assert.True(await MapStrongboxes.Use(boxOwner.Player, new(firstBox.Id, 0, 1, 0), 0));
            var active = Boxes()[0];
            Assert.Equal(1, active.Phase);
            // The guards wait for the box's opening clip, a real-time wait tests never take: release the wave now.
            Assert.Empty(active.Guards);
            Assert.Contains(active.Waves, w => w.Spawns.Count > 0);
            await MapStrongboxes.SpawnWavesNow(boxOwner.Player);
            active = Boxes()[0];
            Assert.NotEmpty(active.Guards);
            Assert.Empty(active.Pending);
            int guardCount = boxOwner.Player.Area.Monsters.Count;
            Assert.True(await MapStrongboxes.Use(boxPeer.Player, new(firstBox.Id, 0, 1, 0), 0));
            Assert.Equal(guardCount, boxOwner.Player.Area.Monsters.Count);
            // The items drop only once every guard is dead; the daemons run in real time, so the drop time is
            // fast-forwarded instead. With the guards still alive, no drop happens.
            Assert.True(active.DropAtTicks > 0);
            await MapStrongboxes.DropNow(boxOwner.Player);
            await MapStrongboxes.Tick(boxOwner.Player);
            Assert.Equal(1, Boxes()[0].Phase);
            Assert.Empty(boxInstance.Ground.All());
            foreach (uint guard in Boxes()[0].Guards)
            {
                var guardMonster = boxOwner.Player.Area.Monsters[guard];
                guardMonster.Dead = true;
                guardMonster.Life = 0;
            }
            await MapStrongboxes.Tick(boxOwner.Player);
            Assert.Equal(2, Boxes()[0].Phase);
            int rewardCount = boxInstance.Ground.All().Count();
            Assert.True(rewardCount > 0);
            await MapStrongboxes.Tick(boxPeer.Player);
            Assert.Equal(rewardCount, boxInstance.Ground.All().Count());
            MapStrongboxes.Leave(boxOwner.Player);
            await MapStrongboxes.Enter(boxOwner.Player);
            Assert.Equal(firstBox.Id, Boxes()[0].Id);
            Assert.Equal(2, Boxes()[0].Phase);
            Assert.Equal(rewardCount, boxInstance.Ground.All().Count());
            Assert.True(MapStrongboxes.ClosesAgain(15, new FixedRoll(14)));
            Assert.False(MapStrongboxes.ClosesAgain(15, new FixedRoll(15)));
            var essenceMods = catalog.Of([Slot("ScarabEssence1"), Slot("ScarabEssence2"), Slot("ScarabEssence4")], Program.Items.Mods);
            var essenceInstance = new InstanceState();
            essenceInstance.OpenedWith(new("EssenceOwner", new(layout.Hash, area.Id, "Strand", 3) { Level = 70 }, essenceMods));
            essenceInstance.AreaStats(() => Compose(essenceMods));
            await using var essenceOwner = await Wire.Open(essenceInstance, layout, seed);
            await using var essencePeer = await Wire.Open(essenceInstance, layout, seed);
            Assert.True(MapEssences.Count(essenceOwner.Player) >= 3);
            await MapEssences.Enter(essenceOwner.Player);
            MapEssences.Monolith[] Monoliths() => System.Text.Json.JsonSerializer.Deserialize<MapEssences.Monolith[]>(
                essenceInstance.ContentState["Essence/monoliths"])!;
            var sealedMonoliths = Monoliths();
            Assert.Equal(MapEssences.Count(essenceOwner.Player), sealedMonoliths.Length);
            await MapEssences.Enter(essencePeer.Player);
            Assert.Equal(sealedMonoliths.Select(m => m.Id), Monoliths().Select(m => m.Id));
            var firstMonolith = sealedMonoliths[0];
            var naturalRare = extra.First(s => s.Rarity == GameServer.Core.Protocol.Instance.MonsterRarity.Rare
                && ordinary.Scattered.Any(p => p.Pack.Id == s.Pack));
            Assert.True(await MapEssences.Imprison(essenceOwner.Player, naturalRare));
            Assert.Equal(sealedMonoliths.Length + 1, Monoliths().Length);
            var converted = Monoliths()[^1];
            Assert.Equal(Game.Monsters.Find(naturalRare.Variety.ToString())!.Id, converted.Variety);
            // The encased previews are instance monsters too (NoRewards, invulnerable); only a released monster counts.
            Assert.DoesNotContain(essenceOwner.Player.Area.Monsters, m => !m.Value.NoRewards);
            Assert.True(await MapEssences.Imprison(essencePeer.Player, naturalRare));
            Assert.Equal(sealedMonoliths.Length + 1, Monoliths().Length);
            Assert.False(await MapEssences.Imprison(essenceOwner.Player, naturalRare with { Rarity = GameServer.Core.Protocol.Instance.MonsterRarity.Unique }));
            Assert.False(await MapEssences.Imprison(essenceOwner.Player, naturalRare with { Pack = MapScarabPacks.Packs[0].Id }));
            essenceOwner.Player.Walk.Teleport(firstMonolith.X + 8, firstMonolith.Y, 0);
            for (byte stage = 2; stage <= 4; stage++)
            {
                Assert.True(await MapEssences.Use(essenceOwner.Player, new(firstMonolith.Id, 0, stage, 0), 0));
                Assert.Equal(stage, Monoliths()[0].Stage);
                // The rare and its pack's magic monsters release together (the essence daemons are friendly casters).
                Assert.Equal(stage == 4 ? 1 + firstMonolith.Minis.Count : 0, essenceOwner.Player.Area.Monsters.Count(m => m.Value.Hostile && !m.Value.NoRewards));
            }
            var released = Monoliths()[0];
            Assert.True(released.Monster > 0);
            var monster = essenceOwner.Player.Area.Monsters[released.Monster];
            Assert.Equal(released.Monster, monster.ObjectId);
            Assert.Equal(GameServer.Core.Protocol.Instance.MonsterRarity.Rare, monster.Rarity);
            Assert.Contains(monster.Mods.Mods, m => firstMonolith.Essences.Select(MapEssenceCatalog.Find)
                .OfType<MapEssenceCatalog.Essence>().SelectMany(e => e.MonsterMods)
                .Any(row => Program.Items.Mods.Row(row)!.Hash == m.Hash));
            var wrongMonster = new DropRoll(essenceOwner.Player, Game.ModMonster(monster, (released.X, released.Y)) with { ObjectId = released.Monster + 1 });
            MapEssences.Drops(wrongMonster);
            Assert.Empty(wrongMonster.Items);
            var rewards = new DropRoll(essenceOwner.Player, Game.ModMonster(monster, (released.X, released.Y)));
            MapEssences.Drops(rewards);
            Assert.Equal(released.Essences.Select(e => MapEssenceCatalog.Find(e)!.Item), rewards.Items);
            var duplicate = new DropRoll(essencePeer.Player, rewards.Monster);
            MapEssences.Drops(duplicate);
            Assert.Empty(duplicate.Items);
            await MapEssences.Use(essencePeer.Player, new(firstMonolith.Id, 0, 5, 0), 0);
            Assert.Equal(1 + released.Minis.Count, essenceOwner.Player.Area.Monsters.Count(m => m.Value.Hostile && !m.Value.NoRewards));
            MapEssences.Leave(essenceOwner.Player);
            await MapEssences.Enter(essenceOwner.Player);
            Assert.Equal((released.Id, released.Stage, released.Monster), (Monoliths()[0].Id, Monoliths()[0].Stage, Monoliths()[0].Monster));
            Assert.Equal(released.Essences, Monoliths()[0].Essences);
            var terrorStats = new Dictionary<uint, int>(catalog.Of([slot, Slot("ScarabDomination4")], Program.Items.Mods).Stats)
            {
                [Stat.MapBossMaximumLifePlusPct] = 40, [Stat.MapBossDamagePlusPct] = 30,
                [Stat.MapBossAttackAndCastSpeedPlusPct] = 20,
            };
            var terrorMods = new MapMods(terrorStats);
            var terrorInstance = new InstanceState();
            terrorInstance.OpenedWith(new("TerrorsOwner", new(layout.Hash, area.Id, "Strand", 3) { Level = 70 }, terrorMods));
            terrorInstance.AreaStats(() => Compose(terrorMods));
            await using var terrors = await Wire.Open(terrorInstance, layout, seed);
            var terrorGuards = MapScarabBosses.Guards(terrors.Player, [.. plan.Select(p => (p.X, p.Y))], []);
            Assert.Equal(plan.Count, terrorGuards.Count);
            Assert.All(terrorGuards, s =>
            {
                Assert.Equal(GameServer.Core.Protocol.Instance.MonsterRarity.Unique, s.Rarity);
                Assert.Equal(MapScarabBosses.PackId, s.Pack);
                Assert.Contains("Standalone", Game.Monsters.Find(s.Variety.ToString())!.Id);
            });
            Assert.Equal(terrorGuards, MapScarabBosses.Guards(terrors.Player, [.. plan.Select(p => (p.X, p.Y))], []));
            var guardPlan = terrorGuards[0];
            var guardVariety = Game.Monsters.Find(guardPlan.Variety.ToString())!;
            uint bossId = await Game.SpawnMonster(terrors.Player, guardVariety, 70, guardPlan.Rarity, guardPlan.X, guardPlan.Y, unrisen: false);
            var boss = terrors.Player.Area.Monsters[bossId];
            Assert.Equal(40, boss.Mods.Effects[Stat.MaximumLifePlusPct]);
            Assert.Equal(30, boss.Mods.Effects[Stat.DamagePlusPct]);
            Assert.Equal(20, boss.Mods.Effects[Stat.AttackSpeedPlusPct]);
            Assert.Equal(20, boss.Mods.Effects[Stat.BaseCastSpeedPlusPct]);
            PlayerInventories.Forget(terrors.Player.Character.Name!);
            PlayerInventories.Forget(essenceOwner.Player.Character.Name!);
            PlayerInventories.Forget(essencePeer.Player.Character.Name!);
            PlayerInventories.Forget(boxOwner.Player.Character.Name!);
            PlayerInventories.Forget(boxPeer.Player.Character.Name!);
        }
        finally
        {
            await ShrineManager.LeaveArea(owner.Player);
            await ShrineManager.LeaveArea(peer.Player);
            PlayerInventories.Forget(owner.Player.Character.Name!);
            PlayerInventories.Forget(peer.Player.Character.Name!);
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseOwnSaves();
        }
    }

    [Fact]
    public void Named_content_diagnostics_keep_item_provenance_and_allow_enabled_providers_in_either_order()
    {
        var player = new Player(new(1, "scarab-diagnostics", 100, 100, 10, 10, null, []), 1, 1)
            { Character = { Name = "ScarabDiagnostics" } };
        var opening = new MapDevice.Opening(new(1, "MapWorldsStrand", "Strand Map", 3), MapMods.None);
        MapOpening Request(params string[] names) => new(player, names.Select(Slot).ToArray(), false, opening, "");
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
        var rareBoxes = new MapMods(new Dictionary<uint, int> { [Stat.MapStrongboxRareBoxesWeightingMultiplier] = 3 });
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

    internal sealed class Wire(TcpClient writer, TcpClient reader, Player player, Task drain) : IAsyncDisposable
    {
        public Player Player => player;
        public static async Task<Wire> Open(InstanceState instance, AreaLayout layout, uint seed, string? character = null)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            var writer = new TcpClient();
            await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var reader = await accepting;
            var player = new Player(layout, 1, seed)
            {
                Connection = { LockstepClient = false, PlayerSent = true, Session = new("scarab-regression", writer.GetStream(), new ConnectionCiphers(new byte[64], false)) },
                Character = { Name = character ?? "ScarabMap" + Guid.NewGuid().ToString("N") },
                Instance = instance,
                PlayerLife = 1000,
                PlayerMana = 1000,
                Pools = new(70, 1000, 1000, 1000, 0),
                LeagueModule = new(new GameServer.Core.Accounts.League("scarab-test", "Standard", false, false),
                    new HashSet<string>(["Game", "scarab-regression"]))
            };
            player.Clock.Stop();
            return new(writer, reader, player, reader.GetStream().CopyToAsync(Stream.Null));
        }
        public async ValueTask DisposeAsync()
        {
            Player.Closed = true;
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
