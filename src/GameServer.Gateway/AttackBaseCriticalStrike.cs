using GameServer.Core.World;

/// <summary>
/// The attack hit's base critical strike chance before the global increased critical chance: the equipped main hand's
/// weapon base with its local mods, or a pure base stat that replaces it. Path of Building's CalcOffence.lua
/// (<see href="https://github.com/PathOfBuildingCommunity/PathOfBuilding/blob/16de4b82d57f1c0de6eb40f37143c32d4da36a02/src/Modules/CalcOffence.lua#L2003-L2012">16de4b8, lines 2003–2012</see>)
/// copies each attack weapon pass's `source.CritChance` (the weapon's base, with its local mods already in) and replaces
/// it with `skillModList:Override(skillCfg, "WeaponBaseCritChance")` — only when that hand's `source.type` is not
/// "None" — before the shared crit calculation, so the global increases still apply on top (ModParser.lua
/// <see href="https://github.com/PathOfBuildingCommunity/PathOfBuilding/blob/16de4b82d57f1c0de6eb40f37143c32d4da36a02/src/Modules/ModParser.lua#L5532">line 5532</see>
/// parses "Base Critical Strike Chance for Attacks with Weapons is X%" as that override). The tree's only weapon grant
/// is Stats row 9892 `override_weapon_base_critical_strike_chance` (client key 9893, permyriad): Slayer Overwhelm
/// (graph id 17315) grants 800 = 8.00% and the Trickster position's Blind Prophet node Sightless (graph id 24698)
/// grants 900 = 9.00%. It names the *weapon's* base, so an unarmed context keeps its own unrelated base: the Behemoth
/// position's Final Blow (graph id 10135) grants `base_melee_critical_strike_chance_while_unarmed_%`
/// (Stats row 3906, client key 3907, permyriad) 1000 = 10.00% where the WeaponTypes base is 0. Neither base is a spell
/// value: <see cref="SpellCriticalStrike"/> stays on the spell route with its own extracted bases.
/// </summary>
static class AttackBaseCriticalStrike
{
    /// <summary>Stats.dat row 9892: the armed main hand's base critical strike chance, permyriad (800 = 8.00%).</summary>
    public const string WeaponOverrideStat = "override_weapon_base_critical_strike_chance";

    /// <summary>Stats.dat row 3906: the unarmed melee attack's base critical strike chance, permyriad (1000 = 10.00%).</summary>
    public const string UnarmedBaseStat = "base_melee_critical_strike_chance_while_unarmed_%";

    // The same dependencies drive the calculation and /set_stat's pool refresh.
    static readonly (string Stat, Func<WeaponProfile, bool>? Applies)[] Increases =
    [
        ("critical_strike_chance_+%", null),
        ("attack_critical_strike_chance_+%", null),
        ("melee_critical_strike_chance_+%", w => w.Melee),
        ("one_handed_melee_critical_strike_chance_+%", w => w.OneHandMelee),
        ("two_handed_melee_critical_strike_chance_+%", w => w.TwoHandMelee),
        ("critical_strike_chance_while_dual_wielding_+%", w => w.DualWield),
        ("critical_strike_chance_while_wielding_shield_+%", w => w.Shield),
        ("axe_critical_strike_chance_+%", w => w.Axe),
        ("sword_critical_strike_chance_+%", w => w.Sword),
        ("mace_critical_strike_chance_+%", w => w.Mace),
        ("claw_critical_strike_chance_+%", w => w.Claw),
        ("dagger_critical_strike_chance_+%", w => w.Dagger),
        ("wand_critical_strike_chance_+%", w => w.Wand),
        ("bow_critical_strike_chance_+%", w => w.Bow),
        ("global_critical_strike_chance_+%_while_holding_bow", w => w.Bow),
        ("staff_critical_strike_chance_+%", w => w.Staff),
        ("global_critical_strike_chance_+%_while_holding_staff", w => w.Staff),
    ];

    /// <summary>Keep signed inputs unbounded until every applicable source, including derived overcap, is added.</summary>
    public static long IncreasedSum(WeaponProfile weapons, Func<string, long> raw)
    {
        long increased = 0;
        foreach (var (stat, applies) in Increases)
            if (applies is null || applies(weapons)) increased += raw(stat);
        return increased;
    }

    public static int IncreasedPct(WeaponProfile weapons, Func<string, int> raw) =>
        (int)Math.Clamp(IncreasedSum(weapons, name => raw(name)), int.MinValue, int.MaxValue);

    /// <summary>Stats.dat row 21564: the unarmed melee attack's critical strike multiplier, percentage points on top of
    /// the base multiplier (the final multiplier is 150 + this sum; Final Blow grants 50 = +50%). Only an empty active
    /// main hand in a melee context grants it; it is not a spell or weapon value.</summary>
    public const string UnarmedCriticalStrikeMultiplierStat = "unarmed_melee_attack_critical_strike_multiplier_+";

    /// <summary>
    /// The main hand's attack base critical strike chance (permyriad) before the global increased chance.
    /// <paramref name="weaponBase"/> is the armed weapon's base with its own local mods (WeaponProfileFor.Critical);
    /// <paramref name="raw"/> sums one stat's value from every passive, equipment and dev (/set_stat) source, where a
    /// sum of 0 or less means the stat is not present. With a weapon the override replaces that base (also when it is
    /// lower); without one the weapon base is empty, and the unarmed melee base takes its place.
    /// </summary>
    public static int OfMainHand(bool weaponInMainHand, int weaponBase, Func<string, int> raw)
    {
        if (weaponInMainHand)
        {
            int overrideChance = raw(WeaponOverrideStat);
            return overrideChance > 0 ? overrideChance : weaponBase;
        }
        return Math.Max(0, raw(UnarmedBaseStat));
    }

    public static int Chance(bool weaponInMainHand, int weaponBase, long increasedPct, Func<string, int> raw)
    {
        if (raw("global_cannot_crit") != 0 || raw("virtual_global_cannot_crit") != 0
            || raw("cannot_critical_strike_with_attacks") != 0) return 0;
        return DamageModel.CriticalStrikeChancePerMyriad(
            OfMainHand(weaponInMainHand, weaponBase, raw), increasedPct);
    }

    public static bool IsInput(string name) => name is WeaponOverrideStat or UnarmedBaseStat
        or "global_cannot_crit" or "virtual_global_cannot_crit" or "cannot_critical_strike_with_attacks"
        or "spell_critical_strike_chance_+%"
        || Increases.Any(input => input.Stat == name);

    /// <summary>The conditional pool inputs PoolsFor reads beyond the ordinary chance and override names: the unarmed
    /// melee critical multiplier, the always-hit flag and its Low Life threshold (override or ordinary). A dev
    /// /set_stat input for any of them rebuilds the pools, so the rolls and the panel see the change at once.</summary>
    public static bool IsConditionalInput(string name) => name is UnarmedCriticalStrikeMultiplierStat
        or "global_always_hit" or "low_life_threshold_%_override" or "low_life_threshold_%";
}
