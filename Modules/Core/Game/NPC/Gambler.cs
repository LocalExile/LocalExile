
public static partial class Game
{
    public static GamblerModifiers GamblerFor(IReadOnlyDictionary<uint, int> stats) =>
        GamblerModifiers.From(name => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0);
}
