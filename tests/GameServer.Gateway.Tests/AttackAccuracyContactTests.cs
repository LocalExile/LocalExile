using GameServer.Core.World;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public partial class ConditionalCriticalGatewayTests
{
    sealed class AccuracyDraws(params int[] values) : Random
    {
        public int Calls;
        public override int Next(int maxValue)
        {
            Assert.Equal(100, maxValue);
            int index = Interlocked.Increment(ref Calls) - 1;
            Assert.True(index < values.Length, "Unexpected accuracy draw");
            return values[index];
        }
    }

    [Theory]
    [InlineData("Rusted Sword", 0, 1500u)]
    [InlineData("Rusted Sword", 99, 1000u)]
    [InlineData("Crude Bow", 0, 1500u)]
    [InlineData("Crude Bow", 99, 1000u)]
    public async Task Ordinary_attack_critical_confirmation_can_downgrade_a_landed_hit(
        string weapon, int confirmation, uint damage)
    {
        var critical = new Draws(0);
        using var f = new Contact(critical, weaponName: weapon, distance: weapon == "Crude Bow" ? 60 : 2);
        f.Set("extra_critical_rolls_while_on_low_life", 0);
        f.Refresh();
        f.Conn.Pools = f.Conn.Pools with { Accuracy = 0 };
        var accuracy = new AccuracyDraws(99, confirmation); // First 5% attack hits through entropy.
        f.Conn.AttackAccuracyRandom = accuracy;
        await f.Use();
        await f.HitLands();
        Assert.Equal(damage, f.Target.MaxLife - f.Target.Life);
        Assert.Equal(2, accuracy.Calls);
        Assert.Equal(1, critical.Calls);
        Assert.Equal(4, f.Target.AttackEvasion.Value); // Confirmation did not advance entropy a second time.
    }

    [Theory]
    [InlineData("Rusted Sword", false)]
    [InlineData("Crude Bow", false)]
    [InlineData("Rusted Sword", true)]
    [InlineData("Crude Bow", true)]
    public async Task Ordinary_hits_cannot_be_evaded_bypass_both_accuracy_checks(string weapon, bool local)
    {
        using var f = new Contact(new Draws(0), weaponName: weapon, distance: weapon == "Crude Bow" ? 60 : 2);
        f.Set("extra_critical_rolls_while_on_low_life", 0);
        if (local)
        {
            var inventory = PlayerInventories.For(f.Conn.Character!, Program.Items);
            var item = Assert.Single(inventory.Items(PlayerInventories.Weapon1));
            var mod = Program.Items.Mods.ById("AlwaysHits")!;
            Assert.Contains(mod.Stats, stat => stat.Id == "local_always_hit" && stat.Local);
            Assert.NotNull(inventory.Change(PlayerInventories.Weapon1, item.Id,
                value => value with { Mods = [new(ModSection.Explicit, mod.Hash, [1])] }));
        }
        else f.Set("global_always_hit", 1);
        f.Refresh();
        f.Conn.Pools = f.Conn.Pools with { Accuracy = 0 };
        var accuracy = new AccuracyDraws();
        f.Conn.AttackAccuracyRandom = accuracy;
        await f.Use();
        await f.HitLands();
        Assert.Equal(1500u, f.Target.MaxLife - f.Target.Life);
        Assert.Equal(0, accuracy.Calls);
        Assert.Equal(-1, f.Target.AttackEvasion.Value);
    }

    [Fact]
    public async Task Evaded_ordinary_melee_contact_has_no_critical_roll_impale_or_on_hit_event()
    {
        var critical = new Draws();
        using var f = new Contact(critical);
        f.Target.Impale.Apply(100, now: 0);
        f.Conn.Pools = f.Conn.Pools with { Accuracy = 0 };
        var accuracy = new AccuracyDraws(0);
        f.Conn.AttackAccuracyRandom = accuracy;
        double before = f.Conn.LastEnemyHitAt;
        await f.Use();
        Assert.Equal(f.Target.MaxLife, f.Target.Life);
        Assert.Equal(before, f.Conn.LastEnemyHitAt);
        Assert.Equal(10, f.Target.Impale.Bonus);
        Assert.Equal(1, f.Target.Impale.Stacks);
        Assert.Equal(0, critical.Calls);
        Assert.Equal(1, accuracy.Calls);
    }

    [Fact]
    public async Task Cancelled_melee_contact_does_not_consume_accuracy()
    {
        using var f = new Contact(new Draws());
        var accuracy = new AccuracyDraws();
        f.Conn.AttackAccuracyRandom = accuracy;
        var use = f.Use();
        f.Conn.ActionCount++;
        await use;
        Assert.Equal(0, accuracy.Calls);
        Assert.Equal(-1, f.Target.AttackEvasion.Value);
        Assert.Equal(f.Target.MaxLife, f.Target.Life);
    }

    [Fact]
    public async Task Ordinary_arrow_passes_an_evading_target_and_resolves_the_next_defender_once()
    {
        var critical = new Draws(0);
        using var f = new Contact(critical, weaponName: "Crude Bow", distance: 20);
        f.Set("extra_critical_rolls_while_on_low_life", 0);
        f.Refresh();
        f.Conn.Pools = f.Conn.Pools with { Accuracy = 0 };
        f.Target.Impale.Apply(100, now: 0);
        var second = new SpawnedMonster("later collision", new(92, 32, 30), 0, 2, 100000);
        f.Conn.Monsters.Add(43, second);
        var accuracy = new AccuracyDraws(0, 99, 99); // First misses; second hits but fails critical confirmation.
        f.Conn.AttackAccuracyRandom = accuracy;
        await f.Use();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (second.Life == second.MaxLife) await Task.Delay(10, timeout.Token);
        Assert.Equal(f.Target.MaxLife, f.Target.Life);
        Assert.Equal(10, f.Target.Impale.Bonus);
        Assert.Equal(1000u, second.MaxLife - second.Life);
        Assert.Equal(5, f.Target.AttackEvasion.Value);
        Assert.Equal(4, second.AttackEvasion.Value);
        Assert.Equal(3, accuracy.Calls);
        Assert.Equal(1, critical.Calls);
    }
}
