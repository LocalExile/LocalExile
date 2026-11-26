using GameServer.Core.Chat;
using GameServer.Core.Protocol.Instance;
using static Program;

namespace GameServer.Modules.Social;

/// <summary>A connected party's additional actor, supplied by another module. No controller or AI is retained here.</summary>
public sealed record PartyCompanion(string Account, string Character, byte[] KnownPlayer);

/// <summary>One authoritative party for socket players and module-owned actors. The first human leads.</summary>
public static class PartyRoster
{
    public const int Capacity = 6;
    const int MaxDescriptionLength = 120;
    static readonly TimeSpan TransferGrace = TimeSpan.FromSeconds(60);
    // BCL-only values survive a collectible Social reload without retaining the old module's types.
    static T Keep<T>(string name, Func<T> create) where T : class
    {
        string key = "LocalExile.Social." + name;
        if (AppContext.GetData(key) is T old) return old;
        var value = create();
        AppContext.SetData(key, value);
        return value;
    }
    static readonly object Gate = Keep("Gate", () => new object());
    static readonly SemaphoreSlim Output = Keep("Output", () => new SemaphoreSlim(1));
    static readonly Dictionary<uint, List<Player>> Humans = Keep("Humans", () => new Dictionary<uint, List<Player>>());
    static readonly Dictionary<uint, List<(string Account, string Character, byte[] Known)>> Companions =
        Keep("Companions", () => new Dictionary<uint, List<(string, string, byte[])>>());
    static readonly Dictionary<(uint Party, string Character), (Player From, Player To)> Invites =
        Keep("Invites", () => new Dictionary<(uint, string), (Player, Player)>());
    static readonly Dictionary<uint, HashSet<Player>> Removed = Keep("Removed", () => new Dictionary<uint, HashSet<Player>>());
    static readonly Dictionary<uint, string> PublicDescriptions = Keep("PublicDescriptions", () => new Dictionary<uint, string>());
    static uint nextId = 0x100;

    static bool Same(Player a, Player b) => ReferenceEquals(a, b)
        || a.Character.Account is not null && string.Equals(a.Character.Account, b.Character.Account, StringComparison.OrdinalIgnoreCase)
            && string.Equals(CharacterName(a), CharacterName(b), StringComparison.Ordinal);
    static bool Available(Player c) => c.Listed && !c.Closed && !c.LeavingArea && !c.Character.Ssf;
    static bool SameLeague(Player a, Player b) => a.Character.Hardcore == b.Character.Hardcore && a.Character.LeagueForm == b.Character.LeagueForm
        && string.Equals(a.Character.League, b.Character.League, StringComparison.OrdinalIgnoreCase);
    static bool Compatible(Player a, Player b) => Available(a) && Available(b) && SameLeague(a, b);
    static uint Find(Player c) => Humans.FirstOrDefault(p => p.Value.Any(m => Same(m, c))).Key;
    static int Count(uint id) => (Humans.GetValueOrDefault(id)?.Count ?? 0) + (Companions.GetValueOrDefault(id)?.Count ?? 0);
    static uint Allocate()
    {
        do { nextId++; } while (nextId == 0 || Humans.ContainsKey(nextId) || Invites.Keys.Any(k => k.Party == nextId) || Removed.ContainsKey(nextId));
        return nextId;
    }
    static void Forget(uint id, Player c)
    {
        if (!Removed.TryGetValue(id, out var pending)) Removed[id] = pending = [];
        pending.Add(c);
    }
    static void Normalize(uint id)
    {
        if (!Humans.TryGetValue(id, out var members)) return;
        if (members.Count != 0 && (Count(id) >= 2 || Invites.Keys.Any(k => k.Party == id) || PublicDescriptions.ContainsKey(id))) return;
        foreach (var c in members) Forget(id, c);
        Humans.Remove(id);
        Companions.Remove(id);
        PublicDescriptions.Remove(id);
        foreach (var key in Invites.Keys.Where(k => k.Party == id).ToArray())
        {
            Forget(id, Invites[key].To);
            Invites.Remove(key);
        }
    }

