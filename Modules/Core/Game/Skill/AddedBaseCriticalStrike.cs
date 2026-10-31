

/// <summary>Ordinary added base critical chance, in permyriad. Actor and selected-skill BASE sources join the
/// completed intrinsic base before INC (pinned PoB CalcOffence.lua3102-3107). Published final chances are not inputs.</summary>
static class AddedBaseCriticalStrike
{
    public const string Generic = "additional_base_critical_strike_chance";
    public const string Attack = "attack_additional_critical_strike_chance_permyriad";
    public const string Spell = "spell_additional_critical_strike_chance_permyriad";

    public static int Chance(int intrinsic, long increasedPct, bool spell, Func<string, int> player,
        Func<string, int>? skill = null)
    {
        string typed = spell ? Spell : Attack;
        long basis = (long)intrinsic + player(Generic) + player(typed)
            + (skill?.Invoke(Generic) ?? 0) + (skill?.Invoke(typed) ?? 0);
        return CriticalChanceFinal.Project(basis, increasedPct, player, skill);
    }

    /// <summary>PoB Item.lua2325: native weapon base plus local BASE, then local INC, rounded to permyriad.
    /// Actor/skill additions and global INC are applied later. This step has no global critical-chance cap.</summary>
    public static int LocalWeaponChance(int intrinsic, int localBase, int localIncrease) =>
        (int)Math.Clamp(decimal.Round(((long)intrinsic + localBase) * (100m + localIncrease) / 100m,
            0, MidpointRounding.AwayFromZero), int.MinValue, int.MaxValue);
}
