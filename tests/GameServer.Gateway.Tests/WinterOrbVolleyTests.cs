namespace GameServer.Gateway.Tests;

public sealed class WinterOrbVolleyTests
{
    [Fact]
    public void AdditionalProjectilesRaiseTheVolleyCountThroughTheGeneralPath()
    {
        // The volley counted only base_number_of_projectiles, so Greater Multiple Projectiles never lengthened the line.
        int Count(int extra, bool gated = false, bool single = false) => Game.WinterOrbVolleyCount(key =>
            key == Stat.BaseNumberOfProjectiles ? 2
            : key == Stat.NumberOfAdditionalProjectiles ? extra
            : key == Stat.SpellsNumberOfAdditionalProjectiles ? 1
            : key == Stat.ModifiersToProjectileCountDoNotApply && gated ? 1
            : key == Stat.MaximumNumberOfProjectilesToFireIs1 && single ? 1
            : 0);

        Assert.Equal(3, Count(0));
        Assert.Equal(5, Count(2));
        Assert.Equal(2, Count(2, gated: true));
        Assert.Equal(1, Count(2, single: true));
        Assert.Equal(2, Game.WinterOrbVolleyCount(key => key == Stat.BaseNumberOfProjectiles ? 2 : 0));
    }

    [Fact]
    public void TheVolleyStaysOnALineAcrossTheAim()
    {
        // Shooter at the origin aiming east: the line is vertical through the anchor, centred on it, 10 cells apart.
        var landings = Game.WinterOrbLandings((0, 0), (100, 0), 5, 10);

        Assert.Equal(5, landings.Count);
        Assert.All(landings, cell => Assert.Equal(100, cell.X));
        Assert.Equal([-20, -10, 0, 10, 20], landings.Select(cell => cell.Y));
    }
}
