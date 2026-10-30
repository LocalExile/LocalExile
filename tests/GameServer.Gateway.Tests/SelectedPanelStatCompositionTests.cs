namespace GameServer.Gateway.Tests;

public sealed class SelectedPanelStatCompositionTests
{
    static Dictionary<string, int> Selected(IReadOnlyDictionary<string, int> actor,
        IReadOnlyDictionary<string, int> own) =>
        PanelStats.Selected(actor.GetValueOrDefault, own.GetValueOrDefault).ToDictionary(s => s.Stat, s => s.Value);

    [Fact]
    public void Character_and_skill_raw_sources_enter_each_selected_alias_once()
    {
        var actor = new Dictionary<string, int>
        {
            ["skill_area_of_effect_+%"] = 10, ["base_skill_area_of_effect_+%"] = 20,
            ["base_item_found_rarity_+%"] = 10, ["item_found_rarity_+%"] = 5,
            ["base_killed_monster_dropped_item_rarity_+%"] = 3,
            ["minion_damage_+%"] = 15,
        };
        var own = new Dictionary<string, int>
        {
            ["skill_area_of_effect_+%"] = 2, ["base_skill_area_of_effect_+%"] = 30,
            ["base_killed_monster_dropped_item_rarity_+%"] = 40,
            ["minion_damage_+%"] = 10, ["support_minion_damage_+%_final"] = 25,
        };
        var actorBefore = actor.ToArray(); var ownBefore = own.ToArray();
        var first = Selected(actor, own);
        Assert.Equal(62, first["skill_area_of_effect_+%"]);
        Assert.Equal(58, first["display_skill_increased_item_rarity_+%"]);
        Assert.Equal(25, first["virtual_minion_damage_+%"]);
        Assert.Equal(first.OrderBy(s => s.Key), Selected(actor, own).OrderBy(s => s.Key));
        Assert.Equal(actorBefore, actor.ToArray()); Assert.Equal(ownBefore, own.ToArray());
    }

    [Fact]
    public void Removal_and_signed_cancellation_publish_zero_instead_of_omitting_an_old_alias()
    {
        var actor = new Dictionary<string, int> { ["base_skill_area_of_effect_+%"] = 20 };
        var own = new Dictionary<string, int> { ["base_skill_area_of_effect_+%"] = -20 };
        var cancelled = Selected(actor, own);
        Assert.Contains("skill_area_of_effect_+%", cancelled.Keys);
        Assert.Contains("reduce_enemy_fire_resistance_%", cancelled.Keys);
        Assert.All(cancelled.Values, value => Assert.Equal(0, value));
        actor.Clear(); own.Clear();
        Assert.All(Selected(actor, own).Values, value => Assert.Equal(0, value));
    }

    [Fact]
    public void Opposing_large_raw_sources_cancel_before_any_integer_saturation()
    {
        var actor = new Dictionary<string, int>
        { ["skill_area_of_effect_+%"] = int.MaxValue, ["base_skill_area_of_effect_+%"] = int.MaxValue };
        var own = new Dictionary<string, int>
        { ["skill_area_of_effect_+%"] = -int.MaxValue, ["base_skill_area_of_effect_+%"] = -int.MaxValue };
        Assert.Equal(0, Selected(actor, own)["skill_area_of_effect_+%"]);
        own.Clear();
        Assert.Equal(int.MaxValue, Selected(actor, own)["skill_area_of_effect_+%"]);
        actor["skill_area_of_effect_+%"] = actor["base_skill_area_of_effect_+%"] = int.MinValue;
        Assert.Equal(int.MinValue, Selected(actor, own)["skill_area_of_effect_+%"]);
    }

    [Fact]
    public void Kill_specific_rarity_uses_the_same_raw_sum_for_character_and_selected_skill()
    {
        var actor = new Dictionary<string, int> { ["base_killed_monster_dropped_item_rarity_+%"] = 40 };
        var character = PanelStats.Compute(actor.GetValueOrDefault).ToDictionary(s => s.Stat, s => s.Value);
        Assert.Equal(40, character["display_skill_increased_item_rarity_+%"]);
        Assert.Equal(40, Selected(actor, new Dictionary<string, int>())["display_skill_increased_item_rarity_+%"]);
    }
}
