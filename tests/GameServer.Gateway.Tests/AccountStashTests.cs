using GameServer.Core.Protocol.Instance;
using GameServer.Core.Saves;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

public class AccountStashTests : IDisposable
{
    // The test items' types (Wand, Orbs, the 2 x 2 helmet), so a stash holding them loads again.
    readonly string _folder = TestSaves.Catalog(new(0xd9b1a12f, "Wand", 1, 3), new(0x1234abcd, "Orb", StackSize: 40), new(0x1234, "Helmet", 2, 2));
    readonly string _account = $"Acc{Guid.NewGuid():N}"[..16];

    public AccountStashTests() => TestSaves.Ensure();

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    AccountStash Open() => AccountStash.For(_account, new ItemCatalog(_folder));

    // A restart of the Gateway: the stash is loaded again from the database.
    void Restart() => AccountStash.Forget(_account);

    [Fact]
    public void Each_league_has_its_own_stash_and_gold_and_standard_keeps_the_accounts()
    {
        Assert.Equal(_account, AccountStash.Key(_account, "Standard"));
        Assert.Equal(_account, AccountStash.Key(_account, null));
        var catalog = new ItemCatalog(_folder);
        var standard = AccountStash.For(AccountStash.Key(_account, "Standard"), catalog);
        var allflame = AccountStash.For(AccountStash.Key(_account, "Allflame"), catalog);
        var hardcore = AccountStash.For(AccountStash.Key(_account, "Hardcore Allflame"), catalog);
        standard.AddGold(100);
        allflame.AddGold(7);

        Assert.Equal(100u, standard.Gold);
        Assert.Equal(7u, allflame.Gold);
        Assert.Equal(0u, hardcore.Gold);
        AccountStash.Forget(AccountStash.Key(_account, "Allflame"));
        Assert.Equal(7u, AccountStash.For(AccountStash.Key(_account, "Allflame"), catalog).Gold);
    }

    [Fact]
    public void A_premium_tabs_name_and_colour_are_kept_across_a_restart_and_normal_tabs_refuse_them()
    {
        var stash = Open();
        int premium = stash.Records(null).First(t => t.Type == 1).Index, normal = stash.Records(null).First(t => t.Type == 0).Index;
        Assert.True(stash.Rename(premium, " Maps "));
        Assert.True(stash.SetColour(premium, 0x00ff8000));
        Assert.False(stash.Rename(normal, "Nope"));
        Assert.False(stash.SetColour(normal, 1));
        Restart();
        var tab = Open().Records(null).First(t => t.Index == premium);
        Assert.Equal("Maps", tab.Name);
        Assert.Equal(0x00ff8000u, tab.U2c);
    }

    [Fact]
    public void The_tab_settings_rename_and_colour_messages_are_read()
    {
        var rename = new GameServer.Core.Protocol.Login.LoginWriter().U16(0xcd).U8(0).U16(3).U32(0).U32(0).WStr("Карты").ToArray();
        Assert.Equal(rename.Length, InstanceStash.RenameTabLength(rename));
        Assert.Equal(((byte)0, (ushort)3, "Карты"), InstanceStash.ReadRenameTab(rename));
        byte[] colour = [0x00, 0xce, 0x01, 0x00, 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0x00, 0x11, 0x22, 0x33];
        Assert.Equal(((byte)1, (ushort)2, 0x00112233u), InstanceStash.ReadColourTab(colour));
    }

    [Fact]
    public void The_tab_order_the_panel_sends_is_kept_across_a_restart()
    {
        var stash = Open();
        Assert.Equal(2, stash.SetPositions([(0, 1), (1, 0)]));
        Assert.Equal(0, stash.SetPositions([(0, 1)]));
        Restart();
        var positions = Open().Records(null).ToDictionary(t => t.Index, t => t.Position);
        Assert.Equal(1, positions[0]);
        Assert.Equal(0, positions[1]);
        Assert.Equal(2, positions[2]);
    }

    [Fact]
    public void Gold_changed_concurrently_is_saved_as_it_ends()
    {
        var stash = Open();

        // Every change saves; an older copy of the stash must never land on disk after a newer one.
        Parallel.For(0, 200, _ => stash.AddGold(1));

        Restart();
        Assert.Equal(200u, Open().Gold);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("null")]
    public void A_stash_that_cannot_be_read_is_refused_and_not_replaced_by_starter_tabs(string content)
    {
        Saves.Db.Put(SaveDatabase.Table.Stashes, _account, content);

        Assert.Throws<UnreadableSaveException>(() => Open());
        Assert.Equal(content, Saves.Get(SaveDatabase.Table.Stashes, _account));
    }

