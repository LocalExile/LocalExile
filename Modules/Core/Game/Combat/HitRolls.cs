
public static partial class Game
{
    public static HitRollModifiers HitRollsFor(IReadOnlyDictionary<uint, int> stats) =>
        HitRollModifiers.From(name => name is var key ? stats.GetValueOrDefault(key) : 0);
}
