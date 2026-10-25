using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class PassiveAttributeModifiersTests
{
    [Fact]
    public void Increased_attributes_apply_after_the_class_and_all_flat_sources_and_reach_pools()
    {
        var cls = SkillCatalog.ClassInfo.Witch;
        var pools = CharacterStats.For(cls, 1, new CharacterStats.Bonus(
            Strength: 11, Dexterity: 12, Intelligence: 13, StrengthPct: 40, DexterityPct: 40, IntelligencePct: 40));

        Assert.Equal((35, 36, 63), (pools.Strength, pools.Dexterity, pools.Intelligence));
        Assert.Equal((uint)(cls.BaseMaxLife + 12 + 35 / 2), pools.Life);
        Assert.Equal((uint)(cls.BaseMaxMana + 6 + 63 / 2), pools.Mana);
        Assert.Equal(2 + 36 * 2, pools.Accuracy);
        Assert.Equal(36 / 5, pools.EvasionPct);
        Assert.Equal(63 / 5, pools.EnergyShieldPct);
        Assert.Equal(35 / 5, pools.MeleePhysicalDamagePct);
    }

    [Fact]
    public void Increased_all_attributes_adds_to_each_attribute_before_rounding()
    {
        int Get(string name) => name switch { "all_attributes_+%" => 10, "strength_+%" => 40, _ => 0 };
        Assert.Equal((22, 16, 16), PassiveAttributeModifiers.Percentages(Get).Apply((15, 15, 15)));
    }

    [Fact]
    public void No_Intelligence_overrides_flat_and_increased_attributes_and_their_pool_bonuses()
    {
        var cls = SkillCatalog.ClassInfo.Witch;
        var pools = CharacterStats.For(cls, 1, new CharacterStats.Bonus(Intelligence: 100, IntelligencePct: 40, NoIntelligence: true));
        Assert.Equal(0, pools.Intelligence);
        Assert.Equal(0, pools.EnergyShieldPct);
        Assert.Equal((uint)(cls.BaseMaxMana + 6), pools.Mana);
        int Get(string name) => name == "intelligence_is_0" ? 1 : 0;
        Assert.Equal((50, 60, 0), PassiveAttributeModifiers.Percentages(Get).Apply((50, 60, 100)));
    }

    [Fact]
    public void Losing_Intelligences_mana_bonus_preserves_its_other_benefits()
    {
        var cls = SkillCatalog.ClassInfo.Witch;
        var pools = CharacterStats.For(cls, 1, new CharacterStats.Bonus(NoIntelligenceMana: true, ManaPerDexterity: 1));
        Assert.Equal(cls.Intelligence, pools.Intelligence);
        Assert.Equal(cls.Intelligence / 5, pools.EnergyShieldPct);
        Assert.Equal((uint)(cls.BaseMaxMana + 6 + cls.Dexterity), pools.Mana);
    }

    [Fact]
    public void Life_per_level_modifiers_use_the_whole_character_level()
    {
        var cls = SkillCatalog.ClassInfo.Witch;
        var ordinary = CharacterStats.For(cls, 20);
        var shaman = CharacterStats.For(cls, 20, new CharacterStats.Bonus(LifePerLevel: -3));
        Assert.Equal(ordinary.Life - 60, shaman.Life);
    }

    [Fact]
    public void Power_of_Purpose_converts_mana_to_armour_with_mana_and_armour_increases_counted_once()
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1, new CharacterStats.Bonus(
            ManaPerFourStrength: 8, ManaConvertedToArmourPct: 80, ManaPct: 100, ArmourPct: 100));
        // Flat mana = 34 + 6 + 32/2 + floor(14/4)*8 = 80. Final mana = 80*2*0.2 = 32.
        // Converted armour = 80*0.8*2*(1+100%+100%) = 384, rather than 80*0.8*2*2*2.
        Assert.Equal(32u, pools.Mana);
        Assert.Equal(384, pools.Armour);
    }

    [Theory]
    [InlineData(155, -25, 116)]
    [InlineData(155, 50, 232)]
    [InlineData(155, 25, 193)]
    [InlineData(155, -100, 0)]
    [InlineData(155, -150, 0)]
    public void Attribute_requirement_modifiers_round_down_and_never_become_negative(int required, int percent, int expected) =>
        Assert.Equal(expected, PassiveAttributeModifiers.Requirement(required, percent));

    [Fact]
    public void Ignoring_requirements_wins_over_an_increase() =>
        Assert.Equal(0, PassiveAttributeModifiers.Requirement(155, name => name switch
        { "global_no_attribute_requirements" => 1, "global_item_attribute_requirements_+%" => 50, _ => 0 }));

    [Theory]
    [InlineData(50, 61, 73, 80)]
    [InlineData(49, 61, 73, 72)]
    [InlineData(500, 0, 300, 0)]
    public void Dreadful_Attitude_counts_complete_groups_of_the_lowest_attribute(int str, int dex, int intel, int expected) =>
        Assert.Equal(expected, PassiveAttributeModifiers.DamageIncrease(
            name => name == "damage_+%_per_5_of_your_lowest_attribute" ? 8 : 0, str, dex, intel));

    [Fact]
    public void Equal_attributes_do_not_satisfy_the_conditional_immunities()
    {
        int Get(string name) => 1;
        Assert.Equal(new(false, false, false), PassiveAttributeModifiers.AilmentImmunities(Get, 100, 100, 100));
        Assert.Equal(new(true, true, false), PassiveAttributeModifiers.AilmentImmunities(Get, 120, 110, 100));
        Assert.Equal(new(false, false, true), PassiveAttributeModifiers.AilmentImmunities(Get, 100, 110, 120));
        Assert.Equal(new(false, false, false), PassiveAttributeModifiers.AilmentImmunities(_ => 0, 120, 110, 100));
    }

    [Fact]
    public void Equipment_checks_the_final_increased_attributes_before_disabling_an_item()
    {
        var item = new ItemModStats.Worn(1, new Item(1, ["Base"], 0), 70, 0, 0, 1);
        var mods = new ModCatalog(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        // 50 * 1.4 = 70. It must not be rejected by a premature comparison with flat strength 50.
        var result = ItemModStats.ResolveEquipment([item], (50, 0, 0), 1, mods, previous: new HashSet<uint> { 1 },
            passiveStat: name => name == "strength_+%" ? 40 : 0);
        Assert.Empty(result.Inactive);
        Assert.Single(result.Active);
    }

    [Fact]
    public void Reduced_attribute_requirements_do_not_remove_the_level_requirement()
    {
        var item = new ItemModStats.Worn(1, new Item(1, ["Base"], 0), 80, 0, 0, 2);
        var mods = new ModCatalog(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        int Get(string name) => name == "global_item_attribute_requirements_+%" ? -25 : 0;
        Assert.Empty(ItemModStats.ResolveEquipment([item], (60, 0, 0), 2, mods, passiveStat: Get).Inactive);
        Assert.Contains(1u, ItemModStats.ResolveEquipment([item], (60, 0, 0), 1, mods, passiveStat: Get).Inactive);
    }

    [Fact]
    public void Removing_an_attribute_source_recalculates_percentages_before_disabling_dependants()
    {
        var givesStrength = new ItemModStats.Worn(1, new Item(1, ["Base"], 0), 0, 0, 100, 1);
        var dependant = new ItemModStats.Worn(2, new Item(2, ["Base"], 0), 80, 0, 0, 1);
        var inactive = ItemModStats.Inactive([givesStrength, dependant], (50, 0, 0), 1,
            item => item.TypeHash == 1 ? (10, 0, 0) : (0, 0, 0),
            finalAttributes: (flat, active) => new PassiveAttributeModifiers.Increases(Strength: 40).Apply(flat));

        // The first item fails its Intelligence requirement; strength drops from (50+10)*1.4=84 to 50*1.4=70.
        Assert.Equal([1u, 2u], inactive.Order());
    }

}
