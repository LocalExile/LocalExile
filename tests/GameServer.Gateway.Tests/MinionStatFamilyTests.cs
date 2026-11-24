namespace GameServer.Gateway.Tests;

// The minion stat family: a minion_* stat whose stripped name the monster code does not
// read has to be mapped onto the stat it does read, and the two the strip cannot reach
// at all (the plural poison chance, the dot multiplier with the prefix in the middle)
// are added explicitly. The rest of the family the strip already forwards, so the test
// pins those too: nothing may count twice.
public sealed class MinionStatFamilyTests
{
    [Fact]
    public void The_minion_stats_whose_stripped_name_nothing_reads_reach_the_summons_own_stats()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var player = f.Player;
        var golem = new GolemState.Golem(0xB0A1, null!, Game.GolemData.Find("SummonFlameGolem")!, 1);
        var plain = Game.GolemModifiers(player, golem, Game.CharacterPlayerStats(player));
        var stats = new Dictionary<uint, int>(Game.CharacterPlayerStats(player))
        {
            [Stat.MinionSpellSuppressionChancePct] = 23,
            [Stat.BaseMinionDotMultiplierPlus] = 30,
            [Stat.MinionElementalResistancePct] = 15,
            [Stat.MinionChaosResistancePct] = 29,
            [Stat.MinionsChanceToPoisonOnHitPct] = 206,
            [Stat.MinionChanceToDealDoubleDamagePct] = 8,
            [Stat.MinionCastSpeedPlusPct] = 42,
            [Stat.MinionCriticalStrikeChancePlusPct] = 80,
        };
        var with = Game.GolemModifiers(player, golem, stats);
        int Delta(uint stat) => with.Effects[stat] - plain.Effects[stat];
        // The two whose stripped name nothing reads now land on the stat the monster code reads.
        Assert.Equal(23, Delta(Stat.BaseSpellSuppressionChancePct));
        Assert.Equal(30, Delta(Stat.DotMultiplierPlus));
        // Nothing lands on the stripped spell suppression name itself.
        Assert.Equal(plain.Effects[Stat.SpellSuppressionChancePct], with.Effects[Stat.SpellSuppressionChancePct]);
        // The family members the strip already forwards are untouched, so nothing counts twice.
        Assert.Equal(15, Delta(Stat.BaseResistAllElementsPct));
        Assert.Equal(29, Delta(Stat.BaseChaosDamageResistancePct));
        Assert.Equal(206, Delta(Stat.BaseChanceToPoisonOnHitPct));
        Assert.Equal(8, Delta(Stat.ChanceToDealDoubleDamagePct));
        Assert.Equal(42, Delta(Stat.CastSpeedPlusPct));
        Assert.Equal(80, Delta(Stat.CriticalStrikeChancePlusPct));
    }
}
