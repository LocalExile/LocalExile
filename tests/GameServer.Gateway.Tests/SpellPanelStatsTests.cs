using System.Text.Json;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class SpellPanelStatsTests
{
    static readonly Lazy<StatCatalog> Stats = new(() => new(Data()));
    static readonly Lazy<SkillCatalog> Skills = new(() => new(Data()));
    static Dictionary<uint, int> Values(params (string Name, int Value)[] values) =>
        values.ToDictionary(v => Stats.Value.Key(v.Name)!.Value, v => v.Value);
    static Dictionary<string, int> Rows(SkillHitPlan plan, DamagePacket hit, int cps = 100) =>
        SpellPanelStats.Compute(plan, hit, cps / 100.0).ToDictionary(r => r.Stat, r => r.Value);

    [Fact]
    public void Real_fireball_uses_its_modified_packet_with_floored_ranges_and_fractional_average()
    {
        int effect = Skills.Value.EffectRow("Fireball");
        var plan = SkillHitPlan.ForSkill(Skills.Value, effect, 1);
        var own = Skills.Value.EffectStats(effect, 1).ToDictionary(s => s.Item1, s => s.Item2);
        var hit = SpellHit.Resolve(plan, Stats.Value, Values(("spell_damage_+%", 12)), own, new DamageConversion());
        var rows = Rows(plan, hit, 133);
        Assert.Equal(10.08, hit.MinTotal, 6);
        Assert.Equal(15.68, hit.MaxTotal, 6);
        Assert.Equal(10, rows["spell_minimum_total_damage"]);
        Assert.Equal(15, rows["spell_maximum_total_damage"]);
        Assert.Equal(10, rows["spell_minimum_fire_damage"]);
        Assert.Equal(15, rows["spell_maximum_fire_damage"]);
        Assert.Equal(0, rows["spell_maximum_physical_damage"]);
        Assert.Equal(1288, rows["display_hundred_times_damage_per_skill_use"]);
        Assert.Equal(1713, rows["hundred_times_damage_per_second"]);
    }

    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }
}