    public static uint PartyOf(Player c) { lock (Gate) return Find(c); }
    public static bool SameParty(Player a, Player b)
    {
        lock (Gate)
        {
            uint party = Find(a);
            return party != 0 && party == Find(b);
        }
    }
    public static Player? Leader(uint id) { lock (Gate) return Humans.GetValueOrDefault(id)?.FirstOrDefault(); }
    public static Player[] Players(uint id) { lock (Gate) return Humans.TryGetValue(id, out var members) ? [.. members] : []; }
    public static string? PublicDescription(uint id) { lock (Gate) return PublicDescriptions.GetValueOrDefault(id); }
    internal static Player? TravelMember(Player actor, string name)
    {
        lock (Gate)
        {
            if (!Available(actor) || !actor.Connection.PlayerSent || actor.PlayerLife == 0 || actor.Character.HardcoreDeathRecorded
                || !InGame.ContainsKey(actor) || !Humans.TryGetValue(Find(actor), out var members)
                || !members.Any(c => ReferenceEquals(c, actor))) return null;
            return members.FirstOrDefault(c => !ReferenceEquals(c, actor) && Available(c) && SameLeague(actor, c)
                && c.Connection.PlayerSent && c.PlayerLife > 0 && !c.Character.HardcoreDeathRecorded && InGame.ContainsKey(c)
                && string.Equals(CharacterName(c), name, StringComparison.OrdinalIgnoreCase));
        }
    }

    public static int Size(uint id) { lock (Gate) return Math.Max(1, Count(id)); }
    public static bool ContainsCompanion(uint id, string account)
    {
        lock (Gate) return Companions.GetValueOrDefault(id)?.Any(m => m.Account == account) == true;
    }
    public static bool IsLeader(Player c, uint id)
    {
        lock (Gate) return Humans.GetValueOrDefault(id)?.FirstOrDefault() is { } first && Same(first, c);
    }

    /// <summary>Admission is atomic with human acceptance, including the shared six-player capacity.</summary>
    public static bool TryAddCompanion(Player inviter, PartyCompanion member, out uint party, uint restore = 0)
    {
        lock (Gate)
        {
            party = Find(inviter);
            uint currentParty = party;
            if (!Available(inviter)
                || Companions.Any(p => p.Key != currentParty && p.Value.Any(m => m.Account == member.Account))) return false;
            if (party == 0)
            {
                party = restore != 0 && !Humans.ContainsKey(restore) && !Invites.Keys.Any(k => k.Party == restore) ? restore : Allocate();
                Humans[party] = [inviter];
            }
            else if (!Compatible(Humans[party][0], inviter)) return false;
            if (!Companions.TryGetValue(party, out var guests)) Companions[party] = guests = [];
            int existing = guests.FindIndex(m => m.Account == member.Account);
            if (existing < 0 && Count(party) >= Capacity) return false;
            var entry = (member.Account, member.Character, member.KnownPlayer);
            if (existing >= 0) guests[existing] = entry; else guests.Add(entry);
            return true;
        }
    }

    public static void UpdateCompanion(uint party, PartyCompanion member)
    {
        lock (Gate)
            if (Companions.TryGetValue(party, out var guests))
            {
                int i = guests.FindIndex(m => m.Account == member.Account);
                if (i >= 0) guests[i] = (member.Account, member.Character, member.KnownPlayer);
            }
    }

    public static bool RemoveCompanion(uint party, string account)
    {
        lock (Gate)
        {
            if (!Companions.TryGetValue(party, out var guests) || guests.RemoveAll(m => m.Account == account) == 0) return false;
            Normalize(party);
            return true;
        }
    }

