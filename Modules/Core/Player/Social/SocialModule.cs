using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;
using static Program;

namespace GameServer.Modules.Social;

/// <summary>Trades between players, parties and persistent account friendships.</summary>
public sealed class SocialModule : IModule
{
    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        GameEvents.EnterMessages.Add(module.Manifest.Id, conn => FriendsFeature.Enter(conn));
        // The first tick after the entry: the party (after an area change) and the friends' presence as they are now.
        GameEvents.Tick.Add(module.Manifest.Id, async (conn, _) =>
        {
            if (conn is not { PlayerSent: true } c || !SocialFeature.Rebound.TryAdd(c, null)) return;
            await PartyRoster.Rebind(c);
            await FriendsFeature.AfterEntry(c);
        });
        InstanceEvents.TransferInstance.Add(module.Manifest.Id, PartyRoster.SharedInstance);
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (conn, why) => FriendsFeature.Leaving(conn));
        GameEvents.Packet(InstanceSocial.FriendAddId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            await FriendsFeature.Change(conn, InstanceSocial.ReadFriendAdd(bytes));
            return true;
        });
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (conn, why) => SocialFeature.CancelPlayerTrade(conn, why));
        GameEvents.Packet(InstanceNpcs.TradeOfferId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            var remote = conn.Wire.Remote;
            if (conn.Trade is not { } trade) return false;
            var offer = InstanceNpcs.ReadTradeOffer(bytes);
            await SocialFeature.PlayerTradeOffer(remote, conn, trade, offer.InventoryId, offer.ItemId, offer.X, offer.Y);
            return true;
        });
        foreach (ushort id in new[] { InstanceNpcs.TradeTakeBackId, InstanceNpcs.TradeMoveAllId })
            GameEvents.Packet(id).Add(module.Manifest.Id, async (conn, bytes) =>
            {
                var remote = conn.Wire.Remote;
                if (conn.Trade is not { } trade) return false;
                var (item, inventory) = InstanceNpcs.ReadTradeMove(bytes);
                await SocialFeature.PlayerTradeOffer(remote, conn, trade, inventory, item);
                return true;
            });
        GameEvents.Packet(InstanceNpcs.TradeAcceptId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            var remote = conn.Wire.Remote;
            if (conn.Trade is not { } trade) return false;
            await SocialFeature.PlayerTradeAccept(remote, conn, trade, InstanceNpcs.ReadTradeAccept(bytes));
            return true;
        });
        GameEvents.Packet(InstanceNpcs.TradeUnacceptId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            if (conn.Trade is not { } trade) return false;
            await SocialFeature.PlayerTradeUnaccept(conn, trade);
            return true;
        });
        GameEvents.Packet(InstanceNpcs.TradeAnswerId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            if (conn.Trade is not null) await SocialFeature.CancelPlayerTrade(conn, "cancelled");
            else if (conn.TradeRequestTo != 0) await SocialFeature.CancelTradeRequest(conn);
            else if (InstanceNpcs.ReadTradeAnswer(bytes).Other != 0 && conn.Sale is null)
                await SocialFeature.DeclineTradeRequest(conn, InstanceNpcs.ReadTradeAnswer(bytes).Other);
            else return false;
            return true;
        });
        GameEvents.Packet(InstanceSocial.TradeRequestAcceptId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            var remote = conn.Wire.Remote;
            await SocialFeature.AcceptTradeRequest(remote, conn, InstanceSocial.ReadTradeRequestAccept(bytes));
            return true;
        });
        GameEvents.Packet(InstanceSocial.TradeRequestId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            var remote = conn.Wire.Remote;
            await SocialFeature.RequestTrade(remote, conn.Wire.Stream, conn.Wire.Ciphers, conn, InstanceSocial.ReadTradeRequest(bytes));
            return true;
        });
        GameEvents.Packet(InstanceSocial.PartyInviteId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            var remote = conn.Wire.Remote;
            // Other modules may resolve names that are not connected human characters.
            return await PartyRoster.InviteHuman(conn, InstanceSocial.ReadPartyInvite(bytes));
        });
        GameEvents.Packet(InstanceSocial.PartyAcceptId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            var remote = conn.Wire.Remote;
            await PartyRoster.AcceptHuman(conn, InstanceSocial.ReadPartyAccept(bytes));
            return true;
        });
        GameEvents.Packet(InstanceSocial.PartyKickId).Add(module.Manifest.Id, (conn, bytes) =>
            PartyRoster.Kick(conn, InstanceSocial.ReadPartyKick(bytes)));
        GameEvents.Packet(InstanceSocial.PartyOptionsId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            if (bytes.Length != InstanceSocial.PartyOptionsLength) return true;
            var options = InstanceSocial.ReadPartyOptions(bytes);
            if (options.CurrentParty) await PartyRoster.ChangeOptions(conn, options.Allocation, options.Scaling);
            return true;
        });
        GameEvents.Packet(InstanceSocial.PartyLeaveId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            var remote = conn.Wire.Remote;
            return await PartyRoster.LeaveHuman(conn, InstanceSocial.ReadPartyAccept(bytes));
        });
        // A "%…" line: only this module knows the party, so it carries the line to the members.
        GameEvents.PartyChat.Add(module.Manifest.Id, PartyChat.Deliver);
    }
}

