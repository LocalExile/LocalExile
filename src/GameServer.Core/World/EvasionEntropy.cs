namespace GameServer.Core.World;

/// <summary>One defender's shared attack evasion sequence. Critical confirmation is an independent random check
/// and must not advance this sequence. Times are seconds on the area's game clock.</summary>
public sealed class EvasionEntropy
{
    public int Value { get; private set; } = -1;
    public double LastAt { get; private set; } = double.NegativeInfinity;

    public bool Hits(int chance, double now, Random random)
    {
        if (Value < 0 || now - LastAt > 3.33 || now < LastAt) Value = random.Next(100);
        LastAt = now;
        Value += Math.Clamp(chance, 5, 100);
        if (Value < 100) return false;
        Value -= 100;
        return true;
    }

    /// <summary>A failed confirmation downgrades an already accepted critical attack to a normal hit.</summary>
    public static bool ConfirmsCritical(int chance, Random random) =>
        chance >= 100 || DamageModel.Hits(chance, random.Next(100));
}
