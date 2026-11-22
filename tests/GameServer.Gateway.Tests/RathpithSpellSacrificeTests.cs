using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class RathpithSpellSacrificeTests
{
    [Fact]
    public void Rathpith_sacrifices_current_life_once_on_successful_spell_use_or_trigger()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var conn = fixture.Conn;
        var items = Game.ItemsOf(conn);
        uint weaponId = Assert.Single(items.Items(PlayerInventories.Weapon1)).Id;
        var wand = ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!);
        items.Change(PlayerInventories.Weapon1, weaponId, _ => wand);
        var shield = Assert.IsType<Item>(new UniqueCatalog(Program.Items).Create("Rathpith Globe", new Random(11)));
        items.Put(PlayerInventories.Offhand1, new(100, 0, 0, shield));
        ushort fireball = Assert.Single(fixture.Equip(("Fireball", 1)));
        conn.PlayerLife = 1000;
        conn.PlayerMana = 0;
        Assert.False(Game.PayMana(conn, fireball, out _, out _));
        Assert.Equal(1000u, conn.PlayerLife);
        conn.PlayerMana = 500;
        Assert.True(Game.PayMana(conn, fireball, out _, out uint life));
        Assert.Equal((100u, 900u), (life, conn.PlayerLife));
        Assert.True(Game.PayMana(conn, fireball, out _, out life));
        Assert.Equal((90u, 810u), (life, conn.PlayerLife));

        ushort linked = Assert.Single(fixture.Equip(("Fireball", 1), ("Cast when Damage Taken Support", 1)));
        var gem = Game.SupportedSkill(conn, linked);
        int threshold = gem.Stats[Program.Stats.Key("cast_on_damage_taken_threshold")!.Value];
        Game.RecordDamageTakenTriggers(conn, threshold, 1);
        Game.ReleaseDamageTakenTriggers(conn, fixture.Session);
        Assert.Equal(729u, conn.PlayerLife);
        Assert.NotNull(Game.GemSkillInstanceDisabled(conn, gem.Effect, linked));
    }
}
