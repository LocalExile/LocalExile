namespace GameServer.Gateway.Tests;

public class RageStateTests
{
    static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;
    static int Mana(string name) => name == "gain_x_rage_per_200_mana_spent" ? 10 : 0;
    static int None(string _) => 0;

    [Fact]
    public void Mana_spending_accumulates_exactly_and_preserves_the_remainder_across_gains()
    {
        var rage = new RageState();
        rage.SpendMana(199, Start, Mana); Assert.Equal(0, rage.Read(Start, Mana).Current);
        rage.SpendMana(2, Start, Mana); Assert.Equal(10, rage.Read(Start, Mana).Current);
        rage.SpendMana(398, Start, Mana); Assert.Equal(20, rage.Read(Start, Mana).Current);
        rage.SpendMana(1, Start, Mana); Assert.Equal(30, rage.Read(Start, Mana).Current);
        rage.SpendMana(uint.MaxValue, Start, Mana); Assert.Equal(30, rage.Read(Start, Mana).Current);
    }

    [Fact]
    public void Base_loss_starts_after_two_seconds_and_loses_ten_per_second_independent_of_tick_size()
    {
        var coarse = new RageState(); var fine = new RageState();
        foreach (var rage in new[] { coarse, fine }) rage.SpendMana(600, Start, Mana);
        Assert.Equal(30, coarse.Read(Start.AddSeconds(2), Mana).Current);
        for (int tick = 1; tick <= 40; tick++) fine.Read(Start.AddMilliseconds(tick * 100), Mana);
        Assert.Equal(10, coarse.Read(Start.AddSeconds(4), Mana).Current);
        Assert.Equal(10, fine.Read(Start.AddSeconds(4), Mana).Current);
        Assert.Equal(0, coarse.Read(Start.AddSeconds(5), Mana).Current);
    }

    [Fact]
    public void An_enemy_hit_delays_loss_even_without_a_rage_gain_and_pause_does_not_advance_it()
    {
        var rage = new RageState(); rage.SpendMana(600, Start, Mana);
        rage.EnemyHit(Start.AddSeconds(1.5), Mana, 99);
        Assert.Equal(30, rage.Read(Start.AddSeconds(3.5), Mana).Current);
        Assert.Equal(30, rage.Read(Start.AddSeconds(30), Mana, paused: true).Current);
        Assert.Equal(20, rage.Read(Start.AddSeconds(31), Mana).Current);
    }

    [Fact]
    public void Outgoing_and_incoming_hit_sources_share_half_second_cooldown_and_maximum_chance()
    {
        int Get(string name) => name switch { "gain_x_rage_on_hit" => 1, "gain_x_rage_on_attack_hit" => 2,
            "gain_x_rage_on_melee_hit" => 3, "gain_x_rage_on_attack_crit" => 4, "gain_x_rage_when_hit" => 5,
            "maximum_rage" => 15, "gain_max_rage_on_rage_gain_from_hit_%_chance" => 15, _ => 0 };
        var rage = new RageState();
        rage.Hit(Start, Get, true, true, true, 15); Assert.Equal(10, rage.Read(Start, Get).Current);
        rage.EnemyHit(Start.AddSeconds(.499), Get, 0); Assert.Equal(10, rage.Read(Start, Get).Current);
        rage.Hit(Start.AddSeconds(.5), Get, false, false, false, 14);
        Assert.Equal(new RageState.Value(45, 45, 45), rage.Read(Start.AddSeconds(.5), Get));
    }

    [Fact]
    public void Effect_maximum_removal_and_death_change_real_current_values()
    {
        int Get(string name) => name switch { "gain_x_rage_per_200_mana_spent" => 10, "maximum_rage" => 15,
            "rage_effect_+%" => 50, "no_inherent_rage_loss" => 1, _ => 0 };
        var rage = new RageState(); rage.SpendMana(1000, Start, Get);
        Assert.Equal(new RageState.Value(45, 45, 67), rage.Read(Start.AddHours(1), Get));
        Assert.Equal(new RageState.Value(30, 30, 30), rage.Read(Start.AddHours(1), None));
        rage.Clear(); Assert.Equal(0, rage.Read(Start.AddHours(1), Get).Current);
    }

    [Fact]
    public void Removing_the_mana_gain_source_discards_its_partial_spending_and_area_identity_is_shared()
    {
        string name = Guid.NewGuid().ToString("N"); var rage = RageState.For(name);
        rage.SpendMana(199, Start, Mana); rage.SpendMana(1, Start, None);
        var area = RageState.For(name.ToUpperInvariant()); Assert.Same(rage, area);
        area.SpendMana(1, Start, Mana); Assert.Equal(0, area.Read(Start, Mana).Current);
        area.SpendMana(199, Start, Mana); Assert.Equal(10, rage.Read(Start, Mana).Current);
    }
}
