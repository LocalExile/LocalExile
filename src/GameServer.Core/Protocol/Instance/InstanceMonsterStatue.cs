namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// The hideout's Monster Statue object: <c>Metadata/MiscellaneousObjects/Hideout/HideoutMonsterStatue</c> with the
/// <c>HideoutMonsterStatue</c> GameObjectTask (GameObjectTasks HASH16 42230). The task reads the first u8 of the
/// HideoutDoodad component as its variation: the low two bits select the MonsterVarieties actor variation
/// (0 live, 1 the stone statue, 2 dead, 3 another stance) and a value above 3 makes the doodad take the task's model
/// path. <see cref="StoneVariation"/> (5) is the stone statue with no life bar or name label; the task's row field is
/// the MonsterVarieties HASH16, not its table row index.
/// </summary>
public static class InstanceMonsterStatue
{
    public const string TypePath = "Metadata/MiscellaneousObjects/Hideout/HideoutMonsterStatue";
    public const uint MtxHash = 2905168814;
    public const ushort TaskHash = 42230;
    public const byte StoneVariation = 5;
    public const ushort CreateId = 0x0209;
    public const uint CreatorAction = 911988280;

    public static byte[] Blob(int x, int y, ushort monsterHash16, ushort scale = 100, byte variation = StoneVariation,
        float rotation = 0)
    {
        var w = new BlobWriter();
        w.U8(0).U8(0);                                                          // object flags, base data count
        w.I32(x).I32(y).F32(rotation).U16(0).U8(0).U8(0x02).U8(0).U8(1);       // Positioned
        w.U8(0);                                                                // Animated: none, the task supplies the model
        new HideoutDoodad(MtxHash, variation, 0).Write(w);                      // HideoutDoodad: the statue task's variation
        w.U8(0).U8(0);                                                          // Inventories: none, no equipped items
        w.U8(1).U16(TaskHash);                                                  // tasks: one, HideoutMonsterStatue
        w.U16(monsterHash16).U16(scale);                                        // task: MonsterVarieties HASH16, scale
        w.U8(0).U32(0);                                                         // task: no animated blob, no model override
        return w.ToArray();
    }

    public static (uint Action, uint Target) ReadCreate(ReadOnlySpan<byte> m)
    {
        if (m.Length != 10 || System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(m) != CreateId)
            throw new ArgumentException("Invalid monster statue create packet", nameof(m));
        return (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(m[2..]),
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(m[6..]));
    }
}
