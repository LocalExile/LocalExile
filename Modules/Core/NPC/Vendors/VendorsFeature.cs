using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using static Program;

static class VendorsFeature
{
    // NPCTalk 950 finishes league flag 1163; the Purchase Items row 952 requires it on the client.
    internal const int LillyGemShopLeagueFlag = 1163;
    static bool CampaignLilly(string id) => id is "Metadata/NPC/Act6/Lilly" or "Metadata/NPC/Act10/Lilly"
        or "Metadata/NPC/Epilogue/Lilly" or "Metadata/NPC/Epilogue/Lilly2";

    internal static async Task<bool> BeginLillyGemIntroduction(Player player, uint id, SpawnedNpc npc)
    {
        if (!CampaignLilly(npc.Npc.Id) || LeagueFor(player).HardMode || player.Character.LeagueQuestFlags.Contains(LillyGemShopLeagueFlag)) return false;
        var flags = player.Character.QuestFlagsSnapshot();
        // These rows have an account context, so the generic automatic-conversation selector leaves them to a module.
        var talk = Npcs.TalksOf(npc.Npc).FirstOrDefault(t => t.Introduction
            && t.StartFlag == GemVendorStock.LillyUnlock && t.Available(flags));
        if (talk is null) return false;
        await global::Game.ExecuteQuestConversation(player, id, npc, talk);
        return player.InNpcTalk;
    }

    internal static async Task FinishLillyGemIntroduction(Player player)
    {
        if (!player.InNpcTalk || player.NpcFinishFlag != GemVendorStock.LillyUnlock
            || player.Character.LeagueQuestFlags.Contains(LillyGemShopLeagueFlag)) return;
        lock (player.Npcs)
            if (player.Npcs.GetValueOrDefault(player.TalkingTo) is not { } npc || !CampaignLilly(npc.Npc.Id)) return;
        await SetLeagueQuestFlags(player, [LillyGemShopLeagueFlag]);
    }

    internal static IReadOnlySet<string> VendorQuestFlags(Player player)
    {
        var flags = player.Character.QuestFlagsSnapshot();
        if (player.Character.LeagueQuestFlags.Contains(LillyGemShopLeagueFlag)) flags.Add(GemVendorStock.LillyUnlock);
        return flags;
    }

    internal static async Task BuyFromShop(Player player, InstanceNpcs.Buy buy)
    {
        if (player.NpcRewardOffer is not null)
        {
            if (PlayerInventories.Row(buy.InventoryId) is byte rewardRow)
                await ClaimNpcReward(player, buy.ItemId, rewardRow, buy.X, buy.Y);
            return;
        }
        if (PlayerInventories.Row(buy.InventoryId) is byte row)
            await Purchase(player,buy.ItemId,row,buy.X,buy.Y);
    }

    internal static async Task BuyAndSocketFromShop(Player player, InstanceNpcs.BuyAndSocket buy)
    {
        if (player.NpcRewardOffer is not null)
        {
            await ClaimNpcRewardIntoSocket(player, buy);
            return;
        }
        await PurchaseAndSocket(player, buy);
    }

    internal static async Task CtrlBuyFromShop(Player player, InstanceNpcs.CtrlBuy buy)
    {
        if (player.NpcRewardOffer is not null) { await ClaimNpcReward(player,buy.ItemId); return; }
        await Purchase(player,buy.ItemId);
    }

