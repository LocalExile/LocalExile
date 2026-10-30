using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class SkillSupportRecordTests
{
    [Fact]
    public void Support_records_keep_the_native_reference_and_metadata_layout()
    {
        var skill = new GrantedSkill(0x103, 10, 20)
        {
            Supports = [new(30u, 31u, 5u), new(40u, 41u, 10u)]
        };
        var bytes = InstanceEntities.PlayerSkillsChange(0x1000, [skill], []);
        string hex = Convert.ToHexString(bytes);
        // LE skill id, GEPL ref, stat-set ref, flags, support count, then two native support records.
        Assert.Contains("0301000A00000000140000000002001E000000001F00000005000000002800000000290000000A000000", hex);
        Assert.Equal(bytes.Length + 28, InstanceEntities.PlayerSkillsChange(0x1000,
            [skill with { Supports = [new(30u, 31u, 5u), new(40u, 41u, 10u), new(50u, 51u), new(60u, 61u)] }], []).Length);
    }

}
