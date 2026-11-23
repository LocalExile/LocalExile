using GameServer.Core;
using GameServer.Core.Protocol.Instance;

// Hostile monster Energy Shield: initialized from MonsterCatalog.BaseEnergyShield (Game.exe 0x141e780ef:
// 40% × MonsterTypes.EnergyShield% of base life) plus ES mods (maximum_life_%_to_add_as_maximum_energy_shield,
// maximum_energy_shield_+%). Non-chaos hit damage is absorbed before Life, chaos bypasses ES unless the monster has
// chaos_damage_does_not_bypass_energy_shield or Chaos Inoculation, and recharge begins after 4 s without taking damage
// (scaled by energy_shield_delay_-%) at 20%/s (scaled by energy_shield_recharge_rate_+%, e.g. Merveil/Piety -65%).
public static partial class Game
{
    const double MonsterEsBaseRechargeDelaySeconds = 4.0;
    const double MonsterEsBaseRechargeRatePerSecond = 0.20;

    sealed class MonsterEsState
    {
        public uint Max;
        public uint Current;
        public double LastHitAt = double.NegativeInfinity;
        public double RechargeCarry;
        public int PendingChaosDamage;
    }

    static readonly RuleTable<SpawnedMonster, MonsterEsState> MonsterEsStates = new();
    static int ActiveMonsterEsCount;

    static bool MonsterEsEnabled =>
        (SystemSettings.Default.Values.TryGetValue("monster_es_enabled", out double v) ? v : 1.0) != 0;

    static bool MonsterSpeciesModsEnabled =>
        (SystemSettings.Default.Values.TryGetValue("monster_species_mods_enabled", out double v) ? v : 1.0) != 0;

    static bool MonsterArmourEnabled =>
        (SystemSettings.Default.Values.TryGetValue("monster_armour_enabled", out double v) ? v : 1.0) != 0;

    static int MonsterBaseCritChancePermyriad =>
        Math.Clamp((int)Math.Round(SystemSettings.Default.Values.TryGetValue("monster_base_crit_chance_permyriad", out double v) ? v : 500.0), 0, 10_000);

    static double MonsterAilmentThresholdScaling =>
        SystemSettings.Default.Values.TryGetValue("monster_ailment_threshold_scaling", out double v) ? v : 1.0;

    /// <summary>Computes a hostile monster's maximum Energy Shield from its variety's base ES and its current
    /// <see cref="MonsterModEffects"/>. Returns 0 when <c>monster_es_enabled</c> is 0.</summary>
    public static uint ComputeMonsterMaxEnergyShield(MonsterCatalog.Variety? variety, int level, long baseLife, uint maxLife, MonsterModEffects effects)
    {
        if (!MonsterEsEnabled) return 0;
        int baseEs = variety is not null ? Monsters.BaseEnergyShield(variety, level, baseLife) ?? 0 : 0;
        return effects.MaxEnergyShield(baseEs, maxLife);
    }

    /// <summary>Initializes Energy Shield state for a newly spawned hostile monster. No-op when <paramref name="maxEs"/> is 0.</summary>
    public static void InitMonsterEnergyShield(SpawnedMonster m, uint maxEs)
    {
        if (maxEs == 0) return;
        var state = MonsterEsStates.GetValue(m, _ =>
        {
            Interlocked.Increment(ref ActiveMonsterEsCount);
            return new MonsterEsState();
        });
        state.Max = maxEs;
        state.Current = maxEs;
        state.LastHitAt = double.NegativeInfinity;
        state.RechargeCarry = 0;
        state.PendingChaosDamage = 0;
    }

    /// <summary>Updates a monster's maximum Energy Shield dynamically (e.g., entering/leaving an allied Discipline aura)
    /// and syncs the client's <c>maximum_energy_shield</c> stat and ES pool when it changes.</summary>
    public static void SyncMonsterMaxEnergyShieldLocked(GameArea area, uint id, SpawnedMonster m, uint newMax)
    {
        if (!MonsterEsEnabled || m.Dead || m.Life == 0) return;
        bool had = MonsterEsStates.TryGetValue(m, out var state);
        uint oldMax = had ? state!.Max : 0;
        if (oldMax == newMax) return;
        if (!had && newMax == 0) return;
        if (!had)
        {
            state = MonsterEsStates.GetValue(m, _ =>
            {
                Interlocked.Increment(ref ActiveMonsterEsCount);
                return new MonsterEsState();
            });
        }
        state!.Max = newMax;
        if (newMax > oldMax)
        {
            uint gained = newMax - oldMax;
            state.Current = (uint)Math.Min(newMax, (long)state.Current + gained);
        }
        else
        {
            state.Current = Math.Min(state.Current, newMax);
        }
        area.Delta.Add(id, InstanceEntities.StatsUpdate(id, [(Stat.MaximumEnergyShield, (int)newMax)]));
        area.Life.Set(id, AreaLife.EnergyShieldPool, state.Current);
    }

    /// <summary>True if the monster currently has a positive Energy Shield pool.</summary>
    public static bool HasMonsterEnergyShield(SpawnedMonster m) =>
        Volatile.Read(ref ActiveMonsterEsCount) > 0 && MonsterEsStates.TryGetValue(m, out var s) && s.Max > 0;

