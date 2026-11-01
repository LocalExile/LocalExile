/// <summary>
/// The Charges tab's three standard maximum rows (issue #75): Endurance, Frenzy and Power Charges. The client reads
/// <c>virtual_maximum_*_charges</c> and derives none of it itself (11-skills.md A4), so the server works the maximum out
/// from Metadata/Characters/Character.ot's base 3 plus unconditional maximum-charge bonuses from passives and items.
/// The per-type bonuses add to the base, as BASE mods in Path of Building's charge calculation do, and so does
/// <c>maximum_frenzy_power_endurance_charges</c> ("+1 to Maximum Endurance, Frenzy and Power Charges") to all three. Their Stats.dat
/// Semantics 2 means OVERRIDE_VALUE; it does not establish that addition rule. The combined power/endurance and
/// power/frenzy bonuses add to both rows; the former is reachable through the ordinary Warstaff base implicit.
/// Conditional stats (while affected by ...) and the mechanics that replace the maximum with 0 are outside the standard
/// contribution. The three explicitly sourced special-charge maximum mirrors are a separate, later projection
/// (<see cref="MirroredMaximums"/>); the special counts come from the converted standard charges
/// (<see cref="Charges.ConversionFlag"/>, <see cref="ChargeEffects.Display"/>). The charges themselves (current counts,
/// gains, expiry, per-charge effects) are <see cref="Charges"/>, which takes its standard maxima from here. No Mods.dat
/// row uses the max_*_charges_override stats today; if supported later, those must replace the sum rather than add to it.
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
        int all = get("maximum_frenzy_power_endurance_charges");
        static int Maximum(long value) => (int)Math.Clamp(value, 0, int.MaxValue);
        return
        [
            ("virtual_maximum_endurance_charges",
                Maximum((long)BaseMaximumCharges + get("max_endurance_charges") + powerAndEndurance + all)),
            ("virtual_maximum_frenzy_charges",
                Maximum((long)BaseMaximumCharges + get("max_frenzy_charges") + powerAndFrenzy + all)),
            ("virtual_maximum_power_charges",
                Maximum((long)BaseMaximumCharges + get("max_power_charges") + powerAndEndurance + powerAndFrenzy + all)),
        ];
    }

    /// <summary>Only the native maximum-equals flags, after the caller has completed the standard maxima and applied
    /// their explicit replacements. Pinned PoB CalcPerform mirrors Endurance/Frenzy/Power into Brutal/Affliction/
    /// Absorption respectively, or zero without the flag. Always emit zero to clear a removed source's native gates.
    /// No direct special maximum, minimum conversion, gain-instead flag or current charge state is inferred.</summary>
    public static List<(string Stat, int Value)> MirroredMaximums(Func<string, int> completed)
    {
        return
        [
            ("virtual_maximum_brutal_charges", Mirror("maximum_brutal_charges_is_equal_to_maximum_endurance_charges", "endurance")),
            ("virtual_maximum_affliction_charges", Mirror("maximum_affliction_charges_is_equal_to_maximum_frenzy_charges", "frenzy")),
            ("virtual_maximum_absorption_charges", Mirror("maximum_absorption_charges_is_equal_to_maximum_power_charges", "power")),
        ];
        int Mirror(string flag, string standard) => completed(flag) != 0
            ? Math.Max(0, completed($"virtual_maximum_{standard}_charges")) : 0;
    }
}
