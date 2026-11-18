using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    public const string TwisterEffect = "Twister";

    static void ReconcileTwisterSourcesLocked(ClientState conn)
    {
        var state = conn.Twister;
        if (!ReferenceEquals(state.Owner, CombatLifetime))
        {
            state.Owner = CombatLifetime;
            state.Sources.Clear();
            state.Pending.Clear();
        }
        var live = !CurrentGameWork || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0
            ? [] : AllWornGrantedSkillsOf(conn).Where(g => g.EffectId == TwisterEffect && !g.Support
                && !IsInactive(conn, g.Host) && LeagueFor(conn).Rules.AllowsGemEffect(g.EffectId)).ToArray();
        var wanted = live.ToHashSet();
        foreach (var old in state.Sources.Keys.Where(g => !wanted.Contains(g)).ToArray()) state.Sources.Remove(old);
        foreach (var grant in live) state.Sources.TryAdd(grant, new(grant));
    }

    // Called once for an observed new phase, never for a duration/effect-only refresh. Long skips earn only the
    // currently observed gain, rather than replaying every missed phase as a burst after pause or a stalled tick.
    public static void QueueTwisterGainLocked(ClientState conn)
    {
        ReconcileTwisterSourcesLocked(conn);
        var state = conn.Twister;
        if (state.Owner is null || conn.Paused || InTown(conn)) return;
        long gain = ++state.Serial;
        foreach (var source in state.Sources.Values)
        {
            if (state.Pending.Count >= 64) break;
            state.Pending.Enqueue(new(state.Owner, conn.ProjectileEpoch, source, gain));
        }
    }

    public static bool TwisterGainCurrentLocked(ClientState conn, TwisterState.Gain gain)
    {
        ReconcileTwisterSourcesLocked(conn);
        return !gain.Consumed && CurrentGameWork && ReferenceEquals(gain.Owner, CombatLifetime)
            && conn.ProjectileActive(gain.Epoch) && !conn.RageRetired && conn.PlayerLife > 0 && !conn.Paused
            && !InTown(conn) && conn.Twister.Sources.TryGetValue(gain.Source.Grant, out var current)
            && ReferenceEquals(current, gain.Source);
    }

    // Admission and native acknowledgement share the send gate. No flight, cooldown spend, resource payment or
    // action interruption occurs before this final source/area/rules check.
    public static byte[]? AdmitTwisterGainLocked(ClientState conn, TwisterState.Gain gain, ProjectileSession session)
    {
        if (!TwisterGainCurrentLocked(conn, gain) || !session.Active || !ReferenceEquals(conn.ProjectileRuntime, session)) return null;
        var grant = gain.Source.Grant;
        var gem = GemSkillEffects(conn).FirstOrDefault(g => g.Effect == grant.Effect
            && SupportedSkill(conn, g.Id).WornGrant == grant);
        if (gem.Name is null || GemSkillInstanceDisabled(conn, gem.Effect, gem.Id, triggered: true) is not null) return null;
        var supported = SupportedSkill(conn, gem.Id);
        int chance = GolemStat(supported.Stats, "cast_on_gain_avians_flight_or_avians_might_%");
        if (!RandomRolls.Chance(chance, 100, conn.CriticalRandom)) return null;
        var plan = CooldownFor(conn, gem.Effect, gem.Level, gem.Quality);
        // Unsupported recovery mechanics must not silently fire without their intrinsic cooldown.
        if (plan is not { } cooldown) return null;
        double now = ProjectileWorldTime(conn);
        int key = CooldownKey(conn, gem.Effect, gem.Level);
        var state = CooldownStateLocked(conn, key, cooldown, now);
        if (!state.Ready) return null;
        var at = conn.Walk.Position(now);
        var target = conn.Monsters.Values.Where(m => m is { Hostile: true, Dead: false, Life: > 0, Asleep: false }
                && Dist(at, m.Walk.Position(now)) <= 150)
            .MinBy(m => Dist(at, m.Walk.Position(now)));
        var aim = target?.Walk.Position(now) ?? (at.X + 1, at.Y);
        ushort sequence = conn.LockstepClient ? NextLockstepSequence(conn) : ++conn.Twister.Sequence;
        var use = ClientDrawnVolley(conn, session, gem.Id, gem,
            CaptureGemProjectile(conn, session, gem.Id, gem, aim, CaptureSkillCriticalRoll(conn, gem.Id), sequence), sequence, 0);
        if (!state.TryUse(now, cooldown)) return null;
        gain.Consumed = true;
        conn.SkillReadyAt[key] = now + state.RemainingSeconds;
        session.Release(use);
        state.ConfirmNativeUse(state.UseVersion, gem.Id, synchronizeProgress: true);
        Console.WriteLine($"[gateway] Twister gain={gain.Serial} seq={sequence} skill=0x{gem.Id:x4} source={grant.Host} "
            + $"metadata={use.Profile.Id} t={now:0.000000}");
        return InstanceProtocol.InstantAction(PlayerId, gem.Id, aim.Item1, aim.Item2, sequence: sequence, flags: conn.LockstepClient ? (ushort)0x101 : (ushort)0, triggered: true);
    }

    static async Task DrainTwisterGains(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        int count;
        lock (conn.Monsters) count = Math.Min(64, conn.Twister.Pending.Count);
        for (int i = 0; i < count; i++)
        {
            TwisterState.Gain gain;
            using (ProjectileMutation(conn))
            {
                if (!conn.Twister.Pending.TryDequeue(out gain!)) break;
                if (!TwisterGainCurrentLocked(conn, gain)) continue;
            }
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, [], "0x01ce Avian gain triggers Twister", atGate: () =>
            {
                using (ProjectileMutation(conn))
                {
                    if (!TwisterGainCurrentLocked(conn, gain)) return null;
                    var session = ProjectilesFor(remote, stream, ciphers, conn);
                    session.Advance();
                    return AdmitTwisterGainLocked(conn, gain, session);
                }
            });
        }
        if (count > 0) await SendNativeCooldownRefresh(remote, stream, ciphers, conn);
    }
}
