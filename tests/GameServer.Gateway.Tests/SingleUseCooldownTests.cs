using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class SingleUseCooldownTests
{
    static string Data() => BenchCraftingTests.Data();
    static readonly Lazy<SkillCatalog> Skills = new(() => new(Data()));
    static readonly Lazy<StatCatalog> Stats = new(() => new(Data()));
    static Dictionary<uint, int> Values(params (string Name, int Value)[] values) =>
        values.ToDictionary(v => Stats.Value.Key(v.Name)!.Value, v => v.Value);
    static Dictionary<uint, int> Own(string name, int quality = 0) => Skills.Value
        .EffectStats(Skills.Value.EffectRow(name), 1, quality).ToDictionary(s => s.Item1, s => s.Item2);
    static SingleUseCooldown? Plan(string name, Dictionary<uint, int>? player = null,
        Dictionary<uint, int>? own = null) => SingleUseCooldown.ForSkill(Skills.Value, Stats.Value,
            Skills.Value.EffectRow(name), 1, player ?? [], own ?? Own(name));
    static ClientState Connection() => new(new AreaLayout(1, "cooldown-test", 500, 500, 10, 10, null, []), 1, 1)
        { PlayerLife = 100, PlayerMana = 100 };

    [Theory]
    [InlineData(0, 2.5)]
    [InlineData(100, 1.25)]
    [InlineData(-50, 5)]
    public void Dash_retains_native_base_and_consumes_base_time_at_the_final_rate(int recovery, double seconds)
    {
        var plan = Plan("Dash", Values(("virtual_cooldown_speed_+%", recovery)))!.Value;
        Assert.Equal(2500, plan.BaseMilliseconds);
        Assert.Equal(recovery, plan.RecoveryPercent);
        var own = Own("Dash");
        own[Stats.Value.Key("virtual_cooldown_speed_+%")!.Value] = plan.RecoveryPercent;
        var native = Skills.Value.FindByRow(0x103, Skills.Value.EffectRow("Dash"), 1,
            own.Select(v => (v.Key, v.Value)).ToArray())!;
        Assert.Equal(2500u, native.CooldownMs); // Native receives base, never 1250 at +100%.
        var state = new SingleUseCooldownState(0, plan.RecoveryRate);
        Assert.True(state.TryUse(0, plan));
        Assert.Equal(seconds, state.RemainingSeconds, 6);
        Assert.False(state.TryUse(seconds - 0.01, plan));
        Assert.True(state.TryUse(seconds, plan));
    }

    [Fact]
    public void Player_computed_total_and_skill_quality_are_added_once_without_counting_player_base_again()
    {
        var player = Values(("base_cooldown_speed_+%", 100));
        int Get(string n) => Stats.Value.Key(n) is uint k ? player.GetValueOrDefault(k) : 0;
        foreach (var (name, value) in PanelStats.Compute(Get))
            if (Stats.Value.Key(name) is uint key) player[key] = value;
        Assert.Equal(100, player[Stats.Value.Key("virtual_cooldown_speed_+%")!.Value]);
        var own = Own("Dash", quality: 20);
        Assert.Equal(10, own[Stats.Value.Key("base_cooldown_speed_+%")!.Value]);
        own[Stats.Value.Key("virtual_cooldown_speed_+%")!.Value] = 25;
        Assert.Equal(135, Plan("Dash", player, own)!.Value.RecoveryPercent);
    }

    [Theory]
    [InlineData("Haste", 1200)]
    [InlineData("RighteousFire", 300)]
    [InlineData("Frostblink", 3000)]
    [InlineData("VigilantStrike", 4000)]
    public void Catalog_single_use_spells_resolve_generic_recovery_without_runtime_protocol_claims(string id, int ms)
    {
        Assert.Equal(ms, Plan(id)!.Value.BaseMilliseconds);
        Assert.Equal(1, Skills.Value.CooldownUses(Skills.Value.EffectRow(id), 1));
    }

    [Theory]
    [InlineData("Fireball")]
    [InlineData("FlameDash")]
    [InlineData("EnduringCry")]
    [InlineData("NoSuchEffect")]
    public void No_cooldown_multi_use_and_unclassified_actions_keep_the_legacy_path(string id) => Assert.Null(Plan(id));

    [Theory]
    [InlineData("base_added_cooldown_count", 1)]
    [InlineData("virtual_total_cooldown_count", 2)]
    [InlineData("skill_cooldown_time_override_ms", 1000)]
    [InlineData("virtual_cooldown_modifier_ms", -100)]
    [InlineData("base_cooldown_speed_+%_final", 20)]
    public void Unsupported_cooldown_mechanics_do_not_enter_the_single_use_state(string stat, int value)
    {
        Assert.Null(Plan("Dash", Values((stat, value))));
        var own = Own("Dash");
        own[Stats.Value.Key(stat)!.Value] = value;
        Assert.Null(Plan("Dash", own: own));
    }

    [Fact]
    public void Mid_cooldown_rate_changes_settle_the_old_rate_before_installing_the_new_rate()
    {
        var state = new SingleUseCooldownState(0, 1);
        Assert.True(state.TryUse(0, new(2500, 0)));
        state.Refresh(0.5, 2);
        Assert.Equal(2000, state.RemainingBaseMilliseconds, 6);
        Assert.Equal(1, state.RemainingSeconds, 6);
        state.Refresh(1, 0.5);
        Assert.Equal(1000, state.RemainingBaseMilliseconds, 6);
        Assert.Equal(2, state.RemainingSeconds, 6);
        Assert.False(state.TryUse(2.9, new(2500, -50)));
        Assert.True(state.TryUse(3, new(2500, -50)));
    }

    [Fact]
    public void Paused_game_time_does_not_consume_budget_and_rate_can_change_while_paused()
    {
        var state = new SingleUseCooldownState(0, 1);
        state.TryUse(0, new(2500, 0));
        state.Refresh(0.5, 1);
        state.Refresh(0.5, 2); // Clock is stopped throughout the wall-time pause.
        state.Refresh(0.5, 2);
        Assert.Equal(2000, state.RemainingBaseMilliseconds, 6);
        Assert.Equal(1, state.RemainingSeconds, 6);
        state.Refresh(1.5, 2);
        Assert.True(state.Ready);
    }

    [Fact]
    public void Zero_rate_stalls_a_spent_use_and_a_later_positive_rate_can_resume_it()
    {
        var state = new SingleUseCooldownState(0, 0);
        Assert.True(state.TryUse(0, new(2500, -100)));
        state.Refresh(10, 0);
        Assert.Equal(2500, state.RemainingBaseMilliseconds);
        Assert.Equal(double.PositiveInfinity, state.RemainingSeconds);
        Assert.False(state.TryUse(10, new(2500, -100)));
        state.Refresh(10, 2);
        Assert.True(state.TryUse(11.25, new(2500, 100)));
    }

    [Fact]
    public void Extreme_recovery_inputs_cannot_overflow_native_percentage_plus_one_hundred()
    {
        var own = Own("Dash");
        own[Stats.Value.Key("base_cooldown_speed_+%")!.Value] = int.MaxValue;
        Assert.Equal(int.MaxValue - 100, Plan("Dash",
            Values(("virtual_cooldown_speed_+%", int.MaxValue)), own)!.Value.RecoveryPercent);
        Assert.Equal(-100, Plan("Dash", Values(("virtual_cooldown_speed_+%", int.MinValue)))!.Value.RecoveryPercent);
    }

    [Fact]
    public void Raw_early_retry_rejects_before_movement_action_echo_mana_or_channel_side_effects()
    {
        var conn = Connection();
        int effect = 10619, echoes = 0;
        conn.ChannelSkill = 0x123;
        conn.SingleUseCooldowns[effect] = new(conn.Clock.Elapsed.TotalSeconds, 2, 1900);
        bool accepted = Program.TryBeginCooldownUse(conn, effect, new(2500, 100), false, () =>
        {
            conn.ActionCount++;
            conn.Walk.Teleport(300, 220, conn.Clock.Elapsed.TotalSeconds);
            conn.ChannelSkill = 0;
            conn.PlayerMana -= 4;
            echoes++;
        }, out var ticket);
        Assert.False(accepted);
        Assert.Null(ticket);
        Assert.Equal(0, conn.ActionCount);
        Assert.Equal((10, 10), conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds));
        Assert.Equal((ushort)0x123, conn.ChannelSkill);
        Assert.Equal(100u, conn.PlayerMana);
        Assert.Equal(0, echoes);
    }

    [Fact]
    public async Task Simultaneous_requests_have_one_reservation_and_one_action_start()
    {
        var conn = Connection();
        int starts = 0;
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(index => Task.Run(() =>
            Program.TryBeginCooldownUse(conn, 10619, new(2500, 0), false, () => starts++, out _))));
        Assert.Single(results, r => r);
        Assert.Equal(1, starts);
    }

    [Fact]
    public void Queued_echo_and_effect_keep_the_original_token_and_cancellation_does_not_refund()
    {
        var conn = Connection();
        Assert.True(Program.TryBeginCooldownUse(conn, 10619, new(2500, 100), false,
            () => conn.ActionCount++, out var ticket));
        Assert.True(ticket!.Current(conn));
        conn.ActionCount++; // New action wins while the accepted request awaits the send gate.
        Assert.False(ticket.Current(conn));
        Assert.False(ticket.TryExecute(conn, 10619));
        Assert.False(Program.TryBeginCooldownUse(conn, 10619, new(2500, 100), false,
            () => throw new Exception("No second action may start"), out _));
    }

    [Fact]
    public void One_ticket_executes_once_and_never_pays_for_another_effect()
    {
        var conn = Connection();
        Program.TryBeginCooldownUse(conn, 10619, new(2500, 0), false, () => conn.ActionCount++, out var ticket);
        Assert.False(ticket!.TryExecute(conn, 10656));
        Assert.True(ticket.TryExecute(conn, 10619));
        Assert.False(ticket.TryExecute(conn, 10619));
    }

    [Fact]
    public void Instant_use_preserves_current_action_but_transfer_invalidates_its_original_epoch()
    {
        var conn = Connection();
        Program.TryBeginCooldownUse(conn, 10619, new(2500, 0), true, () => { }, out var ticket);
        conn.ActionCount++;
        Assert.True(ticket!.Current(conn));
        Assert.True(conn.TryBeginAreaChange(new(2, 0, false), out _));
        conn.PendingTransfer = null;
        conn.ResumeAfterFailedTransfer();
        Assert.False(ticket.Current(conn));
        Assert.False(ticket.TryExecute(conn, 10619));
    }

    [Fact]
    public void Approach_does_not_spend_a_use_and_arrival_cannot_adopt_a_replacement_action()
    {
        var conn = Connection();
        Assert.True(Program.TryBeginCooldownUse(conn, 10619, new(2500, 0), false,
            () => conn.ActionCount++, out var approach, reserve: false));
        Assert.False(approach!.TryExecute(conn, 10619));
        Assert.True(conn.SingleUseCooldowns[10619].Ready);
        conn.ActionCount++;
        Assert.False(Program.TryBeginCooldownUse(conn, 10619, new(2500, 0), false,
            () => throw new Exception("Old arrival cannot start"), out _, actionToken: approach.ActionToken));
        Assert.True(conn.SingleUseCooldowns[10619].Ready);
    }

    [Fact]
    public void Paused_preflight_cannot_spend_or_replace_the_current_action()
    {
        var conn = Connection();
        conn.Clock.Stop();
        Assert.False(Program.TryBeginCooldownUse(conn, 10619, new(2500, 0), false,
            () => throw new Exception("Paused input cannot start"), out _));
        Assert.Empty(conn.SingleUseCooldowns);
        Assert.Equal(0, conn.ActionCount);
    }
}
