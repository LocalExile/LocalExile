using System.Collections.Immutable;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// BuffDefinitions rows as S→C 0x1c1 needs them (InstanceEntities.AddBuff): a row with the +0xff bool (<c>__col44</c>)
/// takes a u16 +0x82, and an IsRecovery row (+0x7c) one u32 per GrantedStats entry, or the client misreads the message.
/// <see cref="Spec"/> writes any row's message from the buff's stats by name, so a caller cannot get that wrong. Also the
/// row's stacking: MergeMode (<c>__col7</c>) 1 keeps each application as its own instance, up to BuffLimit (withered 15,
/// tethered_debuff 3; 0: no limit); any other mode keeps one instance of the row (every ground_* row, the curses).
/// </summary>
public static class BuffRows
{
    public sealed record Row(ushort Index, string Id, bool IsRecovery, bool HasU82, IReadOnlyList<uint> GrantedStats,
        int MergeMode, int Limit, bool Invisible)
    {
        /// <summary>Each application is its own instance (MergeMode 1).</summary>
        public bool Stacks => MergeMode == 1;
    }

    sealed record RegisteredOverlay(long Id, string Owner, Row Row);

    sealed record Snapshot(
        Dictionary<ushort, Row> ByIndex,
        Dictionary<string, ushort> ById,
        IReadOnlyList<uint> FinalMovementKeys,
        long StockRevision,
        long OverlayRevision);

    static readonly Lock SnapshotLock = new();
    static List<Row?> StockRows = [];
    static Dictionary<string, ushort> StockById = new(StringComparer.Ordinal);
    static IReadOnlyList<uint> StockFinalMovementKeys = [];
    static long StockRevision;

    static ImmutableArray<RegisteredOverlay> _sharedOverlays = [];
    static long _sharedNextId;
    static long _sharedRevision;
    static Snapshot _snapshot = new([], new(StringComparer.Ordinal), [], -1, -1);

    /// <summary>Advances whenever runtime <see cref="BuffRows"/> overlays change.</summary>
    public static long OverlayRevision => Volatile.Read(ref _sharedRevision);

