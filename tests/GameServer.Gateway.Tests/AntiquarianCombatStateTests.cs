namespace GameServer.Gateway.Tests;

public class AntiquarianCombatStateTests
{
    [Fact]
    public void Real_monster_Melee_projectile_umbrella_does_not_make_the_hit_ranged()
    {
        if (!File.Exists("data/tables/ActiveSkills.json")) return;
        int melee = Program.Skills.EffectRow("Melee");
        Assert.True(Program.Skills.IsProjectile(melee));
        Assert.True(Program.Skills.IsMelee(melee));
        Assert.False(Program.Skills.IsRangedAttack(melee));
        if (!File.Exists("data/tables/MonsterVarieties.json")) return;
        Assert.False(Program.MonsterAttackIsRanged(Program.Monsters.Find("Metadata/Monsters/Zombies/ZombieBite")!));
        Assert.True(Program.MonsterAttackIsRanged(Program.Monsters.Find("Metadata/Monsters/Skeletons/SkeletonBow")!));
    }
    [Fact]
    public void Adrenaline_has_one_deadline_and_source_death_and_transfer_are_explicit()
    {
        var state = new AntiquarianCombatState();
        Assert.True(state.GrantAdrenaline(10, 20000));
        Assert.False(state.GrantAdrenaline(15, 20000));
        Assert.True(state.Adrenaline(29.999));
        Assert.False(state.Adrenaline(30));
        state.GrantAdrenaline(40, 20000);
        var carry = state.Carry(43);
        var next = new AntiquarianCombatState(); next.Restore(carry, 0);
        Assert.True(next.Adrenaline(16.999)); Assert.False(next.Adrenaline(17));
        next.Observe(1, false, true, true); Assert.False(next.Adrenaline(1));
        state.Observe(44, true, true, false); Assert.False(state.Adrenaline(44));
    }

    [Fact]
    public void Turtle_recovery_integrates_exactly_once_across_refresh_removal_and_transfer()
    {
        var state = new AntiquarianCombatState();
        state.Stunned(10, 1000, 8);
        Assert.Equal(20, state.TakeRecovery(10.25));
        state.Stunned(10.5, 1000, 8);
        Assert.Equal(20, state.TakeRecovery(10.5));
        Assert.Equal(40, state.TakeRecovery(11));
        var carry = state.Carry(11.25);
        var next = new AntiquarianCombatState(); next.Restore(carry, 0);
        Assert.Equal(40, next.TakeRecovery(1)); // 20 earned before transfer plus 20 after arrival.
        Assert.Equal(0, next.TakeRecovery(2));
        state.Observe(11.25, true, false, true);
        Assert.Equal(20, state.TakeRecovery(12)); // earned before source removal, no later recovery.
        state.Stunned(20, 1000, 8); state.Clear(); Assert.Equal(0, state.TakeRecovery(21));
    }

    [Theory]
    [InlineData(0, .8)] [InlineData(10, .8)] [InlineData(17.5, .9)]
    [InlineData(25, 1)] [InlineData(47.5, 1.3)] [InlineData(70, 1.6)] [InlineData(100, 1.6)]
    public void Far_shot_has_the_pinned_distance_ramp(double distance, double multiplier) =>
        Assert.Equal(multiplier, AntiquarianEffects.FarShot(distance), 8);

    [Fact]
    public void Turtle_base_recovery_reduces_real_stun_duration_and_intelligence_zero_wins_last()
    {
        Assert.Equal(.066f, PlayerHitRecovery.Duration(n => n == "stun_recovery_+%" ? 1000 : 0));
        var attributes = AttributeModifiers.From(n => n switch
        { "base_strength" => 200, "intelligence_is_0" => 1, "base_intelligence" => 1000, "dexterity_+%" => -50, _ => 0 });
        Assert.Equal((232, 7, 0), attributes.Apply(32, 14, 14));
        Assert.Equal((int.MaxValue, 0, 0), new AttributeModifiers(Strength: long.MaxValue,
            Intelligence: long.MaxValue, ZeroIntelligence: true).Apply());
    }

    [Fact]
    public void Paid_skill_recovery_and_duration_supports_share_one_frozen_interval()
    {
        double duration = SkillDurationModifiers.Seconds(2500, name => name == "skill_effect_duration_+%" ? 27 : 0,
            name => name == "support_more_duration_skill_effect_duration_+%_final" ? 40 : 0);
        Assert.Equal(4.445, duration, 8);
        var effect = new TimedLifeRecovery(); effect.Begin(10, duration, 100);
        Assert.Equal(100, effect.Take(11));
        var next = new TimedLifeRecovery(); next.Restore(effect.Carry(12), 0);
        Assert.Equal(344.5, next.Take(10), 6);
        Assert.Equal(0, next.Take(20)); next.Clear(); Assert.Equal(0, next.Rate(0));
    }
}
