using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

/// <summary>
/// Onslaught gained on kill. chance_to_gain_onslaught_on_kill_% lasts onslaught_time_granted_on_kill_ms, 4 s when that is
/// 0 (its stat description: "% chance to gain Onslaught for 4 seconds on Kill"); the _for_4_seconds_% and
/// _for_10_seconds_% stats last 4 and 10 s, and onslaught_buff_duration_on_kill_ms ("You gain Onslaught for X seconds on
/// Kill": a unique ring and dagger, Maraketh's timeless notable) always gains for its own time. Each stat rolls on its own
/// and a gain never shortens the Onslaught running.
/// The stats apply while the client has been told (<see cref="Applied"/>), so both switch together.
/// </summary>
sealed class KillOnslaught
{
    public IReadOnlyList<(int Chance, double Seconds)> Rolls = [];
    public double Until = double.NegativeInfinity;
    public double SentUntil = double.NegativeInfinity;
    public ushort Buff;
    public bool Applied => double.IsFinite(SentUntil);

    public static IReadOnlyList<(int Chance, double Seconds)> From(Func<string, int> get)
    {
        int ms = get("onslaught_time_granted_on_kill_ms"), always = get("onslaught_buff_duration_on_kill_ms");
        return [.. new[]
        {
            (get("chance_to_gain_onslaught_on_kill_%"), ms > 0 ? ms / 1000.0 : 4),
            (always > 0 ? 100 : 0, always / 1000.0),
            (get("chance_to_gain_onslaught_on_kill_for_4_seconds_%"), 4.0),
            (get("chance_to_gain_onslaught_on_kill_for_10_seconds_%"), 10.0),
        }.Where(r => r.Item1 > 0)];
    }

    /// <summary>One kill at <paramref name="now"/>: whether it started or lengthened Onslaught.</summary>
    public bool Kill(double now, Random random)
    {
        double seconds = 0;
        foreach (var (chance, s) in Rolls)
            if (RandomRolls.Chance(chance, 100, random)) seconds = Math.Max(seconds, s);
        if (seconds <= 0 || now + seconds <= Until) return false;
        Until = now + seconds;
        return true;
    }

    /// <summary>What the client is told at <paramref name="now"/>, marked as told: the buff to remove (0 for none), the
    /// seconds of a new one (null for none; the caller numbers it into <see cref="Buff"/>), whether the stats change.
    /// Null when nothing changed. Death ends it; <paramref name="hidden"/> (a flask shows its own Onslaught) hides the
    /// buff but not the stats.</summary>
    public (ushort Removed, double? Seconds, bool Stats)? Present(double now, bool alive, bool hidden)
    {
        if (!alive) Until = double.NegativeInfinity;
        double until = now < Until ? Until : double.NegativeInfinity;
        bool show = double.IsFinite(until) && !hidden;
        if (until == SentUntil && show == (Buff != 0)) return null;
        var change = (Buff, show ? until - now : (double?)null, double.IsFinite(until) != Applied);
        Buff = 0;
        SentUntil = until;
        return change;
    }
}

static partial class Game
{
    internal static readonly ConditionalWeakTable<ClientState, KillOnslaught> KillOnslaughts = new();
    internal static KillOnslaught KillOnslaughtOf(ClientState conn) => KillOnslaughts.GetValue(conn, _ => new());

    // Read when the player stats are built, not per death: an area hit that kills a pack does not rebuild them per corpse.
    internal static void ConfigureKillOnslaught(ClientState conn, Func<string, int> get)
    {
        var rolls = KillOnslaught.From(get);
        lock (conn.Monsters) KillOnslaughtOf(conn).Rolls = rolls;
    }

    // A hostile monster the player killed (MonsterKilled, CommitProjectileKill): whether Onslaught started or lengthened.
    // Random.Shared, so a projectile commit's own draws stay in order.
    internal static bool OnslaughtOnKill(ClientState conn)
    {
        lock (conn.Monsters)
            return conn.PlayerLife > 0 && KillOnslaughtOf(conn).Kill(conn.Clock.Elapsed.TotalSeconds, Random.Shared);
    }

    // The kill Onslaught's stats while applied, as sources of the same buff as a Silver Flask's (UtilityStats): with both,
    // the strongest counts once. Its effect is the character's increased Onslaught effect.
    internal static IEnumerable<UtilityFlasks.Source> KillOnslaughtSources(ClientState conn, Func<string, int> character)
    {
        bool applied;
        lock (conn.Monsters) applied = conn.PlayerLife > 0 && KillOnslaughtOf(conn).Applied;
        return applied && ItemsFor(conn).Onslaught is { } onslaught
            ? UtilityFlasks.OnslaughtSources(onslaught, Math.Max(0, 1 + character("onslaught_effect_+%") / 100.0)) : [];
    }

    // The kill Onslaught's buff (timed like Adrenaline's) and, when it starts or ends, the pools, walk speed, stats and
    // skills it changes. A running Silver Flask or Mageblood's shows its own Onslaught buff, so this one hides meanwhile.
    internal static byte[]? KillOnslaughtPresentation(ClientState conn)
    {
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea || conn.RageRetired) return null;
            var o = KillOnslaughtOf(conn);
            var onslaught = ItemsFor(conn).Onslaught;
            bool hidden = onslaught is null || conn.UtilityFlaskEffects.ShowsOnslaught
                || conn.MagebloodBuffs.Values.Any(b => b.Extra is not null);
            if (o.Present(conn.Clock.Elapsed.TotalSeconds, conn.PlayerLife > 0, hidden) is not var (removed, seconds, stats))
                return null;
            var bytes = new List<byte>();
            if (removed != 0) bytes.AddRange(InstanceEntities.RemoveBuff(PlayerId, removed));
            if (seconds is double left)
                bytes.AddRange(InstanceEntities.AddBuff(PlayerId, o.Buff = conn.NextBuff++, onslaught!.BuffRow, PlayerId,
                    u68: BitConverter.SingleToUInt32Bits((float)left)));
            if (stats)
            {
                RecomputePoolsAndSpeed(conn);
                bytes.AddRange(PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)));
                bytes.AddRange(InstanceEntities.PlayerSkillsChange(PlayerId, PlayerSkills(conn), []));
                Console.WriteLine($"[gateway] Onslaught on kill {(o.Applied ? "started" : "ended")}");
            }
            return [.. bytes];
        }
    }

    internal static Task SendKillOnslaught(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn) =>
        Send(remote, stream, ciphers, [], null, atGate: () => KillOnslaughtPresentation(conn));
}
