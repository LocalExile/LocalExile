using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using GameServer.Modules.Legion;

namespace GameServer.Gateway.Tests;

public sealed class FourFamilyScarabTests
{
    [Fact]
    public void Paid_family_inputs_preserve_their_content_plan_and_reward_semantics()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        MapInfluence.LoadGameData();
        MapScarabContent.LoadGameData();
        var catalog = MapRunCatalog.Load("data");
        var beach = new MapDevice.MapArea(AreaTransitionCatalog.HashOf("MapWorldsBeach")!.Value, "MapWorldsBeach", "Beach", 16);
        MapOpening Input(string leaf)
        {
            var type = Program.Items.All.Single(t => t.Id.EndsWith("/" + leaf, StringComparison.Ordinal));
            var slot = new MapDevice.Slot(type.Hash, type.Class, type.Name, type.Row, [], 0) { BaseId = type.Id };
            var request = new MapOpening(player, [slot], false, new(beach, catalog.Of([slot], Program.Items.Mods)), "");
            MapScarabContent.Describe(request);
            Assert.NotEmpty(request.UnhandledContent);
            return request;
        }
        Assert.Equal(16, MapInfluence.Companions.Count);
        LegionModule.LoadData();
        foreach (string leaf in Enumerable.Range(1, 4).Select(i => $"ScarabLegion{i}").Concat(Enumerable.Range(1, 5).Select(i => $"ScarabLegionNew{i}")))
        {
            var request = Input(leaf); LegionScarabs.Opening(request); Assert.Empty(request.UnhandledContent);
        }
        var legionStats = new Dictionary<uint, int>
        {
            [Stat.MapLegionLeagueForceGeneral] = 1, [Stat.MapLegionLeagueForceWarChest] = 1,
            [Stat.MapLegionContainsMarakethFaction] = 1, [Stat.MapLegionAdditionalNumberOfSergeants] = 5,
        };
        for (int seed = 0; seed < 25; seed++)
        {
            var plan = LegionArmy.Plan(LegionData.Current, 83, beach.Id, legionStats, new Random(seed));
            Assert.Contains(plan.Factions, f => f.Id == "Maraketh");
            Assert.All(plan.Factions, f => { Assert.Equal(1, f.Squads.Where(s => s.Tier == 3).Sum(s => s.Count)); Assert.Contains(f.Chests, c => c.WarHoard); });
            Assert.True(plan.Factions.SelectMany(f => f.Squads).Where(s => s.Tier == 2).Sum(s => s.Count) >= 5);
        }
        Assert.Equal(3, LegionScarabs.Rewards(2, 100));
        Assert.Equal(200, LegionScarabs.Difficulty(2, true));
        foreach (string family in new[] { "ShaperRares", "ElderRares", "InfluenceNew" })
            for (int i = 1; i <= 4; i++)
            {
                var request = Input($"Scarab{family}{i}");
                MapInfluence.Opening(request);
                Assert.Empty(request.UnhandledContent);
                var mods = request.Opening!.Mods;
                if (family != "InfluenceNew" || i <= 2)
                {
                    string flag = family == "ElderRares" || family == "InfluenceNew" && i == 2 ? MapInfluence.Elder : MapInfluence.Shaper;
                    Assert.Contains(flag, mods.ContentFlags);
                    var packs = Assert.Single(MapInfluence.Requests(mods));
                    Assert.NotEmpty(packs.Packs);
                    Assert.Contains(flag, mods.WithFragmentStat(Stat.MapItemDropQuantityPlusPct, 25).ContentFlags);
                }
            }
        var shaper = Input("ScarabShaperRares4"); MapInfluence.Opening(shaper);
        player.Use(new AreaLayout(beach.Hash16, beach.Id, 600, 600, 10, 10, (_, _) => true, []));
        player.Instance.OpenedWith(MapDevice.Run.Of(player.Character.Name!, MapDevice.Activate(player.Character.Name!, beach, shaper.Opening!.Mods)));
        try
        {
            var type = Program.Items.Find("Iron Ring")!;
            var item = Program.Rolls.Roll(ItemCatalog.Create(type) with { ItemLevel = 83 }, ItemRarity.Rare, new Random(505));
            var roll = new DropRoll(player, new MonsterInfo("Metadata/Monsters/AtlasBosses/PhoenixBossStandaloneScarab", "Guardian", 83, MonsterRarity.Unique, 200, 200));
            MapInfluence.Drops(roll);
            Assert.Equal(0.2, roll.Quantity, 6);
            var drops = roll.Changed([new(item, type)], new Random(505)).ToArray();
            Assert.InRange(drops.Length, 2, 3);
            Assert.All(drops, d => { Assert.Equal(ItemInfluence.Shaper, d.Item.Influences); Assert.Equal(ItemRarity.Rare, d.Item.Rarity); Assert.NotEmpty(d.Item.Payload()); });
            Assert.NotSame(drops[0].Item, drops[1].Item);
            var influence = Input("ScarabInfluenceNew1"); MapInfluence.Opening(influence);
            var hordes = Input("ScarabInfluenceNew3");
            var values = hordes.Opening!.Mods.Stats;
            var combined = new MapMods(values).WithContentFlags([MapInfluence.Shaper]);
            Assert.Equal(1.4, Assert.Single(MapInfluence.Requests(combined)).Size, 6);
        }
        finally { MapDevice.Close(player.Character.Name!); }
    }
}
