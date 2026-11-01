using GameServer.Core.World;

/// <summary>"X% of Physical Damage from Hits taken as Fire Damage" and the other damage shifts on the player (wiki Damage
/// taken as): before any mitigation, each type of an incoming hit moves each destination's percentage of its original
/// amount at once, so damage shifts only once, in any direction. The source keeps max(100 - total, 0)%, so a total over
/// 100% raises the damage taken (PoB CalcDefence damageShiftTable). The hit_and_dot stats shift hits here; their damage
/// over time share is not read.</summary>
static class DamageTakenAs
{
    const DamageType P = DamageType.Physical, F = DamageType.Fire, C = DamageType.Cold, L = DamageType.Lightning,
        X = DamageType.Chaos;

    static readonly (string Stat, DamageType[] From, DamageType To)[] Shifts =
    [
        ("physical_damage_taken_%_as_fire", [P], F),
        ("physical_damage_taken_%_as_cold", [P], C),
        ("physical_damage_taken_%_as_lightning", [P], L),
        ("physical_damage_taken_%_as_chaos", [P], X),
        ("physical_hit_and_dot_damage_%_taken_as_fire", [P], F),
        ("fire_damage_taken_%_as_cold", [F], C),
        ("fire_damage_taken_%_as_lightning", [F], L),
        ("fire_damage_taken_%_as_chaos", [F], X),
        ("fire_hit_and_dot_damage_%_taken_as_lightning", [F], L),
        ("base_fire_hit_damage_taken_%_as_physical", [F], P),
        ("base_fire_hit_damage_taken_%_as_physical_value_negated", [F], P),
        ("cold_damage_taken_%_as_fire", [C], F),
        ("cold_damage_taken_%_as_lightning", [C], L),
        ("cold_damage_taken_%_as_chaos", [C], X),
        ("cold_hit_and_dot_damage_%_taken_as_fire", [C], F),
        ("cold_hit_and_dot_damage_%_taken_as_lightning", [C], L),
        ("base_cold_hit_damage_taken_%_as_physical", [C], P),
        ("lightning_damage_taken_%_as_fire", [L], F),
        ("lightning_damage_taken_%_as_cold", [L], C),
        ("lightning_damage_taken_%_as_chaos", [L], X),
        ("lightning_hit_and_dot_damage_%_taken_as_fire", [L], F),
        ("base_lightning_hit_damage_taken_%_as_physical", [L], P),
        ("cold_and_lightning_damage_taken_%_as_fire", [C, L], F),
        ("elemental_damage_taken_%_as_chaos", [F, C, L], X),
        ("elemental_hit_damage_taken_%_as_physical", [F, C, L], P),
        ("chaos_hit_and_dot_damage_%_taken_as_fire", [X], F),
        ("chaos_hit_and_dot_damage_%_taken_as_lightning", [X], L),
    ];

    public static IEnumerable<string> StatNames => Shifts.Select(s => s.Stat);

    /// <summary>The hit as the player takes it; the same packet when no shift applies.</summary>
    public static DamagePacket Shift(DamagePacket hit, Func<string, int> get)
    {
        var percent = new int[5, 5];   // [from, to], by DamageType
        bool any = false;
        foreach (var (stat, from, to) in Shifts)
        {
            // The negated stat is stored as -10 and shown as "10% of Fire Damage from Hits taken as Physical Damage".
            int value = stat.EndsWith("_value_negated") ? -get(stat) : get(stat);
            if (value <= 0) continue;
            foreach (var source in from) percent[(int)source, (int)to] += value;
            any = true;
        }
        if (!any) return hit;
        var taken = DamagePacket.Empty;
        foreach (var (type, range) in hit.Components)
        {
            int moved = 0;
            for (int to = 0; to < 5; to++)
            {
                if (percent[(int)type, to] == 0) continue;
                taken = taken.Add((DamageType)to, range.Scale(percent[(int)type, to] / 100.0));
                moved += percent[(int)type, to];
            }
            taken = taken.Add(type, range.Scale(Math.Max(100 - moved, 0) / 100.0));
        }
        return taken;
    }
}
