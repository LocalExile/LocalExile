using System.Runtime.CompilerServices;
using GameServer.Core;
using GameServer.Core.World;
using static Program;

/// <summary>
/// Debuffs that monsters' skills and grounds put on the player, for any of them: a curse, Withered, a trap's immobilise,
/// a tether's slow, standing in burning, caustic or desecrated ground. Each is a BuffDefinitions row with the stats it
/// gives the player (by name), drawn with S→C 0x1c1 (BuffRows.Add) and ended with 0x1c2.
/// <para>Its stats join the player's stat sources (GameEvents.PlayerStatSources: resistances, damage taken, movement),
/// and its <c>base_{type}_damage_taken_per_minute</c> (every ground_* row's: ground_fire_burn, caustic_cloud,
/// ground_desecration) is degeneration, less the player's resistance to the type, which the recovery deals
/// (Player.Degens).</para>
/// <para>A row that stacks (BuffRows.Row.Stacks: withered, tethered_debuff) adds an instance up to its limit, the one
/// that ends first giving way past it; any other row has one instance, which a new application replaces. A held debuff
/// (a ground's, while the player stands in it) has no time and ends when its holder lets go. None outlives the area.</para>
/// </summary>
public static partial class Game
{
    /// <summary>One debuff on the player: its 0x1c1 instance, row, the object it came from, its stats, when it ends
    /// (connection clock seconds; infinity while held) and its holder (null for a timed one).</summary>
    public sealed record PlayerDebuff(ushort Instance, ushort Row, uint Caster, IReadOnlyDictionary<string, int> Stats,
        double Until, object? Holder);

    sealed class PlayerDebuffSet
    {
        public readonly List<PlayerDebuff> Active = [];
        public object? Area;
    }

    static readonly ConditionalWeakTable<Player, PlayerDebuffSet> DebuffsOf = new();

    /// <summary>A debuff for its time (seconds): a curse, Withered, an immobilise.</summary>
    public static Task AddPlayerDebuff(Player conn, ushort row, uint caster, IReadOnlyDictionary<string, int> stats,
        double seconds) => ApplyPlayerDebuff(conn, row, caster, stats, seconds, null);

    /// <summary>A debuff kept until <see cref="ReleasePlayerDebuff"/> with the same holder (a ground's, while the player
    /// stands in it). Holding the same row with the same stats again changes nothing.</summary>
    public static Task HoldPlayerDebuff(Player conn, object holder, ushort row, uint caster, IReadOnlyDictionary<string, int> stats) =>
        ApplyPlayerDebuff(conn, row, caster, stats, double.PositiveInfinity, holder);

    /// <summary>Ends the debuffs <paramref name="holder"/> keeps.</summary>
    public static async Task ReleasePlayerDebuff(Player conn, object holder)
    {
        if (!DebuffsOf.TryGetValue(conn, out var set)) return;
        List<ushort> gone;
        lock (conn.Monsters)
        {
            gone = [.. set.Active.Where(d => ReferenceEquals(d.Holder, holder)).Select(d => d.Instance)];
            if (gone.Count == 0) return;
            set.Active.RemoveAll(d => ReferenceEquals(d.Holder, holder));
        }
        await EndPlayerDebuffs(conn, set, gone);
    }

