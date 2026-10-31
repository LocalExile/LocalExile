using System.Globalization;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class DefenceMoreMultiplierTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static readonly Lazy<ItemCatalog> Items = new(() => new ItemCatalog(Data()));
    static readonly Lazy<SkillCatalog> Skills = new(() => new SkillCatalog(Data()));
    static readonly Lazy<StatCatalog> Stats = new(() => new StatCatalog(Data()));

    static AuraCatalog.Aura Aura(string family) => new(family == "Determination" ? (ushort)0x103 : (ushort)0x104,
        Skills.Value.EffectRow(family), family, family, 1, AuraCatalog.BuffRow(family), 0, 5000, 20);

    static CharacterStats.Bonus AuraBonus(params string[] families) =>
        AuraCatalog.Bonus(families.Select(Aura), 50, Skills.Value, Stats.Value);

    static IEnumerable<(string Stat, int Value)> FlaskStats(string name, int effect = 0)
    {
        var type = Items.Value.Find(name)!;
        var flask = Items.Value.UtilityFlaskOf(type)!;
        Assert.Equal(20, Assert.Single(flask.Stats).Value); // Native Basalt/Stibnite base more modifier.
        var use = UtilityFlasks.For(flask, ItemCatalog.Create(type), new Dictionary<string, int>(),
            stat => stat == "flask_effect_+%" ? effect : 0);
        return use.Stats.Select(s => (s.Stat, s.Value));
    }

    [Theory]
    [InlineData(0, "107.6", 3234)]
    [InlineData(15, "112.79", 3315)]
    [InlineData(25, "116.25", 3369)]
    public void Basalt_retains_the_product_of_aura_and_scaled_flask_more_factors(
        int flaskEffect, string combinedPercent, int expectedArmour)
    {
        var aura = AuraBonus("Determination");
        var bonus = UtilityFlasks.Apply(aura with { Armour = aura.Armour + 19 }, FlaskStats("Basalt Flask", flaskEffect));
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100, bonus);

        Assert.Equal(decimal.Parse(combinedPercent, CultureInfo.InvariantCulture), bonus.ArmourMorePct);
        Assert.Equal(expectedArmour, pools.Armour);
        // Ordinary Basalt: (1539 aura + 19 vest) * 1.73 * 1.20 = 3234.408.
        // Rounding the combined more percentage to 108 first would incorrectly give 3241.
    }

    [Theory]
    [InlineData(0, "71.6", 4082)]
    [InlineData(15, "75.89", 4184)]
    [InlineData(25, "78.75", 4252)]
    public void Stibnite_retains_the_product_after_native_flask_effect_scaling(
        int flaskEffect, string combinedPercent, int expectedEvasion)
    {
        var bonus = UtilityFlasks.Apply(AuraBonus("Grace"), FlaskStats("Stibnite Flask", flaskEffect));
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100, bonus);

        Assert.Equal(decimal.Parse(combinedPercent, CultureInfo.InvariantCulture), bonus.EvasionMorePct);
        Assert.Equal(expectedEvasion, pools.Evasion);
        // Ordinary Stibnite: (15 base + 2317 aura) * 1.02 dexterity * 1.43 * 1.20 = 4081.74624.
    }

    [Fact]
    public void Both_flasks_keep_their_typed_scope_and_exact_products()
    {
        var aura = AuraBonus("Determination", "Grace");
        var bonus = UtilityFlasks.Apply(aura with { Armour = aura.Armour + 19 },
            FlaskStats("Basalt Flask").Concat(FlaskStats("Stibnite Flask")));
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100, bonus);

        Assert.Equal(107.6m, bonus.ArmourMorePct);
        Assert.Equal(71.6m, bonus.EvasionMorePct);
        Assert.Equal(3234, pools.Armour);
        Assert.Equal(4082, pools.Evasion);
    }

    [Fact]
    public void Iron_Reflexes_uses_both_exact_more_factors_and_one_final_armour_round()
    {
        var aura = AuraBonus("Determination", "Grace");
        var bonus = UtilityFlasks.Apply(aura with { Armour = aura.Armour + 19, IronReflexes = true },
            FlaskStats("Basalt Flask").Concat(FlaskStats("Stibnite Flask")));
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100, bonus);

        // 1558 * 2.076 + 2332 * 1.716 * 2.076 = 11541.962112; Dexterity is excluded from converted EV.
        Assert.Equal(11542, pools.Armour);
        Assert.Equal(0, pools.Evasion);
    }

    [Fact]
    public void A_final_halfway_value_rounds_up_after_more_composition()
    {
        var bonus = UtilityFlasks.Apply(new CharacterStats.Bonus(Armour: 1, ArmourMorePct: 25m),
            FlaskStats("Basalt Flask"));

        Assert.Equal(2, CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100, bonus).Armour); // 1 * 1.25 * 1.2 = 1.5.
    }

    [Fact]
    public void Exact_more_composition_still_saturates_the_final_native_rating()
    {
        var bonus = UtilityFlasks.Apply(new CharacterStats.Bonus(Armour: int.MaxValue, ArmourMorePct: 73m),
            FlaskStats("Basalt Flask"));

        Assert.Equal(107.6m, bonus.ArmourMorePct);
        Assert.Equal(int.MaxValue, CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100, bonus).Armour);
    }
}
