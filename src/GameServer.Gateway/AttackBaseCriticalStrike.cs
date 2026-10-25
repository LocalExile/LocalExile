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
/// (PassiveSkills row 17315) grants 800 = 8.00% and the Trickster position's Blind Prophet node Sightless (row 24698)
/// grants 900 = 9.00%. It names the *weapon's* base, so an unarmed context keeps its own unrelated base: the Behemoth
/// position's Final Blow (PassiveSkills row 10135) grants `base_melee_critical_strike_chance_while_unarmed_%`
/// (Stats row 3906, client key 3907, permyriad) 1000 = 10.00% where the WeaponTypes base is 0. Neither base is a spell
/// value: <see cref="SpellCriticalStrike"/> stays on the spell route with its own extracted bases.
/// </summary>
static class AttackBaseCriticalStrike
{
    /// <summary>Stats.dat row 9892: the armed main hand's base critical strike chance, permyriad (800 = 8.00%).</summary>
    public const string WeaponOverrideStat = "override_weapon_base_critical_strike_chance";

    /// <summary>Stats.dat row 3906: the unarmed melee attack's base critical strike chance, permyriad (1000 = 10.00%).</summary>
    public const string UnarmedBaseStat = "base_melee_critical_strike_chance_while_unarmed_%";

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
}
