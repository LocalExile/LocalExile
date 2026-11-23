
public static partial class Game
{
    public static GamblerModifiers GamblerFor(IReadOnlyDictionary<uint, int> stats) =>
        GamblerModifiers.From(name => name is var key ? stats.GetValueOrDefault(key) : 0);
}
