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
        static (ClientState Conn, string Remote) Of(Packet p) => (ConnOf(p.Player), ConnOf(p.Player).Wire.Remote);
        module.OnEnterMessages(player => FriendsFeature.Enter(ConnOf(player)));
        // The first tick after the entry: the party (after an area change) and the friends' presence as they are now.
        module.OnTick(async (player, _) =>
        {
            if (ConnOf(player) is not { PlayerSent: true } c || !SocialFeature.Rebound.TryAdd(c, null)) return;
            await SocialFeature.RebindParty(c);
            await FriendsFeature.AfterEntry(c);
        });
        module.Lifetime.Own(InstanceEvents.TransferInstance.Add("Social", SocialFeature.PartyInstance));
        module.OnPlayerLeaving((player, why) => FriendsFeature.Leaving(ConnOf(player)));
        module.OnPacket(InstanceSocial.FriendAddId, async p =>
        {
            await FriendsFeature.Change(ConnOf(p.Player), InstanceSocial.ReadFriendAdd(p.Bytes.ToArray()));
            return true;
        });
        module.OnPlayerLeaving((player, why) => SocialFeature.CancelPlayerTrade(ConnOf(player), why));
        module.OnPacket(InstanceNpcs.TradeOfferId, async p =>
        {
            var (conn, remote) = Of(p);
            if (conn.Trade is not { } trade) return false;
            var offer = InstanceNpcs.ReadTradeOffer(p.Bytes.ToArray());
            await SocialFeature.PlayerTradeOffer(remote, conn, trade, offer.InventoryId, offer.ItemId, offer.X, offer.Y);
            return true;
        });
        foreach (ushort id in new[] { InstanceNpcs.TradeTakeBackId, InstanceNpcs.TradeMoveAllId })
            module.OnPacket(id, async p =>
            {
                var (conn, remote) = Of(p);
                if (conn.Trade is not { } trade) return false;
                var (item, inventory) = InstanceNpcs.ReadTradeMove(p.Bytes.ToArray());
                await SocialFeature.PlayerTradeOffer(remote, conn, trade, inventory, item);
                return true;
            });
        module.OnPacket(InstanceNpcs.TradeAcceptId, async p =>
        {
            var (conn, remote) = Of(p);
            if (conn.Trade is not { } trade) return false;
            await SocialFeature.PlayerTradeAccept(remote, conn, trade, InstanceNpcs.ReadTradeAccept(p.Bytes.ToArray()));
            return true;
        });
        module.OnPacket(InstanceNpcs.TradeUnacceptId, async p =>
        {
            var (conn, _) = Of(p);
            if (conn.Trade is not { } trade) return false;
            await SocialFeature.PlayerTradeUnaccept(conn, trade);
            return true;
        });
        module.OnPacket(InstanceNpcs.TradeAnswerId, async p =>
        {
            var (conn, _) = Of(p);
            if (conn.Trade is not null) await SocialFeature.CancelPlayerTrade(conn, "cancelled");
            else if (conn.TradeRequestTo != 0) await SocialFeature.CancelTradeRequest(conn);
            else if (InstanceNpcs.ReadTradeAnswer(p.Bytes.ToArray()).Other != 0 && conn.Sale is null)
                await SocialFeature.DeclineTradeRequest(conn, InstanceNpcs.ReadTradeAnswer(p.Bytes.ToArray()).Other);
            else return false;
            return true;
        });
        module.OnPacket(InstanceSocial.TradeRequestAcceptId, async p =>
        {
            var (conn, remote) = Of(p);
            await SocialFeature.AcceptTradeRequest(remote, conn, InstanceSocial.ReadTradeRequestAccept(p.Bytes.ToArray()));
            return true;
        });
        module.OnPacket(InstanceSocial.TradeRequestId, async p =>
        {
            var (conn, remote) = Of(p);
            await SocialFeature.RequestTrade(remote, conn.Wire.Stream, conn.Wire.Ciphers, conn, InstanceSocial.ReadTradeRequest(p.Bytes.ToArray()));
            return true;
        });
        module.OnPacket(InstanceSocial.PartyInviteId, async p =>
        {
            var (conn, remote) = Of(p);
            // Not a player here: another module may own the name (the PlayerBots module's bots).
            return await SocialFeature.InviteToParty(remote, conn, InstanceSocial.ReadPartyInvite(p.Bytes.ToArray()));
        });
        module.OnPacket(InstanceSocial.PartyAcceptId, async p =>
        {
            var (conn, remote) = Of(p);
            await SocialFeature.AcceptParty(remote, conn, InstanceSocial.ReadPartyAccept(p.Bytes.ToArray()));
            return true;
        });
        module.OnPacket(InstanceSocial.PartyLeaveId, async p =>
        {
            var (conn, remote) = Of(p);
            return await SocialFeature.LeaveParty(remote, conn, InstanceSocial.ReadPartyAccept(p.Bytes.ToArray()));
        });
        // A '%' line is party chat. Returning false leaves the packet to the Gateway's own chat handling (local, '#'
        // global, '$' trade); it ignores a party line (there is no party registry there).
        module.OnPacket(InstanceChat.ChatId, async p =>
        {
            var (conn, remote) = Of(p);
            await SocialFeature.PartyChat(remote, conn, InstanceChat.ReadChat(p.Bytes.ToArray()).Text);
            return false;
        });
        // C→S 0x94: the party panel's Kick, a wstr member name (captured live 2026-11-08: the client sent the kicked bot's
        // name as a compact wstr). A bot party is the PlayerBots module's and it sees the packet first, so this handler
        // only runs for a party of real players; returning false hands it on.
        module.OnPacket(0x0094, async p =>
        {
            var (conn, remote) = Of(p);
            string name = new LoginReader(p.Bytes.Span[2..]).WStr();
            if (name.Length == 0) return false;
            var (ok, message) = await SocialFeature.Kick(conn, name);
            Console.WriteLine($"[gateway] {remote} <= 0x0094 kick \"{name}\": {(ok ? message : "not taken")}");
            if (ok) await SocialFeature.Tell(conn, message);
            return ok;
        });
        // C→S 0xac (3 bytes: the id alone): the party panel asks for its members again — it opens it, or a member changes
        // area. Answering with 0xa7 per member is what fills the panel's area column ("Travelling..." until then).
        module.OnPacket(0x00ac, async p =>
        {
            var (conn, remote) = Of(p);
            await SocialFeature.RefreshParty(remote, conn);
            return true;
        });

        // Party commands: the panel's buttons need packets whose layouts are not known yet, so the same operations are
        // also reachable as chat commands. Everything they send (0xa9, 0xaa) is the traffic the UI path already uses.
        module.OnChatCommand("invite", "/invite <character name> (invite that player to your party)", async cmd =>
        {
            if (cmd.Args.Count == 0) { await SocialFeature.Tell(cmd.Player, "用法：/invite <角色名>"); return; }
            string name = string.Join(' ', cmd.Args);
            var conn = ConnOf(cmd.Player);
            bool ok = await SocialFeature.InviteToParty(conn.Wire.Remote, conn, name);
            await SocialFeature.Tell(cmd.Player, ok ? $"已邀请 {name}。" : $"没找到在线角色 \"{name}\"（需已进入游戏）。");
        });
        module.OnChatCommand("leave", "/leave (leave your party)", async cmd =>
        {
            var conn = ConnOf(cmd.Player);
            if (SocialFeature.PartyOf(conn) is not uint party) { await SocialFeature.Tell(cmd.Player, "你没有队伍。"); return; }
            await SocialFeature.LeaveParty(conn.Wire.Remote, conn, party);
        });
        module.OnChatCommand("kick", "/kick <character name> (party leader only: remove that member)", async cmd =>
        {
            if (cmd.Args.Count == 0) { await SocialFeature.Tell(cmd.Player, "用法：/kick <角色名>"); return; }
            await SocialFeature.KickMember(cmd.Player, ConnOf(cmd.Player), string.Join(' ', cmd.Args));
        });
        module.OnChatCommand("promote", "/promote <character name> (party leader only: make that member the leader)", async cmd =>
        {
            if (cmd.Args.Count == 0) { await SocialFeature.Tell(cmd.Player, "用法：/promote <角色名>"); return; }
            await SocialFeature.PromoteMember(cmd.Player, ConnOf(cmd.Player), string.Join(' ', cmd.Args));
        });
        module.OnChatCommand("decline", "/decline (refuse the party invitations waiting for you)", async cmd =>
        {
            int refused = await SocialFeature.DeclineInvites(ConnOf(cmd.Player));
            await SocialFeature.Tell(cmd.Player, refused == 0 ? "没有待接受的组队邀请。" : $"已拒绝 {refused} 个组队邀请。");
        });
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
            PackedClass(about), about.PortraitFrame, WornPortrait(about), AreaName(about)), $"0x00a7 known player {CharacterName(about)}");

    /// <summary>
    /// The area name the party panel shows for a player (the 0xa7 field at +0x90). Empty when the area is not in
    /// WorldAreas.json, which the client draws as "Travelling...".
    /// </summary>
    internal static string AreaName(ClientState conn) => conn.Layout.Id;   // "1_1_town": the client knows its own names

    // 0xb8 builds the trade window's two offer grids at once, even for the request and the waiting notice: their
    // inventories must exist (inventory 0 crashed both clients at 0x1402d1029 through the grid 0x140317a44). A fresh
    // pair also empties what an earlier trade left in them.
    internal static async Task FreshOffers(ClientState c)
    {
        foreach (var inv in new[] { PlayerTradeMine, PlayerTradeTheirs })
            await SendTo(c, InstanceInventories.AddInventory(inv, NpcShops.Offer, NpcShops.Manager), $"0x00c0 trade offer {inv}");
    }

    /// <summary>0xa9 member bytes (0x1421506d0 maps them: 0 → invited, 1 → member, 2 → leader as the handler uses
    /// them, anything else → none), as live 2026-10-29.</summary>
    internal static int _nextPartyId = 0x100;

    // C→S 0x93: "Invite to Party" with the other character's name. Both sides learn each other (0xa7); the invited player
    // gets a party list (0xa9) with the inviter and themself.
    internal static async Task<bool> InviteToParty(string remote, ClientState conn, string name)
    {
        // Anyone in game, wherever they are (the friends list invites by character or account name, from any area).
        var other = InGame.Keys.FirstOrDefault(o => o is { Outbox: not null, Closed: false, LeavingArea: false } && !ReferenceEquals(o, conn)
            && (string.Equals(CharacterName(o), name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(o.Account, name, StringComparison.OrdinalIgnoreCase)));
        if (other is null) { Console.WriteLine($"[gateway] {remote} <= 0x0093 party invite to \"{name}\": not in game"); return false; }
        string me = CharacterName(conn), them = CharacterName(other);
        await Introduce(conn, other);
        await Introduce(other, conn);
        // The client keeps the parties it knows by id (+0xc668): only an id it doesn't know yet shows the invite. It finds
        // the inviter by the second string (an account name, 0x142166a50) among the known players, and shows the notice
        // (0x140dd0ad0) only with +0x6c clear and +0x6d set.
        // An invite from a player already in a party joins that party: a fresh id per invite would leave the first pair
        // in one party and the third member in another.
        uint party = PartyOf(conn) ?? unchecked((uint)Interlocked.Increment(ref _nextPartyId));
        var info = InstanceSocial.PartyInfo(party, me, conn.Account ?? me, 0, 0, 1, 0, [(me, PartyInvited), (them, PartyInvited)]);
        Console.WriteLine($"[gateway] {remote} <= 0x0093 party invite: {me} -> {them} (party 0x{party:x})");
        PartyInvites[party] = (conn, other);
        await SendTo(other, info, $"0x00a9 party info for {them} (invited by {me})");
        return true;
    }

    /// <summary>Open invites by party id: the inviter (leader) and the invited player.</summary>
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, (ClientState Leader, ClientState Invited)> PartyInvites = new();

    // C→S 0x95: Accept on the invite. Both players get the party (0xa9) with themselves at their own index (+0x6e).
    internal static async Task AcceptParty(string remote, ClientState conn, uint party)
    {
        if (!PartyInvites.TryRemove(party, out var invite) || !ReferenceEquals(invite.Invited, conn))
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0095 accept party 0x{party:x}: no such invite");
            return;
        }
        var leader = invite.Leader;
        string lead = CharacterName(leader), member = CharacterName(conn);
        Console.WriteLine($"[gateway] {remote} <= 0x0095 accept party 0x{party:x}: {member} joins {lead}");
        // The party may already exist (a third member invited by someone who is in one): add to it, never replace it.
        if (Parties.TryGetValue(party, out var list)) { lock (list) if (!list.Contains(conn)) list.Add(conn); }
        else Parties[party] = [leader, conn];
        await SendParty(party);
    }

    /// <summary>
    /// The party panel asks for its members again (C→S 0xac): this player gets an 0xa7 for every member, with the area
    /// each is in, which is the column that otherwise stays "Travelling...".
    /// </summary>
    internal static async Task RefreshParty(string remote, ClientState conn)
    {
        if (PartyOf(conn) is not uint party || !Parties.TryGetValue(party, out var list))
        {
            Console.WriteLine($"[gateway] {remote} <= 0x00ac party refresh with no party");
            return;
        }
        ClientState[] members;
        lock (list) members = [.. list];
        foreach (var member in members) await Introduce(conn, member);
        Console.WriteLine($"[gateway] {remote} <= 0x00ac party refresh: {members.Length} member(s) sent");
    }

    /// <summary>The party this connection belongs to, or null.</summary>
    internal static uint? PartyOf(ClientState conn)
    {
        foreach (var (id, list) in Parties)
            lock (list) if (list.Contains(conn)) return id;
        return null;
    }

    /// <summary>A chat line back to the player who ran a command.</summary>
    internal static Task Tell(IPlayer player, string text) => Tell(ConnOf(player), text);

    /// <summary>A chat line to one connection.</summary>
    internal static Task Tell(ClientState conn, string text) =>
        SendTo(conn, InstanceChat.ChatLine(text), $"0x0017 {text}");

    /// <summary>
    /// '/decline': the invitations waiting for this connection are dropped. The inviter is told with 0xaa only when the
    /// party id belongs to that invitation alone — an invite from someone already in a party reuses their party id, and
    /// 0xaa on that id would remove the leader from their own party. Returns how many invitations were refused.
    /// </summary>
    internal static async Task<int> DeclineInvites(ClientState conn)
    {
        int refused = 0;
        foreach (var (party, invite) in PartyInvites)
        {
            if (!ReferenceEquals(invite.Invited, conn)) continue;
            if (!PartyInvites.TryRemove(party, out var gone)) continue;
            refused++;
            Console.WriteLine($"[gateway] {conn.Wire.Remote} /decline: party 0x{party:x} from {CharacterName(invite.Leader)}");
            // A party id that no party holds is this invitation's alone (see above).
            if (!Parties.ContainsKey(party) && invite.Leader.Outbox is { } outbox)
                _ = outbox(InstanceSocial.PartyRemove(party), $"0x00aa party 0x{party:x} removed (declined)");
        }
        return refused;
    }

    /// <summary>
    /// '/kick' from the leader: the member leaves the party (0xaa to them, the smaller party to the rest, or disband
    /// when only the leader is left). The same messages the client's own Leave sends, so nothing new is on the wire.
    /// </summary>
    internal static async Task KickMember(IPlayer actor, ClientState conn, string name)
    {
        var (ok, message) = await Kick(conn, name);
        if (!ok) await Tell(actor, message);
    }

    /// <summary>
    /// The kick itself, shared by '/kick' and the party panel's Kick button (C→S 0x94, a wstr member name — captured
    /// live 2026-11-08 from the panel, see Agents/docs/protocol/28-player-trade.md). Returns whether a member was
    /// removed and the line to show the leader.
    /// </summary>
    internal static async Task<(bool Ok, string Message)> Kick(ClientState conn, string name)
    {
        if (PartyOf(conn) is not uint party || !Parties.TryGetValue(party, out var list))
            return (false, "你没有队伍。");
        ClientState[] members;
        lock (list) members = [.. list];
        if (members.Length < 2 || !ReferenceEquals(members[0], conn))
            return (false, "只有队长可以踢人。");
        var target = members.FirstOrDefault(m => !ReferenceEquals(m, conn)
            && string.Equals(CharacterName(m), name, StringComparison.OrdinalIgnoreCase));
        if (target is null) return (false, $"队伍里没有 \"{name}\"。");
        lock (list) list.Remove(target);
        await SendTo(target, InstanceSocial.PartyRemove(party), $"0x00aa party 0x{party:x} removed (kicked)");
        await Program.SetPartySize(target, 1);
        Console.WriteLine($"[gateway] {conn.Wire.Remote} kick {name}: left party 0x{party:x}");
        await Tell(target, "你已被移出队伍。");
        if (list.Count >= 2) { await SendParty(party); return (true, $"已把 {name} 移出队伍。"); }
        Parties.TryRemove(party, out _);
        foreach (var member in members.Where(m => !ReferenceEquals(m, target)))
        {
            await SendTo(member, InstanceSocial.PartyRemove(party), $"0x00aa party 0x{party:x} removed (disbanded)");
            await Program.SetPartySize(member, 1);
        }
        return (true, $"已把 {name} 移出队伍（队伍不足两人，已解散）。");
    }

    /// <summary>
    /// '/promote' from the leader: that member becomes the leader — the party list (0xa9) carries the leader as its
    /// first entry with status 2, so reordering the list and resending it is the whole transfer.
    /// </summary>
    internal static async Task PromoteMember(IPlayer actor, ClientState conn, string name)
    {
        if (PartyOf(conn) is not uint party || !Parties.TryGetValue(party, out var list))
        {
            await Tell(actor, "你没有队伍。");
            return;
        }
        ClientState[] members;
        lock (list) members = [.. list];
        if (members.Length < 2 || !ReferenceEquals(members[0], conn))
        {
            await Tell(actor, "只有队长可以转移队长。");
            return;
        }
        var target = members.FirstOrDefault(m => !ReferenceEquals(m, conn)
            && string.Equals(CharacterName(m), name, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            await Tell(actor, $"队伍里没有 \"{name}\"。");
            return;
        }
        lock (list)
        {
            list.Remove(target);
            list.Insert(0, target);
        }
        Console.WriteLine($"[gateway] {conn.Wire.Remote} /promote {name}: leader of party 0x{party:x}");
        await SendParty(party);
        await Tell(actor, $"已把队长转给 {name}。");
    }

    /// <summary>
    /// A '%' line (party chat): the text reaches every member of the player's party, its author included, as an S→C
    /// 0x17 line from the character. Without a party the line goes nowhere (the client shows nothing either).
    /// </summary>
    internal static async Task PartyChat(string remote, ClientState conn, string typed)
    {
        var (channel, text) = InstanceChat.SplitChannel(typed);
        if (channel != TypedChannel.Party || string.IsNullOrWhiteSpace(text)) return;
        if (PartyOf(conn) is not uint party || !Parties.TryGetValue(party, out var list))
        {
            Console.WriteLine($"[gateway] {remote} party chat with no party: \"{text}\"");
            return;
        }
        ClientState[] members;
        lock (list) members = [.. list];
        byte[] line = InstanceChat.ChatLine(text, CharacterName(conn));
        foreach (var member in members)
        {
            if (member.Outbox is not { } outbox) continue;
            _ = outbox((byte[])line.Clone(), null);
        }
        Console.WriteLine($"[gateway] {remote} party chat to {members.Length} member(s) in party 0x{party:x}: \"{text}\"");
    }

    /// <summary>The parties formed here by id: the leader first, then the members in joining order.</summary>
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, List<ClientState>> Parties = new();

    // The party (0xa9) to each member, with that member's own index (+0x6e). Members are listed by account name, the key
    // of the known-player table (0xa7) the client resolves them in, and every member, the player too, must be in it:
    // without the player's own entry the joining client crashed building its party panel (live 2026-10-29).
    internal static async Task SendParty(uint party)
    {
        if (!Parties.TryGetValue(party, out var list)) return;
        ClientState[] members;
        lock (list) members = [.. list];
        var leader = members[0];
        string lead = CharacterName(leader);
        (string, byte)[] entries = [.. members.Select((m, i) => (m.Account ?? CharacterName(m), i == 0 ? PartyLeader : PartyMember))];
        for (int i = 0; i < members.Length; i++)
        {
            var to = members[i];
            await Program.SetPartySize(to, members.Length);
            foreach (var m in members) await Introduce(to, m);
            // +0x6c/.6d/.6f stay 0 in a party message. Checked live 2026-11-08: with +0x6f = 1 the client dropped its
            // instance connection right after the party message, so that byte is not "the member's area".
            await SendTo(to, InstanceSocial.PartyInfo(party, lead, leader.Account ?? lead, (byte)i, 0, 0, 0, entries),
                $"0x00a9 party 0x{party:x} for {CharacterName(to)}: {string.Join(", ", members.Select(CharacterName))}");
        }
    }

    // An area change is a new connection of the same character: it takes the old one's place in its party, and every
    // member gets the party again (the new client has none).
    internal static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ClientState, object?> Rebound = new();

    internal static async Task RebindParty(ClientState conn)
    {
        string name = CharacterName(conn);
        foreach (var (party, list) in Parties)
        {
            bool found = false;
            lock (list)
                for (int i = 0; i < list.Count; i++)
                    if (!ReferenceEquals(list[i], conn) && string.Equals(CharacterName(list[i]), name, StringComparison.Ordinal))
                        (list[i], found) = (conn, true);
            if (!found) continue;
            Console.WriteLine($"[gateway] {conn.Wire.Remote} {name} back in party 0x{party:x} after an area change");
            await SendParty(party);
        }
    }

    // A transfer without a chosen instance goes to a party member's instance of that area, when one is there.
    internal static ulong PartyInstance(ClientState conn, PendingTransfer t)
    {
        foreach (var list in Parties.Values)
        {
            ClientState[] members;
            lock (list) members = [.. list];
            if (!members.Contains(conn)) continue;
            foreach (var m in members)
            {
                if (ReferenceEquals(m, conn)) continue;
                // On the way there (both went at once): where Auth is sending them.
                if (m.TransferredTo is { } going && going.Area == t.Area) return going.Instance;
                if (!m.Closed && !m.LeavingArea && m.Layout.Hash == t.Area && InGame.ContainsKey(m)) return m.InstanceNumber;
            }
        }
        return 0;
    }

    // C→S 0x96: "Leave Party". The player gets the party removed (0xaa); with fewer than two left the party is disbanded
    // for everyone, otherwise the others get the smaller party (the next member leads when the leader leaves).
    internal static async Task<bool> LeaveParty(string remote, ClientState conn, uint party)
    {
        if (!Parties.TryGetValue(party, out var list)) { Console.WriteLine($"[gateway] {remote} <= 0x0096 leave party 0x{party:x}: no such party"); return false; }
        ClientState[] left;
        lock (list) { list.Remove(conn); left = [.. list]; }
        Console.WriteLine($"[gateway] {remote} <= 0x0096 {CharacterName(conn)} leaves party 0x{party:x}: {left.Length} left");
        await SendTo(conn, InstanceSocial.PartyRemove(party), $"0x00aa party 0x{party:x} removed (left)");
        await Program.SetPartySize(conn, 1);
        if (left.Length >= 2) { await SendParty(party); return true; }
        Parties.TryRemove(party, out _);
        foreach (var m in left)
        {
            await SendTo(m, InstanceSocial.PartyRemove(party), $"0x00aa party 0x{party:x} removed (disbanded)");
            await Program.SetPartySize(m, 1);
        }
        return true;
    }

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
