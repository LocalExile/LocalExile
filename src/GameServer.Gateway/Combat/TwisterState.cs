// Receipts retain source identities and values only, never callbacks into a replaceable Game module.
public sealed class TwisterState
{
    public sealed class Source(WornGrantedSkills.Grant grant)
    {
        public readonly WornGrantedSkills.Grant Grant = grant;
    }
    public sealed record Gain(object Owner, int Epoch, Source Source, long Serial)
    {
        public bool Consumed;
    }
    public object? Owner;
    public long Serial;
    public ushort Sequence;
    public readonly Dictionary<WornGrantedSkills.Grant, Source> Sources = [];
    public readonly Queue<Gain> Pending = new();
}

public sealed partial class ClientState
{
    public readonly TwisterState Twister = new();
}
