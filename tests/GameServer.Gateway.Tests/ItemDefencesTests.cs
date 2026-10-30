namespace GameServer.Gateway.Tests;

// The local defences of an equipped armour piece/shield (ItemDefences, 13-items.md §2c): base ArmourTypes roll plus the
// item's local flat/increased armour, evasion and energy shield mods, and item quality.
public class ItemDefencesTests
{
    static Dictionary<string, int> Local(params (string Name, int Value)[] stats) =>
        stats.ToDictionary(s => s.Name, s => s.Value);

    static ItemCatalog.BaseDefences Base(int armour = 0, int evasion = 0, int energyShield = 0) =>
        new(armour, armour, evasion, evasion, energyShield, energyShield);

    [Fact]
    public void Armour_adds_its_local_flat_and_increase_and_quality()
    {
        var (armour, _, _) = ItemDefences.Of(Base(armour: 19),
            Local(("local_base_physical_damage_reduction_rating", 20), ("local_physical_damage_reduction_rating_+%", 50)), 20);

        // Native local defence rounds the complete (19 + 20) * 1.5 * 1.2 = 70.2 once, to 70.
        Assert.Equal(70, armour);
    }

}
