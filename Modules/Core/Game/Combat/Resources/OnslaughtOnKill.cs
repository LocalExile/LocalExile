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
public sealed class KillOnslaught
{
    public IReadOnlyList<(int Chance, double Seconds)> Rolls = [];
    // "You gain Onslaught for 4 seconds on Hit" (gain_onslaught_on_hit_duration_ms): every hit, no roll.
    public int HitMs;
    public double Until = double.NegativeInfinity;
    public double SentUntil = double.NegativeInfinity;
    public ushort Buff;
    // A refresh that moves the shown deadline by less than this keeps the client's buff: a hit every tick would
    // otherwise remove and re-add it every tick (#849 review follow-up).
    const double RepresentSeconds = 1;
    public bool Applied => double.IsFinite(SentUntil);
    /// <summary>Whether the client would be told something new now: the buff starts, or its shown end moved enough.
    /// A gain on every hit otherwise waits for the player tick, which presents it anyway.</summary>
    public bool PresentationDue => !Applied || Until - SentUntil >= RepresentSeconds;

    public static IReadOnlyList<(int Chance, double Seconds)> From(Func<uint, int> get)
    {
        int ms = get(Stat.OnslaughtTimeGrantedOnKillMs), always = get(Stat.OnslaughtBuffDurationOnKillMs);
        return [.. new[]
        {
            (get(Stat.ChanceToGainOnslaughtOnKillPct), ms > 0 ? ms / 1000.0 : 4),
            (always > 0 ? 100 : 0, always / 1000.0),
            (get(Stat.ChanceToGainOnslaughtOnKillFor4SecondsPct), 4.0),
            (get(Stat.ChanceToGainOnslaughtOnKillFor10SecondsPct), 10.0),
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

    /// <summary>A gain of <paramref name="seconds"/> at <paramref name="now"/> (an effect rule's): whether it started
    /// or lengthened Onslaught. A gain never shortens the Onslaught running.</summary>
    public bool Extend(double now, double seconds)
    {
        if (seconds <= 0 || now + seconds <= Until) return false;
        Until = now + seconds;
        return true;
    }

    /// <summary>One hit at <paramref name="now"/>: whether it started or lengthened Onslaught ("You gain Onslaught for
    /// X seconds on Hit"). A gain never shortens the Onslaught running.</summary>
    public bool Hit(double now)
    {
        if (HitMs <= 0 || now + HitMs / 1000.0 <= Until) return false;
        Until = now + HitMs / 1000.0;
        return true;
    }

    /// <summary>What the client is told at <paramref name="now"/>, marked as told: the buff to remove (0 for none), the
    /// seconds of a new one (null for none; the caller numbers it into <see cref="Buff"/>), whether the stats change.
    /// Null when nothing changed. Death ends it; <paramref name="hidden"/> (a flask shows its own Onslaught) hides the
    /// buff but not the stats. A running buff is re-added only when the shown deadline is extended by at least
    /// <see cref="RepresentSeconds"/>, so a hit every tick does not re-send it every tick.</summary>
    public (ushort Removed, double? Seconds, bool Stats)? Present(double now, bool alive, bool hidden)
    {
        if (!alive) Until = double.NegativeInfinity;
        double until = now < Until ? Until : double.NegativeInfinity;
        bool show = double.IsFinite(until) && !hidden;
        bool applied = double.IsFinite(until);
        bool remove = Buff != 0 && !show;
        bool add = show && (Buff == 0 || until - SentUntil >= RepresentSeconds);
        if (!remove && !add && applied == Applied) return null;
        var change = (Buff, add ? until - now : (double?)null, applied != Applied);
        Buff = 0;
        SentUntil = applied ? until : double.NegativeInfinity;
        return change;
    }
}

public static partial class Game
{
    public static readonly Store<Player, KillOnslaught> KillOnslaughts = new();
    public static KillOnslaught KillOnslaughtOf(Player player) => KillOnslaughts.GetValue(player, _ => new());

    // Read when the player stats are built, not per death: an area hit that kills a pack does not rebuild them per corpse.
    public static void ConfigureKillOnslaught(Player player, Func<uint, int> get)
    {
        // The stats as its caller gives them: the stat build hands over EffectRules.Unhandled, where a stat an effect
        // rule drives reads 0, so each gain is rolled once, by the row that replaced its line here.
        var rolls = KillOnslaught.From(get);
        var onslaught = KillOnslaughtOf(player);
        onslaught.Rolls = rolls;
        onslaught.HitMs = get(Stat.GainOnslaughtOnHitDurationMs);
    }

    // A hostile monster the player killed (MonsterKilled, CommitProjectileKill): whether Onslaught started or lengthened.
    // AreaRng(conn), so a projectile commit's own draws stay in order.
    // Onslaught from kills and hits lasts until its deadline (on the connection clock); the reading step runs again then.
    static bool KillOnslaughtActiveLocked(Player player)
    {
        double now = AreaTime(player), until = KillOnslaughtOf(player).Until;
        if (player.PlayerLife == 0 || now >= until) return false;
        StatsChangeIn(until - now);
        return true;
    }

    public static bool OnslaughtOnKill(Player player)
    {
        return player.PlayerLife > 0 && KillOnslaughtOf(player).Kill(AreaTime(player), AreaRng(player))
            && Marked(player, PlayerStatStep.Onslaught);
    }

    // "You gain Onslaught for X seconds on Hit" (the Honoured Berserker tattoo and other sources): every player hit.
    public static bool OnslaughtOnHitLocked(Player player, double now)
    {
        return player.PlayerLife > 0 && KillOnslaughtOf(player).Hit(now) && Marked(player, PlayerStatStep.Onslaught);
    }

    // The kill Onslaught's stats while applied, as sources of the same buff as a Silver Flask's (UtilityStats): with both,
    // the strongest counts once. Its effect is the character's increased Onslaught effect.
    public static IEnumerable<UtilityFlasks.Source> KillOnslaughtSources(Player player, Func<uint, int> character)
    {
        bool applied;
        applied = player.PlayerLife > 0 && KillOnslaughtOf(player).Applied;
        return applied && ItemsFor(player).Onslaught is { } onslaught
            ? UtilityFlasks.OnslaughtSources(onslaught, Math.Max(0, 1 + character(Stat.OnslaughtEffectPlusPct) / 100.0)) : [];
    }

    // The kill Onslaught's buff (timed like Adrenaline's) and, when it starts or ends, the pools, walk speed, stats and
    // skills it changes. A running Silver Flask or Mageblood's shows its own Onslaught buff, so this one hides meanwhile.
    public static byte[]? KillOnslaughtPresentation(Player player)
    {
        if (player.Closed || player.LeavingArea || player.RageRetired) return null;
        var o = KillOnslaughtOf(player);
        var onslaught = ItemsFor(player).Onslaught;
        bool hidden = onslaught is null || player.UtilityFlaskEffects.ShowsOnslaught
            || player.MagebloodBuffs.Values.Any(b => b.Extra is not null);
        if (o.Present(AreaTime(player), player.PlayerLife > 0, hidden) is not var (removed, seconds, stats))
            return null;
        var bytes = new List<byte>();
        if (removed != 0 && onslaught is not null) player.Area.Buffs.Clear(player.Id, onslaught.BuffRow, player.Id);
        if (seconds is double left)
        {
            o.Buff = 1;
            player.Area.Buffs.Set(player.Id, new(onslaught!.BuffRow, player.Id, U68: Buff.Seconds(left),
                Visual: BuffArtVisual(player, "onslaught_buff", Stat.VirtualOnslaughtArtVariation)));
        }
        if (stats)
        {
            MarkPlayerStats(player, PlayerStatStep.Onslaught);
            RecomputePoolsAndSpeed(player);
            bytes.AddRange(PlayerStatsOffFrames(player));
            bytes.AddRange(PlayerSkillsIfChanged(player, PlayerSkills(player)));
            Console.WriteLine($"[gateway] Onslaught {(o.Applied ? "started" : "ended")}");
        }
        return [.. bytes];
    }

    public static Task SendKillOnslaught(Player player) =>
        Send(player, [], null, atGate: () => OnslaughtPresentation(player));

    public static byte[] OnslaughtPresentation(Player player) =>
        [.. KillOnslaughtPresentation(player) ?? [], .. ChargeOnslaughtPresentation(player) ?? []];
}
