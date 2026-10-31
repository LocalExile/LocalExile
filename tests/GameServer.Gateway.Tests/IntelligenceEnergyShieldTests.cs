using System.Text.Json;

namespace GameServer.Gateway.Tests;

// Calculator isolation with explicit completed attributes, not item acquisition or native fractional-rounding proof.
public sealed class IntelligenceEnergyShieldTests
{
    static SkillCatalog.ClassInfo Attributes(int intelligence, int strength = 0) =>
        SkillCatalog.ClassInfo.Witch with { Strength = strength, Dexterity = 0, Intelligence = intelligence };

    [Theory]
    [InlineData(0, 0, 100u)]
    [InlineData(4, 0, 100u)]
    [InlineData(5, 0, 100u)]
    [InlineData(9, 0, 100u)]
    [InlineData(10, 1, 101u)]
    [InlineData(19, 1, 101u)]
    [InlineData(20, 2, 102u)]
    public void Intelligence_ES_baseline_uses_one_percent_per_completed_ten(
        int intelligence, int expectedIncrease, uint expectedEnergyShield)
    {
        var pools = CharacterStats.For(Attributes(intelligence), 1, new(EnergyShield: 100));

        Assert.Equal(intelligence, pools.Intelligence);
        Assert.Equal(expectedIncrease, pools.EnergyShieldPct);
        Assert.Equal(expectedEnergyShield, pools.EnergyShield);
    }

    [Fact]
    public void Intelligence_ES_baseline_reads_completed_flat_and_percentage_attribute_bucket()
    {
        var pools = CharacterStats.For(Attributes(9), 1,
            new(Intelligence: 1, IntelligencePct: 75, AllAttributesPct: 25, EnergyShield: 100));

        // (Class9 + flat1) * (1 + .75 + .25) =20, then two complete Intelligence groups.
        Assert.Equal(20, pools.Intelligence);
        Assert.Equal(2, pools.EnergyShieldPct);
        Assert.Equal(102u, pools.EnergyShield);
    }

    [Fact]
    public void Intelligence_ES_baseline_and_Strength_item_term_join_ordinary_global_increase_once()
    {
        var source = new CharacterStats.Bonus(EnergyShield: 100, EnergyShieldPct: 25);
        var baseline = CharacterStats.For(Attributes(100, strength: 100), 1, source);
        var withStrengthSource = CharacterStats.For(Attributes(100, strength: 100), 1,
            source with { EnergyShieldPctPerTenStrength = 1 });

        Assert.Equal(35, baseline.EnergyShieldPct); //Int100 supplies10; the ordinary global source supplies25.
        Assert.Equal(135u, baseline.EnergyShield);
        Assert.Equal(45, withStrengthSource.EnergyShieldPct); //The separate Strength100 source supplies another10.
        Assert.Equal(145u, withStrengthSource.EnergyShield);
        Assert.Equal((baseline.Life, baseline.Mana, baseline.Accuracy, baseline.Armour, baseline.Evasion),
            (withStrengthSource.Life, withStrengthSource.Mana, withStrengthSource.Accuracy,
                withStrengthSource.Armour, withStrengthSource.Evasion));
    }

    [Fact]
    public void Intelligence_ES_slot_sources_keep_existing_single_final_half_up_projection()
    {
        var pools = CharacterStats.For(Attributes(19), 1,
            new(EnergyShield: 50, ShieldEnergyShield: 20, BodyEnergyShield: 20));

        Assert.Equal(1, pools.EnergyShieldPct);
        // Existing pinned-PoB final projection: independent10.1 + shield20.2 + body20.2 =50.5 ->51.
        // Rounding each source first would give50. The native nominal text does not prove fractional parity.
        Assert.Equal(51u, pools.EnergyShield);
    }

    [Fact]
    public void Intelligence_ES_native_nominal_description_declares_one_percent_per_ten_and_mana_per_two()
    {
        // TestEnvironment supplies the repository working directory; keep the native data path relative.
        using var strings = JsonDocument.Parse(File.ReadAllBytes("data/tables/ClientStrings.json"));
        var description = strings.RootElement[169];

        Assert.Equal("IntelligenceDescription", description.GetProperty("Id").GetString());
        Assert.Equal(1789395335u, description.GetProperty("HASH32").GetUInt32());
        Assert.Equal("2 Intelligence = +1 Mana\r\n10 Intelligence = +1% Energy Shield",
            description.GetProperty("Text").GetString());
    }
}
