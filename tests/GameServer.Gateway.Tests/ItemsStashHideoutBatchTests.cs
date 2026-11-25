using System.Buffers.Binary;
using System.Text;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Trade;
using GameServer.Modules.Harvest;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

public class ItemsStashHideoutBatchTests : IDisposable
{
    readonly List<string> _accounts = [];

    public void Dispose()
    {
        foreach (string key in _accounts) AccountStash.Forget(key);
        TestEnvironment.ReleaseOwnSaves();
    }

    [Fact]
    public async Task Delve_sockets_stash_public_flag_item_notes_and_recipe_unlocks_work()
    {
        TestEnvironment.OpenOwnSaves("items-stash-hideout");
        var catalog = new ItemCatalog("data");
        var rolls = new ItemRolls(catalog);
        var bench = new BenchCrafting(catalog, rolls, "data");

        PlayerInventories.Shape? ShapeOf(Item item) => catalog.ByHash(item.TypeHash) is { } t
            ? new(t.Width, t.Height, t.Class, t.MaxStack,
                FullStack: t.FullStackRow >= 0 && catalog.Row(t.FullStackRow) is { } becomes ? () => ItemCatalog.Create(becomes) : null,
                IsFossil: t.IsFossil)
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
        _accounts.Add(account);
        var stash = AccountStash.For(account, catalog);
        var records = stash.Records(null, new HashSet<int>());
        int normalTab = records.First(t => t.Type == 0 && !t.Hidden && t.HiddenKey == 0).Index;
        int premiumTab = records.First(t => (t.Flags & InstanceStash.TabFlags.Premium) != 0 && t.HiddenKey == 0 && !t.Hidden && t.Type == 1).Index;
        int childTab = records.First(t => t.HiddenKey != 0).Index;

        // SetPublic rejects normal tabs and hidden child pages; SetItemNote rejects non-public and normal tabs.
        Assert.False(stash.SetPublic(normalTab, true));
        Assert.False(stash.SetPublic(childTab, true));
        var placedNormal = stash.Place(normalTab, 0, 0, new InventoryItem(stash.NewItemId(), 0, 0, ItemCatalog.Create(fossils[0])), ShapeOf, out _);
        Assert.NotNull(placedNormal);
        Assert.Null(stash.SetItemNote(normalTab, placedNormal.Value.Placed.Id, "~price 1 chaos"));
        var placed = stash.Place(premiumTab, 0, 0, new InventoryItem(stash.NewItemId(), 0, 0, ItemCatalog.Create(fossils[0])), ShapeOf, out _);
        Assert.NotNull(placed);
        Assert.Null(stash.SetItemNote(premiumTab, placed.Value.Placed.Id, "~price 1 chaos"));

        Assert.True(stash.SetPublic(premiumTab, true));
        var placedItem = ItemCatalog.Create(fossils[0]) with { Note = "~price 5 chaos" };
        byte[] payload = placedItem.Payload();
        Assert.True(payload.AsSpan().IndexOf(Encoding.Unicode.GetBytes("~price 5 chaos")) >= 0);
        // Verify Base component note survives wire + stash save/reload.
        var noted = stash.SetItemNote(premiumTab, placed.Value.Placed.Id, "~b/o 1 divine");
        Assert.NotNull(noted);
        Assert.Equal("~b/o 1 divine", noted!.Item.Note);
        // A move inside the public tab keeps a note; landing in a tab that isn't public clears it (retail).
        var second = stash.Place(premiumTab, 2, 0, new InventoryItem(stash.NewItemId(), 0, 0, ItemCatalog.Create(fossils[0])), ShapeOf, out _)!.Value.Placed;
        stash.SetItemNote(premiumTab, second.Id, "~price 2 chaos");
        var kept = stash.Place(premiumTab, 4, 0, stash.Take(premiumTab, second.Id)!, ShapeOf, out _)!.Value.Placed;
        Assert.Equal("~price 2 chaos", kept.Item.Note);
        Assert.Null(stash.Place(normalTab, 2, 0, stash.Take(premiumTab, second.Id)!, ShapeOf, out _)!.Value.Placed.Item.Note);

        // Removing a tab hides it and clears its Public flag; SetPublic & SetItemNote reject deleted tabs.
        int removablePremium = records.Last(t => t.Type == 1 && !t.Hidden && t.HiddenKey == 0 && t.Index != premiumTab).Index;
        Assert.True(stash.SetPublic(removablePremium, true));
        Assert.NotNull(stash.RemoveTab(removablePremium));
        Assert.False(stash.SetPublic(removablePremium, true));

        AccountStash.Forget(account);
        var reloaded = AccountStash.For(account, catalog);
        var record = reloaded.Records(null, new HashSet<int>())[premiumTab];
        Assert.True((record.Flags & InstanceStash.TabFlags.Public) != 0);
        var reloadedItem = reloaded.Inventory(premiumTab).Items.Single(i => i.Id == placed.Value.Placed.Id);
        Assert.Equal("~b/o 1 divine", reloadedItem.Item.Note);

        // 0x1a1 rejects closed stash and character MainInventory; 0xf9 & 0xf5 reject closed stash.
        var player = new Player(new(1, "1_1_town", 100, 100, 10, 10, null, []), 1, 1)
            { Character = { Account = account, Name = character, League = "Standard" } };
        player.StashOpen = false;
        player.GuildStashOpen = false;
        byte[] closedNote = new LoginWriter().U16(InstanceItems.SetItemNoteId)
            .U32(reloaded.InventoryOf(premiumTab)).U32(placed.Value.Placed.Id).WStr("~price 99 divine").ToArray();
        byte[] mainInvNote = new LoginWriter().U16(InstanceItems.SetItemNoteId)
            .U32(PlayerInventories.InventoryId(PlayerInventories.MainInventory)).U32(resonator.Id).WStr("~price 99 divine").ToArray();
        byte[] closedTake0xf9 = new LoginWriter().U16(InstanceHarvest.MoveItemId)
            .U32(placed.Value.Placed.Id).U8(0).U16((ushort)premiumTab).U8(0).ToArray();
        byte[] closedTake0xf5 = new LoginWriter().U16(InstanceStash.TakeBlightOilId)
            .U32(placed.Value.Placed.Id).U8(InstanceStash.Personal).U32((uint)premiumTab).U8(1).ToArray();
        await Game.OnClientMessages(
            [(InstanceItems.SetItemNoteId, closedNote), (InstanceItems.SetItemNoteId, mainInvNote),
             (InstanceHarvest.MoveItemId, closedTake0xf9), (InstanceStash.TakeBlightOilId, closedTake0xf5)],
            [], player);
        Assert.Equal("~b/o 1 divine", reloaded.Inventory(premiumTab).Items.Single(i => i.Id == placed.Value.Placed.Id).Item.Note);
        Assert.Null(inv.Items(PlayerInventories.MainInventory).Single(i => i.Id == resonator.Id).Item.Note);

        // 0xf9 ItemMoves.MoveToSlot rejects equipped gear, PassiveJewels, AtlasUpgrades, CraftingBench, and Quest items.
        var armourType = catalog.All.First(t => t.Class == "Body Armour" && t.Supported);
        var jewelType = catalog.All.First(t => t.Class == "Jewel" && t.Supported);
        var questType = catalog.All.First(t => t.Class == "QuestItem" || t.Components.Contains("Quest"));
        var equippedArmour = inv.Put(PlayerInventories.BodyArmour, new InventoryItem(inv.NewItemId(), 0, 0, ItemCatalog.Create(armourType)));
        var passiveJewel = inv.Put(PlayerInventories.PassiveJewels, new InventoryItem(inv.NewItemId(), 0, 0, ItemCatalog.Create(jewelType)));
        var voidstone = inv.Put(PlayerInventories.AtlasUpgrades, new InventoryItem(inv.NewItemId(), 0, 0, ItemCatalog.Create(jewelType)));
        var onBench = inv.Put(PlayerInventories.CraftingBench, new InventoryItem(inv.NewItemId(), 0, 0, ItemCatalog.Create(armourType)));
        var questInMain = inv.Add(PlayerInventories.MainInventory, 8, 0, ItemCatalog.Create(questType));
        Assert.Null(ItemMoves.MoveToSlot(inv, HarvestModule.Slot, equippedArmour.Id, intoSlot: true, ShapeOf));
        Assert.Null(ItemMoves.MoveToSlot(inv, HarvestModule.Slot, passiveJewel.Id, intoSlot: true, ShapeOf));
        Assert.Null(ItemMoves.MoveToSlot(inv, HarvestModule.Slot, voidstone.Id, intoSlot: true, ShapeOf));
        Assert.Null(ItemMoves.MoveToSlot(inv, HarvestModule.Slot, onBench.Id, intoSlot: true, ShapeOf));
        Assert.Null(ItemMoves.MoveToSlot(inv, HarvestModule.Slot, questInMain.Id, intoSlot: true, ShapeOf));

        // 0xf9 ItemMoves.StashToSlot rejects oversized stacks (e.g. 5,000-stack from Currency tab) while allowing normal stacks.
        int currencyTab = reloaded.Records(null, new HashSet<int>()).First(t => t.Type == StashRules.Currency).Index;
        var chaosType = catalog.Find("Metadata/Items/Currency/CurrencyRerollRare")!;
        var bigStack = reloaded.AddStacked(currencyTab, ItemCatalog.Create(chaosType) with { StackSize = 5000 }, ShapeOf, out _);
        Assert.NotNull(bigStack);
        uint bigStackId = bigStack!.Added.Single().Id;
        Assert.Null(ItemMoves.StashToSlot(reloaded, inv, currencyTab, HarvestModule.Slot, bigStackId, ShapeOf));
        var normalIntoSlot = ItemMoves.StashToSlot(reloaded, inv, premiumTab, HarvestModule.Slot, placed.Value.Placed.Id, ShapeOf);
        Assert.NotNull(normalIntoSlot);
        Assert.NotNull(ItemMoves.MoveToSlot(inv, HarvestModule.Slot, normalIntoSlot!.Id, intoSlot: false, ShapeOf));

        // RecipeUnlockDisplay catalog loads unlock entries cleanly from client.db.
        Assert.NotEmpty(bench.RecipeUnlocks);
        var firstWithArea = bench.RecipeUnlocks.First(u => !string.IsNullOrEmpty(u.UnlockAreaId));
        Assert.Contains(firstWithArea.RecipeId, bench.UnlocksForArea(firstWithArea.UnlockAreaId!));
    }

