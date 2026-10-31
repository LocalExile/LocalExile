
static partial class Game
{
    internal static GamblerModifiers GamblerFor(IReadOnlyDictionary<uint, int> stats) =>
        GamblerModifiers.From(name => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0);
}
