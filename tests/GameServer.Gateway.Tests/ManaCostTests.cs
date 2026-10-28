namespace GameServer.Gateway.Tests;

// The mana an attack skill costs. The client spends it at the start of every use, including a use that only walks
// (attack-move), so the server must know it to give the pool back when it does not run a swing.
public class ManaCostTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
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

    [Fact]
    public void A_skill_without_a_mana_cost_needs_no_pool_update()
    {
        // The default attack (PlayerMelee) has no mana_cost stat: the client spends nothing, so nothing is given back.
        Assert.Equal(0u, Program.ManaCost(Stats, Skills, Skills.EffectRow("PlayerMelee"), 1, 0));
    }

    [Theory]
    [InlineData(4, 0, 0, 0, 4.0, false)]   // At the weapon's reach: a swing, which pays.
    [InlineData(0, 0, 0, 4, 4.0, false)]   // Exactly 4 cells away: still a swing.
    [InlineData(0, 0, 5, 0, 4.0, true)]    // One cell beyond: the client walks (attack-move), which costs nothing.
    [InlineData(0, 0, 40, 40, 4.0, true)]
    public void An_attack_gem_walks_only_beyond_the_weapons_reach(int ax, int ay, int bx, int by, double reach, bool walks)
    {
        // Live 2026-10-26: standing and swinging at empty ground spent no mana because every point was walked.
        Assert.Equal(walks, Program.GroundAttackWalks((ax, ay), (bx, by), reach));
    }
}
