using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using GameServer.Modding;

/// <summary>An optional family's source and admission rules. Durable identities remain readable without its module.</summary>
interface IAscendancyFamilyPolicy
{
    AscendancyFamily Family { get; }
    bool Supports(AscendancyIdentity identity);
    void RequirePlayable(AscendancyIdentity identity);
    IReadOnlyDictionary<uint, int>? AreaStats { get; }
    IReadOnlyList<(uint Key, int Value)> NodeStats(AscendancyCatalog.Definition definition, ushort node,
        IReadOnlyList<(uint Key, int Value)> modeStats);
}

/// <summary>Normal ascendancies are built in. Optional families require a live provider listed by this connection's league.</summary>
static class AscendancyPolicies
{
    static ImmutableDictionary<AscendancyFamily, Registration> _providers = ImmutableDictionary<AscendancyFamily, Registration>.Empty;
    static readonly ConditionalWeakTable<ClientState, Registration> Attachments = new();

    public static IDisposable Register(IModuleLifetime owner, IAscendancyFamilyPolicy policy)
    {
        if (!owner.IsActive) throw new InvalidOperationException("The module is stopping");
        if (policy.Family == AscendancyFamily.Normal) throw new InvalidOperationException("Normal ascendancies are built in");
        var entry = new Registration(owner, policy);
        if (!ImmutableInterlocked.TryAdd(ref _providers, policy.Family, entry))
            throw new InvalidOperationException($"An ascendancy provider already owns {policy.Family}");
        // Own also disposes immediately if Stop raced registration.
        return owner.Own(entry);
    }

    public static bool Available(ClientState conn, AscendancyIdentity selection)
    {
        if (selection.Family == AscendancyFamily.Normal) return true;
        return ReadPolicy(conn, selection) is not null;
    }

    /// <summary>Pure source provenance for transient jobs. Never verifies a graph or acquires the provider gate.</summary>
    public static IModuleLifetime? SourceOwner(ClientState conn)
    {
        var selection = conn.MainAscendancy.Selection;
        if (selection is null || selection.Family == AscendancyFamily.Normal
            || !_providers.TryGetValue(selection.Family, out var entry)) return null;
        var policy = Volatile.Read(ref entry.Policy);
        return policy is not null && entry.Live(conn) && policy.Supports(selection) ? entry.Owner : null;
    }

    /// <summary>Holds a synchronous admission/commit boundary. Never keep it across an await or acquire the host lock.</summary>
    public static IDisposable Enter(ClientState conn, AscendancyIdentity? selection, bool verifyGraph = false)
    {
        if (selection is null || selection.Family == AscendancyFamily.Normal)
            return Empty.Instance;
        if (!_providers.TryGetValue(selection.Family, out var entry)) throw Unavailable(selection);
        bool admitted = Monitor.IsEntered(entry.Gate);
        Monitor.Enter(entry.Gate);
        try
        {
            // A synchronous commit admitted before Stop finishes under the same gate; retirement waits for it.
            if (entry.Policy is null || !admitted && !entry.Live(conn) || !entry.Policy.Supports(selection))
                throw Unavailable(selection);
            if (verifyGraph) entry.Policy.RequirePlayable(selection);
            return new Exit(entry.Gate);
        }
        catch { Monitor.Exit(entry.Gate); throw; }
    }

    public static void RequirePlayable(ClientState conn, AscendancyIdentity? selection)
    {
        if (selection is not null && selection.Family != AscendancyFamily.Normal)
            (ReadPolicy(conn, selection) ?? throw Unavailable(selection)).RequirePlayable(selection);
    }

    public static void RequireAvailable(ClientState conn, AscendancyIdentity? selection)
    {
        if (selection is not null && selection.Family != AscendancyFamily.Normal
            && ReadPolicy(conn, selection) is null) throw Unavailable(selection);
    }

    /// <summary>Inside a transaction, reject another connection's family/class switch before writing or publishing.</summary>
    public static void RequireCurrentSelection(AscendancyIdentity? expected, AscendancyIdentity? durable)
    {
        if (expected is null && durable is null) return;
        if (expected is not null && durable is not null && expected.Family == durable.Family
            && expected.Id == durable.Id && expected.Revision == durable.Revision) return;
        throw new PassiveProgressConflictException();
    }

