using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class SpellCriticalStrikeTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static Func<string, int> Values(params (string Key, int Value)[] values) =>
        name => values.FirstOrDefault(v => v.Key == name).Value;

    [Theory]
    [InlineData("Fireball", 500)]
    public void Each_spell_uses_its_own_extracted_base(string effect, int expected)
    {
        var skills = new SkillCatalog(Data());
        int row = skills.EffectRow(effect);
        Assert.Equal(expected, skills.SpellCritChance(row, 1));
        Assert.Equal(expected, skills.SpellCritChance(row, 20));
        Assert.Equal(expected, SpellCriticalStrike.Chance(skills.SpellCritChance(row, 1), Values()));
    }

}
