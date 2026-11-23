using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Modding;

/// <summary>
/// Bino's Kitchen Knife's regeneration (BuffDefinitions <c>life_regen_from_spread_poison</c>): 400 Life per second while
/// the spread poison lasts. Timed and presented like <see cref="KillOnslaught"/>; another player's kill can grant it, so
/// its own lock guards it instead of the owner's Monsters lock.
/// </summary>
public sealed class SpreadPoisonRegeneration
{
    public const ushort BuffRow = 618;
    public const int PerMinute = 400 * 60;
    readonly Lock gate = new();
    double until = double.NegativeInfinity, sentUntil = double.NegativeInfinity;
    ushort buff;
    readonly List<(double From, double Until)> recovery = [];
    double recoveredUntil = double.NegativeInfinity;

    public bool Applied { get { lock (gate) return double.IsFinite(sentUntil); } }
    /// <summary>When the granted regeneration ends.</summary>
    public double Until { get { lock (gate) return until; } }

    /// <summary>A kill at <paramref name="now"/> grants <paramref name="seconds"/>; a shorter grant never shortens it.</summary>
    public bool Grant(double now, double seconds, bool trackRecovery = false)
    {
        lock (gate)
        {
            if (!(seconds > 0) || now + seconds <= until) return false;
            until = now + seconds;
            if (trackRecovery)
            {
                if (recovery.Count > 0 && recovery[^1].Until >= now)
                    recovery[^1] = (recovery[^1].From, until);
                else recovery.Add((now, until));
            }
            return true;
        }
    }

    /// <summary>Recovery earned since this ally's preceding regeneration step, clipped to each grant's lifetime.
    /// Overlapping grants extend the same effect; separated grants preserve the gap.</summary>
    public double Recover(double now)
    {
        lock (gate)
        {
            double seconds = recovery.Sum(w => Math.Max(0, Math.Min(now, w.Until) - Math.Max(recoveredUntil, w.From)));
            recoveredUntil = Math.Max(recoveredUntil, now);
            recovery.RemoveAll(w => w.Until <= now);
            return seconds * PerMinute / 60;
        }
    }

    /// <summary>As <see cref="KillOnslaught.Present"/>: the buff to remove (0 for none), the new buff numbered by
    /// <paramref name="next"/> with its seconds (null for none), whether the stats change. Death ends it.</summary>
    public (ushort Removed, (ushort Buff, double Seconds)? Added, bool Stats)? Present(double now, bool alive, Func<ushort> next)
    {
        lock (gate)
        {
            if (!alive) { until = double.NegativeInfinity; recovery.Clear(); }
            double running = now < until ? until : double.NegativeInfinity;
            bool applied = double.IsFinite(running), was = double.IsFinite(sentUntil);
            bool remove = buff != 0 && !applied;
            bool add = applied && (buff == 0 || running - sentUntil >= 1 || now >= sentUntil);
            if (!remove && !add && applied == was) return null;
            ushort removed = remove || add ? buff : (ushort)0;
            (ushort, double)? added = null;
            if (add) added = (buff = next(), running - now);
            else if (remove) buff = 0;
            sentUntil = running;
            return (removed, added, applied != was);
        }
    }
}

public static partial class Game
{
    /// <summary>"Enemies within 3 metres" (10 cells to the metre).</summary>
    public const int PoisonSpreadRadius = 30;

    static readonly Store<Player, SpreadPoisonRegeneration> SpreadPoisonRegenerations = new();
    static readonly RuleTable<SpawnedMonster, SpreadPoisonRegeneration> AllySpreadPoisonRegenerations = new();
    public static SpreadPoisonRegeneration SpreadPoisonRegenerationOf(Player player) =>
        SpreadPoisonRegenerations.GetValue(player, _ => new());

    // The ordinary monster regeneration step also owns minion and other allied-monster recovery. No second timer.
    static double SpreadPoisonRecovery(SpawnedMonster monster, double now) =>
        AllySpreadPoisonRegenerations.TryGetValue(monster, out var regeneration) ? regeneration.Recover(now) * PanelStats.LifeRegenerationMultiplier(name => monster.Effects[name]) : 0;

    static int SourceStat(Player player, uint name) => player.StatSourcesCache is { } cached
        ? cached.Sources.GetValueOrDefault(name) : 0;

