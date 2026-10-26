using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class GemSupportsTests
{
    static string Data => "data";

    [Theory]
    [InlineData("Cyclone", "SupportIncreasedAreaOfEffect", true)]
    [InlineData("Cyclone", "SupportFasterAttacks", true)]
    [InlineData("Cyclone", "SupportFasterCasting", false)]
    [InlineData("Fireball", "SupportIncreasedAreaOfEffect", true)]
    [InlineData("Fireball", "SupportMeleePhysicalDamage", false)]
    [InlineData("Clarity", "SupportAddedFireDamage", false)]
    public void Compatibility_uses_native_skill_types(string active, string support, bool expected)
    {
        var skills = new SkillCatalog(Data);
        var catalog = new GemSupports(Data);
        var result = catalog.Resolve(skills.EffectRow(active), [new(skills.EffectRow(support), 20, 0, 1)], skills);
        Assert.Equal(expected, result.Count == 1);
    }

    [Fact]
    public void Postfix_boolean_operators_and_legacy_or_terms_are_respected()
    {
        Assert.True(GemSupports.Matches([48, 54, 1, 6, 73], new HashSet<int> { 1, 6 }, false));
        Assert.False(GemSupports.Matches([48, 54, 1, 6, 73], new HashSet<int> { 1 }, false));
        Assert.True(GemSupports.Matches([1, 7, 72, 47, 74, 73], new HashSet<int> { 7 }, false));
        Assert.False(GemSupports.Matches([1, 7, 72, 47, 74, 73], new HashSet<int> { 1, 47 }, false));
        Assert.False(GemSupports.Matches([73], new HashSet<int>(), false));
    }

    [Fact]
    public void Separate_link_groups_never_share_supports()
    {
        var host = new Item(123, ["Base", "Sockets"], 1)
        {
            Sockets = Enumerable.Range(0, 6).Select(_ => new ItemSocket(SocketColour.White)).ToArray(),
            Links = [3, 2, 1]
        };
        Assert.Equal(new[] { 0, 0, 0, 1, 1, 2 }, Enumerable.Range(0, 6).Select(i => GemSupports.LinkGroup(host, i)));
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, Enumerable.Range(0, 6).Select(i => GemSupports.LinkGroup(host with { Links = null }, i)));
    }

    [Fact]
    public void Five_distinct_supports_coexist_and_duplicates_do_not_stack()
    {
        var skills = new SkillCatalog(Data); var catalog = new GemSupports(Data);
        string[] names = ["SupportIncreasedAreaOfEffect", "SupportFasterAttacks", "SupportMeleePhysicalDamage", "SupportBrutality", "SupportAddedFireDamage"];
        var candidates = names.Select((n, i) => new GemSupports.Candidate(skills.EffectRow(n), 20, 0, i + 1)).ToList();
        candidates.Add(new(skills.EffectRow(names[0]), 1, 20, 6));
        var result = catalog.Resolve(skills.EffectRow("Cyclone"), candidates, skills);
        Assert.Equal(5, result.Count);
        Assert.Equal(20, result.Single(s => s.Effect.Id == names[0]).Gem.Level);
        Assert.Equal(1.3 * 1.1 * 1.3 * 1.4 * 1.2, result.Aggregate(1.0, (v, s) => v * s.CostMultiplier / 100.0), 8);
        var stats = new StatCatalog(Data);
        Assert.Contains(result.Single(s => s.Effect.Id == names[0]).Stats,
            s => s.Key == stats.Key("base_skill_area_of_effect_+%") && s.Value == 49);
    }

    [Fact]
    public void Awakened_and_regular_versions_share_the_native_family()
    {
        var skills = new SkillCatalog(Data); var catalog = new GemSupports(Data);
        var result = catalog.Resolve(skills.EffectRow("Cyclone"),
            [new(skills.EffectRow("SupportAddedFireDamage"), 20, 0, 0),
             new(skills.EffectRow("SupportAwakenedAddedFireDamage"), 5, 0, 1)], skills);
        Assert.Single(result);
        Assert.Equal("SupportAwakenedAddedFireDamage", result[0].Effect.Id);
    }

    [Fact]
    public void Area_support_changes_radius_and_concentrated_effect_also_applies_its_penalty()
    {
        var skills = new SkillCatalog(Data); var catalog = new GemSupports(Data); var stats = new StatCatalog(Data);
        var supports = catalog.Resolve(skills.EffectRow("Cyclone"),
            [new(skills.EffectRow("SupportIncreasedAreaOfEffect"), 20, 0, 1)], skills);
        var values = supports.SelectMany(s => s.Stats).ToDictionary(s => s.Key, s => s.Value);
        int Get(string name) => stats.Key(name) is uint key ? values.GetValueOrDefault(key) : 0;
        Assert.Equal(19, SkillArea.Radius(16, Get, false));
        values[stats.Key("support_concentrated_effect_skill_area_of_effect_+%_final")!.Value] = -30;
        Assert.Equal(16, SkillArea.Radius(16, Get, false));
    }

    [Fact]
    public void More_multipliers_multiply_and_brutality_removes_elemental_and_chaos_damage()
    {
        var hit = DamagePacket.Empty.Add(DamageType.Physical, new(100, 100)).Add(DamageType.Fire, new(50, 50))
            .Add(DamageType.Chaos, new(25, 25));
        (string, int)[] mods = [("support_melee_physical_damage_+%_final", 49),
            ("support_area_concentrate_area_damage_+%_final", 39), ("support_brutality_physical_damage_+%_final", 39),
            ("deal_no_elemental_damage", 1), ("base_deal_no_chaos_damage", 1)];
        var types = new HashSet<int> { 0, 19, 7 };
        hit = GemSupportDamage.Scale(hit, mods, types, false);
        hit = GemSupportDamage.Scale(hit, mods, types, true);
        Assert.Equal(100 * 1.49 * 1.39 * 1.39, hit[DamageType.Physical].Min, 8);
        Assert.True(hit[DamageType.Fire].IsZero);
        Assert.True(hit[DamageType.Chaos].IsZero);
    }

    [Fact]
    public void Physical_more_applies_before_gain_as_extra_and_conditional_more_is_not_applied_to_every_hit()
    {
        var hit = DamagePacket.Empty.Add(DamageType.Physical, new(100, 100));
        (string, int)[] mods = [("support_melee_physical_damage_+%_final", 49),
            ("support_ruthless_big_hit_damage_+%_final", 100), ("support_burning_damage_+%_final", 100)];
        hit = GemSupportDamage.Scale(hit, mods, new HashSet<int> { 0, 19 }, false);
        var conversion = new DamageConversion(); conversion.AddGain(DamageType.Physical, DamageType.Fire, 39);
        hit = hit.Convert(conversion);
        Assert.Equal(149, hit[DamageType.Physical].Min);
        Assert.Equal(149 * .39, hit[DamageType.Fire].Min, 8);
    }

    [Fact]
    public void Quality_area_damage_and_added_damage_are_skill_local_and_add_to_existing_increases()
    {
        var stats = new Dictionary<string, int>
        {
            ["physical_damage_+%"] = 100, ["area_damage_+%"] = 10,
            ["attack_minimum_added_cold_damage"] = 20, ["attack_maximum_added_cold_damage"] = 20
        };
        var weapon = new SkillCatalog.Unarmed(1000, 100, 100, 8);
        var area = SupportedAttackDamage.Hit(weapon, n => stats.GetValueOrDefault(n), new HashSet<int> { 0, 19, 7 });
        var single = SupportedAttackDamage.Hit(weapon, n => stats.GetValueOrDefault(n), new HashSet<int> { 0, 19 });
        Assert.Equal(210, area[DamageType.Physical].Min);
        Assert.Equal(200, single[DamageType.Physical].Min);
        Assert.Equal(22, area[DamageType.Cold].Min);
        Assert.Equal(20, single[DamageType.Cold].Min);
    }

    [Fact]
    public void Native_attack_speed_multiplier_is_used_for_cyclone()
    {
        var skills = new SkillCatalog(Data); var catalog = new GemSupports(Data);
        Assert.Equal(3, catalog.AttackSpeedMultiplier(skills.EffectRow("Cyclone"), 20));
        Assert.Equal(.7, catalog.AttackSpeedMultiplier(skills.EffectRow("Sweep"), 20), 10);
        Assert.Equal(1, catalog.AttackSpeedMultiplier(skills.EffectRow("Fireball"), 20));
    }
}
