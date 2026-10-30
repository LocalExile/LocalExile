using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class WhispererManaTests
{
    [Fact]
    public void Mana_spent_tracks_complete_200_groups_only_and_source_removal_discards_the_remainder()
    {
        var state = new WhispererManaState();
        Assert.Equal(0, state.Spent(199, 10)); Assert.Equal(199, state.SpentRemainder);
        Assert.Equal(10, state.Spent(1, 10)); Assert.Equal(0, state.SpentRemainder);
        Assert.Equal(20, state.Spent(450, 10)); Assert.Equal(50, state.SpentRemainder);
        state.Observe(0); Assert.Equal(0, state.SpentRemainder);
        Assert.Equal(0, state.Spent(150, 10));
    }
    [Fact]
    public void Overlapping_recoup_integrates_each_hit_once_and_carry_freezes_loading_time()
    {
        var state = new WhispererManaState();
        state.Hit(10, 100, 20);
        state.Hit(11, 200, 20);
        Assert.Equal(5, state.Take(11));
        Assert.Equal(15, state.Take(12));
        var carry = state.Carry(13);
        Assert.Equal(15, carry.Pending);
        var destination = new WhispererManaState(); destination.Restore(carry, 1000);
        Assert.Equal(30, destination.Take(1001));
        Assert.Equal(10, destination.Take(1002));
        Assert.Equal(0, destination.Take(1003));
        state.Clear(); Assert.Equal(0, state.Take(1003));
    }
    [Theory]
    [InlineData(100, 60u, 100, 60u)]
    [InlineData(101, 200u, 50, 50u)]
    [InlineData(100, 50u, -1, 0u)]
    public void Mana_mitigation_uses_actual_available_pool_and_never_creates_negative_damage(int damage, uint mana, int pct, uint expected)
        => Assert.Equal(expected, WhispererMana.FromMana(damage, mana, pct));
    [Fact]
    public void Recoup_expiry_settles_the_last_point_through_the_actual_whole_point_writer()
    {
        var state = new WhispererManaState();
        const double start = 12.1234567;
        state.Hit(start, 120, 20);
        uint mana = 0; double carry = 0;
        for (int i = 1; i <= 201; i++)
            mana = RecoveryPool.Advance(mana, 100, state.Take(start + i * .02), ref carry);
        Assert.Equal(24u, mana);
        Assert.Equal(0, carry, 8);
    }
    [Fact]
    public void Sub_point_recoup_carries_once_but_cannot_be_banked_beyond_full_Mana()
    {
        var state = new WhispererManaState();
        state.Hit(0, 1, 20); Assert.Equal(0, state.Take(4));
        var carried = state.Carry(4); state.Restore(carried, 100);
        state.Hit(100, 4, 20); Assert.Equal(1, state.Take(104));
        state.Hit(105, 1, 20); Assert.Equal(0, state.Take(109));
        state.DiscardRecoveryFraction();
        state.Hit(110, 4, 20); Assert.Equal(0, state.Take(114));
    }
    [Fact]
    public void Final_cost_doubles_the_supported_cost_and_clamps_reductions()
    {
        Assert.Equal(14, WhispererMana.Cost(7, n => n == "base_mana_cost_-%" ? -100 : 0));
        Assert.Equal(0, WhispererMana.Cost(7, n => n == "base_mana_cost_-%" ? 101 : 0));
        Assert.Equal(int.MaxValue, WhispererMana.Cost(int.MaxValue, n => n == "base_mana_cost_-%" ? -100 : 0));
    }
    [Fact]
    public void Mana_flask_copies_only_gradual_recovery_and_preserves_the_mana_queue()
    {
        var effects = new Flasks.Effects();
        var e = new Flasks.Effect(0, 1, 0, 0, 10, 0, 4, ManaStays: true, NoQueue: false, ManaAlsoLife: true);
        effects.Start(e);
        Assert.Equal(2, e.Queue);
        var first = effects.Advance(0, 1); Assert.Equal(10, first.Mana); Assert.Equal(10, first.Life);
        var rest = effects.Advance(1, 10); Assert.Equal(30, rest.Mana); Assert.Equal(30, rest.Life);
        Assert.Single(rest.Ended);
        effects.Start(e with { Started = 10, Ends = 11, ManaPerSecond = 0, ManaAtEnd = 100 });
        var instantAtEnd = effects.Advance(10, 12); Assert.Equal(100, instantAtEnd.Mana); Assert.Equal(0, instantAtEnd.Life);
    }
    [Theory]
    [InlineData("local_flask_recovers_instantly")]
    [InlineData("local_flask_mana_recovery_occurs_instantly_at_end_of_flask_effect")]
    [InlineData("local_flask_mana_to_recover_+%")]
    public void Full_Mana_does_not_admit_noncontinuous_or_zero_recovery(string mod)
    {
        var flask = new Flasks.Flask(0, 50, 3, 6, 24);
        var item = new Item(1, [], 1);
        var local = new Dictionary<string, int> { [mod] = mod.EndsWith("_+%") ? -100 : 1 };
        var recovery = FlaskRecovery.For(flask, item, local,
            n => n == "non_instant_mana_recovery_from_flasks_also_recovers_life" ? 1 : 0, 50, 100);
        Assert.False(Flasks.CanRecover(flask, 50, 100, 100, 100, recovery));
    }
    [Fact]
    public void Blind_uses_strongest_effect_without_stacking_and_restores_weaker_remaining_duration()
    {
        var blind = new TimedBlind();
        Assert.True(blind.Inflict(0, 8, 0, false));
        Assert.True(blind.Inflict(1, 4, 50, false));
        Assert.Equal(70, blind.Apply(100, 2)); Assert.Equal(80, blind.Apply(100, 5));
        Assert.Equal(100, blind.Apply(100, 8));
        Assert.False(blind.Inflict(10, 4, 0, true));
        blind.Inflict(10, 4, 0, false); Assert.Equal(100, blind.Apply(100, 11, true));
        Assert.Equal(100, blind.Apply(100, 12));
    }
}
