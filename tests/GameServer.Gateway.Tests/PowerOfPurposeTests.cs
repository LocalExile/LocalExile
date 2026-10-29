using System.Numerics;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class PowerOfPurposeTests
{
    [Theory]
    [InlineData(3, 0, 15, 33, 24u, 412)]
    [InlineData(4, 0, 16, 33, 26u, 435)]
    [InlineData(4, 10, 16, 36, 26u, 440)]
    [InlineData(4, -80, 16, 7, 22u, 397)]
    public void Scaled_attributes_feed_Strength_groups_and_unrounded_Intelligence_before_conversion(
        int strengthPct, int intelligencePct, int strength, int intelligence, uint mana, int armour)
    {
        var bonus = new CharacterStats.Bonus(Mana: 9, ManaPct: 33, ManaPerFourStrength: 8,
            Armour: 100, ArmourPct: 50, MentalConditioning: true,
            Attributes: new(Strength: 1, Intelligence: 1, StrengthPct: strengthPct, IntelligencePct: intelligencePct));
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1, bonus);
        Assert.Equal((strength, intelligence), (pools.Strength, pools.Intelligence));
        // At 16 Str / 33 Int: B = 34 + 6 + 9 + 16.5 + 32; armour = 150 + B * 1.6 * 1.83.
        Assert.Equal(mana, pools.Mana);
        Assert.Equal(armour, pools.Armour);
        Assert.Equal(pools, CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1, bonus));
        var ordinary = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1, bonus with { MentalConditioning = false });
        Assert.Equal((strength, intelligence), (ordinary.Strength, ordinary.Intelligence));
        Assert.Equal(150, ordinary.Armour);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Saturated_scaled_attributes_preserve_main_pool_and_rating_bounds_with_conversion_on_or_off(
        bool ironReflexes)
    {
        var bonus = new CharacterStats.Bonus(LifePct: 100, Mana: int.MaxValue, ManaPct: int.MaxValue,
            ManaPerFourStrength: int.MaxValue, Armour: int.MaxValue, ArmourPct: 100,
            Evasion: int.MaxValue, EvasionPct: int.MaxValue, ColdOvercapEvasionPct: int.MaxValue,
            IronReflexes: ironReflexes, MentalConditioning: true,
            Attributes: new(int.MaxValue, int.MaxValue, int.MaxValue, 100, 100, 100));
        foreach (bool enabled in new[] { true, false })
        {
            var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100,
                bonus with { MentalConditioning = enabled });
            Assert.Equal((int.MaxValue, int.MaxValue, int.MaxValue),
                (pools.Strength, pools.Dexterity, pools.Intelligence));
            Assert.Equal((uint)int.MaxValue, pools.Life);
            Assert.Equal((uint)int.MaxValue, pools.Mana);
            Assert.Equal(int.MaxValue, pools.Armour);
            Assert.Equal(ironReflexes ? 0 : int.MaxValue, pools.Evasion);
            Assert.Equal(int.MaxValue, pools.EvasionPct);
        }
    }

    [Theory]
    [InlineData(3, 25u, 431)]
    [InlineData(4, 27u, 454)]
    [InlineData(7, 27u, 454)]
    [InlineData(8, 29u, 476)]
    public void Final_Strength_groups_join_the_flat_bucket_before_mana_and_armour_increases_once(
        int strength, uint mana, int armour)
    {
        var c = SkillCatalog.ClassInfo.Witch with { Strength = 3, Intelligence = 33 };
        // B = 100.5 + floor(final Strength / 4)*8. Converted armour uses 25+50, ordinary uses only 50.
        var bonus = new CharacterStats.Bonus(Strength: strength - 3, Mana: 44, ManaPct: 25,
            ManaPerFourStrength: 8, Armour: 100, ArmourPct: 50, MentalConditioning: true);
        var pools = CharacterStats.For(c, 1, bonus);
        Assert.Equal(mana, pools.Mana);
        Assert.Equal(armour, pools.Armour);
        Assert.Equal(strength, pools.Strength);
        Assert.Equal(pools, CharacterStats.For(c, 1, bonus));
    }

    [Fact]
    public void Flat_bucket_adds_mana_and_armour_increases_before_applying_existing_armour_more()
    {
        var contribution = PowerOfPurpose.ConvertFlatMana(200, 25, 50);
        Assert.Equal(25 * (BigInteger)PowerOfPurpose.Scale, contribution.Mana);
        Assert.Equal(280 * (BigInteger)PowerOfPurpose.Scale, contribution.Armour);
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1,
            new(Mana: 44, ManaPct: 25, ArmourPct: 50, MentalConditioning: true));
        Assert.Equal(25u, pools.Mana);
        Assert.Equal(280, pools.Armour);
    }

    [Theory]
    [InlineData(false, 516, 150)]
    [InlineData(true, 734, 0)]
    public void Ordinary_armour_Iron_Reflexes_shared_increases_and_more_each_apply_once(
        bool ironReflexes, int armour, int evasion)
    {
        // Armour 50 and evasion 30 already include the shared 20. Dexterity only increases unconverted evasion.
        // Ordinary 180 + mana conversion 336 + (IR only) evasion conversion 217.8; round the sum once.
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1,
            new(Mana: 44, ManaPct: 25, Armour: 100, ArmourPct: 50, Evasion: 85, EvasionPct: 30,
                SharedDefencesPct: 20, ColdOvercapEvasionPct: 5, ArmourMorePct: 20, EvasionMorePct: 10,
                IronReflexes: ironReflexes, MentalConditioning: true));
        Assert.Equal(25u, pools.Mana);
        Assert.Equal(armour, pools.Armour);
        Assert.Equal(evasion, pools.Evasion);
    }

    [Fact]
    public void Active_rounding_keeps_half_Intelligence_and_combines_unrounded_signed_armour()
    {
        var c = SkillCatalog.ClassInfo.Witch with { Intelligence = 33 };
        var bonus = new CharacterStats.Bonus(Mana: 46, ManaPct: 0, Armour: 1, ArmourPct: 50);
        var ordinary = CharacterStats.For(c, 1, bonus);
        Assert.Equal(102u, ordinary.Mana); // Existing path truncates Int/2 and ordinary armour.
        Assert.Equal(1, ordinary.Armour);
        var active = CharacterStats.For(c, 1, bonus with { MentalConditioning = true });
        Assert.Equal(21u, active.Mana); // 102.5 * .2 = 20.5, nearest with positive ties up.
        Assert.Equal(248, active.Armour); // 102.5 * 1.6 * 1.5 + 1.5 = 247.5.
        Assert.Equal(0, PowerOfPurpose.RoundNative(-PowerOfPurpose.Scale));
    }

    [Fact]
    public void Ordinary_outputs_and_native_pool_bytes_keep_the_existing_rounding()
    {
        var c = SkillCatalog.ClassInfo.Witch with { Intelligence = 33 };
        var pools = CharacterStats.For(c, 1,
            new(Mana: 44, ManaPct: 25, Armour: 1, ArmourPct: 50, Evasion: 85, EvasionPct: 30,
                SharedDefencesPct: 20, ArmourMorePct: 20, EvasionMorePct: 10, IronReflexes: true));
        Assert.Equal(125u, pools.Mana);
        Assert.Equal(212, pools.Armour); // trunc(1.8) + trunc(100 * 1.60 * 1.10 * 1.20).
        Assert.Equal(0, pools.Evasion);
        Assert.Equal(InstanceProtocol.LifeUpdate(Program.PlayerId, 125, 13, pool: 1),
            InstanceProtocol.LifeUpdate(Program.PlayerId, pools.Mana, 13, pool: 1));
        Assert.Equal(InstanceEntities.StatsUpdate(Program.PlayerId,
            [(Program.Stats.Key("maximum_mana")!.Value, 125),
                (Program.Stats.Key("physical_damage_reduction_rating")!.Value, 212)]),
            InstanceEntities.StatsUpdate(Program.PlayerId,
                [(Program.Stats.Key("maximum_mana")!.Value, (int)pools.Mana),
                    (Program.Stats.Key("physical_damage_reduction_rating")!.Value, pools.Armour)]));
    }

    [Theory]
    [InlineData(int.MaxValue, int.MaxValue)]
    [InlineData(int.MinValue, 0)]
    public void Huge_flat_mana_clamps_only_the_final_native_values(int flat, int expected)
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1,
            new(Mana: flat, ManaPct: int.MaxValue, ArmourPct: int.MaxValue, ArmourMorePct: int.MaxValue,
                MentalConditioning: true));
        Assert.Equal((uint)expected, pools.Mana);
        Assert.Equal(expected, pools.Armour);
    }

    [Fact]
    public void Signed_contributions_cancel_before_saturation_and_large_Iron_Reflexes_avoids_decimal_overflow()
    {
        var c = SkillCatalog.ClassInfo.Witch with { BaseMaxMana = 0, Intelligence = 0 };
        var bonus = new CharacterStats.Bonus(Mana: -1, ManaPct: 0, Armour: -8, ArmourPct: int.MaxValue,
            ArmourMorePct: int.MaxValue, MentalConditioning: true);
        // Flat mana 5 converts to base armour 8, exactly cancelling -8 ordinary armour despite enormous multipliers.
        var cancelled = CharacterStats.For(c, 1, bonus);
        Assert.Equal(0, cancelled.Armour);
        Assert.Equal(1u, cancelled.Mana);
        var almost = CharacterStats.For(c, 1, bonus with { ArmourPct = 0, ManaPct = 1 });
        Assert.Equal(1717987, almost.Armour); // Only the mana branch's extra 1% survives cancellation.
        var huge = CharacterStats.For(c, 1, bonus with
        {
            Armour = int.MinValue, Evasion = int.MaxValue, EvasionPct = int.MaxValue,
            EvasionMorePct = int.MaxValue, IronReflexes = true,
        });
        Assert.Equal(int.MaxValue, huge.Armour);
        Assert.Equal(0, huge.Evasion);
        var negative = CharacterStats.For(c, 1, bonus with { Armour = int.MinValue });
        Assert.Equal(0, negative.Armour);
    }
}
