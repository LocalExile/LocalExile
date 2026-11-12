using GameServer.Core.World;


/// <summary>
/// The Offence tab's damaging-ailment rows (issue #75): Bleeding, Ignite and Poison damage per second from a hit of
/// one context (main hand, off hand, spell, secondary), from hits and from critical strikes, as the per-minute
/// minimum and maximum the client divides by 60. The client derives none of them (11-skills.md §4).
/// <para>
/// An ailment's base is the hit's damage of the ailment's type(s) after conversion, before the hit-only modifiers:
/// bleed the physical damage (70% per second, <see cref="Ailments"/>), ignite the fire damage (90%), poison the
/// physical and chaos damage (30%). Each converted portion keeps the increases of every type it visited, as the hit
/// itself does (<see cref="PlayerHitDamage"/>), plus the ailment's own buckets: generic damage, damage over time
/// (generic and the ailment type's), "Damage with Ailments", "Bleeding / Burning / Ignite / Poison Damage", the
/// elemental increase for ignite, then the damage-over-time multipliers (generic, the ailment type's, for ailments, the
/// ailment's own, and for critical strikes <c>critical_ailment_dot_multiplier_+</c> and the ailment's: ailments inflicted
/// by critical strikes gain nothing else from the critical multiplier), the Frenzy Charges' more multiplier, the
/// supports' (<see cref="SupportMore"/>) and Crimson Dance's 50% less bleeding.
/// Modifiers to hits (attack, spell, melee, area and projectile damage) never reach an ailment (wiki Ignite and
/// Bleeding); pinned PoB CalcOffence (16de4b82) builds the bleed, poison and ignite configs without those flags.
/// Conditional sources, "as though dealing more" and the moving bleed bonus (a monster's, MonsterBleeds) are not here.
/// </para>
/// The "from only Chaos Damage" poison rows (<c>_to_always_deal_</c>) show the poison the chaos portion alone deals,
/// when a "Chaos Damage has a chance to Poison" source is present.
/// </summary>
public static class AilmentPanelStats
{
    /// <summary>The hit's context: which rows it fills (<paramref name="Hand"/>: main_hand, off_hand, spell,
    /// secondary), whether it is a spell and whether it is a melee hit.</summary>
    public readonly record struct Context(string Hand, bool Spell, bool Melee = false)
    {
        public bool Attack => Hand is "main_hand" or "off_hand";
    }

    public const int BleedPercent = 70, IgnitePercent = 90, PoisonPercent = 30;

    /// <summary>A support's more multiplier that reaches ailments: the damage type the portion or the ailment needs
    /// (<see cref="DamageTypeHistory.None"/>: any), the ailments it is limited to, and whether only ailments from melee
    /// hits or attacks get it.</summary>
    readonly record struct More(string Stat, DamageTypeHistory Type = DamageTypeHistory.None, string[]? Only = null,
        bool MeleeHit = false, bool Attack = false);

