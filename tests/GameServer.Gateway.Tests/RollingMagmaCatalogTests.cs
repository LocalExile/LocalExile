namespace GameServer.Gateway.Tests;

public sealed class RollingMagmaCatalogTests
{
    [Fact]
    public void RollingMagmaFiresTheMagmaOrbAndBouncesOncePerChain()
    {
        // Rolling Magma (once Magma Orb) creates MagmaOrbShort, a row not named after the effect: without the fallback every
        // cast was refused (11-skills.md §10). Its chains are its ground bounces, 2 at level 1 and 3 at level 20.
        var skills = new SkillCatalog();
        var stats = new StatCatalog();
        int effect = skills.EffectRow("RollingMagma");
        Assert.True(effect >= 0 && skills.IsProjectile(effect));
        Assert.Equal("Metadata/Projectiles/MagmaOrbShort", skills.ProjectileFor(effect)?.Id);
        int Chains(int level) => skills.EffectStats(effect, level).FirstOrDefault(s => s.Item1 == stats.Key("number_of_chains")).Item2;
        Assert.Equal(2, Chains(1));
        Assert.Equal(3, Chains(20));
    }
}
