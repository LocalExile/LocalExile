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
}
