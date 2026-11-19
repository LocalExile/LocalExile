public static partial class Game
{
    // Each charge type's flag names, made once: built per lookup they were ~20 strings an item-stat lookup.
    static readonly Dictionary<ChargeType, (string None, string YouHaveNone, string Any, string AtMaximum)> ChargeFlagNames =
        Charges.Types.ToDictionary(type => type, type => Charges.Name(type) is var name
            ? ($"_while_no_{name}_charges", $"_while_you_have_no_{name}_charges", $"_while_have_any_{name}_charges",
                $"_while_at_maximum_{name}_charges")
            : default);

    static void SetChargeConditionFlags(Dictionary<string, bool> flags, ChargeType type,
        int effective, int maximum, bool converted, bool alive)
    {
        var names = ChargeFlagNames[type];
        int standard = converted ? 0 : effective;
        flags[names.None] = flags[names.YouHaveNone] = alive && standard == 0;
        flags[names.Any] = alive && standard > 0;
        flags[names.AtMaximum] = alive && !converted && maximum >= 0 && effective >= maximum;
    }

    static Dictionary<string, bool> RequalifyChargeConditions(Player conn,
        IReadOnlyDictionary<string, bool> captured, IReadOnlyDictionary<uint, int> sources)
    {
        var flags = new Dictionary<string, bool>(captured);
        int Get(uint key) => sources.GetValueOrDefault(key);
        var state = ChargesOf(conn).State;
        foreach (var type in Charges.Types)
        {
            var limits = Charges.LimitsOf(type, Get, state.Held(type));
            SetChargeConditionFlags(flags, type, state.Effective(type, limits), limits.Maximum,
                Get(Charges.ConversionFlag(type)) != 0, conn.PlayerLife > 0);
        }
        return flags;
    }
}
