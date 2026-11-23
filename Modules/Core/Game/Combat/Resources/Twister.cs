using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    public const string TwisterEffect = "Twister";

    static void ReconcileTwisterSourcesLocked(Player player)
    {
        var state = player.Twister;
        if (!ReferenceEquals(state.Owner, CombatLifetime))
        {
            state.Owner = CombatLifetime;
            state.Sources.Clear();
            state.Pending.Clear();
        }
        var live = !CurrentGameWork || player.Closed || player.LeavingArea || player.PlayerLife == 0
            ? [] : AllWornGrantedSkillsOf(player).Where(g => g.EffectId == TwisterEffect && !g.Support
                && !IsInactive(player, g.Host) && LeagueFor(player).Rules.AllowsGemEffect(g.EffectId)).ToArray();
        var wanted = live.ToHashSet();
        foreach (var old in state.Sources.Keys.Where(g => !wanted.Contains(g)).ToArray()) state.Sources.Remove(old);
        foreach (var grant in live) state.Sources.TryAdd(grant, new(grant));
    }

    // Called once for an observed new phase, never for a duration/effect-only refresh. Long skips earn only the
    // currently observed gain, rather than replaying every missed phase as a burst after pause or a stalled tick.
    public static void QueueTwisterGainLocked(Player player)
    {
        ReconcileTwisterSourcesLocked(player);
        var state = player.Twister;
        if (state.Owner is null || player.Paused || InTown(player)) return;
        long gain = ++state.Serial;
        foreach (var source in state.Sources.Values)
        {
            if (state.Pending.Count >= 64) break;
            state.Pending.Enqueue(new(state.Owner, player.ProjectileEpoch, source, gain));
        }
    }

    public static bool TwisterGainCurrentLocked(Player player, TwisterState.Gain gain)
    {
        ReconcileTwisterSourcesLocked(player);
        return !gain.Consumed && CurrentGameWork && ReferenceEquals(gain.Owner, CombatLifetime)
            && player.ProjectileActive(gain.Epoch) && !player.RageRetired && player.PlayerLife > 0 && !player.Paused
            && !InTown(player) && player.Twister.Sources.TryGetValue(gain.Source.Grant, out var current)
            && ReferenceEquals(current, gain.Source);
    }

    // Admission and native acknowledgement share the send gate. No flight, cooldown spend, resource payment or
    // action interruption occurs before this final source/area/rules check.
    public static byte[]? AdmitTwisterGainLocked(Player player, TwisterState.Gain gain, ProjectileSession session)
    {
        if (!TwisterGainCurrentLocked(player, gain) || !session.Active || !ReferenceEquals(player.ProjectileRuntime, session)) return null;
        var grant = gain.Source.Grant;
        var gem = GemSkillEffects(player).FirstOrDefault(g => g.Effect == grant.Effect
            && SupportedSkill(player, g.Id).WornGrant == grant);
        if (gem.Name is null || GemSkillInstanceDisabled(player, gem.Effect, gem.Id, triggered: true) is not null) return null;
        var supported = SupportedSkill(player, gem.Id);
        int chance = GolemStat(supported.Stats, Stat.CastOnGainAviansFlightOrAviansMightPct);
        if (!RandomRolls.Chance(chance, 100, player.CriticalRandom)) return null;
        var plan = CooldownFor(player, gem.Effect, gem.Level, gem.Quality);
        // Unsupported recovery mechanics must not silently fire without their intrinsic cooldown.
        if (plan is not { } cooldown) return null;
        double now = ProjectileWorldTime(player);
        int key = CooldownKey(player, gem.Effect, gem.Level);
        var state = CooldownStateLocked(player, key, cooldown, now);
        if (!state.Ready) return null;
        var at = player.Walk.Position(now);
        var target = player.Area.Monsters.Values.Where(m => m is { Hostile: true, Dead: false, Life: > 0, Asleep: false }
                && Dist(at, m.Walk.Position(now)) <= 150)
            .MinBy(m => Dist(at, m.Walk.Position(now)));
        var aim = target?.Walk.Position(now) ?? (at.X + 1, at.Y);
        ushort sequence = player.Connection.LockstepClient ? NextLockstepSequence(player) : ++player.Twister.Sequence;
        var use = ClientDrawnVolley(player, session, gem.Id, gem,
            CaptureGemProjectile(player, session, gem.Id, gem, aim, CaptureSkillCriticalRoll(player, gem.Id), sequence), sequence, 0);
        if (!state.TryUse(now, cooldown)) return null;
        gain.Consumed = true;
        player.SkillReadyAt[key] = now + state.RemainingSeconds;
        session.Release(use);
        state.ConfirmNativeUse(state.UseVersion, gem.Id, synchronizeProgress: true);
        Console.WriteLine($"[gateway] Twister gain={gain.Serial} seq={sequence} skill=0x{gem.Id:x4} source={grant.Host} "
            + $"metadata={use.Profile.Id} t={now:0.000000}");
        return InstanceProtocol.InstantAction(player.Id, gem.Id, aim.Item1, aim.Item2, sequence: sequence, flags: player.Connection.LockstepClient ? (ushort)0x101 : (ushort)0, triggered: true);
    }

    static async Task DrainTwisterGains(Player player)
    {
        int count;
        count = Math.Min(64, player.Twister.Pending.Count);
        for (int i = 0; i < count; i++)
        {
            TwisterState.Gain gain;
            using (ProjectileMutation(player))
            {
                if (!player.Twister.Pending.TryDequeue(out gain!)) break;
                if (!TwisterGainCurrentLocked(player, gain)) continue;
            }
            await SendWorldRecord(player, player.Id, [], "0x01ce Avian gain triggers Twister", atGate: () =>
            {
                using (ProjectileMutation(player))
                {
                    if (!TwisterGainCurrentLocked(player, gain)) return null;
                    var session = ProjectilesFor(player);
                    session.Advance();
                    return AdmitTwisterGainLocked(player, gain, session);
                }
            });
        }
        if (count > 0) await SendNativeCooldownRefresh(player);
    }
}
