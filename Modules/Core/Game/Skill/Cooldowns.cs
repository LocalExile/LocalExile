using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    public static SingleUseCooldown? CooldownFor(ClientState conn, int effect, int level, int quality)
    {
        var sources = GemSkillEffects(conn).Where(g => g.Effect == effect).ToArray();
        if (SkillsFor(conn).CooldownMs(effect, level) <= 0 || sources.Length != 1) return null;
        var supported = SupportedSkill(conn, sources[0].Id);
        return WithMapCooldown(conn, SingleUseCooldown.ForSkill(SkillsFor(conn), Stats, effect, supported.Level,
            CharacterPlayerStats(conn), supported.Stats));
    }

    public static SingleUseCooldownState CooldownStateLocked(ClientState conn, int effect, SingleUseCooldown plan, double now)
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
        state.Capacity(plan.Uses);
        return state;
    }

    // Readiness, reservation and action side effects are one short critical section, before any echo/await.
    // An approach checks readiness without spending; arrival must reserve under its original action token.
    public static bool TryBeginCooldownUse(ClientState conn, int effect, SingleUseCooldown? plan, bool instant,
        Action sideEffects, out CooldownUseTicket? ticket, int? actionToken = null, bool reserve = true, double? requestedAt = null)
        => TryBeginCooldownUseCore(conn, effect, plan, instant, sideEffects, out ticket,
            actionToken, reserve, requestedAt, null);

    // admit: a use-specific condition checked in the same critical section, before any cost, cooldown use or charge is
    // spent (Flicker Strike needs a monster in range).
    public static bool TryBeginSkillCooldownUse(ClientState conn, ushort skillId, int effect,
        SingleUseCooldown? plan, bool instant, Action sideEffects, out CooldownUseTicket? ticket,
        int? actionToken = null, bool reserve = true, double? requestedAt = null, Func<bool>? admit = null) =>
        TryBeginCooldownUseCore(conn, effect, plan, instant, sideEffects, out ticket,
            actionToken, reserve, requestedAt, skillId, admit);

    public static bool TryBeginCooldownUseCore(ClientState conn, int effect, SingleUseCooldown? plan, bool instant,
        Action sideEffects, out CooldownUseTicket? ticket, int? actionToken, bool reserve, double? requestedAt,
        ushort? skillId, Func<bool>? admit = null)
    {
        using (ProjectileMutation(conn))
        {
            using var sourceAdmission = AscendancyPolicies.Enter(conn, conn.MainAscendancy.Selection);
            ticket = null;
            SingleUseCooldownState? state = null;
            if (conn.Closed || conn.LeavingArea || conn.RageRetired || conn.PlayerLife == 0 || conn.Paused
                || requestedAt is double asked && PlayerActionBlocked(conn, asked, conn.Clock.Elapsed.TotalSeconds)
                || actionToken is int original && conn.ActionCount != original) return false;
            // Covers object/self and instant routes before creating or spending cooldown state or a new action.
            if ((skillId is ushort id ? ActorSkillInstanceDisabled(conn, effect, id)
                : ActorSkillDisabled(conn, effect)) is not null) return false;
            if (skillId is ushort manual && TriggerOnlyReason(SupportedSkill(conn, manual)) is not null) return false;
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
            if (admit is not null && !admit()) return false;
            bool bypassed = false;
            if (plan is { } cooldown)
            {
                double now = conn.Clock.Elapsed.TotalSeconds;
                // A use the player cannot pay for is refused here, before it spends a cooldown use or a bypass charge and
                // before its side effects move the player (a movement skill's landing, Flicker Strike's teleport); an
                // accepted use pays below.
                if (reserve && skillId is ushort paying && !CanPaySkillCost(conn, paying)) return false;
                state = CooldownStateLocked(conn, effect, cooldown, now);
                if (reserve ? !state.TryUse(now, cooldown) : !state.Ready)
                {
                    // Cooling down: a skill with a cooldown bypass is used for a charge instead (CooldownBypass).
                    if (CooldownBypassCharge(conn, effect, skillId) is not { } charge) return false;
                    lock (conn.Monsters)
                        if (reserve ? !ChargesOf(conn).Spend(charge) : !ChargesOf(conn).CanSpend(charge)) return false;
                    bypassed = true;
                }
                conn.SkillReadyAt[effect] = now + state.RemainingSeconds;
            }
            // The accepted use pays its cost now, with its cooldown use or bypass charge, as the client pays at the start of
            // the use: a later failure (mana spent in between, a cast stopped before its effect) cannot leave the use spent
            // and unpaid, and an accepted use is never refunded. The check above ran under the same lock, so this succeeds.
            (uint Mana, uint Life)? paid = null;
            if (reserve && plan is not null && vaal is null && skillId is ushort payer && PaysOnUse(conn, payer, effect))
            {
                if (!PayMana(conn, payer, out uint mana, out uint life)) return false;
                paid = (mana, life);
            }
            sideEffects();
            if (reserve && vaal is not null) CommitVaalUse(conn, vaal, vaalAttackDeadline);
            // A bypassed use spends no cooldown use, so its ticket follows none.
            if (plan is not null) ticket = new(effect, conn.ActionCount, conn.ProjectileEpoch, instant, reserve,
                bypassed ? null : state, bypassed ? 0 : state!.UseVersion) { Paid = paid };
            return true;
        }
    }

    // A link gem links its target with no upfront cost, only its cost over time (CastLink).
    public static bool IsLinkSkill(ClientState conn, int effect) => LinkSkills.Buffs.ContainsKey(SkillsFor(conn).EffectId(effect) ?? "");

    // An aura or stance that is not a trap or mine toggles its reservation instead of paying a cost (ToggleAura).
    public static (int Flat, int Percent)? ToggledReservation(ClientState conn, ushort skillId) =>
        PlacedKind(SupportedSkill(conn, skillId)) is { Trap: false, Mine: false } ? SupportedReservation(conn, skillId) : null;

    // Whether a use of the skill pays its cost when it starts (CastGemSkill's PayMana), not a link's or a toggle's.
    static bool PaysOnUse(ClientState conn, ushort skillId, int effect) =>
        !IsLinkSkill(conn, effect) && ToggledReservation(conn, skillId) is null;

    // The charge a skill spends to be used while it cools down (CooldownBypass), null when it has no bypass.
    static ChargeType? CooldownBypassCharge(ClientState conn, int effect, ushort? skillId)
    {
        var gem = GemSkillEffects(conn).FirstOrDefault(g => skillId is ushort id ? g.Id == id : g.Effect == effect);
        if (gem.Name is null || gem.Effect != effect) return null;
        int type = SkillsFor(conn).CooldownBypassType(effect, gem.Level);
        bool power = GolemStat(SupportedSkill(conn, gem.Id).Stats, CooldownBypass.PowerOverride) != 0
            || CooldownBypass.FlickerVigilantOverride(SkillsFor(conn).EffectId(effect), type)
                && GolemStat(CharacterPlayerStats(conn), CooldownBypass.FlickerVigilantPowerOverride) != 0;
        return CooldownBypass.Charge(type, power);
    }

    public static void RefreshSingleUseCooldowns(ClientState conn)
    {
        lock (conn.Monsters) if (conn.SingleUseCooldowns.Count == 0) return;
        var plans = GemSkillEffects(conn).GroupBy(g => g.Effect).ToDictionary(g => g.Key, g =>
            g.Count() != 1 ? null : CooldownFor(conn, g.Key, g.First().Level, g.First().Quality));
        lock (conn.Monsters)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            foreach (var (effect, state) in conn.SingleUseCooldowns.ToArray())
            {
                if (!plans.TryGetValue(effect, out var plan)) continue; // Removal does not reset a spent use.
                state.Refresh(now, plan?.RecoveryRate ?? state.Rate);
                if (plan is { } current) state.Capacity(current.Uses);
                conn.SkillReadyAt[effect] = now + state.RemainingSeconds;
                if (plan is null) conn.SingleUseCooldowns.Remove(effect); // Unsupported mechanics retain the legacy path.
            }
        }
    }

    public static byte[]? NativeCooldownRefresh(ClientState conn, int epoch,
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
                if (!conn.ClientSkillIds.Contains(id) || !conn.SingleUseCooldowns.TryGetValue(effect, out var state)
                    || state.NativeSkillId != id) continue;
                state.Refresh(now, state.Rate);
                if (!state.NeedsNativeRefresh) continue;
                updates.Add(new(id, state.FullyRecovered ? 0 : (float)(state.RemainingBaseMilliseconds / 1000),
                    state.TotalBaseMilliseconds / 1000f, (float)state.Rate, state.SpentUses));
                synchronized.Add(state);
            }
            if (updates.Count == 0) return null;
            var bytes = InstanceEntities.PlayerSingleUseCooldowns(PlayerId, updates, canDetonateMines: CanDetonateMines(conn));
            foreach (var state in synchronized) state.NativeRefreshed();
            return bytes;
        }
    }

    public static Task SendNativeCooldownRefresh(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        lock (conn.Monsters)
            if (!conn.SingleUseCooldowns.Values.Any(s => s.NeedsNativeRefresh)) return Task.CompletedTask;
        int epoch;
        lock (conn.Monsters) epoch = conn.ProjectileEpoch;
        return Send(remote, stream, ciphers, [], "0x02bc player active cooldown progress/rate refresh",
            atGate: () => NativeCooldownRefresh(conn, epoch, () => GemSkillEffects(conn)
                .Where(g => CooldownFor(conn, g.Effect, g.Level, g.Quality) is not null)
                .Select(g => (g.Id, g.Effect)).ToArray()));
    }
}
