using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class PassiveRequestFramingTests
{
    [Fact]
    public void Mastery_is_a_separate_six_byte_request_and_does_not_consume_the_next_message()
    {
        // Captured Block Mastery (38921), HASH16 4500, then a normal allocation and a tick in the same TCP chunk.
        byte[] mastery = Convert.FromHexString("003598091194");
        byte[] allocate = Convert.FromHexString("00340000DFB000");
        byte[] tick = Convert.FromHexString("001B00000000AA");
        var messages = InstanceProtocol.SplitClientMessages([.. mastery, .. allocate, .. tick], out var partial, out var unknown);
        Assert.Equal([InstanceEntities.MasteryEffectId, InstanceEntities.AllocatePassiveId, InstanceProtocol.TickId], messages.Select(m => m.Id));
        Assert.Equal(((ushort)38921, (ushort)4500), InstanceEntities.ReadMasteryEffect(messages[0].Bytes));
        Assert.Equal(mastery, messages[0].Bytes);
        Assert.Equal(allocate, messages[1].Bytes);
        Assert.Empty(partial);
        Assert.Empty(unknown);
        for (int length = 1; length < mastery.Length; length++)
        {
            Assert.Empty(InstanceProtocol.SplitClientMessages(mastery.AsSpan(0, length), out var waiting, out var bad));
            Assert.Equal(mastery[..length], waiting);
            Assert.Empty(bad);
        }
    }

}
