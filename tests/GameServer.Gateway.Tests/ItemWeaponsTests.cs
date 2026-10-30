using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// A weapon's attack from its base row and the item's local stats (ItemWeapons, 13-items.md §2c).
public class ItemWeaponsTests
{
    static Dictionary<string, int> Local(params (string Name, int Value)[] stats) =>
        stats.ToDictionary(s => s.Name, s => s.Value);

    [Fact]
    public void Physical_damage_adds_local_flat_and_increase_and_quality()
    {
        var weapon = ItemWeapons.Of(1000, 10, 20, 8,
            Local(("local_minimum_added_physical_damage", 5), ("local_maximum_added_physical_damage", 10),
                ("local_physical_damage_+%", 50)), 20);

        // (10 + 5) * 150% * 20% quality = 27; (20 + 10) * 150% * 20% = 54. Quality is a separate factor since 3.25.
        Assert.Equal(27, weapon.MinDamage);
        Assert.Equal(54, weapon.MaxDamage);

        // The post-3.25 rule directly: base 100, 100% increased physical and 20% quality -> 100 * 2 * 1.2 = 240.
        var reviewed = ItemWeapons.Of(1000, 100, 100, 8, Local(("local_physical_damage_+%", 100)), 20);
        Assert.Equal(240, reviewed.MinDamage);
        Assert.Equal(240, reviewed.MaxDamage);
    }

}
