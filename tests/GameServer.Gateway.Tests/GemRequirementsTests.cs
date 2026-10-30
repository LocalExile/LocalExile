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

}
