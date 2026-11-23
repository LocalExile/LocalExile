using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class SocketColourAdmissionTests
{
    [Fact]
    public async Task Any_gem_fits_any_gem_socket()
    {
        string path = Path.Combine(Path.GetTempPath(), $"gem-colour-{Guid.NewGuid():N}.db");
        const string name = "GemColour";
        Saves.Open(path);
        using var wire = new SpellTotemExecutionTests.Wire();
        var conn = new Player(new(1, "socket", 100, 100, 100, 100, null, []), 1, 1)
        {
            Character = { Name = name },
            Connection = { Session = new("socket", wire.Stream, wire.Ciphers) },
        };
        try
        {
            conn.Pools = Game.PoolsFor(conn, 100) with { Strength = 100, Dexterity = 100, Intelligence = 100 };
            var catalog = Program.Items;
            var red = ItemCatalog.Create(catalog.Find("Heavy Strike")!);
            var blue = ItemCatalog.Create(catalog.Find("Fireball")!);
            Assert.Null(Game.GemRequirementsUnmet(conn, blue));
            var host = ItemCatalog.Create(catalog.Find("Rusted Sword")!,
                [new(SocketColour.Red, red)], [1]);
            var inventory = Game.ItemsOf(conn);
            byte row = PlayerInventories.MainInventory;
            foreach (var starter in inventory.Items(row).ToArray()) inventory.Remove(row, starter.Id);
            var placed = inventory.Add(row, 0, 0, host);
            inventory.Add(PlayerInventories.Cursor, 0, 0, blue);
            Task Socket(uint slot) => Game.SocketGem(conn,
                new(PlayerInventories.InventoryId(row), placed.Id, slot));
            Item CurrentHost() => Assert.Single(inventory.Items(row)).Item;
            // 3.29: socket colour only grants quality, so an off-colour gem replaces the one it is dropped on.
            await Socket(0);
            Assert.Same(blue, CurrentHost().Sockets[0].Gem);
            Assert.Same(red, Assert.Single(inventory.Items(PlayerInventories.Cursor)).Item);
        }
        finally
        {
            PlayerInventories.Forget(name);
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
