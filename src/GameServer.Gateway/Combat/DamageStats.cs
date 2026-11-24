using GameServer.Core.World;
using DamageType = GameServer.Core.World.DamageType;

/// <summary>
/// Reads the damage-conversion and gain-as-extra stats the client already has. The stat names follow Stats.dat:
/// <c>physical_damage_%_to_convert_to_fire</c>, <c>base_physical_damage_%_to_convert_to_cold</c>,
/// <c>attack_physical_damage_%_to_add_as_lightning</c>, and so on. Skill, base and global conversion stack; the
/// resulting percentages are normalised by <see cref="DamageConversion"/>.
/// </summary>
public static class DamageStats
{
    public const uint SpellDamageIncreasesApplyToAttacks150 = ArcaneMight.Fixed150;

    public static double ElementalMore(int percent) => Math.Max(0, 1 + percent / 100d);

    // Arcane Might: transfer increased/reduced spell damage into the attack's increased bucket at the strongest grant's
    // ratio (ArcaneMight: the flag, the 150% keystone, the actor/skill ratios, the wand-only and retaliation-only ones),
    // retaining half-percent values; spell-only more multipliers and speed never enter this contribution.
    public static double SpellAttackIncrease(Func<uint, int> get, bool wieldingWand = false, bool retaliation = false) =>
        ArcaneMight.Increase(get, wieldingWand, retaliation);

    public static double SpellAttackIncrease(IReadOnlyDictionary<uint, int>? stats, bool wieldingWand = false,
        bool retaliation = false) => ArcaneMight.Increase(stats, wieldingWand, retaliation);

    public static DamageRange IncreaseAttackRange(DamageRange range, double increased)
    {
        // Multiply before dividing: 200 * 101.5 / 100 is exactly 203, whereas 200 * 1.015 can fall below it
        // and the panel's final floor would disagree with the hit roll. Non-integral damage stays fractional.
        double percent = Math.Max(0, 100.0 + increased);
        return new(range.Min * percent / 100.0, range.Max * percent / 100.0);
    }

    /// <summary>The additive increase bucket for an attack damage type in its actual skill context
    /// (<paramref name="wieldingWand"/> and <paramref name="retaliation"/> select Arcane Might's conditional grants).</summary>
    public static double AttackIncrease(Func<uint, int> get, DamageType type, bool melee,
        bool area = false, bool projectile = false, bool wieldingWand = false, bool retaliation = false) =>
        (double)get(Stat.DamagePlusPct) + get(Stat.AttackDamagePlusPct) + get(Stat.Of($"{type.ToString().ToLowerInvariant()}_damage_+%"))
        + (DamageTypes.IsElemental(type) ? get(Stat.ElementalDamagePlusPct) : 0)
        + (melee ? get(Stat.MeleeDamagePlusPct) : 0)
        + (type == DamageType.Physical ? (double)get(Stat.PhysicalAttackDamagePlusPct)
            + (melee ? get(Stat.MeleePhysicalDamagePlusPct) : 0) : 0)
        // Iron Grip: Strength's physical damage bonus also applies to projectile attacks.
        + (!melee && projectile && type == DamageType.Physical && get(Stat.KeystoneStrongBowman) != 0
            ? StrengthDamageBonus(get) : 0)
        + (area ? get(Stat.AreaDamagePlusPct) : 0) + (projectile ? get(Stat.ProjectileDamagePlusPct) : 0)
        + SpellAttackIncrease(get, wieldingWand, retaliation);

    /// <summary>Strength's damage bonus as PoB's actor.strDmgBonus does it: floor(Strength / 5), 0 with
    /// gain_no_inherent_bonus_from_strength.</summary>
    public static double StrengthDamageBonus(Func<uint, int> get) =>
        get(Stat.GainNoInherentBonusFromStrength) != 0 ? 0 : get(Stat.Strength) / 5;

    /// <summary>"Attack skills have added lightning damage equal to % of maximum mana" (Stats.dat row 11881, client key
    /// 11882): PassiveSkills.dat graph id 22720, Whisperer "we know all", grants row 11881 with the value 6.</summary>
    public const uint MaxManaAddedLightning = Stat.AttackSkillsHaveAddedLightningDamageEqualToPctOfMaximumMana;

    // Raw attack-only added damage, before increases, effectiveness, support restrictions and conversion.
    // Read maximum mana, so spending or reserving the current pool cannot change the grant.
    public static double ManaAddedLightning(Func<uint, int> get) =>
        get(Stat.MaximumMana) is > 0 and var mana ? mana * (get(MaxManaAddedLightning) / 100.0) : 0;

