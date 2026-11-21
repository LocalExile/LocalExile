public static partial class Game
{
    static IReadOnlyDictionary<string, bool> RequalifySourcePhasing(Player player,
        IReadOnlyDictionary<string, bool> captured, IReadOnlyDictionary<uint, int> sources,
        IReadOnlySet<string> recent,
        IReadOnlySet<string> auras, double now)
    {
        captured = RequalifyChargeOnslaught(player, captured, sources);
        int Get(uint key) => sources.GetValueOrDefault(key);
        bool source = CurrentGameWork && !player.Closed && !player.LeavingArea && player.PlayerLife > 0
            && (Get(Stat.GainPhasingWhileYouHaveLowLife) > 0 && captured.GetValueOrDefault("_while_on_low_life")
                || Get(Stat.GainPhasingIfEnemyKilledRecently) > 0 && recent.Contains(StatConditions.KilledEnemy)
                || Get(Stat.PhasingIfBlockedRecently) > 0 && recent.Contains(StatConditions.Blocked)
                || Get(Stat.GainPhasingWhileYouHaveOnslaught) > 0 && captured.GetValueOrDefault("_while_have_onslaught")
                || Get(Stat.GainPhasingWhileAtMaximumFrenzyCharges) > 0 && captured.GetValueOrDefault("_while_at_maximum_frenzy_charges")
                || Get(Stat.GainPhasingWhileAffectedByHaste) > 0 && auras.Contains("haste"));
        bool phase = source || player.Phasing || now < player.PhasingUntil || player.Reservations.CatPhasing;
        return WithFlags(captured, ("source_phasing", source), ("_while_phasing", phase), ("_while_you_have_phasing", phase));
    }

}
