using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GameServer.Core.Accounts;
using GameServer.Core.Chat;
using GameServer.Core.Protocol.Instance;
using GameServer.Storage;
using static Program;

namespace GameServer.Modules.Social;

/// <summary>Directed account friendships, persistent ignore lists, account status messages, hideout invites and duel
/// challenges.</summary>
static class FriendsFeature
{
    const string StatusDataKey = "social.status";
    const string IgnoreDataKey = "social.ignore";
    const int MaxStatusLength = 200;
    const int MaxIgnoreEntries = 100;
    static readonly TimeSpan HideoutInviteTtl = TimeSpan.FromMinutes(30);

    sealed class SocialDbCache
    {
        public ConcurrentDictionary<string, string> Status { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, HashSet<string>> Ignores { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, object> AccountGates { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    static readonly ConditionalWeakTable<SaveDatabase, SocialDbCache> DbCaches = new();
    static SocialDbCache Cache => DbCaches.GetOrCreateValue(Saves.Db);

    static readonly ConcurrentDictionary<string, Player> Online = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, long> HideoutInvites = new(StringComparer.OrdinalIgnoreCase);
    static SqliteAccountStore Accounts => new(Saves.Db);
    static SqliteFriendStore Store => new(Saves.Db);

    static void Register(Player c)
    {
        if (c.Character.Account is { } account && !c.Closed && !c.LeavingArea) Online[account] = c;
    }

    // Existing players can use a newly reloaded Social module without re-entering their area.
    static void Discover(Player c)
    {
        Register(c);
        foreach (var peer in c.Instance.Presence.PlayersExcept(0))
            if (peer is { } owner) Register(owner);
    }

    static Player? Changing(string name) =>
        Moving.TryGetValue(name, out var m) && DateTime.UtcNow - m.Since < MovingGrace ? m.From : null;

    static Player? Connected(string name) => Online.TryGetValue(name, out var c) && !c.Closed && !c.LeavingArea ? c : null;

    static Player? FindOnline(string name) =>
        InGame.Keys.FirstOrDefault(c => !c.Controlled && !c.Closed && !c.LeavingArea
            && (string.Equals(CharacterName(c), name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Character.Account, name, StringComparison.OrdinalIgnoreCase)));

    static void PruneExpiredHideoutInvites(long nowTicks)
    {
        foreach (var kv in HideoutInvites)
            if (kv.Value <= nowTicks) HideoutInvites.TryRemove(kv.Key, out _);
    }

    static void RemoveHideoutInvite(string ownerKey, string guestKey) =>
        HideoutInvites.TryRemove($"{ownerKey}\0{guestKey}", out _);

    static void ClearHideoutInvitesFor(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        foreach (var entryKey in HideoutInvites.Keys)
        {
            int sep = entryKey.IndexOf('\0');
            if (sep <= 0) continue;
            if (entryKey.AsSpan(0, sep).Equals(key, StringComparison.OrdinalIgnoreCase)
                || entryKey.AsSpan(sep + 1).Equals(key, StringComparison.OrdinalIgnoreCase))
                HideoutInvites.TryRemove(entryKey, out _);
        }
    }

    static void EvictAccountCaches(string accountName, string? characterName = null)
    {
        var cache = Cache;
        cache.Status.TryRemove(accountName, out _);
        cache.Ignores.TryRemove(accountName, out _);
        cache.AccountGates.TryRemove(accountName, out _);
        ClearHideoutInvitesFor(accountName);
        if (!string.IsNullOrWhiteSpace(characterName)
            && !string.Equals(characterName, accountName, StringComparison.OrdinalIgnoreCase))
            ClearHideoutInvitesFor(characterName);
    }

    public static string StatusOf(string? account)
    {
        if (string.IsNullOrWhiteSpace(account)) return "";
        var statusCache = Cache.Status;
        if (statusCache.TryGetValue(account, out var cached)) return cached;
        string saved = ChatWire.Clean(AccountStash.For(account, Items).ModuleData(StatusDataKey)).Trim();
        if (saved.Length > MaxStatusLength) saved = saved[..MaxStatusLength];
        statusCache[account] = saved;
        return saved;
    }

    static HashSet<string> IgnoredSet(string account)
    {
        var ignoreCache = Cache.Ignores;
        if (ignoreCache.TryGetValue(account, out var cached)) return cached;
        var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (AccountStash.For(account, Items).ModuleData(IgnoreDataKey) is { Length: > 0 } json)
        {
            try
            {
                foreach (var entry in JsonSerializer.Deserialize<List<string>>(json) ?? [])
                    if (!string.IsNullOrWhiteSpace(entry) && loaded.Count < MaxIgnoreEntries) loaded.Add(entry);
            }
            catch (JsonException) { }
        }
        return ignoreCache.GetOrAdd(account, loaded);
    }

    /// <summary>True when <paramref name="viewerAccount"/> has ignored the account identified by
    /// <paramref name="senderAccountOrCharacter"/> (direct account match or locally online/transferring character).
    /// Avoids synchronous SQLite queries on hot chat paths for unknown/remote speakers.</summary>
    public static bool IsIgnoring(string? viewerAccount, string? senderAccountOrCharacter)
    {
        if (string.IsNullOrWhiteSpace(viewerAccount) || string.IsNullOrWhiteSpace(senderAccountOrCharacter))
            return false;
        var ignored = IgnoredSet(viewerAccount);
        lock (ignored)
        {
            if (ignored.Count == 0) return false;
            if (ignored.Contains(senderAccountOrCharacter)) return true;
        }
        var online = FindOnline(senderAccountOrCharacter);
        if (online?.Character.Account is { } onlineAcc)
            lock (ignored) return ignored.Contains(onlineAcc);
        DateTime now = DateTime.UtcNow;
        foreach (var kv in Moving)
        {
            if (now - kv.Value.Since < MovingGrace
                && (string.Equals(kv.Key, senderAccountOrCharacter, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(CharacterName(kv.Value.From), senderAccountOrCharacter, StringComparison.OrdinalIgnoreCase)))
                lock (ignored) return ignored.Contains(kv.Key);
        }
        return false;
    }

    // Online, or between two connections of an area change (as the connection it left had them). The third field is the
    // area the friend is in, a WorldAreas HASH16 (+0xa8): the friends' and party rows draw it as the area's name and show
    // "Travelling..." for anything that is not an area (#715).
    static byte[] Details(Account a)
    {
        string status = StatusOf(a.Name);
        return (Connected(a.Name) ?? Changing(a.Name)) is { } c
            ? InstanceSocial.KnownPlayer(a.Name, CharacterName(c), c.Layout.Hash, PackedClass(c), c.Character.PortraitFrame, WornPortrait(c), status)
            : InstanceSocial.OfflinePlayer(a.Name, status);
    }

    public static IEnumerable<(string Log, byte[] Message)> Enter(Player c)
    {
        Discover(c);
        if (c.Character.Account is not { } name || Accounts.FindAccount(name) is not { } me) yield break;
        var store = Store;
        var friends = store.Friends(me.Id).ToHashSet();
        foreach (var id in friends.Union(store.Incoming(me.Id)))
        {
            if (Accounts.GetAccount(id) is not { } account) continue;
            bool accepted = friends.Contains(account.Id);
            if (!accepted && !store.Friends(account.Id).Contains(me.Id)) continue;
            yield return ($"0x00a7 friend details {account.Name}", Details(account));
            yield return ($"0x00c9 friend relation {account.Name}", InstanceSocial.FriendRelation(account.Name, accepted));
        }
        // Presence updates are sent after this entry's own messages by the ordinary packet/leave paths as well. After an
        // area change the friends had no offline notice, so they need no online one either.
        if (Moving.TryRemove(name, out var moved) && DateTime.UtcNow - moved.Since < MovingGrace) yield break;
        foreach (var other in Online.Values.Where(o => !ReferenceEquals(o, c) && !o.Closed && !o.LeavingArea))
            _ = RefreshSafely(other, me);
    }

    // The entry's list (Enter) is built when the entry starts and sent with its messages, seconds later in a hideout: a
    // friend who came online in between was listed offline after their own online notice had arrived, and stayed so
    // (live 2026-11-05: admin@admin.ru online at 00:40:44.180, then offline from test1's entry list at .186). On the
    // player's first tick after the entry (SocialModule), the friends' presence as it is then.
    public static async Task AfterEntry(Player c)
    {
        if (c.Closed || c.LeavingArea || c.Character.Account is not { } name || Accounts.FindAccount(name) is not { } me) return;
        Register(c);
        foreach (var id in Store.Friends(me.Id))
            if (Accounts.GetAccount(id) is { } friend) await RefreshSafely(c, friend);
        // And every friend in game, wherever they are, gets this player's presence now (an entry is a status update for
        // them too, the leave its other one: Leaving).
        foreach (var other in Online.Values.Where(o => !ReferenceEquals(o, c) && !o.Closed && !o.LeavingArea).ToList())
            await RefreshSafely(other, me);
    }

    static async Task RefreshSafely(Player viewer, Account about, bool removal = false)
    {
        try { await Refresh(viewer, about, removal); }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        { Console.WriteLine($"[gateway] friend presence delivery to {viewer.Character.Account}: {e.Message}"); }
    }

    static async Task Refresh(Player viewer, Account about, bool removal = false)
    {
        if (viewer.Closed || viewer.LeavingArea || viewer.Character.Account is not { } name || Accounts.FindAccount(name) is not { } me) return;
        var store = Store;
        bool accepted = store.Friends(me.Id).Contains(about.Id);
        bool incoming = store.Friends(about.Id).Contains(me.Id);
        if (!accepted && !incoming && !removal) return;
        if (accepted || incoming) await SocialFeature.SendTo(viewer, Details(about), $"0x00a7 friend details {about.Name}");
        await SocialFeature.SendTo(viewer, InstanceSocial.FriendRelation(about.Name, accepted, !accepted && !incoming),
            $"0x00c9 friend {about.Name}: {(accepted ? "accepted" : incoming ? "invited" : "removed")}");
    }

    /// <summary>C→S 0x00ca: update the account's social status message and broadcast updated 0xa7 metadata to self,
    /// online friends and party members.</summary>
    public static async Task SetStatus(Player c, string rawStatus)
    {
        Discover(c);
        if (c.Character.Account is not { } name || Accounts.FindAccount(name) is not { } me) return;
        string status = ChatWire.Clean(rawStatus).Trim();
        if (status.Length > MaxStatusLength) status = status[..MaxStatusLength];
        var cache = Cache;
        lock (cache.AccountGates.GetOrAdd(me.Name, _ => new object()))
        {
            Saves.Atomically(() =>
            {
                Saves.Db.OnRollback(() => cache.Status.TryRemove(me.Name, out _));
                AccountStash.For(me.Name, Items).ChangeModuleData(StatusDataKey, _ => status);
            });
            cache.Status[me.Name] = status;
        }
        Console.WriteLine($"[gateway] <= 0x00ca social status from {me.Name}: \"{status}\"");
        await SocialFeature.Introduce(c, c);
        foreach (var other in Online.Values.Where(o => !ReferenceEquals(o, c) && !o.Closed && !o.LeavingArea).ToList())
            await RefreshSafely(other, me);
        uint party = PartyRoster.PartyOf(c);
        if (party != 0)
            foreach (var member in PartyRoster.Players(party).Where(m => !ReferenceEquals(m, c) && !m.Closed && !m.LeavingArea))
                try { await SocialFeature.Introduce(member, c); }
                catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }

    public static async Task Change(Player c, (string Name, ushort U16) request)
    {
        Discover(c);
        if (c.Character.Account is not { } name || Accounts.FindAccount(name) is not { } me) return;
        Console.WriteLine($"[gateway] <= 0x0080 social list action 0x{request.U16:x4} from {me.Name}");
        // Wire byte 0 selects the list (0 friends, 1 ignore); byte 1 is add 0, remove/decline 1, accept 2.
        byte list = (byte)(request.U16 & 0xff);
        byte action = (byte)(request.U16 >> 8);
        if (list == 1)
        {
            await ChangeIgnore(c, me, request.Name, action);
            return;
        }
        if (list != 0 || action is not (0 or 1 or 2)) return;
        string target = request.Name.Trim();
        var accounts = Accounts;
        var character = accounts.FindCharacter(target);
        var friend = action != 0
            ? accounts.FindAccount(target) ?? (character is null ? null : accounts.GetAccount(character.AccountId))
            : (character is null ? null : accounts.GetAccount(character.AccountId)) ?? accounts.FindAccount(target);
        if (friend is null || friend.Id == me.Id)
        {
            if (c.Commands is { } commands) await commands.Reply(friend is null ? "Player not found." : "You cannot add yourself as a friend.");
            return;
        }
        var store = Store;
        if (action == 2 && !store.Incoming(me.Id).Contains(friend.Id)) return;
        bool add = action is 0 or 2;
        if (add) store.Add(me.Id, friend.Id);
        else store.Remove(me.Id, friend.Id);
        Console.WriteLine($"[gateway] friends: {me.Name} {(add ? "added" : "removed")} {friend.Name}");
        await Refresh(c, friend, removal: !add);
        if (Connected(friend.Name) is { } other) await RefreshSafely(other, me, removal: !add);
    }

    static async Task ChangeIgnore(Player c, Account me, string rawTarget, byte action)
    {
        if (action is not (0 or 1)) return;
        string target = rawTarget.Trim();
        var accounts = Accounts;
        var character = accounts.FindCharacter(target);
        var other = action != 0
            ? accounts.FindAccount(target) ?? (character is null ? null : accounts.GetAccount(character.AccountId))
            : (character is null ? null : accounts.GetAccount(character.AccountId)) ?? accounts.FindAccount(target);
        if (other is null || other.Id == me.Id)
        {
            if (c.Commands is { } commands)
                await commands.Reply(other is null ? "Player not found." : "You cannot ignore yourself.");
            return;
        }
        bool add = action == 0;
        var cache = Cache;
        bool changed = false, full = false;
        lock (cache.AccountGates.GetOrAdd(me.Name, _ => new object()))
        {
            var ignored = IgnoredSet(me.Name);
            lock (ignored)
            {
                if (add)
                {
                    if (ignored.Contains(other.Name)) changed = false;
                    else if (ignored.Count >= MaxIgnoreEntries) full = true;
                    else changed = true;
                }
                else changed = ignored.Contains(other.Name);
            }
            if (changed)
            {
                Saves.Atomically(() =>
                {
                    string[] snapshot;
                    lock (ignored)
                    {
                        if (add) ignored.Add(other.Name);
                        else ignored.Remove(other.Name);
                        snapshot = [.. ignored.Order(StringComparer.OrdinalIgnoreCase)];
                    }
                    Saves.Db.OnRollback(() => cache.Ignores.TryRemove(me.Name, out _));
                    AccountStash.For(me.Name, Items).ChangeModuleData(IgnoreDataKey,
                        _ => JsonSerializer.Serialize(snapshot));
                });
                if (add)
                {
                    RemoveHideoutInvite(me.Name, other.Name);
                    RemoveHideoutInvite(other.Name, me.Name);
                }
            }
        }
        if (full)
        {
            if (c.Commands is { } limitReply) await limitReply.Reply($"Your ignore list is full (maximum {MaxIgnoreEntries}).");
            return;
        }
        Console.WriteLine($"[gateway] ignore: {me.Name} {(add ? "added" : "removed")} {other.Name}");
        if (c.Commands is { } reply)
            await reply.Reply(add
                ? changed ? $"Added {other.Name} to your ignore list." : $"{other.Name} is already on your ignore list."
                : changed ? $"Removed {other.Name} from your ignore list." : $"{other.Name} is not on your ignore list.");
    }

    /// <summary>C→S 0x0179: invite an online player to visit this player's hideout and record access permission for
    /// C→S 0x017a.</summary>
    public static async Task InviteToHideout(Player player, string rawTarget)
    {
        Discover(player);
        string targetName = rawTarget.Trim();
        var target = FindOnline(targetName);
        if (target is null)
        {
            if (player.Commands is { } reply) await reply.Reply("Player not found.");
            return;
        }
        if (ReferenceEquals(target, player)
            || string.Equals(CharacterName(target), CharacterName(player), StringComparison.OrdinalIgnoreCase))
        {
            if (player.Commands is { } reply) await reply.Reply("You cannot invite yourself to your hideout.");
            return;
        }
        bool compatible = !player.Character.Ssf && !target.Character.Ssf
            && player.Character.Hardcore == target.Character.Hardcore && player.Character.LeagueForm == target.Character.LeagueForm
            && string.Equals(player.Character.League, target.Character.League, StringComparison.OrdinalIgnoreCase);
        if (!compatible)
        {
            if (player.Commands is { } reply) await reply.Reply("You cannot invite that player to your hideout.");
            return;
        }
        if (IsIgnoring(target.Character.Account, player.Character.Account ?? CharacterName(player))) return;
        string ownerKey = player.Character.Account ?? CharacterName(player);
        string guestKey = target.Character.Account ?? CharacterName(target);
        long nowTicks = DateTime.UtcNow.Ticks;
        PruneExpiredHideoutInvites(nowTicks);
        HideoutInvites[$"{ownerKey}\0{guestKey}"] = nowTicks + HideoutInviteTtl.Ticks;
        Console.WriteLine($"[gateway] <= 0x0179 hideout invite: {CharacterName(player)} -> {CharacterName(target)}");
        try
        {
            await SocialFeature.Introduce(target, player);
            await SocialFeature.SendTo(target, InstanceChat.ChatLine($"{CharacterName(player)} has invited you to visit their hideout."),
                $"0x0017 hideout invite from {CharacterName(player)}");
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        if (player.Commands is { } confirm) await confirm.Reply($"Invited {CharacterName(target)} to your hideout.");
    }

    /// <summary>Hideout visit permission check for C→S 0x017a: blocked if owner ignores visitor; otherwise same party,
    /// accepted friend, or unexpired invite via 0x0179.</summary>
    public static bool CanVisitHideout(Player visitor, Player owner)
    {
        if (IsIgnoring(owner.Character.Account, visitor.Character.Account ?? CharacterName(visitor)))
            return false;
        if (PartyRoster.SameParty(visitor, owner)) return true;
        string ownerKey = owner.Character.Account ?? CharacterName(owner);
        string guestKey = visitor.Character.Account ?? CharacterName(visitor);
        string inviteKey = $"{ownerKey}\0{guestKey}";
        if (HideoutInvites.TryGetValue(inviteKey, out long expiresAt))
        {
            if (DateTime.UtcNow.Ticks < expiresAt) return true;
            HideoutInvites.TryRemove(inviteKey, out _);
        }
        if (visitor.Character.Account is { } vAcc && owner.Character.Account is { } oAcc)
        {
            var accounts = Accounts;
            if (accounts.FindAccount(vAcc) is { } v && accounts.FindAccount(oAcc) is { } o)
                return Store.Friends(o.Id).Contains(v.Id);
        }
        return false;
    }

    /// <summary>C→S 0x0155: challenge another online player to a duel, forwarding S→C 0x0157.</summary>
    public static async Task ChallengeDuel(Player player, string rawTarget)
    {
        Discover(player);
        string targetName = rawTarget.Trim();
        var target = FindOnline(targetName);
        if (target is null)
        {
            if (player.Commands is { } reply) await reply.Reply("Player not found.");
            return;
        }
        if (ReferenceEquals(target, player)
            || string.Equals(CharacterName(target), CharacterName(player), StringComparison.OrdinalIgnoreCase))
        {
            if (player.Commands is { } reply) await reply.Reply("You cannot challenge yourself to a duel.");
            return;
        }
        if (IsIgnoring(target.Character.Account, player.Character.Account ?? CharacterName(player))) return;
        Console.WriteLine($"[gateway] <= 0x0155 duel challenge: {CharacterName(player)} -> {CharacterName(target)}");
        try
        {
            await SocialFeature.SendTo(target, InstanceSocial.DuelChallengeNotify(CharacterName(player)),
                $"0x0157 duel challenge from {CharacterName(player)}");
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        if (player.Commands is { } confirm) await confirm.Reply($"Challenged {CharacterName(target)} to a duel.");
    }

    public static async Task Leaving(Player c)
    {
        if (c.Character.Account is not { } name || !Online.TryRemove(new KeyValuePair<string, Player>(name, c))) return;
        if (Accounts.FindAccount(name) is not { } me)
        {
            if (!c.LeavingArea && !c.TransferAccepted) EvictAccountCaches(name, CharacterName(c));
            return;
        }
        // An area change: the player is still online, on the next connection. The friends hear nothing of it (an offline
        // and an online notice on every area change, live 2026-11-05); a player who does not come back is told offline.
        if (c.LeavingArea || c.TransferAccepted)
        {
            Moving[name] = (DateTime.UtcNow, c);
            _ = OfflineIfNotBack(name, me, CharacterName(c));
            return;
        }
        EvictAccountCaches(name, CharacterName(c));
        foreach (var other in Online.Values) await RefreshSafely(other, me);
    }

    // Accounts between two connections of an area change, since when.
    static readonly ConcurrentDictionary<string, (DateTime Since, Player From)> Moving = new(StringComparer.OrdinalIgnoreCase);
    static readonly TimeSpan MovingGrace = TimeSpan.FromSeconds(60);

    static async Task OfflineIfNotBack(string name, Account me, string characterName)
    {
        await Task.Delay(MovingGrace);
        if (!Moving.TryGetValue(name, out var moving) || DateTime.UtcNow - moving.Since < MovingGrace || Connected(name) is not null) return;
        Moving.TryRemove(name, out _);
        EvictAccountCaches(name, characterName);
        foreach (var other in Online.Values) await RefreshSafely(other, me);
    }
}
