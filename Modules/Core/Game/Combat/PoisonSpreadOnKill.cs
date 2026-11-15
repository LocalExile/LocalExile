using System.Net.Sockets;
using System.Runtime.CompilerServices;
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

    public bool Applied { get { lock (gate) return double.IsFinite(sentUntil); } }

    /// <summary>A kill at <paramref name="now"/> grants <paramref name="seconds"/>; a shorter grant never shortens it.</summary>
    public bool Grant(double now, double seconds)
    {
        lock (gate)
        {
            if (!(seconds > 0) || now + seconds <= until) return false;
            until = now + seconds;
            return true;
        }
    }

    /// <summary>As <see cref="KillOnslaught.Present"/>: the buff to remove (0 for none), the new buff numbered by
    /// <paramref name="next"/> with its seconds (null for none), whether the stats change. Death ends it.</summary>
    public (ushort Removed, (ushort Buff, double Seconds)? Added, bool Stats)? Present(double now, bool alive, Func<ushort> next)
    {
        lock (gate)
        {
            if (!alive) until = double.NegativeInfinity;
            double running = now < until ? until : double.NegativeInfinity;
            bool applied = double.IsFinite(running), was = double.IsFinite(sentUntil);
            bool remove = buff != 0 && !applied;
            bool add = applied && (buff == 0 || running - sentUntil >= 1);
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

    static readonly ConditionalWeakTable<ClientState, SpreadPoisonRegeneration> SpreadPoisonRegenerations = new();
    public static SpreadPoisonRegeneration SpreadPoisonRegenerationOf(ClientState conn) =>
        SpreadPoisonRegenerations.GetValue(conn, _ => new());

    static int SourceStat(ClientState conn, string name) => Stats.Key(name) is uint key && conn.StatSourcesCache is { } cached
        ? cached.Sources.GetValueOrDefault(key) : 0;

    /// <summary>Bino's Kitchen Knife (GameEvents.Kill): the player killed a poisoned enemy, so its strongest poison stack
    /// is applied as a new poison (<see cref="SpreadPoisonSeconds"/>) to every living enemy within 3 metres of it, and
    /// the player and the other players within 3 metres regenerate Life for that time. A spread poison can spread again from its own kill.</summary>
    public static void SpreadPoisonOnKill(KillContext kill)
    {
        var conn = kill.Conn;
        if (!kill.PlayerKill || SourceStat(conn, "unique_spread_poison_to_nearby_enemies_on_kill") <= 0) return;
        bool regenerates = SourceStat(conn, "unique_spread_poison_to_nearby_allies_as_200_life_regeneration_per_second_on_kill") > 0;
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
                if (m.Mods.Effects["cannot_be_poisoned"] != 0) continue;
                int avoid = m.Mods.Effects["avoid_poison_%"];
                if (avoid > 0 && RandomRolls.Chance(avoid, 100, Random.Shared)) continue;
                if (m.Poisons.Add(now, dps, seconds * MonsterPoisons.SelfDuration(name => m.Mods.Effects[name]))) spread++;
            }
            if (spread > 0) ReservationPoisonLocked(conn, now);
        }
        Console.WriteLine($"[gateway] Bino's: poison spread to {spread} enemies ({dps:0} dps, {seconds:0.0} s)");
        if (!regenerates) return;
        bool self = false;
        lock (conn.Monsters)
            if (conn.PlayerLife > 0 && Dist(conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds), kill.At) <= PoisonSpreadRadius)
                self = SpreadPoisonRegenerationOf(conn).Grant(conn.Clock.Elapsed.TotalSeconds, seconds);
        // Another player's grant is theirs, on their clock; their own tick presents it.
        foreach (var peer in conn.Instance?.Presence.PlayersExcept(conn.PeerId) ?? [])
            if (peer.Owner is { } other && peer.Current && peer.Life > 0 && Dist((peer.X, peer.Y), kill.At) <= PoisonSpreadRadius
                && SpreadPoisonRegenerationOf(other).Grant(other.Clock.Elapsed.TotalSeconds, seconds))
                Console.WriteLine($"[gateway] Bino's: regeneration granted to player 0x{peer.Id:x} for {seconds:0.0} s");
        if (self && kill.CommittedOutput is null)
        {
            var (remote, stream, ciphers) = conn.Wire;
            kill.Later(() => SendSpreadPoisonRegeneration(remote, stream, ciphers, conn));
        }
    }

    /// <summary>A spread poison and the regeneration last the default poison duration (2 s), not the killing skill's
    /// (poewiki Bino's Kitchen Knife): only the character's own poison duration and faster-poison stats change it, as
    /// the hit path's <see cref="MonsterPoisons.Settings.From"/> does without the skill's supports.</summary>
    public static double SpreadPoisonSeconds(Func<string, int> get)
    {
        double faster = MonsterPoisons.Faster(get);
        return faster <= 0 ? 0 : Ailments.PoisonDurationSeconds
            * Math.Max(0, 1 + ((double)get("poison_duration_+%") + get("base_all_ailment_duration_+%")) / 100) / faster;
    }

    /// <summary>The regeneration's Life per minute while it applies (CharacterStatSources).</summary>
    static Dictionary<uint, int> WithSpreadPoisonRegeneration(ClientState conn, Dictionary<uint, int> sources)
    {
        if (!SpreadPoisonRegenerationOf(conn).Applied || Stats.Key("base_life_regeneration_rate_per_minute") is not uint key)
            return sources;
        var result = new Dictionary<uint, int>(sources);
        result[key] = result.GetValueOrDefault(key) + SpreadPoisonRegeneration.PerMinute;
        return result;
    }

    public static byte[]? SpreadPoisonRegenerationPresentation(ClientState conn)
    {
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea || conn.RageRetired) return null;
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
                RecomputePoolsAndSpeed(conn);
                bytes.AddRange(PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)));
                Console.WriteLine($"[gateway] Bino's regeneration {(added is var (_, s) ? $"started, {s:0.0} s" : "ended")}");
            }
            return [.. bytes];
        }
    }

    public static Task SendSpreadPoisonRegeneration(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn) =>
        Send(remote, stream, ciphers, [], null, atGate: () => SpreadPoisonRegenerationPresentation(conn));
}
