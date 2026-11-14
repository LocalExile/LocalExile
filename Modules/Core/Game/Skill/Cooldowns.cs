using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    public static SingleUseCooldown? CooldownFor(ClientState conn, int effect, int level, int quality)
    {
        var sources = GemSkillEffects(conn).Where(g => g.Effect == effect).ToArray();
        if (SkillsFor(conn).CooldownMs(effect, level) <= 0 || sources.Length == 0) return null;
        ushort source;
        lock (conn.Monsters) source = conn.CooldownSources.GetValueOrDefault(effect);
        return CooldownForSkill(conn, sources.FirstOrDefault(g => g.Id == source).Name is not null ? source : sources[0].Id);
    }

    internal static SingleUseCooldown? CooldownForSkill(ClientState conn, ushort skillId)
    {
        var supported = SupportedSkill(conn, skillId);
        return CooldownPlan(conn, supported, CharacterPlayerStats(conn));
    }

    // Ikiaho's Leadership: travel skills recover cooldowns faster (the level 1 spirit's 100 is a 2x rate).
    public static int TravelSkillCooldownSpeed(SkillCatalog skills, int effect,
        IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int> own) =>
        skills.IsTravel(effect)
            ? GolemStat(player, "travel_skill_cooldown_speed_+%") + GolemStat(own, "travel_skill_cooldown_speed_+%")
            : 0;

    static SingleUseCooldown? CooldownPlan(ClientState conn, SupportedGem supported, IReadOnlyDictionary<uint, int> player)
    {
        var skills = SkillsFor(conn);
        int effect = supported.Effect;
        if (effect < 0) return null;
        var own = supported.Stats;
        if (skills.EffectId(effect) is "Convocation" or "ConvocationOld" && Stats.Key("base_cooldown_speed_+%") is uint recovery)
            own = new(own) { [recovery] = own.GetValueOrDefault(recovery) + GolemStat(player, "convocation_cooldown_speed_+%") };
        int travelRecoveryPct = TravelSkillCooldownSpeed(skills, effect, player, own);
        if (skills.IsTravel(effect) && travelRecoveryPct != 0
            && Stats.Key("base_cooldown_speed_+%") is uint travelRecovery)
            own = new(own) { [travelRecovery] = own.GetValueOrDefault(travelRecovery) + travelRecoveryPct };
        if (SingleUseCooldown.ForSkill(skills, Stats, effect, supported.Level, player, own) is not { } plan) return null;
        int? shared = skills.CooldownGroup(effect, supported.Level) switch
        {
            9 => 9,   // Blink
            10 => 10, // Guard
            1 when skills.IsWarcry(effect) && GolemStat(player, "warcry_skills_share_cooldowns") != 0 => 1,
            _ => null
        };
        return WithMapCooldown(conn, plan with { SharedGroup = shared });
    }

    public static SingleUseCooldownState CooldownStateLocked(ClientState conn, int effect, SingleUseCooldown plan, double now)
    {
        if (!conn.SingleUseCooldowns.TryGetValue(effect, out var state))
        {
            double legacyRemaining = Math.Max(0, conn.SkillReadyAt.GetValueOrDefault(effect) - now);
            state = new(now, plan.RecoveryRate, double.IsFinite(legacyRemaining)
                ? legacyRemaining * 1000 * (plan.RecoveryRate > 0 ? plan.RecoveryRate : 1) : plan.BaseMilliseconds,
                plan.BaseMilliseconds);
            if (legacyRemaining == 0 && plan.SharedGroup is int group)
            {
                var debt = conn.SingleUseCooldowns.Where(e => conn.CooldownGroups.TryGetValue(e.Key, out int peerGroup) && peerGroup == group)
                    .Select(e => e.Value).Select(s => { s.Refresh(now, s.Rate); return s; })
                    .Where(s => !s.FullyRecovered).OrderByDescending(s => s.SpentUses).FirstOrDefault();
                if (debt is not null)
                {
                    state.InheritDebt(debt, now, plan);
                    conn.CooldownGroups[effect] = group;
                }
            }
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
            var sharedUses = new List<(SingleUseCooldownState State, int Version, ushort Skill)>();
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
            ushort? payer = reserve && vaal is null && skillId is ushort candidate && PaysOnUse(conn, candidate, effect)
                ? candidate : null;
            if (payer is ushort paying && !CanPaySkillCost(conn, paying)) return false;
            if (plan is { } cooldown)
            {
                double now = conn.Clock.Elapsed.TotalSeconds;
                state = CooldownStateLocked(conn, effect, cooldown, now);
                var peers = cooldown.SharedGroup is null ? [] : GemSkillEffects(conn).Where(g => g.Effect != effect)
                    .GroupBy(g => g.Effect).Select(g => g.First())
                    .Select(g => (Gem: g, Plan: CooldownFor(conn, g.Effect, g.Level, g.Quality)))
                    .Where(p => p.Plan is { } peer && peer.SharedGroup == cooldown.SharedGroup)
                    .Select(p => (p.Gem, Plan: p.Plan!.Value,
                        State: CooldownStateLocked(conn, p.Gem.Effect, p.Plan.Value, now))).ToArray();
                if (reserve ? !state.TryUse(now, cooldown) : !state.Ready)
                {
                    // Cooling down: a skill with a cooldown bypass is used for a charge instead (CooldownBypass).
                    if (CooldownBypassCharge(conn, effect, skillId) is not { } charge) return false;
                    lock (conn.Monsters)
                        if (reserve ? !ChargesOf(conn).Spend(charge) : !ChargesOf(conn).CanSpend(charge)) return false;
                    bypassed = true;
                }
                conn.SkillReadyAt[effect] = now + state.RemainingSeconds;
                if (reserve && !bypassed)
                {
                    if (skillId is ushort source) conn.CooldownSources[effect] = source;
                    if (cooldown.SharedGroup is int shared) conn.CooldownGroups[effect] = shared;
                    else conn.CooldownGroups.Remove(effect);
                    foreach (var peer in peers)
                    {
                        // A full queue is not extended by another group member's use.
                        if (!peer.State.TryUse(now, peer.Plan)) continue;
                        conn.CooldownGroups[peer.Gem.Effect] = cooldown.SharedGroup!.Value;
                        conn.SkillReadyAt[peer.Gem.Effect] = now + peer.State.RemainingSeconds;
                        sharedUses.Add((peer.State, peer.State.UseVersion, peer.Gem.Id));
                    }
                }
            }
            // The accepted use pays its cost now, with its cooldown use or bypass charge, as the client pays at the start of
            // the use: a later failure (mana spent in between, a cast stopped before its effect) cannot leave the use spent
            // and unpaid, and an accepted use is never refunded. The check above ran under the same lock, so this succeeds.
            (uint Mana, uint Life)? paid = null;
            if (payer is ushort accepted && plan is not null)
            {
                if (!PayMana(conn, accepted, out uint mana, out uint life)) return false;
                paid = (mana, life);
            }
            sideEffects();
            if (reserve && vaal is not null) CommitVaalUse(conn, vaal, vaalAttackDeadline);
            // A bypassed use spends no cooldown use, so its ticket follows none.
            if (plan is not null) ticket = new(effect, conn.ActionCount, conn.ProjectileEpoch, instant, reserve,
                bypassed ? null : state, bypassed ? 0 : state!.UseVersion) { SharedUses = sharedUses, Paid = paid };
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
            CooldownFor(conn, g.Key, g.First().Level, g.First().Quality));
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
                if (!conn.ClientSkillIds.Contains(id) || !conn.SingleUseCooldowns.TryGetValue(effect, out var state)) continue;
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
