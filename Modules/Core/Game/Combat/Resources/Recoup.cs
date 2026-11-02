using GameServer.Core.World;

static partial class Game
{
    // Life Recoup (poedb Recoup: modifiers "that recover some of the damage taken from hits as energy shield, life, or
    // mana over 4 seconds"; "Only Damage from Hits can be Recouped, over 4 seconds following the Hit"). PoB CalcDefence
    // scales it by the Life recovery rate and counts the hit's damage before ward, energy shield and life take it.
    // Nine Lives' recoup_effects_apply_over_3_seconds_instead shortens it to 3 s.
    static readonly (DamageType Type, string Stat)[] LifeRecoupByType =
    [
        (DamageType.Physical, "physical_damage_taken_recouped_as_life_%"),
        (DamageType.Fire, "fire_damage_taken_goes_to_life_over_4_seconds_%"),
        (DamageType.Cold, "cold_damage_taken_goes_to_life_over_4_seconds_%"),
        (DamageType.Lightning, "lightning_damage_taken_goes_to_life_over_4_seconds_%"),
        (DamageType.Chaos, "chaos_damage_taken_goes_to_life_over_4_seconds_%"),
    ];

    /// <summary>The Life a hit's taken damage recoups: the general percentage of all of it plus the per-type ones,
    /// raised by the Life recovery rate; cannot_recover_life stops it.</summary>
    internal static double LifeRecoupOf(Func<string, int> get, DamagePacket taken)
    {
        if (get("cannot_recover_life") != 0) return 0;
        double life = taken.MinTotal * get("damage_taken_goes_to_life_over_4_seconds_%");
        foreach (var (type, stat) in LifeRecoupByType) life += taken[type].Min * get(stat);
        return life / 100 * Math.Max(0, 1 + get("life_recovery_rate_+%") / 100.0);
    }

    internal static double LifeRecoupSeconds(Func<string, int> get) =>
        get("recoup_effects_apply_over_3_seconds_instead") != 0
            || get("recoup_life_effects_apply_over_3_seconds_instead") != 0 ? 3 : 4;

    // A hit the player took (after block, mitigation and Fortification), under the Monsters lock: its recoup starts.
    internal static void RecoupHitLocked(ClientState conn, DamagePacket taken, double now,
        IReadOnlyDictionary<uint, int> stats)
    {
        if (conn.Immune || conn.PlayerLife == 0 || taken.IsZero) return;
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        conn.LifeRecoup.Add(now, LifeRecoupOf(Get, taken), LifeRecoupSeconds(Get));
    }
}
