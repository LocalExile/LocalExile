using GameServer.Core;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// BuffDefinitions rows as S→C 0x1c1 needs them (InstanceEntities.AddBuff): a row with the +0xff bool (<c>__col44</c>)
/// takes a u16 +0x82, and an IsRecovery row (+0x7c) one u32 per GrantedStats entry, or the client misreads the message.
/// <see cref="Add"/> writes any row's message from the buff's stats by name, so a caller cannot get that wrong. Also the
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

    static List<Row?> Rows = [];
    static Dictionary<string, ushort> ById = new(StringComparer.Ordinal);

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
        (Rows, ById) = (rows, byId);
        FinalMovementKeys = [.. rows.SelectMany(r => r?.GrantedStats ?? []).Distinct()
            .Where(n => Program.Stats.Name(n).EndsWith("movement_speed_+%_final", StringComparison.Ordinal))];
    }

    /// <summary>The final movement modifiers buffs grant their target (tethered_movement_speed_+%_final,
    /// bear_trap_movement_speed_+%_final, labyrinth_arrow_movement_speed_+%_final, Toxic Rain's vines'...): each
    /// multiplies the target's whole movement speed.</summary>
    public static IReadOnlyList<uint> FinalMovementKeys { get; private set; } = [];

    public static Row? Of(ushort row) => row < Rows.Count ? Rows[row] : null;

    /// <summary>The row of this Id, 0 when there is none.</summary>
    public static ushort Find(string id) => ById.GetValueOrDefault(id);

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
}
