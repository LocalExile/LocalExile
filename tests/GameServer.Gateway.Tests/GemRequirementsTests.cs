namespace GameServer.Gateway.Tests;

// The level requirement a gem's level carries (GrantedEffectStatSetsPerLevel PlayerLevelReq) and the stall it puts on
// the gem's experience. Anchors from the client's tooltips: Haste level 1 requires character level 24 … 70 at 20,
// Clarity 10 … 70 (poedb, 3.26 data); Fireball level 20 requires 70 and level 4 requires 7 (checked live 2026-10-23:
// a character set to level 10 in a level 20 Fireball wand shows the gem red, "Requires level 70").
public class GemRequirementsTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    [Fact]
    public void A_gems_level_requirement_is_the_PlayerLevelReq_of_its_level()
    {
        var skills = new SkillCatalog(Data());
        int haste = skills.EffectRow("Haste"), clarity = skills.EffectRow("Clarity");

        Assert.Equal(24, skills.PlayerLevelReq(haste, 1));
        Assert.Equal(70, skills.PlayerLevelReq(haste, 20));
        Assert.Equal(10, skills.PlayerLevelReq(clarity, 1));
        Assert.Equal(70, skills.PlayerLevelReq(clarity, 20));
    }

    [Fact]
    public void The_level_stalls_at_the_first_level_the_character_cannot_meet()
    {
        var skills = new SkillCatalog(Data());
        int haste = skills.EffectRow("Haste");

        // Not even level 1 (24) is met: the gem is disabled and shows level 1.
        Assert.Equal(1, skills.MaxLevelAt(haste, 1));
        Assert.Equal(1, skills.MaxLevelAt(haste, 23));
        // Level 2's requirement is 27, level 5's 36, level 9's 48, level 10's 50.
        Assert.Equal(1, skills.MaxLevelAt(haste, 24));
        Assert.Equal(1, skills.MaxLevelAt(haste, 26));
        Assert.Equal(2, skills.MaxLevelAt(haste, 27));
        Assert.Equal(5, skills.MaxLevelAt(haste, 36));
        Assert.Equal(9, skills.MaxLevelAt(haste, 49));
        Assert.Equal(20, skills.MaxLevelAt(haste, 70));
        // Above the cap (bonus levels, corruption): the data's requirements carry on to level 100.
        Assert.Equal(40, skills.MaxLevelAt(haste, 100));
    }

    [Fact]
    public void Fireball_at_character_level_10_stalls_at_level_4()
    {
        var skills = new SkillCatalog(Data());
        int fireball = skills.EffectRow("Fireball");

        Assert.Equal(70, skills.PlayerLevelReq(fireball, 20));
        Assert.Equal(7, skills.PlayerLevelReq(fireball, 4));
        Assert.Equal(20, skills.MaxLevelAt(fireball, 70));
        Assert.Equal(4, skills.MaxLevelAt(fireball, 10));
    }

    [Fact]
    public void A_skill_without_a_requirement_table_does_not_stall()
    {
        var skills = new SkillCatalog(Data());

        Assert.Equal(40, skills.MaxLevelAt(-1, 1));
        Assert.Equal(0, skills.PlayerLevelReq(-1, 1));
    }

    [Fact]
    public void The_level_up_stops_where_the_character_no_longer_meets_the_next_level()
    {
        // The "+" appears when the experience is one point below the next threshold, so the character can be between the
        // two levels only if its level fell after the experience was gained (/set_level; the game has no de-level):
        // Fireball level 9 needs 28 and level 10 needs 32, so at 31 the gem stays level 9.
        var skills = new SkillCatalog(Data());
        int fireball = skills.EffectRow("Fireball");

        Assert.Equal(28, skills.PlayerLevelReq(fireball, 9));
        Assert.Equal(32, skills.PlayerLevelReq(fireball, 10));
        Assert.Equal(9, skills.MaxLevelAt(fireball, 31));
        // The same with attributes: 67 Int meets level 9's requirement, 75 does not meet level 10's.
        Assert.Equal(67, SkillCatalog.GemAttributeRequirement(100, 28, support: false));
        Assert.Equal(75, SkillCatalog.GemAttributeRequirement(100, 32, support: false));
    }

    [Fact]
    public void Attribute_requirements_follow_the_clients_curve()
    {
        // The level requirement through (3 x it + 11), the percentage share to the power 0.9, 0.7 for a skill.
        Assert.Equal(155, SkillCatalog.GemAttributeRequirement(100, 70, support: false));
        Assert.Equal(119, SkillCatalog.GemAttributeRequirement(75, 70, support: false));
        Assert.Equal(98, SkillCatalog.GemAttributeRequirement(60, 70, support: false));
        Assert.Equal(68, SkillCatalog.GemAttributeRequirement(40, 70, support: false));
        // A support's share is 0.5: Added Fire Damage Support at requirement level 8 is 18, at 70 it is 111.
        Assert.Equal(18, SkillCatalog.GemAttributeRequirement(100, 8, support: true));
        Assert.Equal(111, SkillCatalog.GemAttributeRequirement(100, 70, support: true));
        // A requirement below 14 is hidden (0), and a gem with no share has none.
        Assert.Equal(0, SkillCatalog.GemAttributeRequirement(100, 1, support: false));
        Assert.Equal(0, SkillCatalog.GemAttributeRequirement(0, 70, support: false));
    }

    [Fact]
    public void The_table_driven_requirements_match_the_tooltips()
    {
        var items = new ItemCatalog(Data());
        var skills = new SkillCatalog(Data());

        // Level, strength, dexterity, intelligence from poedb (3.26 data): Haste 24/58 … 70/155, Clarity 75% int
        // 10/22 … 70/119, Anger 60% str 40% int 24/37/25 … 70/98/68, Fireball level 4 (requirement level 7) 22.
        (int, int, int) At(string name, int level)
        {
            var (type, variant) = items.FindGem(name)!.Value;
            var gem = ItemCatalog.Create(type) with { GemExperience = items.Gem(type)!.ExperienceFor(level), GemVariant = variant.Index };
            var t = items.Gem(type)!;
            int effect = t.Variant(variant.Index).Effect;
            int lr = skills.PlayerLevelReq(effect, Math.Min(items.GemLevel(gem), t.MaxLevel));
            return (SkillCatalog.GemAttributeRequirement(t.StrengthPercent, lr, t.IsSupport),
                SkillCatalog.GemAttributeRequirement(t.DexterityPercent, lr, t.IsSupport),
                SkillCatalog.GemAttributeRequirement(t.IntelligencePercent, lr, t.IsSupport));
        }

        Assert.Equal((0, 58, 0), At("Haste", 1));
        Assert.Equal((0, 113, 0), At("Haste", 10));
        Assert.Equal((0, 155, 0), At("Haste", 20));
        Assert.Equal((0, 0, 22), At("Clarity", 1));
        Assert.Equal((0, 0, 119), At("Clarity", 20));
        Assert.Equal((37, 0, 25), At("Anger", 1));
        Assert.Equal((98, 0, 68), At("Anger", 20));
        Assert.Equal((0, 0, 22), At("Fireball", 4));
        Assert.Equal((18, 0, 0), At("Added Fire Damage Support", 1));
        Assert.Equal((111, 0, 0), At("Added Fire Damage Support", 20));
    }
}
