using System.Collections.Immutable;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// Reads of client.db's object_types rows (data/object-types.json from scripts/re/ot_components.py, one row per .ot
/// type; a nested object is flattened into <c>&lt;field&gt;_&lt;key&gt;</c> columns: positioned_blocking, life_corpse_usable),
/// plus runtime <see cref="TypeOverlay"/> registrations owned by <see cref="GameEventOwners"/>.
/// </summary>
public static class ObjectTypes
{
    /// <summary>Runtime metadata overlay for a custom or overridden <c>.ot</c> object type.</summary>
    public sealed record TypeOverlay(
        string Type,
        IReadOnlyList<string> Components,
        StateMachineData? StateMachine = null,
        int TransitionStates = 2,
        int ObjectSize = 2,
        bool Blocking = false,
        string? Model = null,
        bool Static = false);

    sealed record RegisteredOverlay(long Id, string Owner, TypeOverlay Overlay);
    sealed record Snapshot(long Revision, Dictionary<string, TypeOverlay> ByType);

    static ImmutableArray<RegisteredOverlay> _overlays = [];
    static long _nextId;
    static long _revision;
    static readonly Lock SnapshotLock = new();
    static Snapshot _snapshot = new(-1, new(StringComparer.Ordinal));

    /// <summary>Advances whenever runtime <see cref="ObjectTypes"/> overlays change.</summary>
    public static long OverlayRevision => Volatile.Read(ref _revision);

    /// <summary>Registers a runtime <see cref="TypeOverlay"/> preserved across <c>/reload</c> and automatically
    /// removed when disposed or when <paramref name="owner"/> unloads via <see cref="GameEventOwners"/>.</summary>
    public static IDisposable RegisterOverlay(string owner, TypeOverlay overlay)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(overlay);
        if (string.IsNullOrWhiteSpace(overlay.Type))
            throw new ArgumentException("ObjectTypes.TypeOverlay.Type must be non-empty.", nameof(overlay));
        ArgumentNullException.ThrowIfNull(overlay.Components);
        long id = Interlocked.Increment(ref _nextId);
        ImmutableInterlocked.Update(ref _overlays, list => list.Add(new(id, owner, overlay)));
        Interlocked.Increment(ref _revision);
        return GameEventOwners.Own(owner, new Undo(() =>
        {
            if (ImmutableInterlocked.Update(ref _overlays, list => list.RemoveAll(e => e.Id == id)))
                Interlocked.Increment(ref _revision);
        }));
    }

    static Dictionary<string, TypeOverlay> CurrentOverlayMap()
    {
        long rev = Volatile.Read(ref _revision);
        var current = Volatile.Read(ref _snapshot);
        if (current.Revision == rev) return current.ByType;
        lock (SnapshotLock)
        {
            rev = Volatile.Read(ref _revision);
            current = _snapshot;
            if (current.Revision == rev) return current.ByType;
            var entries = _overlays;
            var map = new Dictionary<string, TypeOverlay>(entries.Length, StringComparer.Ordinal);
            for (int i = 0; i < entries.Length; i++) map[entries[i].Overlay.Type] = entries[i].Overlay;
            Volatile.Write(ref _snapshot, new Snapshot(rev, map));
            return map;
        }
    }

    /// <summary>Looks up a runtime <see cref="TypeOverlay"/> by exact metadata path, or null when none is registered.</summary>
    public static TypeOverlay? FindOverlay(string type) =>
        _overlays.IsEmpty ? null : CurrentOverlayMap().GetValueOrDefault(type);

    /// <summary>Snapshot of all active runtime <see cref="TypeOverlay"/> registrations.</summary>
    public static IReadOnlyList<TypeOverlay> Overlays =>
        _overlays.IsEmpty ? [] : [.. _overlays.Select(e => e.Overlay)];

    /// <summary>The components the client reads for the type, in order (the first <c>read</c> of its list).</summary>
    public static IReadOnlyList<string> Components(ClientRow type) =>
        [.. type.Texts("components").Take(type.IntOrNull("read") ?? int.MaxValue)];

    /// <summary>A flag the generator writes as a boolean or as the .ot's text ("true"/"false"), or null.</summary>
    public static bool? Flag(ClientRow type, string column) => type.IsNull(column) ? null : type.Get(column) switch
    {
        bool b => b,
        long l => l != 0,
        string s when bool.TryParse(s, out bool b) => b,
        _ => null,
    };

    /// <summary>The object_type_shared_states rows by their type's row (<see cref="StateMachine"/>).</summary>
    public static ILookup<long, ClientRow> SharedStates() => ClientTables.TableOrEmpty("object_type_shared_states").By("type");

    /// <summary>The type's StateMachine defaults (its define_shared_state values, names and shared timer count), or null
    /// for data generated before them (no shared_timers).</summary>
    public static StateMachineData? StateMachine(ClientRow type, ILookup<long, ClientRow> sharedStates)
    {
        if (type.IsNull("shared_timers")) return null;
        var states = sharedStates[type.Index].ToList();
        return new([.. states.Select(s => s.Int("value"))], type.Texts("shared_timers").Length, [.. states.Select(s => s.Text("name") ?? "")]);
    }

    /// <summary>The types by their path (Metadata/...), with each row.</summary>
    public static IEnumerable<(string Type, ClientRow Row)> All() =>
        ClientTables.TableOrEmpty("object_types").Select(r => (r.Text("type")!, r));

    sealed class Undo(Action undo) : IDisposable
    {
        Action? _undo = undo;
        public void Dispose() => Interlocked.Exchange(ref _undo, null)?.Invoke();
    }
}
