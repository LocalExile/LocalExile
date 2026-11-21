using GameServer.Core.World;

public static partial class Game
{
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
    public static double LifeRecoupOf(Func<uint, int> get, DamagePacket taken)
    {
        if (get(Stat.CannotRecoverLife) != 0) return 0;
        double life = taken.MinTotal * get(Stat.DamageTakenGoesToLifeOver4SecondsPct);
        foreach (var (type, stat) in LifeRecoupByType) life += taken[type].Min * get(stat);
        return life / 100 * Math.Max(0, 1 + get(Stat.LifeRecoveryRatePlusPct) / 100.0);
    }

    public static double LifeRecoupSeconds(Func<uint, int> get) =>
        get(Stat.RecoupEffectsApplyOver3SecondsInstead) != 0
            || get(Stat.RecoupLifeEffectsApplyOver3SecondsInstead) != 0 ? 3 : 4;

    // A hit the player took (after block, mitigation and Fortification), in the area's frame: its recoup starts.
    public static void RecoupHitLocked(Player player, DamagePacket taken, double now,
        IReadOnlyDictionary<uint, int> stats)
    {
        if (player.Immune || player.PlayerLife == 0 || taken.IsZero) return;
        int Get(uint key) => stats.GetValueOrDefault(key);
        player.LifeRecoup.Add(now, LifeRecoupOf(Get, taken), LifeRecoupSeconds(Get));
    }
}
