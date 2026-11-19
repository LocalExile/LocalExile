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
    public static SpreadPoisonRegeneration SpreadPoisonRegenerationOf(Player conn) =>
        SpreadPoisonRegenerations.GetValue(conn, _ => new());

    // The ordinary monster regeneration step also owns minion and other allied-monster recovery. No second timer.
    static double SpreadPoisonRecovery(SpawnedMonster monster, double now) =>
        AllySpreadPoisonRegenerations.TryGetValue(monster, out var regeneration) ? regeneration.Recover(now) * PanelStats.LifeRegenerationMultiplier(name => monster.Effects[name]) : 0;

    static int SourceStat(Player conn, uint name) => conn.StatSourcesCache is { } cached
        ? cached.Sources.GetValueOrDefault(name) : 0;

    /// <summary>Bino's Kitchen Knife (GameEvents.Kill): the player killed a poisoned enemy, so its strongest poison stack
    /// is applied as a new poison (<see cref="SpreadPoisonSeconds"/>) to every living enemy within 3 metres of it, and
    /// the player and living allies within 3 metres regenerate Life for that time. A spread poison can spread again from its own kill.</summary>
    public static void SpreadPoisonOnKill(KillContext kill)
    {
        var conn = kill.Conn;
        if (!CurrentGameWork || conn.Closed || conn.LeavingArea || !kill.PlayerKill || SourceStat(conn, Stat.UniqueSpreadPoisonToNearbyEnemiesOnKill) <= 0) return;
        bool regenerates = SourceStat(conn, Stat.UniqueSpreadPoisonToNearbyAlliesAs200LifeRegenerationPerSecondOnKill) > 0;
        var dead = kill.Monster;
        int spread = 0;
        double dps, seconds;
        lock (conn.Monsters)
        {
            if (conn.PlayerLife == 0) return;
            double now = ProjectileWorldTime(conn);
            if (dead.Poisons.Strongest(now) is not var (perSecond, _)) return;
            dps = perSecond;
            seconds = SpreadPoisonSeconds(name => SourceStat(conn, name));
            foreach (var (_, m) in conn.Monsters)
            {
                if (ReferenceEquals(m, dead) || m.Dead || m.Life == 0 || !m.Hostile
                    || Dist(m.Walk.Position(now), kill.At) > PoisonSpreadRadius) continue;
                if (m.Effects[Stat.CannotBePoisoned] != 0) continue;
                int avoid = m.Effects[Stat.AvoidPoisonPct];
                if (avoid > 0 && RandomRolls.Chance(avoid, 100, Random.Shared)) continue;
                if (m.Poisons.Add(now, dps, seconds * MonsterPoisons.SelfDuration(name => m.Effects[name]))) spread++;
            }
            if (spread > 0) ReservationPoisonLocked(conn, now);
            if (regenerates)
                foreach (var ally in conn.Monsters.Values)
                    if (ally is { Hostile: false, Dead: false, Life: > 0 }
                        && Dist(ally.Walk.Position(now), kill.At) <= PoisonSpreadRadius)
                        AllySpreadPoisonRegenerations.GetValue(ally, _ => new()).Grant(now, seconds, trackRecovery: true);
        }
        PresentStats(conn);
        Console.WriteLine($"[gateway] Bino's: poison spread to {spread} enemies ({dps:0} dps, {seconds:0.0} s)");
        if (!regenerates) return;
        lock (conn.Monsters)
            if (conn.PlayerLife > 0 && Dist(conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds), kill.At) <= PoisonSpreadRadius)
                SpreadPoisonRegenerationOf(conn).Grant(conn.Clock.Elapsed.TotalSeconds, seconds);
        // Another player's grant is theirs, on their clock; their Stats phase presents it.
        foreach (var peer in conn.Instance?.Presence.PlayersExcept(conn.PeerId) ?? [])
            if (peer.Owner is { } other && peer.Current && peer.Life > 0 && Dist((peer.X, peer.Y), kill.At) <= PoisonSpreadRadius
                && !other.Closed && !other.LeavingArea && SpreadPoisonRegenerationOf(other).Grant(other.Clock.Elapsed.TotalSeconds, seconds)
                && Logged(true, () => PresentStats(other)))
                Console.WriteLine($"[gateway] Bino's: regeneration granted to player 0x{peer.Id:x} for {seconds:0.0} s");
    }

    /// <summary>The spread starts a fresh default-duration poison, with global duration modifiers. Its copied DPS
    /// already includes faster-poison modifiers; applying those a second time would shorten the new poison twice.</summary>
    public static double SpreadPoisonSeconds(Func<uint, int> get) => Ailments.PoisonDurationSeconds
        * Math.Max(0, 1 + ((double)get(Stat.PoisonDurationPlusPct) + get(Stat.BaseAllAilmentDurationPlusPct)) / 100);

    /// <summary>The regeneration's Life per minute while it applies (CharacterStatSources).</summary>
    static Dictionary<uint, int> WithSpreadPoisonRegeneration(Player conn, Dictionary<uint, int> sources)
    {
        if (conn.PlayerLife == 0 || !SpreadPoisonRegenerationOf(conn).Applied || !(Stat.BaseLifeRegenerationRatePerMinute is var key))
            return sources;
        var result = new Dictionary<uint, int>(sources);
        result[key] = result.GetValueOrDefault(key) + SpreadPoisonRegeneration.PerMinute;
        return result;
    }

    public static byte[]? SpreadPoisonRegenerationPresentation(Player conn)
    {
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea || conn.RageRetired) return null;
            double now = ProjectileWorldTime(conn), clock = conn.Clock.Elapsed.TotalSeconds;
            foreach (var (_, monster) in conn.Monsters)
                if (AllySpreadPoisonRegenerations.TryGetValue(monster, out var ally) && ally.Until > now)
                    PresentIn(conn, ally.Until - now);
            if (SpreadPoisonRegenerationOf(conn).Until > clock) PresentIn(conn, SpreadPoisonRegenerationOf(conn).Until - clock);
            foreach (var (id, monster) in conn.Monsters)
                if (AllySpreadPoisonRegenerations.TryGetValue(monster, out var ally)
                    && ally.Present(now, !monster.Dead && monster.Life > 0, () => conn.NextBuff++) is var (gone, fresh, _))
                {
                    if (gone != 0) conn.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(id, gone), null, WorldId: id));
                    if (fresh is var (instance, duration))
                        conn.Minions.OutputQueue.Add(new(InstanceEntities.AddBuff(id, instance, SpreadPoisonRegeneration.BuffRow, PlayerId,
                            u68: BitConverter.SingleToUInt32Bits((float)duration)), null, WorldId: id));
                }
            var regeneration = SpreadPoisonRegenerationOf(conn);
            if (regeneration.Present(conn.Clock.Elapsed.TotalSeconds, conn.PlayerLife > 0, () => conn.NextBuff++)
                is not var (removed, added, stats)) return null;
            var bytes = new List<byte>();
            if (removed != 0) bytes.AddRange(InstanceEntities.RemoveBuff(PlayerId, removed));
            if (added is var (buff, seconds))
                bytes.AddRange(InstanceEntities.AddBuff(PlayerId, buff, SpreadPoisonRegeneration.BuffRow, PlayerId,
                    u68: BitConverter.SingleToUInt32Bits((float)seconds)));
            if (stats)
            {
                MarkPlayerStats(conn, PlayerStatStep.Sources);
                RecomputePoolsAndSpeed(conn);
                bytes.AddRange(PlayerStatsOffFrames(conn));
                Console.WriteLine($"[gateway] Bino's regeneration {(added is var (_, s) ? $"started, {s:0.0} s" : "ended")}");
            }
            return [.. bytes];
        }
    }

    public static Task SendSpreadPoisonRegeneration(Player conn) =>
        Send(conn, [], null, atGate: () => SpreadPoisonRegenerationPresentation(conn));
}
