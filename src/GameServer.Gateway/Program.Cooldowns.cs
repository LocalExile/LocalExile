using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

partial class Program
{
    static SingleUseCooldown? CooldownFor(ClientState conn, int effect, int level, int quality)
    {
        var matches = GemSkillEffects(conn).Where(g => g.Effect == effect).ToArray();
        if (Skills.CooldownMs(effect, level) <= 0 || matches.Length != 1) return null;
        var gem = SupportedSkill(conn, matches[0].Id);
        if (Skills.IsWarcry(effect) && !EnduringCrySingletonGroup(conn, matches[0].Id)) return null;
        return SingleUseCooldown.ForSkill(Skills, Stats, effect, gem.Level, CharacterPlayerStats(conn), gem.Stats);
    }

    static SingleUseCooldownState CooldownStateLocked(ClientState conn, int effect, SingleUseCooldown plan, double now)
    {
        if (!conn.SingleUseCooldowns.TryGetValue(effect, out var state))
        {
            double legacyRemaining = Math.Max(0, conn.SkillReadyAt.GetValueOrDefault(effect) - now);
            state = new(now, plan.RecoveryRate, double.IsFinite(legacyRemaining)
                ? legacyRemaining * 1000 * (plan.RecoveryRate > 0 ? plan.RecoveryRate : 1) : plan.BaseMilliseconds,
                plan.BaseMilliseconds);
            conn.SingleUseCooldowns[effect] = state;
        }
        state.Refresh(now, plan.RecoveryRate);
        return state;
    }

    // Readiness, reservation and action side effects are one short critical section, before any echo/await.
    // An approach checks readiness without spending; arrival must reserve under its original action token.
    internal static bool TryBeginCooldownUse(ClientState conn, int effect, SingleUseCooldown? plan, bool instant,
        Action sideEffects, out CooldownUseTicket? ticket, int? actionToken = null, bool reserve = true, ushort skillId = 0,
        double? requestedAt = null)
    {
        ticket = null;
        if (skillId != 0 && GemDisabled(conn, effect, skillId) is not null) return false;
        lock (conn.Monsters)
        {
            ticket = null;
            SingleUseCooldownState? state = null;
            SkillCost paidCost = default;
            if (conn.Closed || conn.LeavingArea || conn.PlayerLife == 0 || conn.Paused
                || requestedAt is double asked && conn.HitRecovery.BlocksInput(asked, conn.Clock.Elapsed.TotalSeconds)
                || actionToken is int original && conn.ActionCount != original) return false;
            var warcry = Skills.IsWarcry(effect) ? EnduringCryProfile(conn, skillId) : null;
            if (Skills.IsWarcry(effect) && (plan is null || warcry?.CanResolveOwnerEffect != true || !reserve
                || !EnduringCrySingletonGroup(conn, skillId)
                || CooldownFor(conn, effect, warcry.Level, warcry.Quality) != plan)) return false;
            if (warcry is not null && warcry.Timing.Instant != instant) return false;
            var source = warcry is null ? null : EnduringCrySource(conn, skillId);
            if (warcry is not null && source is null) return false;
            if (plan is { } cooldown)
            {
                double now = conn.Clock.Elapsed.TotalSeconds;
                state = CooldownStateLocked(conn, effect, cooldown, now);
                if (!state.Ready) return false;
                // A manual warcry is paid as part of acceptance, before its native echo or spent budget.
                if (warcry is not null)
                {
                    paidCost = CostFor(conn, effect, warcry.Level, warcry.Quality, skillId);
                    uint mana = conn.PlayerMana, life = conn.PlayerLife;
                    if (source?.Invoke() != true || !paidCost.Pay(ref mana, ref life)) return false;
                }
                if (reserve ? !state.TryUse(now, cooldown) : !state.Ready) return false;
                conn.SkillReadyAt[effect] = now + state.RemainingSeconds;
            }
            if (warcry is not null)
            {
                conn.PlayerMana -= paidCost.Mana;
                conn.PlayerLife -= paidCost.Life;
                RageOf(conn).SpendMana(paidCost.Mana, DateTimeOffset.UtcNow, RageInputs(conn, CharacterStatSources(conn)));
            }
            sideEffects();
            if (plan is not null) ticket = new(effect, conn.ActionCount, conn.ProjectileEpoch, instant, reserve,
                state, state!.UseVersion);
            if (warcry is not null)
            {
                var runtime = EnduringCries.GetValue(conn, _ => new());
                SettleEnduringCryLocked(conn, runtime, conn.Clock.Elapsed.TotalSeconds);
                EnduringCryUses.Add(ticket!, new(skillId, warcry, paidCost, source!, conn.Clock.Elapsed.TotalSeconds,
                    runtime.Generation));
            }
            return true;
        }
    }

