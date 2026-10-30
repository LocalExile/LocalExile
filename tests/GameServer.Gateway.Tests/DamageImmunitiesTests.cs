using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class DamageImmunitiesTests
{
    [Fact]
    public void Elemental_immunity_covers_fire_cold_and_lightning()
    {
        var immune = DamageImmunities.From(n => n == "elemental_damage_immunity" ? 1 : 0);
        Assert.Equal(3, immune.Count);
        Assert.Contains(DamageType.Fire, immune);
        Assert.Contains(DamageType.Cold, immune);
        Assert.Contains(DamageType.Lightning, immune);
        Assert.DoesNotContain(DamageType.Chaos, immune);
    }

}
