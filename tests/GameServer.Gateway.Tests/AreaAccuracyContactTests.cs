using GameServer.Core.World;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public partial class ConditionalCriticalGatewayTests
{
    [Theory]
    [InlineData("Cyclone", 0, true)]
    [InlineData("Cyclone", 99, false)]
    [InlineData("HeavyStrike", 0, true)]
    [InlineData("HeavyStrike", 99, false)]
    public async Task Area_attack_critical_confirmation_uses_the_contact_hit_chance(string gem, int confirmation, bool confirmed)
    {
        var critical = new Draws(0);
        using var f = new Contact(critical, gem, "Rusted Sword");
        f.Set("extra_critical_rolls_while_on_low_life", 0);
        f.Refresh();
        f.Conn.Pools = f.Conn.Pools with { Accuracy = 0 };
        var accuracy = new AccuracyDraws(99, confirmation);
        f.Conn.AttackAccuracyRandom = accuracy;
        var packet = f.Hit;
        int multiplier = f.Multiplier;
        await f.Use();
        await f.HitLands();
        Assert.Equal((uint)Math.Round(packet.MinTotal * (confirmed ? multiplier / 100.0 : 1)),
            f.Target.MaxLife - f.Target.Life);
        Assert.Equal(2, accuracy.Calls);
        Assert.Equal(1, critical.Calls);
        Assert.Equal(4, f.Target.AttackEvasion.Value);
    }

    [Theory]
    [InlineData("Cyclone")]
    [InlineData("HeavyStrike")]
    public async Task Evaded_area_attack_neither_rolls_crit_nor_consumes_impales_or_on_hit_state(string gem)
    {
        var critical = new Draws();
        using var f = new Contact(critical, gem, "Rusted Sword");
        f.Conn.Pools = f.Conn.Pools with { Accuracy = 0 };
        var accuracy = new AccuracyDraws(0);
        f.Conn.AttackAccuracyRandom = accuracy;
        f.Target.Impale.Apply(100, now: 0);
        double previousHit = f.Conn.LastEnemyHitAt;
        await f.Use();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while (true)
            {
                lock (f.Conn.Monsters) if (f.Target.AttackEvasion.Value >= 0) break;
                await Task.Delay(10, timeout.Token);
            }
        Assert.Equal(f.Target.MaxLife, f.Target.Life);
        Assert.Equal(previousHit, f.Conn.LastEnemyHitAt);
        for (int i = 0; i < 4; i++) f.Target.Impale.AfterHit(0);
        Assert.Equal(10, f.Target.Impale.Bonus); // All five uses remained after the miss.
        Assert.Equal(0, critical.Calls);
        Assert.Equal(1, accuracy.Calls);
    }

    [Theory]
    [InlineData("Cyclone")]
    [InlineData("HeavyStrike")]
    public async Task Area_attack_cannot_be_evaded_bypasses_accuracy_and_confirmation(string gem)
    {
        using var f = new Contact(new Draws(0), gem, "Rusted Sword");
        f.Set("extra_critical_rolls_while_on_low_life", 0);
        f.Set("global_always_hit", 1);
        f.Refresh();
        f.Conn.Pools = f.Conn.Pools with { Accuracy = 0 };
        var accuracy = new AccuracyDraws();
        f.Conn.AttackAccuracyRandom = accuracy;
        var packet = f.Hit;
        await f.Use();
        await f.HitLands();
        Assert.Equal((uint)Math.Round(packet.MinTotal * f.Multiplier / 100.0), f.Target.MaxLife - f.Target.Life);
        Assert.Equal(0, accuracy.Calls);
        Assert.Equal(-1, f.Target.AttackEvasion.Value);
    }

    [Fact]
    public async Task Area_spell_does_not_roll_attack_accuracy_or_advance_defender_entropy()
    {
        var critical = new Draws(0);
        using var f = new Contact(critical, "IceNova", "Rusted Sword");
        f.Set("extra_critical_rolls_while_on_low_life", 0);
        f.Refresh();
        f.Conn.Pools = f.Conn.Pools with { Accuracy = 0 };
        var accuracy = new AccuracyDraws();
        f.Conn.AttackAccuracyRandom = accuracy;
        var packet = f.Hit;
        await f.Use();
        await f.HitLands();
        Assert.InRange(f.Target.MaxLife - f.Target.Life,
            (uint)Math.Round(packet.MinTotal * f.Multiplier / 100.0),
            (uint)Math.Round(packet.MaxTotal * f.Multiplier / 100.0));
        Assert.Equal(1, critical.Calls);
        Assert.Equal(0, accuracy.Calls);
        Assert.Equal(-1, f.Target.AttackEvasion.Value);
    }

    [Fact]
    public async Task Cancelled_area_attack_has_no_accuracy_or_critical_draw()
    {
        var critical = new Draws();
        using var f = new Contact(critical, "HeavyStrike", "Rusted Sword");
        var accuracy = new AccuracyDraws();
        f.Conn.AttackAccuracyRandom = accuracy;
        var use = f.Sweep(); // Await the executor itself, including its cancelled contact, not only input dispatch.
        f.Conn.ActionCount++;
        await use;
        Assert.Equal(0, accuracy.Calls);
        Assert.Equal(0, critical.Calls);
        Assert.Equal(-1, f.Target.AttackEvasion.Value);
        Assert.Equal(f.Target.MaxLife, f.Target.Life);
    }

    [Fact]
    public async Task Area_attack_uses_real_Additional_Accuracy_support_in_its_hit_chance()
    {
        using var f = new Contact(new Draws(0), "Cyclone", "Rusted Sword", targetLevel: 100);
        f.Set("extra_critical_rolls_while_on_low_life", 0);
        var inventory = PlayerInventories.For(f.Conn.Character!, Program.Items);
        var host = Assert.Single(inventory.Items(PlayerInventories.BodyArmour));
        var support = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SupportGemAdditionalAccuracy")!) with { Quality = 20 };
        Assert.NotNull(inventory.Change(PlayerInventories.BodyArmour, host.Id,
            item => item with { Sockets = [item.Sockets[0], new(SocketColour.White, support)] }));
        f.Refresh();
        f.Conn.Pools = f.Conn.Pools with
        { Accuracy = AccuracyRating.Calculate(f.Conn.Pools.AccuracyBase, f.Conn.Pools.AccuracyPct) };
        var accuracy = new AccuracyDraws(99, 99);
        f.Conn.AttackAccuracyRandom = accuracy;
        int selectedRating = (int)((f.Conn.Pools.AccuracyBase + 74) * (120 + f.Conn.Pools.AccuracyPct) / 100);
        int chance = DamageModel.AccuracyHitChance(13445, selectedRating);
        Assert.InRange(chance, 6, 99);
        Assert.True(chance > DamageModel.AccuracyHitChance(13445, f.Conn.Pools.Accuracy));
        var packet = f.Hit;
        await f.Use();
        await f.HitLands();
        Assert.Equal((uint)Math.Round(packet.MinTotal), f.Target.MaxLife - f.Target.Life);
        Assert.Equal(chance - 1, f.Target.AttackEvasion.Value);
        Assert.Equal(2, accuracy.Calls);
    }

    [Fact]
    public async Task Area_sweep_can_miss_one_target_and_hit_another_without_reusing_evasion_state()
    {
        var critical = new Draws(0);
        using var f = new Contact(critical, "Cyclone", "Rusted Sword");
        f.Set("extra_critical_rolls_while_on_low_life", 0);
        f.Refresh();
        f.Conn.Pools = f.Conn.Pools with { Accuracy = 0 };
        var second = new SpawnedMonster("second area target", new(33, 32, 30), 0, 0, 100000);
        f.Conn.Monsters.Add(43, second);
        var accuracy = new AccuracyDraws(0, 99, 99);
        f.Conn.AttackAccuracyRandom = accuracy;
        var packet = f.Hit;
        await f.Use();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while (second.Life == second.MaxLife) await Task.Delay(10, timeout.Token);
        Assert.Equal(f.Target.MaxLife, f.Target.Life);
        Assert.Equal((uint)Math.Round(packet.MinTotal), second.MaxLife - second.Life);
        Assert.Equal(5, f.Target.AttackEvasion.Value);
        Assert.Equal(4, second.AttackEvasion.Value);
        Assert.Equal(3, accuracy.Calls);
        Assert.Equal(1, critical.Calls);
    }

    [Theory]
    [InlineData(Program.Blocked)]
    [InlineData(Program.Evaded)]
    public async Task Rejected_contact_results_do_not_apply_zero_damage_hit_side_effects(int result)
    {
        using var f = new Contact(new Draws());
        double before = f.Conn.LastEnemyHitAt;
        Assert.False(await f.ContactDamage(() => result));
        Assert.Equal(before, f.Conn.LastEnemyHitAt);
        Assert.Equal(f.Target.MaxLife, f.Target.Life);
    }
}
