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
/// The main_hand_/off_hand_ and local_ weapon-hand aliases are not read: the server attacks with the main hand's
/// global stats, and a weapon-local scope guess would double-count. The non-base
/// reduce_enemy_elemental/chaos_resistance_% variants (Penetration Support's own granted stat in this client) are a
/// separate follow-up. The non-critical companion belongs to #216; aura and enemy-state conditions remain separate
/// work. Spells take the supported general stats only; this consumer reads chaos from the attack-only stat.
/// Physical damage never penetrates, and "your hits cannot
/// penetrate or ignore elemental resistances" turns every elemental penetration off (chaos is not elemental).
/// </summary>
static class Penetration
{
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
        IReadOnlyDictionary<uint, int>? skill = null)
    {
        int Get(string name) => catalog.Key(name) is uint key
            ? stats.GetValueOrDefault(key) + (skill?.GetValueOrDefault(key) ?? 0) : 0;
        // "your hits cannot penetrate or ignore elemental resistances" (a flag stat): the whole elemental branch is
        // off, general and attack-only (PoB CalcOffence's CannotElePenIgnore); chaos is not elemental.
        bool elementalBlocked = Get("cannot_penetrate_or_ignore_elemental_resistances") != 0;
        int General(string element) => elementalBlocked ? 0
            : Get($"base_reduce_enemy_{element}_resistance_%") + Get("base_penetrate_elemental_resistances_%");
        int AttackOnly(string element) => !attack || elementalBlocked ? 0
            : Get($"base_attack_damage_penetrates_{element}_resist_%")
                + Get("base_attack_damage_penetrates_elemental_resist_%");
        return new(
            General("fire") + AttackOnly("fire"),
            General("cold") + AttackOnly("cold"),
            General("lightning") + AttackOnly("lightning"),
            attack ? Get("base_attack_damage_penetrates_chaos_resist_%") : 0);
    }
}
