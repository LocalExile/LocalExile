using DamageType = GameServer.Core.World.DamageType;

/// <summary>
/// The unconditional resistance penetration of the player's hits, by Stats.dat name as this client ships the rows
/// (values are whole percents):
/// - base_reduce_enemy_{fire,cold,lightning}_resistance_% — the historical penetration stats despite their name (the
///   Fire/Cold/Lightning Penetration support gems grant them, as do the Heart of Flame/Ice/Thunder passives and the
///   jewel mods);
/// - base_penetrate_elemental_resistances_% — general elemental penetration (the Inquisitor's start node, Martyr's
///   Legacy);
/// - base_attack_damage_penetrates_{fire,cold,lightning,chaos}_resist_% and
///   base_attack_damage_penetrates_elemental_resist_% — attack-only penetration (Forces of Nature, Warden).
/// Non-base elemental and chaos penetration are raw general inputs (including Penetration Support).
/// Local penetration comes only from the explicitly supplied attacking weapon, never character aliases.
/// Spells ignore weapon-local and attack-only inputs. Conditional target/critical rules remain in HitResistances.
/// Physical damage never penetrates, and "your hits cannot
/// penetrate or ignore elemental resistances" turns every elemental penetration off (chaos is not elemental).
/// </summary>
static class Penetration
{
    public const string ElementalProhibition = "cannot_penetrate_or_ignore_elemental_resistances";

    public static bool IsElementalDisplay(string stat) => stat is "reduce_enemy_fire_resistance_%"
        or "reduce_enemy_cold_resistance_%" or "reduce_enemy_lightning_resistance_%"
        || (stat.StartsWith("main_hand_", StringComparison.Ordinal) || stat.StartsWith("off_hand_", StringComparison.Ordinal))
            && (stat.EndsWith("_attack_damage_penetrates_fire_resist_%", StringComparison.Ordinal)
                || stat.EndsWith("_attack_damage_penetrates_cold_resist_%", StringComparison.Ordinal)
                || stat.EndsWith("_attack_damage_penetrates_lightning_resist_%", StringComparison.Ordinal));

    /// <summary>The hit's penetration per damage type, in whole percents.</summary>
    public readonly record struct PerType(int Fire, int Cold, int Lightning, int Chaos)
    {
        /// <summary>The penetration one type of the hit takes; physical has none.</summary>
        public int Of(DamageType type) => type switch
        {
            DamageType.Fire => Fire,
            DamageType.Cold => Cold,
            DamageType.Lightning => Lightning,
            DamageType.Chaos => Chaos,
            _ => 0,
        };
    }

    /// <summary>The penetration of one hit: the general stats on every hit, the attack-only ones added for an attack.
    /// The player's and skill's contributions to the same raw penetration stat add, without overwriting either source.</summary>
    public static PerType Of(IReadOnlyDictionary<uint, int> stats, StatCatalog catalog, bool attack,
        IReadOnlyDictionary<uint, int>? skill = null, IReadOnlyDictionary<string, int>? weaponLocal = null)
    {
        long Get(string name) => catalog.Key(name) is uint key
            ? (long)stats.GetValueOrDefault(key) + (skill?.GetValueOrDefault(key) ?? 0) : 0;
        long Local(string name) => attack ? weaponLocal?.GetValueOrDefault(name) ?? 0 : 0;
        static int Project(long value) => (int)Math.Clamp(value, int.MinValue, int.MaxValue);
        // "your hits cannot penetrate or ignore elemental resistances" (a flag stat): the whole elemental branch is
        // off, general and attack-only (PoB CalcOffence's CannotElePenIgnore); chaos is not elemental.
        bool elementalBlocked = catalog.Key(ElementalProhibition) is uint block
            && (stats.GetValueOrDefault(block) != 0 || (skill?.GetValueOrDefault(block) ?? 0) != 0);
        long General(string element) => Get($"base_reduce_enemy_{element}_resistance_%")
            + Get("base_penetrate_elemental_resistances_%") + Get("reduce_enemy_elemental_resistance_%");
        long AttackOnly(string element) => !attack ? 0
            : Get($"base_attack_damage_penetrates_{element}_resist_%")
                + Get("base_attack_damage_penetrates_elemental_resist_%");
        int Element(string element) => elementalBlocked ? 0 : Project(General(element) + AttackOnly(element)
            + Local($"local_{element}_penetration_%") + Local("local_elemental_penetration_%"));
        return new(
            Element("fire"), Element("cold"), Element("lightning"),
            Project(Get("reduce_enemy_chaos_resistance_%") + Local("local_chaos_penetration_%")
                + (attack ? Get("base_attack_damage_penetrates_chaos_resist_%") : 0)));
    }
}
