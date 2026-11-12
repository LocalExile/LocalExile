using GameServer.Core.Projectiles;

namespace GameServer.Gateway.Tests;

public sealed class ProjectileThresholdJewelTests
{
    [Fact]
    public void NamedCountsAndPierceUseTheSkillBudgetOnce()
    {
        var stats = new Dictionary<string, int>
        {
            ["freezing_pulse_number_of_additional_projectiles"] = 2,
            ["frostbolt_number_of_additional_projectiles"] = 3,
            ["number_of_additional_projectiles"] = 1,
            ["ice_shot_pierce_+"] = 4,
            ["projectile_base_number_of_targets_to_pierce"] = 1
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
        stats["always_pierce"] = 1;
        Assert.Equal(-1, Profile("IceShot").Pierce);
        stats.Remove("freezing_pulse_number_of_additional_projectiles");
        Assert.Equal(2, Profile("FreezingPulse").Count);
    }

    [Fact]
    public void JewelNovaMatchesItsSkillAndRetainsTheNativeVolleyRules()
    {
        var stats = new Dictionary<string, int> { ["spark_projectiles_nova"] = 1 };
        int Get(string key) => stats.GetValueOrDefault(key);
        ProjectileProfile Volley(string effect)
        {
            var profile = GatewayProjectileSkillProfiles.Resolve(effect, "test", Get, 1000, attack: false);
            return Game.NativeVolley(profile, Get);
        }

        Assert.True(Volley("VaalSpark").NativeVolley!.Nova);
        Assert.False(Volley("EtherealKnives").NativeVolley!.Nova);
        stats.Clear();
        stats["ethereal_knives_projectiles_nova"] = 1;
        Assert.False(Volley("Spark").NativeVolley!.Nova);
        var fan = Volley("EtherealKnivesAltX").NativeVolley!;
        Assert.True(fan.Nova);
        Assert.Equal(Math.PI, fan.Angle(2, 4, 10), 4);
        stats["support_parallel_projectile_number_of_points_per_side"] = 1;
        Assert.Null(Volley("EtherealKnivesAltX").NativeVolley);
    }
}
