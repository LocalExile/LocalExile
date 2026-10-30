using GameServer.Core.World;

/// <summary>An ordinary bow or wand's offensive inputs frozen at release. The target supplies evasion and impales
/// at contact. This preserves the ordinary attack's existing damage/crit/impale arithmetic.</summary>
sealed record OrdinaryRangedShot(DamagePacket Hit, int Accuracy, int CriticalChance, int CriticalMultiplier, int ImpaleChance)
{
    public Penetration.PerType Penetration { get; init; }
    public HitResistances.Mods ResistanceMods { get; init; }
    public static OrdinaryRangedShot Capture(SkillCatalog.Unarmed weapon, IReadOnlyDictionary<uint, int> character,
        IEnumerable<(uint, int)> skillConversion, StatCatalog catalog, int accuracy, int criticalChance, int criticalMultiplier)
    {
        int Get(string name) => catalog.Key(name) is uint key ? character.GetValueOrDefault(key) : 0;
        var conversion = new Dictionary<uint, int>(character);
        var own = skillConversion.ToDictionary(kv => kv.Item1, kv => kv.Item2);
        foreach (var (key, value) in own) conversion.TryAdd(key, value);
        var hit = RageStats.ApplyAttack(DamageStats.Added(weapon.HitDamage(character), Get, spell: false, melee: false)
            .Convert(DamageStats.Conversion(conversion, catalog, attack: true)), Get);
        return new(hit, accuracy, criticalChance, criticalMultiplier, Get("impale_on_hit_%_chance"))
        { Penetration = global::Penetration.Of(character, catalog, attack: true, own),
            ResistanceMods = new(
                Get("critical_strikes_ignore_elemental_resistances") | Own("critical_strikes_ignore_elemental_resistances"),
                Get("attack_critical_strikes_ignore_elemental_resistances") | Own("attack_critical_strikes_ignore_elemental_resistances"),
                Get("non_critical_strikes_penetrate_elemental_resistances_%") + Own("non_critical_strikes_penetrate_elemental_resistances_%"),
                Get("cannot_penetrate_or_ignore_elemental_resistances") | Own("cannot_penetrate_or_ignore_elemental_resistances")) };

        int Own(string name) => catalog.Key(name) is uint key ? own.GetValueOrDefault(key) : 0;
    }

    public bool Hits(int evasion, Random random) =>
        DamageModel.Hits(DamageModel.AccuracyHitChance(evasion, Accuracy), random.Next(100));

    public (int Damage, bool Critical) Roll(ImpaleState impale, Random random,
        Func<DamagePacket, bool, int>? mitigate = null, int? criticalMultiplier = null,
        int extraCriticalRolls = 0, Random? criticalRandom = null, bool cannotCrit = false)
    {
        var hit = Hit;
        // Keep the existing ordinary attack convention: store the converted physical maximum before the bonus/crit.
        double physical = hit[DamageType.Physical].Max;
        double stored = impale.Bonus;
        if (stored > 0) hit = hit.Add(DamageType.Physical, new DamageRange(stored, stored));
        impale.AfterHit();
        if (ImpaleChance > 0 && random.Next(100) < ImpaleChance) impale.Apply(physical);
        var rolled = hit.Roll(random);
        bool critical = CriticalStrikePolicy.Roll(cannotCrit ? 0 : CriticalChance,
            criticalRandom ?? random, extraCriticalRolls);
        int damage = mitigate?.Invoke(rolled, critical) ?? (int)Math.Round(rolled.MinTotal);
        if (critical) damage = DamageModel.CriticalDamage(damage, criticalMultiplier ?? CriticalMultiplier);
        return (damage, critical);
    }
}
