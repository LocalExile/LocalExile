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
    /// <summary>"Attack skills have added lightning damage equal to % of maximum mana" (Stats.dat row 11881, client key
    /// 11882): PassiveSkills.dat graph id 22720, Whisperer "we know all", grants row 11881 with the value 6.</summary>
    public const string MaxManaAddedLightning = "attack_skills_have_added_lightning_damage_equal_to_%_of_maximum_mana";

    public static DamagePacket Added(DamagePacket hit, Func<string, int> get, bool spell, double effectiveness = 1)
    {
        foreach (var type in Types)
        {
            string slug = Slug(type), prefix = spell ? "spell" : "attack";
            var range = new DamageRange(get($"{prefix}_minimum_added_{slug}_damage"), get($"{prefix}_maximum_added_{slug}_damage"));
            if (!spell)
            {
                if (type == DamageType.Lightning)
                {
                    int manaPercent = get(MaxManaAddedLightning);
                    if (manaPercent != 0 && get("maximum_mana") is > 0 and var maxMana)
                    {
                        // PoB ModParser 16de4b82 lines 5636–5639 maps the node's value to BOTH LightningMin and
                        // LightningMax as BASE with PercentStat Mana (the maximum, not the current pool) and ModFlag
                        // Attack. The pools' maximum_mana is already the full pool, so spending and reserving mana
                        // never change the bonus. Applied before the increases, so increased/decreased attack damage
                        // scales it like any other added lightning; later conversion treats it as lightning too.
                        double added = maxMana * (manaPercent / 100.0);
                        range = range.Add(new DamageRange(added, added));
                    }
                }

                int increased = get("damage_+%") + get("melee_damage_+%") + get($"{slug}_damage_+%")
                    + (DamageTypes.IsElemental(type) ? get("elemental_damage_+%") : 0);
                range = range.Scale(Math.Max(0, 1 + increased / 100.0));
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
