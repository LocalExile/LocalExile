using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class RecentEnemyHitGatewayTests
{

    [Theory]
    [InlineData("/hit chaos 100", 26, 1)]
    [InlineData("/spellhit chaos 100", 16, 2)]
    [InlineData("/blockhit spell chaos 100", 26, 1)]
    public async Task Parsed_chaos_contacts_keep_armour_Fortification_suppression_and_ES_bypass_order(
        string command, int damage, int armourAndSuppressionDraws)
    {
        using var f = new Fixture();
        f.Set(RecentEnemyHits.GenericChance, 100);
        Program.FortificationOf(f.Conn).State.GrantExplicitStacks(f.Conn.Clock.Elapsed.TotalSeconds, 20);
        _ = f.Chance;
        // The refresh above computes an uncapped input; override it with the forced chance too.
        f.Conn.Pools = f.Conn.Pools with { Life = 10000, Armour = 500, ArmourAppliesToChaosDamage = true,
            SpellSuppressionChance = 100, SpellSuppressionChanceUncapped = 100, EnergyShield = 100 };
        f.Conn.EnergyShield.SetMaximum(100, refill: true);
        var random = new TraceRandom(armourRoll: 0, blockRoll: .99);
        f.Conn.IncomingHitRandom = random;
        await f.Command(command);
        Assert.Equal(10000u - (uint)damage, f.Conn.PlayerLife);
        Assert.Equal(100u, f.Conn.EnergyShield.Current);
        Assert.Equal(1, f.Count);
        Assert.Equal(armourAndSuppressionDraws, random.Draws.Count(d => d == "next:100"));
        Assert.Equal(500, f.Conn.Pools.Armour);
        string reply = System.Text.Encoding.ASCII.GetString(await f.Read());
        Assert.Contains(command.StartsWith("/blockhit")
            ? "26; not blocked (0% chance); life 9974/10000." : "67% armour", reply);
    }

}
