using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    public static SingleUseCooldown? CooldownFor(Player player, int effect, int level, int quality)
    {
        var sources = GemSkillEffects(player).Where(g => g.Effect == effect).ToArray();
        if (SkillsFor(player).CooldownMs(effect, level) <= 0) return null;
        if (sources.Length == 0)
        {
            if (!IsDashEffect(SkillsFor(player).EffectId(effect))) return null;
            var fallback = SingleUseCooldown.ForSkill(SkillsFor(player), Stats, effect, level,
                CharacterPlayerStats(player), SkillsFor(player).EffectStats(effect, level, quality).ToDictionary(s => s.Item1, s => s.Item2));
            return WithMapCooldown(player, fallback is { } dash ? dash with { SharedGroup = 9 } : null);
        }
        ushort source;
        source = player.CooldownSources.GetValueOrDefault(effect);
        return CooldownForSkill(player, sources.FirstOrDefault(g => g.Id == source).Name is not null ? source : sources[0].Id);
    }

    public static SingleUseCooldown? CooldownForSkill(Player player, ushort skillId)
    {
        var supported = SupportedSkill(player, skillId);
        // From the skill and the stats (Embrace Madness's also from its live state): kept with the skills (ActiveSkills).
        if (BuildScopeOf(player) is not { Building: true } && CurrentActiveSkills(player) is { } current
            && SkillsFor(player).EffectId(supported.Effect) != MadnessEffect)
        {
            return current.SkillGraphOf(player, skillId, supported).Cooldown;
        }
        return CooldownPlan(player, supported, CharacterPlayerStats(player));
    }

    /// <summary>The client-facing stored-use state of one skill, null when it has none.</summary>
    public static SingleUseCooldownState? SingleUseState(Player player, int effect, ushort skillId) =>
        player.SingleUseCooldowns.GetValueOrDefault(CooldownKey(player, effect, skillId));

    // Ikiaho's Leadership: travel skills recover cooldowns faster (the level 1 spirit's 100 is a 2x rate).
    public static int TravelSkillCooldownSpeed(SkillCatalog skills, int effect,
        IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int> own) =>
        skills.IsTravel(effect)
            ? GolemStat(player, Stat.TravelSkillCooldownSpeedPlusPct) + GolemStat(own, Stat.TravelSkillCooldownSpeedPlusPct)
            : 0;

    static SingleUseCooldown? CooldownPlan(Player player, SupportedGem supported, IReadOnlyDictionary<uint, int> playerStats)
    {
        var skills = SkillsFor(player);
        int effect = supported.Effect;
        if (effect < 0) return null;
        var own = supported.Stats;
        if (skills.EffectId(effect) is "Convocation" or "ConvocationOld" && Stat.BaseCooldownSpeedPlusPct is var recovery)
            own = new Dictionary<uint, int>(own) { [recovery] = own.GetValueOrDefault(recovery) + GolemStat(playerStats, Stat.ConvocationCooldownSpeedPlusPct) };
        if (skills.EffectId(effect) == "BrandRecall" && Stat.BaseCooldownSpeedPlusPct is var recallRecovery)
            own = new Dictionary<uint, int>(own) { [recallRecovery] = own.GetValueOrDefault(recallRecovery) + BrandRecallCooldownBonusPct(player, playerStats) };
        // Fold both flat Travel recovery and its per-Frenzy coefficient into this skill's own input.
        // The published standard count includes minimum charges and excludes converted Affliction Charges.
        if (skills.IsTravel(effect) && Stat.BaseCooldownSpeedPlusPct is var travelRecovery)
        {
            long coefficient = (long)GolemStat(playerStats, Stat.TravelSkillsCooldownSpeedPlusPctPerFrenzyCharge)
                + GolemStat(own, Stat.TravelSkillsCooldownSpeedPlusPctPerFrenzyCharge);
            long flat = TravelSkillCooldownSpeed(skills, effect, playerStats, own);
            if (coefficient != 0 || flat != 0)
                own = new Dictionary<uint, int>(own) { [travelRecovery] = ActorScale.Saturate((long)own.GetValueOrDefault(travelRecovery)
                    + flat + coefficient * Math.Max(0, GolemStat(playerStats, Stat.CurrentFrenzyCharges))) };
        }
        uint scopedRecovery = skills.IsGuard(effect) ? Stat.GuardSkillCooldownRecoveryPlusPct
            : Supports.Of(effect)?.Types.Contains(120) == true ? Stat.CounterattacksCooldownRecoveryPlusPct : 0;
        if (scopedRecovery != 0)
        {
            long scoped = (long)playerStats.GetValueOrDefault(scopedRecovery) + own.GetValueOrDefault(scopedRecovery);
            if (scoped != 0)
                own = new Dictionary<uint, int>(own) { [Stat.BaseCooldownSpeedPlusPct] =
                    ActorScale.Saturate(scoped + own.GetValueOrDefault(Stat.BaseCooldownSpeedPlusPct)) };
        }
        if (SingleUseCooldown.ForSkill(skills, Stats, effect, supported.Level, playerStats, own) is not { } plan) return null;
        if (skills.EffectId(effect) == MadnessEffect && MadnessActiveLocked(player))
            plan = plan with { BaseMilliseconds = plan.BaseMilliseconds
                + MadnessSkillValue(player, Stat.EmbraceMadnessAmountOfCooldownToGainMs) };
        int? shared = skills.CooldownGroup(effect, supported.Level) switch
        {
            9 => 9,   // Blink
            10 => 10, // Guard
            1 when skills.IsWarcry(effect) && GolemStat(playerStats, Stat.WarcrySkillsShareCooldowns) != 0 => 1,
            _ => null
        };
        return WithMapCooldown(player, plan with { SharedGroup = shared });
    }

    // Dash and the supported beast companions use native cooldown groups shared by alternate/worn effect rows.
    public static int CooldownKey(Player player, int effect, int level) =>
        (IsDashEffect(SkillsFor(player).EffectId(effect)) || IsSummonBeastialCompanionEffect(SkillsFor(player).EffectId(effect))
            || SkillsFor(player).EffectId(effect) is TwisterEffect or CatStealthCryEffect)
            && SkillsFor(player).CooldownGroup(effect, level) is >= 0 and var group ? -1 - group : effect;

    public static int CooldownKey(Player player, int effect, ushort skillId)
    {
        var gem = SupportedSkill(player, skillId);
        return gem.Effect == effect ? CooldownKey(player, effect, gem.Level) : effect;
    }

    static (ushort Skill, int Key, SingleUseCooldown Plan, SingleUseCooldownState State)[] SharedCooldownPeers(
        Player player, int effect, int key, SingleUseCooldown cooldown, double now) => cooldown.SharedGroup is null ? []
        : GemSkillEffects(player).Where(g => g.Effect != effect)
            .GroupBy(g => CooldownKey(player, g.Effect, g.Id)).Where(g => g.Key != key).Select(g => g.First())
            .Select(g => (Gem: g, Plan: CooldownFor(player, g.Effect, g.Level, g.Quality)))
            .Where(p => p.Plan is { } peer && peer.SharedGroup == cooldown.SharedGroup)
            .Select(p => (p.Gem.Id, CooldownKey(player, p.Gem.Effect, p.Gem.Id), p.Plan!.Value,
                CooldownStateLocked(player, CooldownKey(player, p.Gem.Effect, p.Gem.Id), p.Plan.Value, now))).ToArray();

    static List<(SingleUseCooldownState State, int Version, ushort Skill)> SpendSharedCooldownPeers(Player player,
        int key, int effect, ushort? skill, SingleUseCooldown plan, double now,
        (ushort Skill, int Key, SingleUseCooldown Plan, SingleUseCooldownState State)[] peers)
    {
        if (skill is ushort source) player.CooldownSources[effect] = source;
        if (plan.SharedGroup is int shared) player.CooldownGroups[key] = shared;
        else player.CooldownGroups.Remove(key);
        List<(SingleUseCooldownState, int, ushort)> receipts = [];
        foreach (var peer in peers)
        {
            if (!peer.State.TryUse(now, peer.Plan)) continue;
            player.CooldownGroups[peer.Key] = plan.SharedGroup!.Value;
            player.SkillReadyAt[peer.Key] = now + peer.State.RemainingSeconds;
            receipts.Add((peer.State, peer.State.UseVersion, peer.Skill));
        }
        return receipts;
    }

    public static SingleUseCooldownState CooldownStateLocked(Player player, int key, SingleUseCooldown plan, double now)
    {
        if (!player.SingleUseCooldowns.TryGetValue(key, out var state))
        {
            double legacyRemaining = Math.Max(0, player.SkillReadyAt.GetValueOrDefault(key) - now);
            state = new(now, plan.RecoveryRate, double.IsFinite(legacyRemaining)
                ? legacyRemaining * 1000 * (plan.RecoveryRate > 0 ? plan.RecoveryRate : 1) : plan.BaseMilliseconds,
                plan.BaseMilliseconds);
            if (legacyRemaining == 0 && plan.SharedGroup is int group)
            {
                var debt = player.SingleUseCooldowns.Where(e => player.CooldownGroups.TryGetValue(e.Key, out int peerGroup) && peerGroup == group)
                    .Select(e => e.Value).Select(s => { s.Refresh(now, s.Rate); return s; })
                    .Where(s => !s.FullyRecovered).OrderByDescending(s => s.SpentUses).FirstOrDefault();
                if (debt is not null)
                {
                    state.InheritDebt(debt, now, plan);
                    player.CooldownGroups[key] = group;
                }
            }
            player.SingleUseCooldowns[key] = state;
        }
        state.Refresh(now, plan.RecoveryRate);
        state.Capacity(plan.Uses);
        return state;
    }

    // Readiness, reservation and action side effects are one short critical section, before any echo/await.
    // An approach checks readiness without spending; arrival must reserve under its original action token.
    public static bool TryBeginCooldownUse(Player player, int effect, SingleUseCooldown? plan, bool instant,
        Action sideEffects, out CooldownUseTicket? ticket, int? actionToken = null, bool reserve = true, double? requestedAt = null)
        => TryBeginCooldownUseCore(player, effect, plan, instant, sideEffects, out ticket,
            actionToken, reserve, requestedAt, null);

    // admit: a use-specific condition checked in the same critical section, before any cost, cooldown use or charge is
    // spent (Flicker Strike needs a monster in range).
    public static bool TryBeginSkillCooldownUse(Player player, ushort skillId, int effect,
        SingleUseCooldown? plan, bool instant, Action sideEffects, out CooldownUseTicket? ticket,
        int? actionToken = null, bool reserve = true, double? requestedAt = null, Func<bool>? admit = null) =>
        TryBeginCooldownUseCore(player, effect, plan, instant, sideEffects, out ticket,
            actionToken, reserve, requestedAt, skillId, admit);

    /// <summary>Accepts an attack before its movement and action echo, with one time for readiness and the swing.</summary>
    public static bool TryBeginAttackUse(Player player, ushort skillId, int effect, double requestedAt,
        Action<double> sideEffects, out CooldownUseTicket? ticket, Func<double, bool>? admit = null)
    {
        using (ProjectileMutation(player))
        {
            double at = Math.Max(requestedAt, ProjectileWorldTime(player));
            return TryBeginCooldownUseCore(player, effect, CooldownForSkill(player, skillId), false,
                () => sideEffects(at), out ticket, null, true, at, skillId,
                admit is null ? null : () => admit(at), reserveAttack: true);
        }
    }

    public static bool TryBeginCooldownUseCore(Player player, int effect, SingleUseCooldown? plan, bool instant,
        Action sideEffects, out CooldownUseTicket? ticket, int? actionToken, bool reserve, double? requestedAt,
        ushort? skillId, Func<bool>? admit = null, bool reserveAttack = false)
    {
        using (ProjectileMutation(player, advance: !reserveAttack))
        {
            using var sourceAdmission = AscendancyPolicies.Enter(player, player.Character.MainAscendancy.Selection);
            ticket = null;
            if (SkillsFor(player).EffectId(effect) is TwisterEffect or CatStealthCryEffect) return false;
            SingleUseCooldownState? state = null;
            StanceAdmissionPlan? stancePlan = null;
            int key = skillId is ushort requestedKey ? CooldownKey(player, effect, requestedKey) : effect;
            var sharedUses = new List<(SingleUseCooldownState State, int Version, ushort Skill)>();
            if (player.Closed || player.LeavingArea || player.RageRetired || player.PlayerLife == 0 || player.Paused
                || requestedAt is double asked && PlayerActionBlocked(player, asked, AreaTime(player))
                || actionToken is int original && player.ActionCount != original) return false;
            // Covers object/self and instant routes before creating or spending cooldown state or a new action.
            if ((skillId is ushort id ? ActorSkillInstanceDisabled(player, effect, id)
                    ?? TravelSkillsExceptDashDisabled(player, effect, id)
                : ActorSkillDisabled(player, effect)) is not null) return false;
            if (skillId is ushort requestedSkill && StanceCatalog.IsSource(SkillsFor(player).EffectId(effect)))
            {
                if (CurrentStanceSourceLocked(player, requestedSkill, effect) is not { } stanceSource) return false;
                plan = CooldownPlan(player, stanceSource.Supported, CharacterPlayerStats(player));
                double acceptedAt = AreaTime(player);
                if (reserve && !TryPlanStanceLocked(player, requestedSkill, effect, acceptedAt, out stancePlan)) return false;
            }
            if (skillId is ushort manual && TriggerOnlyReason(SupportedSkill(player, manual)) is not null) return false;
            // A guard skill shares one cooldown with the other guard skills, and its own buff has to be over before a
            // new one starts: Immortal Call's cooldown does not recover during its effect, so it is ready again only
            // after the buff and its 3 s cooldown (real game and Path of Building). Refuse here, before a cooldown use,
            // a bypass charge or a cost is spent.
            if (skillId is not null && SkillsFor(player).IsGuard(effect)
                && !TriggerGuardReady(player, AreaTime(player))) return false;
            VaalUseReservation? vaal = null;
            double? vaalAttackDeadline = null;
            if (skillId is ushort requested && GemSkillEffects(player)
                .Any(g => g.Id == requested && SkillsFor(player).VaalBudget(g.Effect, g.Level) is not null))
            {
                // No action/movement, cooldown reservation or payment for an unsupported/underfunded Vaal use.
                if (!CurrentGameRules || !VaalExecutor(player, effect) || (vaal = PlanVaalUse(player, requested)) is null || !CanPayVaalUse(player, vaal))
                    return false;
                if (reserve && !TryVaalAttackDeadline(player, vaal, out vaalAttackDeadline)) return false;
                if (reserve)
                {
                    var world = player.RampageWorld;
                    if (!player.OwnedCombatWorker.Bind(player, CombatLifetime!, world.SourceOwner, world, out long generation))
                        return false;
                    vaal.WorkGeneration = generation;
                }
            }
            (ushort Skill, double At, double Seconds)? attack = null;
            if (reserveAttack)
            {
                if (!reserve || skillId is not ushort attacking || !SkillsFor(player).IsAttack(effect)
                    || IsTotemSkill(player, attacking) || vaal is not null) return false;
                double at = requestedAt ?? ProjectileWorldTime(player);
                double seconds = SupportedAttackSeconds(player, attacking);
                if (!AttackTiming.Ready(at, MeleeRepeatAttackDeadline(player, at), seconds)) return false;
                attack = (attacking, at, seconds);
            }
            if (admit is not null && !admit()) return false;
            bool bypassed = false;
            ushort? payer = reserve && vaal is null && skillId is ushort candidate && PaysOnUse(player, candidate, effect)
                ? candidate : null;
            if (payer is ushort paying && !CanPaySkillCost(player, paying)) return false;
            if (plan is { } cooldown)
            {
                double now = AreaTime(player);
                state = CooldownStateLocked(player, key, cooldown, now);
                var peers = SharedCooldownPeers(player, effect, key, cooldown, now);
                if (reserve ? !state.TryUse(now, cooldown) : !state.Ready)
                {
                    // Cooling down: a skill with a cooldown bypass is used for a charge instead (CooldownBypass).
                    if (CooldownBypassCharge(player, effect, skillId) is not { } charge) return false;
                    if (reserve ? !ChargesOf(player).Spend(charge) : !ChargesOf(player).CanSpend(charge)) return false;
                    bypassed = true;
                }
                player.SkillReadyAt[key] = now + state.RemainingSeconds;
                if (reserve && !bypassed)
                    sharedUses = SpendSharedCooldownPeers(player, key, effect, skillId, cooldown, now, peers);
            }
            // The accepted use pays its cost now, with its cooldown use or bypass charge, as the client pays at the start of
            // the use: a later failure (mana spent in between, a cast stopped before its effect) cannot leave the use spent
            // and unpaid, and an accepted use is never refunded. The check above ran under the same lock, so this succeeds.
            (uint Mana, uint Life)? paid = null;
            if (payer is ushort accepted && (plan is not null || attack is not null))
            {
                if (!PayMana(player, accepted, out uint mana, out uint life)) return false;
                paid = (mana, life);
            }
            if (!instant) CancelMeleeRepeats(player, requestedAt ?? ProjectileWorldTime(player));
            if (attack is { } swing)
                player.NextAttackAt = AttackTiming.After(swing.At, player.NextAttackAt, swing.Seconds);
            sideEffects();
            if (reserve && vaal is not null) CommitVaalUse(player, vaal, vaalAttackDeadline);
            StanceUseReceipt? stance = stancePlan is { } committedPlan && skillId is ushort receiptSkill
                ? ApplyStancePlanLocked(player, receiptSkill, committedPlan, player.ActionCount, player.ProjectileEpoch)
                : null;
            if (plan is not null || stance is not null || attack is not null) ticket = new(effect, player.ActionCount, player.ProjectileEpoch,
                instant, reserve, bypassed ? null : state, bypassed ? 0 : state?.UseVersion ?? 0, stance) { SharedUses = sharedUses, Paid = paid, Attack = attack };
            return true;
        }
    }

    // A link gem links its target with no upfront cost, only its cost over time (CastLink).
    public static bool IsLinkSkill(Player player, int effect) => LinkSkills.Buffs.ContainsKey(SkillsFor(player).EffectId(effect) ?? "");

    // An aura or stance that is not a trap or mine toggles its reservation instead of paying a cost (ToggleAura).
    public static (int Flat, int Percent)? ToggledReservation(Player player, ushort skillId) =>
        PlacedKind(SupportedSkill(player, skillId)) is { Trap: false, Mine: false } ? SupportedReservation(player, skillId) : null;

    // Whether a use of the skill pays its cost when it starts (CastGemSkill's PayMana), not a link's or a toggle's.
    static bool PaysOnUse(Player player, ushort skillId, int effect) =>
        !IsLinkSkill(player, effect) && ToggledReservation(player, skillId) is null;

    // The charge a skill spends to be used while it cools down (CooldownBypass), null when it has no bypass.
    static ChargeType? CooldownBypassCharge(Player player, int effect, ushort? skillId)
    {
        var gem = GemSkillEffects(player).FirstOrDefault(g => skillId is ushort id ? g.Id == id : g.Effect == effect);
        if (gem.Name is null || gem.Effect != effect) return null;
        int type = SkillsFor(player).CooldownBypassType(effect, gem.Level);
        bool power = GolemStat(SupportedSkill(player, gem.Id).Stats, CooldownBypass.PowerOverride) != 0
            || CooldownBypass.FlickerVigilantOverride(SkillsFor(player).EffectId(effect), type)
                && GolemStat(CharacterPlayerStats(player), CooldownBypass.FlickerVigilantPowerOverride) != 0;
        return CooldownBypass.Charge(type, power);
    }

    public static void RefreshSingleUseCooldowns(Player player)
    {
        if (player.SingleUseCooldowns.Count == 0) return;
        var plans = GemSkillEffects(player).GroupBy(g => CooldownKey(player, g.Effect, g.Id)).ToDictionary(g => g.Key, g =>
            g.Select(skill => CooldownFor(player, skill.Effect, skill.Level, skill.Quality))
                .FirstOrDefault(plan => plan is not null));
        double now = AreaTime(player);
        foreach (var (key, state) in player.SingleUseCooldowns.ToArray())
        {
            if (!plans.TryGetValue(key, out var plan)) continue; // Removal does not reset a spent use.
            state.Refresh(now, plan?.RecoveryRate ?? state.Rate);
            if (plan is { } current) state.Capacity(current.Uses);
            player.SkillReadyAt[key] = now + state.RemainingSeconds;
            if (plan is null) player.SingleUseCooldowns.Remove(key); // Unsupported mechanics retain the legacy path.
        }
    }

    public static byte[]? NativeCooldownRefresh(Player player, int epoch,
        Func<IReadOnlyList<(ushort Id, int Effect)>> currentSkills)
    {
        if (!player.ProjectileActive(epoch) || player.PlayerLife == 0) return null;
        double now = AreaTime(player);
        var updates = new List<SingleUseCooldownUpdate>();
        var synchronized = new List<SingleUseCooldownState>();
        foreach (var (id, effect) in currentSkills())
        {
            int key = CooldownKey(player, effect, id);
            if (!player.ClientSkillIds.Contains(id) || !player.SingleUseCooldowns.TryGetValue(key, out var state)
                || key < 0 && state.NativeSkillId != id) continue;
            state.Refresh(now, state.Rate);
            if (!state.NeedsNativeRefresh) continue;
            updates.Add(new(id, state.Held ? state.TotalBaseMilliseconds / 1000f
                    : state.FullyRecovered ? 0 : (float)(state.RemainingBaseMilliseconds / 1000),
                state.TotalBaseMilliseconds / 1000f, state.Held ? 0f : (float)state.Rate, state.SpentUses));
            synchronized.Add(state);
        }
        if (updates.Count == 0) return null;
        var bytes = InstanceEntities.PlayerSingleUseCooldowns(player.Id, updates, canDetonateMines: CanDetonateMines(player));
        foreach (var state in synchronized) state.NativeRefreshed();
        return bytes;
    }

    public static Task SendNativeCooldownRefresh(Player player)
    {
        if (player.SingleUseCooldowns.Count == 0) return Task.CompletedTask;
        bool dirty = false;
        foreach (var s in player.SingleUseCooldowns.Values)
            if (s.NeedsNativeRefresh) { dirty = true; break; }
        if (!dirty) return Task.CompletedTask;
        int epoch;
        epoch = player.ProjectileEpoch;
        return Send(player, [], "0x02bc player active cooldown progress/rate refresh",
            atGate: () => NativeCooldownRefresh(player, epoch, () => GemSkillEffects(player)
                .Where(g => CooldownFor(player, g.Effect, g.Level, g.Quality) is not null)
                .Select(g => (g.Id, g.Effect)).ToArray()));
    }
}
