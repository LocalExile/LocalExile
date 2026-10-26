namespace GameServer.Gateway.Tests;

public class SkillAreaTests
{
    static Func<string, int> Stats(params (string Name, int Value)[] stats) =>
        name => stats.FirstOrDefault(s => s.Name == name).Value;

    [Fact]
    public void Without_area_stats_the_radius_is_the_base()
    {
        Assert.Equal(18, SkillArea.Radius(18, Stats(), spell: true));
    }

    [Fact]
    public void Cyclone_uses_its_own_base_and_scales_for_attacks_without_spell_area()
    {
        var normal = Stats();
        var enlarged = Stats(("base_skill_area_of_effect_+%", 300), ("spell_area_of_effect_+%", 900));
        Assert.Equal(16, SkillArea.BaseRadius("Cyclone", normal));
        Assert.Equal(16, SkillArea.Radius(SkillArea.BaseRadius("Cyclone", normal), normal, spell: false));
        Assert.Equal(32, SkillArea.Radius(SkillArea.BaseRadius("Cyclone", enlarged), enlarged, spell: false));
        Assert.Equal(300, SkillArea.Increased(enlarged, spell: false));
        Assert.Equal(100, SkillArea.RadiusPercent(enlarged, spell: false));
        Assert.Equal(0, SkillArea.RadiusPercent(normal, spell: false));
        Assert.Equal(16, SkillArea.BaseRadius("CycloneAltX", normal));
        Assert.Equal(20, SkillArea.BaseRadius("Cyclone", Stats(("active_skill_base_area_of_effect_radius", 20))));
        Assert.Equal(12, SkillArea.BaseRadius("Unknown", normal));
    }

    [Fact]
    public void Increased_area_grows_the_radius_by_its_square_root()
    {
        // 100% increased area: √2 = 1.41, 18 × 1.41 = 25.38 → 25 (PoB calcRadius).
        Assert.Equal(25, SkillArea.Radius(18, Stats(("base_skill_area_of_effect_+%", 60), ("skill_area_of_effect_+%", 40)), spell: true));
        // Spell area only counts for spells.
        Assert.Equal(25, SkillArea.Radius(18, Stats(("spell_area_of_effect_+%", 100)), spell: true));
        Assert.Equal(18, SkillArea.Radius(18, Stats(("spell_area_of_effect_+%", 100)), spell: false));
    }

    [Fact]
    public void More_area_multiplies_and_flat_radius_adds_before_scaling()
    {
        // (1 + 0.44) × (1 - 0.3) = 1.008 → √ = 1.00 (floored to hundredths), on 18 + 2 = 20.
        Assert.Equal(20, SkillArea.Radius(18, Stats(("base_skill_area_of_effect_+%", 44),
            ("support_concentrated_effect_skill_area_of_effect_+%_final", -30), ("active_skill_base_radius_+", 2)), spell: true));
        // A radius "more" scales the radius itself: 18 × 1.5 = 27.
        Assert.Equal(27, SkillArea.Radius(18, Stats(("active_skill_area_of_effect_radius_+%_final", 50)), spell: true));
    }
}

public class SkillAreaPanelTotalTests
{
    static readonly Lazy<StatCatalog> Catalog = new(() => new StatCatalog("data"));

    // The player's final stats as CharacterPlayerStats builds them: the raw sources plus the panel sums (PanelStats),
    // so the base form and the panel total skill_area_of_effect_+% both hold the passives' increase.
    static Dictionary<uint, int> Final(params (string Name, int Value)[] sources)
    {
        var final = sources.ToDictionary(s => Catalog.Value.Key(s.Name)!.Value, s => s.Value);
        int Get(string n) => Catalog.Value.Key(n) is uint k ? final.GetValueOrDefault(k) : 0;
        foreach (var (stat, value) in PanelStats.Compute(Get).ToList())
            if (Catalog.Value.Key(stat) is uint key) final[key] = value;
        return final;
    }

    static int Increased(Dictionary<uint, int> stats, bool spell = false) =>
        SkillArea.Increased(n => Catalog.Value.Key(n) is uint k ? stats.GetValueOrDefault(k) : 0, spell);

    [Fact]
    public void The_players_increased_area_counts_once()
    {
        var final = Final(("base_skill_area_of_effect_+%", 30));
        Assert.Equal(60, Increased(new(final)));   // the old double count: base form + panel total
        var area = SkillArea.WithoutPlayerBase(new(final), Catalog.Value.Key(SkillArea.BaseIncreased));
        Assert.Equal(30, Increased(area));
        // A gem's own increase (quality) laid over the player's stats adds to the player's total.
        area[Catalog.Value.Key(SkillArea.BaseIncreased)!.Value] = 20;
        Assert.Equal(50, Increased(area));
        // Three Power Charges with 10% increased area each: 30% more increase, once.
        var power = SkillArea.WithoutPlayerBase(Final(("base_skill_area_of_effect_+%", 30)), Catalog.Value.Key(SkillArea.BaseIncreased));
        Assert.Equal(SkillArea.Radius(60, n => n == "skill_area_of_effect_+%" ? 30 : 0, spell: false),
            SkillArea.Radius(60, n => Catalog.Value.Key(n) is uint k ? power.GetValueOrDefault(k) : 0, spell: false));
    }
}
