using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>Shared merge seams: percentage attributes and CI must retain slot-specific native defence rules.</summary>
public sealed class MergedDefenceCompositionTests
{
    [Fact]
    public void Percentage_attributes_CI_chaos_armour_and_slot_defences_compose()
    {
        var bonus = new CharacterStats.Bonus(Armour: 200, Evasion: 185, EnergyShield: 100,
            ArmourPct: 20, EvasionPct: 20, EnergyShieldPct: 50,
            ShieldArmour: 80, ShieldEvasion: 100, ShieldEnergyShield: 60, SharedShieldDefencesPct: 50,
            BodyArmour: 120, BodyEvasion: 85, BodyEnergyShield: 40, BodyEnergyShieldPct: 25,
            DoubleBodyArmour: true, ChaosInoculation: true, ArmourAppliesToChaosDamage: true,
            SpellSuppressionChance: 90, SpellSuppressionWhileFullEnergyShield: 25,
            Attributes: new(6, 6, 8, 50, 25, 50));

        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100, bonus);

        Assert.Equal((30, 25, 60), (pools.Strength, pools.Dexterity, pools.Intelligence));
        Assert.Equal(1u, pools.Life); // CI wins over the maximum Life supplied by resolved Strength.
        Assert.Equal(664u, pools.Mana);
        Assert.Equal(424, pools.Armour); // Body 120 * 1.2 * 2 + shield 80 * 1.7.
        Assert.Equal(300, pools.Evasion); // Base/body 100 * 1.25 + shield 100 * 1.75.
        Assert.Equal(196u, pools.EnergyShield); // Int60 grants6%: body40*1.81 + shield60*2.06.
        Assert.True(pools.ArmourAppliesToChaosDamage);
        Assert.Equal(90, CharacterStats.SuppressionChance(pools, false, false, false));
        Assert.Equal(100, CharacterStats.SuppressionChance(pools, false, false, false, true));
    }

    [Fact]
    public void Iron_Reflexes_keeps_body_and_shield_operators_without_scaled_Dexterity()
    {
        var bonus = new CharacterStats.Bonus(Armour: 200, Evasion: 185,
            ArmourPct: 20, EvasionPct: 20, ArmourMorePct: 50, EvasionMorePct: 25,
            ShieldArmour: 80, ShieldEvasion: 100, SharedShieldDefencesPct: 50,
            BodyArmour: 120, BodyEvasion: 85, DoubleBodyArmour: true, IronReflexes: true,
            Attributes: new(Dexterity: int.MaxValue, DexterityPct: int.MaxValue));

        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100, bonus);

        Assert.Equal(int.MaxValue, pools.Dexterity);
        // Native armour is 424 * 1.5; converted evasion is (15*1.4 + 85*1.4*2 + 100*1.9) * 1.25 * 1.5.
        // Sum the two unrounded contributions, then round 1477.875 once.
        Assert.Equal(1478, pools.Armour);
        Assert.Equal(0, pools.Evasion);
        Assert.Equal(20, pools.EvasionPct);
    }

    [Theory]
    [InlineData("local_no_attribute_requirements")]
    [InlineData("local_unique_tabula_rasa_no_requirement_or_energy_shield")]
    public void Callback_qualification_keeps_hidden_attribute_exemptions_and_level_gate(string stat)
    {
        var mods = Program.Items.Mods;
        var native = mods.All.First(m => m.Stats.Any(s => s.Id == stat));
        var item = ItemCatalog.Create(Program.Items.Find("Simple Robe")!) with
        {
            Mods = [new(ModSection.Hidden, native.Hash, native.Stats.Select(s => s.Id == stat ? 1 : 0).ToArray())],
        };
        var worn = new[] { new ItemModStats.Worn(1, item, 999, 999, 999, 60) };
        // Incoming percentage attributes can resolve to zero. The local hidden rule removes attribute gates only.
        var attributes = new AttributeModifiers(StrengthPct: -100, DexterityPct: -100, IntelligencePct: -100);
        var active = ItemModStats.ResolveEquipment(worn, 100, _ => attributes.Apply(14, 14, 32), mods: mods);
        Assert.Empty(active.Inactive);
        Assert.Single(active.Active);

        var belowLevel = ItemModStats.ResolveEquipment(worn, 59, _ => attributes.Apply(14, 14, 32), mods: mods);
        Assert.Contains(1u, belowLevel.Inactive);
        Assert.Empty(belowLevel.Active);
    }
}
