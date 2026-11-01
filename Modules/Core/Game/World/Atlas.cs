using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    /// <summary>Extra S→C messages next to the 0x3d Atlas presets, before 0x21. The Atlas module restores completed
    /// maps here so the client's HUD is not up yet and does not announce them.</summary>
    internal static Func<ClientState, IEnumerable<(string Log, byte[] Message)>>? ExtraPlayerMessages;

    // The Atlas passive tree's three presets. The client keeps the geometry and connectivity; the server owns the
    // allocation lists and the point budget (not yet earned from Atlas progress).
    internal static byte[] AtlasSlots(ClientState conn, int preset) =>
        InstanceEntities.PassiveSlots((ushort)conn.AtlasPassives[preset].Count, conn.AtlasPassivePoints,
            (byte)(1 << (2 + preset)), conn.AtlasPassives[preset]);

    internal static async Task SendAtlasSlots(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        string label)
    {
        for (int preset = 0; preset < conn.AtlasPassives.Length; preset++)
            await Send(remote, stream, ciphers, AtlasSlots(conn, preset),
                $"0x003d {label}: Atlas preset {preset}, {conn.AtlasPassives[preset].Count}/{conn.AtlasPassivePoints} points");
    }
}
