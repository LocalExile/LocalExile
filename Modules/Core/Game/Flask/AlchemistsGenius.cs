public static partial class Game
{
    const ushort AlchemistsGeniusBuffRow = 1573;
    const double AlchemistsGeniusDuration = 4;
    static readonly IReadOnlyDictionary<uint, int> AlchemistsGeniusValues = new Dictionary<uint, int>
    {
        [Stat.FlaskEffectOnSelfPlusPct] = 10,
        [Stat.ChargesGainedPlusPct] = 20,
    };

    sealed record AlchemistsGeniusBuff(double Until);
    static readonly RuleTable<Player, AlchemistsGeniusBuff> AlchemistsGenius = new();

    static Func<uint, int> SelfFlaskEffectValues(Func<uint, int> character, int additional)
    {
        long onSelf = (long)additional + character(Stat.FlaskEffectOnSelfPlusPct);
        return onSelf == 0 ? character : stat => stat == Stat.FlaskEffectPlusPct
            ? ActorScale.Saturate(character(stat) + onSelf) : character(stat);
    }

    static void GrantAlchemistsGeniusLocked(Player player, double now, int chance)
    {
        if (chance <= 0 || !RandomRolls.Chance(chance, 100, AreaRng(player))) return;
        AlchemistsGenius.AddOrUpdate(player, new(now + AlchemistsGeniusDuration));
        player.Area.Buffs.Set(player.Id, BuffRows.Spec(AlchemistsGeniusBuffRow, player.Id,
            AlchemistsGeniusValues, AlchemistsGeniusDuration));
        MarkPlayerStats(player, PlayerStatStep.Sources);
        PresentIn(player, AlchemistsGeniusDuration);
        Automation(player).StatRevision++;
    }

    static void AddAlchemistsGeniusStats(Player player, Dictionary<uint, int> stats)
    {
        if (!AlchemistsGenius.TryGetValue(player, out var buff) || player.PlayerLife == 0) return;
        double remaining = buff.Until - AreaTime(player);
        if (remaining <= 0) return;
        StatsChangeIn(remaining);
        foreach (var (stat, value) in AlchemistsGeniusValues)
            stats[stat] = ActorScale.Add(stat, stats.GetValueOrDefault(stat), value);
    }

    static Task PresentAlchemistsGenius(Player player)
    {
        if (!AlchemistsGenius.TryGetValue(player, out var buff)) return Task.CompletedTask;
        double remaining = buff.Until - AreaTime(player);
        if (remaining > 0 && player.PlayerLife > 0) PresentIn(player, remaining);
        else if (AlchemistsGenius.Remove(player)) player.Area.Buffs.Clear(player.Id, AlchemistsGeniusBuffRow, player.Id);
        return Task.CompletedTask;
    }
}