    public static double ManaAddedPhysical(Func<uint, int> get) =>
        get(Stat.MaximumMana) is > 0 and var mana
            ? mana * (get(Stat.AddedPhysicalDamageToAttacksEqualToPctMaximumMana) / 100.0) : 0;

    // Burning Arrow of Vigour (GEPL AdditionalStats, 21–32): attack fire added from maximum life, as the mana grants above.
    public static double LifeAddedFire(Func<uint, int> get) =>
        get(Stat.MaximumLife) is > 0 and var life
            ? life * (get(Stat.AddedFireDamageToAttacksEqualToPctMaximumLife) / 100.0) : 0;

    /// <summary>Global "Adds X to Y Damage to Attacks per N Attribute": the minimum and maximum stats, the final
    /// attribute and the size of one group. The groups are read from the client's stat descriptions, not from the stat
    /// ids: the Replica Alberon's Warpath pair is named <c>_per_50_strength</c>, but its description (and PoB's and
    /// RePoE's text, "Adds 1 to 80 Chaos Damage to Attacks per 80 Strength") applies it per 80. Weapon-local "with this
    /// Weapon" coefficients have a separate consumer.</summary>
    public static readonly (DamageType Type, uint Minimum, uint Maximum, uint Attribute, int Per)[]
        AttackAddedPerAttribute =
    [
        (DamageType.Physical, Stat.AttackMinimumAddedPhysicalDamagePer25Strength,
            Stat.AttackMaximumAddedPhysicalDamagePer25Strength, Stat.Strength, 25),
        (DamageType.Physical, Stat.AttackMinimumAddedPhysicalDamagePer25Dexterity,
            Stat.AttackMaximumAddedPhysicalDamagePer25Dexterity, Stat.Dexterity, 25),
        (DamageType.Fire, Stat.MinimumAddedFireDamageToAttacksPer10Strength,
            Stat.MaximumAddedFireDamageToAttacksPer10Strength, Stat.Strength, 10),
        (DamageType.Cold, Stat.MinimumAddedColdDamageToAttacksPer10Dexterity,
            Stat.MaximumAddedColdDamageToAttacksPer10Dexterity, Stat.Dexterity, 10),
        (DamageType.Lightning, Stat.MinimumAddedLightningDamageToAttacksPer10Intelligence,
            Stat.MaximumAddedLightningDamageToAttacksPer10Intelligence, Stat.Intelligence, 10),
        (DamageType.Chaos, Stat.MinimumAddedChaosDamageToAttacksPer50Strength,
            Stat.MaximumAddedChaosDamageToAttacksPer50Strength, Stat.Strength, 80),
        (DamageType.Fire, Stat.AttackMinimumAddedFireDamagePer10Strength,
            Stat.AttackMaximumAddedFireDamagePer10Strength, Stat.Strength, 10),
        (DamageType.Lightning, Stat.AttackMinimumAddedLightningDamagePer10Int,
            Stat.AttackMaximumAddedLightningDamagePer10Int, Stat.Intelligence, 10),
        (DamageType.Physical, Stat.AttackMinimumAddedPhysicalDamagePerLevel,
            Stat.AttackMaximumAddedPhysicalDamagePerLevel, Stat.Level, 1),
    ];

    public static readonly (DamageType Type, uint Minimum, uint Maximum, uint Attribute, int Per)[]
        SpellAddedPerAttribute =
    [
        (DamageType.Lightning, Stat.SpellMinimumAddedLightningDamagePer10Intelligence,
            Stat.SpellMaximumAddedLightningDamagePer10Intelligence, Stat.Intelligence, 10),
    ];

    public static DamageRange AttributeAdded(Func<uint, int> get, DamageType type) => AttributeAdded(get, type, spell: false);

    // Raw added damage of one type from the final attributes or actor level, per complete group (PoB ModStore PerStat:
    // floor(attribute / N)); a negative attribute grants nothing. Joins the added range before increases, as above.
    public static DamageRange AttributeAdded(Func<uint, int> get, DamageType type, bool spell)
    {
        double min = 0, max = 0;
        foreach (var (rowType, minimum, maximum, attribute, per) in spell ? SpellAddedPerAttribute : AttackAddedPerAttribute)
        {
            if (rowType != type) continue;
            int low = get(minimum), high = get(maximum);
            if (low == 0 && high == 0) continue;
            long groups = Math.Max(0, get(attribute)) / per;
            min += groups * (double)low;
            max += groups * (double)high;
        }
        return new(min, max);
    }

