namespace GameServer.Gateway.Tests;

public sealed class EssenceDrainDesperationTests
{
    [Fact]
    public async Task Desperation_retains_fractional_loss_and_charges_the_caster_from_a_spread()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var conn = fixture.Conn;
        conn.Clock.Reset();
        conn.PlayerLife = 1000;
        ushort skill = Assert.Single(fixture.Equip(("Essence Drain of Desperation", 1)));
        var cast = Assert.IsType<MonsterEssenceDrain.Cast>(Game.EssenceDrainCast(conn, skill, Game.SupportedSkill(conn, skill).Effect));
        Assert.Equal(30, cast.LossPermyriad);
        conn.EnergyShield.SetMaximum(200, refill: true);
        async Task Tick() => await Game.TickEssenceDrains("desperation", fixture.Stream, fixture.Ciphers, conn);
        var original = new SpawnedMonster("original", new(100, 100, 0), 0, 0, 10000);
        conn.Monsters[10] = original;
        original.EssenceDrain.Apply(-1, cast with { DamagePerSecond = 200 });
        await Tick();
        Assert.Equal((1000u, 200u), (conn.PlayerLife, conn.EnergyShield.Current));
        var spread = Assert.IsType<MonsterEssenceDrain.Cast>(original.EssenceDrain.TakeSpread(0));
        var recipient = new SpawnedMonster("spread", new(105, 100, 0), 0, 0, 10000);
        conn.Monsters[11] = recipient;
        recipient.EssenceDrain.Apply(-1, spread);
        await Tick();
        Assert.Equal((999u, 199u), (conn.PlayerLife, conn.EnergyShield.Current));
    }
}