    // The wire encrypts a packet in place (Program.Write), so a kept one (a companion's Known) is sent as a copy:
    // sent again as it was, it reached the client as noise and ended its stream ("Unable to deserialise packet").
    static Task Send(Player c, byte[] packet) => !c.Closed && c.Connection.Outbox is { } send
        ? send(packet, null) : Task.CompletedTask;
    // The third field is the player's own area as a WorldAreas HASH16 (0xa7 +0xa8), not their level: the party panel's row
    // resolves it for the member's area name and offers the travel button only for an area, so a level shows
    // "Travelling..." with the button disabled (#715).
    static byte[] Known(Player c) => InstanceSocial.KnownPlayer(c.Character.Account ?? CharacterName(c), CharacterName(c),
        c.Layout.Hash, PackedClass(c), c.Character.PortraitFrame, WornPortrait(c), FriendsFeature.StatusOf(c.Character.Account));

    /// <summary>Serialize roster output: a leave/kick cannot be followed by an older in-flight roster.</summary>
    public static async Task Publish(uint party)
    {
        await Output.WaitAsync();
        try
        {
            Player[] removed, humans;
            (string Account, string Character, byte[] Known)[] guests;
            string? description;
            lock (Gate)
            {
                removed = Removed.Remove(party, out var pending) ? [.. pending] : [];
                humans = Humans.TryGetValue(party, out var list) ? [.. list] : [];
                guests = Companions.TryGetValue(party, out var extra) ? [.. extra] : [];
                description = PublicDescriptions.GetValueOrDefault(party);
            }
            foreach (var c in removed)
                await Deliver(c, async () => { await Send(c, InstanceSocial.PartyRemove(party)); await SetPartySize(c, 1); });
            if (humans.Length == 0) return;
            var leader = humans[0];
            string title = description ?? CharacterName(leader);
            (string, byte)[] entries = [.. humans.Select((c, i) => (c.Character.Account ?? CharacterName(c), i == 0 ? (byte)2 : (byte)1)),
                .. guests.Select(m => (m.Account, (byte)1))];
            for (int i = 0; i < humans.Length; i++)
            {
                var to = humans[i];
                byte ownIndex = (byte)i;
                await Deliver(to, async () =>
                {
                    foreach (var human in humans) await Send(to, Known(human));
                    foreach (var guest in guests) await Send(to, [.. guest.Known]);
                    await SetPartySize(to, entries.Length);
                    await Send(to, InstanceSocial.PartyInfo(party, title, leader.Character.Account ?? CharacterName(leader),
                        ownIndex, 0, 0, 0, entries));
                });
            }
        }
        finally { Output.Release(); }
    }
    static async Task Deliver(Player c, Func<Task> action)
    {
        if (c.Closed) return;
        try { await action(); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { /* Other members still receive their roster. */ }
    }

    public static async Task<bool> InviteHuman(Player from, string name)
    {
        var to = InGame.Keys.FirstOrDefault(c => c.Listed && !ReferenceEquals(c, from) && !c.Closed && !c.LeavingArea
            && (string.Equals(CharacterName(c), name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Character.Account, name, StringComparison.OrdinalIgnoreCase)));
        if (to is null) return false; // Another module may own this character's name.
        if (FriendsFeature.IsIgnoring(to.Character.Account, from.Character.Account ?? CharacterName(from))) return true;
        uint party;
        string toName = CharacterName(to);
        lock (Gate)
        {
            party = Find(from);
            if (!Compatible(from, to) || Find(to) != 0 || party != 0 && Count(party) >= Capacity) return true;
            if (party == 0) Humans[party = Allocate()] = [from];
            Invites[(party, toName)] = (from, to);
        }
        await Output.WaitAsync();
        try
        {
            Player[] humans;
            (string Account, string Character, byte[] Known)[] guests;
            string title;
            lock (Gate)
            {
                if (!Invites.TryGetValue((party, toName), out var pending) || !ReferenceEquals(pending.To, to)
                    || !Humans.TryGetValue(party, out var list) || list.Count == 0)
                    return true;
                humans = [.. list];
                guests = Companions.TryGetValue(party, out var extra) ? [.. extra] : [];
                title = PublicDescriptions.GetValueOrDefault(party) ?? CharacterName(from);
            }
            // Clear only the invitee's old knowledge of this id: the native notification requires an unknown party.
            // The party id itself stays stable for its existing members and module-owned actors.
            await Deliver(to, async () =>
            {
                await Send(to, InstanceSocial.PartyRemove(party));
                foreach (var human in humans) await Send(to, Known(human));
                foreach (var guest in guests) await Send(to, [.. guest.Known]);
                await Send(to, Known(to));
                (string, byte)[] entries = [.. humans.Select((c, i) => (c.Character.Account ?? CharacterName(c), i == 0 ? (byte)2 : (byte)1)),
                    .. guests.Select(m => (m.Account, (byte)1)), (to.Character.Account ?? toName, (byte)0)];
                await Send(to, InstanceSocial.PartyInfo(party, title, from.Character.Account ?? CharacterName(from),
                    (byte)(entries.Length - 1), 0, 1, 0, entries));
            });
        }
        finally { Output.Release(); }
        return true;
    }

    public static async Task AcceptHuman(Player to, uint party)
    {
        lock (Gate)
        {
            var key = (party, CharacterName(to));
            if (!Invites.TryGetValue(key, out var invite) || !Same(invite.To, to)) return;
            Invites.Remove(key);
            var liveFrom = InGame.Keys.FirstOrDefault(c => Same(c, invite.From) && Available(c));
            if (liveFrom is not null && Compatible(liveFrom, to) && Find(to) == 0 && Find(liveFrom) == party && Count(party) < Capacity)
            {
                var members = Humans[party];
                int idx = members.FindIndex(m => Same(m, liveFrom));
                if (idx >= 0) members[idx] = liveFrom;
                members.Add(to);
            }
            else
            {
                // The inviter disconnected or became unavailable before acceptance: clean up an orphaned party.
                Forget(party, to);
                Normalize(party);
            }
        }
        await Publish(party);
    }

    /// <summary>C→S 0x0098: create or rename a public party (empty description makes the party private).</summary>
    public static async Task SetPublicParty(Player actor, string rawDescription, byte flag)
    {
        if (!Available(actor)) return;
        string desc = ChatWire.Clean(rawDescription).Trim();
        if (desc.Length > MaxDescriptionLength) desc = desc[..MaxDescriptionLength];
        uint party;
        lock (Gate)
        {
            party = Find(actor);
            if (desc.Length == 0)
            {
                if (party == 0 || !Same(Humans[party][0], actor)) return;
                PublicDescriptions.Remove(party);
                Normalize(party);
            }
            else
            {
                if (party == 0) Humans[party = Allocate()] = [actor];
                else if (!Same(Humans[party][0], actor)) return;
                PublicDescriptions[party] = desc;
            }
        }
        Console.WriteLine($"[gateway] <= 0x0098 public party 0x{party:x} from {CharacterName(actor)}: \"{desc}\" (u8 {flag})");
        await Publish(party);
    }

    /// <summary>Cleans up party membership and pending invitations when a human player actually disconnects.
    /// Area transfers defer cleanup by <see cref="TransferGrace"/> so clients that close mid-transfer are still evicted
    /// if they never rebind via <see cref="Rebind"/>.</summary>
    public static async Task DisconnectHuman(Player c, string why)
    {
        if (why != "disconnected") return;
        if (c.LeavingArea || c.PendingTransfer is not null)
        {
            _ = DisconnectIfNotRebound(c);
            return;
        }
        await RemoveDisconnectedHuman(c);
    }

    static async Task DisconnectIfNotRebound(Player c)
    {
        try
        {
            await Task.Delay(TransferGrace);
            await RemoveDisconnectedHuman(c, requireOffline: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[gateway] party disconnect grace cleanup failed: {ex.Message}");
        }
    }

    static async Task RemoveDisconnectedHuman(Player c, bool requireOffline = false)
    {
        HashSet<uint> affected = [];
        lock (Gate)
        {
            if (requireOffline && InGame.Keys.Any(p => !ReferenceEquals(p, c) && !p.Closed && Same(p, c)))
                return;
            uint party = Find(c);
            if (party != 0 && RemoveHuman(c, party)) affected.Add(party);
            foreach (var key in Invites.Keys.Where(k => Same(Invites[k].From, c) || Same(Invites[k].To, c)).ToArray())
            {
                var invite = Invites[key];
                Invites.Remove(key);
                if (!Same(invite.To, c)) Forget(key.Party, invite.To);
                Normalize(key.Party);
                affected.Add(key.Party);
            }
        }
        foreach (uint id in affected) await Publish(id);
    }

    public static bool RemoveHuman(Player c, uint party)
    {
        lock (Gate)
        {
            if (!Humans.TryGetValue(party, out var members) || members.RemoveAll(m => Same(m, c)) == 0) return false;
            Forget(party, c);
            foreach (var key in Invites.Keys.Where(k => k.Party == party && Same(Invites[k].From, c)).ToArray())
            {
                Forget(party, Invites[key].To);
                Invites.Remove(key);
            }
            Normalize(party);
            return true;
        }
    }

    public static async Task<bool> LeaveHuman(Player c, uint party)
    {
        if (!RemoveHuman(c, party)) return false;
        await Publish(party);
        await BanditChoices.OnPartyChanged(c);
        return true;
    }

    public static async Task<bool> Kick(Player actor, string name)
    {
        uint party;
        Player? kickedHuman = null;
        lock (Gate)
        {
            // 0x94 also cancels an invitation this participant sent; the target has not joined yet.
            var pending = Invites.FirstOrDefault(p => Same(p.Value.From, actor)
                && (string.Equals(CharacterName(p.Value.To), name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.Value.To.Character.Account, name, StringComparison.OrdinalIgnoreCase)));
            if (pending.Key.Party != 0)
            {
                party = pending.Key.Party;
                Invites.Remove(pending.Key);
                Forget(party, pending.Value.To);
                Normalize(party);
            }
            else
            {
                party = Find(actor);
                if (party == 0) return false;
                if (!Same(Humans[party][0], actor)) return true;
                var human = Humans[party].Skip(1).FirstOrDefault(c => string.Equals(c.Character.Account, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(CharacterName(c), name, StringComparison.OrdinalIgnoreCase));
                if (human is not null)
                {
                    RemoveHuman(human, party);
                    kickedHuman = human;
                }
                else if (Companions.GetValueOrDefault(party)?.RemoveAll(m =>
                    string.Equals(m.Account, name, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(m.Character, name, StringComparison.OrdinalIgnoreCase)) != 1) return true;
                Normalize(party);
            }
        }
        await Publish(party);
        if (kickedHuman is not null) await BanditChoices.OnPartyChanged(kickedHuman);
        return true;
    }

    public static async Task Rebind(Player c)
    {
        uint party;
        lock (Gate)
        {
            party = Find(c);
            if (party == 0) return;
            var list = Humans[party];
            int i = list.FindIndex(m => Same(m, c));
            list[i] = c;
        }
        await Publish(party);
    }

    public static ulong SharedInstance(Player c, PendingTransfer transfer)
    {
        // Personal homes use their owner, and a map can only be entered through its finite portal pool.
        // Explicit party travel already pins its destination; ordinary travel must not redirect to these members.
        if (HideoutCatalog.ByArea(transfer.Area) is not null || MapDevices.IsMapArea(transfer.Area)) return 0;
        Player[] members;
        lock (Gate) members = Humans.TryGetValue(Find(c), out var list) ? [.. list] : [];
        foreach (var member in members)
        {
            if (Same(member, c)) continue;
            if (member.TransferredTo is { } going && going.Area == transfer.Area) return going.Instance;
            if (!member.Closed && !member.LeavingArea && member.Layout.Hash == transfer.Area && InGame.ContainsKey(member))
                return member.InstanceNumber;
        }
        return 0;
    }
}
