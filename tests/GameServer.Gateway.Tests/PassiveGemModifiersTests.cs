namespace GameServer.Gateway.Tests;

public class PassiveGemModifiersTests
{
    [Fact]
    public void Active_gems_gain_the_skill_level_and_quality_bonuses_without_changing_the_intrinsic_values()
    {
        var intrinsic = (Level: 20, Quality: 17);
        var mods = new PassiveGemModifiers(SkillLevels: 1, SkillQuality: 20, NonExceptionalSupportLevels: 4);

        Assert.Equal((21, 37), mods.Apply(intrinsic.Level, intrinsic.Quality));
        Assert.Equal((20, 17), intrinsic);
        Assert.Equal((20, 17), default(PassiveGemModifiers).Apply(intrinsic.Level, intrinsic.Quality));
    }

    [Fact]
    public void Support_gems_use_the_separate_non_exceptional_bonus()
    {
        var mods = new PassiveGemModifiers(SkillLevels: 1, SkillQuality: 20, NonExceptionalSupportLevels: 4);
        Assert.Equal((24, 17), mods.Apply(20, 17, support: true));
        Assert.Equal((3, 17), mods.Apply(3, 17, support: true, exceptional: true));
    }

    [Fact]
    public void Removing_bonuses_restores_the_intrinsic_properties_and_reductions_do_not_underflow()
    {
        Assert.Equal((1, 0), new PassiveGemModifiers(-4, -30).Apply(1, 10));
        Assert.Equal((int.MaxValue, int.MaxValue), new PassiveGemModifiers(int.MaxValue, int.MaxValue).Apply(20, 20));
    }

    [Fact]
    public void Actual_Centerpiece_and_Family_Heirlooms_increase_Fireballs_radius_and_damage()
    {
        if (Data() is not { } data) return; // Real-client cross-check is optional on a fresh clone.
        var tree = new PassiveTree(data);
        var names = new StatCatalog(data);
        var allocated = tree.Stats([30851, 46836]);
        int Get(string name) => names.Key(name) is uint key ? allocated.GetValueOrDefault(key) : 0;
        var mods = PassiveGemModifiers.From(Get);
        var effective = mods.Apply(1, 0);
        var skills = new SkillCatalog(data);
        int fireball = skills.EffectRow("Fireball");
        int Radius(int level, int quality) => skills.EffectStats(fireball, level, quality)
            .FirstOrDefault(kv => kv.Item1 == names.Key("active_skill_base_area_of_effect_radius")).Item2;

        Assert.Equal((2, 20), effective);
        // Fireball's quality coefficient is 150 thousandths: 20 * 150 / 1000 = 3 units of radius.
        Assert.Equal(Radius(2, 0) + 3, Radius(effective.Level, effective.Quality));
        Assert.True(skills.HitDamage(fireball, effective.Level, effective.Quality).MaxTotal > skills.HitDamage(fireball, 1, 0).MaxTotal);
    }

    private static string? Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string data = Path.Combine(dir.FullName, "data");
            string[] required = ["PassiveSkills", "Stats", "GrantedEffects", "GrantedEffectsPerLevel",
                "GrantedEffectStatSets", "GrantedEffectStatSetsPerLevel", "GrantedEffectQualityStats"];
            if (required.All(name => File.Exists(Path.Combine(data, "tables", name + ".json")))) return data;
        }
        return null;
    }
}
