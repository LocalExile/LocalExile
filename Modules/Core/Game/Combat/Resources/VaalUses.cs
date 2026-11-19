using System.Net.Sockets;
using GameServer.Core.Protocol.Login;

public sealed record VaalUseReservation(ushort Skill, int Effect, int Action, int Epoch, int Rage, uint Souls,
    uint Mana, uint Life, double PreventionSeconds, SkillCriticalRoll? CriticalRoll = null)
{
    public bool Executed;
    public long WorkGeneration;
    public int RecoveryGeneration;
    public bool RageConverted;
}

public static partial class Game
{
    // A Vaal name never opts into the generic area fallback. Each executor is admitted explicitly.
    public static bool VaalExecutor(Player conn, int effect) =>
        SkillsFor(conn).EffectId(effect) is "VaalGroundSlam" or "VaalFireball" or "VaalSpark" or "VaalMoltenStrike"
            || IsVaalMinion(SkillsFor(conn).EffectId(effect)) || VaalAuras.ContainsKey(SkillsFor(conn).EffectId(effect) ?? "");
    static double VaalResourceTime(Player conn) => conn.LeavingArea && conn.RampageTransferAt is double frozen
        ? frozen : ProjectileWorldTime(conn);

    static void WithVaalPrevention(Player conn, StatMap stats)
    {
        lock (conn.Monsters)
        {
            if (conn.PlayerLife == 0) { conn.VaalPreventionUntil = 0; conn.VaalUse = null; }
            double now = VaalResourceTime(conn);
            if (conn.VaalPreventionUntil > now && Stat.HasSoulGainPrevention is var key)
            {
                stats[key] = 1;
                StatsChangeAt(conn.VaalPreventionUntil, () => VaalResourceTime(conn));
            }
        }
    }

    static VaalUseReservation? PlanVaalUse(Player conn, ushort id)
    {
        var gem = SupportedSkill(conn, id);
        if (!gem.RequirementsMet || !VaalMinionAvailable(conn, id) || WeaponRestricted(conn, gem.Effect) is not null
            || SkillsFor(conn).VaalBudget(gem.Effect, gem.Level) is not { } definition) return null;
        var player = CharacterPlayerStats(conn);
        int Get(uint name) => VaalSoulInput(conn, player, gem.Stats, name);
        int Sum(params uint[] names) => (int)Math.Clamp(names.Sum(n => (long)Get(n)), int.MinValue, int.MaxValue);
        bool conversion = VaalRageCost.ReplacesSouls(Get(Stat.VaalAttackRageCostInsteadOfSoulsPerUse) != 0,
            true, SkillsFor(conn).IsAttack(gem.Effect), true);
        bool free = Get(Stat.NoCost) != 0 || Get(Stat.VirtualNoCost) != 0 || Get(Stat.SkillNoCostOrReservation) != 0;
        int rage = VaalRageCost.Calculate(Math.Max(0, Get(Stat.TotalBaseRageCost)), (int)definition.BaseCost,
            conversion, true, SkillsFor(conn).IsAttack(gem.Effect), true, new(Get(Stat.BaseCostPlusPct), Get(Stat.BaseRageCostPlusPct),
                Get(Stat.CostPlusPctFinal), Sum(Stat.BaseSkillCostEfficiencyPlusPct, Stat.BaseAttackSkillCostEfficiencyPlusPct),
                Get(Stat.BaseRageCostEfficiencyPlusPct), free));
        uint souls = conversion || free ? 0 : (uint)VaalSoulCost.Required(definition.BaseCost, Get);
        uint mana = free ? 0 : (uint)Math.Max(0, SupportedStats(conn, id).GetValueOrDefault(Stat.ManaCost));
        // Lifetap turns the whole upfront cost into Life.
        uint life = 0;
        if (!free && Lifetap.ConvertsCostToLife(gem.Stats))
        {
            life = mana;
            mana = 0;
        }
        int preventionMs = SkillsFor(conn).SoulGainPreventionMs(gem.Effect, gem.Level);
        // Vaal auras: "Modifiers to Buff Duration also apply to this Skill's Soul Gain Prevention".
        bool buffScaled = Get(Stat.ModifiersToBuffEffectDurationAlsoAffectSoulPreventionDuration) != 0;
        double preventionBase = buffScaled ? VaalAuraSeconds(preventionMs, player, gem.Stats)
            : Get(Stat.ModifiersToSkillEffectDurationAlsoAffectSoulPreventionDuration) != 0
            ? SkillDurationModifiers.Seconds(preventionMs, n => GolemStat(player, n), n => GolemStat(gem.Stats, n))
            : preventionMs / 1000d;
        double prevention = Get(Stat.VaalSkillDoesNotApplySoulGainPrevention) != 0
            || Get(Stat.MapPlayerDisableSoulGainPrevention) != 0 ? 0
            : Math.Max(0, preventionBase
                * (1 + Get(Stat.VaalSkillSoulGainPreventionPlusPctFinalFromPactOfKtash) / 100.0));
        return new(id, gem.Effect, conn.ActionCount, conn.ProjectileEpoch, rage, souls, mana, life, prevention)
            { RageConverted = conversion };
    }

