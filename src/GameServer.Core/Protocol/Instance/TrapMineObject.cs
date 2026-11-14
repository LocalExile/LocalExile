namespace GameServer.Core.Protocol.Instance;

/// <summary>The non-monster actor component order used by the client's trap and mine misc objects.</summary>
public sealed record TrapMineObject(int X, int Y, bool Mine, IReadOnlyList<GrantedSkill> Skills,
    IReadOnlyList<(uint Key, int Value)> Stats)
{
    public IReadOnlyList<GrantedSkill> Skills { get; init => field = Owned.List(value); } = Owned.List(Skills);
    public IReadOnlyList<(uint Key, int Value)> Stats { get; init => field = Owned.List(value); } = Owned.List(Stats);
    public byte[] Blob()
    {
        var w = new BlobWriter();
        w.U8(0).U8(0);
        w.I32(X).I32(Y).F32(0).U16(0).U8(0).U8(2).U8(0).U8(1);
        w.U8(0);
        w.Stats(Stats).U32(0).U32(0).U8(0);
        w.U16(0);
        Pool(w, 1); Pool(w, 0); Pool(w, 0); w.U8(0);
        w.WStr32("").U16(0).U8(0).U32(0).U32(0).U8(0x40).U8(0);
        PlayerObject.WriteSkillList(w, Skills);
        if (Mine) new StateMachineData([], 0).Write(w);
        w.U8(0);
        return w.ToArray();
    }
    static void Pool(BlobWriter w, uint current) => w.U32(current).U32(0).U16(0).U32(0).U32(0);
}
