using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    /// <summary>Extra S→C messages next to the 0x3d Atlas presets, before 0x21. The Atlas module restores completed
    /// maps here so the client's HUD is not up yet and does not announce them.</summary>
    public static Func<ClientState, IEnumerable<(string Log, byte[] Message)>>? ExtraPlayerMessages;

    // The Atlas passive tree's three presets. The client keeps the geometry and connectivity; the server owns the
    // allocation lists and the point budget (not yet earned from Atlas progress).
    public static byte[] AtlasSlots(ClientState conn, int preset) =>
        InstanceEntities.PassiveSlots((ushort)conn.AtlasPassives[preset].Count, conn.AtlasPassivePoints,
            (byte)(1 << (2 + preset)), conn.AtlasPassives[preset]);

    public static async Task SendAtlasSlots(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        string label)
    {
        for (int preset = 0; preset < conn.AtlasPassives.Length; preset++)
            await Send(remote, stream, ciphers, AtlasSlots(conn, preset),
                $"0x003d {label}: Atlas preset {preset}, {conn.AtlasPassives[preset].Count}/{conn.AtlasPassivePoints} points");
    }

    /// <summary>The allocated Atlas passives' stats of the active preset, by stat name: what the tree adds to a map's.</summary>
    public static Dictionary<string, int> AtlasStats(ClientState conn)
    {
        Dictionary<string, int> stats = [];
        int preset = conn.AtlasPreset is >= 0 and < 3 ? conn.AtlasPreset
            : Enumerable.Range(0, conn.AtlasPassives.Length).MaxBy(p => conn.AtlasPassives[p].Count);
        foreach (ushort graphId in conn.AtlasPassives[preset])
            if (AtlasPassiveNodes.Find(graphId) is { } node)
                foreach (var (key, value) in node.Stats)
                {
                    string name = Stats.Name(key);
                    stats[name] = stats.GetValueOrDefault(name) + value;
                }
        return stats;
    }

    /// <summary>The stats of the area the player is in that league mechanics read: a map's own (its affixes, scarabs and
    /// fragments) and, in a map, the Atlas passives'. Empty outside a map.</summary>
    public static Dictionary<string, int> AreaLeagueStats(ClientState conn)
    {
        Dictionary<string, int> stats = [];
        if (MapModsOf(conn) is { } mods)
            foreach (var (name, value) in mods.Stats) stats[name] = stats.GetValueOrDefault(name) + value;
        if (MapDevices.IsMapArea(conn.Layout.Hash))
            foreach (var (name, value) in AtlasStats(conn)) stats[name] = stats.GetValueOrDefault(name) + value;
        return stats;
    }
}