    /// <summary>Bino's Kitchen Knife (GameEvents.Kill): the player killed a poisoned enemy, so its strongest poison stack
    /// is applied as a new poison (<see cref="SpreadPoisonSeconds"/>) to every living enemy within 3 metres of it, and
    /// the player and living allies within 3 metres regenerate Life for that time. A spread poison can spread again from its own kill.</summary>
    public static void SpreadPoisonOnKill(KillContext kill)
    {
        var player = kill.Player;
        if (!CurrentGameWork || player.Closed || player.LeavingArea || !kill.PlayerKill || SourceStat(player, Stat.UniqueSpreadPoisonToNearbyEnemiesOnKill) <= 0) return;
        bool regenerates = SourceStat(player, Stat.UniqueSpreadPoisonToNearbyAlliesAs200LifeRegenerationPerSecondOnKill) > 0;
        var dead = kill.Monster;
        int spread = 0;
        double dps, seconds;
        if (player.PlayerLife == 0) return;
        double now = ProjectileWorldTime(player);
        if (dead.Poisons.Strongest(now) is not var (perSecond, _)) return;
        dps = perSecond;
        seconds = SpreadPoisonSeconds(name => SourceStat(player, name));
        foreach (var (mid, m) in player.Area.Monsters)
        {
            if (ReferenceEquals(m, dead) || m.Dead || m.Life == 0 || !m.Hostile
                || Dist(m.Walk.Position(now), kill.At) > PoisonSpreadRadius) continue;
            if (m.Effects[Stat.CannotBePoisoned] != 0) continue;
            int avoid = m.Effects[Stat.AvoidPoisonPct];
            if (avoid > 0 && RandomRolls.Chance(avoid, 100, AreaRng(player))) continue;
            double lasts = seconds * MonsterPoisons.SelfDuration(name => m.Effects[name]);
            if (m.Poisons.Add(now, dps, lasts))
            {
                player.Area.ActivePoisonMonsters.Add(mid);
                InflictedLocked(player, m, AilmentType.Poison, now + lasts);
                spread++;
            }
        }
        if (spread > 0) ReservationPoisonLocked(player, now);
        if (regenerates)
            foreach (var ally in player.Area.Monsters.Values)
                if (ally is { Hostile: false, Dead: false, Life: > 0 }
                    && Dist(ally.Walk.Position(now), kill.At) <= PoisonSpreadRadius)
                    AllySpreadPoisonRegenerations.GetValue(ally, _ => new()).Grant(now, seconds, trackRecovery: true);
        PresentStats(player);
        Console.WriteLine($"[gateway] Bino's: poison spread to {spread} enemies ({dps:0} dps, {seconds:0.0} s)");
        if (!regenerates) return;
        if (player.PlayerLife > 0 && Dist(player.Walk.Position(AreaTime(player)), kill.At) <= PoisonSpreadRadius)
            SpreadPoisonRegenerationOf(player).Grant(AreaTime(player), seconds);
        // Another player's grant is theirs, on their clock; their Stats phase presents it.
        foreach (var peer in player.Instance?.Presence.PlayersExcept(player.Id) ?? [])
            if (peer is { } other && peer.PlayerLife > 0 && Dist(peer.Walk.Position(AreaTime(peer)), kill.At) <= PoisonSpreadRadius
                && !other.Closed && !other.LeavingArea && SpreadPoisonRegenerationOf(other).Grant(AreaTime(other), seconds)
                && Logged(true, () => PresentStats(other)))
                Console.WriteLine($"[gateway] Bino's: regeneration granted to player 0x{peer.Id:x} for {seconds:0.0} s");
    }

    /// <summary>The spread starts a fresh default-duration poison, with global duration modifiers. Its copied DPS
    /// already includes faster-poison modifiers; applying those a second time would shorten the new poison twice.</summary>
    public static double SpreadPoisonSeconds(Func<uint, int> get) => Ailments.PoisonDurationSeconds
        * Math.Max(0, 1 + ((double)get(Stat.PoisonDurationPlusPct) + get(Stat.BaseAllAilmentDurationPlusPct)) / 100);

    /// <summary>The regeneration's Life per minute while it applies (CharacterStatSources).</summary>
    static Dictionary<uint, int> WithSpreadPoisonRegeneration(Player player, Dictionary<uint, int> sources)
    {
        if (player.PlayerLife == 0 || !SpreadPoisonRegenerationOf(player).Applied || !(Stat.BaseLifeRegenerationRatePerMinute is var key))
            return sources;
        var result = new Dictionary<uint, int>(sources);
        result[key] = result.GetValueOrDefault(key) + SpreadPoisonRegeneration.PerMinute;
        return result;
    }

    public static byte[]? SpreadPoisonRegenerationPresentation(Player player)
    {
        if (player.Closed || player.LeavingArea || player.RageRetired) return null;
        double now = ProjectileWorldTime(player), clock = AreaTime(player);
        foreach (var (_, monster) in player.Area.Monsters)
            if (AllySpreadPoisonRegenerations.TryGetValue(monster, out var ally) && ally.Until > now)
                PresentIn(player, ally.Until - now);
        if (SpreadPoisonRegenerationOf(player).Until > clock) PresentIn(player, SpreadPoisonRegenerationOf(player).Until - clock);
        foreach (var (id, monster) in player.Area.Monsters)
            if (AllySpreadPoisonRegenerations.TryGetValue(monster, out var ally)
                && ally.Present(now, !monster.Dead && monster.Life > 0, () => player.NextBuff++) is var (gone, fresh, _))
            {
                if (fresh is var (_, duration))
                    player.Area.Buffs.Set(id, new(SpreadPoisonRegeneration.BuffRow, player.Id, U68: Buff.Seconds(duration)));
                else if (gone != 0) player.Area.Buffs.Clear(id, SpreadPoisonRegeneration.BuffRow, player.Id);
            }
        var regeneration = SpreadPoisonRegenerationOf(player);
        if (regeneration.Present(AreaTime(player), player.PlayerLife > 0, () => player.NextBuff++)
            is not var (removed, added, stats)) return null;
        var bytes = new List<byte>();
        if (added is var (_, seconds))
            player.Area.Buffs.Set(player.Id, new(SpreadPoisonRegeneration.BuffRow, player.Id, U68: Buff.Seconds(seconds)));
        else if (removed != 0) player.Area.Buffs.Clear(player.Id, SpreadPoisonRegeneration.BuffRow, player.Id);
        if (stats)
        {
            MarkPlayerStats(player, PlayerStatStep.Sources);
            RecomputePoolsAndSpeed(player);
            bytes.AddRange(PlayerStatsOffFrames(player));
            Console.WriteLine($"[gateway] Bino's regeneration {(added is var (_, s) ? $"started, {s:0.0} s" : "ended")}");
        }
        return [.. bytes];
    }

    public static Task SendSpreadPoisonRegeneration(Player player) =>
        Send(player, [], null, atGate: () => SpreadPoisonRegenerationPresentation(player));
}
