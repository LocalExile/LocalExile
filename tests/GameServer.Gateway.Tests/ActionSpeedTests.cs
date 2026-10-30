using System.Text.Json;

namespace GameServer.Gateway.Tests;

public class ActionSpeedTests
{
    static ActionSpeed Speed(int reduction = 0, int actionFloor = 0, int movementFloor = 0,
        int tailwind = 0, int effect = 0) => ActionSpeed.From(name => name switch
        {
            ActionSpeed.Reduction => reduction,
            ActionSpeed.ActionFloor => actionFloor,
            ActionSpeed.MovementFloorStat => movementFloor,
            ActionSpeed.HasTailwind => tailwind,
            ActionSpeed.TailwindEffect => effect,
            _ => 0,
        });

    [Theory]
    [InlineData(-10, 10, 1.1)]
    [InlineData(20, -20, .8)]
    [InlineData(100, -100, 0)]
    [InlineData(200, -100, 0)]
    [InlineData(0, 0, 1)]
    public void Signed_reduction_becomes_one_nonnegative_action_factor(int reduction, int percent, double factor)
    {
        var speed = Speed(reduction);
        Assert.Equal(percent, speed.Percent);
        Assert.Equal(factor, speed.Factor, 10);
    }

    [Theory]
    [InlineData(-8, 1, 50, 12)]
    [InlineData(-18, 1, 50, 22)]
    [InlineData(-10, 0, 50, 10)]
    [InlineData(-8, 1, 0, 8)]
    [InlineData(-8, 1, -50, 4)]
    [InlineData(-8, 1, -100, 0)]
    [InlineData(-8, 1, -200, 0)]
    public void Tailwind_effect_scales_only_the_active_eight_percent_contribution(int raw, int active, int effect, int expected)
    {
        Assert.Equal(expected, Speed(raw, tailwind: active, effect: effect).Percent);
    }

    [Theory]
    [InlineData(0, 0, .4)]
    [InlineData(1, 0, .5)]
    [InlineData(0, 1, .8)]
    [InlineData(1, 1, 1)]
    public void Floors_clamp_their_own_factor_before_movement_and_action_multiply(int actionFloor, int movementFloor, double expected)
    {
        var speed = Speed(20, actionFloor, movementFloor);
        Assert.Equal(expected, speed.MovementFactor(-50), 10);
        Assert.Equal(actionFloor == 1 ? 1 : .8, speed.Factor, 10);
    }

    [Fact]
    public void Movement_floor_does_not_remove_action_slow_and_action_floor_does_not_remove_movement_slow()
    {
        Assert.Equal(.8, Speed(20, movementFloor: 1).MovementFactor(-50), 10);
        Assert.Equal(.5, Speed(20, actionFloor: 1).MovementFactor(-50), 10);
        Assert.Equal(1.4, Speed(-12, movementFloor: 1).MovementFactor(25), 10);
        Assert.Equal(2, SkillAttackTiming.Seconds(1000, -50, 0, Speed(20, actionFloor: 1).Factor), 8);
        Assert.Equal(3750, SpellCastTiming.Calculate(750, -80, actionSpeed: Speed(20, actionFloor: 1).Factor).DurationMilliseconds);
    }

