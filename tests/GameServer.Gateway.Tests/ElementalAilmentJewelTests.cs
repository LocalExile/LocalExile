using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class ElementalAilmentJewelTests
{
    static InventoryItem Socket(ClientState conn, int slot, string name)
    {
        var tree = Game.PassivesFor(conn);
        conn.Passives.Add(tree.JewelNodeForSlot(slot)!.Value);
        var item = Assert.IsType<Item>(Game.UniquesFor(conn).Create(name, new Random(7)));
        return Game.ItemsOf(conn).Add(PlayerInventories.PassiveJewels, (byte)slot, 0, item);
    }

    static void Set(ClientState conn, string name, int value) => conn.PlayerStats[Program.Stats.Key(name)!.Value] = value;
    static int Read(IReadOnlyDictionary<uint, int> stats, string name) =>
        Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;

    [Fact]
    public void Stormshroud_and_Ancestral_Vision_add_avoidance_once_using_uncapped_suppression()
    {
        using var f = new MinionUtilityTests.Fixture();
        var conn = f.Conn;
        var storm = Socket(conn, 0, "Stormshroud");
        Set(conn, "base_avoid_shock_%", 60);
        Set(conn, "avoid_all_elemental_status_%", 10);
        var stats = Game.CharacterPlayerStats(conn);
        Assert.Equal(70, Game.ElementalAilmentDefences(conn, "shock", n => Read(stats, n)).AvoidancePct);
        Assert.Equal(70, Game.ElementalAilmentDefences(conn, "brittle", n => Read(stats, n)).AvoidancePct);

        Socket(conn, 1, "Ancestral Vision");
        stats = Game.CharacterPlayerStats(conn);
        conn.Pools = conn.Pools with { SpellSuppressionChance = 100, SpellSuppressionChanceUncapped = 140 };
        Assert.Equal(100, Game.Suppression(conn).Chance);
        Assert.Equal(100, Game.ElementalAilmentDefences(conn, "freeze", n => Read(stats, n)).AvoidancePct);
        Game.InflictPlayerElementalAilmentLocked(conn, PlayerElementalAilments.Kind.Freeze, 100, 2, 1, stats);
        Assert.Equal(1, conn.ElementalAilments.FreezeStatusUntil(1));

        Game.ItemsOf(conn).Remove(PlayerInventories.PassiveJewels, storm.Id);
        stats = Game.CharacterPlayerStats(conn);
        conn.Pools = conn.Pools with { SpellSuppressionChance = 100, SpellSuppressionChanceUncapped = 140 };
        Assert.Equal(80, Game.ElementalAilmentDefences(conn, "ignite", n => Read(stats, n)).AvoidancePct);
    }

    [Fact]
    public void Firesong_shares_reduced_and_less_ignite_duration_on_finite_ailments()
    {
        using var f = new MinionUtilityTests.Fixture();
        var conn = f.Conn;
        var jewel = Socket(conn, 1, "Firesong");
        Set(conn, "base_self_ignite_duration_-%", 35);
        Set(conn, "self_ignite_duration_+%_final", -60);
        Set(conn, "base_self_freeze_duration_-%", 15);
        Set(conn, "self_elemental_status_duration_-%", 5);
        var stats = Game.CharacterPlayerStats(conn);

        Assert.Equal(.24, Game.ElementalAilmentDefences(conn, "ignite", n => Read(stats, n)).DurationFactor, 6);
        Assert.Equal(.24, Game.ElementalAilmentDefences(conn, "scorch", n => Read(stats, n)).DurationFactor, 6);
        Game.InflictPlayerElementalAilmentLocked(conn, PlayerElementalAilments.Kind.Freeze, 100, 2, 1, stats);
        Assert.Equal(1.36, conn.ElementalAilments.FreezeStatusUntil(1), 6);
        Game.InflictPlayerElementalAilmentLocked(conn, PlayerElementalAilments.Kind.Chill, 20, .5, 2, stats, ground: true);
        Assert.Equal(20, conn.ElementalAilments.Magnitude(PlayerElementalAilments.Kind.Chill, 2.49));

        Game.ItemsOf(conn).Remove(PlayerInventories.PassiveJewels, jewel.Id);
        stats = Game.CharacterPlayerStats(conn);
        Game.InflictPlayerElementalAilmentLocked(conn, PlayerElementalAilments.Kind.Freeze, 100, 2, 3, stats);
        Assert.Equal(4.6, conn.ElementalAilments.FreezeStatusUntil(3), 6);
    }
}
