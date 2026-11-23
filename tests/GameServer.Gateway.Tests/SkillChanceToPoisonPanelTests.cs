namespace GameServer.Gateway.Tests;

public sealed class SkillChanceToPoisonPanelTests
{
    [Fact]
    public void Selected_skill_folds_skill_and_character_chance_to_poison()
    {
        // Character has 25% chance to poison from passive tree
        int Character(uint stat) => stat == Stat.BaseChanceToPoisonOnHitPct ? 25 : 0;
        // Skill (e.g. Cobra Lash) has 40% chance to poison
        int Own(uint stat) => stat == Stat.BaseChanceToPoisonOnHitPct ? 40 : 0;

        var selected = PanelStats.Selected(Character, Own, playerLevel: 10);
        var poisonMainHand = selected.FirstOrDefault(s => s.Stat == Stat.VirtualTotalMainHandChanceToPoisonOnHitPct);

        Assert.Equal("virtual_total_main_hand_chance_to_poison_on_hit_%", Program.Stats.Name(poisonMainHand.Stat));
        Assert.Equal(65, poisonMainHand.Value);
    }
}