    /// <summary>Whether a debuff of this row is on the player now.</summary>
    public static bool HasPlayerDebuff(Player conn, ushort row)
    {
        if (!DebuffsOf.TryGetValue(conn, out var set)) return false;
        lock (conn.Monsters)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            return ReferenceEquals(set.Area, conn.Layout) && set.Active.Any(d => d.Row == row && d.Until > now);
        }
    }

    static async Task ApplyPlayerDebuff(Player conn, ushort row, uint caster, IReadOnlyDictionary<string, int> stats,
        double seconds, object? holder)
    {
        if (conn.Closed || conn.LeavingArea || conn.PlayerLife == 0 || InGracePeriod(conn) || seconds <= 0) return;
        var set = DebuffsOf.GetValue(conn, _ => new());
        var info = BuffRows.Of(row);
        List<ushort> replaced = [];
        ushort instance;
        lock (conn.Monsters)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            if (!ReferenceEquals(set.Area, conn.Layout)) { set.Active.Clear(); set.Area = conn.Layout; }
            if (holder is not null && set.Active.FirstOrDefault(d => ReferenceEquals(d.Holder, holder) && d.Row == row) is { } held)
            {
                if (SameStats(held.Stats, stats)) return;
                replaced.Add(held.Instance);
                set.Active.Remove(held);
            }
            foreach (var expired in set.Active.Where(d => d.Until <= now).ToArray()) { replaced.Add(expired.Instance); set.Active.Remove(expired); }
            if (row != 0 && info is { Stacks: true })
            {
                var same = set.Active.Where(d => d.Row == row && d.Holder is null).ToList();
                if (holder is null && info.Limit > 0 && same.Count >= info.Limit)
                {
                    var first = same.MinBy(d => d.Until)!;
                    replaced.Add(first.Instance);
                    set.Active.Remove(first);
                }
            }
            else if (row != 0 && holder is null)
                foreach (var same in set.Active.Where(d => d.Row == row && d.Holder is null).ToArray())
                {
                    replaced.Add(same.Instance);
                    set.Active.Remove(same);
                }
            instance = conn.NextBuff++;
            set.Active.Add(new(instance, row, caster, stats, holder is null ? now + seconds : double.PositiveInfinity, holder));
        }
        foreach (ushort old in replaced)
            await Send(conn, GameServer.Core.Protocol.Instance.InstanceEntities.RemoveBuff(PlayerId, old), null);
        if (row != 0)
            await Send(conn, BuffRows.Add(PlayerId, instance, row, caster, stats, holder is null ? seconds : null),
                $"0x01c1 the player gets {info?.Id ?? row.ToString()} from 0x{caster:x}" + (holder is null ? $" for {seconds:0.#} s" : " while in it"));
        await RefreshDebuffedPlayer(conn);
    }

    static bool SameStats(IReadOnlyDictionary<string, int> a, IReadOnlyDictionary<string, int> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out int v) && v == p.Value);

    /// <summary>Each AI step: the debuffs whose time is up end; after an area change the old area's are dropped (the new
    /// instance has none of their looks to remove).</summary>
    static async Task PlayerDebuffsTick(Player conn)
    {
        if (!DebuffsOf.TryGetValue(conn, out var set)) return;
        List<ushort> gone = [];
        bool areaChanged;
        lock (conn.Monsters)
        {
            if (set.Active.Count == 0) return;
            areaChanged = !ReferenceEquals(set.Area, conn.Layout);
            double now = conn.Clock.Elapsed.TotalSeconds;
            foreach (var d in set.Active.Where(d => areaChanged || d.Until <= now).ToArray())
            {
                if (!areaChanged) gone.Add(d.Instance);
                set.Active.Remove(d);
            }
            if (areaChanged) set.Area = conn.Layout;
            // The resistances the degeneration is less of can change with anything else the player has.
            conn.Degens[set] = DebuffDegenLocked(conn);
            if (gone.Count == 0 && !areaChanged) return;
        }
        await EndPlayerDebuffs(conn, set, gone);
    }

    static async Task EndPlayerDebuffs(Player conn, PlayerDebuffSet set, IReadOnlyList<ushort> instances)
    {
        if (!conn.Closed)
            foreach (ushort instance in instances)
                await Send(conn, GameServer.Core.Protocol.Instance.InstanceEntities.RemoveBuff(PlayerId, instance), null);
        await RefreshDebuffedPlayer(conn);
    }

    /// <summary>The player's pools, movement and degeneration after the debuffs changed, and the stats the client shows.</summary>
    static async Task RefreshDebuffedPlayer(Player conn)
    {
        if (conn.Closed) return;
        lock (conn.Monsters)
        {
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
            conn.ApplyMovementSpeed();
            conn.Degens[DebuffsOf.GetValue(conn, _ => new())] = DebuffDegenLocked(conn);
        }
        await SendPlayerStatSnapshot(conn, () => PlayerStatsOffFrames(conn), null);
    }

    /// <summary>The debuffs' degeneration, points a second: each type's <c>base_{type}_damage_taken_per_minute</c> over
    /// a minute, less the player's resistance to it (none for physical; nothing of a type the player is immune to).</summary>
    static double DebuffDegenLocked(Player conn)
    {
        if (!DebuffsOf.TryGetValue(conn, out var set) || !ReferenceEquals(set.Area, conn.Layout)) return 0;
        double now = conn.Clock.Elapsed.TotalSeconds, total = 0;
        var immune = PlayerImmunities(conn);
        foreach (var type in new[] { DamageType.Physical, DamageType.Fire, DamageType.Cold, DamageType.Lightning, DamageType.Chaos })
        {
            string stat = $"base_{type.ToString().ToLowerInvariant()}_damage_taken_per_minute";
            double perMinute = set.Active.Where(d => d.Until > now).Sum(d => (double)d.Stats.GetValueOrDefault(stat));
            if (perMinute <= 0 || immune.Contains(type)) continue;
            double resisted = type == DamageType.Physical ? 0 : Math.Min(MaxResistance(conn, type), Resistance(conn, type));
            total += perMinute / 60 * (1 - resisted / 100.0);
        }
        return Math.Max(0, total);
    }

    /// <summary>GameEvents.PlayerStatSources: the debuffs on the player add their stats.</summary>
    public static Dictionary<uint, int> PlayerDebuffStats(Player conn, Dictionary<uint, int> sources)
    {
        if (!DebuffsOf.TryGetValue(conn, out var set) || !ReferenceEquals(set.Area, conn.Layout)) return sources;
        double now = conn.Clock.Elapsed.TotalSeconds;
        foreach (var debuff in set.Active.Where(d => d.Until > now))
            foreach (var (name, value) in debuff.Stats)
                if (Stats.Key(name) is uint key) sources[key] = sources.GetValueOrDefault(key) + value;
        return sources;
    }
}
