namespace GameServer.Gateway.Tests;

// The mana an attack skill costs. The client spends it at the start of every use, including a use that only walks
// (attack-move), so the server must know it to give the pool back when it does not run a swing.
public class ManaCostTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static readonly SkillCatalog Skills = new(Data());
    static readonly StatCatalog Stats = new(Data());

    [Fact]
    public void Holy_strike_costs_the_six_mana_the_client_spends_per_use()
    {
        // Live 3.29.3b probe 2026-10-26: the client's mana fell by 6 on each attack-move with Holy Strike.
        Assert.Equal(6u, Program.ManaCost(Stats, Skills, Skills.EffectRow("HolyStrike"), 1, 0));
    }

}
