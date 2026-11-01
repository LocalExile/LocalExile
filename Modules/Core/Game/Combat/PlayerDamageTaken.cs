using GameServer.Core.World;

/// <summary>The player's own stats that change an incoming hit: the damage shift before mitigation, then after resistance
/// and armour the flat damage taken, the increased damage taken and the share of a blocked hit (PoB CalcDefence's
/// TakenHit: max(damage after mitigation + flat, 0) × the taken multiplier), and last the share of its Life damage that
/// mana takes.</summary>
static class PlayerDamageTaken
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

    const string Increased = "base_damage_taken_+%", ElementalIncreased = "elemental_damage_taken_+%",
        FlatPhysical = "physical_damage_taken_+", FlatPhysicalFromAttacks = "physical_attack_damage_taken_+",
        FlatFire = "fire_damage_taken_+", Blocked = "base_block_%_damage_taken",
        ElementalBlocked = "block_%_damage_taken_from_elemental";

    // Mind Over Matter's and Glancing Blows' flags have no value: their 40% and 65% are the keystones' own text (3.29 tree,
    // wiki Mind Over Matter and Glancing Blows).
    const string ManaShield = "keystone_mana_shield", ManaBeforeLife = "base_damage_removed_from_mana_before_life_%",
        GlancingBlows = "keystone_glancing_blows";
    const int ManaShieldPct = 40, GlancingBlowsBlockedPct = 65;

    static string TypedIncreased(DamageType type) => $"{type.ToString().ToLowerInvariant()}_damage_taken_+%";

    static string? TypedManaBeforeLife(DamageType type) => type switch
    {
        DamageType.Physical => "physical_damage_%_taken_from_mana_before_life",
        DamageType.Lightning => "lightning_damage_%_taken_from_mana_before_life",
        DamageType.Chaos => "chaos_damage_%_taken_from_mana_before_life",
        _ => null,
    };

    public static IEnumerable<string> StatNames => Shifts.Select(s => s.Stat)
        .Concat(DamageTypes.ConversionOrder.Select(TypedIncreased))
        .Concat(DamageTypes.ConversionOrder.Select(TypedManaBeforeLife).OfType<string>())
        .Concat([Increased, ElementalIncreased, FlatPhysical, FlatPhysicalFromAttacks, FlatFire, Blocked, ElementalBlocked,
            ManaShield, ManaBeforeLife, GlancingBlows]);

    /// <summary>"X% increased Damage taken", "X% increased Lightning Damage taken" and "X% increased Elemental Damage
    /// taken": one increased value for this type, which adds to the shock's (PlayerElementalAilments.DamageTakenFactor).</summary>
    public static int IncreasedPct(DamageType type, Func<string, int> get) =>
        get(Increased) + get(TypedIncreased(type)) + (DamageTypes.IsElemental(type) ? get(ElementalIncreased) : 0);

    /// <summary>"+X Physical Damage taken from Hits", "... from Attack Hits" and "+X Fire Damage taken from Hits", added to
    /// the type's damage after resistance and armour.</summary>
    public static int Flat(DamageType type, bool attack, Func<string, int> get) => type switch
    {
        DamageType.Physical => get(FlatPhysical) + (attack ? get(FlatPhysicalFromAttacks) : 0),
        DamageType.Fire => get(FlatFire),
        _ => 0,
    };

    /// <summary>"You take X% of Damage from Blocked Hits" (Glancing Blows' 65% too, added as PoB adds its BlockEffect) and
    /// "... of Elemental Damage from Blocked Hits": the share of the type's damage a blocked hit still deals, 0 by default
    /// and over 100% allowed (wiki Blocking). A blocked hit still applies no ailments or other on-hit effects.</summary>
    public static int BlockedPct(DamageType type, Func<string, int> get) =>
        Math.Max(0, get(Blocked) + (get(GlancingBlows) != 0 ? GlancingBlowsBlockedPct : 0)
            + (DamageTypes.IsElemental(type) ? get(ElementalBlocked) : 0));

    static int SharedManaBeforeLife(Func<string, int> get) => (get(ManaShield) != 0 ? ManaShieldPct : 0) + get(ManaBeforeLife);

    /// <summary>"X% of Damage is taken from Mana before Life", Mind Over Matter's 40% and the typed forms ("30% of
    /// Lightning Damage is taken from Mana before Life"): the share of this type's Life damage that mana takes first, at
    /// most 100% (wiki Mana before Life; PoB CalcDefence adds the typed share to the shared one).</summary>
    public static int ManaBeforeLifePct(DamageType type, Func<string, int> get) =>
        Math.Clamp(SharedManaBeforeLife(get) + (TypedManaBeforeLife(type) is string typed ? get(typed) : 0), 0, 100);

    /// <summary>The mana a hit takes from what reaches Life (after ward and energy shield), rounded up as PoB does; what
    /// mana cannot pay stays on Life. <paramref name="types"/>: the hit's damage by type, whose proportions weigh the typed
    /// shares of the non-chaos part.</summary>
    public static int FromMana(int nonChaosLife, int chaosLife, DamagePacket types, int mana, Func<string, int> get)
    {
        var nonChaos = types.Components.Where(c => c.Type != DamageType.Chaos && c.Range.Min > 0).ToList();
        double total = nonChaos.Sum(c => c.Range.Min);
        double nonChaosPct = total > 0 ? nonChaos.Sum(c => c.Range.Min * ManaBeforeLifePct(c.Type, get)) / total
            : Math.Clamp(SharedManaBeforeLife(get), 0, 100);
        double share = (nonChaosLife * nonChaosPct + chaosLife * ManaBeforeLifePct(DamageType.Chaos, get)) / 100;
        return (int)Math.Clamp(Math.Ceiling(share - 1e-9), 0, Math.Max(0, mana));
    }

    /// <summary>"X% of Physical Damage from Hits taken as Fire Damage" and the other damage shifts (wiki Damage taken as):
    /// before any mitigation, each type moves each destination's percentage of its original amount at once, so damage
    /// shifts only once, in any direction. The source keeps max(100 - total, 0)%, so a total over 100% raises the damage
    /// taken (PoB CalcDefence damageShiftTable). The hit_and_dot stats shift hits here; their damage over time share is
    /// not read. The same packet when no shift applies.</summary>
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
