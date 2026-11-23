namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// Where clients reach GameServer.Gateway: the address Auth sends in login 0x14 (the first area) and the Gateway in
/// instance 0x27 (every area change), and the port the Gateway listens on: localexile.conf Gateway.PublicAddress (default
/// 127.0.0.1, an IP address) and Gateway.Port (default 6112), or Game_GATEWAY_HOST / Game_GATEWAY_PORT (ServerConfig);
/// scripts/dev/live.ps1 gives both servers the same values, so a stack on its own ports (-PortBase) sends its clients
/// to its own Gateway.
/// </summary>
public static class GatewayEndpoint
{
    public const string DefaultHost = "127.0.0.1";
    public const ushort DefaultPort = 6112;

    public static string Host => ServerConfig.GatewayPublicAddress;

    public static ushort Port => (ushort)ServerConfig.GatewayPort;
}
