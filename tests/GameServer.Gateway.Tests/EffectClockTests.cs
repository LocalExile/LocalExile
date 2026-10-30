namespace GameServer.Gateway.Tests;

public class EffectClockTests
{
    [Fact]
    public void Native_received_clock_uses_category_masks_additive_inputs_floor_and_Temporal_Chains_exclusion()
    {
        int Get(string name) => name switch
        {
            "buff_time_passed_+%_only_buff_category" => -30,
            "debuff_time_passed_+%" => 100,
            "buff_time_passed_+%_other_than_temporal_chains" => -75,
            _ => 0,
        };
        foreach (int category in new[] { 1, 7, 13, 16 }) Assert.Equal(.25, EffectClock.RecipientRate(category, Get));
        foreach (int category in new[] { 2, 5, 11, 14 }) Assert.Equal(1.25, EffectClock.RecipientRate(category, Get));
        Assert.Equal(2, EffectClock.RecipientRate(5, Get, temporalChains: true));
        Assert.Equal(.25, EffectClock.RecipientRate(4, Get));
    }

    [Fact]
    public void Temporal_curse_packet_projects_base_budget_and_native_debuff_alias_once()
    {
        var clock = new EffectClock(4, 0, .7);
        var state = new TemporalChainsState(1, 0x103, clock, -15, -9, -25);
        var method = typeof(Program).GetMethod("TemporalBuff", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var packet = (byte[])method.Invoke(null, [0x1000u, state, 1d])!;
        float nativeBudget = BitConverter.UInt32BitsToSingle(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(23, 4)));
        Assert.Equal(3.3, nativeBudget, 6); // Native rate .7 consumes this in 3.3/.7 wall seconds.
        Assert.Equal(40d / 7, clock.Deadline, 8);
        var values = PanelStats.Compute(n => n == "debuff_time_passed_+%" ? 100 : 0).ToDictionary(p => p.Stat, p => p.Value);
        Assert.Equal(100, values["virtual_debuff_time_passed_+%"]);
    }

    [Fact]
    public void Received_flask_expiry_rate_changes_time_budget_without_scaling_recovery_DPS_or_instant_end()
    {
        var effects = new Flasks.Effects();
        effects.Start(new(0, 1, 0, 10, 20, 0, 4, ManaAtEnd: 100));
        var first = effects.Advance(0, 2, 1.5, .5);
        Assert.Equal(20, first.Life); Assert.Equal(60, first.Mana); Assert.Empty(first.Ended);
        var second = effects.Advance(2, 5, 1.5, 1); // Three base seconds remain when the curse source ends.
        Assert.Equal(30, second.Life); Assert.Equal(190, second.Mana); Assert.Single(second.Ended);
    }

