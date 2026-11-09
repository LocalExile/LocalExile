namespace GameServer.Core.Protocol.Instance;

using System.Net;
using System.Net.Sockets;

/// <summary>
/// Where clients reach GameServer.Gateway: the address Auth sends in login 0x14 (the first area) and the Gateway in
/// instance 0x27 (every area change), and the port the Gateway listens on: localexile.conf Gateway.PublicAddress (default
/// 127.0.0.1, an IP address) and Gateway.Port (default 6112), or Game_GATEWAY_HOST / Game_GATEWAY_PORT (ServerConfig);
/// scripts/dev/live.ps1 gives both servers the same values, so a stack on its own ports (-PortBase) sends its clients
/// to its own Gateway.
/// A tunnel that maps a different public port (frp and similar) sets Gateway.PublicPort (or Game_GATEWAY_PUBLIC_PORT)
/// to the public one: Auth tells clients that port while the Gateway keeps listening on Gateway.Port.
/// </summary>
public static class GatewayEndpoint
{
    public const string DefaultHost = "127.0.0.1";

    /// <summary>The value of Game_LOCAL_PEERS that counts no peer as local.</summary>
    public const string NoLocalPeers = "none";
    public const ushort DefaultPort = 6112;

    public static string Host => ServerConfig.GatewayPublicAddress;

    public static ushort Port => (ushort)ServerConfig.GatewayPort;

    /// <summary>The port written into 0x14/0x27: <see cref="Port"/> unless a tunnel maps another public one.</summary>
    public static ushort PublicPort => (ushort)ServerConfig.GatewayPublicPort;

    /// <summary>
    /// The peer addresses treated as "this machine", so that a client at one of them is told <see cref="Host"/>'s
    /// loopback and the Gateway's own port instead of the public endpoint (Game_LOCAL_PEERS, comma separated,
    /// <c>127.0.0.1</c> by default; the two loopback spellings imply each other). An address cannot tell a local client
    /// from a remote one when the tunnel's client runs on this PC — both arrive from loopback — so a host in that
    /// topology sets this to <c>none</c>: every client is then told the endpoint it dials to reach the tunnel. (An empty
    /// environment variable is not a value Windows can carry: assigning one removes it, which reads as the default.)
    /// </summary>
    public static IReadOnlySet<IPAddress> LocalPeers
    {
        get
        {
            string raw = Environment.GetEnvironmentVariable("Game_LOCAL_PEERS") ?? DefaultHost;
            if (raw.Trim().Equals(NoLocalPeers, StringComparison.OrdinalIgnoreCase)) return new HashSet<IPAddress>();
            var set = new HashSet<IPAddress>();
            foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (IPAddress.TryParse(part, out var address)) set.Add(address);
            if (set.Remove(IPAddress.Loopback)) { set.Add(IPAddress.Loopback); set.Add(IPAddress.IPv6Loopback); }
            else if (set.Remove(IPAddress.IPv6Loopback)) { set.Add(IPAddress.Loopback); set.Add(IPAddress.IPv6Loopback); }
            return set;
        }
    }

    /// <summary>Whether a login connection's remote address (<c>host:port</c> or <c>[host]:port</c>) counts as local.</summary>
    public static bool IsLocalPeer(string peer) => IsLocalPeer(peer, LocalPeers);

    /// <summary>The same decision against an explicit set, so it can be checked without reading the environment.</summary>
    public static bool IsLocalPeer(string peer, IReadOnlySet<IPAddress> localPeers)
    {
        string host = peer.StartsWith('[') ? peer[1..peer.IndexOf(']')] : peer.Split(':')[0];
        if (!IPAddress.TryParse(host, out var address)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return localPeers.Contains(address);
    }
}
