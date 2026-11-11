// Receipts retain source identities and values only, never callbacks into a replaceable Game module.
sealed class TwisterState
{
    internal sealed class Source(WornGrantedSkills.Grant grant)
    {
        internal readonly WornGrantedSkills.Grant Grant = grant;
    }
    internal sealed record Gain(object Owner, int Epoch, Source Source, long Serial)
    {
        internal bool Consumed;
    }
    internal object? Owner;
    internal long Serial;
    internal ushort Sequence;
    internal readonly Dictionary<WornGrantedSkills.Grant, Source> Sources = [];
    internal readonly Queue<Gain> Pending = new();
}

sealed partial class ClientState
{
    internal readonly TwisterState Twister = new();
}
