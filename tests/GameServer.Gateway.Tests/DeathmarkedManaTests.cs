namespace GameServer.Gateway.Tests;

public sealed class DeathmarkedManaTests
{
    [Fact]
    public void Deathmarked_waives_mark_mana_and_keeps_energy_shield_costs()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var conn = f.Conn;
        ushort mark = Assert.Single(f.Equip(("Assassin's Mark", 1)));
        conn.PlayerMana = 0;
        Assert.False(Game.PayMana(conn, mark, out _, out _));

        conn.PlayerStats[Program.Stats.Key("mark_skills_cost_no_mana")!.Value] = 1;
        Assert.True(Game.PayMana(conn, mark, out uint mana, out uint life));
        Assert.Equal(0u, mana);
        Assert.Equal(0u, life);

        conn.PlayerStats[Program.Stats.Key("base_skill_gain_es_cost_%_of_mana_cost")!.Value] = 100;
        Assert.False(Game.PayMana(conn, mark, out _, out _));
        conn.EnergyShield.SetMaximum(100, refill: true);
        Assert.True(Game.PayMana(conn, mark, out mana, out _));
        Assert.Equal(0u, mana);
        Assert.True(conn.EnergyShield.Current < 100);

        ushort fireball = Assert.Single(f.Equip(("Fireball", 1)));
        Assert.False(Game.PayMana(conn, fireball, out _, out _));
    }
}
