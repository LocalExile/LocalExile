using GameServer.Core.World;


/// <summary>
/// Damage-type immunity stats (issue #75, the Defence tab's "Immune to X Damage" rows). A source sets the base form
/// (<c>base_*_immunity</c>), <c>elemental_damage_immunity</c> covers fire, cold and lightning, and chaos keeps its older
/// <c>chaos_immunity</c>; the display stat itself (<c>fire_damage_immunity</c> …) is accepted too, so a mod or
/// <c>/set_stat</c> can set it directly. The damage model and the character panel both read this table.
/// </summary>
public static class DamageImmunities
{
    static readonly (uint Stat, DamageType[] Types)[] Sources =
    [
        (Stat.FireDamageImmunity, [DamageType.Fire]),
        (Stat.BaseFireImmunity, [DamageType.Fire]),
        (Stat.ColdDamageImmunity, [DamageType.Cold]),
        (Stat.BaseColdImmunity, [DamageType.Cold]),
        (Stat.LightningDamageImmunity, [DamageType.Lightning]),
        (Stat.BaseLightningImmunity, [DamageType.Lightning]),
        (Stat.ChaosDamageImmunity, [DamageType.Chaos]),
        (Stat.ChaosImmunity, [DamageType.Chaos]),
        (Stat.KeystoneChaosInoculation, [DamageType.Chaos]),
        (Stat.PhysicalImmunity, [DamageType.Physical]),
        (Stat.ElementalDamageImmunity, [DamageType.Fire, DamageType.Cold, DamageType.Lightning]),
    ];

    /// <summary>Every source stat and the damage types it immunises.</summary>
    public static IEnumerable<(uint Stat, DamageType[] Types)> All => Sources;

    /// <summary>The immune damage types, from a stat lookup by Stats.dat Id (0 when absent).</summary>
    public static HashSet<DamageType> From(Func<uint, int> get)
    {
        var immune = new HashSet<DamageType>();
        foreach (var (stat, types) in Sources)
        {
            if (get(stat) == 0) continue;
            foreach (var type in types) immune.Add(type);
        }
        return immune;
    }
}
