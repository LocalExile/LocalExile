using System.Net.Sockets;
using GameServer.Core.Protocol.Login;

public sealed record VaalUseReservation(ushort Skill, int Effect, int Action, int Epoch, int Rage, uint Souls,
    uint Mana, uint Life, double PreventionSeconds, SkillCriticalRoll? CriticalRoll = null)
{
    public bool Executed;
    public long WorkGeneration;
    public int RecoveryGeneration;
    public bool RageConverted;
    public int? TotemPlacements;
}

public static partial class Game
{
    // A Vaal name never opts into the generic area fallback. Each executor is admitted explicitly.
    public static bool VaalExecutor(Player player, int effect)
    {
        string id = SkillsFor(player).EffectId(effect) ?? "";
        return id is "VaalGroundSlam" or "VaalFireball" or "VaalSpark" or "VaalMoltenStrike" or "VaalFireTrap" or "VaalSweep" or VaalReaveEffect
            or VaalImmortalCallEffect or VaalArcticArmourEffect or VaalRejuvenationTotemEffect
            or "VaalHaste" or "VaalSmite" or "VaalHeavyStrike" or VaalMoltenShellEffect or VaalLightningWarpEffect
            or VaalAncestralWarchiefEffect or "VaalDetonateDead" or VaalBladeVortexEffect
            || IsVaalMinion(id) || TimedVaalAuraBuffRows.ContainsKey(id);
    }
    static double VaalResourceTime(Player player) => player.LeavingArea && player.RampageTransferAt is double frozen
        ? frozen : ProjectileWorldTime(player);

    static void WithVaalPrevention(Player player, StatMap stats)
    {
        if (player.PlayerLife == 0) { player.VaalPreventionUntil = 0; player.VaalUse = null; }
        double now = VaalResourceTime(player);
        if (player.VaalPreventionUntil > now && Stat.HasSoulGainPrevention is var key)
        {
            stats[key] = 1;
            StatsChangeAt(player.VaalPreventionUntil, () => VaalResourceTime(player));
        }
    }

    static VaalUseReservation? PlanVaalUse(Player player, ushort id)
    {
        var gem = SupportedSkill(player, id);
        if (!gem.RequirementsMet || !VaalMinionAvailable(player, id) || WeaponRestricted(player, gem.Effect) is not null
            || SkillsFor(player).VaalBudget(gem.Effect, gem.Level) is not { } definition) return null;
        var playerStats = CharacterPlayerStats(player);
        int Get(uint name) => VaalSoulInput(player, playerStats, gem.Stats, name);
        int Sum(params uint[] names) => (int)Math.Clamp(names.Sum(n => (long)Get(n)), int.MinValue, int.MaxValue);
        bool conversion = VaalRageCost.ReplacesSouls(Get(Stat.VaalAttackRageCostInsteadOfSoulsPerUse) != 0,
            true, SkillsFor(player).IsAttack(gem.Effect), true);
        bool free = Get(Stat.NoCost) != 0 || Get(Stat.VirtualNoCost) != 0 || Get(Stat.SkillNoCostOrReservation) != 0;
        int rage = VaalRageCost.Calculate(Math.Max(0, Get(Stat.TotalBaseRageCost)), (int)definition.BaseCost,
            conversion, true, SkillsFor(player).IsAttack(gem.Effect), true, new(Get(Stat.BaseCostPlusPct), Get(Stat.BaseRageCostPlusPct),
                Get(Stat.CostPlusPctFinal), Sum(Stat.BaseSkillCostEfficiencyPlusPct, Stat.BaseAttackSkillCostEfficiencyPlusPct),
                Get(Stat.BaseRageCostEfficiencyPlusPct), free));
        rage = ActorScale.Saturate((long)rage + ChainbreakerRageCost(Get));
        uint souls = conversion || free ? 0 : (uint)VaalSoulCost.Required(definition.BaseCost, Get);
        uint mana = 0, life = 0;
        if (!free)
        {
            var costStats = SupportedStats(player, id);
            mana = (uint)Math.Max(0, costStats.GetValueOrDefault(Stat.ManaCost));
            if (Lifetap.ConvertsCostToLife(gem.Stats, costStats))
            {
                life = mana;
                mana = 0;
            }
        }
        if (Get(Stat.NoManaCost) != 0) mana = 0;
        int preventionMs = SkillsFor(player).SoulGainPreventionMs(gem.Effect, gem.Level);
        // Vaal auras: "Modifiers to Buff Duration also apply to this Skill's Soul Gain Prevention".
        bool buffScaled = Get(Stat.ModifiersToBuffEffectDurationAlsoAffectSoulPreventionDuration) != 0;
        bool skillScaled = Get(Stat.ModifiersToSkillEffectDurationAlsoAffectSoulPreventionDuration) != 0;
        int preventionIncrease = Get(Stat.VaalSkillSoulGainPreventationDurationPlusPct);
        if (Get(Stat.ModifiersToTotemDurationAlsoAffectSoulPreventionDuration) != 0)
            preventionIncrease = ActorScale.Saturate((long)preventionIncrease + Get(Stat.TotemDurationPlusPct));
        int PreventionPlayer(uint name) => ActorScale.Saturate((long)GolemStat(playerStats, name)
            + (name == SkillDurationModifiers.IncreasedDuration ? preventionIncrease : 0));
        double preventionBase = buffScaled || skillScaled
            ? SkillDurationModifiers.Seconds(preventionMs, PreventionPlayer, n => GolemStat(gem.Stats, n),
                buffScaled ? SkillDurationModifiers.Kinds.Buff : SkillDurationModifiers.Kinds.None)
            : preventionMs / 1000d * Math.Max(0, 1 + preventionIncrease / 100d);
        double prevention = Get(Stat.VaalSkillDoesNotApplySoulGainPrevention) != 0
            || Get(Stat.MapPlayerDisableSoulGainPrevention) != 0 ? 0
            : Math.Max(0, preventionBase
                * (1 + Get(Stat.VaalSkillSoulGainPreventionPlusPctFinalFromPactOfKtash) / 100.0));
        return new(id, gem.Effect, player.ActionCount, player.ProjectileEpoch, rage, souls, mana, life, prevention)
            { RageConverted = conversion };
    }

