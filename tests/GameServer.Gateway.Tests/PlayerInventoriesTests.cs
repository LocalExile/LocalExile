using GameServer.Core.Protocol.Instance;
using GameServer.Core.Saves;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

public class PlayerInventoriesTests
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), $"saves-{Guid.NewGuid():N}");
    readonly string _character = $"Picker{Guid.NewGuid():N}"[..20];

    public PlayerInventoriesTests() => TestSaves.Ensure();

    // No item tables in this folder: the character starts with no kit, and items are kept by their type hash.
    PlayerInventories NewInventories() => PlayerInventories.For(_character, new ItemCatalog(_folder));

    [Fact]
    public void Items_changed_concurrently_are_saved_as_they_end()
    {
        var items = NewInventories();

        // Every change saves; an older copy of the items must never land on disk after a newer one.
        Parallel.For(0, 16, t =>
        {
            for (int i = 0; i < 40; i++)
            {
                var it = items.Add(PlayerInventories.MainInventory, (byte)t, (byte)(i % 5), Wand());
                if (i % 2 == 0) items.Remove(PlayerInventories.MainInventory, it.Id);
            }
        });

        var saved = CharacterSaves.Load(_character)!.Inventories.GetValueOrDefault(PlayerInventories.MainInventory) ?? [];
        Assert.Equal(items.Items(PlayerInventories.MainInventory).Select(i => i.Id).Order(), saved.Select(p => p.Id).Order());
    }

    static Item Wand() => new(0xd9b1a12f, ["Base"], 0x40a1);

    // Orbs of Transmutation: 1×1, 40 to a stack.
    const uint Orb = 0x1234abcd;
    static Item Orbs(int n) => new(Orb, ["Base", "Usable", "Stack"], 0x1111) { StackSize = (ushort)n };
    // Scroll Fragments: 5 to a stack, a full stack becomes a Scroll of Wisdom.
    const uint Fragment = 0x5555aaaa, Wisdom = 0x7777bbbb;
    static Item Fragments(int n) => new(Fragment, ["Base", "Stack"], 0x2222) { StackSize = (ushort)n };
    static PlayerInventories.Shape? Shapes(Item i) => i.TypeHash switch
    {
        Orb => new(1, 1, "StackableCurrency", 40),
        Fragment => new(1, 1, "StackableCurrency", 5, () => new Item(Wisdom, ["Base", "Usable", "Stack"], 0x3333)),
        Wisdom => new(1, 1, "StackableCurrency", 40),
        _ => new(1, 3, "Wand"),
    };

    // Hands: a bow goes with an empty off hand or a quiver, a quiver only with a bow or an empty main hand.
    const uint BowType = 0xb0b0b0b0, QuiverType = 0xb1b1b1b1, SwordType = 0xb2b2b2b2, ShieldType = 0xb3b3b3b3, AxeType = 0xb4b4b4b4;
    static PlayerInventories.Shape? HandShapes(Item i) => i.TypeHash switch
    {
        BowType => new(2, 4, "Bow"),
        QuiverType => new(2, 3, "Quiver"),
        SwordType => new(1, 3, "One Hand Sword"),
        ShieldType => new(2, 2, "Shield"),
        AxeType => new(2, 4, "Two Hand Axe"),
        _ => null,
    };

    static string? Equip(PlayerInventories items, byte row, uint type)
    {
        items.Put(PlayerInventories.Cursor, new InventoryItem(items.NewItemId(), 0, 0, new Item(type, ["Base"], 0x1)));
        if (items.Place(row, 0, 0, HandShapes, out string why) is not null) return null;
        items.PickUp(PlayerInventories.Cursor, items.Items(PlayerInventories.Cursor).Single().Id);   // clear the cursor
        return why;
    }

    InventoryItem Hold(PlayerInventories items, Item item)
    {
        var placed = items.Add(PlayerInventories.MainInventory, 11, 4, item);
        return items.PickUp(PlayerInventories.MainInventory, placed.Id)!;
    }

}
