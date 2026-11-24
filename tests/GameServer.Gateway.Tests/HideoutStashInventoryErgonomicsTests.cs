using System.Buffers.Binary;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Modules.Harvest;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

public class HideoutStashInventoryErgonomicsTests : IDisposable
{
    readonly List<string> _accounts = [];

    public void Dispose()
    {
        foreach (string key in _accounts) AccountStash.Forget(key);
        TestEnvironment.ReleaseOwnSaves();
    }

    [Fact]
    public void Hideout_layout_0x0213_export_import_and_display_case_guard_persist()
    {
        TestEnvironment.OpenOwnSaves("hideout-layout-0x0213");
        var store = new SqliteAccountStore(Saves.Db);
        var account = new Account(Guid.NewGuid(), "HoExp" + Guid.NewGuid().ToString("N")[..10], "", "", DateTimeOffset.UnixEpoch);
        store.AddAccount(account);
        _accounts.Add(account.Name);
        store.AddCharacter(new(Guid.NewGuid(), account.Id, "HoOwner", CharacterClass.Witch, 1, "Standard",
            AccountService.StartingArea, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));

        var home = HideoutCatalog.Selected(HideoutCatalog.DefaultArea)!;
        var layout = new AreaLayout(home.AreaHash16, home.AreaId, 400, 400, 100, 100, null, []);
        var owner = new Player(layout, 1, 1) { Character = { Account = account.Name, Name = "HoOwner", League = "Standard" } };

        // Music hash <-> row mapping round-trips for the hideout music catalog.
        Assert.Equal((ushort)0, HideoutCatalog.HideoutMusicHash(InstanceHideoutEditing.DefaultMusicRow));
        Assert.Equal(InstanceHideoutEditing.DefaultMusicRow, HideoutCatalog.HideoutMusicRowByHash16(0));
        Assert.Null(HideoutCatalog.HideoutMusicRowByHash16(0xbeef));

        // ReplaceAccountHideoutLayout persists placements, custom decorations and music row atomically.
        var initialDecos = new List<Game.SavedHideoutDecoration>
        {
            new("deco:1", 0x11223344, 150, 160, 1.5f, 3, true),
            new("deco:2", 0x55667788, 180, 190, 0f, 0, false),
        };
        var placements = new Dictionary<string, HideoutPlacement>
        {
            ["stash"] = new(140, 145, 0.5f),
        };
        Assert.True(Game.ReplaceAccountHideoutLayout(owner, home.AreaId, placements, initialDecos, InstanceHideoutEditing.DefaultMusicRow));
        Assert.Equal(2, Game.AccountHideoutDecorations(owner).Count);
        Assert.Equal((140, 145), (Game.SavedHideoutPlacements(owner)!["stash"].X, Game.SavedHideoutPlacements(owner)!["stash"].Y));

        // 0x0213 wire layout round-trip with the hideout's real Hideouts.HASH16 and saved doodads.
        var wireDoodads = initialDecos.Select(d => new InstanceHideoutEditing.LayoutDoodad(
            d.Hash, (ushort)d.X, (ushort)d.Y, InstanceHideoutEditing.EncodeRotation(d.Rotation),
            InstanceHideoutEditing.PackFv(d.Variation, d.Mirrored))).ToArray();
        byte[] packet = InstanceHideoutEditing.EncodeLayout(new(home.Hash16, 0, 0, wireDoodads));
        var decoded = InstanceHideoutEditing.ReadLayout(packet);
        Assert.Equal(home.Hash16, decoded.HideoutHash);
        Assert.Equal(2, decoded.Doodads.Count);
        Assert.Equal((0x11223344u, (ushort)150, (ushort)160, (byte)3, true),
            (decoded.Doodads[0].Hash, decoded.Doodads[0].X, decoded.Doodads[0].Y, decoded.Doodads[0].Variation, decoded.Doodads[0].Mirrored));

        // Layouts exceeding the 750 custom decoration cap are refused without modifying saved state.
        var tooMany = Enumerable.Range(0, Game.MaxHideoutDecorations + 1)
            .Select(i => new Game.SavedHideoutDecoration($"deco:over:{i}", 0x1234, 100, 100, 0f)).ToList();
        Assert.False(Game.ReplaceAccountHideoutLayout(owner, home.AreaId, placements, tooMany, null));
        Assert.Equal(2, Game.AccountHideoutDecorations(owner).Count);

        // An occupied Item Display Case blocks replacing the hideout layout until emptied.
        var chaos = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Currency/CurrencyRerollRare")!);
        Assert.True(Game.SetAccountDisplayCaseItem(owner, home.AreaId, "deco:1", CharacterSaves.ToSaved(chaos, Program.Items.Mods)));
        Assert.False(Game.ReplaceAccountHideoutLayout(owner, home.AreaId, placements, [], null));
        Assert.True(Game.SetAccountDisplayCaseItem(owner, home.AreaId, "deco:1", null));
        Assert.True(Game.ReplaceAccountHideoutLayout(owner, home.AreaId, placements, [], null));
        Assert.Empty(Game.AccountHideoutDecorations(owner));
    }

