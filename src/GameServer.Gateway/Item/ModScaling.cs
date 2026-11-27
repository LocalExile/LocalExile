using System.Collections.Frozen;
using GameServer.Core;

/// <summary>
/// Whether a number on an item line is unscalable: set in stone once rolled, so nothing that changes a line's numbers
/// afterwards touches it (increased effect of modifiers, catalyst quality, enchantment magnitudes, a bundle given at
/// less than its full effect, and whatever is built later that rewrites an item: a Reflecting Mist, a reroll).
///
/// The game's own data says it per stat (Stats IsScalable) and per line (Mods IsUnscalable). The fx tables hold the
/// same flags as rows of their own (fx_stats.unscalable for a stat, fx_lines for one line, a Mods Id), seeded from the
/// game's and read here instead, so a new unique's line can be made unscalable, or a line that is can be opened up,
/// by changing a row. A mod does the same while it is loaded with <see cref="Override"/>.
///
/// What decides, first that says anything: a mod's override for the line, a mod's override for the stat, the line's
/// row, the stat's row, the game's data.
/// </summary>
public static class ModScaling
{
    sealed record Flags(FrozenDictionary<uint, bool> Stats, FrozenDictionary<string, bool> Lines);
    static readonly Flags None = new(FrozenDictionary<uint, bool>.Empty, FrozenDictionary<string, bool>.Empty);
    static Flags _table = None, _overrides = None;
    static readonly List<(object Owner, string? Line, uint Stat, bool Unscalable)> Overrides = [];
    static FrozenSet<uint>? _client;
    static int _version;

    /// <summary>Changes whenever the flags do: what a caller keys a remembered answer by.</summary>
    public static int Version => Volatile.Read(ref _version);

    /// <summary>Takes the fx tables' flags when the game data loads.</summary>
    public static void LoadTable(IReadOnlyDictionary<string, bool> stats, IReadOnlyDictionary<string, bool> lines)
    {
        var byKey = new Dictionary<uint, bool>();
        foreach (var (name, unscalable) in stats)
            if (Program.Stats.Key(name) is uint key) byKey[key] = unscalable;
        Volatile.Write(ref _table, new(byKey.ToFrozenDictionary(), lines.ToFrozenDictionary(StringComparer.Ordinal)));
        Interlocked.Increment(ref _version);
        if (byKey.Count + lines.Count > 0)
            Console.WriteLine($"[rules] {byKey.Count(p => p.Value)} unscalable stat(s) and {lines.Count(p => p.Value)} unscalable line(s) from the fx tables");
    }

    /// <summary>Whether the number of this stat on this line is unscalable.</summary>
    public static bool Unscalable(ModCatalog.Mod row, int index)
    {
        var stat = row.Stats[index];
        var overrides = Volatile.Read(ref _overrides);
        if (!ReferenceEquals(overrides, None))
        {
            if (overrides.Lines.TryGetValue(row.Id, out bool byMod)) return byMod;
            if (overrides.Stats.TryGetValue(stat.Id, out byMod)) return byMod;
        }
        var table = Volatile.Read(ref _table);
        if (table.Lines.Count > 0 && table.Lines.TryGetValue(row.Id, out bool line)) return line;
        if (table.Stats.TryGetValue(stat.Id, out bool own)) return own;
        return row.Unscalable || !stat.Scalable;
    }

    /// <summary>Whether a stat's number is unscalable where there is no line to ask (a stat listed by hand).</summary>
    public static bool Unscalable(uint stat)
    {
        if (Volatile.Read(ref _overrides).Stats.TryGetValue(stat, out bool byMod)) return byMod;
        if (Volatile.Read(ref _table).Stats.TryGetValue(stat, out bool own)) return own;
        return (_client ??= ClientUnscalable()).Contains(stat);
    }

    static FrozenSet<uint> ClientUnscalable() => !ClientTables.Has("Stats") ? FrozenSet<uint>.Empty
        : ClientTables.Table("Stats").Where(r => r.BoolOrNull("IsScalable") == false)
            .Select(r => Program.Stats.Key(r.Text("Id") ?? "")).OfType<uint>().ToFrozenSet();

    /// <summary>A hand-over for mods: while the result is not disposed, the line (a Mods Id) or the stat (a Stats Id)
    /// is unscalable or not as given here, whatever the tables and the game's data say. Give one of the two names.</summary>
    public static IDisposable Override(string? line, string? stat, bool unscalable)
    {
        if ((line is null) == (stat is null)) throw new ArgumentException("name a line or a stat, not both");
        uint key = stat is null ? 0 : Program.Stats.Key(stat) ?? throw new ArgumentException($"unknown stat \"{stat}\"");
        var owner = new object();
        lock (Overrides) { Overrides.Add((owner, line, key, unscalable)); Rebuild(); }
        return new Taken(owner);
    }

    static void Rebuild()
    {
        Interlocked.Increment(ref _version);
        Volatile.Write(ref _overrides, Overrides.Count == 0 ? None : new(
        Overrides.Where(o => o.Line is null).GroupBy(o => o.Stat).ToFrozenDictionary(g => g.Key, g => g.Last().Unscalable),
        Overrides.Where(o => o.Line is not null).GroupBy(o => o.Line!).ToFrozenDictionary(g => g.Key, g => g.Last().Unscalable, StringComparer.Ordinal)));
    }

    sealed class Taken(object owner) : IDisposable
    {
        public void Dispose() { lock (Overrides) { Overrides.RemoveAll(o => ReferenceEquals(o.Owner, owner)); Rebuild(); } }
    }
}
