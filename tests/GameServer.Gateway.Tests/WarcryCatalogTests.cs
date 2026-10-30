using System.Text.Json;

namespace GameServer.Gateway.Tests;

public class WarcryCatalogTests
{
    [Fact]
    public void Actual_type_62_classifies_warcries_independently_of_spells_attacks_and_names()
    {
        using var types = JsonDocument.Parse(File.ReadAllBytes("data/tables/ActiveSkillType.json"));
        Assert.Equal("Warcry", types.RootElement[62].GetProperty("Id").GetString());
        foreach (string id in new[] { "EnduringCry", "RallyingCry", "IntimidatingCry", "InfernalCry" })
            Assert.True(Program.Skills.IsWarcry(Program.Skills.EffectRow(id)), id);
        foreach (string id in new[] { "Fireball", "PlayerMelee", "Haste", "RighteousFire" })
            Assert.False(Program.Skills.IsWarcry(Program.Skills.EffectRow(id)), id);
        Assert.False(Program.Skills.IsSpell(Program.Skills.EffectRow("EnduringCry")));
        Assert.False(Program.Skills.IsWarcry(-1));
        Assert.False(Program.Skills.IsWarcry(int.MaxValue));
    }

}
