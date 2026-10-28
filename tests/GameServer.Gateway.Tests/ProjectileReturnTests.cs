using GameServer.Core.Projectiles;

namespace GameServer.Gateway.Tests;

public class ProjectileReturnTests
{
    static Func<string, int> Stats(params (string Name, int Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Generic_return_stats_obey_attack_scope_and_cannot_return(bool attack)
    {
        ProjectileProfile Resolve(params (string Name, int Value)[] values) =>
            GatewayProjectileModifiers.Resolve(new("generic"), Stats(values), attack);
        Assert.True(Resolve(("projectiles_return", 1)).Returns);
        Assert.Equal(attack, Resolve(("attack_projectiles_return", 1)).Returns);
        Assert.True(Resolve(("projectile_return_%_chance", 100)).Returns);
        Assert.False(Resolve(("projectile_return_%_chance", 50)).Returns);
        Assert.True(Resolve(("projectiles_return", 1), ("skill_projectiles_cannot_return", 1)).CannotReturn);
    }

    [Fact]
    public void The_support_less_damage_and_the_returning_finals_multiply()
    {
        Assert.Equal(.3, ProjectileReturn.DamageMore(Stats(("support_return_damage_+%_final_from_returning_projectile", -70))), 6);
        Assert.Equal(.3, ProjectileReturn.DamageMore(Stats(("support_return_returning_projectiles_damage_+%_final", -70))), 6);
        Assert.Equal(.9, ProjectileReturn.DamageMore(Stats(
            ("support_return_returning_projectiles_damage_+%_final", -70),
            ("damage_+%_final_from_returning_projectile", 200))), 6);
        Assert.Equal(1, ProjectileReturn.DamageMore(Stats()));
    }
}
