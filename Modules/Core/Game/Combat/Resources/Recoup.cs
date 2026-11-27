using GameServer.Core.World;

public static partial class Game
{
    static readonly Store<Player, RecoupRecovery> ShieldRecoup = new();

    // Life Recoup (poedb Recoup: modifiers "that recover some of the damage taken from hits as energy shield, life, or
    // mana over 4 seconds"; "Only Damage from Hits can be Recouped, over 4 seconds following the Hit"). PoB CalcDefence
    // scales it by the Life recovery rate and counts the hit's damage before ward, energy shield and life take it.
    // Nine Lives' recoup_effects_apply_over_3_seconds_instead shortens it to 3 s.
    static readonly (DamageType Type, uint Stat)[] LifeRecoupByType =
    [
        (DamageType.Physical, Stat.PhysicalDamageTakenRecoupedAsLifePct),
        (DamageType.Fire, Stat.FireDamageTakenGoesToLifeOver4SecondsPct),
        (DamageType.Cold, Stat.ColdDamageTakenGoesToLifeOver4SecondsPct),
        (DamageType.Lightning, Stat.LightningDamageTakenGoesToLifeOver4SecondsPct),
        (DamageType.Chaos, Stat.ChaosDamageTakenGoesToLifeOver4SecondsPct),
    ];

    /// <summary>The Life a hit's taken damage recoups: the general percentage of all of it plus the per-type ones,
    /// raised by the Life recovery rate; cannot_recover_life stops it.</summary>
    public static double LifeRecoupOf(Func<uint, int> get, DamagePacket taken) =>
        LifeRecovery.NonLeechBlocked(get) ? 0 : UnscaledLifeRecoup(get, taken, 0) * RecoupRate(get, shield: false);

    static double UnscaledLifeRecoup(Func<uint, int> get, DamagePacket taken, int additionalPct)
    {
        double life = taken.MinTotal * ((long)get(Stat.DamageTakenGoesToLifeOver4SecondsPct) + additionalPct);
        foreach (var (type, stat) in LifeRecoupByType) life += taken[type].Min * get(stat);
        return life / 100;
    }

    static double RecoupRate(Func<uint, int> get, bool shield) => Math.Max(0,
        1 + get(shield ? Stat.EnergyShieldRecoveryRatePlusPct : Stat.LifeRecoveryRatePlusPct) / 100d);

    static bool ShieldRecoupBlocked(Func<uint, int> get) =>
        get(Stat.CannotRecoverEnergyShield) != 0 || get(Stat.NoEnergyShieldRecovery) != 0;

    static void AddLifeRecoupLocked(Player player, Func<uint, int> get, double now, double amount)
    {
        if (amount <= 0) return;
        bool shield = get(Stat.LifeRecoupAppliesToEnergyShieldInstead) != 0;
        if (shield ? ShieldRecoupBlocked(get) : LifeRecovery.NonLeechBlocked(get)) return;
        var recovery = shield ? ShieldRecoup.GetValue(player, static _ => new()) : player.LifeRecoup;
        recovery.Add(now, amount * RecoupRate(get, shield), LifeRecoupSeconds(get));
    }

    static void AdvanceShieldRecoupLocked(Player player, double now, Func<uint, int> get, double mapRecovery, uint esCeiling)
    {
        if (!ShieldRecoup.TryGetValue(player, out var recovery)) return;
        double amount = recovery.Take(now);
        if (amount > 0 && !ShieldRecoupBlocked(get)) player.EnergyShield.Gain(amount * mapRecovery, esCeiling);
    }

    public static double LifeRecoupSeconds(Func<uint, int> get) =>
        get(Stat.RecoupEffectsApplyOver3SecondsInstead) != 0
            || get(Stat.RecoupLifeEffectsApplyOver3SecondsInstead) != 0 ? 3 : 4;

    /// <summary>The Mana recoup window. "Recoup Effects instead occur over 3 seconds" covers every kind of recoup; the
    /// stat has a Life-only form, which does not shorten this.</summary>
    public static double ManaRecoupSeconds(Func<uint, int> get) =>
        get(Stat.RecoupEffectsApplyOver3SecondsInstead) != 0 ? 3 : ManaRecoupState.RecoupSeconds;

    // A hit the player took (after block, mitigation and Fortification), in the area's frame: its recoup starts.
    public static void RecoupHitLocked(Player player, DamagePacket taken, double now,
        IReadOnlyDictionary<uint, int> stats)
    {
        if (player.Immune || player.PlayerLife == 0 || taken.IsZero) return;
        int Get(uint key) => stats.GetValueOrDefault(key);
        int whileFrozen = Get(Stat.DamageTakenWhileFrozenRecoupedAsLifePct);
        if (whileFrozen != 0 && player.ElementalAilments.FreezeStatusUntil(now) <= now) whileFrozen = 0;
        AddLifeRecoupLocked(player, Get, now, UnscaledLifeRecoup(Get, taken, whileFrozen));
    }

    static void RecoupTotemHitLocked(Player owner, uint taken, double now, IReadOnlyDictionary<uint, int>? stats = null)
    {
        if (taken == 0 || owner.PlayerLife == 0 || owner.LeavingArea) return;
        stats ??= PlayerStatValues(owner);
        int Get(uint key) => stats.GetValueOrDefault(key);
        int percent = Get(Stat.RecoupPctOfDamageTakenByYourTotemsAsLife);
        AddLifeRecoupLocked(owner, Get, now, taken * (double)percent / 100);
    }
}
