using GameServer.Core.Protocol.Instance;
using static Program;

namespace GameServer.Modules.Social;

/// <summary>A connected party's additional actor, supplied by another module. No controller or AI is retained here.</summary>
public sealed record PartyCompanion(string Account, string Character, byte[] KnownPlayer);

/// <summary>One authoritative party for socket players and module-owned actors. The first human leads.</summary>
public static class PartyRoster
{
    public const int Capacity = 6;
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
    static readonly Dictionary<uint, List<ClientState>> Humans = Keep("Humans", () => new Dictionary<uint, List<ClientState>>());
    static readonly Dictionary<uint, List<(string Account, string Character, byte[] Known)>> Companions =
        Keep("Companions", () => new Dictionary<uint, List<(string, string, byte[])>>());
    static readonly Dictionary<(uint Party, string Character), (ClientState From, ClientState To)> Invites =
        Keep("Invites", () => new Dictionary<(uint, string), (ClientState, ClientState)>());
    static readonly Dictionary<uint, HashSet<ClientState>> Removed = Keep("Removed", () => new Dictionary<uint, HashSet<ClientState>>());
    static uint nextId = 0x100;

    static bool Same(ClientState a, ClientState b) => ReferenceEquals(a, b)
        || a.Account is not null && string.Equals(a.Account, b.Account, StringComparison.OrdinalIgnoreCase)
            && string.Equals(CharacterName(a), CharacterName(b), StringComparison.Ordinal);
    static bool Available(ClientState c) => !c.Controlled && !c.Closed && !c.LeavingArea && !c.Ssf;
    static bool Compatible(ClientState a, ClientState b) => Available(a) && Available(b)
        && a.Hardcore == b.Hardcore && a.LeagueForm == b.LeagueForm
        && string.Equals(a.League, b.League, StringComparison.OrdinalIgnoreCase)
        && ReferenceEquals(a.Instance.Presence, b.Instance.Presence);
    static uint Find(ClientState c) => Humans.FirstOrDefault(p => p.Value.Any(m => Same(m, c))).Key;
    static int Count(uint id) => (Humans.GetValueOrDefault(id)?.Count ?? 0) + (Companions.GetValueOrDefault(id)?.Count ?? 0);
    static uint Allocate()
    {
        do { nextId++; } while (nextId == 0 || Humans.ContainsKey(nextId) || Invites.Keys.Any(k => k.Party == nextId) || Removed.ContainsKey(nextId));
        return nextId;
    }
    static void Forget(uint id, ClientState c)
    {
        if (!Removed.TryGetValue(id, out var pending)) Removed[id] = pending = [];
        pending.Add(c);
    }
    static void Normalize(uint id)
    {
        if (!Humans.TryGetValue(id, out var members)) return;
        if (members.Count != 0 && (Count(id) >= 2 || Invites.Keys.Any(k => k.Party == id))) return;
        foreach (var c in members) Forget(id, c);
        Humans.Remove(id);
        Companions.Remove(id);
        foreach (var key in Invites.Keys.Where(k => k.Party == id).ToArray())
        {
            Forget(id, Invites[key].To);
            Invites.Remove(key);
        }
    }

    public static uint PartyOf(ClientState c) { lock (Gate) return Find(c); }
    public static ClientState? Leader(uint id) { lock (Gate) return Humans.GetValueOrDefault(id)?.FirstOrDefault(); }
    public static ClientState[] Players(uint id) { lock (Gate) return Humans.TryGetValue(id, out var members) ? [.. members] : []; }
    public static int Size(uint id) { lock (Gate) return Math.Max(1, Count(id)); }
    public static bool ContainsCompanion(uint id, string account)
    {
        lock (Gate) return Companions.GetValueOrDefault(id)?.Any(m => m.Account == account) == true;
    }
    public static bool IsLeader(ClientState c, uint id)
    {
        lock (Gate) return Humans.GetValueOrDefault(id)?.FirstOrDefault() is { } first && Same(first, c);
    }

    /// <summary>Admission is atomic with human acceptance, including the shared six-player capacity.</summary>
    public static bool TryAddCompanion(ClientState inviter, PartyCompanion member, out uint party, uint restore = 0)
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

    static Task Send(ClientState c, byte[] packet) => !c.Closed && c.Outbox is { } send
        ? send(packet, null) : Task.CompletedTask;
    static byte[] Known(ClientState c) => InstanceSocial.KnownPlayer(c.Account ?? CharacterName(c), CharacterName(c),
        c.Pools.Level, PackedClass(c), c.PortraitFrame, WornPortrait(c));