    static bool CanPayVaalUse(Player conn, VaalUseReservation plan)
    {
        var stats = CharacterPlayerStats(conn);
        var rage = conn.Rage.Read(VaalResourceTime(conn), RageStats.Inputs(n => RageStat(stats, n)));
        if (conn.PlayerMana < plan.Mana || plan.Life > 0 && conn.PlayerLife <= plan.Life
            || rage.Current < plan.Rage) return false;
        if (plan.Souls == 0) return true;
        RefreshVaalSouls(conn);
        var definition = SkillsFor(conn).VaalBudget(plan.Effect, SupportedSkill(conn, plan.Skill).Level)!.Value;
        return conn.VaalSouls.Read().Any(b => b.ActiveSkillsRow == definition.ActiveSkillsRow && b.Current >= plan.Souls);
    }

    // Paid attacks reserve the same cadence as ordinary attacks, before an action/echo or any resource is spent.
    // Approach-only checks leave the timer alone; arrival must reserve the actual swing under its action token.
    static bool TryVaalAttackDeadline(Player conn, VaalUseReservation plan, out double? deadline)
    {
        deadline = null;
        if (!SkillsFor(conn).IsAttack(plan.Effect)) return true;
        double now = conn.Clock.Elapsed.TotalSeconds, duration = SupportedAttackSeconds(conn, plan.Skill);
        if (!AttackTiming.Ready(now, conn.NextAttackAt, duration)) return false;
        deadline = AttackTiming.After(now, conn.NextAttackAt, duration);
        return true;
    }

    static void CommitVaalUse(Player conn, VaalUseReservation plan, double? attackDeadline)
    {
        // The caller checked every resource under this same combat lock before changing action/cooldown state.
        var stats = CharacterPlayerStats(conn);
        double now = VaalResourceTime(conn);
        if (!conn.Rage.Spend(plan.Rage, now, RageStats.Inputs(n => RageStat(stats, n))))
            throw new InvalidOperationException("Vaal Rage reservation changed inside admission");
        if (plan.Souls > 0)
        {
            var definition = SkillsFor(conn).VaalBudget(plan.Effect, SupportedSkill(conn, plan.Skill).Level)!.Value;
            if (!conn.VaalSouls.TryConsume(definition.ActiveSkillsRow, plan.Souls))
                throw new InvalidOperationException("Vaal soul reservation changed inside admission");
        }
        conn.PlayerMana -= plan.Mana;
        if (plan.Life > 0)
        {
            conn.PlayerLife -= plan.Life;
            Lifetap.Spend(conn, SupportedSkill(conn, plan.Skill).Stats, (int)plan.Life, now);
            PresentStats(conn);
        }
        ManaSpentLocked(conn, plan.Mana, stats);
        if (attackDeadline is double next) conn.NextAttackAt = next;
        conn.VaalPreventionUntil = Math.Max(conn.VaalPreventionUntil, now + plan.PreventionSeconds);
        Console.WriteLine($"[gateway] Vaal use paid: skill 0x{plan.Skill:x4} {SkillsFor(conn).EffectId(plan.Effect)}, " +
            $"-{plan.Souls} souls, -{plan.Rage} Rage, soul gain prevention {plan.PreventionSeconds:0.##} s (until t={conn.VaalPreventionUntil:0.000})");
        conn.VaalUse = plan with { Action = conn.ActionCount, Epoch = conn.ProjectileEpoch,
            CriticalRoll = CaptureSkillCriticalRoll(conn, plan.Skill), RecoveryGeneration = conn.ActionInterruptionGeneration };
    }

