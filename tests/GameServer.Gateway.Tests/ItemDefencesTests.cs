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

    [Fact]
    public void The_combined_evasion_and_energy_shield_mod_adds_both()
    {
        var (_, evasion, energyShield) = ItemDefences.Of(Base(energyShield: 10),
            Local(("local_evasion_rating_and_energy_shield", 15), ("local_armour_and_evasion_+%", 20)), 0);

        Assert.Equal(18, evasion);        // 15 * 120% (armour-and-evasion applies to evasion)
        Assert.Equal(25, energyShield);   // 10 + 15 flat; the armour-and-evasion increase does not touch ES
    }

    [Fact]
    public void The_all_defences_increase_and_quality_scale_every_pool()
    {
        var (armour, evasion, energyShield) = ItemDefences.Of(Base(10, 10, 10),
            Local(("local_armour_and_evasion_and_energy_shield_+%", 50)), 10);

        Assert.Equal(17, armour);         // 10 * 1.5 * 1.1 = 16.5, native half-up final rounding.
        Assert.Equal(17, evasion);
        Assert.Equal(17, energyShield);
    }

    [Fact]
    public void Quality_alone_raises_energy_shield()
    {
        var (_, _, energyShield) = ItemDefences.Of(Base(energyShield: 20), Local(), 20);

        Assert.Equal(24, energyShield);
    }

    [Fact]
    public void Local_no_energy_shield_zeroes_the_shield_pool()
    {
        var (_, _, energyShield) = ItemDefences.Of(Base(energyShield: 50),
            Local(("local_no_energy_shield", 1)), 20);

        Assert.Equal(0, energyShield);
    }
}
