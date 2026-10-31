/// <summary>Continuous recovery in whole resource points, preserving the fraction between updates.</summary>
static class RecoveryPool
{
    public static uint Advance(uint current, uint maximum, double amount, ref double carry, uint minimum = 0)
    {
        // Life regeneration and flasks never resurrect. Mana can recover from zero.
        if (current == 0 && minimum > 0) { carry = 0; return 0; }
        minimum = Math.Min(minimum, maximum);
        double change = carry + amount;
        double whole = Math.Truncate(change);
        carry = change - whole;
        double target = current + whole;
        uint after = (uint)Math.Clamp(target, minimum, maximum);
        // Recovery beyond a full pool (or RF below its nonlethal floor) cannot be banked for a later hit.
        if (after != target || (after == maximum && carry > 0) || (after == minimum && carry < 0)) carry = 0;
        return after;
    }
}