    public const uint MaximumPowerOnVaalUse = Stat.GainMaximumPowerChargesOnVaalSkillUse;
    public static void GrantVaalUseChargesLocked(Player conn, double at)
    {
        if (conn.Paused || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0 || !CurrentGameWork) return;
        var stats = CharacterPlayerStats(conn);
        int Get(uint key) => stats.GetValueOrDefault(key);
        if (Get(MaximumPowerOnVaalUse) <= 0) return;
        var session = ChargesOf(conn); session.Observe(Get);
        var step = session.Admit(at, [new ChargeEvent.Grant(ChargeType.Power, session.Limits(ChargeType.Power).Maximum,
            Stats.Name(MaximumPowerOnVaalUse))], Get, () => WeaponProfileFor(conn), true, Random.Shared);
        if (step.CountsChanged) { conn.Pools = PoolsFor(conn, conn.Pools.Level); conn.ApplyMovementSpeed(); }
    }

    static VaalUseReservation? ExecuteVaalUse(Player conn, ushort id, int effect)
    {
        lock (conn.Monsters)
        {
            if (!VaalExecutor(conn, effect) || conn.VaalUse is not { Executed: false } paid || paid.Skill != id
                || paid.Effect != effect || paid.Action != conn.ActionCount || paid.Epoch != conn.ProjectileEpoch
                || !VaalAdmissionCurrent(conn, paid) || !PlayerActionCurrent(conn, paid.Action)) return null;
            paid.Executed = true;
            conn.RecentEvents.Record(StatConditions.UsedVaalSkill, VaalResourceTime(conn));
            GrantVaalUseChargesLocked(conn, VaalResourceTime(conn));
            return paid;
        }
    }

    static bool VaalAdmissionCurrent(Player conn, VaalUseReservation paid) => CurrentGameRules
        && ReferenceEquals(conn.VaalUse, paid) && conn.OwnedCombatState is RampageWorld world
        && ReferenceEquals(world.SourceOwner, AscendancyPolicies.SourceOwner(conn))
        && conn.OwnedCombatWorker.Current(CombatLifetime!, world.SourceOwner, world, paid.WorkGeneration)
        && (!paid.RageConverted || Stat.VaalAttackRageCostInsteadOfSoulsPerUse is var conversion
            && CharacterStatSources(conn).Stats.GetValueOrDefault(conversion) != 0);

    // All native action acknowledgements share the same accepted-use guard at their send gate.
    public static bool SkillAcknowledgementAllowed(Player conn, int effect, ushort skillId, Func<bool> nativeUse)
    {
        lock (conn.Monsters)
        {
            if (SkillsFor(conn).VaalBudget(effect, 1) is not null
                && (conn.VaalUse is not { } paid || paid.Skill != skillId || paid.Effect != effect
                    || !VaalAdmissionCurrent(conn, paid) || !PlayerActionCurrent(conn, paid.Action))) return false;
            return OwnAuraAcknowledgementAllowed(conn, effect, skillId, nativeUse);
        }
    }

    static void PublishVaalRageCost(Player conn, ushort id, Dictionary<uint, int> stats)
    {
        if (PlanVaalUse(conn, id) is not { } plan) return;
        void Put(uint name, int value) { if (name is var key) stats[key] = value; }
        Put(Stat.RageCost, plan.Rage);
        var gem = SupportedSkill(conn, id);
        var player = CharacterPlayerStats(conn);
        if (VaalSoulInput(conn, player, gem.Stats, Stat.VaalAttackRageCostInsteadOfSoulsPerUse) != 0
            && SkillsFor(conn).IsAttack(gem.Effect)) Put(Stat.VirtualVaalSkillRequireNoSoulsPerUse, 1);
    }

    static async Task PublishVaalUse(Player conn,
        VaalUseReservation paid)
    {
        var previous = Program.ProjectileOutputGuard.Value;
        bool Current() => VaalAdmissionCurrent(conn, paid) && PlayerActionCurrent(conn, paid.Action)
            && (previous?.Invoke() ?? true);
        Program.ProjectileOutputGuard.Value = Current;
        try
        {
            if (!Current()) return;
            await SendRage(conn);
            if (!Current()) return;
            await SendVaalSouls(conn);
            if (!Current()) return;
            await SendChargeStats(conn, "accepted Vaal use");
            if (!Current()) return;
            await SendSkillCost(conn, paid.Life, "Vaal use paid");
        }
        finally { Program.ProjectileOutputGuard.Value = previous; }
    }
}
