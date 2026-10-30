using GameServer.Core.Projectiles;

namespace GameServer.Gateway.Tests;

public partial class SpellNoDamageTests
{
    [Theory]
    [InlineData("BurningArrow", "Crude Bow")]
    [InlineData("Fireball", "Driftwood Wand")]
    public async Task Whisperer_Blind_and_main_Ignite_keep_independent_release_snapshots(string effect, string weapon)
    {
        using var f = new Fixture(effect, weapon);
        f.Conn.Clock.Start();
        f.Conn.Pools = f.Conn.Pools with { Accuracy = 500 };
        f.Set("global_always_hit", 1);
        f.Set("global_chance_to_blind_on_hit_%", 100);
        f.Set("blind_effect_+%", 50);
        f.Set("base_chance_to_ignite_%", 100);
        Assert.True(Program.InflictPlayerBlind(f.Conn, 60));
        await using var runtime = new ProjectileRuntimeFixture(f.Conn);
        ProjectileUse Capture()
        {
            var gem = ((IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>)
                Method("GemSkillEffects").Invoke(null, [f.Conn])!).Single(g => g.Id == f.Skill);
            return (ProjectileUse)Method("CaptureGemProjectile").Invoke(null,
                [f.Conn, runtime.Session, f.Skill, gem, (100, 32), new SkillCriticalRoll(0)])!;
        }
        var use = Capture();
        var shot = Assert.IsType<ProjectileOffense>(use.Payload);
        Assert.True(shot.ClientPredicted);
        Assert.Equal(400, shot.Accuracy);
        if (effect == "BurningArrow")
        {
            Assert.Equal(400, shot.AccuracyProfile!.Value.Rating);
            Assert.True(shot.AccuracyProfile.Value.CannotBeEvaded);
        }
        Assert.Equal((100, 50, 4d), shot.Blind);
        Assert.Equal(100, shot.Ignite.Chance);
        Assert.True(shot.Ignite.Min > 0);

        // The later release sees new sources; neither its cache nor the live caster rewrites the first shot.
        f.Set("global_chance_to_blind_on_hit_%", 0);
        f.Set("never_ignite", 1);
        f.Conn.Blind.Clear();
        f.Conn.Pools = f.Conn.Pools with { Accuracy = 2000 };
        var later = Assert.IsType<ProjectileOffense>(Capture().Payload);
        Assert.Equal(2000, later.Accuracy);
        Assert.Equal(0, later.Blind.Chance);
        Assert.Equal(MonsterIgnites.Settings.None, later.Ignite);

        runtime.Release(use with { Profile = use.Profile with { Speed = 100 } });
        runtime.Advance(1);
        Assert.True(f.Target.Life < f.Target.MaxLife);
        Assert.Equal(30, f.Target.Blind.Read(1).Less);
        Assert.True(f.Target.Ignites.Burning);
        Assert.NotNull(f.Target.Ignites.Buff);
        Assert.InRange(f.Target.Ignites.Until, 4, 5);
    }
}