    // The 3.29 item prohibitions of whole damage types (RePoE mods.json; PoB ModParser "deal no <type> damage",
    // "deal no non-<type> damage" through dealNoNonDamageType, "deal no non-elemental damage" and "deal no physical or
    // elemental damage"), each with the final types it removes. Skill-, critical- and condition-scoped ones are not
    // here.
    static readonly (uint Stat, DamageType[] Types)[] TypeProhibitions =
    [
        (Stat.BaseDealNoPhysicalDamage, [DamageType.Physical]),
        (Stat.BaseDealNoLightningDamage, [DamageType.Lightning]),
        (Stat.BaseDealNoColdDamage, [DamageType.Cold]),
        (Stat.BaseDealNoFireDamage, [DamageType.Fire]),
        (Stat.BaseDealNoChaosDamage, [DamageType.Chaos]),
        (Stat.DealNoElementalDamage, [DamageType.Lightning, DamageType.Cold, DamageType.Fire]),
        (Stat.DealNoElementalPhysicalDamage,
            [DamageType.Physical, DamageType.Lightning, DamageType.Cold, DamageType.Fire]),
        (Stat.DealNoNonPhysicalDamage, [DamageType.Lightning, DamageType.Cold, DamageType.Fire, DamageType.Chaos]),
        (Stat.DealNoNonLightningDamage, [DamageType.Physical, DamageType.Cold, DamageType.Fire, DamageType.Chaos]),
        (Stat.DealNoNonColdDamage, [DamageType.Physical, DamageType.Lightning, DamageType.Fire, DamageType.Chaos]),
        (Stat.DealNoNonFireDamage, [DamageType.Physical, DamageType.Lightning, DamageType.Cold, DamageType.Chaos]),
        (Stat.DealNoNonElementalDamage, [DamageType.Physical, DamageType.Chaos]),
        (Stat.DealNoNonChaosDamage, [DamageType.Physical, DamageType.Lightning, DamageType.Cold, DamageType.Fire]),
    ];

    /// <summary>Whether a portion of final damage type <paramref name="finalType"/> is prohibited: "Deal no Damage",
    /// the whole-type prohibitions above and, for an <paramref name="attack"/>, "Attacks deal no Physical Damage".
    /// Prohibitions constrain the damage dealt, not a type a converted portion used to have (PoB CalcOffence canDeal,
    /// which gates hits, each ailment on its own type, and DoT).</summary>
    public static bool Prohibited(DamageType finalType, Func<uint, int> get, bool attack = false)
    {
        if (get(Stat.BaseDealNoDamage) != 0) return true;
        if (attack && finalType == DamageType.Physical && get(Stat.AttacksDealNoPhysicalDamage) != 0) return true;
        foreach (var (stat, types) in TypeProhibitions)
            if (Array.IndexOf(types, finalType) >= 0 && get(stat) != 0) return true;
        return false;
    }

    /// <summary>Raw added damage, with effectiveness but without increased/more modifiers or conversion.</summary>
    public static DamagePacket RawAdded(DamagePacket hit, Func<uint, int> get, bool spell, double effectiveness = 1)
    {
        foreach (var type in Types)
        {
            string slug = Slug(type), prefix = spell ? "spell" : "attack";
            // Global added damage applies to attacks and spells alike (a ring's "Adds # to # Fire Damage"); the
            // attack-/spell-specific stats are extra on top, not a replacement.
            var range = new DamageRange(get(Stat.Of($"{prefix}_minimum_added_{slug}_damage")), get(Stat.Of($"{prefix}_maximum_added_{slug}_damage")))
                .Add(new DamageRange(get(Stat.Of($"global_minimum_added_{slug}_damage")), get(Stat.Of($"global_maximum_added_{slug}_damage"))));
            // The charm's "Adds X to Y Physical Damage if you've dealt a Critical Strike Recently" is a global physical
            // added range whose id keeps its condition, so StatConditions leaves it under that id.
            if (type == DamageType.Physical)
                range = range.Add(new DamageRange(get(Stat.Of("minimum_added_physical_damage")),
                    get(Stat.Of("maximum_added_physical_damage"))))
                    .Add(new DamageRange(get(Stat.MinimumAddedPhysicalDamageIfHaveCritRecently),
                        get(Stat.MaximumAddedPhysicalDamageIfHaveCritRecently)));
            range = range.Add(AttributeAdded(get, type, spell));
            if (!spell && type == DamageType.Lightning)
            {
                // PoB ModParser 16de4b82 lines 5636–5639 grants both attack LightningMin and LightningMax.
                double added = ManaAddedLightning(get);
                range = range.Add(new DamageRange(added, added));
            }
            if (!spell && type == DamageType.Physical)
            {
                double added = ManaAddedPhysical(get);
                range = range.Add(new DamageRange(added, added));
            }
            if (!spell && type == DamageType.Fire)
            {
                double added = LifeAddedFire(get);
                range = range.Add(new DamageRange(added, added));
            }
            hit = hit.Add(type, range.Scale(effectiveness));
        }
        return hit;
    }
    private static readonly DamageType[] Types =
        [DamageType.Physical, DamageType.Lightning, DamageType.Cold, DamageType.Fire, DamageType.Chaos];

