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
public static class AttackBaseCriticalStrike
{
    /// <summary>Stats.dat row 9892: the armed main hand's base critical strike chance, permyriad (800 = 8.00%).</summary>
    public const uint WeaponOverrideStat = Stat.OverrideWeaponBaseCriticalStrikeChance;

    /// <summary>Stats.dat row 3906: the unarmed melee attack's base critical strike chance, permyriad (1000 = 10.00%).</summary>
    public const uint UnarmedBaseStat = Stat.BaseMeleeCriticalStrikeChanceWhileUnarmedPct;

    // The same dependencies drive the increase calculation and /set_stat's pool refresh.
    static readonly (uint Stat, Func<WeaponProfile, bool>? Applies)[] Increases =
    [
        (Stat.CriticalStrikeChancePlusPct, null),
        (Stat.AttackCriticalStrikeChancePlusPct, null),
        (Stat.MeleeCriticalStrikeChancePlusPct, w => w.Melee),
        (Stat.OneHandedMeleeCriticalStrikeChancePlusPct, w => w.OneHandMelee),
        (Stat.TwoHandedMeleeCriticalStrikeChancePlusPct, w => w.TwoHandMelee),
        (Stat.CriticalStrikeChanceWhileDualWieldingPlusPct, w => w.DualWield),
        (Stat.CriticalStrikeChanceWhileWieldingShieldPlusPct, w => w.Shield),
        (Stat.AxeCriticalStrikeChancePlusPct, w => w.Axe),
        (Stat.SwordCriticalStrikeChancePlusPct, w => w.Sword),
        (Stat.MaceCriticalStrikeChancePlusPct, w => w.Mace),
        (Stat.ClawCriticalStrikeChancePlusPct, w => w.Claw),
        (Stat.DaggerCriticalStrikeChancePlusPct, w => w.Dagger),
        (Stat.WandCriticalStrikeChancePlusPct, w => w.Wand),
        (Stat.BowCriticalStrikeChancePlusPct, w => w.Bow),
        (Stat.GlobalCriticalStrikeChancePlusPctWhileHoldingBow, w => w.Bow),
        (Stat.StaffCriticalStrikeChancePlusPct, w => w.Staff),
        (Stat.GlobalCriticalStrikeChancePlusPctWhileHoldingStaff, w => w.Staff),
    ];

    public static long IncreasedSum(WeaponProfile weapons, Func<uint, long> raw)
    {
        long increased = 0;
        foreach (var (stat, applies) in Increases)
            if (applies is null || applies(weapons)) increased += raw(stat);
        return increased;
    }

    public static int IncreasedPct(WeaponProfile weapons, Func<uint, int> raw) =>
        (int)Math.Clamp(IncreasedSum(weapons, name => raw(name)), int.MinValue, int.MaxValue);

    /// <summary>
    /// The main hand's attack base critical strike chance (permyriad) before the global increased chance.
    /// <paramref name="weaponBase"/> is the armed weapon's base with its own local mods (WeaponProfileFor.Critical);
    /// <paramref name="raw"/> sums one stat's value from every passive, equipment and dev (/set_stat) source, where a
    /// sum of 0 or less means the stat is not present. With a weapon the override replaces that base (also when it is
    /// lower); without one the weapon base is empty, and the unarmed melee base takes its place.
    /// </summary>
    public static int OfMainHand(bool weaponInMainHand, int weaponBase, Func<uint, int> raw)
    {
        if (weaponInMainHand)
        {
            int overrideChance = raw(WeaponOverrideStat);
            return overrideChance > 0 ? overrideChance : weaponBase;
        }
        return Math.Max(0, raw(UnarmedBaseStat));
    }

    public static int Chance(bool weaponInMainHand, int weaponBase, long increasedPct, Func<uint, int> raw,
        Func<uint, int>? skill = null) =>
        ChanceOfBase(OfMainHand(weaponInMainHand, weaponBase, raw), increasedPct, raw, skill);

    /// <summary>The selected attack's resolved intrinsic base, with actor and own BASE before global/own INC.</summary>
    public static int ChanceOfBase(int intrinsic, long increasedPct, Func<uint, int> raw,
        Func<uint, int>? skill = null)
    {
        int Own(uint name) => skill?.Invoke(name) ?? 0;
        if (raw(Stat.GlobalCannotCrit) != 0 || raw(Stat.VirtualGlobalCannotCrit) != 0
            || raw(Stat.CannotCriticalStrikeWithAttacks) != 0 || Own(Stat.GlobalCannotCrit) != 0
            || Own(Stat.VirtualGlobalCannotCrit) != 0 || Own(Stat.CannotCriticalStrikeWithAttacks) != 0) return 0;
        return AddedBaseCriticalStrike.Chance(intrinsic, increasedPct, spell: false, raw, skill);
    }

    public static bool IsInput(uint name) => name is WeaponOverrideStat or UnarmedBaseStat
        or Stat.AttackCriticalStrikeChancePlusPctPer200AccuracyRating
        or AddedBaseCriticalStrike.Generic or AddedBaseCriticalStrike.Attack or AddedBaseCriticalStrike.Spell
        or Stat.GlobalCannotCrit or Stat.VirtualGlobalCannotCrit or Stat.CannotCriticalStrikeWithAttacks
        or Stat.SpellCriticalStrikeChancePlusPct // Also refresh the pooled spell reference used by /crit.
        || CriticalChanceFinal.IsInput(name) || Increases.Any(input => input.Stat == name);
}
