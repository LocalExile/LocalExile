using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;
using static Program;

namespace GameServer.Modules.Social;

/// <summary>Trades between players, parties and persistent account friendships.</summary>
public sealed class SocialModule : IModule
{
    /// <summary>This module's game data, built in a fixed order when it loads: before the Gateway listens, as the
    /// Gateway's own (WorldLoader). Tests that use the module call it once.</summary>
    public static void LoadData()
    {
        PartyTravel.LoadPartyTravelData();
    }

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        LoadData();
        GameEvents.EnterMessages.Add(module.Manifest.Id, player => FriendsFeature.Enter(player).Concat(GuildFeature.Enter(player)));
        // The first tick after the entry: the party (after an area change) and the friends' presence as they are now.
        GameEvents.AreaTick.Add(module.Manifest.Id, _ =>
        {
            bool done = false;
            return async (c, _) =>
            {
                if (done || !c.Connection.PlayerSent) return;
                done = true;
                if (!SocialFeature.Rebound.TryAdd(c, null)) return;
                await PartyRoster.Rebind(c);
                await FriendsFeature.AfterEntry(c);
                await GuildFeature.AfterEntry(c);
            };
        });
        InstanceEvents.TransferInstance.Add(module.Manifest.Id, PartyRoster.SharedInstance);
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, async (player, why) =>
        {
            await PartyRoster.DisconnectHuman(player, why);
            await FriendsFeature.Leaving(player);
            await GuildFeature.Leaving(player);
        });
        GameEvents.Packet(InstanceSocial.FriendAddId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await FriendsFeature.Change(player, InstanceSocial.ReadFriendAdd(bytes));
            return true;
        });
        GameEvents.Packet(InstanceSocial.SocialStatusId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await FriendsFeature.SetStatus(player, InstanceSocial.ReadSocialStatus(bytes));
            return true;
        });
        GameEvents.Packet(InstanceSocial.PublicPartyId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            var (desc, flag) = InstanceSocial.ReadPublicParty(bytes);
            await PartyRoster.SetPublicParty(player, desc, flag);
            return true;
        });
        GameEvents.Packet(InstanceSocial.HideoutInviteId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await FriendsFeature.InviteToHideout(player, InstanceSocial.ReadHideoutInvite(bytes).Name);
            return true;
        });
        GameEvents.Packet(InstanceSocial.DuelChallengeId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await FriendsFeature.ChallengeDuel(player, InstanceSocial.ReadDuelChallenge(bytes));
            return true;
        });
        GameEvents.PartyInvite.Add(module.Manifest.Id, PartyRoster.InviteHuman);
        GameEvents.PlayerIgnored.Add(module.Manifest.Id, (viewer, sender) => FriendsFeature.IsIgnoring(viewer.Character.Account, sender));
        GameEvents.HideoutVisitAllowed.Add(module.Manifest.Id, (visitor, owner) => FriendsFeature.CanVisitHideout(visitor, owner) || GuildFeature.SameGuild(visitor, owner));
        GameEvents.GuildChat.Add(module.Manifest.Id, GuildFeature.DeliverChat);
        GameEvents.GuildStorageKey.Add(module.Manifest.Id, GuildFeature.StorageKeyOf);
        GameEvents.GuildPeers.Add(module.Manifest.Id, GuildFeature.PeersOf);
        GameEvents.GuildHideoutSelected.Add(module.Manifest.Id, GuildFeature.SelectGuildHideout);
        GameEvents.GuildHideoutHash.Add(module.Manifest.Id, GuildFeature.HideoutHashOf);
        GameEvents.GuildTagQuery.Add(module.Manifest.Id, GuildFeature.TagOf);
        GameEvents.GuildCanManageStash.Add(module.Manifest.Id, GuildFeature.CanManageStash);
        module.Lifetime.Own(new GuildCleanup());
        GameEvents.Packet(InstanceGuilds.CreateId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await GuildFeature.Create(player, InstanceGuilds.ReadText(bytes));
            return true;
        });
        GameEvents.Packet(InstanceGuilds.InviteId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await GuildFeature.Invite(player, InstanceGuilds.ReadText(bytes));
            return true;
        });
        GameEvents.Packet(InstanceGuilds.AcceptInviteId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await GuildFeature.AcceptInvite(player, InstanceGuilds.ReadInviteIndex(bytes));
            return true;
        });
        GameEvents.Packet(InstanceGuilds.DeclineInviteId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await GuildFeature.DeclineInvite(player, InstanceGuilds.ReadInviteIndex(bytes));
            return true;
        });
        GameEvents.Packet(InstanceGuilds.SetMemberRankId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            var (target, rank) = InstanceGuilds.ReadSetMemberRank(bytes);
            await GuildFeature.SetMemberRank(player, target, rank);
            return true;
        });
        GameEvents.Packet(InstanceGuilds.RemoveMemberId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await GuildFeature.LeaveOrKick(player, InstanceGuilds.ReadText(bytes));
            return true;
        });
        GameEvents.Packet(InstanceGuilds.SetStatusId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await GuildFeature.SetStatus(player, InstanceGuilds.ReadText(bytes));
            return true;
        });
        GameEvents.Packet(InstanceGuilds.RenameId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await GuildFeature.Rename(player, InstanceGuilds.ReadText(bytes));
            return true;
        });
        GameEvents.Packet(InstanceGuilds.SetTagId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await GuildFeature.SetTag(player, InstanceGuilds.ReadText(bytes));
            return true;
        });
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (player, why) => SocialFeature.CancelPlayerTrade(player, why));
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (player, _) => PvpQueue.Leaving(player));
        GameEvents.Packet(InstancePvp.LeaveQueueId).Add(module.Manifest.Id, async (player, _) =>
        {
            await PvpQueue.Leave(player, withdraw: false);
            return true;
        });
        GameEvents.Packet(InstancePvp.WithdrawId).Add(module.Manifest.Id, async (player, _) =>
        {
            await PvpQueue.Leave(player, withdraw: true);
            return true;
        });
        GameEvents.Packet(InstanceNpcs.TradeOfferId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            if (player.Trade is not { } trade) return false;
            var offer = InstanceNpcs.ReadTradeOffer(bytes);
            await SocialFeature.PlayerTradeOffer(player, trade, offer.InventoryId, offer.ItemId, offer.X, offer.Y);
            return true;
        });
        foreach (ushort id in new[] { InstanceNpcs.TradeTakeBackId, InstanceNpcs.TradeMoveAllId })
            GameEvents.Packet(id).Add(module.Manifest.Id, async (player, bytes) =>
            {
                if (player.Trade is not { } trade) return false;
                var (item, inventory) = InstanceNpcs.ReadTradeMove(bytes);
                await SocialFeature.PlayerTradeOffer(player, trade, inventory, item);
                return true;
            });
        GameEvents.Packet(InstanceNpcs.TradeCtrlMoveId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            if (player.Trade is not { } trade) return false;
            var (item, inventory, _) = InstanceNpcs.ReadTradeCtrlMove(bytes);
            await SocialFeature.PlayerTradeMoveSameType(player, trade, inventory, item);
            return true;
        });
        GameEvents.Packet(InstanceNpcs.TradeAcceptId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            if (player.Trade is not { } trade) return false;
            await SocialFeature.PlayerTradeAccept(player, trade, InstanceNpcs.ReadTradeAccept(bytes));
            return true;
        });
        GameEvents.Packet(InstanceNpcs.TradeUnacceptId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            if (player.Trade is not { } trade) return false;
            await SocialFeature.PlayerTradeUnaccept(player, trade);
            return true;
        });
        GameEvents.Packet(InstanceNpcs.TradeAnswerId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            if (player.Trade is not null) await SocialFeature.CancelPlayerTrade(player, "cancelled");
            else if (player.TradeRequestTo != 0) await SocialFeature.CancelTradeRequest(player);
            else if (InstanceNpcs.ReadTradeAnswer(bytes).Other != 0 && player.Sale is null)
                await SocialFeature.DeclineTradeRequest(player, InstanceNpcs.ReadTradeAnswer(bytes).Other);
            else return false;
            return true;
        });
        GameEvents.Packet(InstanceSocial.TradeRequestAcceptId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await SocialFeature.AcceptTradeRequest(player, InstanceSocial.ReadTradeRequestAccept(bytes));
            return true;
        });
        GameEvents.Packet(InstanceSocial.TradeRequestId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await SocialFeature.RequestTrade(player, InstanceSocial.ReadTradeRequest(bytes));
            return true;
        });
        GameEvents.Packet(InstanceSocial.PartyInviteId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            // Other modules may resolve names that are not connected human characters.
            return await PartyRoster.InviteHuman(player, InstanceSocial.ReadPartyInvite(bytes));
        });
        GameEvents.Packet(InstanceSocial.PartyAcceptId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await PartyRoster.AcceptHuman(player, InstanceSocial.ReadPartyAccept(bytes));
            return true;
        });
        GameEvents.Packet(InstanceSocial.PartyKickId).Add(module.Manifest.Id, (player, bytes) =>
            PartyRoster.Kick(player, InstanceSocial.ReadPartyKick(bytes)));
        GameEvents.Packet(InstanceSocial.PartyLeaveId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            return await PartyRoster.LeaveHuman(player, InstanceSocial.ReadPartyAccept(bytes));
        });
        // The panel's travel button: the member's row shows their area (0xa7 +0xa8), and this moves the player there.
        GameEvents.Packet(InstanceSocial.PartyTravelId).Add(module.Manifest.Id, (player, bytes) =>
            PartyTravel.Travel(player, InstanceSocial.ReadPartyTravel(bytes)));
        // A "%…" line: only this module knows the party, so it carries the line to the members.
        GameEvents.PartyChat.Add(module.Manifest.Id, PartyChat.Deliver);
        // Supply out-of-area party members for Act 2 Bandit party votes (WaitingToEnterArea).
        module.Lifetime.Own(BanditChoices.RegisterPartyMembers(player =>
        {
            uint party = PartyRoster.PartyOf(player);
            return party == 0 ? [] : PartyRoster.Players(party);
        }));
    }

    sealed class GuildCleanup : IDisposable
    {
        public void Dispose() => GuildFeature.ResetRuntime();
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

    internal static Task SendTo(Player c, byte[] bytes, string? log) => c.Connection.Outbox is { } outbox ? outbox(bytes, log) : Task.CompletedTask;

    // Presence is shared even when SSF world state is private. Check the authoritative modes at each trade boundary.
    internal static bool PlayerTradeAllowed(Player a, Player b) =>
        !ReferenceEquals(a, b) && !a.Character.Ssf && !b.Character.Ssf
        && a.Character.Hardcore == b.Character.Hardcore && a.Character.LeagueForm == b.Character.LeagueForm
        && string.Equals(a.Character.League, b.Character.League, StringComparison.OrdinalIgnoreCase)
        && ReferenceEquals(a.Instance.Presence, b.Instance.Presence)
        && !a.Closed && !b.Closed && !a.LeavingArea && !b.LeavingArea
        && a.PlayerLife > 0 && b.PlayerLife > 0 && !a.Character.HardcoreDeathRecorded && !b.Character.HardcoreDeathRecorded;

    // Each side must know the other (0xa7) before 0xb8: the request is looked up by character name in that table. The
    // third field is the player's area as a WorldAreas HASH16 (+0xa8), not the character's level (#715).
    internal static async Task Introduce(Player to, Player about) =>
        await SendTo(to, InstanceSocial.KnownPlayer(about.Character.Account ?? CharacterName(about), CharacterName(about), about.Layout.Hash,
            PackedClass(about), about.Character.PortraitFrame, WornPortrait(about), FriendsFeature.StatusOf(about.Character.Account)), $"0x00a7 known player {CharacterName(about)}");

    // 0xb8 builds the trade window's two offer grids at once, even for the request and the waiting notice: their
    // inventories must exist (inventory 0 crashed both clients at 0x1402d1029 through the grid 0x140317a44). A fresh
    // pair also empties what an earlier trade left in them.
    internal static async Task FreshOffers(Player c)
    {
        foreach (var inv in new[] { PlayerTradeMine, PlayerTradeTheirs })
            await SendTo(c, InstanceInventories.AddInventory(inv, NpcShops.Offer, NpcShops.Manager), $"0x00c0 trade offer {inv}");
    }

    internal static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Player, object?> Rebound = new();

    // C→S 0xba: "Trade" on another player in the same instance. The requester sees the waiting notice (0xb8 without
    // flags) and the other player the request (0xb8 with TradeRequestFlag and the requester's name), answered by 0xb0.
    internal static async Task RequestTrade(Player player, uint target)
    {
        if (player.Instance.Player(target) is not { Connection.Outbox: not null } other || !PlayerTradeAllowed(player, other)
            || other.Trade is not null || player.Trade is not null
            || FriendsFeature.IsIgnoring(other.Character.Account, player.Character.Account ?? CharacterName(player)))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x00ba trade request to 0x{target:x}: not available");
            return;
        }
        string me = CharacterName(player), them = CharacterName(other);
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x00ba trade request: {me} -> {them} (0x{target:x})");
        player.TradeRequestTo = other.Id;
        await Introduce(player, other);
        await Introduce(other, player);
        await FreshOffers(player);
        await FreshOffers(other);
        await SendTo(player, InstanceNpcs.TradeOpen(other.Id, PlayerTradeMine, PlayerTradeTheirs, PlayerTradeWaitingFlags, them, -1), $"0x00b8 trade request to {them}: waiting");
        await SendTo(other, InstanceNpcs.TradeOpen(player.Id, PlayerTradeMine, PlayerTradeTheirs, InstanceSocial.TradeRequestFlag, me, -1),
            $"0x00b8 trade request from {me}");
    }

    // C→S 0xb1 (u32 the requester, u8) from the notification: the request declined; the requester's box closes.
    internal static async Task DeclineTradeRequest(Player player, uint requester)
    {
        Console.WriteLine($"[gateway] {CharacterName(player)} declined the trade request from 0x{requester:x}");
        await SendTo(player, InstanceNpcs.TradeClose(requester, false), "0x00b9 trade request declined");
        if (player.Instance.Player(requester) is { } other && other.TradeRequestTo == player.Id)
            await CancelTradeRequest(other);
    }

    // C→S 0xb1 on the requester's waiting box (Cancel): the box and the other player's notification close (0xb9).
    internal static async Task CancelTradeRequest(Player player)
    {
        uint to = player.TradeRequestTo;
        player.TradeRequestTo = 0;
        Console.WriteLine($"[gateway] trade request from {CharacterName(player)} to 0x{to:x} cancelled");
        await SendTo(player, InstanceNpcs.TradeClose(to, false), "0x00b9 trade request cancelled");
        if (player.Instance.Player(to) is { } other)
            await SendTo(other, InstanceNpcs.TradeClose(player.Id, false), $"0x00b9 trade request from {CharacterName(player)} withdrawn");
    }

    // C→S 0xb0: the request accepted (u32 the requester's object id). Both get the open trade window.
    internal static async Task AcceptTradeRequest(Player player, uint requester)
    {
        if (player.Instance.Player(requester) is not { } other || other.TradeRequestTo != player.Id
            || !PlayerTradeAllowed(player, other) || other.Trade is not null || player.Trade is not null)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x00b0 trade accept from 0x{requester:x}: no pending request");
            return;
        }
        other.TradeRequestTo = 0;
        var trade = new PlayerTrade(other, player);
        other.Trade = player.Trade = trade;
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x00b0 trade accepted: {CharacterName(other)} <-> {CharacterName(player)}");
        foreach (var c in new[] { other, player })
        {
            var them = trade.Other(c);
            // The window is the request's own panel, found again by the other party's id (0x140c38ef0), still bound to the
            // offer inventories created with the request: new ones here left its grids pointing at the old ones and every
            // offered item invisible (live 2026-10-28).
            await SendTo(c, InstanceNpcs.TradeOpen(them.Id, PlayerTradeMine, PlayerTradeTheirs, PlayerTradeOpenFlags, CharacterName(them), -1),
                $"0x00b8 trade with {CharacterName(them)} open");
        }
    }

    // C→S 0xb4 (Ctrl + right-click): move every item of the clicked item's base type between inventory and trade offer.
    internal static async Task PlayerTradeMoveSameType(Player player, PlayerTrade trade, uint inventory, uint itemId)
    {
        if (!PlayerTradeAllowed(player, trade.Other(player)))
        {
            await CancelPlayerTrade(player, "player or game mode no longer permits trading");
            return;
        }
        var items = ItemsOf(player);
        if (items.Offered().FirstOrDefault(i => i.Id == itemId) is { } offeredTarget)
        {
            uint type = offeredTarget.Item.TypeHash;
            uint[] ids = [itemId, .. items.Offered().Where(i => i.Id != itemId && i.Item.TypeHash == type)
                .OrderBy(i => i.Y).ThenBy(i => i.X).Select(i => i.Id)];
            bool moved = false;
            foreach (uint id in ids)
            {
                if (player.Trade != trade || trade.Over) break;
                int before = items.Offered().Count;
                await PlayerTradeTakeBack(player, trade, id, sendState: false);
                if (items.Offered().Count != before) moved = true;
            }
            if (moved && player.Trade == trade && !trade.Over) await SendTradeState(trade);
            return;
        }
        byte? row = PlayerInventories.Row(inventory);
        var clicked = row is byte r ? items.Items(r).FirstOrDefault(i => i.Id == itemId) : null;
        if (clicked is null)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x00b4 trade ctrl-move: item {itemId} not in inventory {inventory}");
            return;
        }
        uint matchType = clicked.Item.TypeHash;
        uint[] toOffer = [itemId, .. items.Items(row!.Value).Where(i => i.Id != itemId && i.Item.TypeHash == matchType)
            .OrderBy(i => i.Y).ThenBy(i => i.X).Select(i => i.Id)];
        bool offeredAny = false;
        foreach (uint id in toOffer)
        {
            if (player.Trade != trade || trade.Over) break;
            int before = items.Offered().Count;
            await PlayerTradeOffer(player, trade, inventory, id, sendState: false);
            if (items.Offered().Count == before) break;
            offeredAny = true;
        }
        if (offeredAny && player.Trade == trade && !trade.Over) await SendTradeState(trade);
    }

    // C→S 0xb2 / 0xb3 / 0xb5 while a player trade is open: an item into the offer (a drop at x, y, or Ctrl / Shift
    // click into the first free cell), or an offered item taken back.
    internal static async Task PlayerTradeOffer(Player player, PlayerTrade trade, uint inventory, uint itemId, byte? x = null, byte? y = null, bool sendState = true)
    {
        if (!PlayerTradeAllowed(player, trade.Other(player)))
        {
            await CancelPlayerTrade(player, "player or game mode no longer permits trading");
            return;
        }
        var items = ItemsOf(player);
        if (items.Offered().Any(i => i.Id == itemId)) { await PlayerTradeTakeBack(player, trade, itemId, sendState); return; }
        byte? row = PlayerInventories.Row(inventory);
        var it = row is byte r ? items.Items(r).FirstOrDefault(i => i.Id == itemId) : null;
        if (it is null || ShapeOf(it.Item) is not { } shape) { Console.WriteLine($"[gateway] {player.Connection.Remote} trade offer: item {itemId} not in inventory {inventory}"); return; }
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
            Console.WriteLine($"[gateway] {player.Connection.Remote} trade offer: no room for item {itemId}");
            return;
        }
        var other = trade.Other(player);
        var shown = waiting with { X = at.X, Y = at.Y };
        uint mine, theirs, view;
        lock (trade.Lock)
        {
            if (trade.Over) return;
            view = ItemsOf(other).NewItemId();
            trade.Views[player][itemId] = view;
            mine = trade.Revisions[player].Mine + 1;
            trade.Revisions[player] = (mine, trade.Revisions[player].Theirs);
            theirs = trade.Revisions[other].Theirs + 1;
            trade.Revisions[other] = (trade.Revisions[other].Mine, theirs);
            trade.Accepted[player] = trade.Accepted[other] = null;
        }
        await SendTo(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(waits), items.NextRevision(waits), [itemId], []),
            $"0x00bf inventory {PlayerInventories.InventoryId(waits)}: hide traded item {itemId}");
        await SendTo(player, InstanceItems.InventoryChange(PlayerTradeMine, mine, [], [shown], NpcShops.Manager), $"0x00bf trade offer: item {itemId} at ({at.X}, {at.Y})");
        await SendTo(other, InstanceItems.InventoryChange(PlayerTradeTheirs, theirs, [], [shown with { Id = view }], NpcShops.Manager),
            $"0x00bf {CharacterName(player)}'s offer: item {view} at ({at.X}, {at.Y})");
        if (sendState) await SendTradeState(trade);
    }

    internal static async Task PlayerTradeTakeBack(Player player, PlayerTrade trade, uint itemId, bool sendState = true)
    {
        var items = ItemsOf(player);
        if (items.TakeBack(itemId) is not var (row, back)) return;
        var other = trade.Other(player);
        uint mine, theirs, view;
        lock (trade.Lock)
        {
            view = trade.Views[player].Remove(itemId, out uint v) ? v : 0;
            mine = trade.Revisions[player].Mine + 1;
            trade.Revisions[player] = (mine, trade.Revisions[player].Theirs);
            theirs = trade.Revisions[other].Theirs + 1;
            trade.Revisions[other] = (trade.Revisions[other].Mine, theirs);
            trade.Accepted[player] = trade.Accepted[other] = null;
        }
        await SendTo(player, InstanceItems.InventoryChange(PlayerTradeMine, mine, [itemId], [], NpcShops.Manager), $"0x00bf trade offer: remove item {itemId}");
        await SendTo(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), items.NextRevision(row), [], [back]),
            $"0x00bf return item {itemId} to inventory {PlayerInventories.InventoryId(row)}");
        await SendTo(other, InstanceItems.InventoryChange(PlayerTradeTheirs, theirs, [view], [], NpcShops.Manager),
            $"0x00bf {CharacterName(player)}'s offer: remove item {view}");
        if (sendState) await SendTradeState(trade);
    }

    // C→S 0xb6: Accept, with the revision of the other's offer this client saw. Both accepted on the current offers
    // completes the trade; any later change clears both acceptances.
    internal static async Task PlayerTradeAccept(Player player, PlayerTrade trade, uint revision)
    {
        if (!PlayerTradeAllowed(player, trade.Other(player)))
        {
            await CancelPlayerTrade(player, "player or game mode no longer permits trading");
            return;
        }
        bool complete;
        lock (trade.Lock)
        {
            if (trade.Over) return;
            var other = trade.Other(player);
            trade.Accepted[player] = revision == trade.Revisions[player].Theirs ? revision : null;
            complete = trade.Accepted[player] is not null && trade.Accepted[other] == trade.Revisions[other].Theirs;
        }
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x00b6 trade accept (their offer revision {revision}){(complete ? ": both accepted" : "")}");
        if (complete) await CompletePlayerTrade(trade);
        else await SendTradeState(trade);
    }

    // C→S 0xb7: Cancel Accept.
    internal static async Task PlayerTradeUnaccept(Player player, PlayerTrade trade)
    {
        lock (trade.Lock) trade.Accepted[player] = null;
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
            await SendTo(c, InstanceNpcs.TradeOpen(them.Id, PlayerTradeMine, PlayerTradeTheirs, flags, CharacterName(them), -1),
                $"0x00b8 trade with {CharacterName(them)}: flags 0x{flags:x2}");
        }
    }

    // Both accepted: eligibility and both saves commit before either player can die or leave during the exchange.
    internal static async Task CompletePlayerTrade(PlayerTrade trade)
    {
        PlayerInventories.Stacking? gotA = null, gotB = null;
        bool prohibited = false, noRoom = false;
        int countA = 0, countB = 0;
        var first = trade.A.Id < trade.B.Id ? trade.A : trade.B;
        var second = trade.Other(first);
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
    internal static async Task CancelPlayerTrade(Player player, string why)
    {
        if (player.Trade is not { } trade) { player.TradeRequestTo = 0; return; }
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

    internal static async Task EndTradeView(Player c, Player other, bool accepted)
    {
        c.Trade = null;
        c.TradeRequestTo = 0;
        await SendTo(c, InstanceNpcs.TradeClose(other.Id, accepted), $"0x00b9 trade with {CharacterName(other)} {(accepted ? "completed" : "closed")}");
    }
}