    [Fact]
    public async Task Protocol_framing_readers_and_entity_kv_0x2c1_match_3293c_layouts()
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
        Assert.Equal((false, 0xdeadbeefu, (ushort)210, (ushort)330, 1.25f, (byte)2, true),
            (place.ByHash, place.Id, place.X, place.Y, place.Rotation, place.Variation, place.Mirrored));
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
        Assert.Equal((0x40000010u, (byte)0, 7u, (byte)1), (blight.ItemId, blight.Kind, blight.Tab, blight.UiPanelState));
        var harvest = InstanceHarvest.ReadMoveItem(messages[12].Bytes);
        Assert.Equal((55u, (byte)0, (ushort)4, true), (harvest.ItemId, harvest.Flags, harvest.Tab, harvest.IntoStation));

        // Currency Exchange: atomic TryClaim prevents uint underflow on stale/concurrent claims; hourly quantization
        // keeps SettleDue false across ticks within the same UTC hour.
        var listedOrder = new ExchangeOrder { Id = 1, ClaimWant = 10, State = InstanceCurrencyExchange.OrderState.Listed };
        var completedOrder = new ExchangeOrder { Id = 2, ClaimWant = 10, State = InstanceCurrencyExchange.OrderState.Completed };
        var book = new ExchangeBook { Orders = [listedOrder, completedOrder] };
        Assert.Null(book.TryClaim(1, bought: true, 5, out uint listedTaken));
        Assert.Equal(0u, listedTaken);
        var firstClaim = book.TryClaim(2, bought: true, 6, out uint taken1);
        Assert.NotNull(firstClaim);
        Assert.Equal((6u, 4u), (taken1, firstClaim!.ClaimWant));
        var secondClaim = book.TryClaim(2, bought: true, 10, out uint taken2);
        Assert.NotNull(secondClaim);
        Assert.Equal((4u, 0u, InstanceCurrencyExchange.OrderState.Removed), (taken2, secondClaim!.ClaimWant, secondClaim.State));
        Assert.Null(book.TryClaim(2, bought: true, 10, out uint staleTaken));
        Assert.Equal(0u, staleTaken);

