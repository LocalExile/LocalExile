namespace GameServer.Gateway.Tests;

public class PlayerHitRecoveryTests
{
    static Func<string, int> Stats(params (string Key, int Value)[] values) =>
        name => values.FirstOrDefault(v => v.Key == name).Value;

    [Theory]
    [InlineData(0, 0)]
    [InlineData(8, 0)]
    [InlineData(9, 22.5)]
    [InlineData(20, 50)]
    [InlineData(40, 100)]
    [InlineData(1000, 100)]
    public void Physical_melee_chance_has_a_minimum_and_caps_at_100(int damage, double chance) =>
        Assert.Equal(chance, PlayerHitRecovery.Chance(damage, 100, Stats()));

    [Fact]
    public void Threshold_and_avoidance_are_separate_from_the_minimum_chance()
    {
        Assert.Equal(25, PlayerHitRecovery.Chance(20, 100, Stats(("stun_threshold_+%", 100))));
        Assert.Equal(12.5, PlayerHitRecovery.Chance(20, 100,
            Stats(("stun_threshold_+%", 100), ("avoid_stun_%", 50))));
        Assert.Equal(0, PlayerHitRecovery.Chance(20, 100, Stats(("stun_threshold_+%", -100))));
    }

    [Theory]
    [InlineData("cannot_be_stunned", 1)]
    [InlineData("cannot_be_stunned_by_attacks", 1)]
    [InlineData("cannot_be_stunned_by_hits_of_only_physical_damage", 1)]
    [InlineData("avoid_stun_%", 100)]
    [InlineData("maximum_energy_shield", 1)]
    [InlineData("base_maximum_energy_shield", 1)]
    [InlineData("maximum_life_is_one", 1)]
    [InlineData("stun_threshold_based_on_%_mana_instead_of_life", 100)]
    [InlineData("additional_stun_threshold_based_on_%_of_energy_shield", 30)]
    public void Known_immunity_and_unsupported_pools_do_not_use_ordinary_life_rules(string key, int value) =>
        Assert.Equal(0, PlayerHitRecovery.Chance(1000, 100, Stats((key, value))));

    [Fact]
    public void Energy_shield_sources_are_excluded_without_needing_a_computed_final_pool()
    {
        Assert.True(PlayerHitRecovery.EnergyShieldPresent(Stats(("base_maximum_energy_shield", 17))));
        Assert.True(PlayerHitRecovery.EnergyShieldPresent(Stats(), equippedBase: 4));
        Assert.False(PlayerHitRecovery.EnergyShieldPresent(Stats()));
    }

    [Fact]
    public void Authoritative_energy_shield_state_overrides_raw_sources_for_depleted_or_converted_pools()
    {
        var raw = Stats(("maximum_energy_shield", 100), ("base_maximum_energy_shield", 50));
        Assert.Equal(0, PlayerHitRecovery.Chance(40, 100, raw, currentEnergyShield: 1));
        Assert.Equal(100, PlayerHitRecovery.Chance(40, 100, raw, currentEnergyShield: 0));
    }

    [Fact]
    public void Conditional_defences_require_their_own_state_before_enabling_recovery()
    {
        Assert.True(PlayerHitRecovery.UnsupportedStat("cannot_be_stunned_when_on_low_life"));
        Assert.True(PlayerHitRecovery.UnsupportedStat("avoid_stun_%_while_channelling"));
        Assert.True(PlayerHitRecovery.UnsupportedStat("stun_threshold_+%_per_rage"));
        Assert.True(PlayerHitRecovery.UnsupportedStat("stun_threshold_+"));
        Assert.True(PlayerHitRecovery.UnsupportedStat("stun_threshold_increased_by_overcapped_fire_resistance"));
        Assert.True(PlayerHitRecovery.UnsupportedStat("avoid_stun_35%_per_active_herald"));
        Assert.True(PlayerHitRecovery.UnsupportedStat("enchantment_boots_stun_avoid_%_on_kill"));
        Assert.True(PlayerHitRecovery.UnsupportedStat("skill_cannot_be_stunned_before_contact_point"));
        Assert.True(PlayerHitRecovery.UnsupportedStat("stun_recovery_+%_per_frenzy_charge"));
        Assert.False(PlayerHitRecovery.UnsupportedStat("cannot_be_stunned_by_spells"));
        Assert.False(PlayerHitRecovery.UnsupportedStat("avoid_stun_%"));
        Assert.False(PlayerHitRecovery.UnsupportedStat("base_avoid_stun_%"));
    }

