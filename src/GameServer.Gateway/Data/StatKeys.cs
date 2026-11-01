using GameServer.Core.Protocol.Instance;

/// <summary>
/// Client stat keys (the int passed to the Stats getter 0x1424cfa40), read from client code. They are NOT the rows of
/// our extracted data/tables/Stats.json: the order drifts (key 245 is row 244 there, other keys are further off).
/// </summary>
static class StatKeys
{
    /// <summary>monster_life_+%_final_from_rarity (Stats.dat row 2895): the rarity's extra life (MonsterCatalog.RarityLifePct).</summary>
    public const uint MonsterLifeFromRarityPct = 0xb50;
    /// <summary>maximum_life (Stats.dat row 246): the final maximum the life bars and orbs read.</summary>
    public const uint MaximumLife = 0xf7;
    /// <summary>movement_velocity_+%: walk speed = base × (1 + value / 100), at least −100 (0x141e7c0b0).</summary>
    public const uint MovementVelocityPct = 0xf5;
    /// <summary>movement_velocity_cap (0x141e7c0b0 caps the speed with it when positive).</summary>
    public const uint MovementVelocityCap = 0x44f;
    // Main-hand attack stats (client key = Stats.dat row + 1, checked against the keys the client reads in an attack).
    public const uint MainHandMinPhysical = 0x117, MainHandMaxPhysical = 0x118;
    public const uint MainHandMinTotal = 0x29e, MainHandMaxTotal = 0x29f;
    public const uint MainHandAttackDurationMs = 0x142;          // main_hand_base_weapon_attack_duration_ms
    public const uint MainHandTotalAttackDurationMs = 0x221a;    // main_hand_total_base_weapon_attack_duration_ms
    public const uint HundredTimesAttacksPerSecond = 0x2a6;
    public const uint MainHandBaseMaxAttackDistance = 0x146, MainHandMaxAttackDistance = 0x4d3;
    // Off-hand attack stats, the keys next to the main-hand ones in Stats.dat (off_hand_minimum_physical_damage 0x119 and
    // so on). A dual-wielded attack's off-hand strike is set up from them.
    public const uint OffHandMinPhysical = 0x119, OffHandMaxPhysical = 0x11a;
    public const uint OffHandMinTotal = 0x2a0, OffHandMaxTotal = 0x2a1;
    public const uint OffHandAttackDurationMs = 0x143;          // off_hand_base_weapon_attack_duration_ms
    public const uint OffHandTotalAttackDurationMs = 0x221b;    // off_hand_total_base_weapon_attack_duration_ms
    public const uint OffHandAttackSpeedPct = 0x127;
    public const uint OffHandBaseMaxAttackDistance = 0x147, OffHandMaxAttackDistance = 0x4d4;
    /// <summary>hide_mini_life_bar: non-zero hides the object's overhead life bar (listener 0x140220c30).</summary>
    public const uint HideMiniLifeBar = 0x2a8b;
    /// <summary>skill_is_non_pathing: the action starts where the actor stands instead of walking into range first.</summary>
    public const uint SkillIsNonPathing = 0x4f32;
    // "Increased attack speed" stats that apply to an unarmed melee attack (attack time = base / (1 + sum / 100)).
    public const uint AttackSpeedPct = 0x47, MeleeAttackSpeedPct = 0x49, MainHandAttackSpeedPct = 0x126,
        UnarmedMeleeAttackSpeedPct = 0xb19;
    /// <summary>The raw attack-speed inputs /set_stat can add; the passives' conditional ones are summed into
    /// <see cref="CharacterStats.Pools.AttackSpeedPct"/> by PassiveBonus.</summary>
    public static readonly uint[] AttackSpeedInputs = [AttackSpeedPct, MeleeAttackSpeedPct, MainHandAttackSpeedPct, UnarmedMeleeAttackSpeedPct];
    /// <summary>The character panel's Damage per Second and Damage per Use rows (values are hundredths).</summary>
    public const uint HundredTimesDamagePerSecond = 0x2a7, DisplayHundredTimesDamagePerSkillUse = 0x7fc;
    /// <summary>The main-hand flag the character panel checks before drawing the MH_* damage rows.</summary>
    public const uint SkillUsesMainHand = 0x1e70;
    /// <summary>main_hand_weapon_type (Stats.dat row 2): the client's native weapon enum. A bow is 9 - its ItemClasses
    /// row + 1 is 14 and its model stance is another enum again (13-items.md "Client quiver eligibility").</summary>
    public const uint MainHandWeaponType = 0x3;
    // "Increased damage" stats that apply to an unarmed melee attack's physical damage (damage × (1 + sum / 100)):
    // damage_+%, physical_damage_+%, melee_damage_+%, melee_physical_damage_+% (strength's 1% per 5).
    public const uint DamagePct = 0x19, PhysicalDamagePct = 0x1b, MeleeDamagePct = 0x558, MeleePhysicalDamagePct = 0x1ed;
    // 3.29.3b Stats.dat rows 25 and 11810; Whisperer node 50932 enables the fixed 150% transfer.
    public const uint SpellDamagePct = 26, SpellDamageIncreasesApplyToAttacks150 = 11811;
    public const uint SpellDamageIncreasesApplyToAttacks = 2493, SpellDamageIncreasesApplyToAttacksRatio = 11810,
        SpellDamageIncreasesApplyToAttacksWithWand = 11812, SpellDamageIncreasesApplyToRetaliation = 11813,
        SkillSpellDamageIncreasesApplyToAttacks = 11814;
    public const uint FireDamagePct = 43, ColdDamagePct = 50, LightningDamagePct = 57,
        ChaosDamagePct = 64, ElementalDamagePct = 489;
    // 3.29.3b Stats.dat row 5340; Rampager node 49937 supplies 1 on the caster.
    public const uint SpellSkillsDealNoDamage = 5341;
    // 3.29.3b Stats.dat row 2654: generic increased attack damage, including final-armour-derived contributions.
    public const uint AttackDamagePct = 2655;
}