    [Fact]
    public void Real_node_and_buff_metadata_supply_the_pinned_flags_and_signed_base()
    {
        var tree = Program.Passives;
        var flags = tree.Stats([37852]);
        Assert.Equal("Toughened Skin", tree.Find(37852)!.Name);
        Assert.Equal(3444u, Program.Stats.Key(ActionSpeed.ActionFloor));
        Assert.Equal(3445u, Program.Stats.Key(ActionSpeed.MovementFloorStat));
        Assert.Equal(1, flags[3444]);
        Assert.Equal(1, flags[3445]);
        Assert.Equal(348u, Program.Stats.Key(ActionSpeed.Reduction));
        Assert.Equal(3302u, Program.Stats.Key(ActionSpeed.Virtual));
        Assert.Equal(7766u, Program.Stats.Key(ActionSpeed.TailwindEffect));
        Assert.Equal(7786u, Program.Stats.Key(ActionSpeed.HasTailwind));
        Assert.Equal(50, tree.Stats([14442])[7766]);
        // The integration owner supplies this flag in the active owner-buff path; never infer it from effect alone.
        using var definitions = JsonDocument.Parse(File.ReadAllBytes(Path.Combine("data", "tables", "BuffDefinitions.json")));
        Assert.Contains(7785, definitions.RootElement[1094].GetProperty("GrantedFlags").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal(347, definitions.RootElement[922].GetProperty("StatsKeys")[0].GetInt32());
    }

    static Dictionary<uint, int> Stats(params (string Name, int Value)[] values) => values.ToDictionary(
        x => Program.Stats.Key(x.Name)!.Value, x => x.Value);

    [Theory]
    [InlineData(-10, 1.1)]
    [InlineData(20, .8)]
    public void Real_attack_cast_and_native_stat_consumers_multiply_their_own_increases_once(int reduction, double action)
    {
        var stats = Stats((ActionSpeed.Reduction, reduction), ("main_hand_attack_speed_+%", 100), ("cast_speed_+%", 100));
        var weapon = new SkillCatalog.Unarmed(1000, 10, 20, 8);
        Assert.Equal(.5 / action, weapon.AttackSeconds(stats), 8);
        int burning = Program.Skills.EffectRow("BurningArrow");
        Assert.Equal(.7145 / action, SkillAttackTiming.ForSkill(Program.Skills, burning, 1, weapon, stats), 8);

        var native = new Dictionary<uint, int>(stats);
        foreach (var (key, value) in weapon.Stats(stats)) native[key] = value;
        SkillAttackTiming.Apply(Program.Skills, burning, 1, native);
        Assert.Equal(1000, native[StatKeys.MainHandAttackDurationMs]);
        Assert.Equal(1429, native[StatKeys.MainHandTotalAttackDurationMs]);
        Assert.Equal(100, native[StatKeys.MainHandAttackSpeedPct]);
        Assert.Equal((int)Math.Round(100 / (.7145 / action)), native[StatKeys.HundredTimesAttacksPerSecond]);

        int fireball = Program.Skills.EffectRow("Fireball");
        var spell = stats;
        Assert.Equal(750, Program.Skills.CastMs(fireball));
        Assert.Equal(100, spell[Program.Stats.Key("cast_speed_+%")!.Value]);
        Assert.Equal((int)Math.Round(375 / action), SpellCastTiming.Calculate(750, 100, actionSpeed: action).DurationMilliseconds);
        double attackSeconds = SkillAttackTiming.ForSkill(Program.Skills, burning, 1, weapon, stats);
        Assert.Equal(attackSeconds * .374, AttackTiming.ContactDelay(attackSeconds, .374, 0), 8);
        Assert.True(AttackTiming.Ready(attackSeconds * .75, attackSeconds, attackSeconds));
        Assert.False(AttackTiming.Ready(attackSeconds * .74, attackSeconds, attackSeconds));
    }

    [Fact]
    public void Published_virtual_action_value_prevents_reapplying_raw_Tailwind_and_Harbinger_inputs()
    {
        var stats = Stats((ActionSpeed.Reduction, -18), (ActionSpeed.HasTailwind, 1), (ActionSpeed.TailwindEffect, 50),
            (ActionSpeed.Virtual, 22), ("main_hand_attack_speed_+%", 100), ("cast_speed_+%", 100));
        Assert.Equal(1.22, ActionSpeed.Multiplier(stats, Program.Stats.Key), 8);
        Assert.Equal(.5 / 1.22, new SkillCatalog.Unarmed(1000, 10, 20, 8).AttackSeconds(stats), 8);
        Assert.Equal(307, SpellCastTiming.Calculate(750, 100, actionSpeed: 1.22).DurationMilliseconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Impossible_action_factors_refuse_noninstant_timing_but_keep_instant_spells_instant(double factor)
    {
        Assert.Equal(double.PositiveInfinity, SkillAttackTiming.Seconds(1000, 0, 0, factor));
        Assert.Null(SpellCastTiming.Calculate(750, 0, actionSpeed: factor).DurationMilliseconds);
        Assert.Equal(0, SpellCastTiming.Calculate(750, -100, instant: true, actionSpeed: factor).DurationMilliseconds);
    }

}
