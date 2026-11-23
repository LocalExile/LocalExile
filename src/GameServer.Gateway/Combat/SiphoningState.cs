// Connection-owned values only; replaceable game rules settle and retire them in the area's frame.
public sealed class SiphoningState
{
    public object? Rules;
    public int Epoch, Held, Maximum, Gain, Presented;
    public HashSet<uint> Sources = [];
    public double Until, UsedUntil, At, PerMinute, Pending, TakenCarry, BypassCarry;
    public ushort Buff;
    public double PresentedUntil;
    public bool Degenerating, PresentedDegenerating;
    public long Revision;
}

public sealed partial class Player
{
    public readonly SiphoningState Siphoning = new();
}
