using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    internal static SingleUseCooldown? CooldownFor(ClientState conn, int effect, int level, int quality) =>
        SkillsFor(conn).CooldownMs(effect, level) <= 0
        || !IsDashEffect(SkillsFor(conn).EffectId(effect)) && GemSkillEffects(conn).Count(g => g.Effect == effect) != 1 ? null :
            WithMapCooldown(conn, SingleUseCooldown.ForSkill(SkillsFor(conn), Stats, effect, level, CharacterPlayerStats(conn),
                SkillsFor(conn).EffectStats(effect, level, quality).ToDictionary(s => s.Item1, s => s.Item2)));

    // Dash and the supported beast companions use native cooldown groups shared by alternate/worn effect rows.
    internal static int CooldownKey(ClientState conn, int effect, int level) =>
        (IsDashEffect(SkillsFor(conn).EffectId(effect)) || IsSummonBeastialCompanionEffect(SkillsFor(conn).EffectId(effect)))
            && SkillsFor(conn).CooldownGroup(effect, level) is >= 0 and var group ? -1 - group : effect;

    internal static int CooldownKey(ClientState conn, int effect, ushort skillId)
    {
        var gem = SupportedSkill(conn, skillId);
        return gem.Effect == effect ? CooldownKey(conn, effect, gem.Level) : effect;
    }

    internal static SingleUseCooldownState CooldownStateLocked(ClientState conn, int key, SingleUseCooldown plan, double now)
    {
        if (!conn.SingleUseCooldowns.TryGetValue(key, out var state))
        {
            double legacyRemaining = Math.Max(0, conn.SkillReadyAt.GetValueOrDefault(key) - now);
            state = new(now, plan.RecoveryRate, double.IsFinite(legacyRemaining)
                ? legacyRemaining * 1000 * (plan.RecoveryRate > 0 ? plan.RecoveryRate : 1) : plan.BaseMilliseconds,
                plan.BaseMilliseconds);
            conn.SingleUseCooldowns[key] = state;
        }
        state.Refresh(now, plan.RecoveryRate);
        return state;
    }

    // Readiness, reservation and action side effects are one short critical section, before any echo/await.
    // An approach checks readiness without spending; arrival must reserve under its original action token.
    internal static bool TryBeginCooldownUse(ClientState conn, int effect, SingleUseCooldown? plan, bool instant,
        Action sideEffects, out CooldownUseTicket? ticket, int? actionToken = null, bool reserve = true, double? requestedAt = null)
        => TryBeginCooldownUseCore(conn, effect, plan, instant, sideEffects, out ticket,
            actionToken, reserve, requestedAt, null);

    internal static bool TryBeginSkillCooldownUse(ClientState conn, ushort skillId, int effect,
        SingleUseCooldown? plan, bool instant, Action sideEffects, out CooldownUseTicket? ticket,
        int? actionToken = null, bool reserve = true, double? requestedAt = null) =>
        TryBeginCooldownUseCore(conn, effect, plan, instant, sideEffects, out ticket,
            actionToken, reserve, requestedAt, skillId);

    internal static bool TryBeginCooldownUseCore(ClientState conn, int effect, SingleUseCooldown? plan, bool instant,
        Action sideEffects, out CooldownUseTicket? ticket, int? actionToken, bool reserve, double? requestedAt,
        ushort? skillId)
    {
        using (ProjectileMutation(conn))
        {
            using var sourceAdmission = AscendancyPolicies.Enter(conn, conn.MainAscendancy.Selection);
            ticket = null;
            SingleUseCooldownState? state = null;
            StanceAdmissionPlan? stancePlan = null;
            int key = skillId is ushort requestedKey ? CooldownKey(conn, effect, requestedKey) : effect;
            if (conn.Closed || conn.LeavingArea || conn.RageRetired || conn.PlayerLife == 0 || conn.Paused
                || requestedAt is double asked && PlayerActionBlocked(conn, asked, conn.Clock.Elapsed.TotalSeconds)
                || actionToken is int original && conn.ActionCount != original) return false;
            // Covers object/self and instant routes before creating or spending cooldown state or a new action.
            if ((skillId is ushort id ? ActorSkillInstanceDisabled(conn, effect, id)
                    ?? TravelSkillsExceptDashDisabled(conn, effect, id)
                : ActorSkillDisabled(conn, effect)) is not null) return false;
            if (skillId is ushort requestedSkill && StanceCatalog.IsSource(SkillsFor(conn).EffectId(effect)))
            {
                if (CurrentStanceSourceLocked(conn, requestedSkill, effect) is not { } stanceSource) return false;
                plan = CooldownFor(conn, effect, stanceSource.Supported.Level, stanceSource.Supported.Quality);
                double acceptedAt = conn.Clock.Elapsed.TotalSeconds;
                if (reserve && !TryPlanStanceLocked(conn, requestedSkill, effect, acceptedAt, out stancePlan)) return false;
            }
            VaalUseReservation? vaal = null;
            double? vaalAttackDeadline = null;
            if (skillId is ushort requested && GemSkillEffects(conn)
                .Any(g => g.Id == requested && SkillsFor(conn).VaalBudget(g.Effect, g.Level) is not null))
            {
                // No action/movement, cooldown reservation or payment for an unsupported/underfunded Vaal use.
                if (!CurrentGameRules || !VaalExecutor(conn, effect) || (vaal = PlanVaalUse(conn, requested)) is null || !CanPayVaalUse(conn, vaal))
                    return false;
                if (reserve && !TryVaalAttackDeadline(conn, vaal, out vaalAttackDeadline)) return false;
                if (reserve)
                {
                    var world = conn.RampageWorld;
                    if (!conn.OwnedCombatWorker.Bind(conn, CombatLifetime!, world.SourceOwner, world, out long generation))
                        return false;
                    vaal.WorkGeneration = generation;
                }
            }
            double now = conn.Clock.Elapsed.TotalSeconds;
            if (plan is { } cooldown)
            {
                state = CooldownStateLocked(conn, key, cooldown, now);
                if (!state.Ready) return false;
                if (reserve) _ = state.TryUse(now, cooldown);
                conn.SkillReadyAt[key] = now + state.RemainingSeconds;
            }
            sideEffects();
            if (reserve && vaal is not null) CommitVaalUse(conn, vaal, vaalAttackDeadline);
            StanceUseReceipt? stance = stancePlan is { } committedPlan && skillId is ushort receiptSkill
                ? ApplyStancePlanLocked(conn, receiptSkill, committedPlan, conn.ActionCount, conn.ProjectileEpoch)
                : null;
            if (plan is not null || stance is not null) ticket = new(effect, conn.ActionCount, conn.ProjectileEpoch,
                instant, reserve, state, state?.UseVersion ?? 0, stance);
            return true;
        }
    }

    internal static void RefreshSingleUseCooldowns(ClientState conn)
    {
        lock (conn.Monsters) if (conn.SingleUseCooldowns.Count == 0) return;
        var plans = GemSkillEffects(conn).GroupBy(g => CooldownKey(conn, g.Effect, g.Id)).ToDictionary(g => g.Key, g =>
            g.Select(skill => CooldownFor(conn, skill.Effect, skill.Level, skill.Quality))
                .FirstOrDefault(plan => plan is not null));
        lock (conn.Monsters)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            foreach (var (key, state) in conn.SingleUseCooldowns.ToArray())
            {
                if (!plans.TryGetValue(key, out var plan)) continue; // Removal does not reset a spent use.
                state.Refresh(now, plan?.RecoveryRate ?? state.Rate);
                conn.SkillReadyAt[key] = now + state.RemainingSeconds;
                if (plan is null) conn.SingleUseCooldowns.Remove(key); // Unsupported mechanics retain the legacy path.
            }
        }
    }

    internal static byte[]? NativeCooldownRefresh(ClientState conn, int epoch,
        Func<IReadOnlyList<(ushort Id, int Effect)>> currentSkills)
    {
        lock (conn.Monsters)
        {
            if (!conn.ProjectileActive(epoch) || conn.PlayerLife == 0) return null;
            double now = conn.Clock.Elapsed.TotalSeconds;
            var updates = new List<SingleUseCooldownUpdate>();
            var synchronized = new List<SingleUseCooldownState>();
            foreach (var (id, effect) in currentSkills())
            {
                if (!conn.ClientSkillIds.Contains(id) || !conn.SingleUseCooldowns.TryGetValue(
                        CooldownKey(conn, effect, id), out var state)
                    || state.NativeSkillId != id) continue;
                state.Refresh(now, state.Rate);
                if (!state.NeedsNativeRefresh) continue;
                updates.Add(new(id, state.Ready ? 0 : (float)(state.RemainingBaseMilliseconds / 1000),
                    state.TotalBaseMilliseconds / 1000f, (float)state.Rate));
                synchronized.Add(state);
            }
            if (updates.Count == 0) return null;
            var bytes = InstanceEntities.PlayerSingleUseCooldowns(PlayerId, updates, canDetonateMines: CanDetonateMines(conn));
            foreach (var state in synchronized) state.NativeRefreshed();
            return bytes;
        }
    }

    internal static Task SendNativeCooldownRefresh(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        lock (conn.Monsters)
            if (!conn.SingleUseCooldowns.Values.Any(s => s.NeedsNativeRefresh)) return Task.CompletedTask;
        int epoch;
        lock (conn.Monsters) epoch = conn.ProjectileEpoch;
        return Send(remote, stream, ciphers, [], "0x02ba player active cooldown progress/rate refresh",
            atGate: () => NativeCooldownRefresh(conn, epoch, () => GemSkillEffects(conn)
                .Where(g => CooldownFor(conn, g.Effect, g.Level, g.Quality) is not null)
                .Select(g => (g.Id, g.Effect)).ToArray()));
    }
}