    [Fact]
    public void A_stash_holding_an_item_the_game_data_does_not_know_is_refused_and_kept()
    {
        var stash = Open();
        stash.Place(0, 0, 0, new InventoryItem(1, 0, 0, Wand()), Shape, out _);
        string saved = Saves.Get(SaveDatabase.Table.Stashes, _account)!;
        Restart();

        // No item tables at all (not extracted): the wand's type is unknown.
        Assert.Throws<UnknownSavedItemException>(() => AccountStash.For(_account, new ItemCatalog(Path.Combine(_folder, "none"))));
        Assert.Equal(saved, Saves.Get(SaveDatabase.Table.Stashes, _account));
    }

    static Item Wand() => new(0xd9b1a12f, ["Base"], 0x40a1);

    // Every test item is 1 × 3, like a wand.
    static PlayerInventories.Shape? Shape(Item _) => new(1, 3, "Wand");

    [Fact]
    public void A_new_account_gets_the_starter_tabs_of_several_types()
    {
        var tabs = Open().Records(null);

        Assert.True(tabs.Count >= 5);
        Assert.Equal(Enumerable.Range(0, tabs.Count).Select(i => (ushort)i), tabs.Select(t => t.Index));
        Assert.Equal(Enumerable.Range(0, tabs.Count).Select(i => AccountStash.InventoryId(i)), tabs.Select(t => t.Inventory));
        Assert.Contains(tabs, t => t.Type == 0);   // NormalStash
        Assert.Contains(tabs, t => t.Type == 1);   // PremiumStash
        Assert.Contains(tabs, t => t.Type == 7);   // QuadStash
        Assert.Contains(tabs, t => t.Type == 3);   // CurrencyStash
        Assert.All(tabs, t => Assert.False(t.Loaded));
    }

    [Fact]
    public void Only_the_last_tab_carries_the_select_affinity()
    {
        var stash = Open();
        stash.SetLastTab(2);

        var tabs = stash.Records(0);

        Assert.Equal([2], tabs.Where(t => t.Affinities != 0).Select(t => (int)t.Index));
        Assert.Equal(1u, tabs[2].Affinities);
        Assert.All(stash.Records(null), t => Assert.Equal(0u, t.Affinities));
    }

    [Fact]
    public void A_placed_item_gets_a_stash_id_and_comes_back_with_it()
    {
        var stash = Open();
        var held = new InventoryItem(7, 0, 0, Wand());

        var done = stash.Place(1, 3, 2, held, Shape, out _);

        Assert.NotNull(done);
        var (placed, displaced) = done.Value;
        Assert.Null(displaced);
        Assert.True(placed.Id >= AccountStash.FirstItemId);
        Assert.Equal((3, 2), (placed.X, placed.Y));
        Assert.Equal(placed.Id, stash.Take(1, placed.Id)?.Id);
        Assert.Empty(stash.Inventory(1).Items);
    }

    [Fact]
    public void Dropping_on_one_item_swaps_it_and_on_two_refuses()
    {
        var stash = Open();
        var a = stash.Place(0, 0, 0, new InventoryItem(1, 0, 0, Wand()), Shape, out _)!.Value.Placed;
        var b = stash.Place(0, 1, 0, new InventoryItem(2, 0, 0, Wand()), Shape, out _)!.Value.Placed;

        Assert.Null(stash.Place(0, 0, 0, new InventoryItem(3, 0, 0, new Item(0x1234, ["Base"], 0x40a1)), _ => new PlayerInventories.Shape(2, 2, "Helmet"), out string why));
        Assert.Contains("in the way", why);
        var swap = stash.Place(0, 0, 1, new InventoryItem(4, 0, 0, Wand()), Shape, out _);
        Assert.Equal(a.Id, swap!.Value.Displaced!.Id);
        Assert.Equal(2, stash.Inventory(0).Items.Count);
        Assert.Contains(stash.Inventory(0).Items, i => i.Id == b.Id);
    }