static class SocialFeature
{
    /// <summary>The two offer inventories of a player trade (manager 1): the NPC sale's (OpenNpcSale), as a player is never
    /// in both. The grid stayed empty with 0x7e0 / 0x7e1 although the item's 0xbf was the sale's (live 2026-10-28).</summary>

    /// <summary>0xb8 flags for a player (0x140dca680): bit 1 alone is the request notification for the other player, bit 0
    /// alone the requester's "Waiting for player to accept trade request..." box (with Cancel), both bits the trade window
    /// (and bit 0 the active trade for Ctrl + click, 0x140d60630); 0x10 lets Accept send 0xb6 without hovering each item.
    /// Neither bit shows nothing (live 2026-10-28).</summary>

    internal static Task SendTo(ClientState c, byte[] bytes, string? log) => c.Outbox is { } outbox ? outbox(bytes, log) : Task.CompletedTask;

    // Presence is shared even when SSF world state is private. Check the authoritative modes at each trade boundary.
    internal static bool PlayerTradeAllowed(ClientState a, ClientState b) =>
        !ReferenceEquals(a, b) && !a.Ssf && !b.Ssf
        && a.Hardcore == b.Hardcore && a.LeagueForm == b.LeagueForm
        && string.Equals(a.League, b.League, StringComparison.OrdinalIgnoreCase)
        && ReferenceEquals(a.Instance.Presence, b.Instance.Presence)
        && !a.Closed && !b.Closed && !a.LeavingArea && !b.LeavingArea
        && a.PlayerLife > 0 && b.PlayerLife > 0 && !a.HardcoreDeathRecorded && !b.HardcoreDeathRecorded;

    // Each side must know the other (0xa7) before 0xb8: the request is looked up by character name in that table.
    internal static async Task Introduce(ClientState to, ClientState about) =>
        await SendTo(to, InstanceSocial.KnownPlayer(about.Account ?? CharacterName(about), CharacterName(about), about.Pools.Level,
            PackedClass(about), about.PortraitFrame, WornPortrait(about)), $"0x00a7 known player {CharacterName(about)}");

    // 0xb8 builds the trade window's two offer grids at once, even for the request and the waiting notice: their
    // inventories must exist (inventory 0 crashed both clients at 0x1402d1029 through the grid 0x140317a44). A fresh
    // pair also empties what an earlier trade left in them.
    internal static async Task FreshOffers(ClientState c)
    {
        foreach (var inv in new[] { PlayerTradeMine, PlayerTradeTheirs })
            await SendTo(c, InstanceInventories.AddInventory(inv, NpcShops.Offer, NpcShops.Manager), $"0x00c0 trade offer {inv}");
    }

    internal static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ClientState, object?> Rebound = new();

    // C→S 0xba: "Trade" on another player in the same instance. The requester sees the waiting notice (0xb8 without
    // flags) and the other player the request (0xb8 with TradeRequestFlag and the requester's name), answered by 0xb0.
    internal static async Task RequestTrade(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint target)
    {
        if (conn.Instance.Player(target) is not { Owner: { Outbox: not null } other } || !PlayerTradeAllowed(conn, other)
            || other.Trade is not null || conn.Trade is not null)
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
    internal static async Task DeclineTradeRequest(ClientState conn, uint requester)
    {
        Console.WriteLine($"[gateway] {CharacterName(conn)} declined the trade request from 0x{requester:x}");
        await SendTo(conn, InstanceNpcs.TradeClose(requester, false), "0x00b9 trade request declined");
        if (conn.Instance.Player(requester) is { Owner: { } other } && other.TradeRequestTo == conn.PeerId)
            await CancelTradeRequest(other);
    }

    // C→S 0xb1 on the requester's waiting box (Cancel): the box and the other player's notification close (0xb9).
    internal static async Task CancelTradeRequest(ClientState conn)
    {
        uint to = conn.TradeRequestTo;
        conn.TradeRequestTo = 0;
        Console.WriteLine($"[gateway] trade request from {CharacterName(conn)} to 0x{to:x} cancelled");
        await SendTo(conn, InstanceNpcs.TradeClose(to, false), "0x00b9 trade request cancelled");
        if (conn.Instance.Player(to) is { Owner: { } other })
            await SendTo(other, InstanceNpcs.TradeClose(conn.PeerId, false), $"0x00b9 trade request from {CharacterName(conn)} withdrawn");
    }

    // C→S 0xb0: the request accepted (u32 the requester's object id). Both get the open trade window.
    internal static async Task AcceptTradeRequest(string remote, ClientState conn, uint requester)
    {
        if (conn.Instance.Player(requester) is not { Owner: { } other } || other.TradeRequestTo != conn.PeerId
            || !PlayerTradeAllowed(conn, other) || other.Trade is not null || conn.Trade is not null)
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
            // The window is the request's own panel, found again by the other party's id (0x140c38ef0), still bound to the
            // offer inventories created with the request: new ones here left its grids pointing at the old ones and every
            // offered item invisible (live 2026-10-28).
            await SendTo(c, InstanceNpcs.TradeOpen(them.PeerId, PlayerTradeMine, PlayerTradeTheirs, PlayerTradeOpenFlags, CharacterName(them), -1),
                $"0x00b8 trade with {CharacterName(them)} open");
        }
    }

