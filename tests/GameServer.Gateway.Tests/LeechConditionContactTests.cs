using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public partial class ConditionalCriticalGatewayTests
{
    static void EquipEnergyLeech(Contact f)
    {
        var inventory = PlayerInventories.For(f.Conn.Character!, Program.Items);
        var body = Assert.Single(inventory.Items(PlayerInventories.BodyArmour));
        inventory.Change(PlayerInventories.BodyArmour, body.Id, item => item with
        { Sockets = [item.Sockets![0], new(SocketColour.White,
            ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SupportGemEnergyShieldLeech")!) with { Quality = 20 })], Links = [2] });
    }

    [Fact]
    public void Actual_Energy_Leech_support_uses_full_ES_more_and_leeching_quality_in_separate_buckets()
    {
        using var f = new Contact(new Draws(), "Fireball");
        f.Set("global_cannot_crit", 1);
        EquipEnergyLeech(f);
        f.Conn.Pools = f.Conn.Pools with { EnergyShield = 1000 };
        var baseline = f.Hit;
        Assert.True(baseline.MinTotal > 0);
        var state = Program.LeechOf(f.Conn);
        state.Hit(f.Conn.Clock.Elapsed.TotalSeconds, (0, 0, 100), false, false, false);
        var leeching = f.Hit;
        Assert.Equal(baseline.MinTotal * 1.2, leeching.MinTotal, 6);
        Assert.Equal(1, Program.CharacterPlayerStats(f.Conn).GetValueOrDefault(Program.Stats.Key("is_es_leeching")!.Value));

        f.Conn.EnergyShield.SetMaximum(1000, refill: true);
        var full = f.Hit;
        Assert.Equal(baseline.MinTotal * 1.15, full.MinTotal, 6);
        Assert.Equal(0, state.EnergyShield.Count); // Ordinary leech ended; full ES bonus remains.

        f.Set("energy_shield_leech_does_not_stop_on_full_energy_shield", 1);
        Program.CharacterPlayerStats(f.Conn);
        state.Hit(f.Conn.Clock.Elapsed.TotalSeconds, (0, 0, 100), false, false, true);
        Assert.Equal(baseline.MinTotal * 1.2 * 1.15, f.Hit.MinTotal, 6);
        var inventory = PlayerInventories.For(f.Conn.Character!, Program.Items);
        var body = Assert.Single(inventory.Items(PlayerInventories.BodyArmour));
        inventory.Change(PlayerInventories.BodyArmour, body.Id, item => item with { Links = [] });
        Assert.Equal(baseline.MinTotal, f.Hit.MinTotal, 6);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Real_Energy_Leech_conditions_apply_to_the_committed_projectile_hit(bool full, bool leeching)
    {
        using var f = new Contact(new Draws(), "Fireball");
        f.Set("global_cannot_crit", 1);
        EquipEnergyLeech(f);
        f.Conn.Pools = f.Conn.Pools with { EnergyShield = 1000 };
        var baseline = f.Hit;
        f.Set("energy_shield_leech_does_not_stop_on_full_energy_shield", 1);
        f.Conn.EnergyShield.SetMaximum(1000, refill: full);
        Program.CharacterPlayerStats(f.Conn);
        if (leeching) Program.LeechOf(f.Conn).Hit(f.Conn.Clock.Elapsed.TotalSeconds, (0, 0, 100), false, false, full);
        double factor = (full ? 1.15 : 1) * (leeching ? 1.2 : 1);
        await f.Launch();
        Assert.InRange(f.Target.MaxLife - f.Target.Life,
            (uint)baseline.Components.Sum(c => Math.Floor(c.Range.Min * factor + 1e-9)),
            (uint)baseline.Components.Sum(c => Math.Floor(c.Range.Max * factor + 1e-9)));
    }

    [Fact]
    public async Task Idle_player_tick_publishes_leech_state_changes_without_a_new_action()
    {
        using var f = new Contact(new Draws());
        f.Conn.Pools = f.Conn.Pools with { EnergyShield = 1000 };
        Program.CharacterPlayerStats(f.Conn);
        var tick = f.RunPanelTick();
        try
        {
            Program.LeechOf(f.Conn).Hit(f.Conn.Clock.Elapsed.TotalSeconds, (0, 0, 100), false, false, false);
            await f.AssertNextPlayerStats();
            Assert.False(tick.IsCompleted);
        }
        finally
        {
            f.Conn.Closed = true;
            await tick.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData("life", "is_life_leeching")]
    [InlineData("mana", "is_mana_leeching")]
    [InlineData("energy_shield", "is_es_leeching")]
    public void Leech_flags_follow_actual_instances_and_cannot_be_forged(string pool, string flag)
    {
        using var f = new Contact(new Draws());
        f.Conn.Pools = f.Conn.Pools with { Life = 1000, EnergyShield = 1000 };
        f.Conn.PlayerMana = 500;
        f.Set(flag, 99); f.Set("is_leeching", 99); f.Set("on_full_energy_shield", 99);
        int Value(Dictionary<uint, int> stats, string name) => stats.GetValueOrDefault(Program.Stats.Key(name)!.Value);
        var before = Program.CharacterPlayerStats(f.Conn);
        Assert.Equal(0, Value(before, flag));
        Assert.Equal(0, Value(before, "is_leeching"));
        Assert.Equal(0, Value(before, "on_full_energy_shield"));
        var s = Program.LeechOf(f.Conn);
        s.Hit(f.Conn.Clock.Elapsed.TotalSeconds, (pool == "life" ? 100 : 0,
            pool == "mana" ? 100 : 0, pool == "energy_shield" ? 100 : 0), false, false, false);
        var active = Program.CharacterPlayerStats(f.Conn);
        Assert.Equal(1, Value(active, flag));
        Assert.Equal(1, Value(active, "is_leeching"));
        s.Clear(f.Conn.Clock.Elapsed.TotalSeconds);
        Assert.Equal(0, Value(Program.CharacterPlayerStats(f.Conn), flag));
    }
}
