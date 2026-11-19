/// <summary>
/// The standard charge maxima as the panel and the charges use them: <see cref="ChargePanelStats.Compute"/> (Character.ot's
/// 3, the maximum-charge mods, Masterful Form), then the uniques that replace a maximum (stat descriptions):
/// <list type="bullet">
/// <item>Badge of the Brotherhood's "Your Maximum Frenzy Charges is equal to your Maximum Power Charges"
/// (<c>maximum_frenzy_charges_is_equal_to_maximum_power_charges</c>); with Masterful Form the Endurance maximum follows
/// the Frenzy one it gives;</item>
/// <item>Eternal Damnation's "Maximum Endurance, Frenzy and Power Charges is 0"
/// (<c>maximum_endurance_frenzy_power_charges_is_0</c>), last, over everything.</item>
/// </list>
/// </summary>
public static class ChargeMaxima
{
    const string FrenzyEqualsPower = "maximum_frenzy_charges_is_equal_to_maximum_power_charges";
    const string AllZero = "maximum_endurance_frenzy_power_charges_is_0";

    static readonly string[] Names = [.. Charges.Types.Select(t => $"virtual_maximum_{Charges.Name(t)}_charges")];

    /// <summary>The three <c>virtual_maximum_*_charges</c> rows.</summary>
    public static List<(string Stat, int Value)> Compute(Func<string, int> get)
    {
        var rows = ChargePanelStats.Compute(get);
        if (get(FrenzyEqualsPower) == 0 && get(AllZero) == 0) return rows;
        int endurance = Value(ChargeType.Endurance), frenzy = Value(ChargeType.Frenzy), power = Value(ChargeType.Power);
        if (get(FrenzyEqualsPower) != 0)
        {
            frenzy = power;
            if (get(ChargePanelStats.EnduranceEqualsFrenzy) != 0) endurance = frenzy;
        }
        if (get(AllZero) != 0) endurance = frenzy = power = 0;
        return [(Names[0], endurance), (Names[1], frenzy), (Names[2], power)];

        int Value(ChargeType type) => Find(rows, type);
    }

    /// <summary>One type's maximum.</summary>
    public static int Of(ChargeType type, Func<string, int> get) => Find(Compute(get), type);

    // A loop, not First with a lambda: the condition flags ask on every stat lookup (performance.md, "Hot paths").
    static int Find(List<(string Stat, int Value)> rows, ChargeType type)
    {
        foreach (var (stat, value) in rows)
            if (stat == Names[(int)type]) return value;
        throw new InvalidOperationException(Names[(int)type]);
    }

    /// <summary>The type's limits with these maxima (<see cref="Charges.LimitsOf"/>).</summary>
    public static Charges.Limits LimitsOf(ChargeType type, Func<string, int> get, int held = 0) =>
        Charges.LimitsOf(type, get, held, Of(type, get));
}
