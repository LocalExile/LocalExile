using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class AbyssJewelTests
{
    [Fact]
    public void A_stygian_socket_accepts_eyes_and_preserves_them_across_reload_and_removal()
    {
        var items = Program.Items;
        var type = items.Find("Stygian Vise")!;
        var belt = ItemCatalog.Create(type);
        Assert.Equal(SocketColour.Abyss, Assert.Single(belt.Sockets).Colour);
        var legacy = CharacterSaves.ToSaved(belt with { Sockets = [] }, items.Mods);
        Assert.Equal(SocketColour.Abyss, Assert.Single(CharacterSaves.FromSaved(legacy, items).Sockets).Colour);
        Assert.Null(CurrencyEffects.Chromatic(belt, default, new Random(1)));
        var gem = ItemCatalog.Create(items.Find("Fireball")!);
        var eye = ItemCatalog.Create(items.Find("Murderous Eye Jewel")!);
        var path = Path.Combine(Path.GetTempPath(), $"abyss-socket-{Guid.NewGuid():N}.db");
        const string name = "AbyssSocket";
        Saves.Open(path);
        try
        {
            var inv = PlayerInventories.For(name, items);
            inv.Put(PlayerInventories.MainInventory, new(81, 6, 0, belt));
            inv.Put(PlayerInventories.Cursor, new(82, 0, 0, gem));
            Assert.Null(inv.Socket(PlayerInventories.MainInventory, 81, 0, Game.ShapeOf, inv.NewItemId, out _));
            inv.Remove(PlayerInventories.Cursor, 82);
            inv.Put(PlayerInventories.Cursor, new(83, 0, 0, eye));
            var white = belt with { Sockets = [new(SocketColour.White)] };
            inv.Put(PlayerInventories.MainInventory, new(84, 8, 0, white));
            Assert.Null(inv.Socket(PlayerInventories.MainInventory, 84, 0, Game.ShapeOf, inv.NewItemId, out _));
            Assert.NotNull(inv.Socket(PlayerInventories.MainInventory, 81, 0, Game.ShapeOf, inv.NewItemId, out _));
            PlayerInventories.Forget(name);
            inv = PlayerInventories.For(name, items);
            var removed = inv.Unsocket(PlayerInventories.MainInventory, 81, 0, inv.NewItemId, out _);
            Assert.Equal(eye.TypeHash, removed!.Value.Held.Item.TypeHash);
        }
        finally { PlayerInventories.Forget(name); TestEnvironment.ReleaseSaves(path); }
    }

    [Fact]
    public void Passive_and_equipment_eyes_contribute_once_and_follow_the_active_host()
    {
        var path = Path.Combine(Path.GetTempPath(), $"abyss-stats-{Guid.NewGuid():N}.db");
        Saves.Open(path);
        var items = Program.Items;
        var conn = new ClientState(new(1, "abyss", 100, 100, 100, 100, null, []), 1, 1) { Character = "AbyssStats" };
        try
        {
            var eye = ItemCatalog.Create(items.Find("Murderous Eye Jewel")!) with
                { Rarity = ItemRarity.Magic, Mods = [new(ModSection.Explicit, items.Mods.ById("FireResist1")!.Hash, [10])] };
            var socket = Program.Passives.JewelNodeForSlot(1)!.Value;
            conn.Passives.Add(socket);
            Assert.Null(Game.PassiveJewelRefusal(conn, 1, 0, eye));
            Game.ItemsOf(conn).Put(PlayerInventories.PassiveJewels, new(99, 1, 0, eye));
            var belt = new UniqueCatalog(items).Create("Darkness Enthroned", new Random(15))!;
            Assert.Equal(2, belt.Sockets.Count);
            Assert.All(belt.Sockets, s => Assert.Equal(SocketColour.Abyss, s.Colour));
            belt = belt with { Sockets = [belt.Sockets[0] with { Gem = eye }, belt.Sockets[1]] };
            var stats = Game.EquipmentStatSources(conn, [(10, new(105, 0, 0, belt))], [socket], 100).Sources;
            int effect = ItemModStats.Local(belt, items.Mods)["local_socketed_abyss_jewel_effect_+%"];
            Assert.Equal(10 + RadiusJewelCatalogue.ScaleEffect(10, effect), stats[Program.Stats.Key("base_fire_damage_resistance_%")!.Value]);
            Assert.Equal(2, stats[Program.Stats.Key("number_of_melee_abyss_jewels")!.Value]);
            Assert.Equal(1, stats[Program.Stats.Key("number_of_abyss_jewel_types")!.Value]);
            var removed = Game.EquipmentStatSources(conn, [], [], 100).Sources;
            Assert.Equal(0, removed.GetValueOrDefault(Program.Stats.Key("number_of_abyss_jewels")!.Value));
        }
        finally { PlayerInventories.Forget(conn.Character!); TestEnvironment.ReleaseSaves(path); }
    }
}