    [Fact]
    public void Queued_flask_preserves_waiting_budget_and_pays_instant_end_exactly_once_after_rate_change()
    {
        var effects = new Flasks.Effects();
        effects.Start(new(0, 1, 0, 0, 20, 0, 2, ManaAtEnd: 100));
        effects.Start(new(1, 2, 0, 0, 10, 0, 2, ManaAtEnd: 50));
        Assert.Equal(40, effects.Advance(0, 2, 1, .5).Mana);
        var first = effects.Advance(2, 3, 1, 1);
        Assert.Equal(120, first.Mana); Assert.Equal((ushort)1, Assert.Single(first.Ended).Buff);
        var second = effects.Advance(3, 5, 1, 1);
        Assert.Equal(70, second.Mana); Assert.Equal((ushort)2, Assert.Single(second.Ended).Buff);
        Assert.Equal(0, effects.Advance(5, 10, 1.5, .5).Mana);
    }
    [Fact]
    public void Enduring_Cry_projection_uses_the_pinned_regular_buff_and_not_the_different_hidden_recovery_row()
    {
        if (!File.Exists("data/tables/BuffDefinitions.json")) return;
        using var rows = System.Text.Json.JsonDocument.Parse(File.ReadAllText("data/tables/BuffDefinitions.json"));
        Assert.Equal("enduring_cry", rows.RootElement[1541].GetProperty("Id").GetString());
        Assert.Equal(1, rows.RootElement[1541].GetProperty("BuffCategory").GetInt32());
        Assert.Equal("enduring_cry_life_regeneration", rows.RootElement[421].GetProperty("Id").GetString());
        Assert.Equal(18, rows.RootElement[421].GetProperty("BuffCategory").GetInt32());
        Assert.Equal(1, rows.RootElement[1090].GetProperty("BuffCategory").GetInt32());
        Assert.Equal(4, rows.RootElement[130].GetProperty("BuffCategory").GetInt32());
    }
    [Fact]
    public void Enduring_Cry_recovery_integrates_wall_time_at_unchanged_rate_across_expiry_refund_and_carry()
    {
        var recovery = new TimedLifeRecovery();
        recovery.Begin(0, 1, 100, .7);
        Assert.Equal(10d / 7, recovery.Until, 10);
        Assert.Equal(100, recovery.Rate(1));
        Assert.Equal(50, recovery.Take(.5));
        var carry = recovery.Carry(.5);
        Assert.Equal(.65, carry.Remaining, 10);
        var next = new TimedLifeRecovery(); next.Restore(carry, 100);
        Assert.Equal(100 + .65 / .7, next.Until, 10);
        next.SetExpiryRate(100.2, 1); // .14 base seconds elapsed, leaving .51.
        Assert.Equal(100.71, next.Until, 10);
        Assert.Equal(71, next.Take(110), 8); // 20 earned before refund + 51 after; no recovery-rate multiplier.
        Assert.Equal(0, next.Take(111));
    }

    [Fact]
    public void Adrenaline_category_one_uses_received_expiry_but_Turtle_recovery_retains_its_own_clock()
    {
        var state = new AntiquarianCombatState(); state.SetBuffExpiryRate(0, .7);
        Assert.True(state.GrantAdrenaline(0, 1000));
        state.Stunned(0, 1000, 5);
        Assert.Equal(10d / 7, state.AdrenalineUntil, 10);
        Assert.Equal(50, state.TakeRecovery(2)); // Category-18 recovery still lasts exactly one second.
        Assert.False(state.GrantAdrenaline(1, 1000));
        state.SetBuffExpiryRate(1, 1);
        Assert.Equal(1.3, state.AdrenalineUntil, 10);
        var next = new AntiquarianCombatState(); next.Restore(state.Carry(1.1), 100);
        Assert.Equal(100.2, next.AdrenalineUntil, 10);
    }

    [Fact]
    public void Actual_Atziri_variety_supplies_curse_reflection_without_importing_its_unrelated_innate_mods()
    {
        if (!File.Exists("data/tables/MonsterVarieties.json")) return;
        var atziri = Program.Monsters.Find("Metadata/Monsters/Atziri/Atziri")!;
        var monster = new SpawnedMonster("Atziri", new(0, 0, 1), 0, 1, 100) { Variety = atziri };
        Assert.Equal(1, Program.CurseSources.Get(monster, "reflect_curses"));
        Assert.Equal(0, Program.CurseSources.Get(monster, "monster_life_+%_final"));
    }

    [Fact]
    public void Small_DOT_ticks_preserve_fractional_thirty_percent_bypass_instead_of_rounding_it_away()
    {
        var shield = new EnergyShield(); shield.SetMaximum(100, true);
        double carry = 0;
        int life = 0;
        for (int i = 0; i < 100; i++) life += HarbingerDefences.DamageOverTime(shield, 1, 30, i, ref carry);
        Assert.Equal(30, life);
        Assert.Equal(30u, shield.Current);
        Assert.InRange(carry, 0, 1e-8);
    }

