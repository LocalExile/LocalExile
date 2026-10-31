using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class MonsterStatueTests
{
    [Fact]
    public void BlobCarriesTheStoneVariationAndMonsterHash()
    {
        byte[] blob = InstanceMonsterStatue.Blob(10, 20, 0x1234);

        Assert.Equal(41, blob.Length);
        Assert.Equal(10, BitConverter.ToInt32(blob, 2));                       // Positioned x
        Assert.Equal(20, BitConverter.ToInt32(blob, 6));                       // Positioned y
        Assert.Equal(InstanceMonsterStatue.MtxHash, BitConverter.ToUInt32(blob, 21)); // HideoutDoodad hash
        Assert.Equal(InstanceMonsterStatue.StoneVariation, blob[25]);          // HideoutDoodad variation
        Assert.Equal(1, blob[29]);                                             // tasks: one
        Assert.Equal(InstanceMonsterStatue.TaskHash, BitConverter.ToUInt16(blob, 30));
        Assert.Equal(0x1234, BitConverter.ToUInt16(blob, 32));                 // MonsterVarieties HASH16
        Assert.Equal(100, BitConverter.ToUInt16(blob, 34));                    // scale
    }

    [Fact]
    public void ReadsTheCreatePacket()
    {
        byte[] packet = [0x02, 0x09, 0x36, 0x5B, 0xD6, 0x38, 0x00, 0x00, 0x30, 0x40];

        var (action, target) = InstanceMonsterStatue.ReadCreate(packet);

        Assert.Equal(InstanceMonsterStatue.CreatorAction, action);
        Assert.Equal(0x3040u, target);
    }
}
