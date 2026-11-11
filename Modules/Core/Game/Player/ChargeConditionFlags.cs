public static partial class Game
{
    static void SetChargeConditionFlags(Dictionary<string, bool> flags, ChargeType type,
        int effective, int maximum, bool converted, bool alive)
    {
        string name = Charges.Name(type);
        int standard = converted ? 0 : effective;
        flags[$"_while_no_{name}_charges"] = flags[$"_while_you_have_no_{name}_charges"] = alive && standard == 0;
        flags[$"_while_have_any_{name}_charges"] = alive && standard > 0;
        flags[$"_while_at_maximum_{name}_charges"] = alive && !converted && maximum > 0 && effective >= maximum;
    }

    static Dictionary<string, bool> RequalifyChargeConditions(ClientState conn,
        IReadOnlyDictionary<string, bool> captured, IReadOnlyDictionary<uint, int> sources,
        IReadOnlyDictionary<uint, int> overrides)
    {
        var flags = new Dictionary<string, bool>(captured);
        int Get(string name) => Stats.Key(name) is uint key
            ? overrides.TryGetValue(key, out int replacement) ? replacement : sources.GetValueOrDefault(key) : 0;
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
