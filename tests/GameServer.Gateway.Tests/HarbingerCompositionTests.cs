using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class HarbingerCompositionTests
{
    static DamagePacket Fire(int n) => DamagePacket.Of(DamageType.Fire, n, n);
    static int None(string _) => 0;
    static Dictionary<uint, int> Stats(params (string Name, int Value)[] values) =>
        values.ToDictionary(p => Program.Stats.Key(p.Name)!.Value, p => p.Value);

    [Fact]
    public void Debuff_clock_doubles_elapsed_time_without_doubling_ignite_DPS_and_refund_preserves_budget()
    {
        var state = new PlayerElementalAilments();
        int Fast(string n) => n == "debuff_time_passed_+%" ? 100 : 0;
        state.Apply(Fire(100), Fire(100), true, 1000, 0, Fast, new Random(1));
        Assert.Equal(58, state.Advance(0, .5, Fast, 0, 75, false)); // 117 DPS, not 234.
        state.Reconcile(None, .5); // Three base seconds remain after one base second elapsed.
        Assert.Equal(351, state.Advance(.5, 3.5, None, 0, 75, false));
        Assert.False(state.Snapshot(3.5).Ignite);
    }

    [Fact]
    public void Temporal_Chains_other_expiry_is_additive_with_received_debuff_elapsed_rate()
    {
        var state = new PlayerElementalAilments();
        int Get(string n) => n switch { "debuff_time_passed_+%" => 100,
            "buff_time_passed_+%_other_than_temporal_chains" => -25, _ => 0 };
        state.Apply(Fire(100), Fire(100), true, 1000, 0, Get, new Random(1));
        Assert.True(state.Snapshot(2.28).Ignite);
        Assert.False(state.Snapshot(2.29).Ignite); // Four / 1.75, not four / two / .75.
        Assert.Equal(267, state.Advance(0, 4, Get, 0, 75, false));
    }

    [Fact]
    public void Focus_generic_immunity_removes_all_existing_ordinary_ailments_and_prevents_new_applications()
    {
        var state = new PlayerElementalAilments();
        var hit = Fire(100).Add(DamagePacket.Of(DamageType.Cold, 100, 100))
            .Add(DamagePacket.Of(DamageType.Lightning, 100, 100));
        state.Apply(hit, hit, true, 1000, 0, None, new Random(1));
        Assert.Equal((true, 19, true, 19), state.Snapshot(.1));
        int Focus(string n) => n == "immune_to_status_ailments" ? 1 : 0;
        state.Reconcile(Focus, .1);
        state.Apply(hit, hit, true, 1000, .1, Focus, new Random(1));
        Assert.Equal((false, 0, false, 0), state.Snapshot(.2));
        Assert.Equal(0, state.Advance(.1, 10, None, 0, 75, false));
    }


    [Fact]
    public void Ignite_bypass_keeps_subpoint_thirty_percent_without_spending_Mana()
    {
        TestSaves.Ensure();
        var state = new ClientState(new(ushort.MaxValue, "composition", 128, 128, 32, 32, null, []), 0, 1)
        { PlayerLife = 1000, PlayerMana = 100, Pools = new(1, 1000, 100, 0, 0, EnergyShield: 1000) };
        state.EnergyShield.SetMaximum(1000, refill: true);
        state.ElementalAilments.Apply(Fire(100), Fire(100), true, 1000, 0, None, new Random(1));
        var stats = Stats(("base_non_chaos_damage_bypass_energy_shield_%", 30), ("damage_taken_goes_to_mana_%", 100));
        for (int i = 0; i < 100; i++) Program.AdvancePlayerAilmentsLocked(state, i / 100d, (i + 1) / 100d, stats);
        Assert.Equal(965u, state.PlayerLife); // Floor(117 * .30) over all frames.
        Assert.Equal(918u, state.EnergyShield.Current);
        Assert.Equal(100u, state.PlayerMana);
    }


}
