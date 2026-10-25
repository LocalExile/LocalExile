/// <summary>
/// The Charges tab's three standard maximum rows (issue #75): Endurance, Frenzy and Power Charges. The client reads
/// <c>virtual_maximum_*_charges</c> and derives none of it itself (11-skills.md A4), so the server works the maximum out
/// from Metadata/Characters/Character.ot's base 3 plus unconditional maximum-charge bonuses from passives and items.
/// The per-type bonuses add to the base, as BASE mods in Path of Building's charge calculation do. Their Stats.dat
/// Semantics 2 means OVERRIDE_VALUE; it does not establish that addition rule. The combined power/endurance and
/// power/frenzy bonuses add to both rows; the former is reachable through the ordinary Warstaff base implicit.
/// Conditional stats (while affected by ...), conversions (maximum_..._is_equal_to_...) and the mechanics that replace
/// the maximum with 0 are outside this contribution and are not counted. Current charges, charge generation and
/// consumption, and per-charge effects are not here either. Conversion/zero flags currently require unsupported
/// unique or ascendancy sources; revisit them when those sources become reachable. No Mods.dat row uses the
/// max_*_charges_override stats today; if supported later, those must replace the sum rather than add to it.
/// </summary>
static class ChargePanelStats
{
    /// <summary>Metadata/Characters/Character.ot's <c>max_endurance_charges</c>, <c>max_frenzy_charges</c> and
    /// <c>max_power_charges</c>: every character starts with three of each.</summary>
    public const int BaseMaximumCharges = 3;

    /// <summary>The three panel rows, from the stats as the server holds them (<paramref name="get"/>: by Stats.dat Id,
    /// 0 when absent). Reductions that would take a maximum below zero show zero instead.</summary>
    public static List<(string Stat, int Value)> Compute(Func<string, int> get)
    {
        int powerAndEndurance = get("maximum_power_and_endurance_charges_+");
        int powerAndFrenzy = get("maximum_power_and_frenzy_charges_+");
        static int Maximum(long value) => (int)Math.Clamp(value, 0, int.MaxValue);
        return
        [
            ("virtual_maximum_endurance_charges",
                Maximum((long)BaseMaximumCharges + get("max_endurance_charges") + powerAndEndurance)),
            ("virtual_maximum_frenzy_charges",
                Maximum((long)BaseMaximumCharges + get("max_frenzy_charges") + powerAndFrenzy)),
            ("virtual_maximum_power_charges",
                Maximum((long)BaseMaximumCharges + get("max_power_charges") + powerAndEndurance + powerAndFrenzy)),
        ];
    }
}
