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
        /// <summary>The client behaviour class name (<c>BuffDefinitions.Id2</c>), or empty when none.</summary>
        public string Behaviour { get; init; } = "";

        /// <summary>Each application is its own instance (MergeMode 1).</summary>
        public bool Stacks => MergeMode == 1;
    }

    static readonly uint[] OneZero = [0u];
    static readonly uint[] TwoZeros = [0u, 0u];
    static readonly uint[] ThreeZeros = [0u, 0u, 0u];
    static readonly uint[] FiveZeros = [0u, 0u, 0u, 0u, 0u];
    static readonly uint[] ChronomancerDefaults = [42u, 100u];
    static readonly uint[] PenanceChargeDefaults = [20u];

    /// <summary>The minimum trailing <c>0x01c1</c> list words (<c>Buff+0x80</c>) that the client's native buff backend
    /// reads without a length check, or null when the row's backend accepts an empty list. Prevents client access
    /// violations when a generic buff emitter (monster skills, minions, mods) omits behaviour-specific data.</summary>
    public static IReadOnlyList<uint>? DefaultList(Row? info)
    {
        if (info is null) return null;
        return info.Behaviour switch
        {
            // TempestShieldBuff (0x1417a9410 / 0x1417a945f): [artVariation, hideVisual] read unchecked.
            "TempestShieldBuff" => TwoZeros,
            // ChronomancerBuff (0x141f83180 / 0x141f832b0): [sampleCount, intervalMs] read unchecked.
            "ChronomancerBuff" => ChronomancerDefaults,
            // Guard / absorption backends (0x141f84000): [remainingCapacity] read unchecked.
            "QuickGuardBuff" or "DamageAbsorptionBuff" or "BoneArmourBuff" or "ArcaneCloakBuff" => OneZero,
            // FrostFuryStage (0x1415d17d0 -> 0x1417b6030): entries 1..2 read unchecked.
            "FrostFuryStage" => ThreeZeros,
            // MirageArcherVisualBuff (0x1415d4c20 / 0x1417c7cb0): entry 0 read unchecked.
            "MirageArcherVisualBuff" => OneZero,
            // MirageArcherDisplayBuff (0x1417c7010): entries 0..2 read unchecked.
            "MirageArcherDisplayBuff" => ThreeZeros,
            // RecordSkillUseLocations (0x142060950): [sequence, playerX, playerY, targetX, targetY] read unchecked.
            "RecordSkillUseLocations" => FiveZeros,
            // ActiveSigil (0x1415c6860 / 0x1415c6d60): [miscAnimatedRow, phase] read unchecked.
            "ActiveSigil" => TwoZeros,
            // PenanceBrandCharge: [maxEnergy] read when updating the effect.
            "PenanceBrandCharge" => PenanceChargeDefaults,
            // LegionRewardDisplay (0x1417c5d00): 5 entries read unchecked.
            "LegionRewardDisplay" => FiveZeros,
            _ => info.Id switch
            {
                // Backends with empty Id2 that still dereference Buff+0x80[0] unchecked.
                "mortal_call" or "vaal_immortal_call" or "elemental_hit_last_damage_type" or "skill_cost_over_time" => OneZero,
                _ => null,
            },
        };
    }

    /// <summary>The wire list for <paramref name="info"/>: keeps caller-supplied words and fills any missing words
    /// required by <see cref="DefaultList"/> so the client never dereferences a null or short <c>Buff+0x80</c> buffer.</summary>
    public static IReadOnlyList<uint>? WireList(Row? info, IReadOnlyList<uint>? list)
    {
        if (DefaultList(info) is not { } defaults) return list;
        if (list is null || list.Count == 0) return defaults;
        if (list.Count >= defaults.Count) return list;
        var padded = new uint[defaults.Count];
        for (int i = 0; i < list.Count; i++) padded[i] = list[i];
        for (int i = list.Count; i < defaults.Count; i++) padded[i] = defaults[i];
        return padded;
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
        ArgumentNullException.ThrowIfNull(row.GrantedStats);
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
                r.IntOrNull("__col7") ?? 0, r.IntOrNull("BuffLimit") ?? 0, r.BoolOrNull("Invisible") ?? false)
            {
                Behaviour = r.Text("Id2") ?? "",
            });
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
                if (byIndex.TryGetValue(r.Index, out var prevByIndex)
                    && !string.Equals(prevByIndex.Id, r.Id, StringComparison.Ordinal)
                    && byId.TryGetValue(prevByIndex.Id, out ushort mappedIdx) && mappedIdx == r.Index)
                    byId.Remove(prevByIndex.Id);
                if (byId.TryGetValue(r.Id, out ushort prevIdx) && prevIdx != r.Index)
                    byIndex.Remove(prevIdx);
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
            U82: info is { HasU82: true } ? (ushort)0 : null, List: DefaultList(info), Tag: tag);
    }

    sealed class Undo(Action undo) : IDisposable
    {
        Action? _undo = undo;
        public void Dispose() => Interlocked.Exchange(ref _undo, null)?.Invoke();
    }
}