    [Fact]
    public void Recovery_uses_the_monster_melee_base_and_player_recovery_then_rounds_up_to_a_tick()
    {
        Assert.Equal(.429f, PlayerHitRecovery.Duration(Stats()));
        Assert.Equal(.231f, PlayerHitRecovery.Duration(Stats(("stun_recovery_+%", 100))));
        Assert.Equal(.858f, PlayerHitRecovery.Duration(Stats(("stun_duration_on_self_+%", 100))));
        Assert.Equal(0, PlayerHitRecovery.Duration(Stats(("stun_recovery_+%", -100))));
        Assert.Equal(0, PlayerHitRecovery.Duration(Stats(("stun_duration_on_self_+%", int.MaxValue))));
    }

    [Fact]
    public void An_active_recovery_does_not_restart_or_extend_and_expiry_allows_the_next_one()
    {
        var recovery = new PlayerHitRecovery();
        Assert.True(recovery.Begin(1, .5f));
        Assert.False(recovery.Begin(1.4, .5f));
        Assert.Equal(1.5, recovery.Until);
        Assert.False(recovery.Active(1.5));
        Assert.True(recovery.Begin(1.5, .5f));
    }

    [Fact]
    public void Buffered_actions_from_before_or_during_recovery_cannot_restart_after_expiry()
    {
        var recovery = new PlayerHitRecovery(); recovery.Begin(1, .5f);
        Assert.True(recovery.BlocksInput(.9, 1.1));
        Assert.True(recovery.BlocksInput(1.4, 2));
        Assert.False(recovery.BlocksInput(1.5, 2));
        recovery.Clear();
        Assert.False(recovery.BlocksInput(1, 2));
    }

    [Fact]
    public void Invalid_duration_cannot_lock_input()
    {
        var recovery = new PlayerHitRecovery();
        foreach (float seconds in new[] { 0, -1, float.NaN, float.PositiveInfinity })
            Assert.False(recovery.Begin(0, seconds));
        Assert.False(recovery.Active(0));
    }

    static ClientState ObjectWalkState()
    {
        var conn = new ClientState(new AreaLayout(0, "test", 100, 100, 30, 30, null, []), 0, 1)
        { PlayerLife = 57, ActionCount = 4, ChannelSkill = 123 };
        conn.Clock.Reset();
        return conn;
    }

    [Fact]
    public void Object_walk_rechecks_recovery_before_replacing_the_token_or_moving()
    {
        var conn = ObjectWalkState();
        var before = conn.Walk.Position(0);
        conn.HitRecovery.Begin(0, .5f);
        Assert.False(global::Program.TryBeginObjectWalk(conn, 0, 70, 30, 2, out _));
        Assert.Equal(4, conn.ActionCount);
        Assert.Equal((ushort)123, conn.ChannelSkill);
        Assert.Equal(before, conn.Walk.Position(1));
        conn.HitRecovery.Clear();
        Assert.True(global::Program.TryBeginObjectWalk(conn, 0, 70, 30, 2, out var walk));
        Assert.Equal(5, walk.Action);
        Assert.Equal((ushort)0, conn.ChannelSkill);
        Assert.True(walk.Arrive > walk.Now);
    }

    [Fact]
    public void An_old_dispatched_object_walk_cannot_acquire_a_new_token_after_recovery_expired()
    {
        var conn = ObjectWalkState();
        // Shift the synthetic timeline below zero so its recovery has expired at the stopped clock's zero.
        conn.HitRecovery.Begin(-1, .5f);
        Assert.False(global::Program.TryBeginObjectWalk(conn, -.75, 70, 30, 2, out _));
        Assert.Equal(4, conn.ActionCount);
        Assert.True(global::Program.TryBeginObjectWalk(conn, 0, 70, 30, 2, out _));
    }
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, -.75)]
    public void Bow_ground_attacks_cannot_reserve_pacing_or_a_new_token_from_recovery_input(double began, double requestedAt)
    {
        var conn = ObjectWalkState();
        conn.Clock.Start(); // Bow uses are refused while paused; this test isolates the input's recovery timestamp.
        var before = conn.Walk.Position(0);
        conn.HitRecovery.Begin(began, .5f);
        Assert.False(global::Program.TryBeginBowAttack(conn, requestedAt, 1, 0x102, 7, out _));
        Assert.Equal(4, conn.ActionCount);
        Assert.Equal(0, conn.NextAttackAt);
        Assert.Equal((ushort)123, conn.ChannelSkill);
        Assert.Equal(before, conn.Walk.Position(1));
        conn.HitRecovery.Clear();
        Assert.True(global::Program.TryBeginBowAttack(conn, 0, 1, 0x102, 7, out var bow));
        Assert.Equal(5, bow.Action);
        Assert.Equal((ushort)0x102, conn.ChannelSkill);
        Assert.True(conn.NextAttackAt > 0);
        Assert.False(global::Program.TryBeginBowAttack(conn, 0, 1, 0x102, 8, out _));
        Assert.Equal(5, conn.ActionCount);
    }
}
