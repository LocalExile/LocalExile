using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class CovenantLifeCostTests
{
    [Fact]
    public void Covenant_adds_life_based_on_base_mana_and_rejects_an_unpayable_cost_atomically()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var conn = fixture.Conn;
        ushort fireball = Assert.Single(fixture.Equip(("Fireball", 1)));
        var covenant = Assert.IsType<Item>(new UniqueCatalog(Program.Items).Create("The Covenant", new Random(11)));
        Game.ItemsOf(conn).Put(PlayerInventories.BodyArmour, new(100, 0, 0, covenant));

        conn.PlayerStats[Program.Stats.Key("skill_mana_cost_+")!.Value] = 10;
        uint manaBefore = conn.PlayerMana;
        uint lifeBefore = conn.PlayerLife;
        Assert.True(Game.PayMana(conn, fireball, out uint mana, out uint life));
        Assert.Equal((15u, 5u), (mana, life));
        Assert.Equal((manaBefore - mana, lifeBefore - life), (conn.PlayerMana, conn.PlayerLife));

        conn.PlayerLife = life;
        manaBefore = conn.PlayerMana;
        Assert.False(Game.PayMana(conn, fireball, out _, out _));
        Assert.Equal((manaBefore, life), (conn.PlayerMana, conn.PlayerLife));
    }
}