    private static string Slug(DamageType type) => type switch
    {
        DamageType.Physical => "physical",
        DamageType.Lightning => "lightning",
        DamageType.Cold => "cold",
        DamageType.Fire => "fire",
        DamageType.Chaos => "chaos",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>Existing combat precedence: player keys win, then the skill fills missing conversion inputs.</summary>
    public static DamageConversion Conversion(IReadOnlyDictionary<uint, int> player, IEnumerable<(uint, int)> own,
        StatCatalog catalog, bool attack)
    {
        var stats = new Dictionary<uint, int>(player);
        foreach (var (key, value) in own) stats.TryAdd(key, value);
        return Conversion(stats, catalog, attack);
    }

    /// <summary>Conversion for a hit. <paramref name="attack"/> selects the attack-only stats; spells use the spell
    /// stats instead, and a hit that is neither (an explosion, null) only the global ones. Conditional stats (while affected
    /// by an aura, against a frozen enemy, …) are ignored.</summary>
    // Every damage type pair in conversion order with its convert and gain stats ({from}_damage_%_to_convert_to_{to},
    // base_…, skill_…, attack_/spell_…, {from}_damage_%_to_add_as_{to}, skill_…, attack_/spell_physical_…), built once.
    static readonly (DamageType From, DamageType To, uint Convert, uint BaseConvert, uint SkillConvert, uint AttackConvert,
        uint SpellConvert, uint Gain, uint SkillGain, uint AttackGain, uint SpellGain)[] ConversionPairs =
        [.. Enumerable.Range(0, Types.Length - 1).SelectMany(i => Enumerable.Range(i + 1, Types.Length - i - 1).Select(j =>
        {
            string source = Slug(Types[i]), target = Slug(Types[j]);
            return (Types[i], Types[j], Stat.Of($"{source}_damage_%_to_convert_to_{target}"),
                Stat.Of($"base_{source}_damage_%_to_convert_to_{target}"), Stat.Of($"skill_{source}_damage_%_to_convert_to_{target}"),
                Stat.Of($"attack_{source}_damage_%_to_convert_to_{target}"), Stat.Of($"spell_{source}_damage_%_to_convert_to_{target}"),
                Stat.Of($"{source}_damage_%_to_add_as_{target}"), Stat.Of($"skill_{source}_damage_%_to_add_as_{target}"),
                Stat.Of($"attack_physical_damage_%_to_add_as_{target}"), Stat.Of($"spell_physical_damage_%_to_add_as_{target}"));
        }))];

    public static DamageConversion Conversion(IReadOnlyDictionary<uint, int> stats, StatCatalog catalog, bool? attack)
    {
        var conversion = new DamageConversion();
        int Get(uint key) => stats.GetValueOrDefault(key);
        void Add(uint name, DamageType from, DamageType to, bool skill = false)
        {
            int percent = Get(name);
            if (percent > 0)
            {
                if (skill) conversion.AddSkill(from, to, percent);
                else conversion.AddGlobal(from, to, percent);
            }
        }
        void Gain(uint name, DamageType from, DamageType to)
        {
            int percent = Get(name);
            if (percent > 0) conversion.AddGain(from, to, percent);
        }

        foreach (var p in ConversionPairs)
        {
            Add(p.Convert, p.From, p.To);
            Add(p.BaseConvert, p.From, p.To);
            Add(p.SkillConvert, p.From, p.To, skill: true);
            if (attack is bool a) Add(a ? p.AttackConvert : p.SpellConvert, p.From, p.To);
            Gain(p.Gain, p.From, p.To);
            Gain(p.SkillGain, p.From, p.To);
            if (p.From == DamageType.Physical && attack is bool b) Gain(b ? p.AttackGain : p.SpellGain, p.From, p.To);
        }

        // Non-chaos to chaos is a common wording (e.g. "gain 15% of non-chaos damage as extra chaos damage").
        int nonChaos = Get(Stat.NonChaosDamageToAddAsChaosDamagePct);
        if (nonChaos > 0)
            foreach (var type in Types)
                if (type != DamageType.Chaos) conversion.AddGain(type, DamageType.Chaos, nonChaos);
        int elementalChaos = Get(Stat.ElementalDamagePctToAddAsChaos);
        if (elementalChaos > 0)
            foreach (var type in Types)
                if (DamageTypes.IsElemental(type)) conversion.AddGain(type, DamageType.Chaos, elementalChaos);

        return conversion;
    }
}
