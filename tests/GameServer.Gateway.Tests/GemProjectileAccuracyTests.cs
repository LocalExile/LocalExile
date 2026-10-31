using GameServer.Core.World;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public partial class ConditionalCriticalGatewayTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(99, false)]
    public async Task Parsed_gem_projectile_confirms_critical_hits_with_selected_accuracy(int confirmation, bool confirmed)
    {
        var critical = new Draws(0);
        using var f = new Contact(critical, "BurningArrow", "Crude Bow", distance: 30);
        f.Set("extra_critical_rolls_while_on_low_life", 0);
        f.Conn.Pools = f.Conn.Pools with { Accuracy = 0 };
        var accuracy = new AccuracyDraws(99, confirmation);
        f.Conn.AttackAccuracyRandom = accuracy;
        var packet = f.Hit;
        await f.Use();
        await f.HitLands();
        Assert.Equal((uint)Math.Round(packet.MinTotal * (confirmed ? f.Multiplier / 100.0 : 1)),
            f.Target.MaxLife - f.Target.Life);
        Assert.Equal(2, accuracy.Calls);
        Assert.Equal(1, critical.Calls);
        Assert.Equal(4, f.Target.AttackEvasion.Value);
    }

    [Fact]
    public async Task Gem_projectile_passes_an_evading_target_and_confirms_the_later_hit()
    {
        var critical = new Draws(0);
        using var f = new Contact(critical, "BurningArrow", "Crude Bow", distance: 30);
        f.Set("extra_critical_rolls_while_on_low_life", 0);
        f.Conn.Pools = f.Conn.Pools with { Accuracy = 0 };
        f.Target.Impale.Apply(100, now: 0);
        var second = new SpawnedMonster("later target", new(92, 32, 30), 0, 0, 100000);
        f.Conn.Monsters.Add(43, second);
        var accuracy = new AccuracyDraws(0, 99, 99);
        f.Conn.AttackAccuracyRandom = accuracy;
        var packet = f.Hit;
        await f.Launch();
        Assert.Equal(f.Target.MaxLife, f.Target.Life);
        Assert.Equal((uint)Math.Round(packet.MinTotal), second.MaxLife - second.Life);
        Assert.Equal(5, f.Target.AttackEvasion.Value);
        Assert.Equal(4, second.AttackEvasion.Value);
        Assert.Equal(10, f.Target.Impale.Bonus);
        Assert.Equal(3, accuracy.Calls);
        Assert.Equal(1, critical.Calls);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 99)]
    [InlineData(true, 0)]
    [InlineData(true, 99)]
    public async Task Piercing_and_returning_gem_contacts_confirm_before_damage_scaling(bool returning, int confirmation)
    {
        var critical = new Draws(0);
        using var f = new Contact(critical, "BurningArrow", "Crude Bow", distance: returning ? -12 : 30);
        f.Set("extra_critical_rolls_while_on_low_life", 0);
        f.Set("projectile_base_number_of_targets_to_pierce", 1);
        if (returning)
        {
            f.Set("attack_projectiles_return", 1);
            f.Set("support_return_returning_projectiles_damage_+%_final", -60);
        }
        f.Conn.Pools = f.Conn.Pools with { Accuracy = 0 };
        var accuracy = new AccuracyDraws(99, confirmation);
        f.Conn.AttackAccuracyRandom = accuracy;
        int direct = (int)Math.Round(f.Hit.MinTotal);
        int expected = confirmation == 0 ? DamageModel.CriticalDamage(direct, f.Multiplier) : direct;
        if (returning) expected = (int)Math.Floor(expected * 0.4);
        await f.Launch((92, 32));
        Assert.Equal((uint)expected, f.Target.MaxLife - f.Target.Life);
        Assert.Equal(2, accuracy.Calls);
        Assert.Equal(1, critical.Calls);
        Assert.Equal(4, f.Target.AttackEvasion.Value);
    }

    [Fact]
    public async Task Gem_projectile_cannot_be_evaded_bypasses_both_accuracy_checks()
    {
        using var f = new Contact(new Draws(0), "BurningArrow", "Crude Bow", distance: 30);
        f.Set("extra_critical_rolls_while_on_low_life", 0);
        f.Set("global_always_hit", 1);
        f.Conn.Pools = f.Conn.Pools with { Accuracy = 0 };
        var accuracy = new AccuracyDraws();
        f.Conn.AttackAccuracyRandom = accuracy;
        var packet = f.Hit;
        await f.Launch();
        Assert.Equal((uint)Math.Round(packet.MinTotal * f.Multiplier / 100.0), f.Target.MaxLife - f.Target.Life);
        Assert.Equal(0, accuracy.Calls);
        Assert.Equal(-1, f.Target.AttackEvasion.Value);
    }

    [Fact]
    public async Task Gem_projectile_uses_real_Additional_Accuracy_support_for_both_checks()
    {
        using var f = new Contact(new Draws(0), "BurningArrow", "Crude Bow", distance: 30, targetLevel: 100);
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
        int rating = (int)((f.Conn.Pools.AccuracyBase + 74) * (120 + f.Conn.Pools.AccuracyPct) / 100);
        int chance = DamageModel.AccuracyHitChance(13445, rating);
        Assert.InRange(chance, 6, 99);
        Assert.True(chance > DamageModel.AccuracyHitChance(13445, f.Conn.Pools.Accuracy));
        var packet = f.Hit;
        await f.Launch();
        Assert.Equal((uint)Math.Round(packet.MinTotal), f.Target.MaxLife - f.Target.Life);
        Assert.Equal(chance - 1, f.Target.AttackEvasion.Value);
        Assert.Equal(2, accuracy.Calls);
    }

    [Fact]
    public async Task Spell_projectile_never_rolls_attack_accuracy()
    {
        using var f = new Contact(new Draws(0), "Fireball", "Crude Bow", distance: 30);
        f.Set("extra_critical_rolls_while_on_low_life", 0);
        f.Conn.Pools = f.Conn.Pools with { Accuracy = 0 };
        var accuracy = new AccuracyDraws();
        f.Conn.AttackAccuracyRandom = accuracy;
        var packet = f.Hit;
        await f.Launch();
        Assert.InRange(f.Target.MaxLife - f.Target.Life,
            (uint)Math.Round(packet.MinTotal * f.Multiplier / 100.0),
            (uint)Math.Round(packet.MaxTotal * f.Multiplier / 100.0));
        Assert.Equal(0, accuracy.Calls);
        Assert.Equal(-1, f.Target.AttackEvasion.Value);
    }

    [Fact]
    public async Task One_attack_projectile_volley_has_one_direct_hit_per_target()
    {
        var critical = new Draws(0);
        using var f = new Contact(critical, "BurningArrow", "Crude Bow"); // All three routes overlap this nearby target.
        f.Set("extra_critical_rolls_while_on_low_life", 0);
        f.Set("global_always_hit", 1);
        f.Set("number_of_additional_projectiles", 2);
        var accuracy = new AccuracyDraws();
        f.Conn.AttackAccuracyRandom = accuracy;
        var packet = f.Hit;
        uint firstObject = f.Conn.NextObjectId;
        await f.Launch();
        // An attack gem's arrows are not client-predicted (Frida 2026-10-31): each of the three gets a server object, and
        // still the target takes one direct hit.
        Assert.Equal(firstObject + 3, f.Conn.NextObjectId);
        Assert.Equal((uint)Math.Round(packet.MinTotal * f.Multiplier / 100.0), f.Target.MaxLife - f.Target.Life);
        Assert.Equal(1, critical.Calls);
        Assert.Equal(0, accuracy.Calls);
    }
}
