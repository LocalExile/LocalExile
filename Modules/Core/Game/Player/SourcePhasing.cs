public static partial class Game
{
    static Dictionary<string, bool> RequalifySourcePhasing(ClientState conn,
        IReadOnlyDictionary<string, bool> captured, IReadOnlyDictionary<uint, int> sources,
        IReadOnlyDictionary<uint, int> overrides, IReadOnlySet<string> recent,
        IReadOnlySet<string> auras, double now)
    {
        int Get(string name) => Stats.Key(name) is uint key
            ? overrides.TryGetValue(key, out int replacement) ? replacement : sources.GetValueOrDefault(key) : 0;
        bool source = CurrentGameWork && !conn.Closed && !conn.LeavingArea && conn.PlayerLife > 0
            && (Get("gain_phasing_while_you_have_low_life") > 0 && captured.GetValueOrDefault("_while_on_low_life")
                || Get("gain_phasing_if_enemy_killed_recently") > 0 && recent.Contains(StatConditions.KilledEnemy)
                || Get("phasing_if_blocked_recently") > 0 && recent.Contains(StatConditions.Blocked)
                || Get("gain_phasing_while_you_have_onslaught") > 0 && captured.GetValueOrDefault("_while_have_onslaught")
                || Get("gain_phasing_while_at_maximum_frenzy_charges") > 0 && captured.GetValueOrDefault("_while_at_maximum_frenzy_charges")
                || Get("gain_phasing_while_affected_by_haste") > 0 && auras.Contains("haste"));
        bool phase = source || conn.Phasing || now < conn.PhasingUntil || conn.Reservations.CatPhasing;
        return new(captured)
        {
            ["source_phasing"] = source,
            ["_while_phasing"] = phase,
            ["_while_you_have_phasing"] = phase,
        };
    }

}