    [Fact]
    public void Outside_the_grid_is_refused()
    {
        var stash = Open();
        Assert.Null(stash.Place(0, 11, 10, new InventoryItem(1, 0, 0, Wand()), Shape, out string why));
        Assert.Equal("outside the grid", why);
    }

    [Fact]
    public void Tabs_items_and_the_last_tab_survive_a_restart_without_reusing_ids()
    {
        var stash = Open();
        var placed = stash.Place(4, 5, 5, new InventoryItem(9, 0, 0, Wand()), Shape, out _)!.Value.Placed;
        stash.Take(4, placed.Id);
        var kept = stash.Place(4, 6, 6, new InventoryItem(10, 0, 0, Wand()), Shape, out _)!.Value.Placed;
        stash.SetLastTab(4);
        Restart();

        var again = Open();

        Assert.Equal([(kept.Id, (byte)6, (byte)6)], again.Inventory(4).Items.Select(i => (i.Id, i.X, i.Y)));
        Assert.Equal(0xd9b1a12fu, again.Inventory(4).Items[0].Item.TypeHash);
        Assert.Equal(4, again.LastTab);
        // The id taken out earlier is not handed out again.
        Assert.True(again.NewItemId() > kept.Id);
    }

    [Fact]
    public void The_guild_stash_has_its_own_tabs_inventory_ids_and_item_ids()
    {
        var personal = Open();
        var guild = AccountStash.ForGuild(_account, new ItemCatalog(_folder));

        Assert.True(guild.IsGuild);
        Assert.False(personal.IsGuild);
        Assert.NotSame(personal, guild);
        // Inventory ids from 0x900: the personal stash's (0x800…) never name a guild tab, and the other way round.
        Assert.Equal(AccountStash.GuildFirstInventoryId + 1, guild.InventoryOf(1));
        Assert.Equal(1, guild.TabOf(AccountStash.GuildFirstInventoryId + 1));
        Assert.Null(guild.TabOf(AccountStash.InventoryId(1)));
        Assert.Null(personal.TabOf(AccountStash.GuildFirstInventoryId + 1));
        Assert.All(guild.Records(null), t => Assert.True(t.Inventory >= AccountStash.GuildFirstInventoryId));

        // An item from the personal stash gets a guild id, and back again a personal one.
        var fromPersonal = personal.Place(1, 0, 0, new InventoryItem(7, 0, 0, Wand()), Shape, out _)!.Value.Placed;
        var moved = personal.Take(1, fromPersonal.Id)!;
        var inGuild = guild.Place(0, 0, 0, moved, Shape, out _)!.Value.Placed;
        Assert.InRange(inGuild.Id, AccountStash.GuildFirstItemId, AccountStash.GuildFirstItemId + 0x0fffffff);
        var back = personal.Place(1, 0, 0, guild.Take(0, inGuild.Id)!, Shape, out _)!.Value.Placed;
        Assert.InRange(back.Id, AccountStash.FirstItemId, AccountStash.GuildFirstItemId - 1);
        Assert.True(personal.NewItemId() < AccountStash.GuildFirstItemId);
    }

    [Fact]
    public void Guild_storage_is_partitioned_by_league_and_preserves_standard_legacy_data()
    {
        var catalog = new ItemCatalog(_folder);
        var legacy = AccountStash.ForGuild(_account, catalog);
        legacy.Place(0, 0, 0, new InventoryItem(7, 0, 0, Wand()), Shape, out _);
        var standard = AccountStash.ForGuild(_account, catalog, "Standard");
        var allflame = AccountStash.ForGuild(_account, catalog, "Allflame");
        var hardcore = AccountStash.ForGuild(_account, catalog, "Hardcore Allflame");
        var ruthless = AccountStash.ForGuild(_account, catalog, "Ruthless Allflame");
        Assert.Same(legacy, standard);
        Assert.Single(standard.Inventory(0).Items);
        Assert.Empty(allflame.Inventory(0).Items);
        Assert.Empty(hardcore.Inventory(0).Items);
        Assert.Empty(ruthless.Inventory(0).Items);
        allflame.Place(0, 0, 0, new InventoryItem(8, 0, 0, Wand()), Shape, out _);
        AccountStash.Forget("guild:" + AccountStash.Key(_account, "Allflame"));
        Assert.Single(AccountStash.ForGuild(_account, catalog, "Allflame").Inventory(0).Items);
        Assert.Empty(hardcore.Inventory(0).Items);
    }

