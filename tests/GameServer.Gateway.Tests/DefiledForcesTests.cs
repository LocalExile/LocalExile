using Kind = MonsterElementalAilments.Kind;

namespace GameServer.Gateway.Tests;

public sealed class DefiledForcesTests
{
    [Fact]
    public void Defiled_forces_refreshes_chill_and_shock_only_on_an_accepted_curse()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var conn = fixture.Conn;
        ushort skill = Assert.Single(fixture.Equip(("Flammability", 1)));
        var target = new SpawnedMonster("ailment target", new(100, 100, 0), 0, 0, 1000);
        int None(string _) => 0;
        target.Ailments.WithoutDamage(Kind.Chill, 2, 0, None, None, stated: 20);
        target.Ailments.WithoutDamage(Kind.Shock, 2, 0, None, None, stated: 30);
        target.Ailments.Freeze(2, 0, None, None);
        uint defiled = Program.Stats.Key("refresh_duration_of_shock_chill_ignite_on_enemy_when_cursing_enemy")!.Value;
        uint limit = Program.Stats.Key("number_of_additional_curses_allowed")!.Value;
        conn.PlayerStats[defiled] = 1;
        conn.PlayerStats[limit] = -1;
        Game.ApplyCurseLocked(conn, target, skill, 1, aura: false);
        Assert.Empty(target.Curses.All);
        Assert.Equal(1, target.Ailments.Remaining(Kind.Shock, 1));

        conn.PlayerStats.Remove(limit);
        Assert.True(Game.ApplyCurseLocked(conn, target, skill, 1.5, aura: false));
        Assert.Equal(20, target.Ailments.Magnitude(Kind.Chill, 2.25));
        Assert.Equal(30, target.Ailments.Magnitude(Kind.Shock, 2.25));
        Assert.Equal(0, target.Ailments.Magnitude(Kind.Freeze, 2.25));
        Assert.Equal(0, target.Ailments.Magnitude(Kind.Shock, 3.5));
    }
}
