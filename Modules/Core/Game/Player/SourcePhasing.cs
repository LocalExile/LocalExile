public static partial class Game
{
    static Dictionary<string, bool> RequalifySourcePhasing(Player conn,
        IReadOnlyDictionary<string, bool> captured, IReadOnlyDictionary<uint, int> sources,
        IReadOnlySet<string> recent,
        IReadOnlySet<string> auras, double now)
    {
        captured = RequalifyChargeOnslaught(conn, captured, sources);
        int Get(uint key) => sources.GetValueOrDefault(key);
        bool source = CurrentGameWork && !conn.Closed && !conn.LeavingArea && conn.PlayerLife > 0
            && (Get(Stat.GainPhasingWhileYouHaveLowLife) > 0 && captured.GetValueOrDefault("_while_on_low_life")
                || Get(Stat.GainPhasingIfEnemyKilledRecently) > 0 && recent.Contains(StatConditions.KilledEnemy)
                || Get(Stat.PhasingIfBlockedRecently) > 0 && recent.Contains(StatConditions.Blocked)
                || Get(Stat.GainPhasingWhileYouHaveOnslaught) > 0 && captured.GetValueOrDefault("_while_have_onslaught")
                || Get(Stat.GainPhasingWhileAtMaximumFrenzyCharges) > 0 && captured.GetValueOrDefault("_while_at_maximum_frenzy_charges")
                || Get(Stat.GainPhasingWhileAffectedByHaste) > 0 && auras.Contains("haste"));
        bool phase = source || conn.Phasing || now < conn.PhasingUntil || conn.Reservations.CatPhasing;
        return new(captured)
        {
            ["source_phasing"] = source,
            ["_while_phasing"] = phase,
            ["_while_you_have_phasing"] = phase,
        };
    }

}