    static bool CanPayVaalUse(Player player, VaalUseReservation plan)
    {
        var stats = CharacterPlayerStats(player);
        var rage = player.Rage.Read(VaalResourceTime(player), RageStats.Inputs(player, n => RageStat(stats, n)));
        if (player.PlayerMana < plan.Mana || plan.Life > 0 && player.PlayerLife <= plan.Life
            || rage.Current < plan.Rage) return false;
        if (plan.Souls == 0) return true;
        RefreshVaalSouls(player);
        var definition = SkillsFor(player).VaalBudget(plan.Effect, SupportedSkill(player, plan.Skill).Level)!.Value;
        return player.VaalSouls.Read().Any(b => b.ActiveSkillsRow == definition.ActiveSkillsRow && b.Current >= plan.Souls);
    }

    // Paid attacks reserve the same cadence as ordinary attacks, before an action/echo or any resource is spent.
    // Approach-only checks leave the timer alone; arrival must reserve the actual swing under its action token.
    static bool TryVaalAttackDeadline(Player player, VaalUseReservation plan, out double? deadline)
    {
        deadline = null;
        if (!SkillsFor(player).IsAttack(plan.Effect)) return true;
        double now = AreaTime(player), duration = SupportedAttackSeconds(player, plan.Skill);
        if (!AttackTiming.Ready(now, player.NextAttackAt, duration)) return false;
        deadline = AttackTiming.After(now, player.NextAttackAt, duration);
        return true;
    }

    static void CommitVaalUse(Player player, VaalUseReservation plan, double? attackDeadline)
    {
        // The caller checked every resource under this same combat lock before changing action/cooldown state.
        var stats = CharacterPlayerStats(player);
        double now = VaalResourceTime(player);
        if (!player.Rage.Spend(plan.Rage, now, RageStats.Inputs(player, n => RageStat(stats, n))))
            throw new InvalidOperationException("Vaal Rage reservation changed inside admission");
        if (plan.Souls > 0)
        {
            var definition = SkillsFor(player).VaalBudget(plan.Effect, SupportedSkill(player, plan.Skill).Level)!.Value;
            if (!player.VaalSouls.TryConsume(definition.ActiveSkillsRow, plan.Souls))
                throw new InvalidOperationException("Vaal soul reservation changed inside admission");
        }
        player.PlayerMana -= plan.Mana;
        player.PlayerLife -= plan.Life;
        ManaSpentLocked(player, plan.Mana, stats);
        uint adrenaline = Stat.AdrenalineOnVaalSkillUseDurationMs;
        if (player.WardBreak.GrantAdrenaline(now, stats.GetValueOrDefault(adrenaline), adrenaline))
        {
            player.Pools = PoolsFor(player, player.Pools.Level);
            player.ApplyMovementSpeed();
        }
        if (plan.Life > 0 || plan.Mana > 0)
            ApplyPaidSupportCostsLocked(player, plan.Skill, SupportedSkill(player, plan.Skill).Stats, plan.Mana, (int)plan.Life, now);
        uint sacrificed = SacrificeItemLifeLocked(player, plan.Effect, stats);
        if (plan.Life + sacrificed > 0) PresentStats(player);
        if (attackDeadline is double next) player.NextAttackAt = next;
        player.VaalPreventionUntil = Math.Max(player.VaalPreventionUntil, now + plan.PreventionSeconds);
        Console.WriteLine($"[gateway] Vaal use paid: skill 0x{plan.Skill:x4} {SkillsFor(player).EffectId(plan.Effect)}, " +
            $"-{plan.Souls} souls, -{plan.Rage} Rage, soul gain prevention {plan.PreventionSeconds:0.##} s (until t={player.VaalPreventionUntil:0.000})");
        player.VaalUse = plan with { Action = player.ActionCount, Epoch = player.ProjectileEpoch, Life = plan.Life + sacrificed,
            CriticalRoll = CaptureSkillCriticalRoll(player, plan.Skill), RecoveryGeneration = player.ActionInterruptionGeneration };
    }

