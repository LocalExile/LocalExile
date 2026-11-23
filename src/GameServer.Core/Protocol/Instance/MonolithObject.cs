namespace GameServer.Core.Protocol.Instance;

/// <summary>Metadata/MiscellaneousObjects/Monolith; Monolith's reader is 0x1415df360.</summary>
public sealed record MonolithObject(int X, int Y, ushort Monster, IReadOnlyList<uint> Essences, byte Stage = 1,
    uint LinkedMonster = 0, bool Corrupted = false)
{
    public static StateMachineData States(byte stage) =>
        new StateMachineData([0], 0, ["targetable"]).Set("targetable", stage < 4 ? 0 : 1);

    public byte[] Blob()
    {
        if (Stage is < 1 or > 4 || Essences.Count > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(Stage));
        var w = new BlobWriter();
        w.U8(0).U8(0); // base data
        // The Positioned byte is a flag, not the .ot's object_size (that is client-side): 5 here made the client read
        // fields past the components and answer "Buffer Underflow". Keep 2, as every other object's blob does.
        w.I32(X).I32(Y).F32(0).U16(0).U8(0).U8(2).U8(0).U8(1);
        w.U8(0).U8(0).U8(0); // Animated (own model), MinimapIcon, InteractionAction
        w.U8(Stage); // Transitionable: four states
        // Monolith component (+0xf0): the .ot's is_mini_monolith is bit 0 (0 here, 1 for a mini); bit 1 is the
        // Vaal-corrupted flag (confirmed live 2026-11-20), so a corrupted main monolith sends 2 while is_mini stays 0.
        // The reader's schema lists no separate corrupted field — this bit is it.
        w.U8((byte)(Corrupted ? 2 : 0)).U16(Monster).U8((byte)Essences.Count);
        foreach (uint essence in Essences) w.U32(essence);
        // Monolith +0x104. The reader stores a free u32 here; the panel needs the imprisoned monster's name and mods,
        // so the working hypothesis (live test pending) is that the official server writes that monster's entity id.
        // Zero is a valid value: the crystal still renders without it.
        w.U32(LinkedMonster);
        States(Stage).Write(w);
        return w.U8(0).ToArray();
    }
}

/// <summary>
/// Metadata/MiscellaneousObjects/MiniMonolith: the small crystals of a monolith's imprisoned pack. Its .ot reads
/// Positioned (object_size 2), Animated (rig_small.ao), Transitionable, Monolith and BaseEvents — no MinimapIcon,
/// InteractionAction or StateMachine — and sets is_mini_monolith, the Monolith component's first u8 (the main monolith
/// sends 0 there). The same Monolith reader (0x1415df360) fills it. Transitionable has two states: 1 intact, 2 broken
/// (on_transition / create_transitioned play break and remove collision). Transitionable is component index 2.
/// </summary>
public sealed record MiniMonolithObject(int X, int Y, ushort Monster, IReadOnlyList<uint> Essences, byte Stage = 1,
    uint LinkedMonster = 0)
{
    public const string TypePath = "Metadata/MiscellaneousObjects/MiniMonolith";
    public byte[] Blob()
    {
        if (Stage is < 1 or > 2 || Essences.Count > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(Stage));
        var w = new BlobWriter();
        w.U8(0).U8(0); // base data
        w.I32(X).I32(Y).F32(0).U16(0).U8(0).U8(2).U8(0).U8(1); // Positioned
        w.U8(0); // Animated (own model)
        w.U8(Stage); // Transitionable: intact 1, broken 2
        w.U8(1).U16(Monster).U8((byte)Essences.Count); // Monolith: is_mini_monolith = true
        foreach (uint essence in Essences) w.U32(essence);
        w.U32(LinkedMonster); // Monolith +0x104: the mini's imprisoned monster
        return w.U8(0).ToArray(); // BaseEvents
    }
}