    static void RefreshSingleUseCooldowns(ClientState conn)
    {
        lock (conn.Monsters) if (conn.SingleUseCooldowns.Count == 0) return;
        var player = CharacterPlayerStats(conn);
        var plans = GemSkillEffects(conn).GroupBy(g => g.Effect).ToDictionary(g => g.Key, g =>
        {
            if (g.Count() != 1) return null;
            var gem = SupportedSkill(conn, g.First().Id);
            if (Skills.IsWarcry(g.Key) && !EnduringCrySingletonGroup(conn, g.First().Id)) return null;
            return SingleUseCooldown.ForSkill(Skills, Stats, g.Key, gem.Level, player, gem.Stats);
        });
        lock (conn.Monsters)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            foreach (var (effect, state) in conn.SingleUseCooldowns.ToArray())
            {
                if (!plans.TryGetValue(effect, out var plan)) continue; // Removal does not reset a spent use.
                state.Refresh(now, plan?.RecoveryRate ?? state.Rate);
                conn.SkillReadyAt[effect] = now + state.RemainingSeconds;
                if (plan is null && !Skills.IsWarcry(effect)) conn.SingleUseCooldowns.Remove(effect);
            }
        }
    }

    internal static byte[]? NativeCooldownRefresh(ClientState conn, int epoch,
        Func<IReadOnlyList<(ushort Id, int Effect)>> currentSkills, Func<bool>? still = null)
    {
        lock (conn.Monsters)
        {
            if (!conn.ProjectileActive(epoch) || conn.PlayerLife == 0 || still?.Invoke() == false) return null;
            var skills = currentSkills();
            if (still?.Invoke() == false) return null;
            double now = conn.Clock.Elapsed.TotalSeconds;
            var updates = new List<SingleUseCooldownUpdate>();
            var synchronized = new List<SingleUseCooldownState>();
            foreach (var (id, effect) in skills)
            {
                if (!conn.ClientSkillIds.Contains(id) || !conn.SingleUseCooldowns.TryGetValue(effect, out var state)
                    || state.NativeSkillId != id) continue;
                state.Refresh(now, state.Rate);
                if (!state.NeedsNativeRefresh) continue;
                updates.Add(new(id, state.Ready ? 0 : (float)(state.RemainingBaseMilliseconds / 1000),
                    state.TotalBaseMilliseconds / 1000f, (float)state.Rate));
                synchronized.Add(state);
            }
            if (updates.Count == 0) return null;
            var bytes = InstanceEntities.PlayerSingleUseCooldowns(PlayerId, updates);
            if (still?.Invoke() == false) return null;
            foreach (var state in synchronized) state.NativeRefreshed();
            return bytes;
        }
    }

    static Task SendNativeCooldownRefresh(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, Func<bool>? still = null)
    {
        lock (conn.Monsters)
            if (!conn.SingleUseCooldowns.Values.Any(s => s.NeedsNativeRefresh)) return Task.CompletedTask;
        int epoch;
        lock (conn.Monsters) epoch = conn.ProjectileEpoch;
        return Send(remote, stream, ciphers, [], "0x02ba player active cooldown progress/rate refresh", still: still,
            atGate: () => NativeCooldownRefresh(conn, epoch, () => GemSkillEffects(conn)
                .Where(g => CooldownFor(conn, g.Effect, g.Level, g.Quality) is not null)
                .Select(g => (g.Id, g.Effect)).ToArray(), still));
    }
}
