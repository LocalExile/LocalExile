using GameServer.Core.Projectiles;

namespace GameServer.Gateway.Tests;

public sealed class ProjectileThresholdJewelTests
{
    [Fact]
    public void NamedCountsAndPierceUseTheSkillBudgetOnce()
    {
        var stats = new Dictionary<uint, int>
        {
            [Stat.FreezingPulseNumberOfAdditionalProjectiles] = 2,
            [Stat.FrostboltNumberOfAdditionalProjectiles] = 3,
            [Stat.NumberOfAdditionalProjectiles] = 1,
            [Stat.IceShotPiercePlus] = 4,
            [Stat.ProjectileBaseNumberOfTargetsToPierce] = 1
        };
        var skills = new SkillCatalog();
        ProjectileProfile Profile(string effect)
        {
            var definition = Game.ResolveProjectileDefinition(skills.EffectRow(effect));
            Assert.NotNull(definition);
            return definition.Parameters.Resolve(key => stats.GetValueOrDefault(key), 1000, effect == "IceShot");
        }

        Assert.Equal(4, Profile("FreezingPulse").Count);
        Assert.Equal(5, Profile("FrostBolt").Count);
        Assert.Equal(2, Profile("Fireball").Count);
        Assert.Equal(1, Profile("Fireball").Pierce);
        Assert.Equal(5, Profile("IceShot").Pierce);
        stats[Stat.AlwaysPierce] = 1;
        Assert.Equal(-1, Profile("IceShot").Pierce);
        stats.Remove(Stat.FreezingPulseNumberOfAdditionalProjectiles);
        Assert.Equal(2, Profile("FreezingPulse").Count);
    }

    [Fact]
    public void JewelNovaMatchesItsSkillAndRetainsTheNativeVolleyRules()
    {
        var stats = new Dictionary<uint, int> { [Stat.SparkProjectilesNova] = 1 };
        int Get(uint key) => stats.GetValueOrDefault(key);
        ProjectileProfile Volley(string effect)
        {
            var profile = GatewayProjectileSkillProfiles.Resolve(effect, "test", Get, 1000, attack: false);
            return Game.NativeVolley(profile, Get);
        }

        Assert.True(Volley("VaalSpark").NativeVolley!.Nova);
        Assert.False(Volley("EtherealKnives").NativeVolley!.Nova);
        stats.Clear();
        stats[Stat.EtherealKnivesProjectilesNova] = 1;
        Assert.False(Volley("Spark").NativeVolley!.Nova);
        var fan = Volley("EtherealKnivesAltX").NativeVolley!;
        Assert.True(fan.Nova);
        Assert.Equal(Math.PI, fan.Angle(2, 4, 10), 4);
        stats[Stat.SupportParallelProjectileNumberOfPointsPerSide] = 1;
        Assert.Null(Volley("EtherealKnivesAltX").NativeVolley);
    }
}
