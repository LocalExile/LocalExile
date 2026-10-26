using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

partial class Program
{
    internal static readonly Lazy<VendorRecipes> VendorSales = new(() => new(Items));

    // Creates one NPC (0x2b9, its NPCs.dat Id is an .ot type) and puts it in the wake zone; kept for clicks and /npcs.
    static async Task SpawnNpc(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, NpcCatalog.Npc npc,
        int x, int y, float rotation)
    {
        uint id = conn.NextObjectId++;
        var obj = new NpcObject(x, y, npc.Components!, rotation, npc.StateMachine, npc.TransitionState, npc.Model, npc.Height);
        uint type = InstanceEntities.ObjectTypeHash(npc.Id);
        await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, type, obj.Blob()),
            $"0x02b9 create npc 0x{id:x} {npc} type=0x{type:x8} at ({x}, {y}) rotation {rotation:0.##}, components {string.Join(", ", npc.Components!)}");
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), $"0x01a9 npc 0x{id:x} enters wake zone");
        lock (conn.Npcs) conn.Npcs[id] = new SpawnedNpc(npc, x, y);
        conn.SetBlocker(id, x, y, ClientState.NpcBlockerSize);
        if (HideoutCatalog.ByArea(conn.Layout.Hash) is not null)
            conn.HideoutEditor.Register(id, "npc:" + npc.Id, x, y, rotation, ClientState.NpcBlockerSize);
    }

    // The town's NPCs (NpcCatalog.In) at their cells.
    static async Task SpawnTownNpcs(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var notes = new List<string>();
        var placements = HideoutFurniture(conn)?.Npcs ?? NpcCatalog.In(conn.Layout.Hash, conn.Seed, notes);
        foreach (var note in notes) Console.WriteLine($"[gateway] {remote} npcs: {note}");
        if (placements.Count == 0 || Environment.GetEnvironmentVariable("Game_NPCS") == "0") return;
        if (Npcs.Value.Error is { } error) { Console.WriteLine($"[gateway] {remote} npcs: {error}"); return; }
        foreach (var p in placements)
        {
            if (Npcs.Value.Place(p) is not { Supported: true } npc) { Console.WriteLine($"[gateway] {remote} npc {p.Id}: unknown or unsupported components"); continue; }
            if (!p.FromRoom && conn.Layout.Walkable is { } w && !w(p.X, p.Y)) Console.WriteLine($"[gateway] {remote} npc {npc.Name}: ({p.X}, {p.Y}) is not walkable");
            await SpawnNpc(remote, stream, ciphers, conn, npc, p.X, p.Y, p.Rotation);
        }
    }

    // A click on an NPC (C→S 0x24 on its object): the client walks to it first; follow it and answer once the player is there.
    static async Task TalkToNpc(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        InstanceTransfer.UseSkillOnObject use, SpawnedNpc npc, double requestedAt)
    {
        if (!TryBeginObjectWalk(conn, requestedAt, npc.X, npc.Y, ClientState.NpcBlockerSize, out var objectWalk)) return;
        var (action, now, here, stop, arrive) = objectWalk;
        Console.WriteLine($"[gateway] {remote} <= 0x0024 npc {npc.Npc} ({use}) from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
        if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        if (!PlayerActionCurrent(conn, action)) return;
        uint id;
        lock (conn.Npcs) id = conn.Npcs.FirstOrDefault(n => ReferenceEquals(n.Value, npc)).Key;
        conn.TalkingTo = id;
        conn.InNpcTalk = false;
        conn.NpcFinishFlag = null;
        conn.NpcRewardOffer = null;
        if (npc.Npc.Id == TutorialObjects.Exile)
        {
            await TalkToWoundedExile(remote, stream, ciphers, conn, id, npc);
            return;
        }
        await Send(remote, stream, ciphers, InstanceNpcs.Talk(id), $"0x0084 talk to npc 0x{id:x} {npc.Npc.Name}",
            still: () => PlayerActionCurrent(conn, action));
    }

    // C→S 0x85: the dialogue window was closed or, while a talk plays, Continue was pressed. The client stays in its dialogue
    // state (and the player cannot move) until the server answers: back to the option list (0x84 0xff) after a talk, else
    // 0x84 with object 0, which ends the dialogue.
    static async Task CloseNpcDialogue(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (conn.Sale is not null) await CloseSale(remote, stream, ciphers, conn, accepted: false);
        uint id = conn.TalkingTo;
        if (id != 0 && conn.InNpcTalk && !IsWoundedExile())
        {
            conn.InNpcTalk = false;
            if (conn.NpcFinishFlag is { } finish)
            {
                conn.NpcFinishFlag = null;
                await SetQuestFlags(remote, stream, ciphers, conn, [finish]);
            }
            await Send(remote, stream, ciphers, InstanceNpcs.Talk(id), $"0x0084 npc 0x{id:x}: back to the options (0x85 after a talk)");
            return;
        }
        conn.TalkingTo = 0;
        conn.InNpcTalk = false;
        conn.NpcFinishFlag = null;
        conn.NpcRewardOffer = null;
        await Send(remote, stream, ciphers, InstanceNpcs.Talk(0), "0x0084 object 0: npc dialogue closed (0x85)");

        bool IsWoundedExile()
        {
            lock (conn.Npcs) return conn.Npcs.GetValueOrDefault(id)?.Npc.Id == TutorialObjects.Exile;
        }
    }

    // C→S 0x82: an option picked in the NPC dialogue window (an NPCTalk row of the NPC talked to).
    static async Task ChooseNpcOption(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, byte index)
    {
        uint id = conn.TalkingTo;
        SpawnedNpc? npc;
        lock (conn.Npcs) npc = conn.Npcs.GetValueOrDefault(id);
        if (npc is null) { Console.WriteLine($"[gateway] {remote} <= 0x0082 npc option {index}: no NPC dialogue open"); return; }
        if (npc.Npc.Id == TutorialObjects.Exile) return;
        var talk = Npcs.Value.TalkOf(npc.Npc, index);
        Console.WriteLine($"[gateway] {remote} <= 0x0082 npc {npc.Npc.Name} option {index}: " +
            (talk is null ? "no NPCTalk row" : $"NPCTalk row {talk.Row} \"{talk.Option}\" ({talk.Category}), text audio [{string.Join(", ", talk.TextAudio)}]"));
        if (talk is null) return;
        if (!talk.Available(Quests.Names(PlayerQuestFlags(conn)))) return;
        if (!await HandInQuestItems(remote, stream, ciphers, conn, talk)) return;   // a1q5: Nessa takes the Medicine Chest
        if (talk.StartFlag is { } start) await SetQuestFlags(remote, stream, ciphers, conn, [start]);
        conn.NpcFinishFlag = talk.FinishFlag;
        switch (talk.Category)
        {
            case "SelectHideout":
                conn.InNpcTalk = false;
                conn.TalkingTo = 0;
                await Send(remote, stream, ciphers, InstanceNpcs.Talk(0), "0x0084 close NPC dialogue for hideout selection");
                await Send(remote, stream, ciphers, InstanceWaypoints.OpenUi(InstanceNpcs.HideoutSelectionUi,
                    talk.OpensGuildHideouts ? 1u : 0u), $"0x007b open hideout selection ({(talk.OpensGuildHideouts ? "guild" : "character")})");
                break;
            case "QuestReward" when talk.RewardOffer is { } offer:
                if (!QuestRewardCatalog.Implemented(offer)) break;
                var rewardCatalog = QuestRewardCatalog.Load();
                var rewardItems = rewardCatalog.Choices(offer, conn.ClassRow)
                    .Select(c => (Choice: c, Item: QuestRewardCatalog.Create(c, Items))).Where(c => c.Item is not null).ToArray();
                if (rewardCatalog.Find(offer) is not { } rewardOffer || conn.QuestFlags.Contains(rewardOffer.TakenFlag)
                    || rewardItems.Length == 0) break;
                conn.InNpcTalk = false;
                conn.NpcRewardOffer = offer;
                conn.NpcRewardItems = rewardItems.ToDictionary(c => c.Choice.Id, c => c.Item!);
                await Send(remote, stream, ciphers, InstanceInventories.AddInventory(0x600, NpcShops.Tab, NpcShops.Manager,
                    [.. rewardItems.Select((c, i) => new InventoryItem(c.Choice.Id, (byte)(i % 12), (byte)(i / 12), c.Item!))]),
                    $"0x00c0 quest reward inventory: offer {offer}, {rewardItems.Length} choices");
                await Send(remote, stream, ciphers, InstanceNpcs.OpenQuestReward(0x600, checked((ushort)offer)),
                    $"0x0087 reward inventory 0x600: quest reward offer {offer}");
                break;
            case "Gossip" or "Quests" or "QuestReward" when talk.TextAudio.Count > 0:
                // 0x84 with the row's index: the client opens the dialogue window on that NPCTalk row (its text and audio).
                conn.InNpcTalk = true;
                await Send(remote, stream, ciphers, InstanceNpcs.Talk(id, (byte)talk.Index),
                    $"0x0084 npc 0x{id:x} {npc.Npc.Name} talks: NPCTalk row {talk.Row} \"{talk.Option}\" (index {talk.Index})");
                break;
            case "BuyItems":
                await OpenNpcShop(remote, stream, ciphers, conn, id, npc);
                break;
            case "SellItems":
                await OpenNpcSale(remote, stream, ciphers, conn, npc);
                break;
            // Bestel's Respecialisation (script: SendOpenScreen( CharacterPassiveTreeScreen, 1 )): the character's passive tree,
            // S→C 0x7b kind 0x5f with 1. Its refunds come back as C→S 0x36 (per node) and 0x37 (Refund All), paid in gold.
            case "RespecPassivePointsForGold":
                conn.InNpcTalk = false;
                await Send(remote, stream, ciphers, InstanceWaypoints.OpenUi(InstanceNpcs.PassiveTreeUi, 1), "0x007b open the passive tree (respec)");
                break;
            // Nessa's Identify Items (script: triggerer.IdentifyItems(), then the IdentifyAll sound): every unidentified item
            // in the main inventory, free. The sound effect is not sent (its message is not mapped).
            case "IdentifyItems":
                await IdentifyInventory(remote, stream, ciphers, conn, npc);
                break;
            // Niko's View Subterranean Chart: SendOpenScreen( DelveMapScreen, 1 ), S→C 0x7b kind 0x1c with 1.
            case "Quests" when talk.Script.Contains("DelveMapScreen"):
                conn.InNpcTalk = false;
                await Send(remote, stream, ciphers, InstanceWaypoints.OpenUi(InstanceNpcs.DelveMapUi, 1), "0x007b open the Delve chart");
                break;
            // Niko's Visit Mine Encampment: BeginDelve(), to the Azurite Mine Encampment (WorldAreas Delve_Hub), as /goto does.
            // The mine's own mechanics (darkness, the cart, depth) are not implemented.
            case "Quests" when talk.Script.Contains("BeginDelve"):
                conn.InNpcTalk = false;
                if (AreaTransitionCatalog.HashOf("Delve_Hub") is { } delve)
                    await ChangeArea(remote, stream, ciphers, conn, new PendingTransfer(delve, 0, false, AtSpawn: true), $"{npc.Npc.Name}: {talk.Option}");
                else
                    Console.WriteLine($"[gateway] {remote} {talk.Option}: Delve_Hub is not in data/area-transitions.json");
                break;
            default:
                Console.WriteLine($"[gateway] {remote} npc option category {talk.Category} is not implemented yet");
                break;
        }
    }

    // An NPC's Identify Items: the main inventory's unidentified items become identified (their explicit mods and rare names
    // are sent from now on). Removing and adding the same ids in one 0xbf replaces them on the client (see UseItemOn).
    static async Task IdentifyInventory(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, SpawnedNpc npc)
    {
        const byte row = PlayerInventories.MainInventory;
        var items = ItemsOf(conn);
        var identified = items.IdentifyAll(row);
        if (identified.Count == 0)
        {
            Console.WriteLine($"[gateway] {remote} npc {npc.Npc.Name} identify items: nothing unidentified");
            return;
        }
        uint inventory = PlayerInventories.InventoryId(row);
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(inventory, items.NextRevision(row), [.. identified.Select(i => i.Id)], identified),
            $"0x00bf inventory {inventory}: {npc.Npc.Name} identified {identified.Count} item(s)");
        foreach (var i in identified)
            Console.WriteLine($"[gateway] {remote} npc {npc.Npc.Name} identified item {i.Id}: {string.Join("; ", i.Item.Mods.Select(Items.Mods.Describe))}");
        // ClientStrings IdentifyItemsChatMessage / IdentifyItemsChatMessageSingle, as a chat line (S→C 0x17).
        string text = identified.Count == 1 ? "1 Item identified" : $"{identified.Count} Items identified";
        await Send(remote, stream, ciphers, InstanceChat.ChatLine(text), $"0x0017 chat line \"{text}\"");
        // The client decides itself whether the option is usable (NPCTalkCategory IdentifyItems, 0x141e754b0: gray with
        // "No items in your inventory can be identified" when nothing is unidentified), but only when the options are
        // listed: list them again.
        await Send(remote, stream, ciphers, InstanceNpcs.Talk(conn.TalkingTo), $"0x0084 npc 0x{conn.TalkingTo:x}: options again");
    }

    // C→S 0x88: an item bought from the open shop tab. The client picked the free cell; the item leaves the tab (0xbf on
    // manager 1) and enters the inventory (0xbf), keeping its id. Free for now: prices are not charged yet.
    static async Task BuyFromShop(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, InstanceNpcs.Buy buy)
    {
        if (conn.NpcRewardOffer is not null)
        {
            if (buy.InventoryId == PlayerInventories.InventoryId(PlayerInventories.MainInventory))
                await ClaimNpcReward(remote, stream, ciphers, conn, buy.ItemId);
            return;
        }
        OpenShop? shop;
        uint tab = 0;
        InventoryItem? item = null;
        lock (conn.Npcs)
        {
            shop = conn.Shop;
            if (shop is not null && shop.Items.Remove(buy.ItemId, out var found)) (tab, item) = found;
        }
        if (shop is null || item is null || PlayerInventories.Row(buy.InventoryId) is not byte row)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0088 buy {buy}: {(shop is null ? "no shop open" : item is null ? "not in the shop" : "unknown inventory")}");
            if (shop is not null && item is not null) lock (conn.Npcs) shop.Items[item.Id] = (tab, item);
            return;
        }
        var items = ItemsOf(conn);
        var stash = StashOf(conn);
        // The price: the item's price list (its first entry), paid from the main inventory and then the stash, together with
        // the item's arrival (one transaction). Not enough: nothing happens (the client shows the price in red).
        (uint Currency, int Amount)? price = item.Item.Price is [var (currency, amount), ..] ? (currency, (int)amount) : null;
        if (ItemMoves.Buy(items, stash, row, item with { X = buy.X, Y = buy.Y, Item = item.Item with { Price = [] } }, price) is not var (paid, bought))
        {
            lock (conn.Npcs) shop.Items[item.Id] = (tab, item);
            Console.WriteLine($"[gateway] {remote} <= 0x0088 buy item {item.Id}: costs {price!.Value.Amount} × {Items.ByHash(price.Value.Currency)?.Name ?? $"0x{price.Value.Currency:x8}"}, not enough in the inventory and the stash");
            return;
        }
        if (paid?.Inventory is { } fromInventory)
            await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                items.NextRevision(PlayerInventories.MainInventory), [.. fromInventory.Removed, .. fromInventory.Changed.Select(c => c.Id)], fromInventory.Changed),
                $"0x00bf inventory 1: paid (stacks emptied [{string.Join(", ", fromInventory.Removed)}], changed [{string.Join(", ", fromInventory.Changed.Select(c => $"{c.Id}={c.Item.StackSize}"))}])");
        await SendStashPayment(remote, stream, ciphers, conn, stash, paid?.Stash);
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(tab, shop.NextRevision(tab), [item.Id], [], NpcShops.Manager),
            $"0x00bf shop tab {tab}: item {item.Id} sold");
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(buy.InventoryId, items.NextRevision(row), [], [bought]),
            $"0x00bf inventory {buy.InventoryId}: bought item {item.Id} from {shop.Npc.Npc.Name} at ({buy.X}, {buy.Y}) ({buy})");
    }

    static async Task ClaimNpcReward(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint itemId)
    {
        if (conn.NpcRewardOffer is not int offer || !conn.NpcRewardItems.TryGetValue(itemId, out var reward)) return;
        SpawnedNpc? npc;
        lock (conn.Npcs) npc = conn.Npcs.GetValueOrDefault(conn.TalkingTo);
        if (npc is null || !Npcs.Value.HasAvailableReward(npc.Npc, offer, Quests.Names(PlayerQuestFlags(conn)))) return;
        var catalog = QuestRewardCatalog.Load();
        if (catalog.Find(offer) is not { } data) return;
        var items = ItemsOf(conn);
        var claimed = QuestRewardCatalog.Claim(CharacterName(conn), data.TakenFlag, items, reward, ShapeOf);
        if (claimed is null) return;
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
            items.NextRevision(PlayerInventories.MainInventory), claimed.RemovedIds, claimed.AddedItems), "0x00bf claimed quest reward");
        // Empty the offer inventory so the native reward window finishes its pending purchase and closes.
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(0x600, 1, [.. conn.NpcRewardItems.Keys], [], NpcShops.Manager),
            $"0x00bf quest reward offer {offer}: all choices removed");
        conn.NpcRewardOffer = null; conn.NpcRewardItems.Clear();
        await SetQuestFlags(remote, stream, ciphers, conn, [data.TakenFlag]);
        conn.InNpcTalk = false; conn.NpcFinishFlag = null; conn.TalkingTo = 0;
        await Send(remote, stream, ciphers, InstanceNpcs.Talk(0), "0x0084 quest reward claimed: dialogue ended");
    }

    // What a price took from the stash (ItemMoves.Price.Stash), to the client: one 0xbf per tab this connection has the
    // contents of (the others come whole when it asks, 0xc7).
    static async Task SendStashPayment(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, AccountStash stash, AccountStash.Payment? paid)
    {
        foreach (var (tab, (removed, changed)) in paid?.Tabs ?? new Dictionary<int, (List<uint>, List<InventoryItem>)>())
            if (TabsSentOf(conn, stash).Contains(tab))
                await Send(remote, stream, ciphers, InstanceItems.InventoryChange(stash.InventoryOf(tab), stash.NextRevision(tab), [.. removed, .. changed.Select(c => c.Id)], changed, stash.IsGuild ? InstanceStash.GuildManager : (byte)0),
                    $"0x00bf stash tab {tab}: paid (stacks emptied [{string.Join(", ", removed)}], changed [{string.Join(", ", changed.Select(c => $"{c.Id}={c.Item.StackSize}"))}])");
    }

    // C→S 0x8a: Ctrl + click on an item in the open shop tab: bought into the main inventory (a stack onto stacks of its type,
    // then the first free cell), the price paid from the inventory and then the stash in the same transaction
    // (ItemMoves.BuyToInventory). Without room or enough to pay, nothing happens.
    static async Task CtrlBuyFromShop(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, InstanceNpcs.CtrlBuy buy)
    {
        if (conn.NpcRewardOffer is not null)
        {
            await ClaimNpcReward(remote, stream, ciphers, conn, buy.ItemId);
            return;
        }
        OpenShop? shop;
        uint tab = 0;
        InventoryItem? item = null;
        lock (conn.Npcs)
        {
            shop = conn.Shop;
            if (shop is not null && shop.Items.Remove(buy.ItemId, out var found)) (tab, item) = found;
        }
        if (shop is null || item is null)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x008a Ctrl + click buy {buy}: {(shop is null ? "no shop open" : "not in the shop")}");
            return;
        }
        var items = ItemsOf(conn);
        var stash = StashOf(conn);
        (uint Currency, int Amount)? price = item.Item.Price is [var (currency, amount), ..] ? (currency, (int)amount) : null;
        if (ItemMoves.BuyToInventory(items, stash, item.Item with { Price = [] }, price, ShapeOf, out string why) is not var (paid, into))
        {
            lock (conn.Npcs) shop.Items[item.Id] = (tab, item);
            Console.WriteLine($"[gateway] {remote} <= 0x008a Ctrl + click buy item {item.Id}: refused ({why})");
            return;
        }
        // The payment and the item land in the same inventory: one 0xbf with every stack that shrank, emptied, grew or arrived.
        var fromInventory = paid?.Inventory;
        var gone = new List<uint>([.. fromInventory?.Removed ?? [], .. fromInventory?.Changed.Select(c => c.Id) ?? [], .. into.Changed.Select(c => c.Id)]);
        var now = new Dictionary<uint, InventoryItem>();
        foreach (var i in (IEnumerable<InventoryItem>)[.. fromInventory?.Changed ?? [], .. into.Changed, .. into.Added]) now[i.Id] = i;
        foreach (var removed in fromInventory?.Removed ?? []) now.Remove(removed);
        await SendStashPayment(remote, stream, ciphers, conn, stash, paid?.Stash);
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(tab, shop.NextRevision(tab), [item.Id], [], NpcShops.Manager),
            $"0x00bf shop tab {tab}: item {item.Id} sold");
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
            items.NextRevision(PlayerInventories.MainInventory), [.. gone.Distinct()], [.. now.Values]),
            $"0x00bf inventory 1: bought item {item.Id} from {shop.Npc.Npc.Name} with Ctrl + click" + (price is var (_, a) ? $" for {a}" : ""));
    }

    // An item goes into the sell offer (C→S 0xb2, or 0x2a on the offer inventory) from the inventory it is in (the cursor, or
    // the main inventory on a Ctrl-click); the NPC answers with one Scroll Fragment per offered item in its own offer. As in
    // the saved item and its inventory cell remain reserved until the sale ends (PlayerInventories.Offer).
    // The client sees the item only in its offer grid.
    static async Task OfferForSale(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, NpcSale sale,
        uint fromInventory, uint itemId, byte x, byte y)
    {
        var items = ItemsOf(conn);
        if (PlayerInventories.Row(fromInventory) is not byte row || items.Offer(row, itemId, x, y, ShapeOf) is not var (waits, offered))
        {
            Console.WriteLine($"[gateway] {remote} sell offer: item {itemId} is not in inventory {fromInventory} (or no cell to wait in)");
            return;
        }
        // Keep the durable item in its reserved server cell, but show it only in the offer grid.
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(fromInventory, items.NextRevision(row), [offered.Id], []),
            $"0x00bf inventory {fromInventory}: hide offered item {offered.Id}");
        var inOffer = offered with { X = x, Y = y };
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(sale.Mine, ++sale.MineRevision, [], [inOffer], NpcShops.Manager),
            $"0x00bf sell offer: item {inOffer.Id} at ({x}, {y})");
        await SendSaleReward(remote, stream, ciphers, conn, sale);
    }

    // Taking an offered item back restores its original inventory view and removes its offer entry.
    static async Task TakeFromSale(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, NpcSale sale, uint itemId)
    {
        var items = ItemsOf(conn);
        if (items.TakeBack(itemId) is not var (row, back))
        {
            Console.WriteLine($"[gateway] {remote} sell offer item {itemId}: not offered");
            return;
        }
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(sale.Mine, ++sale.MineRevision, [itemId], [], NpcShops.Manager),
            $"0x00bf sell offer: remove item {itemId} (back in inventory {PlayerInventories.InventoryId(row)} at ({back.X}, {back.Y}))");
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), items.NextRevision(row), [], [back]),
            $"0x00bf return item {back.Id} to inventory {PlayerInventories.InventoryId(row)}");
        await SendSaleReward(remote, stream, ciphers, conn, sale);
    }

    // Quote once on each offer change. The same immutable outputs are displayed and paid at acceptance.
    static async Task SendSaleReward(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, NpcSale sale)
    {
        var items = ItemsOf(conn);
        var offered = items.Offered().OrderBy(i => i.Id).ToList();
        sale.QuoteError = null;
        VendorRecipes.Quote quote;
        try { quote = VendorSales.Value.Evaluate(offered); }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            sale.QuoteError = ex.Message;
            quote = new([], []);
            Console.WriteLine($"[gateway] {remote} vendor quote refused: {ex.Message}");
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
        if (sale.QuoteError is not null) { reward.Clear(); await conn.Commands!.Reply(sale.QuoteError); }
        List<uint> before;
        lock (conn.Npcs)
        {
            before = [.. sale.Reward.Select(r => r.Id)];
            sale.Reward = reward;
            sale.QuotedItems = offered;
            sale.QuotedRewards = quote.Rewards;
        }
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(sale.Theirs, ++sale.TheirsRevision, before, reward, NpcShops.Manager),
            $"0x00bf {sale.Npc.Npc.Name}'s offer: {reward.Count} reward stack(s); {string.Join(", ", quote.Recipes)}");
    }

    // C→S 0xb1: Cancel (u8 0). The offered items go back to the inventory and S→C 0xb9 closes the window.
    static async Task AnswerSale(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, (uint Other, byte Answer) a)
    {
        Console.WriteLine($"[gateway] {remote} <= 0x00b1 trade answer {a.Answer} (trade {a.Other})");
        await CloseSale(remote, stream, ciphers, conn, accepted: a.Answer != 0);
    }

    // An item Shift-clicked in an inventory while the sell window is open goes into a free cell of the offer.
    static async Task OfferFromInventory(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, NpcSale sale, uint inventory, uint itemId)
    {
        var items = ItemsOf(conn);
        byte? row = PlayerInventories.Row(inventory);
        var it = row is byte r ? items.Items(r).FirstOrDefault(i => i.Id == itemId) : null;
        if (it is null || ShapeOf(it.Item) is not { } shape) { Console.WriteLine($"[gateway] {remote} sell offer: item {itemId} not in inventory {inventory}"); return; }
        List<(InventoryItem Item, PlayerInventories.Shape? Shape)> offered = [.. items.Offered().Select(o => (o, ShapeOf(o.Item)))];
        (byte X, byte Y)? cell = null;
        for (int x = 0; x + shape.Width <= NpcShops.Offer.Width && cell is null; x++)
            for (int y = 0; y + shape.Height <= NpcShops.Offer.Height && cell is null; y++)
                if (!offered.Any(o => o.Shape is { } s && o.Item.X < x + shape.Width && x < o.Item.X + s.Width && o.Item.Y < y + shape.Height && y < o.Item.Y + s.Height))
                    cell = ((byte)x, (byte)y);
        if (cell is not { } c) { Console.WriteLine($"[gateway] {remote} sell offer: no room for item {itemId}"); return; }
        await OfferForSale(remote, stream, ciphers, conn, sale, inventory, itemId, c.X, c.Y);
    }

    // Ends the sale. Accepted: the offered items are gone and the NPC's offer goes into the inventory, in one save; when the
    // offer doesn't fit (the offered items' cells free up first, so it nearly always does), nothing changes and the window
    // stays open. Cancelled: every offered item is in its cell again (it kept it). Then S→C 0xb9 closes the window.
    static async Task CloseSale(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, bool accepted)
    {
        NpcSale? sale;
        lock (conn.Npcs) sale = conn.Sale;
        if (sale is null) Console.WriteLine($"[gateway] {remote} trade: no sale open");
        accepted &= sale is not null;
        var items = ItemsOf(conn);
        if (accepted)
        {
            var offered = items.Offered().OrderBy(i => i.Id).ToList();
            if (offered.Count == 0) { await CloseSale(remote, stream, ciphers, conn, accepted: false); return; }
            bool sameQuote = sale!.QuotedItems.Select(i => (i.Id, i.Item)).SequenceEqual(offered.Select(i => (i.Id, i.Item)));
            PlayerInventories.Stacking? added = null;
            string? refused = sale.QuoteError ?? (!sameQuote ? "The offer has changed. Review the vendor's new offer before accepting." : null);
            if (refused is null && (added = items.Sell(sale.QuotedRewards, ShapeOf)) is null)
                refused = "Not enough room in your inventory for the complete vendor reward.";
            if (refused is not null)
            {
                Console.WriteLine($"[gateway] {remote} sale refused: {refused}");
                await conn.Commands!.Reply(refused);
                return;
            }
            Console.WriteLine($"[gateway] {remote} sale accepted: {offered.Count} item(s), {sale.QuotedRewards.Count} reward stack(s)");
            // Offered items were already removed from the client's inventory view.
            if (added is { Any: true })
                await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                    items.NextRevision(PlayerInventories.MainInventory),
                    added.RemovedIds, added.AddedItems),
                    $"0x00bf inventory 1: sold items out, {added.Changed.Count} stack(s) grown, {added.Added.Count} new (the NPC's offer)");
        }
        else if (sale is not null)
        {
            // Restore the hidden inventory view before closing the offer.
            var returned = items.CancelOffers();
            foreach (var group in returned.GroupBy(i => i.Row))
                await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(group.Key), items.NextRevision(group.Key),
                    [], [.. group.Select(i => i.Item)]), $"0x00bf sale cancelled: restore {group.Count()} item(s)");
        }
        lock (conn.Npcs) if (conn.Sale == sale) conn.Sale = null;
        await Send(remote, stream, ciphers, InstanceNpcs.TradeClose(0, accepted), $"0x00b9 sell window closed ({(accepted ? "accepted" : "cancelled")})");
        // The client is still talking to the NPC (session +0xd7c0 = 2, +0xd7c4 = the NPC, live 2026-10-19) and the player cannot
        // move until 0x84 with object 0 ends the talk. Cancel sends 0x85 for it, Accept sends nothing, so end it here.
        conn.TalkingTo = 0;
        conn.InNpcTalk = false;
        await Send(remote, stream, ciphers, InstanceNpcs.Talk(0), "0x0084 object 0: npc dialogue closed (sale over)");
    }

    // Sell Items: two empty offer inventories (manager 1: the player's offer, the NPC's), then S→C 0xb8 with no other party
    // (an NPC sale) opens the sell window. Being tested.
    static async Task OpenNpcSale(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, SpawnedNpc npc)
    {
        if (conn.Sale is not null) await CloseSale(remote, stream, ciphers, conn, accepted: false);
        uint mine = NpcShops.FirstTabId + 0x10, theirs = mine + 1;
        ItemsOf(conn).CancelOffers();   // none should be left; a new sale must not count them
        lock (conn.Npcs) conn.Sale = new NpcSale(npc, mine, theirs);
        await Send(remote, stream, ciphers, InstanceInventories.AddInventory(mine, NpcShops.Offer, NpcShops.Manager), $"0x00c0 sell offer {mine} (manager {NpcShops.Manager})");
        await Send(remote, stream, ciphers, InstanceInventories.AddInventory(theirs, NpcShops.Offer, NpcShops.Manager), $"0x00c0 {npc.Npc.Name}'s offer {theirs} (manager {NpcShops.Manager})");
        conn.InNpcTalk = false;
        await Send(remote, stream, ciphers, InstanceNpcs.TradeOpen(0, mine, theirs, InstanceNpcs.TradeActive | InstanceNpcs.TradeNoMouseOver, npc.Npc.Name, npc.Npc.Row),
            $"0x00b8 open the sell window with {npc.Npc.Name} (offers {mine}, {theirs})");
    }

    // Purchase Items: the NPC's stock as one shop tab (S→C 0xc0, inventory manager 1), then S→C 0x8c opens the buy window on it.
    static async Task OpenNpcShop(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint npcObject, SpawnedNpc npc)
    {
        if (conn.Sale is not null) await CloseSale(remote, stream, ciphers, conn, accepted: false);
        await RefreshBenchBalance(remote, stream, ciphers, conn, clear: true);
        conn.BenchOpen = false;
        if (Items.Error is { } error) { Console.WriteLine($"[gateway] {remote} shop: {error}"); return; }
        var notes = new List<string>();
        var items = ItemsOf(conn);
        var tabs = NpcShops.Tabs(npc.Npc.Id, Items, items.NewItemId, notes);
        int shopRow = Npcs.Value.TalkSource(npc.Npc).ShopRow;
        foreach (var n in notes) Console.WriteLine($"[gateway] {remote} shop {npc.Npc.Name}: {n}");
        // One manager-1 inventory per tab (S→C 0xc0), then 0x8c lists them with their names.
        var ids = tabs.Select((_, i) => NpcShops.FirstTabId + (uint)i).ToList();
        lock (conn.Npcs) conn.Shop = new OpenShop(npc, tabs.SelectMany((t, i) => t.Items.Select(item => (Tab: ids[i], Item: item))).ToDictionary(p => p.Item.Id));
        for (int i = 0; i < tabs.Count; i++)
            await Send(remote, stream, ciphers, InstanceInventories.AddInventory(ids[i], NpcShops.Tab, NpcShops.Manager, tabs[i].Items),
                $"0x00c0 shop tab {ids[i]} \"{tabs[i].Name}\" (manager {NpcShops.Manager}) of {npc.Npc.Name}: {tabs[i].Items.Count} items");
        conn.InNpcTalk = false;
        // 0x8c's u8: 0 opens the window at UI +0x7b0, 1 the one at +0x7b8 (listener 0x140dcccf0); being tested live whether
        // +0x7b8 is the tabbed vendor window (with 8 tabs +0x7b0 drew its tab bar over the title and the grid below the frame).
        await Send(remote, stream, ciphers, InstanceNpcs.Dialogue(npcObject, (ushort)Math.Max(0, shopRow), tabs.Count > 1, ids,
            [.. tabs.Select((t, i) => new InstanceNpcs.Choice(i, t.Name))]),
            $"0x008c open the buy window of {npc.Npc.Name} (NPCShop row {shopRow}) with tabs {string.Join(", ", tabs.Select(t => t.Name))}");
    }
}
