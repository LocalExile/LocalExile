using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class PerseveranceDamageTests
{
    [Fact]
    public void Perseverance_uses_whole_steps_of_the_lower_defence_for_attacks()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var conn = fixture.Conn;
        var items = Game.ItemsOf(conn);
        uint weaponId = Assert.Single(items.Items(PlayerInventories.Weapon1)).Id;
        var wand = ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!);
        items.Change(PlayerInventories.Weapon1, weaponId, item => wand with { Sockets = item.Sockets, Links = item.Links });
        fixture.Equip(("Power Siphon", 1), ("Fireball", 1));
        ushort siphon = Game.GemSkillEffects(conn).Single(skill => skill.Name == "Power Siphon").Id;
        ushort fireball = Game.GemSkillEffects(conn).Single(skill => skill.Name == "Fireball").Id;
        var belt = Assert.IsType<Item>(new UniqueCatalog(Program.Items).Create("Perseverance", new Random(11)));
        items.Put(PlayerInventories.Belt, new(100, 0, 0, belt));
        double Attack() => Game.SupportedHitFor(conn, siphon, suppress: false)[DamageType.Physical].Max;
        double Spell() => Game.SupportedHitFor(conn, fireball, suppress: false)[DamageType.Fire].Max;

        conn.Pools = conn.Pools with { Armour = 1500, Evasion = 299 };
        double baseline = Attack(), spell = Spell();
        Assert.True(baseline > 0);
        conn.Pools = conn.Pools with { Armour = 300, Evasion = 1500 };
        double boosted = Attack();
        Assert.True(boosted > baseline);
        conn.Pools = conn.Pools with { Armour = 10000, Evasion = 599 };
        Assert.Equal(boosted, Attack(), 8);
        Assert.Equal(spell, Spell(), 8);

        items.Remove(PlayerInventories.Belt, 100);
        Assert.Equal(baseline, Attack(), 8);
    }
}
