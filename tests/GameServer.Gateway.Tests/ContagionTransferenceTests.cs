namespace GameServer.Gateway.Tests;

public sealed class ContagionTransferenceTests
{
    [Fact]
    public void A_hit_spreads_transference_to_nearby_enemies_without_resetting_either_debuff()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var conn = fixture.Conn;
        conn.Clock.Reset();
        var first = new SpawnedMonster("source", new(100, 100, 0), 0, 0, 1000);
        var nearby = new SpawnedMonster("nearby", new(105, 100, 0), 0, 0, 1000);
        var distant = new SpawnedMonster("distant", new(180, 100, 0), 0, 0, 1000);
        conn.Monsters[10] = first;
        conn.Monsters[11] = nearby;
        conn.Monsters[12] = distant;
        first.Contagion.Apply(-1, new MonsterContagion.Cast(100, 5, 20, Caster: conn) { SpreadOnHitChance = 100 });
        first.EssenceDrain.Apply(-1, new MonsterEssenceDrain.Cast(100, 5, Caster: conn));
        Assert.NotNull(Game.ApplyMonsterHitLocked(conn, first, 1));
        Assert.True(first.Life > 0);
        Assert.False(nearby.Contagion.Idle);
        Assert.False(nearby.EssenceDrain.Idle);
        Assert.True(distant.Contagion.Idle);
        Assert.Same(conn, nearby.Contagion.Active!.Caster);
        Assert.Equal(4, nearby.Contagion.Until);
        Assert.Equal(4, nearby.EssenceDrain.Until);

        Assert.NotNull(Game.ApplyMonsterHitLocked(conn, first, 1));
        Assert.Equal(4, nearby.Contagion.Until);
        Assert.Equal(4, nearby.EssenceDrain.Until);
        Assert.Null(first.Contagion.TakeSpread(1));
    }
}
