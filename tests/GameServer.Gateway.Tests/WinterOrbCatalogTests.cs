namespace GameServer.Gateway.Tests;

public sealed class WinterOrbCatalogTests
{
    [Fact]
    public void WinterOrbFiresFrostFuryMortarsWithColdDamage()
    {
        // Winter Orb's channelled orb fires FrostFuryMortar, a row not named after the effect: without the fallback every
        // use was refused while the client played the channel, so the skill dealt no damage (11-skills.md §10).
        var skills = new SkillCatalog();
        var stats = new StatCatalog();
        int effect = skills.EffectRow("WinterOrb");
        Assert.True(effect >= 0 && skills.IsProjectile(effect) && skills.IsChannelled(effect));
        Assert.Equal("Metadata/Projectiles/FrostFuryMortar", skills.ProjectileFor(effect)?.Id);
        Assert.False(skills.HitDamage(effect, 1).IsZero);
        int Stat(int level, string name) => skills.EffectStats(effect, level)
            .FirstOrDefault(s => s.Item1 == stats.Key(name)).Item2;
        Assert.Equal(1200, Stat(1, "frost_fury_base_fire_interval_ms"));
        Assert.Equal(80, Stat(1, "frost_fury_fire_speed_+%_final_while_channelling"));
        Assert.Equal(2, Stat(1, "base_number_of_projectiles"));
    }
}
