using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// Party invite, accept and leave between two clients in one town (live 2026-10-29).
public class PartyTests
{
    [Fact]
    public void Accept_and_leave_are_framed_and_read()
    {
        // Live: Accept on the invite notice, then "Leave Party", both for party 0x101.
        byte[] accept = Convert.FromHexString("009500000101"), leave = Convert.FromHexString("009600000101");
        var both = InstanceProtocol.SplitClientMessages([.. accept, .. leave], out var partial, out var unknown);
        Assert.Empty(unknown);
        Assert.Empty(partial);
        Assert.Equal([InstanceSocial.PartyAcceptId, InstanceSocial.PartyLeaveId], both.Select(m => m.Id));
        Assert.Equal(0x101u, InstanceSocial.ReadPartyAccept(both[0].Bytes));
        Assert.Equal(0x101u, InstanceSocial.ReadPartyAccept(both[1].Bytes));
    }

}
