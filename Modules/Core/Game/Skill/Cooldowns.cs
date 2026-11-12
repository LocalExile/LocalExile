using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    public static SingleUseCooldown? CooldownFor(ClientState conn, int effect, int level, int quality)
    {
        var sources = GemSkillEffects(conn).Where(g => g.Effect == effect).ToArray();
        if (SkillsFor(conn).CooldownMs(effect, level) <= 0
            || !IsDashEffect(SkillsFor(conn).EffectId(effect)) && sources.Length != 1)
            return null;
        var supported = sources.Length == 1 ? SupportedSkill(conn, sources[0].Id) : null;
        var player = CharacterPlayerStats(conn);
        var own = new Dictionary<uint, int>(supported?.Stats
            ?? SkillsFor(conn).EffectStats(effect, level, quality).ToDictionary(s => s.Item1, s => s.Item2));
        if (SkillsFor(conn).EffectId(effect) is "Convocation" or "ConvocationOld"
            && Stats.Key("base_cooldown_speed_+%") is uint recovery)
            own[recovery] = own.GetValueOrDefault(recovery) + GolemStat(player, "convocation_cooldown_speed_+%");
        var plan = SingleUseCooldown.ForSkill(SkillsFor(conn), Stats, effect, supported?.Level ?? level,
            player, own);
        if (plan is { } madness && SkillsFor(conn).EffectId(effect) == MadnessEffect && MadnessActiveLocked(conn))
            plan = madness with { BaseMilliseconds = madness.BaseMilliseconds
                + MadnessSkillValue(conn, "embrace_madness_amount_of_cooldown_to_gain_ms") };
        return WithMapCooldown(conn, plan);
    }

    // Dash and the supported beast companions use native cooldown groups shared by alternate/worn effect rows.
    public static int CooldownKey(ClientState conn, int effect, int level) =>
        (IsDashEffect(SkillsFor(conn).EffectId(effect)) || IsSummonBeastialCompanionEffect(SkillsFor(conn).EffectId(effect))
            || SkillsFor(conn).EffectId(effect) is TwisterEffect or CatStealthCryEffect)
            && SkillsFor(conn).CooldownGroup(effect, level) is >= 0 and var group ? -1 - group : effect;

    public static int CooldownKey(ClientState conn, int effect, ushort skillId)
    {
        var gem = SupportedSkill(conn, skillId);
        return gem.Effect == effect ? CooldownKey(conn, effect, gem.Level) : effect;
    }

    public static SingleUseCooldownState CooldownStateLocked(ClientState conn, int key, SingleUseCooldown plan, double now)
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
            if (SkillsFor(conn).EffectId(effect) is TwisterEffect or CatStealthCryEffect) return false;
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
                state = CooldownStateLocked(conn, key, cooldown, now);
                if (reserve ? !state.TryUse(now, cooldown) : !state.Ready)
                {
                    // Cooling down: a skill with a cooldown bypass is used for a charge instead (CooldownBypass).
                    if (CooldownBypassCharge(conn, effect, skillId) is not { } charge) return false;
                    lock (conn.Monsters)
                        if (reserve ? !ChargesOf(conn).Spend(charge) : !ChargesOf(conn).CanSpend(charge)) return false;
                    bypassed = true;
                }
                conn.SkillReadyAt[key] = now + state.RemainingSeconds;
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
            StanceUseReceipt? stance = stancePlan is { } committedPlan && skillId is ushort receiptSkill
                ? ApplyStancePlanLocked(conn, receiptSkill, committedPlan, conn.ActionCount, conn.ProjectileEpoch)
                : null;
            if (plan is not null || stance is not null) ticket = new(effect, conn.ActionCount, conn.ProjectileEpoch,
                instant, reserve, bypassed ? null : state, bypassed ? 0 : state?.UseVersion ?? 0, stance) { Paid = paid };
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
                if (plan is { } current) state.Capacity(current.Uses);
                conn.SkillReadyAt[key] = now + state.RemainingSeconds;
                if (plan is null) conn.SingleUseCooldowns.Remove(key); // Unsupported mechanics retain the legacy path.
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
                if (!conn.ClientSkillIds.Contains(id) || !conn.SingleUseCooldowns.TryGetValue(
                        CooldownKey(conn, effect, id), out var state)
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
