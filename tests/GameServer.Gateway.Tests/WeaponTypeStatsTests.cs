namespace GameServer.Gateway.Tests;

public sealed class WeaponTypeStatsTests
{
    [Fact]
    public void OnlyTwoWeaponsOrUnarmedHandsWithTheStatDualWield()
    {
        // The hands' native values (13-items.md "Client weapon-type stats"); no item is an empty hand.
        int sceptre = WeaponTypeStats.Of("Sceptre"), shield = WeaponTypeStats.Of("Shield"), claw = WeaponTypeStats.Of("Claw");
        int empty = WeaponTypeStats.Of(null);
        Assert.Equal(9, WeaponTypeStats.Of("Bow"));
        Assert.Equal(15, WeaponTypeStats.Of("FishingRod"));
        Assert.Equal(13, shield);
        Assert.Equal(14, empty);
        Assert.Equal(empty, WeaponTypeStats.Of("Quiver"));

        // As the client's calculator 0x142521190: a sceptre and a shield do not dual wield, so Shield Charge, which only
        // the off hand fits, stays usable (11-skills.md §10a); two weapons do, an empty off hand does not, and two empty
        // hands only with unarmed_counts_as_dual_wielding.
        Assert.False(WeaponTypeStats.IsDualWielding(sceptre, shield, false));
        Assert.True(WeaponTypeStats.IsDualWielding(sceptre, claw, false));
        Assert.False(WeaponTypeStats.IsDualWielding(WeaponTypeStats.Of("Two Hand Axe"), empty, false));
        Assert.False(WeaponTypeStats.IsDualWielding(empty, empty, false));
        Assert.True(WeaponTypeStats.IsDualWielding(empty, empty, true));
        Assert.False(WeaponTypeStats.IsDualWielding(empty, shield, true));
    }
}
