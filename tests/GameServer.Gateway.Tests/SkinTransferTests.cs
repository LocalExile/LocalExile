using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public sealed class SkinTransferTests
{
    static string Saved(Item item, ItemCatalog catalog) => JsonSerializer.Serialize(CharacterSaves.ToSaved(item, catalog.Mods));
    static ItemMod Mod(ItemCatalog catalog, string stat, int value) => catalog.Mods.All
        .First(m => m.Stats.Count == 1 && m.Stats[0].Id == stat).With(ModSection.Explicit, [value], new Random(1));

    [Fact]
    public void Native_binding_preserves_both_items_through_relational_saves_stash_and_failed_reclaim()
    {
        byte[] bind = Convert.FromHexString("02070000177300000008000000060000000100000002");
        byte[] reclaim = Convert.FromHexString("01220000000200000006");
        Assert.Equal(new InstanceSkinTransfers.Bind(6003, 8, 6, 1, 2), InstanceSkinTransfers.ReadBind(bind));
        Assert.Equal(new InstanceSkinTransfers.Reclaim(2, 6), InstanceSkinTransfers.ReadReclaim(reclaim));
        for (int cut = 1; cut < bind.Length; cut++)
        {
            Assert.Empty(InstanceProtocol.SplitClientMessages(bind.AsSpan(0, cut), out var pending, out var unknown));
            Assert.Empty(unknown);
            byte[] next = [.. pending, .. bind.AsSpan(cut).ToArray(), .. reclaim, 0x01, 0x53];
            Assert.Equal(new ushort[] { 0x207, 0x122, InstanceProtocol.ExitToCharacterSelectId },
                InstanceProtocol.SplitClientMessages(next, out pending, out unknown).Select(m => m.Id));
            Assert.Empty(pending); Assert.Empty(unknown);
        }
        TestEnvironment.OpenOwnSaves("skin-transfer");
        string name = "Skin" + Guid.NewGuid().ToString("N");
        try
        {
            var catalog = Program.Items;
            var basis = catalog.All.First(t => t.Supported && t.Class == "Helmet");
            var a = ItemCatalog.Create(basis) with { Mods = [Mod(catalog, "base_maximum_life", 60)] };
            var gem = ItemCatalog.Create(catalog.Find("Metadata/Items/Gems/SkillGemFireball")!);
            var b = ItemCatalog.Create(basis) with { Quality = 17, MemoryStrands = 23, Intangibility = 31,
                Sockets = [new(SocketColour.White, gem)], Links = [1], Mods = [Mod(catalog, "base_maximum_life", 80)] };
            string originalA = Saved(a, catalog), originalB = Saved(b, catalog);
            CharacterSaves.SetInventories(name, [], null);
            var inventory = PlayerInventories.For(name, catalog);
            var target = inventory.Add(PlayerInventories.Helmet, 0, 0, a);
            var donor = inventory.Add(PlayerInventories.Cursor, 0, 0, b);
            Assert.Null(inventory.BindSkin(target.Id, PlayerInventories.MainInventory, PlayerInventories.Cursor, donor.Id, Game.ShapeOf, out _));
            var bound = inventory.BindSkin(target.Id, Game.ShapeOf, out _)!;
            Assert.NotNull(bound);
            Assert.Null(new ItemRolls(catalog).Mirror(bound.Target.Item));
            Assert.Empty(inventory.Items(PlayerInventories.Cursor));
            Assert.Null(inventory.BindSkin(target.Id, Game.ShapeOf, out _));
            Assert.Null(inventory.Offer(PlayerInventories.Helmet, target.Id, 0, 0, Game.ShapeOf));
            Assert.Equal(originalA, Saved(bound.Target.Item with { SkinTransferSource = null }, catalog));
            Assert.Equal(originalB, Saved(bound.Target.Item.SkinTransferSource!, catalog));
            var payload = bound.Target.Item.Payload();
            Assert.Equal(0x40, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(10)) & 0x40);
            Assert.Equal(b.Payload(), payload.AsSpan(13, b.Payload().Length).ToArray());
            var stash = AccountStash.For(name, catalog);
            var moved = ItemMoves.InventoryToStash(stash, inventory, 0, target.Id, Game.ShapeOf, out _);
            Assert.NotNull(moved);
            var stashItem = stash.Inventory(0).Items.Single();
            Assert.Equal(originalB, Saved(stashItem.Item.SkinTransferSource!, catalog));
            Assert.NotNull(ItemMoves.StashToCursor(stash, inventory, 0, stashItem.Id));
            var held = inventory.Items(PlayerInventories.Cursor).Single();
            Assert.NotNull(inventory.Place(PlayerInventories.Helmet, 0, 0, Game.ShapeOf, out _));
            uint targetId = held.Id;
            PlayerInventories.Forget(name);
            inventory = PlayerInventories.For(name, catalog);
            Assert.Equal(originalB, Saved(inventory.Items(PlayerInventories.Helmet).Single().Item.SkinTransferSource!, catalog));
            Saves.FailWrite = key => key == name;
            Assert.Throws<IOException>(() => inventory.UnbindSkin(targetId, out _));
            Saves.FailWrite = null;
            inventory = PlayerInventories.For(name, catalog);
            Assert.Empty(inventory.Items(PlayerInventories.Cursor));
            Assert.NotNull(inventory.Items(PlayerInventories.Helmet).Single().Item.SkinTransferSource);
            var returned = inventory.UnbindSkin(targetId, out _)!;
            Assert.Equal(originalA, Saved(returned.Target.Item, catalog));
            Assert.Equal(originalB, Saved(returned.Source.Item, catalog));
            Assert.Null(inventory.UnbindSkin(targetId, out _));
            using var count = Saves.Db.Command("SELECT count(*) FROM item_skin_transfers");
            Assert.Equal(0L, count.ExecuteScalar());
        }
        finally { Saves.FailWrite = null; PlayerInventories.Forget(name); TestEnvironment.ReleaseOwnSaves(); }
    }

    [Fact]
    public void Optional_item_stat_sources_have_independent_defences_and_never_supply_gems_or_skill_grants()
    {
        var previous = Program.GameLogicOrNull;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        string name = "Sources" + Guid.NewGuid().ToString("N");
        var catalog = Program.Items;
        var conn = new ClientState(new(1, "sources", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = name, God = true, PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
            Experience = CharacterStats.ExperienceFor(100), Pools = new(100, 1000, 1000, int.MaxValue, 0,
                Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        IDisposable? provider = null;
        try
        {
            CharacterSaves.SetInventories(name, [], null);
            var inventory = PlayerInventories.For(name, catalog);
            var type = catalog.All.First(t => t.Supported && t.Class == "Body Armour" && catalog.Defences(t)?.ArmourMin > 0);
            var a = ItemCatalog.Create(type) with { Quality = 20,
                Mods = [Mod(catalog, "base_maximum_life", 60), Mod(catalog, "local_physical_damage_reduction_rating_+%", 50)] };
            var b = ItemCatalog.Create(type) with { Quality = 10,
                Mods = [Mod(catalog, "base_maximum_life", 80), Mod(catalog, "local_physical_damage_reduction_rating_+%", 100)],
                Sockets = [new(SocketColour.White, ItemCatalog.Create(catalog.Find("Metadata/Items/Gems/SkillGemFireball")!))] };
            var host = inventory.Add(1, 0, 0, a with { SkinTransferSource = b });
            uint life = Program.Stats.Key("base_maximum_life")!.Value;
            int baseline = Game.CharacterStatSources(conn).Stats.GetValueOrDefault(life);
            decimal armour = Game.EquippedBaseDefences(conn).Armour;
            var extra = new InventoryItem(0x80000000u | host.Id, 0, 0, b with { Sockets = [], Links = null });
            provider = GameEvents.ItemStatSources.Add("Game", (_, _) => [(1, extra)]);
            Assert.Equal(baseline + 80, Game.CharacterStatSources(conn).Stats.GetValueOrDefault(life));
            Assert.Equal(armour + ItemDefences.Calculate(b, catalog.Defences(type)!.Value, catalog.Mods).Armour,
                Game.EquippedBaseDefences(conn).Armour);
            Assert.Equal(2, Game.EquipmentForStats(conn).Count);
            Assert.Equal(2, Game.EquipmentForStats(conn, Game.EquipmentForStats(conn)).Count);
            Assert.Single(inventory.EquipmentSnapshot());
            Assert.Empty(inventory.SocketedGems());
            Assert.Empty(Game.AllWornGrantedSkillsOf(conn));
            Assert.Empty(Game.GemSkillEffects(conn));
            provider.Dispose(); provider = null;
            Assert.Equal(baseline, Game.CharacterStatSources(conn).Stats.GetValueOrDefault(life));
            Assert.Equal(armour, Game.EquippedBaseDefences(conn).Armour);
            Assert.Same(b, inventory.Items(1).Single().Item.SkinTransferSource);
        }
        finally { provider?.Dispose(); PlayerInventories.Forget(name); Program.SetGameLogic(previous!); }
    }
}
