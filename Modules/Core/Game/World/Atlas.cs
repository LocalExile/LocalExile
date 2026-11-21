using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    /// <summary>Extra S→C messages next to the 0x3d Atlas presets, before 0x21. The Atlas module restores completed
    /// maps here so the client's HUD is not up yet and does not announce them.</summary>
    public static Func<ClientState, IEnumerable<(string Log, byte[] Message)>>? ExtraPlayerMessages;

    /// <summary>Module data keys: the active Atlas preset (0..2), respec points, one-off free reset flag, and preset names.</summary>
    public const string AtlasActiveKey = "atlas.active", AtlasRespecKey = "atlas.respec",
        AtlasFreeResetKey = "atlas.free_reset";

    public static string AtlasTreeNameKey(int preset) => $"atlas.name.{preset}";

    public static string[] AtlasTreeNames(ClientState conn)
    {
        string character = CharacterName(conn);
        return
        [
            CharacterSaves.ModuleData(character, AtlasTreeNameKey(0)) ?? "",
            CharacterSaves.ModuleData(character, AtlasTreeNameKey(1)) ?? "",
            CharacterSaves.ModuleData(character, AtlasTreeNameKey(2)) ?? "",
        ];
    }

    public static void SetAtlasTreeName(ClientState conn, int preset, string name) =>
        CharacterSaves.ChangeModuleData(CharacterName(conn), AtlasTreeNameKey(preset), _ => name);

    public static byte[] AtlasTreeNamesMessage(ClientState conn)
    {
        var names = AtlasTreeNames(conn);
        return InstanceEntities.AtlasTreeNames(names[0], names[1], names[2]);
    }

    public static bool HasAtlasFreeReset(ClientState conn) =>
        CharacterSaves.ModuleData(CharacterName(conn), AtlasFreeResetKey) == "1";

    public static void SetAtlasFreeReset(ClientState conn, bool available) =>
        CharacterSaves.ChangeModuleData(CharacterName(conn), AtlasFreeResetKey, _ => available ? "1" : "");

    /// <summary>The preset the client's tree shows and allocates into (C→S 0x2a8 picks it).</summary>
    public static int AtlasActivePreset(ClientState conn) => AtlasActivePreset(CharacterName(conn), conn.AtlasPassives);

    /// <summary>A character's active preset of <paramref name="presets"/>, -1 when it has none. Without a saved one it is the
    /// first preset with nodes (older saves hold their nodes in preset 2), else 0.</summary>
    public static int AtlasActivePreset(string character, IReadOnlyList<IReadOnlyCollection<ushort>> presets)
    {
        if (presets.Count == 0) return -1;
        if (int.TryParse(CharacterSaves.ModuleData(character, AtlasActiveKey), out int preset) && preset >= 0 && preset < presets.Count)
            return preset;
        for (int i = 0; i < presets.Count; i++)
            if (presets[i].Count > 0) return i;
        return 0;
    }

    public static void SetAtlasActivePreset(ClientState conn, int preset) =>
        CharacterSaves.ChangeModuleData(CharacterName(conn), AtlasActiveKey, _ => preset.ToString());

    /// <summary>Atlas passive respec points: a refund (C→S 0x3f) spends one, an Orb of Unmaking grants one.</summary>
    public static ushort AtlasRespecPoints(ClientState conn) =>
        ushort.TryParse(CharacterSaves.ModuleData(CharacterName(conn), AtlasRespecKey), out ushort points) ? points : (ushort)0;

    public static void SetAtlasRespecPoints(ClientState conn, ushort points) =>
        CharacterSaves.ChangeModuleData(CharacterName(conn), AtlasRespecKey, _ => points.ToString());

    // One Atlas preset, sorted (the client diffs and binary-searches the list): u28 the respec points, u2a the points
    // before what allocated nodes grant. Bit 0 is set only when the character has an unused free full reset.
    public static byte[] AtlasSlots(ClientState conn, int preset) =>
        InstanceEntities.PassiveSlots(AtlasRespecPoints(conn), conn.AtlasPassivePoints,
            (byte)((1 << (2 + preset)) | (HasAtlasFreeReset(conn) ? InstanceEntities.PassiveFreeResetFlag : 0)),
            [.. conn.AtlasPassives[preset].Order()]);

    /// <summary>The three presets, the active one last: a single-preset 0x3d also makes that preset the client's active
    /// one, which the client never changes by itself.</summary>
    public static IEnumerable<(string Log, byte[] Message)> AtlasPresetMessages(ClientState conn, string label)
    {
        int active = AtlasActivePreset(conn);
        foreach (int preset in Enumerable.Range(0, conn.AtlasPassives.Length).Where(p => p != active).Append(active))
            yield return ($"0x003d {label}: Atlas preset {preset}{(preset == active ? " (active)" : "")}, " +
                $"{conn.AtlasPassives[preset].Count}/{conn.AtlasPassivePoints} points, {AtlasRespecPoints(conn)} respec",
                AtlasSlots(conn, preset));
    }

    public static async Task SendAtlasSlots(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        string label)
    {
        foreach (var (log, message) in AtlasPresetMessages(conn, label))
            await Send(remote, stream, ciphers, message, log);
    }
}
