namespace GameServer.Core.Protocol.Instance;

/// <summary>The component order of Metadata/MiscellaneousObjects/TemplarRingOfIceStatue.</summary>
public static class FrozenLegionStatueObject
{
    public const string TypePath = "Metadata/MiscellaneousObjects/TemplarRingOfIceStatue";
    public const byte ActorComponentIndex = 7;

    // The statue rig's "begin" and "end" events delimit the attack's contact window.
    public const float SweepContactWindow = .52406198f;

    public static byte[] Blob(int x, int y, float rotation, GrantedSkill sweep,
        IReadOnlyList<VisibleItem> equipment, IReadOnlyList<(uint Key, int Value)>? stats = null)
    {
        var writer = new BlobWriter();
        writer.U8(0).U8(0); // Object flags and base data count.
        writer.Positioned(x, y, rotation, blocking: false, blocksNoWalker: true);
        writer.U8(0); // Animated uses the native statue rig. Pathfinding writes nothing.
        writer.Stats(stats).U32(0).U32(0).U8(0);
        writer.U16(0); // Buffs.
        writer.U32(1).Zeros(51); // Life, mana, energy shield and life flags.
        PlayerObject.WriteInventories(writer, equipment);
        writer.WStr32("").U16(0).U8(0).U32(0).U32(0).U8(0x40).U8(0);
        PlayerObject.WriteSkillList(writer, [sweep]);
        // DiesAfterTime and PlayerClass have no creation fields.
        return writer.U8(0).ToArray(); // Tasks.
    }

    /// <summary>RingOfIceSweep's action snapshot (3.29.3c reader 0x141c14860).</summary>
    public static ActorAction Action(ushort skill, ushort sequence, int x, int y, bool sweeping, float speed = 1)
    {
        byte[] payload = sweeping
            ? new BlobWriter().U8(0).U8(1).U8(0).F32(SweepContactWindow / speed).ToArray()
            : [1, 0];
        return new(skill, sequence)
        {
            TargetX = x, TargetY = y,
            Animation = new AnimationControllerState(sweeping ? 1 : 0, speed),
            Payload = payload,
        };
    }
}
