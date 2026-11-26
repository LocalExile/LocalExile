using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using GameServer.Modules.Legion;
using GameServer.Modules.Abyss;
using GameServer.Modules.Mercenary;
using System.Text.Json;

namespace GameServer.Gateway.Tests;

public sealed class FourFamilyScarabTests
{
    [Fact]
    public async Task Paid_family_inputs_preserve_their_content_plan_and_reward_semantics()
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
        AbyssModule.LoadData();
        Assert.Equal(1, AbyssEncounter.PitCount(new Dictionary<uint, int> { [Stat.MapContainsAbyssDepths] = 1 }, false));
        MercenaryModule.LoadData();
        foreach (string leaf in Enumerable.Range(1, 4).Select(i => $"ScarabMercenaries{i}"))
        {
            var request = Input(leaf); MercenaryModule.Opening(request); Assert.Empty(request.UnhandledContent);
        }
        for (int seed = 0; seed < 20; seed++)
        {
            var pick = MercenaryTables.Pick(new Random(seed), true)!.Value;
            Assert.True(pick.Build.Infamous);
            var worn = MercenaryEquipment.Roll(pick.Kind, pick.Build, 83, new Random(seed), true, Game.UniquesFor(player));
            Assert.True(worn.Count >= 8);
            Assert.All(worn, w => { Assert.Equal(ItemRarity.Unique, w.Item.Rarity); Assert.Equal(w.Item.VisualIdentity, w.Look.VisualIdentity); Assert.NotEmpty(w.Item.Payload()); });
        }
        foreach (string leaf in Enumerable.Range(1, 4).Select(i => $"ScarabAbyss{i}").Concat(Enumerable.Range(1, 5).Select(i => $"ScarabAbyssNew{i}")))
        {
            var request = Input(leaf); AbyssScarabs.Opening(request); Assert.Empty(request.UnhandledContent);
            if (request.Opening!.Mods.Get(Stat.MapNumExtraAbysses) is int extra and > 0)
            {
                Assert.Equal(2 * extra - 1, AbyssEncounter.PitCount(request.Opening.Mods.Stats, false));
                Assert.Equal(1 + 2 * extra, AbyssEncounter.PitCount(request.Opening.Mods.Stats, true));
            }
        }
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
                    var v = Game.Monsters.Find(packs.Packs[0].Entries[0].Variety.ToString())!;
                    var applied = MapInfluence.MonsterMods(v, MonsterModSet.None, mods);
                    Assert.Equal(flag == MapInfluence.Shaper ? 1 : 2, applied.Effects[Stat.DroppedItemsHaveInfluenceType]);
                    Assert.Same(applied, MapInfluence.MonsterMods(v, applied, mods));
                    Assert.Contains(flag, mods.WithFragmentStat(Stat.MapItemDropQuantityPlusPct, 25).ContentFlags);
                }
            }
        var shaper = Input("ScarabShaperRares4"); MapInfluence.Opening(shaper);
        player.Use(new AreaLayout(beach.Hash16, beach.Id, 600, 600, 10, 10, (_, _) => true, []));
        player.Instance.OpenedWith(MapDevice.Run.Of(player.Character.Name!, MapDevice.Activate(player.Character.Name!, beach,
            shaper.Opening!.Mods.WithFragmentStat(Stat.MapLegionMultipleBreakout, 100))));
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

            var variety = Game.Monsters.Find(LegionData.Current.MonsterVarieties.First(m => Game.Monsters.Find(m.Monster.ToString()) is { Supported: true }).Monster.ToString())!;
            var action = new ActorAction(0x18fe, 123) { Animation = new AnimationControllerState(0), Payload = new BlobWriter().I32(0).U8(2).U32(100).F32(0).F32(0).ToArray() };
            uint frozenId = await Game.SpawnMonster(player, variety, 83, MonsterRarity.Normal, 100, 100, scripted: true, currentAction: action,
                owned: (_, m) => m.NoRewards = true, shape: o => o with { StateMachine = o.StateMachine?.Set("present", 1) });
            var frozen = player.Area.Monsters[frozenId];
            SavedMonster Capture(uint id, SpawnedMonster monster) => Assert.Single(SavedMonster.Save([monster], Game.AreaTime(player),
                origin: _ => player.Instance.World.MonsterOrigins[id].Object));
            void Forget(uint id) { player.Area.Monsters.Remove(id); player.Instance.World.MonsterOrigins.TryRemove(id, out _); }
            var saved = JsonSerializer.Deserialize<SavedMonster>(JsonSerializer.Serialize(Capture(frozenId, frozen)))!;
            Forget(frozenId);
            uint restoredId = await Game.RestoreMonster(player, variety, saved);
            Assert.NotEqual(frozenId, restoredId);
            var restored = player.Area.Monsters[restoredId];
            Assert.True(restored.Scripted);
            Assert.True(restored.NoRewards);
            Assert.Equal(action.Payload, player.Instance.World.MonsterOrigins[restoredId].Object.CurrentAction!.Payload);
            var member = new LegionStasis.Member(frozenId, 1, 0, 2, false, false, Broken: true, Rewards: [0], Breakouts: 2, Variety: variety.Id, X: saved.X, Y: saved.Y);
            var rebound = Assert.Single(LegionStasis.RebindArmy(player, [member]));
            Assert.Equal(restoredId, rebound.Id);
            uint fighterId = await LegionStasis.Wake(player, rebound, restored, 100, 100, "Karui");
            var fighter = player.Area.Monsters[fighterId];
            Assert.True(fighter.Effects[Stat.MaximumLifePlusPct] >= 100);
            Assert.True(fighter.Effects[Stat.DamagePlusPct] >= 100);
            await LegionStasis.KeepFighters(player);
            var fightingSave = Capture(fighterId, fighter); Forget(fighterId);
            uint newFighterId = await Game.RestoreMonster(player, variety, fightingSave);
            LegionStasis.RestoreFighters(player);
            Assert.Equal(3, LegionStasis.FighterOf(player, newFighterId, false)!.RewardFactor);
            var reward = new DropRoll(player, new MonsterInfo(variety.Id, variety.Name, 83, MonsterRarity.Normal, saved.X, saved.Y) { ObjectId = newFighterId });
            LegionRewards.Drops(reward);
            Assert.Equal(3, reward.Quantity);
            Assert.True(reward.Made.Count + reward.Stacks.Count > 0);
            Assert.Null(LegionStasis.FighterOf(player, newFighterId, true));
            LegionStasis.ClearFighters();
        }
        finally { MapDevice.Close(player.Character.Name!); }
    }
}
