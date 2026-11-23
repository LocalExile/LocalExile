using DamageType = GameServer.Core.World.DamageType;

/// <summary>
/// The resistance penetration of the player's hits, by Stats.dat name as this client ships the rows (values are whole
/// percents):
/// - base_reduce_enemy_{fire,cold,lightning}_resistance_% — the historical penetration stats despite their name (the
///   Fire/Cold/Lightning Penetration support gems grant them, as do the Heart of Flame/Ice/Thunder passives and the
///   jewel mods);
/// - base_penetrate_elemental_resistances_% — general elemental penetration (the Inquisitor's start node, Martyr's
///   Legacy);
/// - base_attack_damage_penetrates_{fire,cold,lightning,chaos}_resist_% and
///   base_attack_damage_penetrates_elemental_resist_% — attack-only penetration (Forces of Nature, Warden);
/// - reduce_enemy_{fire,cold,lightning}_resistance_with_weapons_% and reduce_enemy_elemental_resistance_with_weapons_%
///   — "Damage with Weapons Penetrates" (Lava Lash, Fangs of Frost, Arcing Blows, the wand and staff clusters, Galvanic
///   Hammer, Piscator's Vigil), only for an attack with a weapon: PoB's ModFlag.Weapon, which an unarmed attack lacks;
/// - cold_penetration_%_vs_chilled_enemies — "against Chilled Enemies" (The Pandemonius, Blanketed Snow), kept apart in
///   <see cref="PerType.ColdVsChilled"/> because the target decides it.
/// Watcher's Eye's while-affected-by-an-aura forms reach base_reduce_enemy_*_resistance_% before this
/// (AuraCatalog.WhileAffectedPenetration).
/// Non-base elemental and chaos penetration are raw general inputs (including Penetration Support).
/// Local penetration comes only from the explicitly supplied attacking weapon, never character aliases.
/// Spells ignore weapon-local and attack-only inputs. Conditional target/critical rules remain in HitResistances.
/// Physical damage never penetrates, and "your hits cannot
/// penetrate or ignore elemental resistances" turns every elemental penetration off (chaos is not elemental).
/// </summary>
public static class Penetration
{
    public const uint ElementalProhibition = Stat.CannotPenetrateOrIgnoreElementalResistances;

    static readonly string[] Elements = ["fire", "cold", "lightning"];

    // Each hand's elemental penetration display (main_hand_attack_damage_penetrates_fire_resist_%, …).
    static readonly HashSet<uint> HandDisplays = [.. new[] { "main_hand", "off_hand" }.SelectMany(hand =>
        Elements.Select(element => Stat.Of($"{hand}_attack_damage_penetrates_{element}_resist_%")))];

    // Per element (Elements order): the general, attack-only, with-weapons and weapon-local penetration stats.
    static readonly (uint General, uint Attack, uint Weapons, uint Local)[] ElementStats = [.. Elements.Select(e =>
        (Stat.Of($"base_reduce_enemy_{e}_resistance_%"), Stat.Of($"base_attack_damage_penetrates_{e}_resist_%"),
            Stat.Of($"reduce_enemy_{e}_resistance_with_weapons_%"), Stat.Of($"local_{e}_penetration_%")))];

    public static bool IsElementalDisplay(uint stat) => stat is Stat.ReduceEnemyFireResistancePct
        or Stat.ReduceEnemyColdResistancePct or Stat.ReduceEnemyLightningResistancePct
        || HandDisplays.Contains(stat);

    /// <summary>The hit's penetration per damage type, in whole percents; <paramref name="ColdVsChilled"/> adds to cold
    /// against a chilled target.</summary>
    public readonly record struct PerType(int Fire, int Cold, int Lightning, int Chaos, int ColdVsChilled = 0)
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
        IReadOnlyDictionary<uint, int>? skill = null, IReadOnlyDictionary<uint, int>? weaponLocal = null)
    {
        long Get(uint key) => (long)stats.GetValueOrDefault(key) + (skill?.GetValueOrDefault(key) ?? 0);
        long Local(uint name) => attack ? weaponLocal?.GetValueOrDefault(name) ?? 0 : 0;
        // Every equipped weapon has its local sources (Equipment.WeaponIn); an unarmed attack has none.
        bool withWeapon = attack && weaponLocal is not null;
        static int Project(long value) => (int)Math.Clamp(value, int.MinValue, int.MaxValue);
        // "your hits cannot penetrate or ignore elemental resistances" (a flag stat): the whole elemental branch is
        // off, general and attack-only (PoB CalcOffence's CannotElePenIgnore); chaos is not elemental.
        bool elementalBlocked = stats.GetValueOrDefault(ElementalProhibition) != 0
            || (skill?.GetValueOrDefault(ElementalProhibition) ?? 0) != 0;
        long General(int element) => Get(ElementStats[element].General)
            + Get(Stat.BasePenetrateElementalResistancesPct) + Get(Stat.ReduceEnemyElementalResistancePct);
        long AttackOnly(int element) => !attack ? 0
            : Get(ElementStats[element].Attack) + Get(Stat.BaseAttackDamagePenetratesElementalResistPct);
        long WithWeapon(int element) => !withWeapon ? 0
            : Get(ElementStats[element].Weapons) + Get(Stat.ReduceEnemyElementalResistanceWithWeaponsPct);
        int Element(int element) => elementalBlocked ? 0 : Project(General(element) + AttackOnly(element)
            + WithWeapon(element) + Local(ElementStats[element].Local) + Local(Stat.LocalElementalPenetrationPct));
        return new(
            Element(0), Element(1), Element(2),
            Project(Get(Stat.ReduceEnemyChaosResistancePct) + Local(Stat.LocalChaosPenetrationPct)
                + (attack ? Get(Stat.BaseAttackDamagePenetratesChaosResistPct) : 0)),
            elementalBlocked ? 0 : Project(Get(Stat.ColdPenetrationPctVsChilledEnemies)));
    }
}
