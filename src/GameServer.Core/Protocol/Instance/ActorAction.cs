namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// An animation controller's state as the client serializes it inside an action's own data (writer <c>0x141dc09a0</c>,
/// reader <c>0x141dc0bc0</c>), little-endian: u32 n + n UTF-16 units of an animated-object path (empty: the entity's own
/// model), i32 clip index (+0x190; -1 none), f32 (+0x1a4, read into the time setter <c>0x140cf0b70</c>), f32 speed
/// (+0x1b0), f32 (+0x1d8), i32 (the first queued entry's +0x10; -1 none).
/// </summary>
public readonly record struct AnimationControllerState(int Clip, float Speed = 1f)
{
    public string Path { get; init; } = "";
    public float Time { get; init; }
    public float Field1d8 { get; init; }
    public int Queued { get; init; } = -1;

    public BlobWriter Write(BlobWriter w) => w.WStr32(Path).I32(Clip).F32(Time).F32(Speed).F32(Field1d8).I32(Queued);
}

/// <summary>
/// An actor's current action as its Actor component carries it (flag f0 0x04 of slot 20, <c>0x142199c20</c>), in a
/// create or an S→C 0x2bc to the Actor: u16 skill id (the actor's skill <c>0x141ef0050</c> builds the action from), the
/// common action record (<c>0x14193e340</c>: u16 sequence → +0x11a, u32 target entity id (0 none), u32 target x → +0x130,
/// u32 target y → +0x134, u16 flags → +0x122), u8 → +0x120, then the action class's own data (vtable slot 87): here an
/// optional <see cref="AnimationControllerState"/> followed by <see cref="Payload"/>, whose meaning is the class's.
/// The skill id must be one the client's actor has (a basic action of its type is <c>ActionTypes.HASH16 &amp; 0x3fff</c>).
/// </summary>
public sealed record ActorAction(ushort SkillId, ushort Sequence)
{
    public uint TargetId { get; init; }
    public int TargetX { get; init; }
    public int TargetY { get; init; }
    public ushort Flags { get; init; }
    /// <summary>The byte after the record (action +0x120).</summary>
    public byte Extra { get; init; }
    /// <summary>The animation block that starts the class's data, when the class reads one first.</summary>
    public AnimationControllerState? Animation { get; init; }
    /// <summary>The rest of the class's data, written as is (a copy is kept).</summary>
    public byte[] Payload { get; init => field = value.ToArray(); } = [];

    /// <summary>Flag f0 bit of the Actor's slot-20 part that announces the current action.</summary>
    public const byte ActorFlag = 0x04;

    public BlobWriter Write(BlobWriter w)
    {
        w.U16(SkillId).U16(Sequence).U32(TargetId).I32(TargetX).I32(TargetY).U16(Flags).U8(Extra);
        Animation?.Write(w);
        return w.Bytes(Payload);
    }
}
