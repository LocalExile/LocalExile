using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class SkillMessagesTests
{
    [Fact]
    public void Bind_skill_is_slot_then_big_endian_skill_id()
    {
        // Live 2026-10-18: Fireball (0x0103) put on the right mouse button.
        byte[] m = Convert.FromHexString("004E020103");
        var split = InstanceProtocol.SplitClientMessages(m, out var partial, out var unknown);
        Assert.Single(split);
        Assert.Empty(partial);
        Assert.Empty(unknown);
        Assert.Equal(((byte)2, (ushort)0x0103), InstanceProtocol.ReadBindSkill(split[0].Bytes));
    }

}
