using System.Net;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>
/// Which login peers count as "this machine". An address cannot tell a local client from a remote one when the tunnel's
/// client runs on this PC, so the set is read from Game_LOCAL_PEERS and can be empty: then every client is told the
/// endpoint it dials to reach the tunnel instead of the Gateway's loopback.
/// </summary>
public class LocalPeersTests
{
    static readonly IReadOnlySet<IPAddress> Loopback = new HashSet<IPAddress> { IPAddress.Loopback, IPAddress.IPv6Loopback };

    [Fact]
    public void The_loopback_set_matches_both_spellings_and_leaves_other_peers_remote()
    {
        Assert.True(GatewayEndpoint.IsLocalPeer("127.0.0.1:50123", Loopback));
        Assert.True(GatewayEndpoint.IsLocalPeer("[::1]:50123", Loopback));
        Assert.False(GatewayEndpoint.IsLocalPeer("192.168.1.20:50123", Loopback));
        Assert.False(GatewayEndpoint.IsLocalPeer("192.168.1.20:50123", new HashSet<IPAddress>()));
    }
}