    /// <summary>Registers a runtime <c>BuffDefinitions</c> row overlay preserved across <see cref="LoadGameData"/>
    /// (<c>/reload</c>) and automatically removed when disposed or when <paramref name="owner"/> unloads via
    /// <see cref="GameEventOwners"/>.</summary>
    public static IDisposable RegisterOverlay(string owner, Row row)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(row);
        if (string.IsNullOrWhiteSpace(row.Id))
            throw new ArgumentException("BuffRows.Row.Id must be non-empty.", nameof(row));
        long id = Interlocked.Increment(ref _sharedNextId);
        ImmutableInterlocked.Update(ref _sharedOverlays, list => list.Add(new(id, owner, row)));
        Interlocked.Increment(ref _sharedRevision);
        return GameEventOwners.Own(owner, new Undo(() =>
        {
            if (ImmutableInterlocked.Update(ref _sharedOverlays, list => list.RemoveAll(e => e.Id == id)))
                Interlocked.Increment(ref _sharedRevision);
        }));
    }

    /// <summary>Fills the rows at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadGameData()
    {
        List<Row?> rows = [];
        Dictionary<string, ushort> byId = new(StringComparer.Ordinal);
        foreach (var r in ClientTables.TableOrEmpty("BuffDefinitions"))
        {
            string id = r.Text("Id") ?? "";
            var granted = r.Ints("GrantedStats").Where(s => s >= 0).Select(s => (uint)s + 1).ToList();
            rows.Add(new((ushort)r.Index, id, r.BoolOrNull("IsRecovery") ?? false, r.BoolOrNull("__col44") ?? false, granted,
                r.IntOrNull("__col7") ?? 0, r.IntOrNull("BuffLimit") ?? 0, r.BoolOrNull("Invisible") ?? false));
            byId.TryAdd(id, (ushort)r.Index);
        }
        IReadOnlyList<uint> finalMovementKeys = [.. rows.SelectMany(r => r?.GrantedStats ?? []).Distinct()
            .Where(n => Program.Stats.Name(n).EndsWith("movement_speed_+%_final", StringComparison.Ordinal))];
        lock (SnapshotLock)
        {
            StockRows = rows;
            StockById = byId;
            StockFinalMovementKeys = finalMovementKeys;
            Interlocked.Increment(ref StockRevision);
        }
    }

    static Snapshot CurrentSnapshot()
    {
        long stockRev = Volatile.Read(ref StockRevision);
        long overlayRev = Volatile.Read(ref _sharedRevision);
        var current = Volatile.Read(ref _snapshot);
        if (current.StockRevision == stockRev && current.OverlayRevision == overlayRev)
            return current;
        lock (SnapshotLock)
        {
            stockRev = Volatile.Read(ref StockRevision);
            overlayRev = Volatile.Read(ref _sharedRevision);
            current = _snapshot;
            if (current.StockRevision == stockRev && current.OverlayRevision == overlayRev)
                return current;
            var overlays = _sharedOverlays;
            var byIndex = new Dictionary<ushort, Row>(StockRows.Count + overlays.Length);
            for (int i = 0; i < StockRows.Count; i++)
                if (StockRows[i] is { } r) byIndex[r.Index] = r;
            var byId = new Dictionary<string, ushort>(StockById, StringComparer.Ordinal);
            List<uint>? extraMovement = null;
            for (int i = 0; i < overlays.Length; i++)
            {
                var r = overlays[i].Row;
                byIndex[r.Index] = r;
                byId[r.Id] = r.Index;
                if (Program.Stats is not null)
                    foreach (uint stat in r.GrantedStats)
                        if (Program.Stats.Name(stat).EndsWith("movement_speed_+%_final", StringComparison.Ordinal))
                            (extraMovement ??= []).Add(stat);
            }
            IReadOnlyList<uint> movement = extraMovement is null
                ? StockFinalMovementKeys
                : [.. StockFinalMovementKeys.Concat(extraMovement).Distinct()];
            var rebuilt = new Snapshot(byIndex, byId, movement, stockRev, overlayRev);
            Volatile.Write(ref _snapshot, rebuilt);
            return rebuilt;
        }
    }

    /// <summary>The final movement modifiers buffs grant their target (tethered_movement_speed_+%_final,
    /// bear_trap_movement_speed_+%_final, labyrinth_arrow_movement_speed_+%_final, Toxic Rain's vines'...): each
    /// multiplies the target's whole movement speed.</summary>
    public static IReadOnlyList<uint> FinalMovementKeys => CurrentSnapshot().FinalMovementKeys;

    public static Row? Of(ushort row) => CurrentSnapshot().ByIndex.GetValueOrDefault(row);

    /// <summary>The row of this Id, 0 when there is none.</summary>
    public static ushort Find(string id) => CurrentSnapshot().ById.GetValueOrDefault(id);

    /// <summary>S→C 0x1c1 for the row on <paramref name="objectId"/>: its GrantedStats values from <paramref name="stats"/>
    /// (0 for one it does not name) when the row is IsRecovery, the +0x82 u16 (0) when it has one, and its time as the
    /// countdown (none: until 0x1c2 ends it).</summary>
    /// <summary>A row's buff with the fields its definition reads: GrantedStats values on a recovery row, +0x82 where the
    /// row has it. <paramref name="tag"/> tells apart instances of a row that stacks.</summary>
    public static Buff Spec(ushort row, uint source, IReadOnlyDictionary<uint, int>? stats, double? seconds = null, ushort tag = 0)
    {
        var info = Of(row);
        IReadOnlyList<uint>? values = info is { IsRecovery: true }
            ? [.. info.GrantedStats.Select(name => unchecked((uint)(stats?.GetValueOrDefault(name) ?? 0)))] : null;
        return new(row, source, Values: values, U68: seconds is double s && double.IsFinite(s) ? Buff.Seconds(s) : null,
            U82: info is { HasU82: true } ? (ushort)0 : null, Tag: tag);
    }

    sealed class Undo(Action undo) : IDisposable
    {
        Action? _undo = undo;
        public void Dispose() => Interlocked.Exchange(ref _undo, null)?.Invoke();
    }
}
