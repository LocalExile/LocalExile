using GameServer.Core.World;


/// <summary>
/// The Offence tab's damaging-ailment rows (issue #75): Bleeding, Ignite and Poison damage per second from a hit of
/// one context (main hand, off hand, spell, secondary), from hits and from critical strikes, as the per-minute
/// minimum and maximum the client divides by 60. The client derives none of them (11-skills.md §4).
/// <para>
/// An ailment's base is the hit's damage of the ailment's type(s) after conversion, before the hit-only modifiers:
/// bleed the physical damage (70% per second, <see cref="Ailments"/>), ignite the fire damage (90%), poison the
/// physical and chaos damage (30%). Each converted portion keeps the increases of every type it visited, as the hit
/// itself does (<see cref="PlayerHitDamage"/>), plus the ailment's own buckets: generic, attack or spell, melee,
/// area and projectile damage, damage over time (generic and the ailment's type), "Bleeding / Burning / Poison
/// Damage", the elemental increase for ignite, then the damage-over-time multipliers (generic, the ailment type's,
/// and for critical strikes <c>critical_ailment_dot_multiplier_+</c>: ailments inflicted by critical strikes gain
/// nothing else from the critical multiplier) and the Frenzy Charges' more multiplier. Pinned PoB CalcOffence
/// (16de4b82, calcAilmentDamage and the bleed/poison/ignite sections) composes the same buckets; conditional sources,
/// stacking, "as though dealing more", support more multipliers and the moving bleed bonus are not modelled.
/// </para>
/// The "from only Chaos Damage" poison rows (<c>_to_always_deal_</c>) show the poison the chaos portion alone deals,
/// when a "Chaos Damage has a chance to Poison" source is present.
/// </summary>
static class AilmentPanelStats
{
    /// <summary>The hit's context: which rows it fills (<paramref name="Hand"/>: main_hand, off_hand, spell,
    /// secondary) and which damage buckets apply.</summary>
    public readonly record struct Context(string Hand, bool Spell, bool Melee = false, bool Area = false, bool Projectile = false)
    {
        public bool Attack => Hand is "main_hand" or "off_hand";
    }

    public const int BleedPercent = 70, IgnitePercent = 90, PoisonPercent = 30;

    public static List<(string Stat, int Value)> Compute(Context context, DamagePacket raw, DamageConversion conversion,
        Func<string, int> get)
    {
        var rows = new List<(string, int)>();
        var paths = conversion.Trace(raw).ToList();
        double charges = ChargeEffects.DamageMore(get, context.Spell, ailment: true);
        void Add(string ailment, DamageType ailmentType, DamageType[] sources, int percent, bool chaosOnly = false)
        {
            foreach (bool critical in new[] { false, true })
            {
                var (min, max) = Range(paths, sources, ailmentType, context, get, critical);
                string kind = critical ? "critical" : "non_critical";
                string name = $"{ailment}_damage_to_{(chaosOnly ? "always_" : "")}deal_per_minute_from_{kind}_{context.Hand}_damage";
                rows.Add(($"minimum_{name}", PerMinute(min * percent / 100.0 * charges)));
                rows.Add(($"maximum_{name}", PerMinute(max * percent / 100.0 * charges)));
            }
        }
        if (context.Attack) Add("bleeding", DamageType.Physical, [DamageType.Physical], BleedPercent);
        Add("ignite", DamageType.Fire, [DamageType.Fire], IgnitePercent);
        Add("poison", DamageType.Chaos, [DamageType.Physical, DamageType.Chaos], PoisonPercent);
        if (get("chaos_damage_chance_to_poison_%") != 0)
            Add("poison", DamageType.Chaos, [DamageType.Chaos], PoisonPercent, chaosOnly: true);
        return rows;
    }

    static int PerMinute(double perSecond) =>
        double.IsNaN(perSecond) || perSecond <= 0 ? 0 : (int)Math.Min(int.MaxValue, Math.Round(perSecond * 60));

    static (double Min, double Max) Range(List<DamageConversionPath> paths, DamageType[] sources, DamageType ailmentType,
        Context context, Func<string, int> get, bool critical)
    {
        if (get("base_deal_no_damage") != 0 || get("never_damaging_ailments") != 0) return (0, 0);
        double common = (double)get("damage_+%") + get("damage_over_time_+%")
            + (context.Attack ? get("attack_damage_+%") + DamageStats.SpellAttackIncrease(get) : 0)
            + (context.Spell ? get("spell_damage_+%") : 0)
            + (context.Melee ? get("melee_damage_+%") : 0) + (context.Area ? get("area_damage_+%") : 0)
            + (context.Projectile ? get("projectile_damage_+%") : 0)
            + get(Slug(ailmentType) + "_damage_+%") + get(Slug(ailmentType) + "_damage_over_time_+%")
            + ailmentType switch
            {
                DamageType.Physical => get("bleeding_damage_+%"),
                DamageType.Fire => get("burn_damage_+%") + get("elemental_damage_+%"),
                _ => get("base_poison_damage_+%"),
            };
        double multiplier = Math.Max(0, 1 + ((double)get("dot_multiplier_+") + get(Slug(ailmentType) + "_dot_multiplier_+")
            + (critical ? get("critical_ailment_dot_multiplier_+") : 0)) / 100)
            // Burning Arrow's quality: 1% more ignite damage per quality (GrantedEffectQualityStats).
            * (ailmentType == DamageType.Fire ? Math.Max(0, 1 + get("active_skill_ignite_damage_+%_final") / 100.0) : 1);
        var typed = DamageTypes.ConversionOrder.Select(type => (History: DamageTypes.HistoryOf(type), Increased: get(Slug(type) + "_damage_+%"))).ToArray();
        int elemental = get("elemental_damage_+%");
        double min = 0, max = 0;
        foreach (var path in paths)
        {
            if (!sources.Contains(path.Type)) continue;
            if (DamageTypes.IsElemental(path.Type) && get("deal_no_elemental_damage") != 0
                || path.Type == DamageType.Chaos && get("base_deal_no_chaos_damage") != 0) continue;
            double increased = common;
            // The types the portion visited count once each; the ailment's own type is already in the common bucket.
            foreach (var source in typed)
                if ((path.History & source.History) != 0 && source.History != DamageTypes.HistoryOf(ailmentType)) increased += source.Increased;
            if ((path.History & DamageTypeHistory.Elemental) != 0 && ailmentType != DamageType.Fire) increased += elemental;
            if ((path.History & DamageTypeHistory.Physical) != 0 && context.Melee) increased += get("melee_physical_damage_+%");
            double factor = Math.Max(0, 1 + increased / 100) * multiplier;
            min += path.Range.Min * factor;
            max += path.Range.Max * factor;
        }
        return (min, max);
    }

    static string Slug(DamageType type) => type.ToString().ToLowerInvariant();

    /// <summary>Every row name of a context, for callers that clear the rows of a hand that is no longer swung.</summary>
    public static IEnumerable<string> Rows(string hand)
    {
        foreach (string ailment in new[] { "bleeding", "ignite", "poison" })
        {
            if (ailment == "bleeding" && hand is not ("main_hand" or "off_hand")) continue;
            foreach (string kind in new[] { "non_critical", "critical" })
                foreach (string bound in new[] { "minimum", "maximum" })
                {
                    yield return $"{bound}_{ailment}_damage_to_deal_per_minute_from_{kind}_{hand}_damage";
                    if (ailment == "poison") yield return $"{bound}_poison_damage_to_always_deal_per_minute_from_{kind}_{hand}_damage";
                }
        }
    }
}
