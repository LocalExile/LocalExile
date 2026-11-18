namespace GameServer.Core.Protocol.Instance;

/// <summary>Metadata/MiscellaneousObjects/Monolith; Monolith's reader is 0x1415df360.</summary>
public sealed record MonolithObject(int X, int Y, ushort Monster, IReadOnlyList<uint> Essences, byte Stage = 1)
{
    public static StateMachineData States(byte stage) =>
        new StateMachineData([0], 0, ["targetable"]).Set("targetable", stage < 4 ? 0 : 1);

    public byte[] Blob()
    {
        if (Stage is < 1 or > 4 || Essences.Count > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(Stage));
        var w = new BlobWriter();
        w.U8(0).U8(0); // base data
        w.I32(X).I32(Y).F32(0).U16(0).U8(0).U8(2).U8(0).U8(1); // Positioned
        w.U8(0).U8(0).U8(0); // Animated (own model), MinimapIcon, InteractionAction
        w.U8(Stage); // Transitionable: four states
        w.U8(Stage).U16(Monster).U8((byte)Essences.Count);
        foreach (uint essence in Essences) w.U32(essence);
        w.U32(0); // Monolith +0x104: meaning unverified; do not fabricate an entity reference.
        States(Stage).Write(w);
        return w.U8(0).ToArray();
    }
}