    /// <summary>Serialize roster output: a leave/kick cannot be followed by an older in-flight roster.</summary>
    public static async Task Publish(uint party)
    {
        await Output.WaitAsync();
        try
        {
            ClientState[] removed, humans;
            (string Account, string Character, byte[] Known)[] guests;
            lock (Gate)
            {
                removed = Removed.Remove(party, out var pending) ? [.. pending] : [];
                humans = Humans.TryGetValue(party, out var list) ? [.. list] : [];
                guests = Companions.TryGetValue(party, out var extra) ? [.. extra] : [];
            }
            foreach (var c in removed)
                await Deliver(c, async () => { await Send(c, InstanceSocial.PartyRemove(party)); await SetPartySize(c, 1); });
            if (humans.Length == 0) return;
            var leader = humans[0];
            (string, byte)[] entries = [.. humans.Select((c, i) => (c.Account ?? CharacterName(c), i == 0 ? (byte)2 : (byte)1)),
                .. guests.Select(m => (m.Account, (byte)1))];
            for (int i = 0; i < humans.Length; i++)
            {
                var to = humans[i];
                byte ownIndex = (byte)i;
                await Deliver(to, async () =>
                {
                    foreach (var human in humans) await Send(to, Known(human));
                    foreach (var guest in guests) await Send(to, guest.Known);
                    await SetPartySize(to, entries.Length);
                    await Send(to, InstanceSocial.PartyInfo(party, CharacterName(leader), leader.Account ?? CharacterName(leader),
                        ownIndex, 0, 0, 0, entries));
                });
            }
        }
        finally { Output.Release(); }
    }
    static async Task Deliver(ClientState c, Func<Task> action)
    {
        if (c.Closed) return;
        try { await action(); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { /* Other members still receive their roster. */ }
    }

    public static async Task<bool> InviteHuman(ClientState from, string name)
    {
        var to = InGame.Keys.FirstOrDefault(c => !c.Controlled && !ReferenceEquals(c, from) && !c.Closed && !c.LeavingArea
            && (string.Equals(CharacterName(c), name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Account, name, StringComparison.OrdinalIgnoreCase)));
        if (to is null) return false; // Another module may own this character's name.
        uint party;
        ClientState[] humans;
        (string Account, string Character, byte[] Known)[] guests;
        lock (Gate)
        {
            party = Find(from);
            if (!Compatible(from, to) || Find(to) != 0 || party != 0 && Count(party) >= Capacity) return true;
            if (party == 0) Humans[party = Allocate()] = [from];
            humans = [.. Humans[party]];
            guests = Companions.TryGetValue(party, out var extra) ? [.. extra] : [];
            Invites[(party, CharacterName(to))] = (from, to);
        }
        // Clear only the invitee's old knowledge of this id: the native notification requires an unknown party.
        // The party id itself stays stable for its existing members and module-owned actors.
        await Send(to, InstanceSocial.PartyRemove(party));
        foreach (var human in humans) await Send(to, Known(human));
        foreach (var guest in guests) await Send(to, guest.Known);
        await Send(to, Known(to));
        (string, byte)[] entries = [.. humans.Select((c, i) => (c.Account ?? CharacterName(c), i == 0 ? (byte)2 : (byte)1)),
            .. guests.Select(m => (m.Account, (byte)1)), (to.Account ?? CharacterName(to), (byte)0)];
        await Send(to, InstanceSocial.PartyInfo(party, CharacterName(from), from.Account ?? CharacterName(from),
            (byte)(entries.Length - 1), 0, 1, 0, entries));
        return true;
    }

    public static async Task AcceptHuman(ClientState to, uint party)
    {
        bool accepted = false;
        lock (Gate)
        {
            var key = (party, CharacterName(to));
            if (!Invites.TryGetValue(key, out var invite) || !Same(invite.To, to)) return;
            Invites.Remove(key);
            var from = InGame.Keys.FirstOrDefault(c => Same(c, invite.From) && Available(c)) ?? invite.From;
            if (Compatible(from, to) && Find(to) == 0 && Find(from) == party && Count(party) < Capacity)
            {
                Humans[party].Add(to);
                accepted = true;
            }
        }
        if (accepted) await Publish(party);
        else await Send(to, InstanceSocial.PartyRemove(party));
    }

    public static bool RemoveHuman(ClientState c, uint party)
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

    public static async Task<bool> LeaveHuman(ClientState c, uint party)
    {
        if (!RemoveHuman(c, party)) return false;
        await Publish(party);
        return true;
    }

    public static async Task<bool> Kick(ClientState actor, string name)
    {
        uint party;
        lock (Gate)
        {
            // 0x94 also cancels an invitation this participant sent; the target has not joined yet.
            var pending = Invites.FirstOrDefault(p => Same(p.Value.From, actor)
                && (string.Equals(CharacterName(p.Value.To), name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.Value.To.Account, name, StringComparison.OrdinalIgnoreCase)));
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
                var human = Humans[party].Skip(1).FirstOrDefault(c => string.Equals(c.Account, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(CharacterName(c), name, StringComparison.OrdinalIgnoreCase));
                if (human is not null) RemoveHuman(human, party);
                else if (Companions.GetValueOrDefault(party)?.RemoveAll(m =>
                    string.Equals(m.Account, name, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(m.Character, name, StringComparison.OrdinalIgnoreCase)) != 1) return true;
                Normalize(party);
            }
        }
        await Publish(party);
        return true;
    }

    public static async Task Rebind(ClientState c)
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

    public static ulong SharedInstance(ClientState c, PendingTransfer transfer)
    {
        ClientState[] members;
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
