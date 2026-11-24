using GameServer.Core;
using GameServer.Modules.Legion;

namespace GameServer.Gateway.Tests;

public sealed class LegionTests
{
    [Fact]
    public void Timer_follows_the_balance_row_of_the_level_and_the_duration_stat()
    {
        var data = LegionData.Current;
        Assert.Equal(30000, LegionMonolith.TimerMilliseconds(data, 1, 0));
        Assert.Equal(15000, LegionMonolith.TimerMilliseconds(data, 68, 0));
        Assert.Equal(15000, LegionMonolith.TimerMilliseconds(data, 83, 0));
        Assert.Equal(22500, LegionMonolith.TimerMilliseconds(data, 68, 50));
        Assert.Equal(7500, LegionMonolith.TimerMilliseconds(data, 68, -50));
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
