using GameServer.Core.World;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public partial class ConditionalCriticalGatewayTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "Crude Bow")]
    [InlineData("Cyclone", null)]
    [InlineData("BurningArrow", "Crude Bow")]
    [InlineData("Fireball", null)]
    [InlineData("IceNova", null)]
    public async Task Parsed_direct_hits_create_all_three_leech_instances(string? gem, string? weapon)
    {
        using var f = new Contact(new Draws(), gem, weapon);
        f.Set("global_cannot_crit", 1);
        foreach (string pool in new[] { "life", "mana", "energy_shield" })
            f.Set($"{pool}_leech_from_any_damage_permyriad", 100);
        f.Conn.Pools = f.Conn.Pools with { Life = 1000, EnergyShield = 1000 };
        f.Conn.PlayerMana = 500;
        await f.Use(); await f.HitLands();
        var leech = Program.LeechOf(f.Conn);
        Assert.Equal(1, leech.Life.Count);
        Assert.Equal(1, leech.Mana.Count);
        Assert.Equal(1, leech.EnergyShield.Count);
        var recovered = leech.TakeRecovery(f.Conn.Clock.Elapsed.TotalSeconds + 20);
        double expected = Math.Min(100, (f.Target.MaxLife - f.Target.Life) * .01);
        Assert.Equal(expected, recovered.Life, 6);
        Assert.Equal(expected, recovered.Mana, 6);
        Assert.Equal(expected, recovered.EnergyShield, 6);
    }

    [Theory]
    [InlineData("life", "Fireball", "SupportGemLifeLeech", .02)]
    [InlineData("mana", "Cyclone", "SupportGemManaLeech", .02)]
    [InlineData("energy_shield", "Fireball", "SupportGemEnergyShieldLeech", .015)]
    public async Task Real_linked_leech_support_recovers_its_resource_at_accepted_contact(
        string pool, string gem, string support, double fraction)
    {
        using var f = new Contact(new Draws(), gem);
        f.Set("global_cannot_crit", 1); f.Set("leech_%_is_instant", 100);
        var inventory = PlayerInventories.For(f.Conn.Character!, Program.Items);
        var body = Assert.Single(inventory.Items(PlayerInventories.BodyArmour));
        inventory.Change(PlayerInventories.BodyArmour, body.Id, item => item with
        { Sockets = [item.Sockets![0], new(SocketColour.White,
            ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + support)!))], Links = [2] });
        f.Conn.Pools = f.Conn.Pools with { Life = 1000, EnergyShield = 1000 };
        f.Conn.PlayerMana = 500;
        uint before = pool == "life" ? f.Conn.PlayerLife : pool == "mana" ? f.Conn.PlayerMana : f.Conn.EnergyShield.Current;
        // Invoke the already admitted hit path so Mana cost is independent of recovery assertions.
        if (gem == "Cyclone") await f.Sweep(); else await f.Launch();
        uint after = pool == "life" ? f.Conn.PlayerLife : pool == "mana" ? f.Conn.PlayerMana : f.Conn.EnergyShield.Current;
        double expected = Math.Min(100, (f.Target.MaxLife - f.Target.Life) * fraction);
        Assert.True(expected >= 1);
        Assert.Equal((uint)Math.Truncate(expected), after - before);
        Assert.Equal(0, Program.LeechOf(f.Conn).Life.Count + Program.LeechOf(f.Conn).Mana.Count + Program.LeechOf(f.Conn).EnergyShield.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Evaded_melee_and_projectiles_cannot_create_leech(bool projectile)
    {
        using var f = new Contact(new Draws(), projectile ? "BurningArrow" : "Cyclone", projectile ? "Crude Bow" : null);
        f.Set("life_leech_from_any_damage_permyriad", 10000);
        f.Conn.Pools = f.Conn.Pools with { Accuracy = 0 };
        f.Conn.AttackAccuracyRandom = new AccuracyDraws(0);
        if (projectile) await f.Launch(); else await f.Sweep();
        Assert.Equal(f.Target.MaxLife, f.Target.Life);
        Assert.Equal(0, Program.LeechOf(f.Conn).Life.Count);
    }

    [Fact]
    public async Task Target_life_leech_immunity_does_not_disable_mana_leech()
    {
        using var f = new Contact(new Draws(), targetMods: new([], [],
            new(new Dictionary<string, int> { ["cannot_have_life_leeched_from"] = 1 })));
        f.Set("global_cannot_crit", 1); f.Set("leech_%_is_instant", 100);
        f.Set("life_leech_from_any_damage_permyriad", 100);
        f.Set("mana_leech_from_any_damage_permyriad", 100);
        f.Conn.PlayerMana = 500;
        await f.Use();
        Assert.Equal(50u, f.Conn.PlayerLife);
        Assert.Equal(510u, f.Conn.PlayerMana);
        Assert.Contains("cannot_have_life_leeched_from", MonsterModCatalog.Modelled);
    }

    [Fact]
    public async Task Resource_writer_recovers_all_pools_respects_pause_and_clears_on_death()
    {
        using var f = new Contact(new Draws());
        f.Set("global_cannot_crit", 1);
        f.Set("no_life_regeneration", 1);
        f.Set("mana_regeneration_rate_+%", -100);
        f.Set("no_energy_shield_recharge_or_regeneration", 1);
        foreach (string pool in new[] { "life", "mana", "energy_shield" })
            f.Set($"{pool}_leech_from_any_damage_permyriad", 1000);
        f.Conn.Pools = f.Conn.Pools with { Life = 1000, EnergyShield = 1000 };
        f.Conn.PlayerMana = 500;
        await f.Use();
        f.Conn.Clock.Stop();
        var run = f.RunRecovery();
        try
        {
            await Task.Delay(80);
            Assert.Equal(50u, f.Conn.PlayerLife);
            Assert.Equal(500u, f.Conn.PlayerMana);
            Assert.Equal(0u, f.Conn.EnergyShield.Current);
            f.Conn.Clock.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (f.Conn.PlayerLife < 53 || f.Conn.PlayerMana < 503 || f.Conn.EnergyShield.Current < 3)
            {
                if (run.IsCompleted) await run;
                await Task.Delay(10, timeout.Token);
            }
            lock (f.Conn.Monsters) f.Conn.PlayerLife = 0;
            while (Program.LeechOf(f.Conn).Life.Count > 0)
            {
                if (run.IsCompleted) await run;
                await Task.Delay(10, timeout.Token);
            }
            Assert.Equal(0u, f.Conn.PlayerLife);
            Assert.Equal(0u, f.Conn.EnergyShield.Current);
            Assert.Equal(0, Program.LeechOf(f.Conn).Mana.Count);
        }
        finally
        {
            f.Conn.Closed = true;
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("leaving")]
    [InlineData("paused")]
    [InlineData("dead")]
    [InlineData("target-dead")]
    [InlineData("friendly")]
    public void Rejected_owner_and_target_states_cannot_grant_or_recover(string condition)
    {
        using var f = new Contact(new Draws());
        f.Set("life_leech_from_any_damage_permyriad", 10000);
        f.Set("leech_%_is_instant", 100);
        var sources = Program.HitLeechSources(Program.CharacterPlayerStats(f.Conn), true);
        switch (condition)
        {
            case "closed": f.Conn.Closed = true; break;
            case "leaving": f.Conn.LeavingArea = true; break;
            case "paused": f.Conn.Clock.Stop(); break;
            case "dead": f.Conn.PlayerLife = 0; break;
            case "target-dead": f.Target.Life = 0; break;
        }
        uint before = f.Conn.PlayerLife;
        var target = condition == "friendly" ? new SpawnedMonster("friendly", new(34, 32, 30), 0, 2, 100) { Hostile = false } : f.Target;
        Program.LeechOnHitLocked(f.Conn, target, DamagePacket.Of(DamageType.Physical, 100, 100), 100, sources);
        Assert.Equal(before, f.Conn.PlayerLife);
        Assert.Equal(0, Program.LeechOf(f.Conn).Life.Count);
    }
}
