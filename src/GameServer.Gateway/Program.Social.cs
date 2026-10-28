using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

/// <summary>
/// A trade between two players in one instance. Each client shows two offer inventories (manager 1): its own offer
/// (<see cref="Program.PlayerTradeMine"/>) and the other player's (<see cref="Program.PlayerTradeTheirs"/>). An offered
/// item stays reserved in its owner's inventory (PlayerInventories.Offer, hidden from that client like a sale) until the
/// trade completes or ends. Guarded by its own lock; the sends happen outside it.
/// </summary>
sealed class PlayerTrade(ClientState a, ClientState b)
{
    public readonly ClientState A = a, B = b;
    public readonly object Lock = new();
    public bool Over;
    /// <summary>Per client: the revision of its own offer inventory and of the other's, as last sent to it.</summary>
    public readonly Dictionary<ClientState, (uint Mine, uint Theirs)> Revisions = new() { [a] = (0, 0), [b] = (0, 0) };
    /// <summary>Per client: the revision of the other's offer it accepted, or null.</summary>
    public readonly Dictionary<ClientState, uint?> Accepted = new() { [a] = null, [b] = null };
    /// <summary>Per offering client: its item id → the id the other client sees it under (its own id space).</summary>
    public readonly Dictionary<ClientState, Dictionary<uint, uint>> Views = new() { [a] = [], [b] = [] };

    public ClientState Other(ClientState c) => ReferenceEquals(c, A) ? B : A;
}

partial class Program
{
    /// <summary>The two offer inventories of a player trade (manager 1): the NPC sale's (OpenNpcSale), as a player is never
    /// in both. The grid stayed empty with 0x7e0 / 0x7e1 although the item's 0xbf was the sale's (live 2026-10-28).</summary>
    internal const uint PlayerTradeMine = NpcShops.FirstTabId + 0x10, PlayerTradeTheirs = PlayerTradeMine + 1;

    /// <summary>0xb8 flags for a player (0x140dccdf0): bit 1 alone is the request notification for the other player, bit 0
    /// alone the requester's "Waiting for player to accept trade request..." box (with Cancel), both bits the trade window
    /// (and bit 0 the active trade for Ctrl + click, 0x140d62ec0); 0x10 lets Accept send 0xb6 without hovering each item.
    /// Neither bit shows nothing (live 2026-10-28).</summary>
    const byte PlayerTradeWaitingFlags = InstanceNpcs.TradeActive,
        PlayerTradeOpenFlags = InstanceNpcs.TradeActive | InstanceSocial.TradeRequestFlag | InstanceNpcs.TradeNoMouseOver;

    static Task SendTo(ClientState c, byte[] bytes, string? log) => c.Outbox is { } outbox ? outbox(bytes, log) : Task.CompletedTask;

    // Each side must know the other (0xa7) before 0xb8: the request is looked up by character name in that table.
    static async Task Introduce(ClientState to, ClientState about) =>
        await SendTo(to, InstanceSocial.KnownPlayer(about.Account ?? CharacterName(about), CharacterName(about), about.Pools.Level),
            $"0x00a7 known player {CharacterName(about)}");

    // 0xb8 builds the trade window's two offer grids at once, even for the request and the waiting notice: their
    // inventories must exist (inventory 0 crashed both clients at 0x1402d0a99 through the grid 0x1403174b4). A fresh
    // pair also empties what an earlier trade left in them.
    static async Task FreshOffers(ClientState c)
    {
        foreach (var inv in new[] { PlayerTradeMine, PlayerTradeTheirs })
            await SendTo(c, InstanceInventories.AddInventory(inv, NpcShops.Offer, NpcShops.Manager), $"0x00c0 trade offer {inv}");
    }

