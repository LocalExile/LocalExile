using GameServer.Core.World;


/// <summary>
/// Damage-type immunity stats (issue #75, the Defence tab's "Immune to X Damage" rows). A source sets the base form
/// (<c>base_*_immunity</c>), <c>elemental_damage_immunity</c> covers fire, cold and lightning, and chaos keeps its older
/// <c>chaos_immunity</c>; the display stat itself (<c>fire_damage_immunity</c> …) is accepted too, so a mod or
/// <c>/set_stat</c> can set it directly. The damage model and the character panel both read this table.
/// </summary>
static class DamageImmunities
{
    static readonly (string Stat, DamageType[] Types)[] Sources =
    [
        ("fire_damage_immunity", [DamageType.Fire]),
        ("base_fire_immunity", [DamageType.Fire]),
        ("cold_damage_immunity", [DamageType.Cold]),
        ("base_cold_immunity", [DamageType.Cold]),
        ("lightning_damage_immunity", [DamageType.Lightning]),
        ("base_lightning_immunity", [DamageType.Lightning]),
        ("chaos_damage_immunity", [DamageType.Chaos]),
        ("chaos_immunity", [DamageType.Chaos]),
        ("keystone_chaos_inoculation", [DamageType.Chaos]),
        ("physical_immunity", [DamageType.Physical]),
        ("elemental_damage_immunity", [DamageType.Fire, DamageType.Cold, DamageType.Lightning]),
    ];

    /// <summary>Every source stat and the damage types it immunises.</summary>
    public static IEnumerable<(string Stat, DamageType[] Types)> All => Sources;

    /// <summary>The immune damage types, from a stat lookup by Stats.dat Id (0 when absent).</summary>
    public static HashSet<DamageType> From(Func<string, int> get)
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
