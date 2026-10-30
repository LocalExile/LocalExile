using System.Reflection;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class LifeManaPoolSourceTests
{
    static SkillCatalog.ClassInfo AttributeBase(int dexterity = 0, int intelligence = 0, int strength = 0) =>
        SkillCatalog.ClassInfo.Witch with { Strength = strength, Dexterity = dexterity, Intelligence = intelligence };

    [Theory]
    [InlineData(3, 1, 50u)]
    [InlineData(4, 2, 52u)]
    [InlineData(9, 9, 56u)]
    [InlineData(10, 10, 59u)]
    [InlineData(19, 19, 65u)]
    [InlineData(20, 20, 69u)]
    public void Four_attribute_sources_use_their_own_complete_groups(int dexterity, int intelligence, uint expectedLife)
    {
        var pools = CharacterStats.For(AttributeBase(dexterity, intelligence), 1,
            new(LifePerFourDexterity: 1, LifePerTenDexterity: 1,
                LifePerTwoIntelligence: 1, LifePerTenIntelligence: 1));
        Assert.Equal(expectedLife, pools.Life);
    }

    [Fact]
    public void Final_attributes_and_all_signed_flat_sources_precede_increases_and_existing_pool_floor()
    {
        // Final20 Dex/Int, rather than the class9 or flat10, supplies every group. Attribute sums are separate
        // from the level term and ordinary flat stats. The257.5/163.75 endpoints retain the existing floor policy.
        var bonus = new CharacterStats.Bonus(Dexterity: 1, Intelligence: 1, DexterityPct: 100, IntelligencePct: 100,
            Life: 7, LifePct: 25, Mana: 7, ManaPct: 25, LifePerLevel: 2, ManaPerLevel: 2,
            LifePerFourDexterity: 3, LifePerTenDexterity: 2, LifePerTwoIntelligence: -1, LifePerTenIntelligence: 5);
        var pools = CharacterStats.For(AttributeBase(9, 9, 4), 10, bonus);
        Assert.Equal((4, 20, 20), (pools.Strength, pools.Dexterity, pools.Intelligence));
        Assert.Equal(257u, pools.Life); // (38+120+2+7+20+15+4-10+10)*1.25.
        Assert.Equal(163u, pools.Mana); // (34+60+10+7+20)*1.25.
        var strengthMana = CharacterStats.For(AttributeBase(9, 9, 4), 10, bonus with { ManaPerFourStrength = 8 });
        Assert.Equal(257u, strengthMana.Life);
        Assert.Equal(173u, strengthMana.Mana); // One complete Strength group adds8 to the same flat Mana bucket.
    }

    [Theory]
    [InlineData(-12, 38u)]
    [InlineData(-6, 98u)]
    [InlineData(-3, 128u)]
    [InlineData(-10, 58u)] // A -12 penalty and a real +2 item source cancel before scaling.
    public void Signed_per_level_life_penalties_subtract_from_inherent_growth(int coefficient, uint expectedLife)
        => Assert.Equal(expectedLife, CharacterStats.For(AttributeBase(), 10,
            new(LifePerLevel: coefficient)).Life);

    [Theory]
    [InlineData(-1, 1, 52u, 43u)]
    [InlineData(1, 1, 52u, 43u)]
    [InlineData(100, 100, 1438u, 934u)]
    [InlineData(101, 100, 1438u, 934u)]
    public void Level_sources_share_the_real_clamped_pool_level(int supplied, int expectedLevel, uint life, uint mana)
    {
        var pools = CharacterStats.For(AttributeBase(), supplied, new(LifePerLevel: 2, ManaPerLevel: 3));
        Assert.Equal(expectedLevel, pools.Level);
        Assert.Equal(life, pools.Life);
        Assert.Equal(mana, pools.Mana);
    }

    [Fact]
    public void Wide_signed_group_terms_cancel_before_the_final_native_pool_clamp()
    {
        var pools = CharacterStats.For(AttributeBase(10, 10), 1,
            new(LifePerTenDexterity: int.MaxValue, LifePerTenIntelligence: int.MinValue));
        Assert.Equal(49u, pools.Life);
        var high = CharacterStats.For(AttributeBase(int.MaxValue, int.MaxValue), 100,
            new(LifePerLevel: int.MaxValue, ManaPerLevel: int.MaxValue,
                LifePerTenDexterity: int.MaxValue, LifePerTenIntelligence: int.MaxValue,
                LifePerFourDexterity: int.MaxValue, LifePerTwoIntelligence: int.MaxValue,
                LifePct: int.MaxValue, ManaPct: int.MaxValue));
        Assert.Equal((uint)int.MaxValue, high.Life);
        Assert.Equal((uint)int.MaxValue, high.Mana);
        var low = CharacterStats.For(AttributeBase(20, 20), 100,
            new(LifePerLevel: int.MinValue, ManaPerLevel: int.MinValue,
                LifePerFourDexterity: int.MinValue, LifePerTwoIntelligence: int.MinValue));
        Assert.Equal(1u, low.Life);
        Assert.Equal(0u, low.Mana);
    }

    [Fact]
    public void Absent_and_explicit_zero_coefficients_preserve_existing_pool_and_Strength_mana_policies()
    {
        var source = new CharacterStats.Bonus(Life: 7, Mana: 7, LifePct: 33, ManaPct: 33, ManaPerFourStrength: 8);
        var original = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 10, source);
        var zero = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 10, source with
        {
            LifePerLevel = 0, ManaPerLevel = 0, LifePerTenDexterity = 0, LifePerTenIntelligence = 0,
            LifePerFourDexterity = 0, LifePerTwoIntelligence = 0,
        });
        Assert.Equal(original, zero);
        Assert.Equal(228u, zero.Life);
        Assert.Equal(187u, zero.Mana);
    }

    public static IEnumerable<object[]> NativeItemSources()
    {
        {
            yield return [14152, "LifePerLevelUnique__1", 369u, 2, 185u, 110u];
            yield return [14153, "ManaPerLevelUnique__1", 370u, 2, 165u, 130u];
            yield return [39378, "MutatedUniqueRing26ManaPerLevel", 370u, 2, 165u, 130u];
            yield return [13772, "MaximumLifePer10DexterityUnique__1", 5473u, 2, 167u, 110u];
            yield return [39622, "DivergentMaximumLifePer10DexterityUnique__1", 5473u, 2, 167u, 110u];
            yield return [21199, "LifePer10IntelligenceUnique__1", 9749u, 2, 171u, 110u];
            yield return [13591, "LifePerDexterityUnique__1", 4995u, 1, 168u, 110u];
            yield return [16634, "IncreasedLifePerIntelligenceUnique__1", 7963u, 1, 181u, 110u];
        }
    }

    static CharacterStats.Bonus SourceBonus(Dictionary<uint, int> raw) =>
        (CharacterStats.Bonus)typeof(Program).GetMethod("PassiveBonus", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [raw, default(WeaponProfile), AttributeModifiers.From(name =>
                Program.Stats.Key(name) is uint key ? raw.GetValueOrDefault(key) : 0)])!;

    [Theory]
    [MemberData(nameof(NativeItemSources))]
    public void Exact_native_item_rows_feed_real_pool_bonus_consumers(int row,
        string id, uint key, int coefficient, uint life, uint mana)
    {
        var catalog = new ItemCatalog("data");
        var stats = new StatCatalog("data");
        var source = catalog.Mods.Row(row)!;
        Assert.Equal(id, source.Id);
        Assert.Equal(1, source.Domain);
        Assert.Equal(id, catalog.Mods.ByHash(source.Hash)!.Id);
        var field = Assert.Single(source.Stats);
        Assert.False(field.Local);
        Assert.Equal(key, stats.Key(field.Id));
        Assert.InRange(coefficient, field.Min, field.Max);
        // Isolated native-modifier carrier; this is not a claim that the cross-base item is legally obtainable.
        var item = ItemCatalog.Create(catalog.Find("Iron Ring")!) with
            { Mods = [new(ModSection.Explicit, source.Hash, [coefficient])] };
        var raw = ItemModStats.Global([item], catalog.Mods, stats);
        Assert.Equal(coefficient, raw[key]);
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 10, SourceBonus(raw));
        Assert.Equal(life, pools.Life);
        Assert.Equal(mana, pools.Mana);
    }

    [Theory]
    [InlineData(56940, "The Apostate", -12, 38u)]
    [InlineData(20931, "Crimson Power", -6, 98u)]
    [InlineData(24063, "Sanguine Power", -3, 128u)]
    public void Actual_negative_passive_vectors_remain_signed_without_claiming_tree_admission(        ushort graph, string name, int coefficient, uint expectedLife)
    {
        var tree = new PassiveTree("data");
        Assert.Equal(name, tree.Find(graph)!.Name);
        var raw = tree.Stats([graph]);
        Assert.Equal(coefficient, raw[369]);
        var bonus = SourceBonus(raw);
        Assert.Equal(coefficient, bonus.LifePerLevel);
        Assert.Equal(expectedLife, CharacterStats.For(AttributeBase(), 10, bonus).Life);
    }

    [Theory]
    [InlineData(LifeManaPoolSources.LifePerLevel, true)]
    [InlineData(LifeManaPoolSources.ManaPerLevel, false)]
    [InlineData(LifeManaPoolSources.LifePerTenDexterity, true)]
    [InlineData(LifeManaPoolSources.LifePerTenIntelligence, true)]
    [InlineData(LifeManaPoolSources.LifePerFourDexterity, true)]
    [InlineData(LifeManaPoolSources.LifePerTwoIntelligence, true)]
    public void Raw_coefficient_commands_rebuild_the_matching_actual_pool(string name, bool life)
    {
        Assert.True(LifeManaPoolSources.IsInput(name));
        Assert.Equal(life, LifeManaPoolSources.IsLifeInput(name));
        Assert.Equal(!life, LifeManaPoolSources.IsManaInput(name));
        Assert.False(LifeManaPoolSources.IsInput("local_" + name));
    }
}