    [Fact]
    public async Task Stash_quick_moves_heist_equipment_and_incremental_gem_xp_work()
    {
        TestEnvironment.OpenOwnSaves("stash-inventory-ergonomics");
        var catalog = Program.Items;
        PlayerInventories.Shape? ShapeOf(Item item) => catalog.ByHash(item.TypeHash) is { } t
            ? new(t.Width, t.Height, t.Class, t.MaxStack,
                FullStack: t.FullStackRow >= 0 && catalog.Row(t.FullStackRow) is { } becomes ? () => ItemCatalog.Create(becomes) : null,
                IsFossil: t.IsFossil)
            : null;

        string account = "ErgAcc" + Guid.NewGuid().ToString("N")[..10];
        string character = "ErgChr" + Guid.NewGuid().ToString("N")[..10];
        _accounts.Add(account);
        var stash = AccountStash.For(account, catalog);
        var inv = PlayerInventories.For(character, catalog);
        var player = new Player(new(1, "1_1_town", 100, 100, 10, 10, null, []), 1, 1)
            { Character = { Account = account, Name = character, League = "Standard" } };
        player.Pools = Game.PoolsFor(player, 20);
        player.PlayerLife = player.Pools.Life;

        var tabs = stash.Records(null, new HashSet<int>());
        int normalTab = tabs.First(t => t.Type == 0 && !t.Hidden && t.HiddenKey == 0).Index;
        int currencyTab = tabs.First(t => t.Type == StashRules.Currency && !t.Hidden && t.HiddenKey == 0).Index;
        int mapTab = tabs.First(t => t.Type == StashRules.Map && !t.Hidden && t.HiddenKey == 0).Index;
        var chaosType = catalog.Find("Metadata/Items/Currency/CurrencyRerollRare")!;
        var questType = catalog.All.First(t => t.Class == "QuestItem" || t.Components.Contains("Quest"));

        // C→S 0x00e2 cursor quick-place into Currency tab merges stacks and clears cursor; quest items are refused.
        var cursorChaos = inv.Put(PlayerInventories.Cursor, new InventoryItem(inv.NewItemId(), 0, 0, ItemCatalog.Create(chaosType) with { StackSize = 3 }));
        var quick = ItemMoves.HeldQuickToStash(stash, inv, currencyTab, cursorChaos.Id, ShapeOf, out _);
        Assert.NotNull(quick);
        Assert.Equal(currencyTab, quick!.Tab);
        Assert.Empty(inv.Items(PlayerInventories.Cursor));
        Assert.Equal(3, stash.CountOf(chaosType.Hash));

        var cursorQuest = inv.Put(PlayerInventories.Cursor, new InventoryItem(inv.NewItemId(), 0, 0, ItemCatalog.Create(questType)));
        Assert.Null(ItemMoves.HeldQuickToStash(stash, inv, normalTab, cursorQuest.Id, ShapeOf, out _));
        inv.Remove(PlayerInventories.Cursor, cursorQuest.Id);

        // C→S 0x002b sub-tab resolution finds the Map child page by SubTabKey and rejects bogus keys.
        int firstMapChild = stash.MapChildren(mapTab).First();
        ulong firstMapKey = stash.PageKeyOf(firstMapChild);
        Assert.Equal(firstMapChild, stash.FindSubTab(mapTab, firstMapKey));
        Assert.Null(stash.FindSubTab(mapTab, 0xdeadbeefcafe0001ul));

        // C→S 0x0114 collection -> personal stash moves only from row 114 while personal stash is open.
        var colItem = inv.Add(PlayerInventories.DeepwaterCollection, 0, 0, ItemCatalog.Create(chaosType) with { StackSize = 2 });
        var mainItem = inv.Add(PlayerInventories.MainInventory, 0, 0, ItemCatalog.Create(chaosType) with { StackSize = 1 });
        player.StashOpen = false;
        Assert.False(await Game.MoveCollectionToStash(player, new(colItem.Id, (ushort)normalTab, 0)));
        player.StashOpen = true;
        Assert.False(await Game.MoveCollectionToStash(player, new(mainItem.Id, (ushort)normalTab, 0)));
        Assert.True(await Game.MoveCollectionToStash(player, new(colItem.Id, (ushort)normalTab, 0)));
        Assert.Empty(inv.Items(PlayerInventories.DeepwaterCollection));
        Assert.Equal(5, stash.CountOf(chaosType.Hash));

        // C→S 0x00fd / 0x002d Heist NPC equipment quick-moves place tools/brooches in native columns and move them back.
        var toolType = catalog.All.First(t => t.Class == "HeistEquipmentTool");
        var broochType = catalog.All.First(t => t.Class == "HeistEquipmentReward");
        var tool = inv.Add(PlayerInventories.MainInventory, 2, 0, ItemCatalog.Create(toolType));
        var brooch = inv.Add(PlayerInventories.MainInventory, 4, 0, ItemCatalog.Create(broochType));
        var toolIn = ItemMoves.MoveHeistNpcEquipment(inv, slot: 2, tool.Id, intoTarget: true, fromExpanded: false, ShapeOf);
        var broochIn = ItemMoves.MoveHeistNpcEquipment(inv, slot: 2, brooch.Id, intoTarget: true, fromExpanded: false, ShapeOf);
        Assert.NotNull(toolIn);
        Assert.NotNull(broochIn);
        Assert.Equal(((byte)(PlayerInventories.HeistNpcEquipmentFirst + 2), (byte)0), (toolIn!.To, toolIn.Placed.X));
        Assert.Equal(((byte)(PlayerInventories.HeistNpcEquipmentFirst + 2), (byte)6), (broochIn!.To, broochIn.Placed.X));
        Assert.Null(ItemMoves.MoveHeistNpcEquipment(inv, slot: 2, mainItem.Id, intoTarget: true, fromExpanded: false, ShapeOf));
        Assert.Null(ItemMoves.MoveHeistNpcEquipment(inv, slot: 9, tool.Id, intoTarget: true, fromExpanded: false, ShapeOf));
        var toolOut = ItemMoves.MoveHeistNpcEquipment(inv, slot: 2, tool.Id, intoTarget: false, fromExpanded: false, ShapeOf);
        Assert.NotNull(toolOut);
        Assert.Equal(PlayerInventories.MainInventory, toolOut!.To);

        // C→S 0x00f8 Harvest quick-move uses MoveToSlot between carried inventory and row 43.
        var toHarvest = ItemMoves.MoveToSlot(inv, HarvestModule.Slot, mainItem.Id, intoSlot: true, ShapeOf);
        Assert.NotNull(toHarvest);
        Assert.Equal(HarvestModule.Slot, toHarvest!.To);
        var fromHarvest = ItemMoves.MoveToSlot(inv, HarvestModule.Slot, mainItem.Id, intoSlot: false, ShapeOf);
        Assert.NotNull(fromHarvest);
        Assert.Equal(PlayerInventories.MainInventory, fromHarvest!.To);

        // S→C 0x00c3 incremental socketed gem XP emits 6-byte 0x00c3 on clean kills and falls back to 0x00bf when inactive.
        var wandType = catalog.Find("Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1")!;
        var fireballType = catalog.All.First(t => t.Class == "Active Skill Gem" && t.Name == "Fireball");
        var fireball = ItemCatalog.Create(fireballType) with { GemExperience = 0 };
        var wand = ItemCatalog.Create(wandType) with { Sockets = [new(SocketColour.Blue, fireball)] };
        var equippedWand = inv.Put(PlayerInventories.Weapon1, new InventoryItem(inv.NewItemId(), 0, 0, wand));
        Game.CharacterStatSources(player);
        var (packets, leveled) = Game.CommitGemExperience(player, 200);
        Assert.False(leveled);
        var gemPkt = Assert.Single(packets);
        Assert.Equal(6, gemPkt.Length);
        Assert.Equal(InstanceItems.GemExperienceId, BinaryPrimitives.ReadUInt16BigEndian(gemPkt));
        Assert.Equal(20u, BinaryPrimitives.ReadUInt32BigEndian(gemPkt.AsSpan(2)));
        Assert.Equal(20, inv.Items(PlayerInventories.Weapon1).Single().Item.Sockets[0].Gem!.GemExperience);

        // With an inactive equipped host (high level requirement > character level 20) alongside an active one,
        // CommitGemExperience falls back to 0x00bf instead of 0x00c3 so only the active host's gem gains XP.
        var highWandType = catalog.All.First(t => t.Class == "Wand" && t.DropLevel > 50 && t.Supported);
        var offWand = inv.Put(PlayerInventories.Offhand1, new InventoryItem(inv.NewItemId(), 0, 0,
            ItemCatalog.Create(highWandType) with { Sockets = [new(SocketColour.Blue, ItemCatalog.Create(fireballType))] }));
        Game.CharacterStatSources(player);
        var (fallbackPackets, _) = Game.CommitGemExperience(player, 200);
        var fb = Assert.Single(fallbackPackets);
        Assert.Equal(InstanceItems.InventoryChangeId, BinaryPrimitives.ReadUInt16BigEndian(fb));
        Assert.Equal(40, inv.Items(PlayerInventories.Weapon1).Single().Item.Sockets[0].Gem!.GemExperience);
        Assert.Equal(0, inv.Items(PlayerInventories.Offhand1).Single().Item.Sockets[0].Gem!.GemExperience);
        Game.InactiveItems.TryRemove(character, out _);
    }
}
