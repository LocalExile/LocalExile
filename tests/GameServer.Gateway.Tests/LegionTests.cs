using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modules.Legion;

namespace GameServer.Gateway.Tests;

public sealed class LegionTests
{
    [Fact]
    public async Task In_the_areas_frame_a_record_goes_once_to_every_client_and_a_joiners_to_it_alone()
    {
        var layout = new AreaLayout(0x1e9, "MapWorldsReef", 600, 600, 300, 300, (_, _) => true, []);
        var instance = new InstanceState();
        Player Make()
        {
            var player = new Player(layout, 1, 1) { Instance = instance, Connection = { PlayerSent = true } };
            instance.AddPlayer(player);
            player.Connection.WorldOutput.Begin(1);
            return player;
        }
        Player first = Make(), second = Make();
        Assert.Same(first.Area, second.Area);
        const uint id = 0x500;
        var owner = GameArea.Own(first.Area);
        try
        {
            // A per-player loop would put one copy per player in the frame that every client is written.
            await LegionOutput.ToArea(first, p => Program.Send(p, InstanceProtocol.EnterWakeZone(id), null));
            Assert.Null(Assert.Single(first.Area.Delta.Take()).Only);
            await LegionOutput.ToOne(second, () => Program.Send(second, InstanceProtocol.EnterWakeZone(id), null));
            Assert.Same(second, Assert.Single(first.Area.Delta.Take()).Only);
        }
        finally { GameArea.Own(owner); }
    }

    [Fact]
    public void Army_plan_has_two_factions_fixed_campaign_pair_and_no_general_before_46()
    {
        var data = LegionData.Current;
        var none = new Dictionary<uint, int>();
        for (int seed = 0; seed < 100; seed++)
        {
            var climb = LegionArmy.Plan(data, 1, "1_1_6", none, new Random(seed));
            Assert.Equal(["EternalEmpire", "Karui"], climb.Factions.Select(f => f.Id).Order().ToArray());
            var early = LegionArmy.Plan(data, 45, "MapAtziri", none, new Random(seed));
            Assert.Equal(2, early.Factions.Select(f => f.Faction).Distinct().Count());
            Assert.DoesNotContain(early.Factions.SelectMany(f => f.Squads), s => s.Tier == 3);
            var late = LegionArmy.Plan(data, 70, "MapAtziri", none, new Random(seed));
            Assert.Equal(2, late.Factions.Select(f => f.Faction).Distinct().Count());
            foreach (var faction in late.Factions.Concat(climb.Factions).Concat(early.Factions))
                foreach (var squad in faction.Squads)
                    Assert.Contains(data.MonsterVarieties, v => v.Monster == squad.Monster && v.Faction == faction.Faction);
        }
        // A general is rolled at a high level (about 8% there): some seed in a fixed range has one, so a roll that never succeeds fails.
        Assert.Contains(Enumerable.Range(0, 400), seed => LegionArmy.Plan(data, 75, "MapAtziri", none, new Random(seed))
            .Factions.SelectMany(f => f.Squads).Any(s => s.Tier == 3));
    }
}