        long h1 = ExchangeRates.QuantizeHour(1_700_001_234), h2 = ExchangeRates.QuantizeHour(1_700_001_264);
        Assert.Equal(h1, h2);
        Assert.Equal(0L, h1 % 3600);
        var market = new ExchangeMarket(new(h1, "Baseline", [], Hours: 1),
            new() { [ExchangeRates.Chaos] = new(1, 5000) }, _ => ExchangeRates.Chaos, 1, .1);
        var hourlyBook = new ExchangeBook { Orders = [new ExchangeOrder { Id = 3, State = InstanceCurrencyExchange.OrderState.Listed, LastHour = h1, PlacedAt = h1 }] };
        Assert.False(hourlyBook.SettleDue(market, h1 + 30));

        // Claiming a full stack of BecomesWhenFull shards converts to the full currency item via PlayerInventories.Completed.
        var mirrorShard = Program.Items.Find("Metadata/Items/Currency/CurrencyDuplicateShard")!;
        var mirrorFull = Program.Items.Find("Metadata/Items/Currency/CurrencyDuplicate")!;
        var shardItem = ItemCatalog.Create(mirrorShard) with { StackSize = mirrorShard.MaxStack };
        var completedShard = PlayerInventories.Completed(new InventoryItem(1, 0, 0, shardItem), Game.ShapeOf(shardItem)!.Value);
        Assert.Equal(mirrorFull.Hash, completedShard.Item.TypeHash);
        Assert.Equal((ushort)1, completedShard.Item.StackSize);

