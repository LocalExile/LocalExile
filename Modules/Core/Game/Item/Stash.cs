using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // The account's stash (AccountStash): every character of the account shares it. Without an account (an old Auth) the
    // character's name stands in.
    public static AccountStash StashOf(Player player) =>
        player.TradeStashOpen && !player.Character.Ssf ? TradeStashOf(player)
            : player.GuildStashOpen && !player.Character.Ssf ? GuildStashOf(player)
            : PersonalStashOf(player);

    // The personal stash (and the account's gold, kept with it), whichever stash panel is open: the account's in the
    // character's league (AccountStash.Key).
    public static AccountStash PersonalStashOf(Player player) => AccountStash.For(AccountStash.Key(player.Character.Account ?? CharacterName(player), player.Character.League), ItemsFor(player));

    // The guild stash: shared by members of the same guild when in a guild, else per-account fallback. SSF refuses it.
    public static AccountStash GuildStashOf(Player player) =>
        AccountStash.ForGuild(GameEvents.ResolveGuildStorageKey(player) ?? player.Character.Account ?? CharacterName(player),
            ItemsFor(player), player.Character.League, player.Character.Ssf);

    // The async-trade merchant stash (kind 2, S→C 0x7b kind 0x63). SSF refuses it.
    public static AccountStash TradeStashOf(Player player) =>
        AccountStash.ForTrade(player.Character.Account ?? CharacterName(player),
            ItemsFor(player), player.Character.League, player.Character.Ssf);

    // Resolves a stash by its wire kind (0 personal, 1 guild, 2 async-trade); null when unsupported or SSF-blocked.
    public static AccountStash? StashByKind(Player player, byte kind) => kind switch
    {
        InstanceStash.Personal => PersonalStashOf(player),
        InstanceStash.Guild when !player.Character.Ssf => GuildStashOf(player),
        InstanceStash.Trade when !player.Character.Ssf => TradeStashOf(player),
        _ => null,
    };

    // Whether the player may manage tab metadata (reorder/rename/colour/public/folder) for this stash kind.
    public static bool CanManageStashKind(Player player, byte kind) => kind switch
    {
        InstanceStash.Personal => true,
        InstanceStash.Guild => !player.Character.Ssf && player.GuildStashOpen && GameEvents.CanManageGuildStash(player),
        InstanceStash.Trade => !player.Character.Ssf && player.TradeStashOpen,
        _ => false,
    };

    // The tabs of that stash whose contents this connection has.
    public static HashSet<int> TabsSentOf(Player player, AccountStash stash) =>
        stash.IsGuild ? player.Connection.GuildStashTabsSent
            : stash.IsTrade ? player.Connection.TradeStashTabsSent
            : player.Connection.StashTabsSent;

    static (StashCatalog.Placement Personal, StashCatalog.Placement Guild)? AdditionalTownStashes(Player player) => player.Layout.Id switch
    {
        "1_2_town" => (new(192, 283, 0), new(182, 283, 0)),
        "1_4_town" or "2_9_town" => (new(214, 512, 310 * MathF.PI / 180), new(213, 485, 210 * MathF.PI / 180)),
        "1_5_town" => (new(337, 336, 282 * MathF.PI / 180), new(335, 347, 282 * MathF.PI / 180)),
        "2_7_town" => (new(598, 628, 0), new(598, 588, MathF.PI)),
        "2_8_town" => (new(296, 423, 31 * MathF.PI / 180), new(287, 418, 31 * MathF.PI / 180)),
        _ => null,
    };

    // The town's Stash object (Agents/docs/protocol/18-stash.md §1) where it has one.
    public static async Task SpawnTownStash(Player player)
    {
        var scene = HideoutFurniture(player);
        var additional = AdditionalTownStashes(player);
        var town = additional?.Personal ?? StashCatalog.In(player.Layout.Hash);
        var at = scene is { } furnished ? new StashCatalog.Placement(furnished.Stash.X, furnished.Stash.Y, furnished.Stash.Rotation)
            : town is not null && StashCatalog.FromArrival(player.Layout.Hash)
                ? town with { X = player.Layout.SpawnX + town.X, Y = player.Layout.SpawnY + town.Y } : town;
        if (at is null || Environment.GetEnvironmentVariable("Game_STASH") == "0") return;
        uint id = player.TakeObjectId();
        var stash = new StashObject(at.X, at.Y, at.Rotation);
        await SendWorldCreate(player, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(StashObject.MetadataPath), stash.Blob()),
            $"0x02bb create stash 0x{id:x} at ({at.X}, {at.Y})");
        await SendWorldRecord(player, id, InstanceProtocol.EnterWakeZone(id), $"0x01a9 stash 0x{id:x} enters wake zone");
        player.StashObject = id;
        player.StashAt = (at.X, at.Y);
        SetAreaStash(player, id, (at.X, at.Y));
        await RegisterStationaryCollision(player, id, StashObject.MetadataPath, at.X, at.Y, at.Rotation,
            scene is not null ? "stash" : null);
        // The supplied towns and hideouts also get a guild stash, except in SSF.
        if (additional is null && HideoutCatalog.ByArea(player.Layout.Hash) is null
            && player.Layout.Hash is not (StashCatalog.DelveHub or StashCatalog.MenagerieHub or StashCatalog.SanctumHub or StashCatalog.Kingsmarch or StashCatalog.HeistHub or StashCatalog.ChayulaHub or StashCatalog.DeepwaterHub or AreaLayouts.LioneyesWatch
                or StashCatalog.SarnEncampment or StashCatalog.LioneyesWatchAct6 or StashCatalog.OriathDocks)) return;
        var townGuild = scene is null ? additional?.Guild ?? StashCatalog.GuildIn(player.Layout.Hash) : null;
        if (townGuild is { } offset && StashCatalog.FromArrival(player.Layout.Hash))
            townGuild = offset with { X = player.Layout.SpawnX + offset.X, Y = player.Layout.SpawnY + offset.Y };
        var (gx, gy) = scene?.Guild ?? (townGuild is { } tg ? (tg.X, tg.Y) : player.Layout.Near((at.X, at.Y)));
        float guildRotation = SavedHideoutPlacements(player)?.GetValueOrDefault("guild-stash")?.Rotation ?? townGuild?.Rotation ?? at.Rotation;
        if (!player.Character.Ssf)
        {
            uint guild = player.TakeObjectId();
            await SendWorldCreate(player, guild, () => InstanceEntities.CreateObject(guild, InstanceEntities.ObjectTypeHash(StashObject.GuildMetadataPath),
                new StashObject(gx, gy, guildRotation).Blob()),
                    $"0x02bb create guild stash 0x{guild:x} at ({gx}, {gy})", wake: true, awaitBirth: false);
            player.GuildStashObject = guild;
            player.GuildStashAt = (gx, gy);
            SetAreaGuildStash(player, guild, (gx, gy));
            await RegisterStationaryCollision(player, guild, StashObject.GuildMetadataPath, gx, gy, guildRotation,
                scene is not null ? "guild-stash" : null);
        }
        // The stations of the modules this league uses. One taken away is simply not placed: its spot stays free.
        player.CurrentHideoutScene = scene;
        if (scene is not null) await GameEvents.RunHideoutReady(player);
    }

    // A click on the Stash (C→S 0x24): walk there, then S→C 0x7b kind 1 opens the stash panel. Its affinity argument selects
    // the tab carrying that affinity bit in the tab list, which only the account's last tab does (AccountStash.Records).
    public static async Task OpenStash(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        await RefreshBenchBalance(player, clear: true);
        player.BenchOpen = false;
        player.ModuleWindow = null;
        player.ModuleWindowAnchor = null;
        if (player.StashObject == 0) return;
        var at = player.StashAt;
        if (!TryBeginObjectWalk(player, requestedAt, at.X, at.Y, Player.StashBlockerSize, out var objectWalk)) return;
        var (action, now, here, stop, arrive) = objectWalk;
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0024 stash ({use}) from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
        if (arrive > now) await AreaDelay(player, arrive - now);
        if (!PlayerActionCurrent(player, action)) return;
        player.StashOpen = true;
        player.GuildStashOpen = false;
        player.TradeStashOpen = false;
        var stash = StashOf(player);
        // The panel keeps its tab while this connection lasts; only its first open here is moved to the account's last tab.
        // A second 0x7b while it is open would close it (0x140dc2640 toggles), so the choice is made now.
        if (!player.StashOpened)
        {
            player.StashOpened = true;
            player.StashForcedTab = stash.LastTab;
            player.StashForcedUntil = AreaTime(player) + StashForcedWindow;
            await Send(player, InstanceWaypoints.OpenUi(InstanceStash.OpenStashUi, player.StashObject, StashSelectAffinity),
                $"0x007b open the stash on tab {stash.LastTab} \"{stash.NameOf(stash.LastTab)}\" (affinity {StashSelectAffinity})",
                still: () => PlayerActionCurrent(player, action));
        }
        else
        {
            if (player.Connection.StashTabsSent.Count > 0)
            {
                var tabs = stash.Records(null, player.Connection.StashTabsSent, ItemsFor(player));
                if (tabs.Any(t => !t.Loaded && player.Connection.StashTabsSent.Contains(t.Index)))
                    await Send(player, InstanceStash.TabList(tabs), "0x0136 stash pages before reopen");
            }
            await Send(player, InstanceWaypoints.OpenUi(InstanceStash.OpenStashUi, player.StashObject, InstanceStash.NoAffinity),
                "0x007b open the stash on the panel's own tab", still: () => PlayerActionCurrent(player, action));
        }
    }

    // Opens the async-trade merchant stash panel (Faustus "Manage Shop", NPCTalk SendOpenScreen(AsyncTradeStashWindow)):
    // sends S→C 0x136 kind 2, S→C 0x02ae earnings balances, and S→C 0x7b kind 0x63. Refused in SSF.
    public static async Task OpenAsyncTradeStash(Player player)
    {
        if (player.Closed || player.LeavingArea) return;
        player.TalkingTo = 0;
        player.InNpcTalk = false;
        player.NpcFinishFlag = null;
        player.NpcFinishLeagueFlag = null;
        player.NpcTalkScript = null;
        player.NpcRewardOffer = null;
        await Send(player, InstanceNpcs.Talk(0), "0x0084 close NPC dialogue for async-trade stash");
        if (player.Character.Ssf)
        {
            await Send(player, InstanceChat.ChatLine("Merchant shop is unavailable in Solo Self-Found."),
                "0x0017 async-trade refused (SSF)");
            return;
        }
        await RefreshBenchBalance(player, clear: true);
        player.StashOpen = false;
        player.GuildStashOpen = false;
        player.BenchOpen = false;
        player.ModuleWindow = null;
        player.ModuleWindowAnchor = null;
        player.TradeStashOpen = true;
        player.TradeStashAt = player.Walk.Position(AreaTime(player));
        var trade = TradeStashOf(player);
        var tabs = trade.Records(null, player.Connection.TradeStashTabsSent, ItemsFor(player));
        await Send(player, InstanceStash.TabList(tabs, InstanceStash.Trade),
            $"0x0136 async-trade stash tab list: {tabs.Count} tab(s)");
        await Send(player, InstanceAsyncTrade.EncodeEarnings(trade.TradeEarnings()),
            "0x02ae async-trade earnings balances");
        await Send(player, InstanceWaypoints.OpenUi(InstanceStash.OpenAsyncTradeStashUi),
            "0x007b open async-trade stash window (0x63)");
    }

    // C→S 0x02ac: sends the full async-trade sale history snapshot (S→C 0x02ad, trailing isIncremental = 0).
    public static async Task SendAsyncTradeHistory(Player player)
    {
        if (player.Closed || player.LeavingArea) return;
        if (player.Character.Ssf)
        {
            await Send(player, InstanceAsyncTrade.EncodeHistory([], isIncremental: false),
                "0x02ad async-trade history (SSF empty)");
            return;
        }
        var trade = TradeStashOf(player);
        var catalog = ItemsFor(player);
        var entries = new List<InstanceAsyncTrade.SaleEntry>();
        foreach (var saved in trade.TradeSaleHistory())
        {
            try
            {
                var item = CharacterSaves.FromSaved(saved.Item, catalog);
                entries.Add(new(saved.Timestamp, saved.CurrencyTypeHash, saved.CurrencyAmount, saved.Sold, item.Payload()));
            }
            catch (UnknownSavedItemException) { }
        }
        await Send(player, InstanceAsyncTrade.EncodeHistory(entries, isIncremental: false),
            $"0x02ad async-trade history: {entries.Count} entry(ies)");
    }

    // C→S 0x02b0: withdraw earned currency from async-trade earnings into the player's main inventory.
    public static async Task TakeAsyncTradeEarnings(Player player, InstanceAsyncTrade.TakeEarningsRequest req)
    {
        if (player.Closed || player.LeavingArea || player.Character.Ssf || !player.TradeStashOpen || player.PlayerLife == 0) return;
        var trade = TradeStashOf(player);
        if (req.Amount == 0 || ItemsFor(player).ByHash(req.CurrencyTypeHash) is not { Supported: true } currencyType)
        {
            await Send(player, InstanceAsyncTrade.EncodeEarnings(trade.TradeEarnings()),
                $"0x02ae async-trade earnings unchanged (req 0x{req.CurrencyTypeHash:x8} x{req.Amount})");
            return;
        }
        var items = ItemsOf(player);
        bool hadBalance = false;
        int placed = 0;
        IReadOnlyList<PlayerInventories.Stacking> stacks = [];
        IReadOnlyList<InstanceAsyncTrade.EarningBalance> after = [];
        // Compute available balance, stack into inventory, and debit earnings inside one atomic DB transaction so
        // concurrent 0x02b0 packets serialize cleanly without TOCTOU overdraw or partial mutation.
        Saves.Atomically(() =>
        {
            var balance = trade.TradeEarnings().FirstOrDefault(b => b.CurrencyTypeHash == req.CurrencyTypeHash);
            uint take = Math.Min(req.Amount, balance.Amount);
            if (take > 0)
            {
                hadBalance = true;
                int capped = (int)Math.Min(take, int.MaxValue);
                (placed, stacks) = ItemMoves.ReceiveCurrency(items, ItemCatalog.Create(currencyType), capped, ShapeOf,
                    amount => amount > 0 && trade.TryDebitTradeEarnings(req.CurrencyTypeHash, (uint)amount, out _));
            }
            after = trade.TradeEarnings();
        });
        if (placed > 0)
        {
            foreach (var stack in stacks)
                await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                    items.NextRevision(PlayerInventories.MainInventory), stack.RemovedIds, stack.AddedItems),
                    $"0x00bf async-trade earnings withdrawn: {placed} {currencyType.Name}");
        }
        else if (hadBalance)
        {
            await Send(player, InstanceItems.InventoryFull(), "0x0117 inventory full taking async-trade earnings");
        }
        await Send(player, InstanceAsyncTrade.EncodeEarnings(after),
            $"0x02ae async-trade earnings after taking {placed} {currencyType.Name}");
    }

    // C→S 0xc7: the panel shows a tab whose contents it does not have (or it just opened on it). The first time per
    // connection the tab's inventory goes out (S→C 0xc0, manager 0, row 26), then S→C 0xc8 marks the tab loaded and the panel
    // builds its page. The tab asked for is the one on screen: the stash opens on it next time.
    public static async Task SendStashTab(Player player, ushort tab, byte kind)
    {
        if (StashByKind(player, kind) is not { } stash || tab >= stash.Count)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x00c7 stash {kind} tab {tab}: no such tab");
            return;
        }
        if (stash.UniqueParent(tab) != tab)
        {
            await SendUniqueChild(player, stash, tab);
            return;
        }
        // The panel asks for the tab it shows: as it opens (its tab before the selection) and when a tab without contents
        // is picked. The client sends its queue newest first, so during the first open the forced tab's request comes before
        // the panel's old one; that one is answered but not taken as the player's choice.
        bool forcedOpen = !stash.IsGuild && !stash.IsTrade && player.StashForcedTab >= 0 && AreaTime(player) < player.StashForcedUntil;
        bool chosen = !forcedOpen || tab == player.StashForcedTab;
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x00c7 stash {kind} tab {tab} \"{stash.NameOf(tab)}\" ({stash.TypeOf(tab)})" +
            (chosen ? "" : $" (the panel's tab before it opened on {player.StashForcedTab}: not kept as the last tab)"));
        if (chosen) stash.SetLastTab(tab);
        uint inventory = stash.InventoryOf(tab);
        byte manager = stash.IsGuild ? InstanceStash.GuildManager : (byte)0;
        var sent = TabsSentOf(player, stash);
        if (sent.Add(tab))
        {
            var (slot, items) = stash.Inventory(tab);
            await Send(player, InstanceInventories.AddInventory(inventory, slot, manager, items),
                $"0x00c0 stash tab {tab} inventory {inventory} ({slot.Width} x {slot.Height}): {items.Count} item(s)");
        }
        await Send(player, InstanceStash.TabLoaded(tab, inventory, stash.StashKind), $"0x00c8 stash {kind} tab {tab} loaded");
        foreach (int child in stash.UniqueChildren(tab)) await SendUniqueChild(player, stash, child);
    }

    static async Task SendUniqueChild(Player player, AccountStash stash, int tab)
    {
        var sent = TabsSentOf(player, stash);
        byte manager = stash.IsGuild ? InstanceStash.GuildManager : (byte)0;
        if (sent.Add(tab))
        {
            var (slot, items) = stash.Inventory(tab);
            await Send(player, InstanceInventories.AddInventory(stash.InventoryOf(tab), slot, manager, items),
                $"Unique category {stash.NameOf(tab)}: {items.Count} items");
        }
        await Send(player, InstanceStash.TabLoaded((ushort)tab, stash.InventoryOf(tab), stash.StashKind), null);
    }

    public static async Task RefreshUniqueCollection(Player player, AccountStash stash, int parent)
    {
        var sent = TabsSentOf(player, stash);
        byte kind = stash.StashKind;
        // Advertise hidden category records before the loaded notifications that bind the native collection pages.
        await Send(player, InstanceStash.TabList(stash.Records(null, sent), kind), "Unique collection category list");
        foreach (int child in stash.UniqueChildren(parent))
        {
            if (sent.Remove(child)) await Send(player, InstanceInventories.RemoveInventory(stash.InventoryOf(child),
                stash.IsGuild ? InstanceStash.GuildManager : (byte)0), null);
            await SendUniqueChild(player, stash, child);
        }
    }

    /// <summary>Registers a stash tab's inventory (0xc0 + 0xc8) before a 0xbf when this connection has not sent it yet
    /// (for example an affinity Ctrl+click or a Map/Unique parent drop routed into an unopened child). A 0xbf for an
    /// inventory the client does not have aborts the game ("Got an inventory set change for an inventory that isn't in the
    /// set", #605, #710).</summary>
    static async Task EnsureTabLoaded(Player player, AccountStash stash, int tab)
    {
        var sent = TabsSentOf(player, stash);
        if (!sent.Add(tab)) return;
        var (slot, items) = stash.Inventory(tab);
        byte manager = stash.IsGuild ? InstanceStash.GuildManager : (byte)0;
        await Send(player, InstanceInventories.AddInventory(stash.InventoryOf(tab), slot, manager, items),
            $"0x00c0 stash tab {tab} inventory {stash.InventoryOf(tab)} (registered before change)");
        await Send(player, InstanceStash.TabLoaded((ushort)tab, stash.InventoryOf(tab), stash.StashKind), $"0x00c8 stash tab {tab} loaded");
    }

    /// <summary>A change to a stash tab's items (S→C 0xbf). Every one goes out through here: an unopened tab is registered
    /// first (EnsureTabLoaded), and the message names the server's inventory id for the tab, not one the client sent.</summary>
    public static async Task SendTabChange(Player player, AccountStash stash, int tab,
        IReadOnlyList<uint> gone, IReadOnlyList<InventoryItem> now, string? label)
    {
        await EnsureTabLoaded(player, stash, tab);
        await Send(player, InstanceItems.InventoryChange(stash.InventoryOf(tab), stash.NextRevision(tab), gone, now,
            stash.IsGuild ? InstanceStash.GuildManager : (byte)0), label);
    }

    // C→S 0x28 on a stash tab: the item leaves the tab for the (empty) cursor, keeping its id; both saves in one transaction
    // (ItemMoves).
    public static async Task PickUpFromStash(Player player, int tab, InstanceItems.PickUpItem pick)
    {
        var stash = StashOf(player);
        var items = ItemsOf(player);
        if (stash.Inventory(tab).Items.FirstOrDefault(i => i.Id == pick.ItemId) is { } source
            && ShapeOf(source.Item) is { } shape && source.Item.StackSize > Math.Max(1, (int)shape.MaxStack))
        {
            await SplitStack(player,
                new(stash.InventoryOf(tab), pick.ItemId, (uint)Math.Max(1, (int)shape.MaxStack), 0), "");
            return;
        }
        if (ItemMoves.StashToCursor(stash, items, tab, pick.ItemId) is not { } held)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0028 pick up {pick} from stash tab {tab}: refused");
            return;
        }
        // The tab the click names, not the id the client sent: that id can be one it just unloaded (live 2026-11-06, #605).
        await SendTabChange(player, stash, tab, [held.Id], [], $"0x00bf stash tab {tab}: remove item {held.Id}");
        await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
            items.NextRevision(PlayerInventories.Cursor), [], [held]), $"0x00bf cursor: add item {held.Id} (from the stash)");
    }

    // C→S 0x2a on a stash tab: the held item goes into the tab (with a stash id, AccountStash.Place) or onto a stack of its
    // type there; the one item it was dropped on comes onto the cursor. One transaction (ItemMoves.HeldToStash).
    public static async Task PlaceInStash(Player player, int tab, InstanceItems.PlaceItem place)
    {
        var stash = StashOf(player);
        var items = ItemsOf(player);
        if (ItemMoves.HeldToStash(stash, items, tab, (int)place.X, (int)place.Y, place.A != 0, ShapeOf, out string why) is not { } done)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x002a place {place} in stash tab {tab}: refused ({why})");
            return;
        }
        tab = done.Tab;
        var (inTab, before) = (done.InTab, done.HeldBefore);
        if (done.Merged)
            await SendTabChange(player, stash, tab, [inTab.Id], [inTab], $"0x00bf stash tab {tab}: stack {inTab.Id} now {inTab.Item.StackSize}");
        else
            await SendTabChange(player, stash, tab, done.Displaced is null ? [] : [done.Displaced.Id], [inTab],
                $"0x00bf stash tab {tab}: add item {inTab.Id} at ({inTab.X}, {inTab.Y})" + (done.Displaced is null ? "" : $", remove item {done.Displaced.Id}"));
        await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
            items.NextRevision(PlayerInventories.Cursor), [before.Id], done.Held is null ? [] : [done.Held]),
            "0x00bf cursor: " + (done.Held is null ? $"remove item {before.Id}" : done.Displaced is null ? $"stack {before.Id} now {done.Held.Item.StackSize}" : $"remove item {before.Id}, hold item {done.Held.Id}"));
    }

    // C→S 0xe0: Ctrl + click on an inventory item while a stash tab is shown: into that tab (ItemMoves.InventoryToStash: a
    // stack onto the tab's stacks, a special tab only its own items, a slotted tab into the item's slot), one transaction.
    public static async Task MoveToStash(Player player, InstanceStash.MoveToTab move)
    {
        bool allowed = move.Kind == InstanceStash.Personal
            || (move.Kind == InstanceStash.Trade && !player.Character.Ssf && player.TradeStashOpen);
        if (!allowed)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x00e0 move item {move.ItemId} to stash tab {move.Tab} ({move}): refused");
            return;
        }
        await CtrlClickToStash(player, move.Tab, move.ItemId, move.Flags, $"0x00e0 ({move})");
    }

    // C→S 0xe1: Ctrl + click on an inventory item that has an affinity tab (currency …): into the tab the client picked for it
    // (StashTabAffinities), whichever tab is shown.
    public static async Task MoveToAffinityTab(Player player, InstanceStash.MoveToAffinityTab move, string hex)
    {
        if (move.Tabs is not [var (tab, affinity), ..])
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x00e1 move item {move.ItemId} to its affinity tab: refused (no tab named): {hex}");
            return;
        }
        await CtrlClickToStash(player, tab, move.ItemId, move.Flags, $"0x00e1 (affinity {affinity})");
    }

    // Ctrl + right click (both messages' flag): every stack of that type in the same inventory goes, the clicked one first; each
    // stack is one Ctrl + click (its own transaction), so a full slot stops only what doesn't fit.
    static async Task CtrlClickToStash(Player player, int tab, uint itemId, byte flags, string why)
    {
        var items = ItemsOf(player);
        if ((flags & InstanceStash.MoveAllOfType) != 0 && items.RowOf(itemId) is byte row
            && items.Items(row).FirstOrDefault(i => i.Id == itemId) is { } clicked)
        {
            uint[] ids = [clicked.Id, .. items.Items(row).Where(i => i.Item.TypeHash == clicked.Item.TypeHash && i.Id != clicked.Id).Select(i => i.Id)];
            foreach (uint id in ids)
                await CtrlClickToStash(player, tab, id, $"{why} Ctrl + right click, all {ids.Length} stacks");
            return;
        }
        await CtrlClickToStash(player, tab, itemId, why);
    }

    public static async Task CtrlClickToStash(Player player, int tab, uint itemId, string why)
    {
        var stash = StashOf(player);
        var items = ItemsOf(player);
        var moved = ItemMoves.InventoryToStash(stash, items, tab, itemId, ShapeOf, out string refused);
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= {why}: move item {itemId} to stash tab {tab}" + (moved is null ? $": refused ({refused})" : ""));
        if (moved is not null) await SendTransfer(player, items, stash, moved, tabFirst: false);
    }

    // An item out of a stash tab into the main inventory (C→S 0xe3 / 0xf5 / 0xf9), a stack onto the inventory's stacks first
    // (ItemMoves.StashToInventory), one transaction.
    public static async Task TakeFromStash(Player player, int tab, uint itemId, bool all, byte? kind = null)
    {
        var stash = kind is byte k ? StashByKind(player, k) : StashOf(player);
        if (stash is null) return;
        var items = ItemsOf(player);
        if (ItemMoves.StashToInventory(stash, items, tab, itemId, ShapeOf, all) is not { } moved)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} stash tab {tab} item {itemId}: refused (not there, unknown type or no room in the inventory)");
            return;
        }
        await SendTransfer(player, items, stash, moved, tabFirst: true);
    }

    // Ctrl + click on an oil in the blight tab (C→S 0xf5; panelState is 1 either way): with an item in the anoint cell one
    // oil goes to the first free input cell, and nothing happens when they are full; otherwise into the inventory
    // (retail, 2026-11-24).
    public static async Task TakeBlightOil(Player player, int tab, uint itemId, byte kind)
    {
        var stash = kind switch
        {
            InstanceStash.Guild when !player.Character.Ssf => GuildStashOf(player),
            InstanceStash.Guild => null,
            InstanceStash.Personal => PersonalStashOf(player),
            _ => StashOf(player),
        };
        if (stash is null) return;
        if (ItemMoves.StashToAnointInput(stash, tab, itemId, ShapeOf, out bool full) is { } moved)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} 0x00f5 oil {itemId} -> anoint input cell {moved.TabAdded[0].X}");
            await SendTransfer(player, ItemsOf(player), stash, moved, tabFirst: true);
            return;
        }
        if (full)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} 0x00f5 oil {itemId}: the anoint input cells are full, nothing moves");
            return;
        }
        await TakeFromStash(player, tab, itemId, all: false, kind);
    }

    // What a Ctrl + click moved (ItemMoves.Transfer), to the client: one 0xbf for the inventory row, one for the tab
    // (SendTabChange).
    public static async Task SendTransfer(Player player, PlayerInventories items,
        AccountStash stash, ItemMoves.Transfer t, bool tabFirst)
    {
        List<uint> invGone = [.. t.InventoryChanged.Select(i => i.Id)], tabGone = [.. t.TabChanged.Select(i => i.Id)];
        List<InventoryItem> invNow = [.. t.InventoryChanged, .. t.InventoryAdded], tabNow = [.. t.TabChanged, .. t.TabAdded];
        if (t.InventoryItemId != 0) { invGone.Add(t.InventoryItemId); if (t.LeftInInventory is { } l) invNow.Add(l); }
        if (t.TabItemId != 0) { tabGone.Add(t.TabItemId); if (t.LeftInTab is { } l) tabNow.Add(l); }
        async Task Inventory()
        {
            if (invGone.Count + invNow.Count == 0) return;
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(t.Row), items.NextRevision(t.Row), invGone, invNow),
                $"0x00bf inventory {PlayerInventories.InventoryId(t.Row)}: remove [{string.Join(", ", invGone)}], add [{string.Join(", ", invNow.Select(i => $"{i.Id} at ({i.X}, {i.Y}) x{i.Item.StackSize}"))}]");
        }
        async Task Tab()
        {
            if (tabGone.Count + tabNow.Count == 0) return;
            // The tab may have been unloaded by a tab switch while the panel still shows it: it is loaded again instead of
            // skipping the update, which left the moved item's icon in place until the panel asked again (#605).
            await SendTabChange(player, stash, t.Tab, tabGone, tabNow,
                $"0x00bf stash tab {t.Tab}: remove [{string.Join(", ", tabGone)}], add [{string.Join(", ", tabNow.Select(i => $"{i.Id} at ({i.X}, {i.Y}) x{i.Item.StackSize}"))}]");
        }
        if (tabFirst) { await Tab(); await Inventory(); }
        else { await Inventory(); await Tab(); }
        if (t.InventoryItemId != 0 && PlayerInventories.ShowsOnModel(t.Row)) await SendEquipment(player, items);
    }

    // After the tab settings change a tab (0xcd, 0xce, 0xcf, 0xd1, 0xd2, 0xd3): the tab list again (S→C 0x136); after
    // a rename or colour the settings window waits for it before it closes.
    public static async Task SendTabListAgain(Player player, byte kind)
    {
        if (StashByKind(player, kind) is not { } stash) return;
        var tabs = stash.Records(null, TabsSentOf(player, stash));
        await Send(player, InstanceStash.TabList(tabs, stash.StashKind),
            $"0x0136 stash {kind} tab list again ({tabs.Count} tabs, after the tab settings)");
    }

    // The stash is the one the message's kind names, not the open panel's: with the guild panel open, a kind 1 change was
    // refused and the guild tabs went out as the personal list.
    public static async Task SetTabAffinities(Player player, InstanceStash.SetAffinities set)
    {
        if (set.Kind > InstanceStash.Guild || (set.Kind == InstanceStash.Guild && player.Character.Ssf)) return;
        var stash = set.Kind == InstanceStash.Guild ? GuildStashOf(player) : PersonalStashOf(player);
        uint? now = stash.SetAffinities(set.Tab, set.Changes);
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x00d3 stash {set.Kind} tab {set.Tab} affinities [{string.Join(", ", set.Changes.Select(c => $"{c.Affinity}={(c.On ? "on" : "off")}"))}]" +
            (now is uint bits ? $": now 0x{bits:x}" : ": refused (no such tab)"));
        // The settings window closes on the change echoed as S→C 0xd3; on 0x136 alone it stays open (live 2026-11-24).
        if (now is not null)
            await Send(player, set.Encode(), $"0x00d3 stash {set.Kind} tab {set.Tab} affinities echoed");
        await SendTabListAgain(player, set.Kind);
    }
}