    public const uint MaximumPowerOnVaalUse = Stat.GainMaximumPowerChargesOnVaalSkillUse;
    public static void GrantVaalUseChargesLocked(Player player, double at)
    {
        if (player.Paused || player.Closed || player.LeavingArea || player.PlayerLife == 0 || !CurrentGameWork) return;
        var stats = CharacterPlayerStats(player);
        int Get(uint key) => stats.GetValueOrDefault(key);
        if (Get(MaximumPowerOnVaalUse) <= 0) return;
        var session = ChargesOf(player); session.Observe(Get);
        var step = session.Admit(at, [new ChargeEvent.Grant(ChargeType.Power, session.Limits(ChargeType.Power).Maximum,
            Stats.Name(MaximumPowerOnVaalUse))], Get, () => WeaponProfileFor(player), true, AreaRng(player));
        if (step.CountsChanged) { player.Pools = PoolsFor(player, player.Pools.Level); player.ApplyMovementSpeed(); }
    }

    static VaalUseReservation? ExecuteVaalUse(Player player, ushort id, int effect)
    {
        if (!VaalExecutor(player, effect) || player.VaalUse is not { Executed: false } paid || paid.Skill != id
            || paid.Effect != effect || paid.Action != player.ActionCount || paid.Epoch != player.ProjectileEpoch
            || !VaalAdmissionCurrent(player, paid) || !PlayerActionCurrent(player, paid.Action)) return null;
        paid.Executed = true;
        player.RecentEvents.Record(StatConditions.UsedVaalSkill, VaalResourceTime(player));
        GrantVaalUseChargesLocked(player, VaalResourceTime(player));
        return paid;
    }

    static bool VaalAdmissionCurrent(Player player, VaalUseReservation paid) => CurrentGameRules
        && ReferenceEquals(player.VaalUse, paid) && player.OwnedCombatState is RampageWorld world
        && ReferenceEquals(world.SourceOwner, AscendancyPolicies.SourceOwner(player))
        && player.OwnedCombatWorker.Current(CombatLifetime!, world.SourceOwner, world, paid.WorkGeneration)
        && (!paid.RageConverted || Stat.VaalAttackRageCostInsteadOfSoulsPerUse is var conversion
            && StatSourceValues(player).GetValueOrDefault(conversion) != 0);

    // All native action acknowledgements share the same accepted-use guard at their send gate.
    public static bool SkillAcknowledgementAllowed(Player player, int effect, ushort skillId, Func<bool> nativeUse)
    {
        if (SkillsFor(player).VaalBudget(effect, 1) is not null
            && (player.VaalUse is not { } paid || paid.Skill != skillId || paid.Effect != effect
                || !VaalAdmissionCurrent(player, paid) || !PlayerActionCurrent(player, paid.Action))) return false;
        return OwnAuraAcknowledgementAllowed(player, effect, skillId, nativeUse);
    }

    static void PublishVaalRageCost(Player player, ushort id, Dictionary<uint, int> stats)
    {
        if (PlanVaalUse(player, id) is not { } plan) return;
        void Put(uint name, int value) { if (name is var key) stats[key] = value; }
        Put(Stat.RageCost, plan.Rage);
        var gem = SupportedSkill(player, id);
        var playerStats = CharacterPlayerStats(player);
        if (VaalSoulInput(player, playerStats, gem.Stats, Stat.VaalAttackRageCostInsteadOfSoulsPerUse) != 0
            && SkillsFor(player).IsAttack(gem.Effect)) Put(Stat.VirtualVaalSkillRequireNoSoulsPerUse, 1);
    }

    static async Task PublishVaalUse(Player player,
        VaalUseReservation paid)
    {
        var previous = Program.ProjectileOutputGuard.Value;
        bool Current() => VaalAdmissionCurrent(player, paid) && PlayerActionCurrent(player, paid.Action)
            && (previous?.Invoke() ?? true);
        Program.ProjectileOutputGuard.Value = Current;
        try
        {
            if (!Current()) return;
            await SendRage(player);
            if (!Current()) return;
            await SendVaalSouls(player);
            if (!Current()) return;
            await SendChargeStats(player, "accepted Vaal use");
            if (!Current()) return;
            await SendSkillCost(player, paid.Life, "Vaal use paid");
        }
        finally { Program.ProjectileOutputGuard.Value = previous; }
    }
}