    public static void SelectionChanged(ClientState conn)
    {
        // Bind only after a successful durable/runtime change, never while validating a prospective choice.
        // Failed optional choices leave ordinary characters outside optional-module retirement.
        if (conn.MainAscendancy.Selection is null || conn.MainAscendancy.Selection.Family == AscendancyFamily.Normal)
        {
            Attachments.Remove(conn);
            return;
        }
        using var admission = Enter(conn, conn.MainAscendancy.Selection);
        var entry = _providers[conn.MainAscendancy.Selection.Family];
        Attachments.AddOrUpdate(conn, entry);
        entry.Connections.GetValue(conn, _ => new object());
    }

    public static IReadOnlyDictionary<uint, int>? AreaStats(ClientState conn, AscendancyIdentity? selection)
    {
        return selection is null || selection.Family == AscendancyFamily.Normal ? null
            : (ReadPolicy(conn, selection) ?? throw Unavailable(selection)).AreaStats;
    }

    public static IReadOnlyList<(uint Key, int Value)> NodeStats(ClientState conn,
        AscendancyCatalog.Definition definition, ushort node, IReadOnlyList<(uint Key, int Value)> modeStats)
    {
        return definition.Identity.Family == AscendancyFamily.Normal ? modeStats
            : (ReadPolicy(conn, definition.Identity) ?? throw Unavailable(definition.Identity))
                .NodeStats(definition, node, modeStats);
    }

    // Source/availability queries also run inside saved-progress transactions. They must never acquire the
    // provider commit gate there: mutating paths use connection -> provider -> database ordering.
    // Policies are pure readers; a local snapshot may finish reading while its owner is retiring.
    static IAscendancyFamilyPolicy? ReadPolicy(ClientState conn, AscendancyIdentity selection)
    {
        if (!_providers.TryGetValue(selection.Family, out var entry)) return null;
        var policy = Volatile.Read(ref entry.Policy);
        return policy is not null && (Monitor.IsEntered(entry.Gate) || entry.Live(conn)) && policy.Supports(selection)
            ? policy : null;
    }

    static AscendancyRuleException Unavailable(AscendancyIdentity selection) => new(
        $"{selection.Family}/{selection.Id} is unavailable: this league needs its loaded ascendancy module, " +
        "implemented class and explicit compatibility opt-in. Saved progress is retained.");

    sealed class Registration(IModuleLifetime owner, IAscendancyFamilyPolicy policy) : IDisposable
    {
        public IModuleLifetime Owner => owner;
        public readonly object Gate = new();
        public readonly ConditionalWeakTable<ClientState, object> Connections = new();
        public IAscendancyFamilyPolicy? Policy = policy;
        readonly AscendancyFamily _family = policy.Family;
        public bool Live(ClientState conn) => Policy is not null && owner.IsActive && !conn.Closed
            && Program.ModulesFor(conn).Contains(owner.Id);

        public void Dispose()
        {
            ClientState[] affected;
            lock (Gate)
            {
                if (Policy is null) return;
                Volatile.Write(ref Policy, null);
                ImmutableInterlocked.Update(ref _providers, entries =>
                    entries.TryGetValue(_family, out var current) && ReferenceEquals(current, this)
                        ? entries.Remove(_family) : entries);
                affected = Connections.Select(pair => pair.Key).ToArray();
                Connections.Clear();
            }
            // Do not acquire a connection lock while holding the provider lock: mutations take them in the reverse order.
            // A reload conservatively ends affected areas. Re-entry binds current code without a missing-drawback interval.
            foreach (var conn in affected)
            {
                lock (conn.Monsters)
                {
                    if (!Attachments.TryGetValue(conn, out var current) || !ReferenceEquals(current, this)) continue;
                    Attachments.Remove(conn);
                    conn.Closed = true;
                    conn.Rampage.Clear();
                    conn.RetireOwnedCombat();
                    conn.InvalidateIncomingHits();
                }
                Console.WriteLine($"[gateway] {Program.CharacterName(conn)}: {_family} module reloaded; reconnect to retain current rules");
                conn.Wire.Stream?.Close();
            }
        }
    }

    sealed class Exit(object gate) : IDisposable
    {
        object? _gate = gate;
        public void Dispose() { if (Interlocked.Exchange(ref _gate, null) is { } current) Monitor.Exit(current); }
    }
    sealed class Empty : IDisposable
    {
        public static readonly Empty Instance = new();
        public void Dispose() { }
    }
}
