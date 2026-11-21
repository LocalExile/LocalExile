namespace GameServer.Gateway.Tests;

// The Eater of Worlds body-armour implicits ("1% less Damage Taken per X attribute") and their final multiplier.
public sealed class IncomingResourceDefencesTests
{
    [Fact]
    public void Attribute_implicits_take_one_percent_less_damage_per_their_X()
    {
        var pools = new CharacterStats.Pools(1, 100, 0, 0, 0, Strength: 450, Dexterity: 1000, Intelligence: 2000);
        int Get(string name) => name switch
        {
            IncomingResourceDefences.LessPerStrength => 200,
            IncomingResourceDefences.LessPerDexterity => 400,
            IncomingResourceDefences.LessPerIntelligence => 200,
            _ => 0,
        };

        // 450 / 200 + 1000 / 400 + 2000 / 200 = 2 + 2 + 10.
        Assert.Equal(14, IncomingResourceDefences.LessTakenPct(pools, Get));
        Assert.Equal(0, IncomingResourceDefences.LessTakenPct(pools, _ => 0));
    }

    [Fact]
    public void Less_damage_taken_multiplies_the_final_taken_stat_on_hits_and_over_time()
    {
        int Harbinger(string name) => name == IncomingResourceDefences.TakenStat ? -50 : 0;

        Assert.Equal(90, IncomingResourceDefences.Taken(100, _ => 0, lessPct: 10));
        Assert.Equal(45, IncomingResourceDefences.Taken(100, Harbinger, lessPct: 10));
        Assert.Equal(100, IncomingResourceDefences.Taken(100, _ => 0));

        double carry = 0;
        Assert.Equal(45, IncomingResourceDefences.TakenOverTime(101, Harbinger, ref carry, lessPct: 10));
        Assert.Equal(0.45, carry, 6);
    }

    [Fact]
    public void Handed_over_final_taken_stats_multiply_hits_over_time_and_factors_until_disposed()
    {
        const string stat = "test_handed_over_damage_taken_+%_final";
        var pools = new CharacterStats.Pools(1, 100, 0, 0, 0, Strength: 2000);
        int LessOnly(string name) => name == stat ? -10 : 0;
        int Combined(string name) => name switch
        {
            IncomingResourceDefences.TakenStat => -50,
            IncomingResourceDefences.LessPerStrength => 200, // 10% less
            stat => -10,
            _ => 0,
        };

        // Inactive before hand-over: stat is ignored.
        Assert.Equal(100, IncomingResourceDefences.Taken(100, LessOnly));
        Assert.Equal(1.0, IncomingResourceDefences.FinalTakenFactor(pools, LessOnly), 6);

        var first = IncomingResourceDefences.HandOverFinalTaken([stat]);
        try
        {
            // A duplicate hand-over is a safe no-op whose disposal does not remove the first registration.
            using (IncomingResourceDefences.HandOverFinalTaken([stat])) { }

            Assert.Equal(90, IncomingResourceDefences.Taken(100, LessOnly));
            Assert.Equal(0.9, IncomingResourceDefences.FinalTakenFactor(pools, LessOnly), 6);
            // Multiplicative stacking: 100 * 0.5 (TakenStat) * 0.9 (attribute implicit) * 0.9 (handed over) = 40.5 -> 40.
            Assert.Equal(40, IncomingResourceDefences.Taken(100, Combined, lessPct: 10));
            Assert.Equal(0.405, IncomingResourceDefences.FinalTakenFactor(pools, Combined), 6);

            double carry = 0;
            Assert.Equal(40, IncomingResourceDefences.TakenOverTime(100, Combined, ref carry, lessPct: 10));
            Assert.Equal(0.5, carry, 6);
            Assert.Equal(41, IncomingResourceDefences.TakenOverTime(100, Combined, ref carry, lessPct: 10));
            Assert.Equal(0.0, carry, 6);
        }
        finally
        {
            first.Dispose();
            first.Dispose(); // idempotent
        }

        // Restored after disposal.
        Assert.Equal(100, IncomingResourceDefences.Taken(100, LessOnly));
        Assert.Equal(1.0, IncomingResourceDefences.FinalTakenFactor(pools, LessOnly), 6);
    }
}
