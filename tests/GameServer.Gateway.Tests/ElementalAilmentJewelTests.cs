using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class ElementalAilmentJewelTests
{
    static InventoryItem Socket(Player player, int slot, string name)
    {
        var tree = Game.PassivesFor(player);
        player.Character.Passives.Add(tree.JewelNodeForSlot(slot)!.Value);
        var item = Assert.IsType<Item>(Game.UniquesFor(player).Create(name, new Random(7)));
        return Game.ItemsOf(player).Add(PlayerInventories.PassiveJewels, (byte)slot, 0, item);
    }

    static Dictionary<uint, int> WithDefences(Player player, params (uint Key, int Value)[] modifiers)
    {
        var stats = new Dictionary<uint, int>(Game.CharacterPlayerStats(player));
        foreach (var (key, value) in modifiers) stats[key] = value;
        return stats;
    }

    [Fact]
    public void Stormshroud_and_Ancestral_Vision_add_avoidance_once_using_uncapped_suppression()
    {
        using var f = new MinionUtilityTests.Fixture();
        var player = f.Player;
        var storm = Socket(player, 0, "Stormshroud");
        Dictionary<uint, int> Stats() => WithDefences(player, (Stat.AvoidShockPct, 70),
            (Stat.AvoidAllElementalStatusPct, 10), (Stat.AvoidFreezePct, 10), (Stat.AvoidIgnitePct, 10));
        var stats = Stats();
        Assert.Equal(70, Game.ElementalAilmentDefences(player, "shock", key => stats.GetValueOrDefault(key)).AvoidancePct);
        Assert.Equal(70, Game.ElementalAilmentDefences(player, "brittle", key => stats.GetValueOrDefault(key)).AvoidancePct);

        Socket(player, 1, "Ancestral Vision");
        stats = Stats();
        player.Pools = player.Pools with { SpellSuppressionChance = 100, SpellSuppressionChanceUncapped = 140 };
        Assert.Equal(100, Game.Suppression(player).Chance);
        Assert.Equal(100, Game.ElementalAilmentDefences(player, "freeze", key => stats.GetValueOrDefault(key)).AvoidancePct);
        Game.InflictPlayerElementalAilmentLocked(player, PlayerElementalAilments.Kind.Freeze, 100, 2, 1, stats);
        Assert.Equal(1, player.ElementalAilments.FreezeStatusUntil(1));

        Game.ItemsOf(player).Remove(PlayerInventories.PassiveJewels, storm.Id);
        stats = Stats();
        player.Pools = player.Pools with { SpellSuppressionChance = 100, SpellSuppressionChanceUncapped = 140 };
        Assert.Equal(80, Game.ElementalAilmentDefences(player, "ignite", key => stats.GetValueOrDefault(key)).AvoidancePct);
    }

    [Fact]
    public void Firesong_shares_reduced_and_less_ignite_duration_on_finite_ailments()
    {
        using var f = new MinionUtilityTests.Fixture();
        var player = f.Player;
        var jewel = Socket(player, 1, "Firesong");
        Dictionary<uint, int> Stats() => WithDefences(player, (Stat.SelfIgniteDurationMinusPct, 40),
            (Stat.SelfIgniteDurationPlusPctFinal, -60), (Stat.SelfFreezeDurationMinusPct, 20),
            (Stat.SelfElementalStatusDurationMinusPct, 5));
        var stats = Stats();

        Assert.Equal(.24, Game.ElementalAilmentDefences(player, "ignite", key => stats.GetValueOrDefault(key)).DurationFactor, 6);
        Assert.Equal(.24, Game.ElementalAilmentDefences(player, "scorch", key => stats.GetValueOrDefault(key)).DurationFactor, 6);
        Game.InflictPlayerElementalAilmentLocked(player, PlayerElementalAilments.Kind.Freeze, 100, 2, 1, stats);
        Assert.Equal(1.36, player.ElementalAilments.FreezeStatusUntil(1), 6);
        Game.InflictPlayerElementalAilmentLocked(player, PlayerElementalAilments.Kind.Chill, 20, .5, 2, stats, ground: true);
        Assert.Equal(20, player.ElementalAilments.Magnitude(PlayerElementalAilments.Kind.Chill, 2.49));

        Game.ItemsOf(player).Remove(PlayerInventories.PassiveJewels, jewel.Id);
        stats = Stats();
        Game.InflictPlayerElementalAilmentLocked(player, PlayerElementalAilments.Kind.Freeze, 100, 2, 3, stats);
        Assert.Equal(4.6, player.ElementalAilments.FreezeStatusUntil(3), 6);
    }
}