    // C→S 0xb2 / 0xb3 / 0xb5 while a player trade is open: an item into the offer (a drop at x, y, or Ctrl / Shift
    // click into the first free cell), or an offered item taken back.
    internal static async Task PlayerTradeOffer(string remote, ClientState conn, PlayerTrade trade, uint inventory, uint itemId, byte? x = null, byte? y = null)
    {
        if (!PlayerTradeAllowed(conn, trade.Other(conn)))
        {
            await CancelPlayerTrade(conn, "player or game mode no longer permits trading");
            return;
        }
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

    internal static async Task PlayerTradeTakeBack(string remote, ClientState conn, PlayerTrade trade, uint itemId)
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
    internal static async Task PlayerTradeAccept(string remote, ClientState conn, PlayerTrade trade, uint revision)
    {
        if (!PlayerTradeAllowed(conn, trade.Other(conn)))
        {
            await CancelPlayerTrade(conn, "player or game mode no longer permits trading");
            return;
        }
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
    internal static async Task PlayerTradeUnaccept(ClientState conn, PlayerTrade trade)
    {
        lock (trade.Lock) trade.Accepted[conn] = null;
        await SendTradeState(trade);
    }

    /// <summary>0xb8 flags bit 2 (0x04): this player accepted, its own offer grid is greyed and Accept turns into Cancel
    /// Accept (C→S 0xb7); bit 3 (0x08): the other player accepted, their offer grid is greyed. The trade window copies
    /// the struct (0x140c32980) and sets the grids' +0x33b from +0x378 bits 2 and 3 (0x140c372c0).</summary>

    // The open window again with each side's acceptance: 0xb8 for the same other party updates the existing window
    // (found by that id, 0x140c38ef0) and keeps its grids.
    internal static async Task SendTradeState(PlayerTrade trade)
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

    // Both accepted: eligibility and both saves commit before either player can die or leave during the exchange.
    internal static async Task CompletePlayerTrade(PlayerTrade trade)
    {
        PlayerInventories.Stacking? gotA = null, gotB = null;
        bool prohibited = false, noRoom = false;
        int countA = 0, countB = 0;
        var first = trade.A.PeerId < trade.B.PeerId ? trade.A : trade.B;
        var second = trade.Other(first);
        lock (first.Monsters)
        lock (second.Monsters)
        lock (trade.Lock)
        {
            if (trade.Over) return;
            prohibited = !PlayerTradeAllowed(trade.A, trade.B);
            if (!prohibited)
            {
                var a = ItemsOf(trade.A);
                var b = ItemsOf(trade.B);
                List<Item> fromA = [.. a.Offered().Select(i => i.Item)], fromB = [.. b.Offered().Select(i => i.Item)];
                noRoom = !a.CanExchange(fromB, ShapeOf) || !b.CanExchange(fromA, ShapeOf);
                if (noRoom) trade.Accepted[trade.A] = trade.Accepted[trade.B] = null;
                else
                {
                    Saves.Atomically(() =>
                    {
                        gotA = a.Exchange(fromB, ShapeOf) ?? throw new InvalidOperationException("First trade inventory changed during exchange");
                        gotB = b.Exchange(fromA, ShapeOf) ?? throw new InvalidOperationException("Second trade inventory changed during exchange");
                    });
                    trade.Over = true;
                    (countA, countB) = (fromA.Count, fromB.Count);
                }
            }
        }
        if (prohibited)
        {
            await CancelPlayerTrade(trade.A, "player or game mode no longer permits trading");
            return;
        }
        if (noRoom)
        {
            foreach (var c in new[] { trade.A, trade.B })
                await (c.Commands?.Reply("Not enough room in an inventory for the traded items.") ?? Task.CompletedTask);
            await SendTradeState(trade);
            return;
        }
        Console.WriteLine($"[gateway] trade completed: {CharacterName(trade.A)} gave {countA}, {CharacterName(trade.B)} gave {countB}");
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

    internal static async Task EndTradeView(ClientState c, ClientState other, bool accepted)
    {
        c.Trade = null;
        c.TradeRequestTo = 0;
        await SendTo(c, InstanceNpcs.TradeClose(other.PeerId, accepted), $"0x00b9 trade with {CharacterName(other)} {(accepted ? "completed" : "closed")}");
    }
}
