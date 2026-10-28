using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// A gem's level comes from its experience, its skill's numbers from that level's rows and its quality, and its variant
// picks the skill (Agents/docs/protocol/19-gems.md). Reads the extracted game tables.
public class GemLevelsTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static readonly ItemCatalog Items = new(Data());
    static readonly SkillCatalog Skills = new(Data());
    static readonly StatCatalog Stats = new(Data());

    static ItemCatalog.GemType GemOf(string name) => Items.Gem(Items.FindGem(name)!.Value.Type)!;

    static Item Gem(string name, int level = 1, int quality = 0)
    {
        var (type, variant) = Items.FindGem(name)!.Value;
        return ItemCatalog.Create(type) with
        {
            GemExperience = Items.Gem(type)!.ExperienceFor(level), GemVariant = variant.Index, Quality = (byte)quality,
        };
    }

    static string[] Skills_(Item gem) => [.. Items.GemEffects(gem).Select(e => Skills.EffectId(e)!)];

    static int Stat(int effect, int level, int quality, string name) =>
        Skills.EffectStats(effect, level, quality).FirstOrDefault(kv => kv.Item1 == Stats.Key(name)).Item2;

    [Fact]
    public void The_level_follows_the_experience_up_to_the_cap()
    {
        var fireball = GemOf("Fireball");

        Assert.Equal(20, fireball.MaxLevel);
        Assert.Equal(1, fireball.Level(0));
        Assert.Equal(1, fireball.Level(69));
        Assert.Equal(2, fireball.Level(70));
        Assert.Equal(20, fireball.Level(int.MaxValue));
        Assert.Equal(21, fireball.Level(-1));
        for (int level = 1; level <= 20; level++) Assert.Equal(level, fireball.Level(fireball.ExperienceFor(level)));
        Assert.Equal(7, Items.GemLevel(Gem("Fireball", 7)));
    }

    [Fact]
    public void The_experience_type_sets_the_cap()
    {
        Assert.Equal(3, GemOf("Empower Support").MaxLevel);
        Assert.Equal(4, GemOf("Awakened Empower Support").MaxLevel);
        Assert.Equal(1, GemOf("Portal").MaxLevel);
    }

    [Fact]
    public void Each_level_has_its_own_damage_cost_and_reservation()
    {
        int fireball = Skills.EffectRow("Fireball"), clarity = Skills.EffectRow("Clarity");

        Assert.Equal(9, Stat(fireball, 1, 0, "spell_minimum_base_fire_damage"));
        Assert.Equal(2825, Stat(fireball, 20, 0, "spell_maximum_base_fire_damage"));
        Assert.Equal(5, Stat(fireball, 1, 0, "mana_cost"));
        Assert.Equal(20, Stat(fireball, 20, 0, "mana_cost"));
        Assert.Equal((34, 0), Skills.Reservation(clarity, 1));
        Assert.Equal((279, 0), Skills.Reservation(clarity, 20));
        Assert.Equal(1200, Skills.CooldownMs(clarity, 20));
        Assert.Equal(0, Skills.VaalSouls(clarity, 20));
        Assert.Equal(30, Skills.VaalSouls(Skills.EffectRow("VaalFireball"), 1));
    }

    [Fact]
    public void Quality_adds_its_stats_rounded_down()
    {
        int fireball = Skills.EffectRow("Fireball");
        int radius = Stat(fireball, 1, 0, "active_skill_base_area_of_effect_radius");

        // 150 per mille per 1%: 20% → 3, 9% → 1.35 → 1.
        Assert.Equal(radius + 3, Stat(fireball, 1, 20, "active_skill_base_area_of_effect_radius"));
        Assert.Equal(radius + 1, Stat(fireball, 1, 9, "active_skill_base_area_of_effect_radius"));
    }

    [Fact]
    public void Transfigured_and_Vaal_gems_grant_their_own_skills()
    {
        Assert.Equal(["Fireball"], Skills_(Gem("Fireball")));
        Assert.Equal(["IceNovaAltX"], Skills_(Gem("Ice Nova of Frostbolts")));
        Assert.Equal(["VaalFireball", "Fireball"], Skills_(Gem("Vaal Fireball")));
        Assert.Equal(["VaalIceNova", "IceNovaAltX"], Skills_(Gem("Vaal Ice Nova of Frostbolts")));
        Assert.Equal("Ice Nova of Frostbolts", Items.GemName(Gem("Ice Nova of Frostbolts")));
        Assert.Empty(Skills_(Gem("Added Fire Damage Support")));
        Assert.NotNull(Items.FindGem("Heavy Strike of Trarthus"));
    }

    [Fact]
    public void Placeholder_unused_and_disabled_gems_are_never_created()
    {
        Assert.Null(Items.FindGem("[DNT] FireballAltX"));
        Assert.Null(Items.FindGem("[UNUSED] Blitz"));
        Assert.Null(Items.FindGem("WIP Support"));
        Assert.Null(Items.FindGem("Awakened Added Fire Damage Support"));
        // Vaal Fireball's second variant has only a placeholder as its base skill.
        Assert.All(Items.Gem(Items.FindGem("Vaal Fireball")!.Value.Type)!.Variants.Skip(1), v => Assert.False(v.Creatable));
    }

    [Fact]
    public void An_unknown_variant_is_the_normal_gem()
    {
        Assert.Equal(["IceNova"], Skills_(Gem("Ice Nova of Frostbolts") with { GemVariant = 99 }));
    }

    [Fact]
    public void Saves_keep_the_variant()
    {
        var gem = Gem("Ice Nova of Frostbolts", 12, 7) with { GemFlags = 4 };

        var back = CharacterSaves.FromSaved(CharacterSaves.ToSaved(gem), Items)!;

        Assert.Equal((gem.GemVariant, gem.GemExperience, gem.Quality, gem.GemFlags), (back.GemVariant, back.GemExperience, back.Quality, back.GemFlags));
    }
}
