/// <summary>The limits an effect rule's action can be told to ignore, by name, in its "ignore" word (several with
/// commas): "Curse Enemies with a random Hex, ignoring Curse Limit" is the curse action with "ignore":"curse_limit".
/// The names are listed in the fx_limits table; an action honours the ones it knows and each new limit is a name
/// here, a row there and one test in the action that owns the limit.</summary>
public static class EffectLimits
{
    public const string CurseLimit = "curse_limit";

    /// <summary>Capacity that stands for "no limit" where a limit is a count.</summary>
    public const int Unlimited = 1000;

    public static bool Ignores(EffectRules.Fire fire, string limit) => Ignores(fire.Word("ignore"), limit);

    public static bool Ignores(string? ignore, string limit)
    {
        if (string.IsNullOrEmpty(ignore)) return false;
        foreach (var name in ignore.AsSpan().Split(','))
            if (ignore.AsSpan(name).Trim().SequenceEqual(limit)) return true;
        return false;
    }
}
