namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    [Fact]
    public async Task Toughened_Skin_real_allocation_and_paid_refund_clamp_only_their_respective_factors()
    {
        Assert.True(DataAvailable());
        using var game = new GatewayCharacter(classRow: 0, baseClass: 1, choice: "Ancestral Commander", refundPoints: 60);
        await game.Command("/set_stat action_speed_-% 20");
        await game.Command("/set_stat movement_velocity_+% -50");
        Assert.Equal(-20, game.PlayerStat(ActionSpeed.Virtual));
        Assert.Equal(-50, game.PlayerStat("movement_velocity_+%"));
        Assert.Equal(14.8, game.State.Walk.Speed, 6);
        int fireball = Program.Skills.EffectRow("Fireball");
        int? slowCast = Program.Skills.CastTimeMs(fireball, 1, 0, Program.CharacterPlayerStats(game.State));
        Assert.Equal(938, slowCast);

        var definition = Program.Ascendancies.Find(game.State.MainAscendancy.Selection!)!;
        Assert.Null((await game.Change(PathTo(definition, 37852), allocate: true)).Error);
        Assert.Equal(1, game.PlayerStat(ActionSpeed.ActionFloor));
        Assert.Equal(1, game.PlayerStat(ActionSpeed.MovementFloorStat));
        Assert.Equal(0, game.PlayerStat(ActionSpeed.Virtual));
        Assert.Equal(0, game.PlayerStat("movement_velocity_+%"));
        Assert.Equal(37, game.State.Walk.Speed, 6);
        double allocatedAttack = Program.SkillAttackSeconds(game.State, -1, 1);
        Assert.Equal(750, Program.Skills.CastTimeMs(fireball, 1, 0, Program.CharacterPlayerStats(game.State)));

        ushort refunds = game.State.RefundPoints;
        Assert.Null((await game.Change([37852], allocate: false, refundPoints: true)).Error);
        Assert.Equal(refunds - 5, game.State.RefundPoints);
        Assert.Equal(0, game.PlayerStat(ActionSpeed.ActionFloor));
        Assert.Equal(0, game.PlayerStat(ActionSpeed.MovementFloorStat));
        Assert.Equal(-20, game.PlayerStat(ActionSpeed.Virtual));
        Assert.Equal(-50, game.PlayerStat("movement_velocity_+%"));
        Assert.Equal(14.8, game.State.Walk.Speed, 6);
        Assert.Equal(allocatedAttack / .8, Program.SkillAttackSeconds(game.State, -1, 1), 8);
        Assert.Equal(slowCast, Program.Skills.CastTimeMs(fireball, 1, 0, Program.CharacterPlayerStats(game.State)));
    }

    [Fact]
    public async Task Greatwolfs_Call_real_owner_marker_applies_only_the_extra_Tailwind_effect_and_refunds_cleanly()
    {
        Assert.True(DataAvailable());
        using var game = new GatewayCharacter(classRow: 3, baseClass: 2, choice: "Wildspeaker", refundPoints: 60);
        Assert.Equal(0, game.PlayerStat(ActionSpeed.HasTailwind));
        var definition = Program.Ascendancies.Find(game.State.MainAscendancy.Selection!)!;
        double baseline = Program.SkillAttackSeconds(game.State, -1, 1);
        Assert.Null((await game.Change(PathTo(definition, 14442), allocate: true)).Error);
        Assert.Equal(1, game.PlayerStat(ActionSpeed.HasTailwind));
        Assert.Equal(-8, game.PlayerStat(ActionSpeed.Reduction));
        Assert.Equal(50, game.PlayerStat(ActionSpeed.TailwindEffect));
        Assert.Equal(12, game.PlayerStat(ActionSpeed.Virtual));
        Assert.Equal(3, game.PlayerStat("movement_velocity_+%"));
        Assert.Equal(42.6832, game.State.Walk.Speed, 6); // The connected path also grants 3% movement speed.
        // The path also has ordinary attack speed; the independent action factor applies to that final speed once.
        var player = Program.CharacterPlayerStats(game.State);
        player[Program.Stats.Key(ActionSpeed.Virtual)!.Value] = 0;
        double withoutAction = Program.Skills.UnarmedFor(game.State.ClassRow).AttackSeconds(player);
        Assert.Equal(withoutAction / 1.12, Program.SkillAttackSeconds(game.State, -1, 1), 8);
        Assert.Null((await game.Change([14442], allocate: false, refundPoints: true)).Error);
        Assert.Equal(0, game.PlayerStat(ActionSpeed.HasTailwind));
        Assert.Equal(0, game.PlayerStat(ActionSpeed.Reduction));
        Assert.Equal(0, game.PlayerStat(ActionSpeed.Virtual));
        Assert.Equal(38.11, game.State.Walk.Speed, 6);
        Assert.True(Program.SkillAttackSeconds(game.State, -1, 1) <= baseline);
    }

    [Fact]
    public async Task Impossible_cast_is_refused_before_cost_and_cooldown_while_instant_cooldown_keeps_real_time()
    {
        Assert.True(DataAvailable());
        using var game = new GatewayCharacter(level: 24, god: true);
        EquipBlueGems(game, "Fireball", "Haste");
        await game.Command("/set_stat cast_speed_+% -100");
        uint mana = game.State.PlayerMana;
        Assert.False((await game.Cast("Fireball")).Accepted);
        Assert.Equal(mana, game.State.PlayerMana);
        Assert.Empty(game.State.SkillReadyAt);

        await game.Command("/set_stat action_speed_-% -100");
        int haste = Program.Skills.EffectRow("Haste");
        Assert.Equal(1200, Program.Skills.CooldownMs(haste, 1));
        double before = game.State.Clock.Elapsed.TotalSeconds;
        Assert.True((await game.Cast("Haste")).Accepted);
        double after = game.State.Clock.Elapsed.TotalSeconds;
        Assert.InRange(game.State.SkillReadyAt[haste], before + 1.2, after + 1.2);
        Assert.Equal(0, Program.Skills.CastTimeMs(haste, 1, 0, Program.CharacterPlayerStats(game.State)));
    }
}
