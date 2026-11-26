public static partial class Game
{
    // Each charge type's flag names, made once: built per lookup they were ~20 strings an item-stat lookup.
    static readonly Dictionary<ChargeType, (string None, string YouHaveNone, string Any, string AtMaximum)> ChargeFlagNames =
        Charges.Types.ToDictionary(type => type, type => Charges.Name(type) is var name
            ? ($"_while_no_{name}_charges", $"_while_you_have_no_{name}_charges", $"_while_have_any_{name}_charges",
                $"_while_at_maximum_{name}_charges")
            : default);

    static void SetChargeConditionFlags(Dictionary<string, bool> flags, ChargeType type,
        int effective, int maximum, bool converted, bool alive, bool countsAsMaximum = false)
    {
        var names = ChargeFlagNames[type];
        int standard = converted ? 0 : effective;
        flags[names.None] = flags[names.YouHaveNone] = alive && standard == 0;
        flags[names.Any] = alive && standard > 0;
        flags[names.AtMaximum] = alive && !converted && maximum >= 0
            && (effective >= maximum || countsAsMaximum && maximum > 0);
    }

    static Dictionary<string, bool> RequalifyChargeConditions(Player player,
        IReadOnlyDictionary<string, bool> captured, IReadOnlyDictionary<uint, int> sources)
    {
        // A copy to requalify, not a read: copying a recorded map would mark every flag read.
        var flags = captured is ReadFlags recorded ? recorded.CopyUnrecorded() : new Dictionary<string, bool>(captured);
        int Get(uint key) => sources.GetValueOrDefault(key);
        var state = ChargesOf(player).State;
        Func<uint, int> get = Get;   // one delegate for the three types: every stat lookup comes here
        foreach (var type in Charges.Types)
        {
            var limits = ChargeMaxima.LimitsOf(type, get, state.Held(type));
            SetChargeConditionFlags(flags, type, state.Effective(type, limits), limits.Maximum,
                Get(Charges.ConversionFlag(type)) != 0, player.PlayerLife > 0, ChargeScaling.CountsAsMaximum(type, get));
        }
        return flags;
    }
}
