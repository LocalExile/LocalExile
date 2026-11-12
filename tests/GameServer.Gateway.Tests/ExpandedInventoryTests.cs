namespace GameServer.Gateway.Tests;

public sealed class ExpandedInventoryTests
{
    [Fact]
    public void Native_grid_keeps_item_shapes_capacity_and_stack_limits()
    {
        using var f = new Fixture();
        var items = Game.ItemsOf(f.Conn);
        var gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemFireball")!);
        for (byte y = 0; y < 5; y++)
            for (byte x = 0; x < 4; x++)
            {
                items.Add(PlayerInventories.Cursor, 0, 0, gem);
                Assert.NotNull(items.Place(PlayerInventories.ExpandedInventory, x, y, Game.ShapeOf, out _, expandedAllowed: true));
            }
        Assert.Equal(20, items.Items(PlayerInventories.ExpandedInventory).Count);
        items.Add(PlayerInventories.Cursor, 0, 0, gem);
        Assert.Null(items.Place(PlayerInventories.ExpandedInventory, 4, 0, Game.ShapeOf, out _, expandedAllowed: true));
        Assert.Single(items.Items(PlayerInventories.Cursor));
        Assert.Equal(20, CharacterSaves.Load(f.Conn.Character!)!.Inventories[PlayerInventories.ExpandedInventory].Count);
        items.Remove(PlayerInventories.Cursor, items.Items(PlayerInventories.Cursor)[0].Id);
        foreach (var item in items.Items(PlayerInventories.ExpandedInventory)) items.Remove(PlayerInventories.ExpandedInventory, item.Id);
        var armour = ItemCatalog.Create(Program.Items.Find("Plate Vest")!);
        items.Add(PlayerInventories.Cursor, 0, 0, armour);
        Assert.Null(items.Place(PlayerInventories.ExpandedInventory, 3, 4, Game.ShapeOf, out _, expandedAllowed: true));
        Assert.NotNull(items.Place(PlayerInventories.ExpandedInventory, 0, 0, Game.ShapeOf, out _, expandedAllowed: true));
        var chaos = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Currency/CurrencyRerollRare")!);
        items.Add(PlayerInventories.Cursor, 0, 0, chaos);
        var stack = items.Place(PlayerInventories.ExpandedInventory, 3, 4, Game.ShapeOf, out _, expandedAllowed: true)!;
        items.Add(PlayerInventories.Cursor, 0, 0, chaos);
        Assert.True(items.MergeOnto(PlayerInventories.ExpandedInventory, stack.Placed.Id, Game.ShapeOf, out _, expandedAllowed: true)!.Merged);
        Assert.Equal(2, items.Items(PlayerInventories.ExpandedInventory).Single(i => i.Id == stack.Placed.Id).Item.StackSize);
    }

    [Fact]
    public void Removing_grant_keeps_saved_contents_visible_but_rejects_both_deposit_packets()
    {
        using var f = new Fixture();
        uint key = Program.Stats.Key(Game.ExpandedInventoryStat)!.Value;
        var items = Game.ItemsOf(f.Conn);
        f.Conn.PlayerStats[key] = 1;
        Assert.True(Game.ExpandedInventoryGranted(f.Conn));
        var chaos = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Currency/CurrencyRerollRare")!);
        items.Add(PlayerInventories.Cursor, 0, 0, chaos);
        var placed = items.Place(PlayerInventories.ExpandedInventory, 0, 0, Game.ShapeOf, out _,
            expandedAllowed: Game.ExpandedInventoryGranted(f.Conn))!;
        f.Conn.PlayerStats[key] = 0;
        Assert.False(Game.ExpandedInventoryGranted(f.Conn));
        Assert.Equal(2, Game.ExpandedInventoryMode(f.Conn));
        Dictionary<uint, int> visible = new() { [key] = 0 };
        Game.KeepExpandedInventoryVisible(f.Conn, visible);
        Assert.Equal(1, visible[key]);
        items.Add(PlayerInventories.Cursor, 0, 0, chaos);
        Assert.Null(items.Place(PlayerInventories.ExpandedInventory, 1, 0, Game.ShapeOf, out _));
        Assert.Null(items.MergeOnto(PlayerInventories.ExpandedInventory, placed.Placed.Id, Game.ShapeOf, out _));
        Assert.Equal(1, Assert.Single(items.Items(PlayerInventories.Cursor)).Item.StackSize);
        Assert.Equal(1, Assert.Single(items.Items(PlayerInventories.ExpandedInventory)).Item.StackSize);
        items.Remove(PlayerInventories.Cursor, items.Items(PlayerInventories.Cursor)[0].Id);
        PlayerInventories.Forget(f.Conn.Character!);
        items = Game.ItemsOf(f.Conn);
        Assert.NotNull(items.PickUp(PlayerInventories.ExpandedInventory, placed.Placed.Id));
        Assert.NotNull(items.Place(PlayerInventories.MainInventory, 5, 0, Game.ShapeOf, out _));
        Assert.Equal(0, Game.ExpandedInventoryMode(f.Conn));
        visible.Clear(); Game.KeepExpandedInventoryVisible(f.Conn, visible);
        Assert.False(visible.ContainsKey(key));
        Assert.False(CharacterSaves.Load(f.Conn.Character!)!.Inventories.ContainsKey(PlayerInventories.ExpandedInventory));
    }

    sealed class Fixture : IDisposable
    {
        readonly GameServer.Modding.IModuleLifetime? previousRules = Game.CombatLifetime;
        readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
        readonly ModuleLifetime rules = new("Game");
        readonly string path = Path.Combine(Path.GetTempPath(), $"backpack-{Guid.NewGuid():N}.db");
        internal readonly ClientState Conn = new(new(1, "backpack", 300, 300, 100, 100, null, []), 1, 1)
        { Character = "Backpack" + Guid.NewGuid().ToString("N"), PlayerLife = 300, PlayerMana = 100 };
        internal Fixture()
        {
            Saves.Open(path);
            Game.CombatLifetime = rules;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(rules));
        }
        public void Dispose()
        {
            rules.Stop(_ => { });
            Game.CombatLifetime = previousRules;
            Program.SetGameLogic(previousLogic!);
            PlayerInventories.Forget(Conn.Character!);
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
