using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalCriticalGatewayTests
{
    [Theory]
    [InlineData("Cyclone", 0)]
    [InlineData("HeavyStrike", 0)]
    [InlineData("IceNova", 0)]
    [InlineData("Cyclone", 1)]
    [InlineData("HeavyStrike", 1)]
    [InlineData("IceNova", 1)]
    [InlineData("Cyclone", -1)]
    [InlineData("HeavyStrike", -1)]
    [InlineData("IceNova", -1)]
    public async Task An_area_skill_shares_its_critical_roll_across_all_targets(string gem, int luck)
    {
        var critical = new Draws(luck == 1 ? 9000 : 0, 0);
        using var f = new Contact(critical, gem, "Rusted Sword");
        f.Set("extra_critical_rolls_while_on_low_life", 0);
        f.Set("extra_critical_rolls", luck);
        var second = new SpawnedMonster("same sweep target", new(33, 32, 30), 0, 0, 100000);
        f.Conn.Monsters.Add(43, second);
        var packet = f.Hit;
        await f.Sweep();
        foreach (var target in new[] { f.Target, second })
            Assert.InRange(target.MaxLife - target.Life,
                (uint)Math.Round(packet.MinTotal * f.Multiplier / 100.0),
                (uint)Math.Round(packet.MaxTotal * f.Multiplier / 100.0));
        Assert.Equal(luck == 0 ? 1 : 2, critical.Calls);
    }

    [Fact]
    public async Task Separate_area_uses_reroll_after_the_previous_action()
    {
        var critical = new Draws(0, 9999);
        using var f = new Contact(critical, "Cyclone", "Rusted Sword");
        f.Set("extra_critical_rolls_while_on_low_life", 0);
        var packet = f.Hit;
        await f.Sweep();
        uint afterFirst = f.Target.Life;
        await f.Sweep();
        Assert.Equal((uint)Math.Round(packet.MinTotal * f.Multiplier / 100.0), f.Target.MaxLife - afterFirst);
        Assert.Equal((uint)Math.Round(packet.MinTotal), afterFirst - f.Target.Life);
        Assert.Equal(2, critical.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Piercing_and_returning_hits_keep_the_original_skill_roll(bool returning)
    {
        var critical = new Draws(0);
        using var f = new Contact(critical, "BurningArrow", "Crude Bow", distance: 30);
        f.Set("extra_critical_rolls_while_on_low_life", 0);
        f.Set("projectile_base_number_of_targets_to_pierce", 1);
        SpawnedMonster? second = null;
        if (returning)
        {
            f.Set("attack_projectiles_return", 1);
            f.Set("support_return_returning_projectiles_damage_+%_final", -60);
        }
        else
        {
            second = new("later pierced target", new(92, 32, 30), 0, 0, 100000);
            f.Conn.Monsters.Add(43, second);
        }
        int direct = DamageModel.CriticalDamage((int)Math.Round(f.Hit.MinTotal), f.Multiplier);
        await f.Launch();
        Assert.Equal((uint)(direct + (returning ? (int)Math.Floor(direct * .4) : 0)), f.Target.MaxLife - f.Target.Life);
        if (second is not null) Assert.Equal((uint)direct, second.MaxLife - second.Life);
        Assert.Equal(1, critical.Calls);
    }

    [Fact]
    public async Task Different_projectiles_in_the_same_volley_share_the_roll()
    {
        var critical = new Draws(0);
        using var f = new Contact(critical, "BurningArrow", "Crude Bow", distance: 60);
        f.Set("extra_critical_rolls_while_on_low_life", 0);
        f.Set("number_of_additional_projectiles", 1);
        f.Target.Walk.Teleport(92, 21, f.Conn.Clock.Elapsed.TotalSeconds);
        var second = new SpawnedMonster("other projectile target", new(92, 43, 30), 0, 0, 100000);
        f.Conn.Monsters.Add(43, second);
        int damage = DamageModel.CriticalDamage((int)Math.Round(f.Hit.MinTotal), f.Multiplier);
        await f.Launch((92, 32));
        Assert.Equal((uint)damage, f.Target.MaxLife - f.Target.Life);
        Assert.Equal((uint)damage, second.MaxLife - second.Life);
        Assert.Equal(1, critical.Calls);
    }

    [Fact]
    public async Task Projectile_and_its_impact_area_share_the_original_spell_roll()
    {
        var critical = new Draws(0);
        using var f = new Contact(critical, "Fireball", "Rusted Sword", distance: 30);
        f.Set("extra_critical_rolls_while_on_low_life", 0);
        var second = new SpawnedMonster("nearby explosion target", new(62, 35, 30), 0, 0, 100000);
        f.Conn.Monsters.Add(43, second);
        var packet = f.Hit;
        await f.Launch();
        foreach (var target in new[] { f.Target, second })
            Assert.InRange(target.MaxLife - target.Life,
                (uint)Math.Round(packet.MinTotal * f.Multiplier / 100.0),
                (uint)Math.Round(packet.MaxTotal * f.Multiplier / 100.0));
        Assert.Equal(1, critical.Calls);
    }

    [Fact]
    public async Task Area_targets_share_the_critical_roll_but_confirm_accuracy_independently()
    {
        var critical = new Draws(0);
        using var f = new Contact(critical, "Cyclone", "Rusted Sword");
        f.Set("extra_critical_rolls_while_on_low_life", 0);
        f.Conn.Pools = f.Conn.Pools with { Accuracy = 0 };
        var accuracy = new AccuracyDraws(99, 0, 99, 99);
        f.Conn.AttackAccuracyRandom = accuracy;
        var second = new SpawnedMonster("failed confirmation", new(33, 32, 30), 0, 0, 100000);
        f.Conn.Monsters.Add(43, second);
        var packet = f.Hit;
        await f.Sweep();
        Assert.Equal((uint)Math.Round(packet.MinTotal * f.Multiplier / 100.0), f.Target.MaxLife - f.Target.Life);
        Assert.Equal((uint)Math.Round(packet.MinTotal), second.MaxLife - second.Life);
        Assert.Equal(1, critical.Calls);
        Assert.Equal(4, accuracy.Calls);
    }

    [Fact]
    public async Task Shared_roll_compares_against_current_chance_after_a_source_changes_between_targets()
    {
        var critical = new Draws(5000);
        using var f = new Contact(critical, "Cyclone", "Rusted Sword");
        f.Set("extra_critical_rolls_while_on_low_life", 0);
        var second = new SpawnedMonster("changed chance", new(33, 32, 30), 0, 0, 100000);
        f.Conn.Monsters.Add(43, second);
        var packet = f.Hit;
        await f.SendGate.WaitAsync();
        Task sweep;
        try
        {
            sweep = f.Sweep();
            await f.HitLands();
            Assert.Equal((uint)Math.Round(packet.MinTotal), f.Target.MaxLife - f.Target.Life);
            Assert.Equal(second.MaxLife, second.Life);
            f.Set("critical_strike_chance_+%", 100000);
            f.Refresh();
        }
        finally { f.SendGate.Release(); }
        await sweep;
        Assert.Equal((uint)Math.Round(packet.MinTotal * f.Multiplier / 100.0), second.MaxLife - second.Life);
        Assert.Equal(1, critical.Calls);
    }

    [Fact]
    public async Task Later_prohibition_suppresses_a_shared_critical_roll_without_rerolling()
    {
        var critical = new Draws(0);
        using var f = new Contact(critical, "Cyclone", "Rusted Sword");
        f.Set("extra_critical_rolls_while_on_low_life", 0);
        var second = new SpawnedMonster("prohibited critical", new(33, 32, 30), 0, 0, 100000);
        f.Conn.Monsters.Add(43, second);
        var packet = f.Hit;
        await f.SendGate.WaitAsync();
        Task sweep;
        try
        {
            sweep = f.Sweep();
            await f.HitLands();
            Assert.Equal((uint)Math.Round(packet.MinTotal * f.Multiplier / 100.0), f.Target.MaxLife - f.Target.Life);
            f.Set("global_cannot_crit", 1);
        }
        finally { f.SendGate.Release(); }
        await sweep;
        Assert.Equal((uint)Math.Round(packet.MinTotal), second.MaxLife - second.Life);
        Assert.Equal(1, critical.Calls);
    }
}