    // C→S 0xba: "Trade" on another player in the same instance. The requester sees the waiting notice (0xb8 without
    // flags) and the other player the request (0xb8 with TradeRequestFlag and the requester's name), answered by 0xb0.
    static async Task RequestTrade(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint target)
    {
        if (conn.Instance.Player(target) is not { Owner: { Outbox: not null } other } || other.Trade is not null || conn.Trade is not null)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x00ba trade request to 0x{target:x}: not available");
            return;
        }
        string me = CharacterName(conn), them = CharacterName(other);
        Console.WriteLine($"[gateway] {remote} <= 0x00ba trade request: {me} -> {them} (0x{target:x})");
        conn.TradeRequestTo = other.PeerId;
        await Introduce(conn, other);
        await Introduce(other, conn);
        await FreshOffers(conn);
        await FreshOffers(other);
        await SendTo(conn, InstanceNpcs.TradeOpen(other.PeerId, PlayerTradeMine, PlayerTradeTheirs, PlayerTradeWaitingFlags, them, -1), $"0x00b8 trade request to {them}: waiting");
        await SendTo(other, InstanceNpcs.TradeOpen(conn.PeerId, PlayerTradeMine, PlayerTradeTheirs, InstanceSocial.TradeRequestFlag, me, -1),
            $"0x00b8 trade request from {me}");
    }

    // C→S 0xb1 (u32 the requester, u8) from the notification: the request declined; the requester's box closes.
    static async Task DeclineTradeRequest(ClientState conn, uint requester)
    {
        Console.WriteLine($"[gateway] {CharacterName(conn)} declined the trade request from 0x{requester:x}");
        await SendTo(conn, InstanceNpcs.TradeClose(requester, false), "0x00b9 trade request declined");
        if (conn.Instance.Player(requester) is { Owner: { } other } && other.TradeRequestTo == conn.PeerId)
            await CancelTradeRequest(other);
    }

    // C→S 0xb1 on the requester's waiting box (Cancel): the box and the other player's notification close (0xb9).
    static async Task CancelTradeRequest(ClientState conn)
    {
        uint to = conn.TradeRequestTo;
        conn.TradeRequestTo = 0;
        Console.WriteLine($"[gateway] trade request from {CharacterName(conn)} to 0x{to:x} cancelled");
        await SendTo(conn, InstanceNpcs.TradeClose(to, false), "0x00b9 trade request cancelled");
        if (conn.Instance.Player(to) is { Owner: { } other })
            await SendTo(other, InstanceNpcs.TradeClose(conn.PeerId, false), $"0x00b9 trade request from {CharacterName(conn)} withdrawn");
    }

    // C→S 0xb0: the request accepted (u32 the requester's object id). Both get the open trade window.
    static async Task AcceptTradeRequest(string remote, ClientState conn, uint requester)
    {
        if (conn.Instance.Player(requester) is not { Owner: { } other } || other.TradeRequestTo != conn.PeerId
            || other.Trade is not null || conn.Trade is not null)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x00b0 trade accept from 0x{requester:x}: no pending request");
            return;
        }
        other.TradeRequestTo = 0;
        var trade = new PlayerTrade(other, conn);
        other.Trade = conn.Trade = trade;
        Console.WriteLine($"[gateway] {remote} <= 0x00b0 trade accepted: {CharacterName(other)} <-> {CharacterName(conn)}");
        foreach (var c in new[] { other, conn })
        {
            var them = trade.Other(c);
            // The window is the request's own panel, found again by the other party's id (0x140c38d70), still bound to the
            // offer inventories created with the request: new ones here left its grids pointing at the old ones and every
            // offered item invisible (live 2026-10-28).
            await SendTo(c, InstanceNpcs.TradeOpen(them.PeerId, PlayerTradeMine, PlayerTradeTheirs, PlayerTradeOpenFlags, CharacterName(them), -1),
                $"0x00b8 trade with {CharacterName(them)} open");
        }
    }

    // C→S 0xb2 / 0xb3 / 0xb5 while a player trade is open: an item into the offer (a drop at x, y, or Ctrl / Shift
    // click into the first free cell), or an offered item taken back.
    static async Task PlayerTradeOffer(string remote, ClientState conn, PlayerTrade trade, uint inventory, uint itemId, byte? x = null, byte? y = null)
    {
        var items = ItemsOf(conn);
        if (items.Offered().Any(i => i.Id == itemId)) { await PlayerTradeTakeBack(remote, conn, trade, itemId); return; }
        byte? row = PlayerInventories.Row(inventory);
        var it = row is byte r ? items.Items(r).FirstOrDefault(i => i.Id == itemId) : null;
        if (it is null || ShapeOf(it.Item) is not { } shape) { Console.WriteLine($"[gateway] {remote} trade offer: item {itemId} not in inventory {inventory}"); return; }
        (byte X, byte Y)? cell = x is byte cx && y is byte cy && cx != 0xff ? (cx, cy) : null;
        if (cell is null)
        {
            var offered = items.Offered().Select(o => (o, ShapeOf(o.Item))).ToList();
            for (int fx = 0; fx + shape.Width <= NpcShops.Offer.Width && cell is null; fx++)
                for (int fy = 0; fy + shape.Height <= NpcShops.Offer.Height && cell is null; fy++)
                    if (!offered.Any(o => o.Item2 is { } s && o.o.X < fx + shape.Width && fx < o.o.X + s.Width && o.o.Y < fy + shape.Height && fy < o.o.Y + s.Height))
                        cell = ((byte)fx, (byte)fy);
        }
        if (cell is not { } at || items.Offer(row!.Value, itemId, at.X, at.Y, ShapeOf) is not var (waits, waiting))
        {
            Console.WriteLine($"[gateway] {remote} trade offer: no room for item {itemId}");
            return;
        }
        var other = trade.Other(conn);
        var shown = waiting with { X = at.X, Y = at.Y };
        uint mine, theirs, view;
        lock (trade.Lock)
        {
            if (trade.Over) return;
            view = ItemsOf(other).NewItemId();
            trade.Views[conn][itemId] = view;
            mine = trade.Revisions[conn].Mine + 1;
            trade.Revisions[conn] = (mine, trade.Revisions[conn].Theirs);
            theirs = trade.Revisions[other].Theirs + 1;
            trade.Revisions[other] = (trade.Revisions[other].Mine, theirs);
            trade.Accepted[conn] = trade.Accepted[other] = null;
        }
        await SendTo(conn, InstanceItems.InventoryChange(PlayerInventories.InventoryId(waits), items.NextRevision(waits), [itemId], []),
            $"0x00bf inventory {PlayerInventories.InventoryId(waits)}: hide traded item {itemId}");
        await SendTo(conn, InstanceItems.InventoryChange(PlayerTradeMine, mine, [], [shown], NpcShops.Manager), $"0x00bf trade offer: item {itemId} at ({at.X}, {at.Y})");
        await SendTo(other, InstanceItems.InventoryChange(PlayerTradeTheirs, theirs, [], [shown with { Id = view }], NpcShops.Manager),
            $"0x00bf {CharacterName(conn)}'s offer: item {view} at ({at.X}, {at.Y})");
        await SendTradeState(trade);
    }

    static async Task PlayerTradeTakeBack(string remote, ClientState conn, PlayerTrade trade, uint itemId)
    {
        var items = ItemsOf(conn);
        if (items.TakeBack(itemId) is not var (row, back)) return;
        var other = trade.Other(conn);
        uint mine, theirs, view;
        lock (trade.Lock)
        {
            view = trade.Views[conn].Remove(itemId, out uint v) ? v : 0;
            mine = trade.Revisions[conn].Mine + 1;
            trade.Revisions[conn] = (mine, trade.Revisions[conn].Theirs);
            theirs = trade.Revisions[other].Theirs + 1;
            trade.Revisions[other] = (trade.Revisions[other].Mine, theirs);
            trade.Accepted[conn] = trade.Accepted[other] = null;
        }
        await SendTo(conn, InstanceItems.InventoryChange(PlayerTradeMine, mine, [itemId], [], NpcShops.Manager), $"0x00bf trade offer: remove item {itemId}");
        await SendTo(conn, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), items.NextRevision(row), [], [back]),
            $"0x00bf return item {itemId} to inventory {PlayerInventories.InventoryId(row)}");
        await SendTo(other, InstanceItems.InventoryChange(PlayerTradeTheirs, theirs, [view], [], NpcShops.Manager),
            $"0x00bf {CharacterName(conn)}'s offer: remove item {view}");
        await SendTradeState(trade);
    }

    // C→S 0xb6: Accept, with the revision of the other's offer this client saw. Both accepted on the current offers
    // completes the trade; any later change clears both acceptances.
    static async Task PlayerTradeAccept(string remote, ClientState conn, PlayerTrade trade, uint revision)
    {
        bool complete;
        lock (trade.Lock)
        {
            if (trade.Over) return;
            var other = trade.Other(conn);
            trade.Accepted[conn] = revision == trade.Revisions[conn].Theirs ? revision : null;
            complete = trade.Accepted[conn] is not null && trade.Accepted[other] == trade.Revisions[other].Theirs;
        }
        Console.WriteLine($"[gateway] {remote} <= 0x00b6 trade accept (their offer revision {revision}){(complete ? ": both accepted" : "")}");
        if (complete) await CompletePlayerTrade(trade);
        else await SendTradeState(trade);
    }

    // C→S 0xb7: Cancel Accept.
    static async Task PlayerTradeUnaccept(ClientState conn, PlayerTrade trade)
    {
        lock (trade.Lock) trade.Accepted[conn] = null;
        await SendTradeState(trade);
    }

    /// <summary>0xb8 flags bit 2 (0x04): this player accepted, its own offer grid is greyed and Accept turns into Cancel
    /// Accept (C→S 0xb7); bit 3 (0x08): the other player accepted, their offer grid is greyed. The trade window copies
    /// the struct (0x140c32800) and sets the grids' +0x33b from +0x378 bits 2 and 3 (0x140c37140).</summary>
    const byte TradeMineAccepted = 0x04, TradeTheirsAccepted = 0x08;

    // The open window again with each side's acceptance: 0xb8 for the same other party updates the existing window
    // (found by that id, 0x140c38d70) and keeps its grids.
    static async Task SendTradeState(PlayerTrade trade)
    {
        foreach (var c in new[] { trade.A, trade.B })
        {
            var them = trade.Other(c);
            byte flags;
            lock (trade.Lock)
            {
                if (trade.Over) return;
                flags = (byte)(PlayerTradeOpenFlags | (trade.Accepted[c] is not null ? TradeMineAccepted : 0)
                    | (trade.Accepted[them] is not null ? TradeTheirsAccepted : 0));
            }
            await SendTo(c, InstanceNpcs.TradeOpen(them.PeerId, PlayerTradeMine, PlayerTradeTheirs, flags, CharacterName(them), -1),
                $"0x00b8 trade with {CharacterName(them)}: flags 0x{flags:x2}");
        }
    }

    // Both accepted: each side's offered items go to the other, in one save per side, after checking that both fit.
    static async Task CompletePlayerTrade(PlayerTrade trade)
    {
        var a = ItemsOf(trade.A);
        var b = ItemsOf(trade.B);
        List<Item> fromA = [.. a.Offered().Select(i => i.Item)], fromB = [.. b.Offered().Select(i => i.Item)];
        if (!a.CanExchange(fromB, ShapeOf) || !b.CanExchange(fromA, ShapeOf))
        {
            lock (trade.Lock) trade.Accepted[trade.A] = trade.Accepted[trade.B] = null;
            foreach (var c in new[] { trade.A, trade.B })
                await (c.Commands?.Reply("Not enough room in an inventory for the traded items.") ?? Task.CompletedTask);
            await SendTradeState(trade);
            return;
        }
        lock (trade.Lock)
        {
            if (trade.Over) return;
            trade.Over = true;
        }
        var gotA = a.Exchange(fromB, ShapeOf);
        var gotB = b.Exchange(fromA, ShapeOf);
        Console.WriteLine($"[gateway] trade completed: {CharacterName(trade.A)} gave {fromA.Count}, {CharacterName(trade.B)} gave {fromB.Count}");
        foreach (var (c, got) in new[] { (trade.A, gotA), (trade.B, gotB) })
        try
        {
            if (got is { Any: true })
                await SendTo(c, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                    ItemsOf(c).NextRevision(PlayerInventories.MainInventory), got.RemovedIds, got.AddedItems),
                    $"0x00bf inventory 1: traded items in ({got.Added.Count} new, {got.Changed.Count} stack(s) grown)");
            await EndTradeView(c, trade.Other(c), accepted: true);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { c.Trade = null; c.TradeRequestTo = 0; }
    }

    // C→S 0xb1 (cancel), a disconnect or leaving the area: every offered item is in its cell again on both sides.
    internal static async Task CancelPlayerTrade(ClientState conn, string why)
    {
        if (conn.Trade is not { } trade) { conn.TradeRequestTo = 0; return; }
        lock (trade.Lock)
        {
            if (trade.Over) return;
            trade.Over = true;
        }
        Console.WriteLine($"[gateway] trade {CharacterName(trade.A)} <-> {CharacterName(trade.B)} cancelled: {why}");
        // Release both reservations first: a side whose connection is gone cannot stop the other getting its items back.
        var restored = new[] { trade.A, trade.B }.Select(c => (c, ItemsOf(c).CancelOffers())).ToList();
        foreach (var (c, back) in restored)
        {
            try
            {
                foreach (var group in back.GroupBy(i => i.Row))
                    await SendTo(c, InstanceItems.InventoryChange(PlayerInventories.InventoryId(group.Key), ItemsOf(c).NextRevision(group.Key),
                        [], [.. group.Select(i => i.Item)]), $"0x00bf trade cancelled: restore {group.Count()} item(s)");
                await EndTradeView(c, trade.Other(c), accepted: false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { c.Trade = null; c.TradeRequestTo = 0; }
        }
    }

    static async Task EndTradeView(ClientState c, ClientState other, bool accepted)
    {
        c.Trade = null;
        c.TradeRequestTo = 0;
        await SendTo(c, InstanceNpcs.TradeClose(other.PeerId, accepted), $"0x00b9 trade with {CharacterName(other)} {(accepted ? "completed" : "closed")}");
    }
}
