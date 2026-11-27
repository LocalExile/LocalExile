using System.Text.Json;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using GameServer.Modules.Betrayal;
using GameServer.Modules.Expedition;
using GameServer.Modules.Incursion;

namespace GameServer.Gateway.Tests;

public sealed class NextFourScarabTests
{
    [Fact]
    public void Paid_pack_substitution_and_encounter_modifiers_preserve_encounter_identity()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        MapScarabPacks.LoadGameData(); MapScarabBosses.LoadGameData(); MapAllflame.LoadGameData();
        ExpeditionModule.LoadData(); IncursionModule.LoadData(); BetrayalModule.LoadData();
        var catalog = MapRunCatalog.Load("data");
        var beach = new MapDevice.MapArea(AreaTransitionCatalog.HashOf("MapWorldsBeach")!.Value, "MapWorldsBeach", "Beach", 16);
        MapOpening Input(string leaf)
        {
            var type = Program.Items.All.Single(t => t.Id.EndsWith("/" + leaf, StringComparison.Ordinal));
            var slot = new MapDevice.Slot(type.Hash, type.Class, type.Name, type.Row, [], 0) { BaseId = type.Id };
            return new(fixture.Player, [slot], false, new(beach, catalog.Of([slot], Program.Items.Mods)), "");
        }
        MapScarabContent.LoadGameData();
        var inputs = Enumerable.Range(1, 4).Select(i => $"ScarabPerandus{i}")
            .Concat(Enumerable.Range(1, 5).Select(i => $"ScarabExpedition{i}"))
            .Concat(Enumerable.Range(1, 4).Select(i => $"ScarabIncursion{i}"))
            .Concat(Enumerable.Range(1, 4).Select(i => $"ScarabBetrayal{i}")).Append("ScarabUber7");
        foreach (string leaf in inputs)
        {
            var input = Input(leaf); MapScarabContent.Describe(input); Assert.NotEmpty(input.UnhandledContent);
            ExpeditionModule.Opening(input); IncursionModule.Opening(input); BetrayalModule.Opening(input);
            MapAllflame.Opening(input); MapScarabPacks.Opening(input); MapScarabBosses.Opening(input);
            Assert.Empty(input.UnhandledContent);
        }
        var invasion = Input("ScarabIncursion2");
        MapScarabPacks.Opening(invasion, new Random(505));
        var additional = Assert.Single(MapScarabPacks.Requests(invasion.Opening!.Mods));
        Assert.InRange(additional.Count, 12, 16); Assert.NotEmpty(additional.Packs);
        Assert.All(additional.Packs, p => Assert.StartsWith("incursion-", p.Id));
        for (int seed = 0; seed < 20; seed++)
        {
            var mods = ExpeditionTables.RollRelicMods(new Random(seed), 2, 2);
            Assert.Equal(3, mods.Count(m => m.GenerationType == 1));
            Assert.Equal(3, mods.Count(m => m.GenerationType == 2));
            Assert.Equal(mods.Count, mods.Select(m => m.Hash).Distinct().Count());
            Assert.Equal(mods.SelectMany(m => m.Families).Count(), mods.SelectMany(m => m.Families).Distinct().Count());
        }
        var source = Enumerable.Range(1, 80).SelectMany(i => Enumerable.Range(0, 3).Select(j =>
            new AreaPopulation.Spawn(1, 100 + i * 20 + j, 100, MonsterRarity.Normal, "ordinary") { PackId = i })).ToList();
        var boss = new AreaPopulation.Spawn(MapScarabBosses.Bosses[0].Row, 50, 50, MonsterRarity.Unique, "mapboss") { PackId = 81 };
        source.Add(boss);
        var all = MapMods.None.WithFragmentStat(Stat.MapAtlasBossPackReplacementChancePct, 100);
        var replaced = MapScarabBosses.ReplacePacks(source, all, 505, (_, _, _) => true);
        Assert.Equal(81, replaced.Count); Assert.Contains(boss, replaced);
        Assert.All(replaced, s => Assert.Equal(MonsterRarity.Unique, s.Rarity));
        Assert.Equal(replaced, MapScarabBosses.ReplacePacks(replaced, all, 505, (_, _, _) => true));
        Assert.Equal(source, MapScarabBosses.ReplacePacks(source, all, 505, (_, _, _) => false));
        var ember = MapMods.None.WithFragmentStat(Stat.MapNumAllflameMeatsackPacks, 18);
        int Substituted(List<AreaPopulation.Spawn> plan) => plan.Where(s => s.Pack.StartsWith("Allflame/", StringComparison.Ordinal))
            .Select(s => s.PackId).Distinct().Count();
        Assert.Equal(18, Substituted(MapAllflame.ReplacePacks(source, ember, 505, (_, _) => true)));
        var boosted = MapAllflame.ReplacePacks(source, ember.WithFragmentStat(Stat.MapAllflameWeightEffectivenessPct, 75), 505, (_, _) => true);
        Assert.Equal(31, Substituted(boosted)); Assert.Contains(boss, boosted);
        Assert.Equal(boosted, MapAllflame.ReplacePacks(boosted, ember.WithFragmentStat(Stat.MapAllflameWeightEffectivenessPct, 75), 505, (_, _) => true));
        var member = BetrayalTables.Members.First(m => m.Rank > 0);
        var board = new Dictionary<int, BetrayalRanks.Target>();
        board[member.Row] = BetrayalRanks.Interrogate(new(2), 100, new Random(505));
        string saved = JsonSerializer.Serialize(board);
        board = BetrayalRanks.Read(saved);
        BetrayalRanks.Advance(board); BetrayalRanks.Advance(board);
        Assert.Equal(1, board[member.Row].Turns); Assert.Equal(2, board[member.Row].Rank);
        BetrayalRanks.Advance(board); Assert.Equal(new BetrayalRanks.Target(2), board[member.Row]);
        board[member.Row] = BetrayalRanks.Interrogate(board[member.Row], 0, new Random(505));
        for (int i = 0; i < 3; i++) BetrayalRanks.Advance(board);
        Assert.Equal(new BetrayalRanks.Target(1), board[member.Row]);
        var plan = BetrayalPlan.Place(member, (500, 500), (_, _) => true, new Random(505))!;
        var mission = new BetrayalRun(plan, 83);
        mission.Advance(BetrayalRun.Stage.Waiting); mission.After(47, BetrayalRun.Due.Leave);
        var json = new JsonSerializerOptions { IncludeFields = true };
        var restoredMission = BetrayalRun.Restore(JsonSerializer.Deserialize<BetrayalRun.Snapshot>(JsonSerializer.Serialize(mission.Save(), json), json)!);
        Assert.Equal(mission.Current, restoredMission.Current);
        Assert.Equal(mission.Plan.Member.Row, restoredMission.Plan.Member.Row);
        Assert.Single(restoredMission.Step(47 * BetrayalRun.FrameSeconds));
        var incursion = new IncursionRun([(1, 1), (2, 2), (3, 3)], 83, 505);
        incursion.Step(IncursionRun.Stage.Hidden, IncursionRun.Stage.Open);
        incursion.Start([1, 2, 3], new Dictionary<uint, int> { [3] = 5 }, 10); incursion.Begin(); incursion.Kill(1, 2);
        var restoredRun = IncursionRun.Restore(JsonSerializer.Deserialize<IncursionRun.Snapshot>(JsonSerializer.Serialize(incursion.Save(), json), json)!);
        Assert.Equal(incursion.Left, restoredRun.Left); Assert.Equal(incursion.Killed, restoredRun.Killed);
        Assert.False(restoredRun.Kill(1, 2)); Assert.True(restoredRun.Kill(3, 0)); Assert.Equal(new[] { 5 }, restoredRun.KilledArchitects);
        Assert.True(Enumerable.Range(0, 1000).Count(s => BetrayalEncounter.Reinforcements(50, new(s)))
            > Enumerable.Range(0, 1000).Count(s => BetrayalEncounter.Reinforcements(0, new(s))));
    }

    [Fact]
    public void Structured_encounter_rewards_keep_their_actual_rolls_through_SQL_and_item_encoding()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        ExpeditionModule.LoadData(); IncursionModule.LoadData();
        {
            var book = ExpeditionLogbooks.Create(83, new Random(505), true)!;
            Assert.NotNull(book.ExpeditionSaga);
            Assert.InRange(book.ExpeditionSaga.Areas.Count, 2, 3);
            var hashes = ClientTables.Table("WorldAreas").Select(a => (ushort)a.Int("HASH16")).ToHashSet();
            foreach (var area in book.ExpeditionSaga.Areas)
            {
                Assert.Contains(area.AreaHash, hashes); Assert.Equal(4, area.Implicits.Count);
                Assert.All(area.Implicits, m => Assert.Equal(Program.Items.Mods.ByHash(m.Hash)!.Stats.Count, m.Values.Count));
            }
            Assert.All(ExpeditionLogbooks.Create(83, new Random(505), false)!.ExpeditionSaga!.Areas, a => Assert.Equal(3, a.Implicits.Count));
            var temple = IncursionTemple.Create("ScarabReward", 83)!;
            string original = temple.ToJson();
            var templeType = Program.Items.Find("Metadata/Items/Incursion/ItemisedTemple")!;
            var item = ItemCatalog.Create(templeType) with { ItemLevel = 83, IncursionTemple = temple.ItemData(83, new Random(505), true) };
            Assert.Equal(11, item.IncursionTemple!.Rooms.Count); Assert.Equal(original, temple.ToJson());
            Assert.Equal(temple.ItemData(83, new Random(1), false).Connections, item.IncursionTemple.Connections);
            var beach = new MapDevice.MapArea(AreaTransitionCatalog.HashOf("MapWorldsBeach")!.Value, "MapWorldsBeach", "Beach", 16);
            fixture.Player.Use(new AreaLayout(beach.Hash16, beach.Id, 600, 600, 10, 10, (_, _) => true, []));
            fixture.Player.Instance.OpenedWith(MapDevice.Run.Of(fixture.Player.Character.Name, new(beach,
                MapMods.None.WithFragmentStat(Stat.MapExpeditionPctChanceLogbooksDropWithFourImplicits, 100)
                    .WithFragmentStat(Stat.MapFinalIncursionArchitectCopiesTemple, 1))));
            var normalBook = ExpeditionLogbooks.Create(83, new Random(505), false)!;
            var prepared = ExpeditionLogbooks.Prepare(fixture.Player, normalBook);
            for (int i = 0; i < normalBook.ExpeditionSaga!.Areas.Count; i++)
            {
                Assert.Equal(4, prepared.ExpeditionSaga!.Areas[i].Implicits.Count);
                Assert.Equal(normalBook.ExpeditionSaga.Areas[i].Implicits, prepared.ExpeditionSaga.Areas[i].Implicits.Take(3));
            }
            var randomized = IncursionModule.Prepare(fixture.Player, item);
            Assert.Equal(item.IncursionTemple!.Connections, randomized.IncursionTemple!.Connections);
            Assert.Equal(original, temple.ToJson());
            var items = new[] { prepared, randomized };
            CharacterSaves.Write("ScarabRewards", new(new() { [6] = [.. items.Select((i, n) => new CharacterSaves.SavedPlacement((uint)n + 1, 0, (byte)n, CharacterSaves.ToSaved(i, Program.Items.Mods)))] }));
            var read = CharacterSaves.Load("ScarabRewards")!.Inventories[6];
            for (int i = 0; i < items.Length; i++)
            {
                var restored = CharacterSaves.FromSaved(read[i].Item, Program.Items);
                Assert.Equal(items[i].Payload(), restored.Payload());
            }
            var emptyBook = ItemCatalog.Create(Program.Items.Find(ExpeditionLogbooks.Type)!);
            Assert.NotEmpty(emptyBook.Payload());
        }
    }
}