    [Fact]
    public void Ssf_cannot_open_guild_storage_even_if_legacy_storage_already_exists()
    {
        var catalog = new ItemCatalog(_folder);
        var legacy = AccountStash.ForGuild(_account, catalog);
        legacy.Place(0, 0, 0, new InventoryItem(7, 0, 0, Wand()), Shape, out _);
        Assert.Throws<InvalidOperationException>(() => AccountStash.ForGuild(_account, catalog, "SSF Standard", solo: true));
        Assert.Throws<InvalidOperationException>(() => AccountStash.ForGuild(_account, catalog, "SSF Allflame", solo: true));
        Assert.Null(Saves.Get(SaveDatabase.Table.Stashes, "guild:" + AccountStash.Key(_account, "SSF Allflame")));
        Assert.Single(legacy.Inventory(0).Items);
        var personal = AccountStash.For(AccountStash.Key(_account, "SSF Allflame"), catalog);
        personal.Place(0, 0, 0, new InventoryItem(8, 0, 0, Wand()), Shape, out _);
        Assert.Same(personal, AccountStash.For(AccountStash.Key(_account, "SSF Allflame"), catalog));
        Assert.Single(personal.Inventory(0).Items);
        Assert.Empty(AccountStash.For(AccountStash.Key(_account, "Allflame"), catalog).Inventory(0).Items);
    }

    [Fact]
    public void Stash_inventory_ids_map_back_to_their_tab()
    {
        var stash = Open();
        Assert.Equal(3, stash.TabOf(AccountStash.InventoryId(3)));
        Assert.Null(stash.TabOf(1));
        Assert.Null(stash.TabOf(AccountStash.InventoryId(stash.Count)));
    }

    [Fact]
    public void Gold_is_the_accounts_and_survives_a_restart()
    {
        var stash = Open();
        Assert.Equal(0u, stash.Gold);
        Assert.Equal(500u, stash.AddGold(500));
        Assert.Equal(0u, stash.AddGold(-1000));   // never below 0
        stash.AddGold(1200);
        Restart();

        Assert.Equal(1200u, Open().Gold);
    }

    [Fact]
    public void Spending_gold_takes_nothing_when_there_is_not_enough()
    {
        var stash = Open();
        stash.AddGold(300);

        Assert.Null(stash.TrySpendGold(301));
        Assert.Equal(300u, stash.Gold);
        Assert.Equal(100u, stash.TrySpendGold(200));
        Assert.Equal(100u, stash.Gold);
    }

    static Item Orbs(int n) => new(0x1234abcd, ["Base", "Usable", "Stack"], 0x1111) { StackSize = (ushort)n };
    static PlayerInventories.Shape? Orb(Item _) => new(1, 1, "StackableCurrency", 40);

    [Fact]
    public void A_held_stack_merges_into_a_tab_stack_of_its_type()
    {
        var stash = Open();
        var under = stash.Place(1, 0, 0, new InventoryItem(7, 0, 0, Orbs(32)), Orb, out _)!.Value.Placed;

        var merged = stash.Merge(1, 0, 0, new InventoryItem(8, 0, 0, Orbs(10)), Orb);

        Assert.Equal((under.Id, (ushort)40, 8), (merged!.Value.Stack.Id, merged.Value.Stack.Item.StackSize, merged.Value.Moved));
        Assert.Null(stash.Merge(1, 0, 0, new InventoryItem(9, 0, 0, Orbs(1)), Orb));   // full now
        Assert.Null(stash.Merge(1, 5, 5, new InventoryItem(9, 0, 0, Orbs(1)), Orb));   // nothing there
    }

    [Fact]
    public void Shrinking_a_tab_stack_keeps_its_id_and_refuses_the_whole_stack()
    {
        var stash = Open();
        var stack = stash.Place(1, 0, 0, new InventoryItem(7, 0, 0, Orbs(12)), Orb, out _)!.Value.Placed;

        Assert.Null(stash.Shrink(1, stack.Id, 12));
        var left = stash.Shrink(1, stack.Id, 5);

        Assert.Equal((stack.Id, (ushort)7), (left!.Id, left.Item.StackSize));
        Assert.Equal([7], stash.Inventory(1).Items.Select(i => (int)i.Item.StackSize));
    }
}
