namespace GameServer.Gateway.Tests;

public sealed class HandPenetrationPanelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Elemental_prohibition_zeroes_all_nine_aliases_without_suppressing_chaos(bool onSkill)
    {
        var raw = new Dictionary<string, int>
        { ["reduce_enemy_elemental_resistance_%"] = 20, ["reduce_enemy_chaos_resistance_%"] = 11 };
        var own = new Dictionary<string, int> { ["base_reduce_enemy_fire_resistance_%"] = 10 };
        (onSkill ? own : raw)[Penetration.ElementalProhibition] = 1;
        var selected = PanelStats.Selected(raw.GetValueOrDefault, own.GetValueOrDefault,
            mainHand: _ => 6, offHand: _ => 9).ToDictionary(s => s.Stat, s => s.Value);
        var fields = selected.Where(p => Penetration.IsElementalDisplay(p.Key)).ToArray();
        Assert.Equal(9, fields.Length);
        Assert.All(fields, p => Assert.Equal(0, p.Value));
        Assert.Equal(17, selected["main_hand_attack_damage_penetrates_chaos_resist_%"]);
        if (!onSkill)
        {
            var actor = PanelStats.Compute(raw.GetValueOrDefault).ToDictionary(s => s.Stat, s => s.Value);
            Assert.Equal(9, actor.Count(p => Penetration.IsElementalDisplay(p.Key)));
            Assert.All(actor.Where(p => Penetration.IsElementalDisplay(p.Key)), p => Assert.Equal(0, p.Value));
        }
    }

    [Theory]
    [InlineData("fire")]
    [InlineData("cold")]
    [InlineData("lightning")]
    public void Attack_only_sources_add_to_both_hands_without_changing_spell_penetration(string element)
    {
        var raw = new Dictionary<string, int>
        {
            [$"base_reduce_enemy_{element}_resistance_%"] = 10,
            ["base_penetrate_elemental_resistances_%"] = 5,
            [$"base_attack_damage_penetrates_{element}_resist_%"] = 7,
            ["base_attack_damage_penetrates_elemental_resist_%"] = 3,
        };
        var panel = PanelStats.Compute(raw.GetValueOrDefault).ToDictionary(s => s.Stat, s => s.Value);
        Assert.Equal(25, panel[$"main_hand_attack_damage_penetrates_{element}_resist_%"]);
        Assert.Equal(25, panel[$"off_hand_attack_damage_penetrates_{element}_resist_%"]);
        Assert.Equal(15, panel[$"reduce_enemy_{element}_resistance_%"]);
    }

    [Fact]
    public void Chaos_attack_sources_combine_with_general_and_each_weapons_local_value()
    {
        var raw = new Dictionary<string, int>
        {
            ["reduce_enemy_chaos_resistance_%"] = 4,
            ["base_attack_damage_penetrates_chaos_resist_%"] = 13,
        };
        var panel = PanelStats.Compute(raw.GetValueOrDefault,
            name => name == "local_chaos_penetration_%" ? 6 : 0,
            name => name == "local_chaos_penetration_%" ? 9 : 0).ToDictionary(s => s.Stat, s => s.Value);
        Assert.Equal(23, panel["main_hand_attack_damage_penetrates_chaos_resist_%"]);
        Assert.Equal(26, panel["off_hand_attack_damage_penetrates_chaos_resist_%"]);
    }

    [Fact]
    public void Selected_hands_keep_distinct_local_sources_once_and_clear_removed_supports()
    {
        var raw = new Dictionary<string, int>
        {
            ["base_reduce_enemy_fire_resistance_%"] = 10,
            ["base_attack_damage_penetrates_elemental_resist_%"] = 3,
        };
        var own = new Dictionary<string, int> { ["base_reduce_enemy_fire_resistance_%"] = 20 };
        var main = new Dictionary<string, int> { ["local_fire_penetration_%"] = 6 };
        var off = new Dictionary<string, int> { ["local_fire_penetration_%"] = 9 };
        Dictionary<string, int> Read() => PanelStats.Selected(raw.GetValueOrDefault, own.GetValueOrDefault,
            mainHand: main.GetValueOrDefault, offHand: off.GetValueOrDefault).ToDictionary(s => s.Stat, s => s.Value);
        var panel = Read();
        Assert.Equal(39, panel["main_hand_attack_damage_penetrates_fire_resist_%"]);
        Assert.Equal(42, panel["off_hand_attack_damage_penetrates_fire_resist_%"]);
        Assert.Equal(30, panel["reduce_enemy_fire_resistance_%"]);
        Assert.Equal(panel.OrderBy(p => p.Key), Read().OrderBy(p => p.Key));
        own.Clear();
        Assert.Equal(19, Read()["main_hand_attack_damage_penetrates_fire_resist_%"]);
        main.Clear();
        Assert.Equal(13, Read()["main_hand_attack_damage_penetrates_fire_resist_%"]);
        Assert.Equal(22, Read()["off_hand_attack_damage_penetrates_fire_resist_%"]);
        raw.Clear(); off.Clear();
        Assert.Equal(0, Read()["main_hand_attack_damage_penetrates_fire_resist_%"]);
        Assert.Equal(0, Read()["off_hand_attack_damage_penetrates_fire_resist_%"]);
    }
}
