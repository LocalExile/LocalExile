using GameServer.Core.World;
using DamageType = GameServer.Core.World.DamageType;

/// <summary>
/// Reads the damage-conversion and gain-as-extra stats the client already has. The stat names follow Stats.dat:
/// <c>physical_damage_%_to_convert_to_fire</c>, <c>base_physical_damage_%_to_convert_to_cold</c>,
/// <c>attack_physical_damage_%_to_add_as_lightning</c>, and so on. Skill, base and global conversion stack; the
/// resulting percentages are normalised by <see cref="DamageConversion"/>.
/// </summary>
static class DamageStats
{
    public const string SpellDamageIncreasesApplyToAttacks150 =
        "additive_spell_damage_modifiers_apply_to_attack_damage_at_150%_value";

    // This stat is a flag. Transfer increased/reduced spell damage into the attack's increased bucket,
    // retaining half-percent values; spell-only more multipliers and speed never enter this contribution.
    public static double SpellAttackIncrease(Func<string, int> get) =>
        get("spell_damage_+%") * Math.Max(get(SpellDamageIncreasesApplyToAttacks150) != 0 ? 150 : 0,
            get("active_skill_additive_spell_damage_modifiers_apply_to_attack_damage_at_%_value")) / 100.0;

    public static double SpellAttackIncrease(IReadOnlyDictionary<uint, int>? stats) =>
        SpellAttackIncrease(stats?.GetValueOrDefault(StatKeys.SpellDamageIncreasesApplyToAttacks150) ?? 0,
            stats?.GetValueOrDefault(StatKeys.SpellDamagePct) ?? 0);

    static double SpellAttackIncrease(int enabled, int increased) => enabled != 0 ? increased * 1.5 : 0;

    public static DamageRange IncreaseAttackRange(DamageRange range, double increased)
    {
        // Multiply before dividing: 200 * 101.5 / 100 is exactly 203, whereas 200 * 1.015 can fall below it
        // and the panel's final floor would disagree with the hit roll. Non-integral damage stays fractional.
        double percent = Math.Max(0, 100.0 + increased);
        return new(range.Min * percent / 100.0, range.Max * percent / 100.0);
    }

    /// <summary>"Attack skills have added lightning damage equal to % of maximum mana" (Stats.dat row 11881, client key
    /// 11882): PassiveSkills.dat graph id 22720, Whisperer "we know all", grants row 11881 with the value 6.</summary>
    public const string MaxManaAddedLightning = "attack_skills_have_added_lightning_damage_equal_to_%_of_maximum_mana";

    // Raw attack-only added damage, before increases, effectiveness, support restrictions and conversion.
    // Read maximum mana, so spending or reserving the current pool cannot change the grant.
    public static double ManaAddedLightning(Func<string, int> get) =>
        get("maximum_mana") is > 0 and var mana ? mana * (get(MaxManaAddedLightning) / 100.0) : 0;

    public static double ManaAddedPhysical(Func<string, int> get) =>
        get("maximum_mana") is > 0 and var mana
            ? mana * (get("added_physical_damage_to_attacks_equal_to_%_maximum_mana") / 100.0) : 0;

    public static DamagePacket Added(DamagePacket hit, Func<string, int> get, bool spell, bool melee,
        double effectiveness = 1)
    {
        foreach (var type in Types)
        {
            string slug = Slug(type), prefix = spell ? "spell" : "attack";
            var range = new DamageRange(get($"{prefix}_minimum_added_{slug}_damage"), get($"{prefix}_maximum_added_{slug}_damage"));
            if (!spell)
            {
                if (type == DamageType.Physical)
                {
                    double added = ManaAddedPhysical(get);
                    range = range.Add(new DamageRange(added, added));
                }
                if (type == DamageType.Lightning)
                {
                    // PoB ModParser 16de4b82 lines 5636–5639 grants both attack LightningMin and LightningMax.
                    double added = ManaAddedLightning(get);
                    range = range.Add(new DamageRange(added, added));
                }

                // attack_damage_+% is a plain Attack-flag increase: every attack, no inference from the weapon or the
                // range. melee_damage_+% is a melee-only one: a bow's or wand's attack must not take it,
                // so the caller passes whether this swing is actually melee: the default attack and the panel's
                // main-hand rows by their weapon profile (an inactive weapon is not there; unarmed is melee) and an
                // attack gem by its own ActiveSkillType Melee classification.
                double increased = (double)get("damage_+%") + get("attack_damage_+%") + (melee ? get("melee_damage_+%") : 0)
                    + get($"{slug}_damage_+%")
                    + (DamageTypes.IsElemental(type) ? get("elemental_damage_+%") : 0)
                    + SpellAttackIncrease(get);
                range = IncreaseAttackRange(range, increased);
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
    /// stats instead. Conditional stats (while affected by an aura, against a frozen enemy, …) are ignored.</summary>
    public static DamageConversion Conversion(IReadOnlyDictionary<uint, int> stats, StatCatalog catalog, bool attack)
    {
        var conversion = new DamageConversion();
        int Get(string name) => catalog.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        void Add(string name, DamageType from, DamageType to, bool skill = false)
        {
            int percent = Get(name);
            if (percent > 0)
            {
                if (skill) conversion.AddSkill(from, to, percent);
                else conversion.AddGlobal(from, to, percent);
            }
        }
        void Gain(string name, DamageType from, DamageType to)
        {
            int percent = Get(name);
            if (percent > 0) conversion.AddGain(from, to, percent);
        }

        for (int fromIndex = 0; fromIndex < Types.Length - 1; fromIndex++)
        {
            var from = Types[fromIndex];
            string source = Slug(from);
            for (int toIndex = fromIndex + 1; toIndex < Types.Length; toIndex++)
            {
                var to = Types[toIndex];
                string target = Slug(to);
                Add($"{source}_damage_%_to_convert_to_{target}", from, to);
                Add($"base_{source}_damage_%_to_convert_to_{target}", from, to);
                Add($"skill_{source}_damage_%_to_convert_to_{target}", from, to, skill: true);
                Add($"{(attack ? "attack" : "spell")}_{source}_damage_%_to_convert_to_{target}", from, to);
                Gain($"{source}_damage_%_to_add_as_{target}", from, to);
                Gain($"skill_{source}_damage_%_to_add_as_{target}", from, to);
                if (from == DamageType.Physical)
                    Gain($"{(attack ? "attack" : "spell")}_physical_damage_%_to_add_as_{target}", from, to);
            }
        }

        // Non-chaos to chaos is a common wording (e.g. "gain 15% of non-chaos damage as extra chaos damage").
        int nonChaos = Get("non_chaos_damage_to_add_as_chaos_damage_%");
        if (nonChaos > 0)
            foreach (var type in Types)
                if (type != DamageType.Chaos) conversion.AddGain(type, DamageType.Chaos, nonChaos);
        int elementalChaos = Get("elemental_damage_%_to_add_as_chaos");
        if (elementalChaos > 0)
            foreach (var type in Types)
                if (DamageTypes.IsElemental(type)) conversion.AddGain(type, DamageType.Chaos, elementalChaos);

        return conversion;
    }
}
