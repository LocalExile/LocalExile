using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class SocketColourAdmissionTests
{
    [Fact]
    public async Task Socketing_checks_colour_before_replacing_a_gem_or_consuming_the_cursor()
    {
        string path = Path.Combine(Path.GetTempPath(), $"gem-colour-{Guid.NewGuid():N}.db");
        const string name = "GemColour";
        Saves.Open(path);
        using var wire = new SpellTotemExecutionTests.Wire();
        var conn = new ClientState(new(1, "socket", 100, 100, 100, 100, null, []), 1, 1) { Character = name };
        try
        {
            conn.Pools = Game.PoolsFor(conn, 100) with { Strength = 100, Dexterity = 100, Intelligence = 100 };
            var catalog = Program.Items;
            var red = ItemCatalog.Create(catalog.Find("Heavy Strike")!);
            var blue = ItemCatalog.Create(catalog.Find("Fireball")!);
            var white = ItemCatalog.Create(catalog.Find("Portal")!);
            Assert.Null(Game.GemRequirementsUnmet(conn, blue));
            Assert.Null(Game.GemRequirementsUnmet(conn, white));
            var host = ItemCatalog.Create(catalog.Find("Rusted Sword")!,
                [new(SocketColour.Red, red), new(SocketColour.Blue), new(SocketColour.White)], [3]);
            var inventory = Game.ItemsOf(conn);
            byte row = PlayerInventories.MainInventory;
            var placed = inventory.Add(row, 0, 0, host);
            var held = inventory.Add(PlayerInventories.Cursor, 0, 0, blue);
            Task Socket(uint slot) => Game.SocketGem("socket", wire.Stream, wire.Ciphers, conn,
                new(PlayerInventories.InventoryId(row), placed.Id, slot));
            Item CurrentHost() => Assert.Single(inventory.Items(row)).Item;
            await Socket(0);
            Assert.Same(host, CurrentHost());
            Assert.Equal(held, Assert.Single(inventory.Items(PlayerInventories.Cursor)));
            await Socket(1);
            Assert.Same(blue, CurrentHost().Sockets[1].Gem);
            Assert.Empty(inventory.Items(PlayerInventories.Cursor));
            inventory.Add(PlayerInventories.Cursor, 0, 0, blue);
            await Socket(2);
            Assert.Same(blue, CurrentHost().Sockets[2].Gem);
            inventory.Add(PlayerInventories.Cursor, 0, 0, white);
            await Socket(0);
            Assert.Same(white, CurrentHost().Sockets[0].Gem);
            Assert.Same(red, Assert.Single(inventory.Items(PlayerInventories.Cursor)).Item);
        }
        finally
        {
            PlayerInventories.Forget(name);
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
