using GameServer.Core.Projectiles;

namespace GameServer.Gateway.Tests;

public sealed class RandomVolleyTests
{
    // random_projectile_direction: a volley turns each projectile from the aim by its own 0–2π draw of one seeded
    // generator (0x141bb9280); a lone projectile ignores the aim and flies along (sin a, cos a) (0x141bb13a0).
    [Fact]
    public void A_volley_turns_from_the_aim_and_a_lone_projectile_ignores_it()
    {
        var volley = Game.RandomVolleyAngles(0x2a, 5, 0x1000, new(10, 0));
        Assert.Equal(volley, Game.RandomVolleyAngles(0x2a, 5, 0x1000, new(0, -3)));
        Assert.All(volley, a => Assert.InRange(a, 0, 6.2831855));
        Assert.Equal(5, volley.Distinct().Count());
        Assert.NotEqual(volley, Game.RandomVolleyAngles(0x2b, 5, 0x1000, new(10, 0)));

        ProjectileVector Flight(ProjectileVector aim) =>
            aim.Unit.Rotate(Game.RandomVolleyAngles(0x2a, 1, 0x1000, aim)[0]);
        var east = Flight(new(10, 0));
        var north = Flight(new(0, -3));
        Assert.Equal(east.X, north.X, 6);
        Assert.Equal(east.Y, north.Y, 6);
        float a = new AnimationVariantSelector.NativeRandom([0x2a, 2, 0, 0, 0x1000]).Range(0, 6.2831855f);
        Assert.Equal(MathF.Sin(a), east.X, 6);
        Assert.Equal(MathF.Cos(a), east.Y, 6);
    }
}
