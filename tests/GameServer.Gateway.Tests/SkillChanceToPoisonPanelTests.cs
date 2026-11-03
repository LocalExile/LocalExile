namespace GameServer.Gateway.Tests;

public sealed class SkillChanceToPoisonPanelTests
{
    [Fact]
    public void Selected_skill_folds_skill_and_character_chance_to_poison()
    {
        // Character has 25% chance to poison from passive tree
        int Character(string stat) => stat == "base_chance_to_poison_on_hit_%" ? 25 : 0;
        // Skill (e.g. Cobra Lash) has 40% chance to poison
        int Own(string stat) => stat == "base_chance_to_poison_on_hit_%" ? 40 : 0;

        var selected = PanelStats.Selected(Character, Own, playerLevel: 10);
        var poisonMainHand = selected.FirstOrDefault(s => s.Stat == "virtual_total_main_hand_chance_to_poison_on_hit_%");

        Assert.Equal("virtual_total_main_hand_chance_to_poison_on_hit_%", poisonMainHand.Stat);
        Assert.Equal(65, poisonMainHand.Value);
    }
}
