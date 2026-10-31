

/// <summary>
/// Reap's Blood Charges (issue #75, the Charges tab's Blood Charges rows). ActiveSkills <c>reap</c>: "If any survive,
/// you gain a blood charge which raises the damage and cost of the skill. Players can have 5 maximum blood charges";
/// the skill's <c>lose_blood_scythe_charge_on_kill</c> takes one per enemy its hit kills. Each charge is
/// <c>blood_scythe_damage_+%_final_per_charge</c> more damage (15%, +0.25% per quality) and
/// <c>blood_scythe_cost_+%_final_per_charge</c> more cost (20%) for the skill (pinned PoB act_str.lua: Damage and
/// LifeCost MORE × the BloodCharge multiplier). The maximum is Character.ot's <c>maximum_blood_scythe_charges</c> 5
/// (PoB Misc.lua characterConstants) plus the Reap enchantment's stat; BuffDefinitions 1822 <c>blood_scythe_charge</c>
/// (<c>CancelOnDeath</c>, <c>ShowCount</c>) shows them. No game table gives them a duration and PoB has none: they
/// stay until a kill, death or logout takes them. Vaal Reap's temporary maximum is not modelled.
/// </summary>
static class BloodCharges
{
    public const int BaseMaximum = 5;
    public const string DamagePerCharge = "blood_scythe_damage_+%_final_per_charge";
    public const string CostPerCharge = "blood_scythe_cost_+%_final_per_charge";
    public const string Maximum = "maximum_blood_scythe_charges";
    public const string Current = "current_blood_scythe_charges";
    public const string Show = "show_blood_charge_info";
    public const ushort BuffRow = 1822;

    /// <summary>Whether a skill uses Blood Charges: it has the per-charge damage stat (Reap, Vaal Reap).</summary>
    public static bool Uses(Func<string, int> own) => own(DamagePerCharge) != 0;

    /// <summary>The more multiplier of <paramref name="held"/> charges at <paramref name="perCharge"/> percent each: one
    /// multiplier of value × charges (PoB's Multiplier tag), not compounded, never below zero.</summary>
    public static double More(int perCharge, int held) => Math.Max(0, 100 + (long)perCharge * Math.Max(0, held)) / 100.0;

    /// <summary>The charges after a use of the skill: one fewer per enemy its hit killed (<paramref name="kills"/>), then
    /// one more when any hit enemy survived (<paramref name="survivors"/>), within 0 and <paramref name="maximum"/>.</summary>
    public static int Settle(int held, int kills, int survivors, int maximum)
    {
        int after = Math.Max(0, held - Math.Max(0, kills));
        if (survivors > 0) after++;
        return Math.Clamp(after, 0, Math.Max(0, maximum));
    }
}