    [Fact]
    public void Curse_and_generic_increased_duration_add_before_more_and_recipient_modifiers()
    {
        int Player(string name) => name switch
        {
            "skill_effect_duration_+%" => 20,
            "curse_duration_+%" => 20,
            "self_curse_duration_+%" => 50,
            _ => 0,
        };
        Assert.Equal(7, TemporalChainsState.Duration(5000, Player, _ => 0, false), 8);
        Assert.Equal(10.5, TemporalChainsState.Duration(5000, Player, _ => 0, true), 8);
        var received = new EffectClock(10.5, 0, 2);
        Assert.Equal(5.25, received.Deadline, 8);
        Assert.Equal(9.1, TemporalChainsState.Duration(5000, Player,
            name => name == "support_more_duration_skill_effect_duration_+%_final" ? 30 : 0, false), 8);
    }

    [Fact]
    public void Slower_expiry_is_reciprocal_and_rate_changes_preserve_elapsed_effect_time()
    {
        var clock = new EffectClock(4, 0, .7);
        Assert.Equal(40d / 7, clock.Deadline, 10);
        clock.Advance(2);
        Assert.Equal(2.6, clock.Remaining, 10);
        Assert.Equal(40d / 7, clock.Deadline, 10);
        clock.ChangeRate(2, 1);
        Assert.Equal(4.6, clock.Deadline, 10);
        clock.ChangeRate(3, 2);
        Assert.Equal(3.8, clock.Deadline, 10);
        clock.Advance(3.8);
        Assert.InRange(clock.Remaining, 0, 1e-9);
    }

    [Fact]
    public void Zero_rate_freezes_remaining_budget_and_repeated_reads_do_not_drift_the_deadline()
    {
        var clock = new EffectClock(4, 0);
        clock.ChangeRate(1, 0);
        clock.Advance(100);
        Assert.Equal(3, clock.Remaining);
        Assert.Equal(double.PositiveInfinity, clock.Deadline);
        clock.ChangeRate(100, .7);
        double deadline = clock.Deadline;
        for (int i = 1; i < 200; i++) clock.Advance(100 + i / 100d);
        Assert.Equal(deadline, clock.Deadline);
    }

    [Theory]
    [InlineData(1, .7)] [InlineData(2, 2)] [InlineData(5, 2)] [InlineData(18, 1)]
    public void Pinned_buff_debuff_curse_and_other_categories_select_only_their_own_source(int category, double rate)
    {
        Assert.Equal(rate, EffectClock.RecipientRate(category, name => name switch
        {
            "buff_time_passed_+%_only_buff_category" => -30,
            "debuff_time_passed_+%" => 100,
            _ => 0,
        }));
    }

    [Fact]
    public void Fortification_expiry_changes_without_changing_recovery_rate_or_hit_throttle()
    {
        var state = new FortificationState();
        state.SetExpiryRate(0, .7);
        Assert.True(state.TryGrantFromHit(0, 5));
        Assert.False(state.TryGrantFromHit(.05, 5));
        Assert.Equal(60d / 7, state.Read(.05).NextExpiry!.Value, 10);
        state.SetExpiryRate(2, 1);
        Assert.Equal(6.6, state.Read(2).NextExpiry!.Value, 10);
        Assert.Equal(5, state.Read(6).Current);
        Assert.Equal(0, state.Read(6.6).Current);
        Assert.Equal(33, state.StackSeconds, 8);
    }

    [Fact]
    public void Mana_recovery_rate_multiplies_timed_flask_amount_only_and_preserves_instant_end_amount()
    {
        var effects = new Flasks.Effects();
        effects.Start(new Flasks.Effect(0, 1, 0, 10, 20, 0, 4, ManaAtEnd: 100));
        var first = effects.Advance(0, 2, 1.5);
        Assert.Equal(20, first.Life);
        Assert.Equal(60, first.Mana);
        var second = effects.Advance(2, 4, 1.5);
        Assert.Equal(20, second.Life);
        Assert.Equal(160, second.Mana);
        Assert.Single(second.Ended);
    }
}
