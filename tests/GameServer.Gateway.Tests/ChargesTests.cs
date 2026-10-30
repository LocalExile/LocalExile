namespace GameServer.Gateway.Tests;

/// <summary>A Random whose Next(n) returns scripted values (each taken modulo n), so chance rolls are deterministic;
/// it fails the test when a roll is asked for that the script does not have.</summary>
sealed class ScriptedRandom(params int[] rolls) : Random
{
    private readonly Queue<int> queue = new(rolls);
    public int Asked { get; private set; }

    public override int Next(int maxValue)
    {
        Asked++;
        Assert.True(queue.Count > 0, "an unexpected roll");
        return queue.Dequeue() % maxValue;
    }

    public int Left => queue.Count;
}

public class ChargesTests
{
    static Func<string, int> Stats(params (string Name, int Value)[] stats) =>
        name => stats.Where(s => s.Name == name).Sum(s => s.Value);

    static readonly Charges.Limits Three = new(3, 0, Charges.BaseDurationMs);

    [Fact]
    public void A_character_holds_three_of_each_for_ten_seconds()
    {
        foreach (var type in Charges.Types)
            Assert.Equal(new Charges.Limits(3, 0, 10_000), Charges.LimitsOf(type, Stats()));
    }

}
