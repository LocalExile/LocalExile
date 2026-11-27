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
    const ushort TemporalChainsPlayerBuffRow = 69;

    /// <summary>One debuff on the player: its 0x1c1 instance, row, the object it came from, its stats, when it ends
    /// (connection clock seconds; infinity while held) and its holder (null for a timed one).</summary>
    public sealed record PlayerDebuff(ushort Instance, ushort Row, uint Caster, IReadOnlyDictionary<uint, int> Stats,
        double Until, object? Holder);

    sealed class PlayerDebuffSet
    {
        public readonly List<PlayerDebuff> Active = [];
        public object? Area;
        public PlayerDebuff? TemporalChains;
    }

    static readonly ConditionalWeakTable<Player, PlayerDebuffSet> DebuffsOf = new();

    /// <summary>A debuff for its time (seconds): a curse, Withered, an immobilise.</summary>
    public static Task AddPlayerDebuff(Player player, ushort row, uint caster, IReadOnlyDictionary<uint, int> stats,
        double seconds) => ApplyPlayerDebuff(player, row, caster, stats, seconds, null);

    /// <summary>A debuff kept until <see cref="ReleasePlayerDebuff"/> with the same holder (a ground's, while the player
    /// stands in it). Holding the same row with the same stats again changes nothing.</summary>
    public static Task HoldPlayerDebuff(Player player, object holder, ushort row, uint caster, IReadOnlyDictionary<uint, int> stats) =>
        ApplyPlayerDebuff(player, row, caster, stats, double.PositiveInfinity, holder);

    /// <summary>Ends the debuffs <paramref name="holder"/> keeps.</summary>
    public static async Task ReleasePlayerDebuff(Player player, object holder)
    {
        if (!DebuffsOf.TryGetValue(player, out var set)) return;
        List<PlayerDebuff> gone = [.. set.Active.Where(d => ReferenceEquals(d.Holder, holder))];
        if (gone.Count == 0) return;
        set.Active.RemoveAll(d => ReferenceEquals(d.Holder, holder));
        await EndPlayerDebuffs(player, set, gone);
    }

    /// <summary>Whether a debuff of this row is on the player now.</summary>
    public static bool HasPlayerDebuff(Player player, ushort row)
    {
        if (!DebuffsOf.TryGetValue(player, out var set)) return false;
        double now = AreaTime(player);
        return ReferenceEquals(set.Area, player.Layout) && set.Active.Any(d => d.Row == row && d.Until > now);
    }

    static readonly IReadOnlyDictionary<ushort, KeyValuePair<string, CurseCatalog.Definition>> PlayerCurseRows =
        CurseCatalog.All.GroupBy(curse => curse.Value.Buff).ToDictionary(group => group.Key, group => group.First());

    static async Task ApplyPlayerDebuff(Player player, ushort row, uint caster, IReadOnlyDictionary<uint, int> stats,
        double seconds, object? holder)
    {
        if (player.Closed || player.LeavingArea || player.PlayerLife == 0 || InGracePeriod(player) || seconds <= 0) return;
        if (row == MaimedBuffRow && PlayerAvoidsMaim(PlayerStatValues(player).GetValueOrDefault, AreaRng(player)))
            return;
        double now = AreaTime(player);
        bool curse = PlayerCurseRows.TryGetValue(row, out var definition);
        bool exposure = Array.IndexOf(ExposureBuffRows, row) >= 0;
        var defence = curse || exposure ? PlayerStatValues(player) : null;
        if (holder is null && caster != 0 && curse && !definition.Value.Mark && defence is not null
            && defence.GetValueOrDefault(Stat.ReflectHexesChancePct) is > 0 and var reflectChance
            && RandomRolls.Chance(reflectChance, 100, AreaRng(player)))
        {
            var reflected = new Dictionary<uint, int>(stats);
            reflected.TryAdd(Stat.BaseSkillEffectDuration, Math.Max(1000, (int)Math.Round(seconds * 1000)));
            var layout = player.Layout;
            void Reflect(GameArea area)
            {
                if (player.Closed || player.LeavingArea || !ReferenceEquals(player.Layout, layout)) return;
                if (area.Monsters.TryGetValue(caster, out var casterMonster)
                    && casterMonster is { Dead: false, Life: > 0, Hostile: true })
                    ApplyCurseDataLocked(player, casterMonster, 0, definition.Key, reflected, AreaTime(player), false, CharacterPlayerStats(player));
            }
            if (player.Frames.Running) InArea(player, Reflect);
            else Reflect(player.Area);
        }
        if (defence is not null && (exposure && defence.GetValueOrDefault(Stat.ImmuneToExposure) != 0
            || curse && (CurseImmunity(defence.GetValueOrDefault) || player.FlaskGrounds.CurseImmune || WitchbaneImmunityActive(player, now))))
            return;
        var set = DebuffsOf.GetValue(player, _ => new());
        var info = BuffRows.Of(row);
        List<PlayerDebuff> replaced = [];
        ushort instance;
        if (!ReferenceEquals(set.Area, player.Layout)) { set.Active.Clear(); set.Area = player.Layout; set.TemporalChains = null; }
        if (holder is not null && set.Active.FirstOrDefault(d => ReferenceEquals(d.Holder, holder) && d.Row == row) is { } held)
        {
            if (SameStats(held.Stats, stats)) return;
            replaced.Add(held);
            set.Active.Remove(held);
        }
        foreach (var expired in set.Active.Where(d => d.Until <= now).ToArray()) { replaced.Add(expired); set.Active.Remove(expired); }
        if (row != 0 && info is { Stacks: true })
        {
            var same = set.Active.Where(d => d.Row == row && d.Holder is null).ToList();
            if (holder is null && info.Limit > 0 && same.Count >= info.Limit)
            {
                var first = same.MinBy(d => d.Until)!;
                replaced.Add(first);
                set.Active.Remove(first);
            }
        }
        else if (row != 0 && holder is null)
            foreach (var same in set.Active.Where(d => d.Row == row && d.Holder is null
                && (row != TemporalChainsPlayerBuffRow || d.Caster == caster)).ToArray())
            {
                replaced.Add(same);
                set.Active.Remove(same);
            }
        instance = player.NextBuff++;
        set.Active.Add(new(instance, row, caster, stats, holder is null ? now + seconds : double.PositiveInfinity, holder));
        foreach (var old in replaced) ClearPlayerDebuff(player, old);
        // Each debuff is a buff of its own (its instance number is its tag: a row that stacks has several from one caster).
        if (row != 0)
        {
            if (row != TemporalChainsPlayerBuffRow)
                player.Area.Buffs.Set(player.Id, BuffRows.Spec(row, caster, stats, holder is null ? seconds : null, instance));
            Console.WriteLine($"[gateway] the player gets {info?.Id ?? row.ToString()} from 0x{caster:x}" + (holder is null ? $" for {seconds:0.#} s" : " while in it"));
        }
        await RefreshDebuffedPlayer(player);
    }

    sealed record ChannelCurseRemoval(int Generation, double At);
    static readonly Store<Player, ChannelCurseRemoval> ChannelCurseRemovals = new();

    static void ArmChannelCurseRemoval(Player player, int generation, double now)
    {
        ChannelCurseRemovals.Remove(player);
        if (CombatLifetime is { IsActive: true }
            && PlayerStatValues(player).GetValueOrDefault(Stat.RemoveXCursesAfterChannellingFor2Seconds) > 0)
            ChannelCurseRemovals.Add(player, new(generation, now + 2));
    }

    static int DueChannelCurseRemoval(Player player, double now)
    {
        if (!ChannelCurseRemovals.TryGetValue(player, out var removal) || now < removal.At) return 0;
        ChannelCurseRemovals.Remove(player);
        return player.PaidChannel is { } payment && payment.Generation == removal.Generation && PaidChannelCurrent(player, payment)
            ? PlayerStatValues(player).GetValueOrDefault(Stat.RemoveXCursesAfterChannellingFor2Seconds) : 0;
    }

    /// <summary>Each AI step: the debuffs whose time is up end; after an area change the old area's are dropped (the new
    /// instance has none of their looks to remove).</summary>
    static async Task PlayerDebuffsTick(Player player)
    {
        double now = AreaTime(player);
        int removeCurses = DueChannelCurseRemoval(player, now);
        if (!DebuffsOf.TryGetValue(player, out var set) || set.Active.Count == 0) return;
        List<PlayerDebuff> gone = [];
        bool areaChanged = !ReferenceEquals(set.Area, player.Layout);
        foreach (var d in set.Active.Where(d => areaChanged || d.Until <= now).ToArray())
        {
            if (!areaChanged) gone.Add(d);
            set.Active.Remove(d);
        }
        if (areaChanged) { set.Area = player.Layout; set.TemporalChains = null; }
        else if (removeCurses > 0)
        {
            // Persistent sources keep their own conditions; this dispels applied, timed curses.
            PlayerDebuff[] curses = [.. set.Active.Where(debuff => debuff.Holder is null && PlayerCurseRows.ContainsKey(debuff.Row))
                .Take(removeCurses)];
            foreach (var curse in curses) set.Active.Remove(curse);
            gone.AddRange(curses);
        }
        // The resistances the degeneration is less of can change with anything else the player has.
        player.Degens[set] = DebuffDegenLocked(player);
        if (gone.Count == 0 && !areaChanged) return;
        await EndPlayerDebuffs(player, set, gone);
    }

    static void ClearPlayerDebuff(Player player, PlayerDebuff debuff)
    {
        if (debuff.Row != 0 && debuff.Row != TemporalChainsPlayerBuffRow)
            player.Area.Buffs.Clear(player.Id, debuff.Row, debuff.Caster, tag: debuff.Instance);
    }

    static async Task EndPlayerDebuffs(Player player, PlayerDebuffSet set, IReadOnlyList<PlayerDebuff> debuffs)
    {
        if (!player.Closed)
            foreach (var debuff in debuffs) ClearPlayerDebuff(player, debuff);
        await RefreshDebuffedPlayer(player);
    }

    /// <summary>The player's pools, movement and degeneration after the debuffs changed, and the stats the client shows.</summary>
    static async Task RefreshDebuffedPlayer(Player player)
    {
        if (player.Closed) return;
        var set = DebuffsOf.GetValue(player, _ => new());
        double now = AreaTime(player);
        var curse = set.Active.Where(d => d.Row == TemporalChainsPlayerBuffRow && d.Until > now)
            .MinBy(d => (TemporalChainsSlow(d), d.Stats.GetValueOrDefault(Stat.BuffTimePassedPlusPctOtherThanTemporalChains), -d.Until));
        if (!ReferenceEquals(curse, set.TemporalChains))
        {
            if (set.TemporalChains is { } old)
                player.Area.Buffs.Clear(player.Id, old.Row, old.Caster, tag: old.Instance);
            set.TemporalChains = curse;
            if (curse is not null)
                player.Area.Buffs.Set(player.Id, BuffRows.Spec(curse.Row, curse.Caster, curse.Stats,
                    double.IsFinite(curse.Until) ? curse.Until - now : null, curse.Instance));
        }
        player.Pools = PoolsFor(player, player.Pools.Level);
        player.Degens[set] = DebuffDegenLocked(player);
        if (!PublishByFrame(player, PlayerStatChange.Sources))
        {
            player.ApplyMovementSpeedAt(now);
            await SendPlayerStatSnapshot(player, () => PlayerStatsOffFrames(player), null);
        }
    }

    // Held sources such as Doedre's pillar also use this buff row.
    static long TemporalChainsSlow(PlayerDebuff debuff) => debuff.Stats.GetValueOrDefault(Stat.TemporalChainsActionSpeedPlusPctFinal)
        - (long)debuff.Stats.GetValueOrDefault(Stat.ActionSpeedMinusPct);

    /// <summary>The debuffs' degeneration, points a second: each type's <c>base_{type}_damage_taken_per_minute</c> over
    /// a minute, less the player's resistance to it (none for physical; nothing of a type the player is immune to).</summary>
    static double DebuffDegenLocked(Player player)
    {
        if (!DebuffsOf.TryGetValue(player, out var set) || !ReferenceEquals(set.Area, player.Layout)) return 0;
        double now = AreaTime(player), total = 0;
        var immune = PlayerImmunities(player);
        foreach (var type in new[] { DamageType.Physical, DamageType.Fire, DamageType.Cold, DamageType.Lightning, DamageType.Chaos })
        {
            uint stat = Stat.Of($"base_{type.ToString().ToLowerInvariant()}_damage_taken_per_minute");
            double perMinute = set.Active.Where(d => d.Until > now).Sum(d => (double)d.Stats.GetValueOrDefault(stat));
            if (perMinute <= 0 || immune.Contains(type)) continue;
            int extraRes = type == DamageType.Chaos ? PlayerStat(player, Stat.AdditionalChaosResistanceAgainstDamageOverTimePct) : 0;
            double resisted = type == DamageType.Physical ? 0 : Math.Min(MaxResistance(player, type), Resistance(player, type) + extraRes);
            double rate = perMinute / 60 * (1 - resisted / 100.0);
            // "#% increased Chaos Damage taken over time" (Solaris Lorica), with the caustic cloud's own.
            if (type == DamageType.Chaos)
                rate *= Math.Max(0, 1 + (PlayerStat(player, Stat.ChaosDamageTakenOverTimePlusPct)
                    + (player.InCausticCloud ? PlayerStat(player, Stat.ChaosDamageTakenOverTimePlusPctWhileInCausticCloud) : 0)) / 100.0);
            else if (type == DamageType.Physical && player.Walk.Moving(now))
                rate *= Math.Max(0, 1 + PlayerStat(player, Stat.PhysicalDamageOverTimeTakenPlusPctWhileMoving) / 100.0);
            total += rate;
        }
        return Math.Max(0, total);
    }

    /// <summary>GameEvents.PlayerStatSources: the debuffs on the player add their stats.</summary>
    public static Dictionary<uint, int> PlayerDebuffStats(Player player, Dictionary<uint, int> sources)
    {
        if (!DebuffsOf.TryGetValue(player, out var set) || !ReferenceEquals(set.Area, player.Layout)) return sources;
        double now = AreaTime(player);
        foreach (var debuff in set.Active.Where(d => d.Until > now))
        {
            if (debuff.Row == TemporalChainsPlayerBuffRow && !ReferenceEquals(debuff, set.TemporalChains)) continue;
            foreach (var (name, value) in debuff.Stats)
                sources[name] = sources.GetValueOrDefault(name) + value;
        }
        return sources;
    }
}