    // PoB 16de4b82 sup_*.lua: the support more multipliers with no Hit, Attack, Spell, Melee, Area or Projectile flag
    // (GemSupportDamage applies those to the hit, with the generic and typed ones here).
    static readonly More[] SupportMore =
    [
        new("doedre_aura_damage_+%_final"), new("cast_on_damage_taken_damage_+%_final"),
        new("support_cast_while_channelling_triggered_skill_damage_+%_final"),
        new("support_echo_damage_+%_final"), new("support_reduced_duration_damage_+%_final"),
        new("support_spirit_strike_damage_+%_final"), new("support_storm_barrier_damage_+%_final"),
        new("support_pure_shock_damage_+%_final"), new("support_blood_thirst_damage_+%_final"),
        new("support_spell_cascade_damage_+%_final"), new("support_barrage_damage_+%_final"),
        new("support_better_ailments_ailment_damage_+%_final"), new("support_unbound_ailments_ailment_damage_+%_final"),
        new("support_efficacy_damage_over_time_+%_final"), new("support_rapid_decay_damage_over_time_+%_final"),
        new("support_brutality_physical_damage_+%_final", DamageTypeHistory.Physical),
        new("support_maim_chance_physical_damage_+%_final", DamageTypeHistory.Physical),
        new("support_void_manipulation_chaos_damage_+%_final", DamageTypeHistory.Chaos),
        new("support_bonechill_cold_damage_+%_final", DamageTypeHistory.Cold),
        new("support_chance_to_ignite_fire_damage_+%_final", DamageTypeHistory.Fire),
        new("support_gem_elemental_damage_+%_final", DamageTypeHistory.Elemental),
        new("support_attack_skills_elemental_damage_+%_final", DamageTypeHistory.Elemental, Attack: true),
        new("support_chance_to_bleed_bleeding_damage_+%_final", Only: ["bleeding"]),
        new("support_phys_chaos_projectile_physical_damage_over_time_+%_final", Only: ["bleeding"]),
        new("support_debilitate_poison_damage_+%_final", Only: ["poison"]),
        new("support_lethal_dose_poison_damage_+%_final", Only: ["poison"]),
        new("support_phys_chaos_projectile_chaos_damage_over_time_+%_final", Only: ["poison"]),
        new("support_ignite_prolif_ignite_damage_+%_final", Only: ["ignite"]),
        new("support_burning_damage_+%_final", Only: ["ignite"]),
        new("support_melee_physical_damage_poison_and_bleeding_damage_+%_final_from_melee_hits", Only: ["bleeding", "poison"],
            MeleeHit: true),
        new("support_fortify_ailment_damage_+%_final_from_melee_hits", MeleeHit: true),
    ];

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
                var (min, max) = Range(paths, ailment, sources, ailmentType, context, get, critical);
                string kind = critical ? "critical" : "non_critical";
                string name = $"{ailment}_damage_to_{(chaosOnly ? "always_" : "")}deal_per_minute_from_{kind}_{context.Hand}_damage";
                rows.Add(($"minimum_{name}", PerMinute(min * percent / 100.0 * charges)));
                rows.Add(($"maximum_{name}", PerMinute(max * percent / 100.0 * charges)));
            }
        }
        if (context.Attack) Add("bleeding", DamageType.Physical, [DamageType.Physical], BleedPercent);
        // Shaper of Flames' "All Damage can Ignite" (all_damage_can_ignite): every type's portion adds to the ignite's
        // base, with the increases of the types it visited, as poison's physical portion does.
        Add("ignite", DamageType.Fire, get("all_damage_can_ignite") != 0 ? DamageTypes.ConversionOrder : [DamageType.Fire],
            IgnitePercent);
        Add("poison", DamageType.Chaos, [DamageType.Physical, DamageType.Chaos], PoisonPercent);
        if (get("chaos_damage_chance_to_poison_%") != 0)
            Add("poison", DamageType.Chaos, [DamageType.Chaos], PoisonPercent, chaosOnly: true);
        return rows;
    }

    static int PerMinute(double perSecond) =>
        double.IsNaN(perSecond) || perSecond <= 0 ? 0 : (int)Math.Min(int.MaxValue, Math.Round(perSecond * 60));

    static (double Min, double Max) Range(List<DamageConversionPath> paths, string ailment, DamageType[] sources,
        DamageType ailmentType, Context context, Func<string, int> get, bool critical)
    {
        // The ailment deals its own type: a prohibition of that type stops it whatever its base (PoB CalcOffence gates
        // bleed on canDeal.Physical, poison on canDeal.Chaos and ignite on canDeal.Fire).
        if (get("never_damaging_ailments") != 0 || DamageStats.Prohibited(ailmentType, get, context.Attack))
            return (0, 0);
        double common = (double)get("damage_+%") + get("damage_over_time_+%") + get("base_ailment_damage_+%")
            + get(Slug(ailmentType) + "_damage_+%") + get(Slug(ailmentType) + "_damage_over_time_+%")
            + ailmentType switch
            {
                DamageType.Physical => get("bleeding_damage_+%"),
                DamageType.Fire => get("burn_damage_+%") + get("ignite_damage_+%") + get("elemental_damage_+%"),
                _ => get("base_poison_damage_+%"),
            };
        var more = SupportMore.Where(m => (m.Only is null || m.Only.Contains(ailment)) && (!m.MeleeHit || context.Melee)
            && (!m.Attack || context.Attack) && get(m.Stat) != 0).ToList();
        more.Add(new("delirium_aura_damage_over_time_+%_final"));
        if (ailment == "poison") more.Add(new("herald_of_agony_poison_damage_+%_final"));
        if (ailmentType == DamageType.Physical) more.Add(new("herald_of_purity_physical_damage_+%_final"));
        double multiplier = Math.Max(0, 1 + ((double)get("dot_multiplier_+") + get(Slug(ailmentType) + "_dot_multiplier_+")
            + get("ailment_dot_multiplier_+") + get(ailment + "_dot_multiplier_+")
            + (critical ? get("critical_ailment_dot_multiplier_+") + get("critical_" + ailment + "_dot_multiplier_+") : 0)) / 100)
            // Burning Arrow's quality: 1% more ignite damage per quality (GrantedEffectQualityStats).
            * (ailmentType == DamageType.Fire ? Math.Max(0, 1 + get("active_skill_ignite_damage_+%_final") / 100.0) : 1)
            // Crimson Dance: "50% less Damage with Bleeding" (ReminderTextKeystoneCrimsonDance).
            * (ailment == "bleeding" && get("keystone_crimson_dance") != 0 ? .5 : 1);
        var typed = DamageTypes.ConversionOrder.Select(type => (History: DamageTypes.HistoryOf(type), Increased: get(Slug(type) + "_damage_+%"))).ToArray();
        int elemental = get("elemental_damage_+%");
        double min = 0, max = 0;
        foreach (var path in paths)
        {
            if (!sources.Contains(path.Type)) continue;
            // The hit's prohibitions (PlayerHitDamage): a portion the hit cannot deal contributes no ailment base.
            if (DamageStats.Prohibited(path.Type, get, context.Attack)) continue;
            double increased = common;
            // The types the portion visited count once each; the ailment's own type is already in the common bucket.
            foreach (var source in typed)
                if ((path.History & source.History) != 0 && source.History != DamageTypes.HistoryOf(ailmentType)) increased += source.Increased;
            if ((path.History & DamageTypeHistory.Elemental) != 0 && ailmentType != DamageType.Fire) increased += elemental;
            // A typed support counts the portion's visited types and the ailment's own (PoB's typeFlags).
            var types = path.History | DamageTypes.HistoryOf(ailmentType);
            double factor = Math.Max(0, 1 + increased / 100) * multiplier * more
                .Where(m => m.Type == DamageTypeHistory.None || (types & m.Type) != 0)
                .Aggregate(1.0, (v, m) => v * Math.Max(0, 1 + get(m.Stat) / 100.0));
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
