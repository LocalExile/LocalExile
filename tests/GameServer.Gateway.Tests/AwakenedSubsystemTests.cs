namespace GameServer.Gateway.Tests;

// Awakened support subsystems: Melee Splash's splash radius and damage multiplier (Multistrike's repeats are driven by
// the same skill stats through SupportedSkill).
public sealed class AwakenedSubsystemTests
{
    static Func<string, int> Get(params (string Name, int Value)[] stats) =>
        name => stats.Where(s => s.Name == name).Sum(s => s.Value);

    [Fact]
    public void Melee_splash_radius_and_damage_follow_its_stats()
    {
        Assert.Null(MeleeSplash.Settings(Get()));
        var plain = MeleeSplash.Settings(Get(("support_melee_splash_damage_+%_final_for_splash", -40)));
        Assert.NotNull(plain);
        Assert.Equal(12, plain!.Value.Radius);
        Assert.Equal(0.6, plain.Value.More, 9);
        var grown = MeleeSplash.Settings(Get(("support_melee_splash_damage_+%_final_for_splash", -40),
            ("melee_splash_area_of_effect_+%_final", 100)));
        Assert.Equal(16, grown!.Value.Radius);
    }
}
