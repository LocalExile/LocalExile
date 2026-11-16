using System.Reflection;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class SummonVariantTests
{
    static ushort Equip(AnimatedAdmissionTests.Fixture f, string name, string weapon = "Rusted Sword", int sockets = 1)
    {
        var items = Game.ItemsOf(f.Conn);
        foreach (var p in items.Items(PlayerInventories.Weapon1)) items.Remove(PlayerInventories.Weapon1, p.Id);
        var type = Program.Items.FindGem(name)!.Value;
        var gem = ItemCatalog.Create(type.Type) with { GemVariant = type.Variant.Index,
            GemExperience = Program.Items.Gem(type.Type)!.ExperienceFor(20) };
        var host = ItemCatalog.Create(Program.Items.Find(weapon)!) with
        {
            Sockets = Enumerable.Range(0, sockets).Select(i => new ItemSocket(SocketColour.White, i == 0 ? gem : null)).ToArray()
        };
        items.Add(PlayerInventories.Weapon1, 0, 0, host);
        return Game.GemSkillEffects(f.Conn).Single().Id;
    }

    [Fact]
    public async Task Self_reflection_copies_the_active_melee_weapon_and_never_consumes_ground_equipment()
    {
        using var f = new AnimatedAdmissionTests.Fixture();
        ushort source = Equip(f, "Animate Weapon of Self Reflection", sockets: 6);
        var equipped = Game.ItemsOf(f.Conn).Items(PlayerInventories.Weapon1).Single().Item;
        var definition = Game.SummonData.Find("AnimateWeaponAltX")!;
        Assert.True(Game.SupportedSkill(f.Conn, source).RequirementsMet);
        uint id = await Game.SummonFighter("test", f.Stream, f.Ciphers, f.Conn, source, definition, (100, 100));
        Assert.NotEqual(0u, id);
        Assert.Same(equipped, f.Conn.Summons.Fighters[id].Equipment[2]);
        var ground = new WorldItemObject(100, 100, ItemCatalog.Create(Program.Items.Find("Rusted Sword")!));
        uint groundId = f.Conn.Instance.Ground.Add(ground, f.Conn);
        id = await Game.SummonFighter("test", f.Stream, f.Ciphers, f.Conn, source, definition, (100, 100), groundId);
        Assert.NotEqual(0u, id); Assert.Same(ground, f.Conn.Instance.Ground.Get(groundId));
        Assert.Same(equipped, f.Conn.Summons.Fighters[id].Equipment[2]);
        Assert.Same(equipped, Game.ItemsOf(f.Conn).Items(PlayerInventories.Weapon1).Single().Item);
        int count = f.Conn.Summons.Fighters.Count;
        source = Equip(f, "Animate Weapon of Self Reflection", "Crude Bow");
        Assert.Equal(0u, await Game.SummonFighter("test", f.Stream, f.Ciphers, f.Conn, source, definition, (100, 100), groundId));
        Assert.Equal(count, f.Conn.Summons.Fighters.Count);
        Assert.Same(ground, f.Conn.Instance.Ground.Get(groundId));
    }
}
