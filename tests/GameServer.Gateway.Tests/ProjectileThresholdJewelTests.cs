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
        foreach (var (effect, expectedSpeed) in new[] { ("BallLightning", 48.944), ("BallLightningAltX", 130.191) })
        {
            int row = skills.EffectRow(effect);
            var ball = Game.ResolveProjectileDefinition(row)!;
            Assert.Equal("Metadata/Projectiles/BallLightningPlayer", ball.Metadata);
            var gem = skills.EffectStats(row, 1, 20).ToDictionary(s => Program.Stats.Name(s.Item1)!, s => s.Item2);
            int BallStat(string name) => gem.GetValueOrDefault(name);
            int baseSpeed = skills.Projectile(ball.Metadata)!.Value.Speed;
            int increased = ProjectileSpeed.EffectivePercent(0, BallStat);
            float speed = ProjectileSpeed.SkillCellsPerSecond(effect, baseSpeed, increased, BallStat);
            var orb = ball.Parameters.Resolve(BallStat, speed, false);
            Assert.Equal(expectedSpeed, orb.Speed, 3);
            Assert.Equal(42, ProjectileSpeed.SkillCellsPerSecond(effect, baseSpeed, increased, BallStat, "42"));
            Assert.False(orb.DirectDamage);
            Assert.Equal(22, orb.Pulse!.Radius);
            Assert.Equal(ProjectileRuntime.Time(.15), orb.Pulse.Interval);
            Assert.Equal(ProjectileRuntime.Time(2), orb.Lifetime);
            if (effect == "BallLightningAltX")
            {
                var legs = orb.Legs!(0);
                var leg = legs.First(new(100, 100), new(147, 100));
                Assert.Equal(new ProjectileVector(101, 99), leg.Target);
                while (legs.NextTurn(leg.Target, false) is { } turn) leg = turn.Leg;
                Assert.Equal(new ProjectileVector(100, 52), leg.Target);
            }
            else Assert.Null(orb.Legs);
        }
        stats["always_pierce"] = 1;
        Assert.Equal(-1, Profile("IceShot").Pierce);
        stats.Remove("freezing_pulse_number_of_additional_projectiles");
        Assert.Equal(2, Profile("FreezingPulse").Count);

        var uniques = new UniqueCatalog(Program.Items);
        foreach (var (jewel, attribute) in new[] { ("Volley Fire", "base_dexterity"),
            ("Frozen Trail", "base_intelligence"), ("Rapid Expansion", "base_strength") })
        {
            var item = Assert.IsType<GameServer.Core.Protocol.Instance.Item>(uniques.Create(jewel, new Random(2)));
            var (radius, effects) = ThresholdJewels.Read(item, Program.Items.Mods);
            Assert.Equal(1200, radius);
            Assert.NotEmpty(effects);
            uint key = Program.Stats.Key(attribute)!.Value;
            foreach (var effect in effects)
            {
                Assert.False(ThresholdJewels.Met(effect, new Dictionary<uint, int> { [key] = 39 }));
                Assert.True(ThresholdJewels.Met(effect, new Dictionary<uint, int> { [key] = 40 }));
            }
        }
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
        var parallel = Volley("EtherealKnivesAltX").NativeVolley!;
        Assert.NotNull(parallel.Layout);
        Assert.False(parallel.Nova);
    }
}