        // 0x5a carries the decoration's hash itself; 0x5c is the editor's Delete key on an object.
        byte[] hashPlace = new LoginWriter().U16(InstanceHideoutEditing.PlaceByHashId)
            .U32(0xdeadbeef).U16(210).U16(330).U32(0).U8(0).U8(0).ToArray();
        var hashParsed = InstanceHideoutEditing.ReadPlace(hashPlace);
        Assert.Equal((true, 0xdeadbeefu), (hashParsed.ByHash, hashParsed.Id));
        Assert.Equal(0x3100u, InstanceHideoutEditing.ReadRemove(new LoginWriter().U16(InstanceHideoutEditing.RemoveId).U32(0x3100).ToArray()));

        // ItemDisplayCase league scoping, occupied reclaim/reset refusal, and hideout 750 decoration cap.
        TestEnvironment.OpenOwnSaves("hideout-security");
        var store = new SqliteAccountStore(Saves.Db);
        var account = new Account(Guid.NewGuid(), "Disp" + Guid.NewGuid().ToString("N")[..10], "", "", DateTimeOffset.UnixEpoch);
        store.AddAccount(account);
        _accounts.Add(account.Name);
        _accounts.Add(AccountStash.Key(account.Name, "Solo Self-Found"));
        store.AddCharacter(new(Guid.NewGuid(), account.Id, "StdOwner", CharacterClass.Witch, 1, "Standard",
            AccountService.StartingArea, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
        store.AddCharacter(new(Guid.NewGuid(), account.Id, "SsfAlt", CharacterClass.Witch, 1, "Solo Self-Found",
            AccountService.StartingArea, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
        var home = HideoutCatalog.Selected(HideoutCatalog.DefaultArea)!;
        var layout = new AreaLayout(home.AreaHash16, home.AreaId, 100, 100, 20, 20, null, []);
        var stdPlayer = new Player(layout, 1, 1) { Character = { Account = account.Name, Name = "StdOwner", League = "Standard" } };
        var ssfPlayer = new Player(layout, 1, 1) { Character = { Account = account.Name, Name = "SsfAlt", League = "Solo Self-Found", Ssf = true } };
        var noAccount = new Player(layout, 1, 1) { Character = { Name = "Anon" } };

        var caseDeco = new Game.SavedHideoutDecoration("deco:case1", 0x1234, 25, 25, 0f);
        Assert.True(Game.AddAccountHideoutDecoration(stdPlayer, home.AreaId, caseDeco));
        var chaos = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Currency/CurrencyRerollRare")!);
        var savedChaos = CharacterSaves.ToSaved(chaos, Program.Items.Mods);
        Assert.False(Game.SetAccountDisplayCaseItem(noAccount, home.AreaId, caseDeco.Key, savedChaos));
        Assert.True(Game.SetAccountDisplayCaseItem(stdPlayer, home.AreaId, caseDeco.Key, savedChaos));
        // SSF character on the same account cannot read Standard's stored display case item.
        Assert.NotNull(Game.AccountDisplayCaseItem(stdPlayer, home.AreaId, caseDeco.Key));
        Assert.Null(Game.AccountDisplayCaseItem(ssfPlayer, home.AreaId, caseDeco.Key));
        // Reclaiming or resetting is refused while occupied (even from another league on the same account).
        Assert.False(Game.RemoveAccountHideoutDecoration(stdPlayer, home.AreaId, caseDeco.Key));
        Assert.False(Game.RemoveAccountHideoutDecoration(ssfPlayer, home.AreaId, caseDeco.Key));
        Assert.False(Game.ClearAccountHideoutDecorations(stdPlayer, home.AreaId, resetPlacements: false));
        Assert.False(Game.ClearAccountHideoutDecorations(ssfPlayer, home.AreaId, resetPlacements: true));
        // Emptying the display case allows reclaim and reset again.
        Assert.True(Game.SetAccountDisplayCaseItem(stdPlayer, home.AreaId, caseDeco.Key, null));
        Assert.True(Game.RemoveAccountHideoutDecoration(stdPlayer, home.AreaId, caseDeco.Key));

        // 750 custom decoration cap per hideout.
        for (int i = 0; i < Game.MaxHideoutDecorations; i++)
            Assert.True(Game.AddAccountHideoutDecoration(stdPlayer, home.AreaId, new($"deco:{i}", 0x1234, 25, 25, 0f)));
        Assert.False(Game.AddAccountHideoutDecoration(stdPlayer, home.AreaId, new("deco:overflow", 0x1234, 25, 25, 0f)));
        Assert.Equal(Game.MaxHideoutDecorations, Game.AccountHideoutDecorations(stdPlayer).Count);

        // Walk-away UI close also closes ModuleWindow when moving beyond close range.
        stdPlayer.Connection.PlayerSent = true;
        stdPlayer.ModuleWindow = "harvest";
        stdPlayer.ModuleWindowAnchor = ("harvest", 20, 20);
        await Game.CloseInteractionsIfMovingAway(stdPlayer, (25, 25), 0);
        Assert.Equal("harvest", stdPlayer.ModuleWindow);
        await Game.CloseInteractionsIfMovingAway(stdPlayer, (80, 80), 0);
        Assert.Null(stdPlayer.ModuleWindow);
    }
}
