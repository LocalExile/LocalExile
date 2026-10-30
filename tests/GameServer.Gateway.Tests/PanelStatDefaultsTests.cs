using System.Text.Json;

namespace GameServer.Gateway.Tests;

public sealed class PanelStatDefaultsTests
{
    internal static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    [Fact]
    public void Exact_Character_template_coefficients_and_Brand_limit_have_native_keys()
    {
        var expected = new[] { 3, 3, 10, 8, 8, 12 };
        Assert.Equal(expected, PanelStatDefaults.Inherent.Select(v => v.Value));
        var stats = new StatCatalog(Data());
        foreach (var (stat, _) in PanelStatDefaults.Inherent) Assert.NotNull(stats.Key(stat));
        Assert.DoesNotContain(PanelStatDefaults.Inherent, s => s.Stat == "skill_is_brand_skill");
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 4)]
    [InlineData(3, 6)]
    [InlineData(-3, 0)]
    public void Inherent_value_adds_once_to_original_sources_without_mutating_them(int source, int expected)
    {
        var raw = new Dictionary<string, int> { ["base_total_number_of_sigils_allowed"] = source };
        for (int i = 0; i < 2; i++)
            Assert.Equal(expected, PanelStatDefaults.Compute(n => raw.GetValueOrDefault(n))
                .Single(s => s.Stat == "base_total_number_of_sigils_allowed").Value);
        Assert.Equal(source, raw["base_total_number_of_sigils_allowed"]);
    }

    [Fact]
    public void Coefficients_are_not_multiplied_by_current_or_maximum_special_counts()
    {
        Assert.Equal(new[] { 3, 3, 10, 8, 8, 12 }, PanelStatDefaults.Compute(n =>
            n is "brutal_charges" or "virtual_maximum_brutal_charges" ? 99 : 0).Select(v => v.Value));
        Assert.Equal(int.MaxValue, PanelStatDefaults.Compute(_ => int.MaxValue)[0].Value);
    }

    [Fact]
    public void Brand_gate_comes_from_native_type_and_compatible_Foulgrasp_owns_two_once()
    {
        var skills = new SkillCatalog(Data());
        var stats = new StatCatalog(Data());
        uint flag = stats.Key("skill_is_brand_skill")!.Value, limit = stats.Key("base_total_number_of_sigils_allowed")!.Value;
        using var types = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Data(), "tables", "ActiveSkillType.json")));
        Assert.Equal("Brand", types.RootElement[64].GetProperty("Id").GetString());
        Assert.Equal((int)flag - 1, types.RootElement[64].GetProperty("FlagStat").GetInt32());
        foreach (string name in new[] { "StormBrand", "ArmageddonBrand" })
        {
            int effect = skills.EffectRow(name);
            Assert.True(skills.IsBrand(effect));
            Assert.Equal(1, skills.EffectStats(effect, 1).Single(s => s.Item1 == flag).Item2);
        }
        int fireball = skills.EffectRow("Fireball");
        Assert.False(skills.IsBrand(fireball));
        Assert.Equal(0, skills.EffectStats(fireball, 1).Single(s => s.Item1 == flag).Item2);
        Assert.False(skills.IsBrand(-1));
        var supports = new GemSupports(Data());
        int foulgrasp = supports.Effects.Single(e => e.Id == "SupportFoulgrasp").Row;
        GemSupports.Candidate[] candidates = [new(foulgrasp, 1, 0, 1)];
        var applied = Assert.Single(supports.Resolve(skills.EffectRow("StormBrand"), candidates, skills));
        Assert.Equal(2, applied.Stats.Single(s => s.Key == limit).Value);
        Assert.Empty(supports.Resolve(fireball, candidates, skills));
    }
}
