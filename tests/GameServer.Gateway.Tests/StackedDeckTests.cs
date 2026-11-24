using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class StackedDeckTests
{
    static byte[] UseItemClickPacket(uint inventoryId, uint itemId, byte flag = 0)
    {
        byte[] packet = new byte[InstanceItems.UseItemClickLength];
        BinaryPrimitives.WriteUInt16BigEndian(packet, InstanceItems.UseItemClickId);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(2), inventoryId);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(6), itemId);
        packet[10] = flag;
        return packet;
    }

    [Fact]
    public async Task Right_clicking_stacked_deck_in_inventory_draws_card_to_cursor_or_inventory_and_refuses_when_full()
    {
        string save = Path.Combine(Path.GetTempPath(), $"stacked-deck-inv-{Guid.NewGuid():N}.db");
        Saves.Open(save);
        using var runtime = new PartyFollowerTests.Runtime();
        using var wire = new PartyFollowerTests.Wire();
        string name = "StackedDeckInv" + Guid.NewGuid().ToString("N");
        var player = runtime.PlayerOf(new(), wire, name);
        var items = Game.ItemsOf(player);
        const byte main = PlayerInventories.MainInventory;
        uint mainInvId = PlayerInventories.InventoryId(main);
        try
        {
            foreach (var existing in items.Items(main).ToArray()) items.Remove(main, existing.Id);
            var deckType = Program.Items.Find(Game.StackedDeck);
            Assert.NotNull(deckType);
            var deck = items.Add(main, 0, 0, ItemCatalog.Create(deckType!) with { StackSize = 3 });

            // 1. Empty cursor: right-clicking via C->S 0x0057 consumes one deck and puts the drawn card on the cursor.
            byte[] packet = UseItemClickPacket(mainInvId, deck.Id);
            await Game.OnClientMessages([(InstanceItems.UseItemClickId, packet)], [], player);
            var onCursor = Assert.Single(items.Items(PlayerInventories.Cursor));
            var drawnType = Program.Items.ByHash(onCursor.Item.TypeHash);
            Assert.NotNull(drawnType);
            Assert.Equal("DivinationCard", drawnType!.Class);
            Assert.Equal(2, items.Items(main).Single(i => i.Id == deck.Id).Item.StackSize);

            // 2. Occupied cursor: opening another deck places the drawn card in main inventory without overwriting the cursor.
            await Game.OnClientMessages([(InstanceItems.UseItemClickId, packet)], [], player);
            Assert.Equal(onCursor.Id, Assert.Single(items.Items(PlayerInventories.Cursor)).Id);
            Assert.Equal(1, items.Items(main).Single(i => i.Id == deck.Id).Item.StackSize);
            var inMain = items.Items(main).Where(i => i.Id != deck.Id).ToArray();
            var mainCard = Assert.Single(inMain);
            Assert.Equal("DivinationCard", Program.Items.ByHash(mainCard.Item.TypeHash)?.Class);

            // 3. Persistence check before filling the inventory.
            PlayerInventories.Forget(name);
            var restored = PlayerInventories.For(name, Program.Items);
            Assert.Equal(1, restored.Items(main).Single(i => i.Id == deck.Id).Item.StackSize);
            Assert.Single(restored.Items(PlayerInventories.Cursor));

            // 4. Occupied cursor + full main inventory: refuses atomically without consuming the final deck.
            var fillerType = Program.Items.Find("Metadata/Items/Currency/CurrencyIdentification")!;
            for (byte x = 0; x < 12; x++)
                for (byte y = 0; y < 5; y++)
                    if (!restored.Items(main).Any(i => i.X == x && i.Y == y))
                        restored.Add(main, x, y, ItemCatalog.Create(fillerType));
            await Game.OnClientMessages([(InstanceItems.UseItemClickId, packet)], [], player);
            Assert.Equal(1, restored.Items(main).Single(i => i.Id == deck.Id).Item.StackSize);
        }
        finally
        {
            PlayerInventories.Forget(name);
            AccountStash.Forget(name);
            TestEnvironment.ReleaseSaves(save);
        }
    }

    [Fact]
    public async Task Right_clicking_stacked_deck_in_stash_consumes_from_stash_and_delivers_card()
    {
        string save = Path.Combine(Path.GetTempPath(), $"stacked-deck-stash-{Guid.NewGuid():N}.db");
        Saves.Open(save);
        using var runtime = new PartyFollowerTests.Runtime();
        using var wire = new PartyFollowerTests.Wire();
        string name = "StackedDeckStash" + Guid.NewGuid().ToString("N");
        var player = runtime.PlayerOf(new(), wire, name);
        var items = Game.ItemsOf(player);
        var stash = Game.StashOf(player);
        const int tab = 0;
        uint stashInvId = stash.InventoryOf(tab);
        try
        {
            foreach (var existing in items.Items(PlayerInventories.MainInventory).ToArray())
                items.Remove(PlayerInventories.MainInventory, existing.Id);
            var deckType = Program.Items.Find(Game.StackedDeck);
            Assert.NotNull(deckType);
            var placed = stash.Place(tab, 0, 0, new InventoryItem(1, 0, 0, ItemCatalog.Create(deckType!) with { StackSize = 2 }),
                i => Program.Items.ByHash(i.TypeHash) is { } t ? new(t.Width, t.Height, t.Class, t.MaxStack) : null, out _);
            Assert.NotNull(placed);
            var deck = placed!.Value.Placed;

            // 1. Shrink stack from 2 to 1 in stash, drawing card to empty cursor.
            byte[] packet = UseItemClickPacket(stashInvId, deck.Id);
            await Game.OnClientMessages([(InstanceItems.UseItemClickId, packet)], [], player);
            var onCursor = Assert.Single(items.Items(PlayerInventories.Cursor));
            Assert.Equal("DivinationCard", Program.Items.ByHash(onCursor.Item.TypeHash)?.Class);
            Assert.Equal(1, stash.Inventory(tab).Items.Single(i => i.Id == deck.Id).Item.StackSize);

            // 2. Consume last deck (stack 1 -> removed from stash) while cursor is occupied; drawn card lands in main inventory.
            await Game.OnClientMessages([(InstanceItems.UseItemClickId, packet)], [], player);
            Assert.DoesNotContain(stash.Inventory(tab).Items, i => i.Id == deck.Id);
            var drawnInMain = Assert.Single(items.Items(PlayerInventories.MainInventory));
            Assert.Equal("DivinationCard", Program.Items.ByHash(drawnInMain.Item.TypeHash)?.Class);
        }
        finally
        {
            PlayerInventories.Forget(name);
            AccountStash.Forget(name);
            TestEnvironment.ReleaseSaves(save);
        }
    }
}
