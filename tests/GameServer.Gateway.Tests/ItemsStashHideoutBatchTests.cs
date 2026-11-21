using System.Buffers.Binary;
using System.Text;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public class ItemsStashHideoutBatchTests : IDisposable
{
    public void Dispose() => TestEnvironment.ReleaseOwnSaves();

    [Fact]
    public void Delve_sockets_stash_public_flag_item_notes_and_recipe_unlocks_work()
    {
        TestEnvironment.OpenOwnSaves("items-stash-hideout");
        var catalog = new ItemCatalog("data");
        var rolls = new ItemRolls(catalog);
        var bench = new BenchCrafting(catalog, rolls, "data");

        PlayerInventories.Shape? ShapeOf(Item item) => catalog.ByHash(item.TypeHash) is { } t
            ? new(t.Width, t.Height, t.Class, t.MaxStack, IsFossil: t.IsFossil)
            : null;

        // Delve socket colour 6 validation: rejects skill gems, accepts fossils (splitting stacks), rejects duplicates,
        // and excludes fossils from SocketedGemPlaces().
        var resonatorType = catalog.Find("Metadata/Items/Delve/DelveStackableSocketableCurrencyReroll2")!;
        var fossils = catalog.All.Where(t => t.IsFossil).Take(2).ToList();
        Assert.Equal(2, fossils.Count);
        var gemType = catalog.All.First(t => t.Class == "Active Skill Gem");

        string character = "DelveSock" + Guid.NewGuid().ToString("N")[..10];
        var inv = PlayerInventories.For(character, catalog);
        var resonator = inv.Add(PlayerInventories.MainInventory, 0, 0, ItemCatalog.Create(resonatorType));
        Assert.Equal(2, resonator.Item.Sockets.Count);
        Assert.All(resonator.Item.Sockets, s => Assert.Equal(SocketColour.Delve, s.Colour));

        // Skill gem must be refused in a Delve socket.
        inv.Put(PlayerInventories.Cursor, new InventoryItem(inv.NewItemId(), 0, 0, ItemCatalog.Create(gemType)));
        Assert.Null(inv.Socket(PlayerInventories.MainInventory, resonator.Id, 0, ShapeOf, inv.NewItemId, out _));
        inv.Remove(PlayerInventories.Cursor, inv.Items(PlayerInventories.Cursor)[0].Id);

        // Stack of 2 of Fossil 1: socketing splits 1 into socket 0 and leaves 1 on cursor.
        var fossil1Stack = inv.Put(PlayerInventories.Cursor,
            new InventoryItem(inv.NewItemId(), 0, 0, ItemCatalog.Create(fossils[0]) with { StackSize = 2 }));
        var socketed1 = inv.Socket(PlayerInventories.MainInventory, resonator.Id, 0, ShapeOf, inv.NewItemId, out _);
        Assert.NotNull(socketed1);
        Assert.NotNull(socketed1.Value.HeldNow);
        Assert.Equal((ushort)1, socketed1.Value.HeldNow!.Item.StackSize);
        Assert.Equal(fossils[0].Hash, socketed1.Value.Item.Item.Sockets[0].Gem!.TypeHash);

        // Duplicate identical fossil in socket 1 must be refused.
        Assert.Null(inv.Socket(PlayerInventories.MainInventory, resonator.Id, 1, ShapeOf, inv.NewItemId, out _));
        inv.Remove(PlayerInventories.Cursor, fossil1Stack.Id);

        // Different fossil in socket 1 succeeds and fossils never appear as skill gems.
        inv.Put(PlayerInventories.Cursor, new InventoryItem(inv.NewItemId(), 0, 0, ItemCatalog.Create(fossils[1])));
        var socketed2 = inv.Socket(PlayerInventories.MainInventory, resonator.Id, 1, ShapeOf, inv.NewItemId, out _);
        Assert.NotNull(socketed2);
        Assert.Null(socketed2.Value.HeldNow);
        Assert.Empty(inv.SocketedGemPlaces());

        // Stash tab Public flag (C->S 0xcf) & Item price note (C->S 0x1a1 / Base bit 4 WStr32) round-trip across reload.
        string account = "PublicNote" + Guid.NewGuid().ToString("N")[..10];
        var stash = AccountStash.For(account, catalog);
        int premiumTab = stash.Records(null, new HashSet<int>()).First(t => (t.Flags & InstanceStash.TabFlags.Premium) != 0).Index;
        Assert.True(stash.SetPublic(premiumTab, true));
        var placedItem = ItemCatalog.Create(fossils[0]) with { Note = "~price 5 chaos" };
        byte[] payload = placedItem.Payload();
        Assert.True(payload.AsSpan().IndexOf(Encoding.Unicode.GetBytes("~price 5 chaos")) >= 0);
        // Verify Base component note survives wire + stash save/reload.
        var placed = stash.Place(premiumTab, 0, 0, new InventoryItem(stash.NewItemId(), 0, 0, ItemCatalog.Create(fossils[0])), ShapeOf, out _);
        Assert.NotNull(placed);
        var noted = stash.SetItemNote(premiumTab, placed.Value.Placed.Id, "~b/o 1 divine");
        Assert.NotNull(noted);
        Assert.Equal("~b/o 1 divine", noted!.Item.Note);

        AccountStash.Forget(account);
        var reloaded = AccountStash.For(account, catalog);
        var record = reloaded.Records(null, new HashSet<int>())[premiumTab];
        Assert.True((record.Flags & InstanceStash.TabFlags.Public) != 0);
        var reloadedItem = reloaded.Inventory(premiumTab).Items.Single(i => i.Id == placed.Value.Placed.Id);
        Assert.Equal("~b/o 1 divine", reloadedItem.Item.Note);

        // RecipeUnlockDisplay catalog loads unlock entries cleanly from client.db.
        Assert.NotEmpty(bench.RecipeUnlocks);
        var firstWithArea = bench.RecipeUnlocks.First(u => !string.IsNullOrEmpty(u.UnlockAreaId));
        Assert.Contains(firstWithArea.RecipeId, bench.UnlocksForArea(firstWithArea.UnlockAreaId!));
    }

    [Fact]
    public void Protocol_framing_readers_and_entity_kv_0x2c1_match_3293c_layouts()
    {
        // S->C 0x2c1 BaseDataUpdate + extended EntityVariable types (Entity, IntPair, Float, Int, Byte, String, Empty).
        var vars = new[]
        {
            EntityVariable.Entity("target", 0x1000),
            EntityVariable.IntPair("coords", (10, 20)),
            EntityVariable.Float("scale", 1.5f),
            EntityVariable.Int("ranks", 1, 2, 3),
            EntityVariable.Byte("flag", 1),
            EntityVariable.String("label", "ok"),
            EntityVariable.Empty("cleared"),
        };
        byte[] kvPacket = InstanceEntities.BaseDataUpdate(0x3005, vars, orderKey: 7, checkpoint: 9);
        Assert.Equal(InstanceEntities.BaseDataUpdateId, BinaryPrimitives.ReadUInt16BigEndian(kvPacket));
        Assert.Equal(0x3005u, BinaryPrimitives.ReadUInt32BigEndian(kvPacket.AsSpan(2)));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32BigEndian(kvPacket.AsSpan(6)));
        Assert.Equal((ushort)9, BinaryPrimitives.ReadUInt16BigEndian(kvPacket.AsSpan(10)));
        ushort blobLen = BinaryPrimitives.ReadUInt16BigEndian(kvPacket.AsSpan(12));
        Assert.Equal(kvPacket.Length - 14, blobLen);
        Assert.Equal((byte)vars.Length, kvPacket[14]);

        // HideoutObject supports ItemDisplayCase (0 wire bytes for that component).
        var displayCaseBlob = new HideoutObject(100, 120, 0f,
            ["Positioned", "BaseEvents", "Animated", "ItemDisplayCase", "InteractionAction", "HideoutDoodad"],
            new HideoutDoodad(0x12345678, 0)).Blob();
        Assert.NotEmpty(displayCaseBlob);
        Assert.NotNull(HideoutCatalog.HideoutMusicName(InstanceHideoutEditing.DefaultMusicRow));

        // Build and frame a concatenated stream of all newly-wired C->S opcodes in this batch.
        var stream = new LoginWriter();
        // 0xcf PublicTab (14 B)
        stream.U16(InstanceStash.PublicTabId).U8(InstanceStash.Personal).U16(3).U32(0).U32(0).U8(1);
        // 0x1a1 SetItemNote (variable)
        stream.U16(InstanceItems.SetItemNoteId).U32(0x803).U32(0x80000042).WStr("~price 2 chaos");
        // 0x2ed Claim (11 B: cursor click vs Ctrl-click)
        stream.U16(InstanceCurrencyExchange.ClaimId).U32(77).U8(1).U8(1).U8(1).U8(0xfe).U8(0xff);
        // 0x59 PlaceDecoration (16 B)
        stream.U16(InstanceHideoutEditing.PlaceId).U32(0xdeadbeef).U16(210).U16(330)
            .U32(BitConverter.SingleToUInt32Bits(1.25f)).U8(2).U8(1);
        // 0x5d Reclaim (6 B), 0x5e Reset (3 B), 0x255 Music (4 B)
        stream.U16(InstanceHideoutEditing.ReclaimId).U32(0x3100);
        stream.U16(InstanceHideoutEditing.ResetId).U8(1);
        stream.U16(InstanceHideoutEditing.MusicId).U16(0);
        // 0x67 DisplayCasePlace (14 B), 0x68 DisplayCaseToggle (7 B)
        stream.U16(InstanceHideoutEditing.DisplayCasePlaceId).U32(0x3200).U32(13).U32(99);
        stream.U16(InstanceHideoutEditing.DisplayCaseToggleId).U32(0x3200).U8(0);
        // 0x78 UseItemTypeOnPassive (17 B), 0x79 UseItemTypeOnObject (18 B)
        stream.U16(InstanceItems.UseItemTypeOnPassiveId).U32(0x11223344).U32(0).U16(0).U16(0).U8(0).U16(5432);
        stream.U16(InstanceItems.UseItemTypeOnObjectId).U32(0x55667788).U32(0x4001).U32(0).U16(0).U16(0);
        // 0xf5 TakeBlightOil (12 B), 0xf9 Harvest MoveItem (10 B)
        stream.U16(InstanceStash.TakeBlightOilId).U32(0x40000010).U8(0).U32(7).U8(1);
        stream.U16(InstanceHarvest.MoveItemId).U32(55).U8(0).U16(4).U8(1);

        var messages = InstanceProtocol.SplitClientMessages(stream.ToArray(), out var partial, out var unknown);
        Assert.Empty(partial);
        Assert.Empty(unknown);
        Assert.Equal(13, messages.Count);

        var pub = InstanceStash.ReadPublicTab(messages[0].Bytes);
        Assert.Equal((InstanceStash.Personal, (ushort)3, true), pub);

        var note = InstanceItems.ReadSetItemNote(messages[1].Bytes);
        Assert.Equal((0x803u, 0x42u, true, "~price 2 chaos"), (note.InventoryId, note.ItemId, note.Flag, note.Note));

        var claim = InstanceCurrencyExchange.ReadClaim(messages[2].Bytes);
        Assert.Equal((77u, (byte)1, (byte)1, true, (byte)0xfe, (byte)0xff),
            (claim.Order, claim.Side, claim.PressKind, claim.ToCursor, claim.CellX, claim.CellY));
        byte[] errPacket = InstanceCurrencyExchange.Error(InstanceCurrencyExchange.ErrorCouldNotFindSellItems);
        Assert.Equal(InstanceCurrencyExchange.ErrorId, BinaryPrimitives.ReadUInt16BigEndian(errPacket));
        Assert.Equal(InstanceCurrencyExchange.ErrorCouldNotFindSellItems, BinaryPrimitives.ReadUInt16BigEndian(errPacket.AsSpan(2)));

        var place = InstanceHideoutEditing.ReadPlace(messages[3].Bytes);
        Assert.Equal((0xdeadbeefu, (ushort)210, (ushort)330, 1.25f, (byte)2, true),
            (place.Hash, place.X, place.Y, place.Rotation, place.Variation, place.Mirrored));
        Assert.Equal(0x3100u, InstanceHideoutEditing.ReadReclaim(messages[4].Bytes));
        Assert.Equal((byte)1, InstanceHideoutEditing.ReadReset(messages[5].Bytes));
        Assert.Equal((ushort)0, InstanceHideoutEditing.ReadMusic(messages[6].Bytes));
        Assert.Equal(new InstanceHideoutEditing.DisplayCasePlace(0x3200, 13, 99),
            InstanceHideoutEditing.ReadDisplayCasePlace(messages[7].Bytes));
        Assert.Equal(new InstanceHideoutEditing.DisplayCaseToggle(0x3200, false),
            InstanceHideoutEditing.ReadDisplayCaseToggle(messages[8].Bytes));

        var onPassive = InstanceItems.ReadUseItemTypeOnPassive(messages[9].Bytes);
        Assert.Equal((0x11223344u, (byte)0, (ushort)5432), (onPassive.TypeHash, onPassive.Flags, onPassive.Node));
        var onObject = InstanceItems.ReadUseItemTypeOnObject(messages[10].Bytes);
        Assert.Equal((0x55667788u, 0x4001u), (onObject.TypeHash, onObject.TargetObjectId));
        var blight = InstanceStash.ReadTakeBlightOil(messages[11].Bytes);
        Assert.Equal((0x40000010u, (byte)0, 7u, (byte)1), (blight.ItemId, blight.Kind, blight.Tab, blight.Bulk));
        var harvest = InstanceHarvest.ReadMoveItem(messages[12].Bytes);
        Assert.Equal((55u, (byte)0, (ushort)4, true), (harvest.ItemId, harvest.Flags, harvest.Tab, harvest.IntoStation));
    }
}
