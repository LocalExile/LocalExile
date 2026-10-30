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

}

public class SkillAreaPanelTotalTests
{
    static readonly Lazy<StatCatalog> Catalog = new(() => new StatCatalog(ChargePanelStatsTestsData.Folder));

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

}