    static async Task Purchase(Player player,
        uint id, byte? row = null, byte x = 0, byte y = 0)
    {
        var shop = player.Shop;
        if (shop is null || !shop.Items.TryGetValue(id,out var offer)) return;
        var items = ItemsOf(player);
        var stash = StashOf(player);
        var catalog = ItemsFor(player);
        bool expandedAllowed = ExpandedInventoryGranted(player);
        if (row is byte requested && LevelTooLow(player, requested, offer.Item.Item) is { } low)
        { await player.Commands!.Reply(low); return; }
        var before = items.Items(PlayerInventories.MainInventory).Select(i => i.Id).ToArray();
        bool finite = shop.Finite.TryGetValue(id,out var token);
        bool Consume() => !finite || shop.StockKey is { } key && VendorStock.Consume(CharacterName(player),key,token,PersonalStashOf(player));
        var bought = offer.Item.Item;
        if (Gambling.OfferOf(shop,id) is { } gamble)
        {
            // A gamble: a new item of the offer's kind at its price. It need not have the shown item's size, so it goes to the
            // cell asked for when it fits there and to the inventory's first free cell otherwise; never straight onto equipment.
            if (gamble.Roll(AreaRng(player)) is not { } rolled) { await player.Commands!.Reply("Nothing to gamble for at your level."); return; }
            bought = rolled with { Price = bought.Price };
            if (row is byte asked && asked != PlayerInventories.MainInventory && asked != PlayerInventories.ExpandedInventory && asked != PlayerInventories.Cursor) row = null;
        }
        // A gamble window opened before a module reload lost its offers (Gambling.Open): never sell the "Random …" item itself.
        else if (catalog.ByHash(bought.TypeHash)?.Class == "HiddenItem")
        { await player.Commands!.Reply("Talk to the gambler again to gamble."); return; }
        var purchase = ItemMoves.PurchaseVendor(items,
            shop.Npc.Npc.Id.EndsWith("/Siosa",StringComparison.Ordinal) ? null : stash, PersonalStashOf(player),
            bought,catalog,ShapeOf,Consume,row,x,y,expandedAllowed);
        if (purchase is null && row is not null && bought != offer.Item.Item)
        {
            row = null;
            purchase = ItemMoves.PurchaseVendor(items, stash, PersonalStashOf(player), bought, catalog, ShapeOf, Consume);
        }
        if (purchase is null) { await player.Commands!.Reply("Unable to buy: check the price, inventory space and available stock."); return; }
        if (finite) shop.Items.Remove(id);
        foreach (var payment in purchase.Payments) await SendStashPayment(player,stash,payment.Stash);
        await Send(player,InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
            items.NextRevision(PlayerInventories.MainInventory),before,items.Items(PlayerInventories.MainInventory)),"0x00bf vendor payment and purchase");
        if (purchase.Equipped is { } equipped)
        {
            if (equipped.Row == PlayerInventories.Flask)
                equipped = equipped with { Placed = EmptyIfEquippedInHostileArea(player, items, equipped.Placed) };
            await Send(player,InstanceItems.InventoryChange(PlayerInventories.InventoryId(equipped.Row),
                items.NextRevision(equipped.Row),equipped.Displaced is { } old ? [old.Id] : [],[equipped.Placed]),
                "0x00bf vendor item equipped");
            if (equipped.Displaced is { } displaced)
                await Send(player,InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
                    items.NextRevision(PlayerInventories.Cursor),[],[displaced]),"0x00bf replaced equipment to cursor");
            await SendEquipment(player,items);
            if (equipped.Row == PlayerInventories.Flask)
            {
                await ReconcileTincturesAfterFlaskBarChange(player);
                await RefreshMagebloodFlasks(player);
            }
        }
        else if (purchase.Placed is { } placed)
        {
            if (placed.Row != PlayerInventories.MainInventory)
            {
                uint[] removed = placed.Merged ? [placed.Placed.Id]
                    : placed.Displaced is { } old ? [old.Id] : [];
                await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(placed.Row),
                    items.NextRevision(placed.Row), removed, [placed.Placed]), "0x00bf vendor item placed");
            }
            if (placed.Displaced is { } displaced)
                await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
                    items.NextRevision(PlayerInventories.Cursor), [], [displaced]), "0x00bf replaced item to cursor");
            else if (placed.Rest is { } rest)
                await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
                    items.NextRevision(PlayerInventories.Cursor), [], [rest]), "0x00bf vendor stack overflow to cursor");
        }
        else if (row is byte destination && destination != PlayerInventories.MainInventory)
            await Send(player,InstanceItems.InventoryChange(PlayerInventories.InventoryId(destination),
                items.NextRevision(destination),[],purchase.Bought),"0x00bf vendor item to cursor");
        await Send(player,InstanceItems.InventoryChange(offer.Tab,shop.NextRevision(offer.Tab),[id],
            finite ? [] : [offer.Item],NpcShops.Manager),"0x00bf vendor offer updated");
        await Send(player,InstanceEntities.Gold(PersonalStashOf(player).Gold),"0x02aa vendor gold balance");
        Console.WriteLine($"[gateway] {player.Connection.Remote} vendor purchased {id} from {shop.Npc.Npc.Name}; finite={finite}");
    }

    static async Task PurchaseAndSocket(Player player, InstanceNpcs.BuyAndSocket buy)
    {
        var shop = player.Shop;
        if (shop is null || !shop.Items.TryGetValue(buy.OfferItemId, out var offer)) return;
        if (PlayerInventories.Row(buy.InventoryId) is not byte targetRow)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0089 vendor socket {buy}: refused (unknown inventory)");
            return;
        }
        var items = ItemsOf(player);
        var stash = StashOf(player);
        var catalog = ItemsFor(player);
        if (Gambling.OfferOf(shop, buy.OfferItemId) is not null || catalog.ByHash(offer.Item.Item.TypeHash)?.Class == "HiddenItem")
        {
            await player.Commands!.Reply("Unable to socket this vendor item directly.");
            return;
        }
        var before = items.Items(PlayerInventories.MainInventory).Select(i => i.Id).ToArray();
        bool finite = shop.Finite.TryGetValue(buy.OfferItemId, out var token);
        bool Consume() => !finite || shop.StockKey is { } key && VendorStock.Consume(CharacterName(player), key, token, PersonalStashOf(player));
        bool equipped = ItemModStats.EquipmentRows.Contains(targetRow);
        ItemMoves.VendorSocketPurchase? purchase = null;
        string why = "";
        string? missing = null;
        using (ProjectileMutation(player, advance: false, phase: "vendor gem socket"))
        {
            IReadOnlyList<PlayerSkillSource> displacedSources = [];
            if (equipped && items.Items(targetRow).FirstOrDefault(i => i.Id == buy.TargetItemId) is { } host
                && buy.Socket < host.Item.Sockets.Count && host.Item.Sockets[(int)buy.Socket].Gem is { } oldGem)
                displacedSources = GemSourceRowsLocked(player, host.Id, (int)buy.Socket, oldGem);
            if (GemRequirementsUnmetAt(player, offer.Item.Item, null, targetItemId: buy.TargetItemId) is { } unmet)
                missing = unmet;
            else
            {
                purchase = ItemMoves.PurchaseAndSocketVendor(items,
                    shop.Npc.Npc.Id.EndsWith("/Siosa", StringComparison.Ordinal) ? null : stash, PersonalStashOf(player),
                    offer.Item.Item, catalog, targetRow, buy.TargetItemId, (int)buy.Socket, ShapeOf, Consume, out why);
                if (purchase is not null && equipped)
                {
                    if (purchase.Displaced is { } displaced)
                    {
                        RetireDirectMinionSourcesLocked(player, displacedSources);
                        ForgetGemActivation(player, displaced.Item);
                    }
                    RecomputePoolsAndSpeed(player);
                    ReconcileBeastCompanionsLocked(player);
                }
            }
        }
        if (missing is not null)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0089 vendor socket {buy}: refused ({missing})");
            return;
        }
        if (purchase is null)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0089 vendor socket {buy}: refused ({why})");
            await player.Commands!.Reply("Unable to buy and socket: check the price, socket compatibility and available stock.");
            return;
        }
        if (finite) shop.Items.Remove(buy.OfferItemId);
        foreach (var payment in purchase.Payments) await SendStashPayment(player, stash, payment.Stash);
        await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
            items.NextRevision(PlayerInventories.MainInventory), before, items.Items(PlayerInventories.MainInventory)),
            "0x00bf vendor payment for direct socket");
        if (targetRow != PlayerInventories.MainInventory)
            await Send(player, InstanceItems.InventoryChange(buy.InventoryId, items.NextRevision(targetRow),
                [purchase.Host.Id], [purchase.Host]),
                $"0x00bf inventory {buy.InventoryId}: item {purchase.Host.Id} with vendor gem in socket {buy.Socket}");
        if (purchase.Displaced is { } displacedGem)
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
                items.NextRevision(PlayerInventories.Cursor), [], [displacedGem]),
                $"0x00bf cursor: hold displaced gem {displacedGem.Id} from socket {buy.Socket}");
        if (equipped) await SendEquipment(player, items);
        await Send(player, InstanceItems.InventoryChange(offer.Tab, shop.NextRevision(offer.Tab), [buy.OfferItemId],
            finite ? [] : [offer.Item], NpcShops.Manager), "0x00bf vendor offer updated");
        await Send(player, InstanceEntities.Gold(PersonalStashOf(player).Gold), "0x02aa vendor gold balance");
        Console.WriteLine($"[gateway] {player.Connection.Remote} vendor purchased and socketed {buy.OfferItemId} into item {buy.TargetItemId} socket {buy.Socket} from {shop.Npc.Npc.Name}; finite={finite}");
    }

    // An item goes into the sell offer (C→S 0xb2, or 0x2a on the offer inventory) from the inventory it is in (the cursor, or
    // the main inventory on a Ctrl-click); the NPC answers with one Scroll Fragment per offered item in its own offer. As in
    // the saved item and its inventory cell remain reserved until the sale ends (PlayerInventories.Offer).
    // The client sees the item only in its offer grid.
    internal static async Task OfferForSale(Player player, NpcSale sale,
        uint fromInventory, uint itemId, byte x, byte y, bool sendReward = true)
    {
        var items = ItemsOf(player);
        if (PlayerInventories.Row(fromInventory) is not byte row || items.Offer(row, itemId, x, y, ShapeOf) is not var (waits, offered))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} sell offer: item {itemId} is not in inventory {fromInventory} (or no cell to wait in)");
            return;
        }
        // Keep the durable item in its reserved server cell, but show it only in the offer grid.
        await Send(player, InstanceItems.InventoryChange(fromInventory, items.NextRevision(row), [offered.Id], []),
            $"0x00bf inventory {fromInventory}: hide offered item {offered.Id}");
        if (row == PlayerInventories.Flask) await ReconcileTincturesAfterFlaskBarChange(player);
        var inOffer = offered with { X = x, Y = y };
        await Send(player, InstanceItems.InventoryChange(sale.Mine, ++sale.MineRevision, [], [inOffer], NpcShops.Manager),
            $"0x00bf sell offer: item {inOffer.Id} at ({x}, {y})");
        if (sendReward) await SendSaleReward(player, sale);
    }

    // Taking an offered item back restores its original inventory view and removes its offer entry.
    internal static async Task TakeFromSale(Player player, NpcSale sale, uint itemId, bool sendReward = true)
    {
        var items = ItemsOf(player);
        if (items.TakeBack(itemId) is not var (row, back))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} sell offer item {itemId}: not offered");
            return;
        }
        await Send(player, InstanceItems.InventoryChange(sale.Mine, ++sale.MineRevision, [itemId], [], NpcShops.Manager),
            $"0x00bf sell offer: remove item {itemId} (back in inventory {PlayerInventories.InventoryId(row)} at ({back.X}, {back.Y}))");
        await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), items.NextRevision(row), [], [back]),
            $"0x00bf return item {back.Id} to inventory {PlayerInventories.InventoryId(row)}");
        if (sendReward) await SendSaleReward(player, sale);
    }

    // Quote once on each offer change. The same immutable outputs are displayed and paid at acceptance.
    internal static async Task SendSaleReward(Player player, NpcSale sale)
    {
        var items = ItemsOf(player);
        var offered = items.Offered().OrderBy(i => i.Id).ToList();
        sale.QuoteError = null;
        VendorRecipes.Quote quote;
        try { quote = VendorSales.Evaluate(offered, Game.AreaRng(player),
            basicSaleMultiplier: LeagueFor(player).Rules.VendorRewardMultiplier, player: player); }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            sale.QuoteError = ex.Message;
            quote = new([], []);
            Console.WriteLine($"[gateway] {player.Connection.Remote} vendor quote refused: {ex.Message}");
        }
        var reward = new List<InventoryItem>();
        bool[,] occupied = new bool[NpcShops.Offer.Width, NpcShops.Offer.Height];
        foreach (var item in quote.Rewards)
        {
            var shape = ShapeOf(item) ?? new PlayerInventories.Shape(1, 1, "");
            (byte X, byte Y)? spot = null;
            for (byte y = 0; y + shape.Height <= NpcShops.Offer.Height && spot is null; y++)
                for (byte x = 0; x + shape.Width <= NpcShops.Offer.Width && spot is null; x++)
                    if (!Enumerable.Range(x, shape.Width).Any(cx => Enumerable.Range(y, shape.Height).Any(cy => occupied[cx, cy]))) spot = (x, y);
            if (spot is not { } cell) { sale.QuoteError = "The vendor reward does not fit in the offer window. Sell fewer items at once."; break; }
            for (int x = cell.X; x < cell.X + shape.Width; x++)
                for (int y = cell.Y; y < cell.Y + shape.Height; y++) occupied[x, y] = true;
            // Random recipe outputs are hidden until acceptance, matching the native vendor UI behavior since 3.13.
            var preview = item.Rarity != ItemRarity.Normal ? item with { Identified = false } : item;
            reward.Add(new(items.NewItemId(), cell.X, cell.Y, preview));
        }
        if (sale.QuoteError is not null) { reward.Clear(); await player.Commands!.Reply(sale.QuoteError); }
        List<uint> before;
        lock (player.Npcs)
        {
            before = [.. sale.Reward.Select(r => r.Id)];
            sale.Reward = reward;
            sale.QuotedItems = offered;
            sale.QuotedRewards = quote.Rewards;
        }
        await Send(player, InstanceItems.InventoryChange(sale.Theirs, ++sale.TheirsRevision, before, reward, NpcShops.Manager),
            $"0x00bf {sale.Npc.Npc.Name}'s offer: {reward.Count} reward stack(s); {string.Join(", ", quote.Recipes)}");
    }

    // C→S 0xb1: Cancel (u8 0). The offered items go back to the inventory and S→C 0xb9 closes the window.
    internal static async Task AnswerSale(Player player, (uint Other, byte Answer) a)
    {
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x00b1 trade answer {a.Answer} (trade {a.Other})");
        await CloseSale(player, accepted: a.Answer != 0);
    }

    // An item Shift-clicked in an inventory while the sell window is open goes into a free cell of the offer.
    internal static async Task OfferFromInventory(Player player, NpcSale sale, uint inventory, uint itemId, bool sendReward = true)
    {
        var items = ItemsOf(player);
        byte? row = PlayerInventories.Row(inventory);
        var it = row is byte r ? items.Items(r).FirstOrDefault(i => i.Id == itemId) : null;
        if (it is null || ShapeOf(it.Item) is not { } shape) { Console.WriteLine($"[gateway] {player.Connection.Remote} sell offer: item {itemId} not in inventory {inventory}"); return; }
        List<(InventoryItem Item, PlayerInventories.Shape? Shape)> offered = [.. items.Offered().Select(o => (o, ShapeOf(o.Item)))];
        (byte X, byte Y)? cell = null;
        for (int x = 0; x + shape.Width <= NpcShops.Offer.Width && cell is null; x++)
            for (int y = 0; y + shape.Height <= NpcShops.Offer.Height && cell is null; y++)
                if (!offered.Any(o => o.Shape is { } s && o.Item.X < x + shape.Width && x < o.Item.X + s.Width && o.Item.Y < y + shape.Height && y < o.Item.Y + s.Height))
                    cell = ((byte)x, (byte)y);
        if (cell is not { } c) { Console.WriteLine($"[gateway] {player.Connection.Remote} sell offer: no room for item {itemId}"); return; }
        await OfferForSale(player, sale, inventory, itemId, c.X, c.Y, sendReward);
    }

    // Ends the sale. Accepted: the offered items are gone and the NPC's offer goes into the inventory, in one save; when the
    // offer doesn't fit (the offered items' cells free up first, so it nearly always does), nothing changes and the window
    // stays open. Cancelled: every offered item is in its cell again (it kept it). Then S→C 0xb9 closes the window.
    internal static async Task CloseSale(Player player, bool accepted)
    {
        NpcSale? sale;
        lock (player.Npcs) sale = player.Sale;
        if (sale is null) Console.WriteLine($"[gateway] {player.Connection.Remote} trade: no sale open");
        accepted &= sale is not null;
        var items = ItemsOf(player);
        if (accepted)
        {
            var offered = items.Offered().OrderBy(i => i.Id).ToList();
            if (offered.Count == 0) { await CloseSale(player, accepted: false); return; }
            bool sameQuote = sale!.QuotedItems.Select(i => (i.Id, i.Item)).SequenceEqual(offered.Select(i => (i.Id, i.Item)));
            PlayerInventories.Stacking? added = null;
            string? refused = sale.QuoteError ?? (!sameQuote ? "The offer has changed. Review the vendor's new offer before accepting." : null);
            if (refused is null && (added = items.Sell(sale.QuotedRewards, ShapeOf)) is null)
                refused = "Not enough room in your inventory for the complete vendor reward.";
            if (refused is not null)
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} sale refused: {refused}");
                await player.Commands!.Reply(refused);
                return;
            }
            Console.WriteLine($"[gateway] {player.Connection.Remote} sale accepted: {offered.Count} item(s), {sale.QuotedRewards.Count} reward stack(s)");
            // Offered items were already removed from the client's inventory view.
            if (added is { Any: true })
                await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                    items.NextRevision(PlayerInventories.MainInventory),
                    added.RemovedIds, added.AddedItems),
                    $"0x00bf inventory 1: sold items out, {added.Changed.Count} stack(s) grown, {added.Added.Count} new (the NPC's offer)");
        }
        else if (sale is not null)
        {
            // Restore the hidden inventory view before closing the offer.
            var returned = items.CancelOffers();
            foreach (var group in returned.GroupBy(i => i.Row))
                await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(group.Key), items.NextRevision(group.Key),
                    [], [.. group.Select(i => i.Item)]), $"0x00bf sale cancelled: restore {group.Count()} item(s)");
        }
        lock (player.Npcs) if (player.Sale == sale) player.Sale = null;
        await Send(player, InstanceNpcs.TradeClose(0, accepted), $"0x00b9 sell window closed ({(accepted ? "accepted" : "cancelled")})");
        // The client is still talking to the NPC (session +0xd7c0 = 2, +0xd7c4 = the NPC, live 2026-10-19) and the player cannot
        // move until 0x84 with object 0 ends the talk. Cancel sends 0x85 for it, Accept sends nothing, so end it here.
        player.TalkingTo = 0;
        player.InNpcTalk = false;
        await Send(player, InstanceNpcs.Talk(0), "0x0084 object 0: npc dialogue closed (sale over)");
    }

    // Sell Items: two empty offer inventories (manager 1: the player's offer, the NPC's), then S→C 0xb8 with no other party
    // (an NPC sale) opens the sell window. Being tested.
    internal static async Task OpenNpcSale(Player player, SpawnedNpc npc)
    {
        player.Shop = null;
        if (player.Sale is not null) await CloseSale(player, accepted: false);
        uint mine = NpcShops.FirstTabId + 0x10, theirs = mine + 1;
        ItemsOf(player).CancelOffers();   // none should be left; a new sale must not count them
        lock (player.Npcs) player.Sale = new NpcSale(npc, mine, theirs);
        await Send(player, InstanceInventories.AddInventory(mine, NpcShops.Offer, NpcShops.Manager), $"0x00c0 sell offer {mine} (manager {NpcShops.Manager})");
        await Send(player, InstanceInventories.AddInventory(theirs, NpcShops.Offer, NpcShops.Manager), $"0x00c0 {npc.Npc.Name}'s offer {theirs} (manager {NpcShops.Manager})");
        player.InNpcTalk = false;
        await Send(player, InstanceNpcs.TradeOpen(0, mine, theirs, InstanceNpcs.TradeActive | InstanceNpcs.TradeNoMouseOver, npc.Npc.Name, npc.Npc.Row),
            $"0x00b8 open the sell window with {npc.Npc.Name} (offers {mine}, {theirs})");
    }

    // Purchase Items: the NPC's stock as one shop tab (S→C 0xc0, inventory manager 1), then S→C 0x8c opens the buy window on it.
    // Gamble for Items: the gamble window's tabs (Gambling.Window), opened as the black market (window kind 10).
    internal static async Task OpenGamble(Player player, uint npcObject, SpawnedNpc npc)
    {
        if (Items.Error is not null) { await OpenNpcShop(player, npcObject, npc); return; }
        var notes = new List<string>();
        var (window, offers) = Gambling.Window(player, npc.Npc.Id, ItemsOf(player).NewItemId, notes);
        foreach (var n in notes) Console.WriteLine($"[gateway] {player.Connection.Remote} gamble {npc.Npc.Name}: {n}");
        await OpenNpcShop(player, npcObject, npc, window, Gambling.WindowKind);
        if (player.Shop is { } shop) Gambling.Remember(shop, offers);
    }

    internal static async Task OpenNpcShop(Player player, uint npcObject, SpawnedNpc npc, VendorStock.Window? prepared = null,
        ushort windowKind = 0)
    {
        if (player.Sale is not null) await CloseSale(player, accepted: false);
        await RefreshBenchBalance(player, clear: true);
        player.BenchOpen = false;
        player.ModuleWindow = null;
        if (Items.Error is { } error) { Console.WriteLine($"[gateway] {player.Connection.Remote} shop: {error}"); return; }
        var notes = new List<string>();
        var window = prepared ?? BuildWindow(player,npc,notes);
        var tabs = window.Tabs;
        bool nativeTabs = window.Key.Length > 0 && !window.Labelled;
        foreach (var n in notes) Console.WriteLine($"[gateway] {player.Connection.Remote} shop {npc.Npc.Name}: {n}");
        // One manager-1 inventory per tab (S→C 0xc0), then 0x8c lists them with their names.
        var ids = tabs.Select((_, i) => NpcShops.FirstTabId + (uint)i).ToList();
        lock (player.Npcs) player.Shop = new OpenShop(npc, tabs.SelectMany((t, i) => t.Items.Select(item => (Tab: ids[i], Item: item))).ToDictionary(p => p.Item.Id)) { StockKey = window.Key, Finite = window.Finite };
        for (int i = 0; i < tabs.Count; i++)
            await Send(player, InstanceInventories.AddInventory(ids[i], nativeTabs ? NpcShops.NativeTab : NpcShops.Tab,
                NpcShops.Manager, tabs[i].Items, nativeTabs ? (byte)0x60 : null),
                $"0x00c0 shop tab {ids[i]} \"{tabs[i].Name}\" (manager {NpcShops.Manager}) of {npc.Npc.Name}: {tabs[i].Items.Count} items");
        player.InNpcTalk = false;
        // 0x8c's u16 is the window kind (0x140740840, +0x5f0: 0 buy, 1-4 haggle/deal/exchange/expedition, 9 Azmeri, 10 black
        // market, 11 unset), its u8 the async trade mode ("Merchant" title, +78 height, 0x14073ef50). The NPC's DialogueStyle
        // picks the window, not the u8 (0x140dca580). A tab's compact value is its colour (0x14073ef50: ARGB, 0 the default).
        await Send(player, InstanceNpcs.Dialogue(npcObject, windowKind, false, ids,
            nativeTabs ? [] : [.. tabs.Select(t => new InstanceNpcs.Choice(unchecked((int)t.TabColour), t.Name))]),
            $"0x008c open the buy window of {npc.Npc.Name} with tabs {string.Join(", ", tabs.Select(t => t.Name))}");
    }

    static VendorStock.Window BuildWindow(Player player, SpawnedNpc npc, List<string> notes, bool refresh = false)
    {
        var catalog = ItemsFor(player);
        var league = LeagueFor(player);
        var gems = GemVendorStock.Sells(npc.Npc.Id)
            ? GemVendorStock.For(catalog,Npcs,SkillsFor(player),league.HardMode,league.Rules.SupportGemRewards) : null;
        var stock = gems is not null
            ? gems.Stock(npc.Npc.Id,player.Character.ClassRow,VendorQuestFlags(player),league.HardMode,player.Pools.Level,league.Rules) : [];
        if (league.Rules.FullGemStock && npc.Npc.Id == "Metadata/NPC/Act1/Nessa")
            return new(NpcShops.Tabs(npc.Npc.Id,catalog,ItemsOf(player).NewItemId,notes,gemStock:stock,
                gemPrice:gems!.Price,fullGemStock:true,hardMode:league.HardMode,rules:league.Rules),"",[]);
        var window = VendorStock.Open(CharacterName(player),player.Character.League ?? "Standard",npc.Npc.Id,player.Pools.Level,
            catalog,RollsFor(player),SkillsFor(player),stock,ItemsOf(player).NewItemId,league.Rules,league.HardMode,notes,refresh,account:PersonalStashOf(player));
        if (!league.Rules.DevelopmentShopTabs || npc.Npc.Id != "Metadata/NPC/Act1/Nessa") return window;
        var development = NpcShops.Tabs(npc.Npc.Id,catalog,ItemsOf(player).NewItemId,notes,gemPrice:gems is null ? null : gems.Price,
            hardMode:league.HardMode,rules:league.Rules,developmentOnly:true);
        return window with { Tabs = [.. window.Tabs, .. development], Labelled = true };
    }

    internal static async Task RefreshKirac(Player player)
    {
        if (player.Shop is not { } shop || VendorCatalog.Default.Find(shop.Npc.Npc.Id)?.Kind != "Kirac")
        { await player.Commands!.Reply("Open Kirac's shop first."); return; }
        var window = Saves.Atomically(() =>
        {
            if (PersonalStashOf(player).TrySpendGold(KiracProgress.For(PersonalStashOf(player)).ShopRerollCost) is null) return null;
            return BuildWindow(player,shop.Npc,[],refresh:true);
        });
        if (window is null) { await player.Commands!.Reply($"Kirac's refresh costs {KiracProgress.For(PersonalStashOf(player)).ShopRerollCost} Gold."); return; }
        
        await Send(player,InstanceEntities.Gold(PersonalStashOf(player).Gold),"0x02aa Kirac refresh");
        uint npcObject = player.Npcs.FirstOrDefault(p => p.Value == shop.Npc).Key;
        await OpenNpcShop(player,npcObject,shop.Npc,window);
    }

}