    /// <summary>Current and maximum Energy Shield of <paramref name="m"/> (0, 0 when it has none).</summary>
    public static (uint Current, uint Max) MonsterEnergyShieldOf(SpawnedMonster m) =>
        Volatile.Read(ref ActiveMonsterEsCount) > 0 && MonsterEsStates.TryGetValue(m, out var s) ? (s.Current, s.Max) : (0u, 0u);

    /// <summary>Records the chaos-damage share of an outgoing hit just computed by <c>MonsterTakesMitigatedCore</c> so
    /// <see cref="AbsorbMonsterEnergyShieldLocked"/> lets chaos bypass Energy Shield.</summary>
    public static void NoteMonsterHitChaosDamage(SpawnedMonster m, int chaosDamage)
    {
        if (chaosDamage <= 0 || Volatile.Read(ref ActiveMonsterEsCount) == 0) return;
        if (MonsterEsStates.TryGetValue(m, out var state) && state.Max > 0)
            state.PendingChaosDamage = chaosDamage;
    }

    /// <summary>Absorbs non-chaos hit damage into the monster's Energy Shield before Life, interrupts ES recharge,
    /// publishes the updated ES pool via S→C 0x1b1 (pool 2), and returns the remaining damage to apply to Life.</summary>
    public static int AbsorbMonsterEnergyShieldLocked(Player player, SpawnedMonster m, int damage, double now)
    {
        if (damage <= 0 || Volatile.Read(ref ActiveMonsterEsCount) == 0) return damage;
        if (!MonsterEsStates.TryGetValue(m, out var state) || state.Max == 0) return damage;
        int chaosDamage = Math.Clamp(state.PendingChaosDamage, 0, damage);
        state.PendingChaosDamage = 0;
        if (double.IsFinite(now)) state.LastHitAt = now;
        state.RechargeCarry = 0;

        bool ci = m.Effects[Stat.KeystoneChaosInoculation] != 0;
        if (ci)
        {
            damage -= chaosDamage;
            chaosDamage = 0;
        }
        bool chaosBypass = chaosDamage > 0
            && m.Effects[Stat.ChaosDamageDoesNotBypassEnergyShield] == 0
            && m.Effects[Stat.VirtualChaosDamageDoesNotBypassEnergyShield] == 0;

        int shieldDamage = chaosBypass ? damage - chaosDamage : damage;
        int bypassDamage = chaosBypass ? chaosDamage : 0;
        uint absorbed = state.Current > 0 && shieldDamage > 0 ? Math.Min(state.Current, (uint)shieldDamage) : 0;
        if (absorbed > 0)
        {
            state.Current -= absorbed;
            if (m.ObjectId != 0) player.Area.Life.Set(m.ObjectId, AreaLife.EnergyShieldPool, state.Current);
        }
        return (shieldDamage - (int)absorbed) + bypassDamage;
    }

    /// <summary>Advances Energy Shield recharge for one living monster in <see cref="MonsterRegeneration"/>. Starts
    /// recharging after 4 s out of hit (shortened by <c>energy_shield_delay_-%</c>) at 20%/s (scaled by
    /// <c>energy_shield_recharge_rate_+%</c>).</summary>
    public static void StepMonsterEnergyShieldLocked(GameArea area, uint id, SpawnedMonster m, double seconds, double now)
    {
        if (Volatile.Read(ref ActiveMonsterEsCount) == 0 || seconds <= 0 || m.Dead || m.Life == 0) return;
        if (!MonsterEsStates.TryGetValue(m, out var state) || state.Max == 0 || state.Current >= state.Max) return;
        double delay = MonsterEsBaseRechargeDelaySeconds / Math.Max(0.01, 1 + m.Effects[Stat.EnergyShieldDelayMinusPct] / 100.0);
        if (now < state.LastHitAt + delay) return;
        double rechargeSeconds = Math.Min(seconds, now - (state.LastHitAt + delay));
        if (rechargeSeconds <= 0) return;
        double rate = state.Max * MonsterEsBaseRechargeRatePerSecond
            * Math.Max(0, 1 + m.Effects[Stat.EnergyShieldRechargeRatePlusPct] / 100.0);
        if (rate <= 0) return;
        double total = state.RechargeCarry + rate * rechargeSeconds;
        uint whole = (uint)Math.Truncate(total);
        state.RechargeCarry = total - whole;
        if (whole == 0) return;
        state.Current = (uint)Math.Min(state.Max, (long)state.Current + whole);
        if (state.Current >= state.Max) state.RechargeCarry = 0;
        area.Life.Set(id, AreaLife.EnergyShieldPool, state.Current);
    }

    /// <summary>Clears Energy Shield on death so the client ES bar zeroes cleanly.</summary>
    public static void ClearMonsterEnergyShieldLocked(Player player, SpawnedMonster m)
    {
        if (Volatile.Read(ref ActiveMonsterEsCount) == 0) return;
        if (!MonsterEsStates.TryGetValue(m, out var state) || state.Current == 0) return;
        state.Current = 0;
        state.RechargeCarry = 0;
        if (m.ObjectId != 0) player.Area.Life.Set(m.ObjectId, AreaLife.EnergyShieldPool, 0);
    }
}
