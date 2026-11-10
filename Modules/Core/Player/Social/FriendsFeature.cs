using System.Collections.Concurrent;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Storage;
using static Program;

namespace GameServer.Modules.Social;

/// <summary>Directed account friendships: an add also gives the other account an invitation to add back.</summary>
static class FriendsFeature
{
    static readonly ConcurrentDictionary<string, ClientState> Online = new(StringComparer.OrdinalIgnoreCase);
    static SqliteAccountStore Accounts => new(Saves.Db);
    static SqliteFriendStore Store => new(Saves.Db);

    static void Register(ClientState c)
    {
        if (c.Account is { } account && !c.Closed && !c.LeavingArea) Online[account] = c;
    }

    // Existing players can use a newly reloaded Social module without re-entering their area.
    static void Discover(ClientState c)
    {
        Register(c);
        foreach (var peer in c.Instance.Presence.PlayersExcept(0))
            if (peer.Owner is { } owner) Register(owner);
    }

    static ClientState? Changing(string name) =>
        Moving.TryGetValue(name, out var m) && DateTime.UtcNow - m.Since < MovingGrace ? m.From : null;

    static ClientState? Connected(string name) => Online.TryGetValue(name, out var c) && !c.Closed && !c.LeavingArea ? c : null;
    // Online, or between two connections of an area change (as the connection it left had them).
    static byte[] Details(Account a) => (Connected(a.Name) ?? Changing(a.Name)) is { } c
        ? InstanceSocial.KnownPlayer(a.Name, CharacterName(c), c.Pools.Level, PackedClass(c), c.PortraitFrame, WornPortrait(c), c.Layout.Hash)
        : InstanceSocial.OfflinePlayer(a.Name);

    public static IEnumerable<(string Log, byte[] Message)> Enter(ClientState c)
    {
        Discover(c);
        if (c.Account is not { } name || Accounts.FindAccount(name) is not { } me) yield break;
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
    public static async Task AfterEntry(ClientState c)
    {
        if (c.Closed || c.LeavingArea || c.Account is not { } name || Accounts.FindAccount(name) is not { } me) return;
        Register(c);
        foreach (var id in Store.Friends(me.Id))
            if (Accounts.GetAccount(id) is { } friend) await RefreshSafely(c, friend);
        // And every friend in game, wherever they are, gets this player's presence now (an entry is a status update for
        // them too, the leave its other one: Leaving).
        foreach (var other in Online.Values.Where(o => !ReferenceEquals(o, c) && !o.Closed && !o.LeavingArea).ToList())
            await RefreshSafely(other, me);
    }

    static async Task RefreshSafely(ClientState viewer, Account about, bool removal = false)
    {
        try { await Refresh(viewer, about, removal); }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        { Console.WriteLine($"[gateway] friend presence delivery to {viewer.Account}: {e.Message}"); }
    }

    static async Task Refresh(ClientState viewer, Account about, bool removal = false)
    {
        if (viewer.Closed || viewer.LeavingArea || viewer.Account is not { } name || Accounts.FindAccount(name) is not { } me) return;
        var store = Store;
        bool accepted = store.Friends(me.Id).Contains(about.Id);
        bool incoming = store.Friends(about.Id).Contains(me.Id);
        if (!accepted && !incoming && !removal) return;
        if (accepted || incoming) await SocialFeature.SendTo(viewer, Details(about), $"0x00a7 friend details {about.Name}");
        await SocialFeature.SendTo(viewer, InstanceSocial.FriendRelation(about.Name, accepted, !accepted && !incoming),
            $"0x00c9 friend {about.Name}: {(accepted ? "accepted" : incoming ? "invited" : "removed")}");
    }

    public static async Task Change(ClientState c, (string Name, ushort U16) request)
    {
        Discover(c);
        if (c.Account is not { } name || Accounts.FindAccount(name) is not { } me) return;
        Console.WriteLine($"[gateway] <= 0x0080 friend action 0x{request.U16:x4} from {me.Name}");
        // Wire byte 0 selects the list (0 friends, 1 ignore); byte 1 is add 0, remove/decline 1, accept 2.
        if (request.U16 is not (0 or 0x100 or 0x200)) return;
        string target = request.Name.Trim();
        var accounts = Accounts;
        var character = accounts.FindCharacter(target);
        var friend = request.U16 != 0
            ? accounts.FindAccount(target) ?? (character is null ? null : accounts.GetAccount(character.AccountId))
            : (character is null ? null : accounts.GetAccount(character.AccountId)) ?? accounts.FindAccount(target);
        if (friend is null || friend.Id == me.Id)
        {
            if (c.Commands is { } commands) await commands.Reply(friend is null ? "Player not found." : "You cannot add yourself as a friend.");
            return;
        }
        var store = Store;
        if (request.U16 == 0x200 && !store.Incoming(me.Id).Contains(friend.Id)) return;
        bool add = request.U16 is 0 or 0x200;
        if (add) store.Add(me.Id, friend.Id);
        else store.Remove(me.Id, friend.Id);
        Console.WriteLine($"[gateway] friends: {me.Name} {(add ? "added" : "removed")} {friend.Name}");
        await Refresh(c, friend, removal: !add);
        if (Connected(friend.Name) is { } other) await RefreshSafely(other, me, removal: !add);
    }

    public static async Task Leaving(ClientState c)
    {
        if (c.Account is not { } name || !Online.TryRemove(new KeyValuePair<string, ClientState>(name, c))) return;
        if (Accounts.FindAccount(name) is not { } me) return;
        // An area change: the player is still online, on the next connection. The friends hear nothing of it (an offline
        // and an online notice on every area change, live 2026-11-05); a player who does not come back is told offline.
        if (c.LeavingArea || c.TransferAccepted)
        {
            Moving[name] = (DateTime.UtcNow, c);
            _ = OfflineIfNotBack(name, me);
            return;
        }
        foreach (var other in Online.Values) await RefreshSafely(other, me);
    }

    // Accounts between two connections of an area change, since when.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Since, ClientState From)> Moving = new(StringComparer.OrdinalIgnoreCase);
    static readonly TimeSpan MovingGrace = TimeSpan.FromSeconds(60);

    static async Task OfflineIfNotBack(string name, Account me)
    {
        await Task.Delay(MovingGrace);
        if (!Moving.TryGetValue(name, out var moving) || DateTime.UtcNow - moving.Since < MovingGrace || Connected(name) is not null) return;
        Moving.TryRemove(name, out _);
        foreach (var other in Online.Values) await RefreshSafely(other, me);
    }
}
