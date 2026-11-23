using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    /// <summary>Extra S→C messages next to the 0x3d Atlas presets, before 0x21. The Atlas module restores completed
    /// maps here so the client's HUD is not up yet and does not announce them.</summary>
    public static Func<Player, IEnumerable<(string Log, byte[] Message)>>? ExtraPlayerMessages;

    /// <summary>Module data keys: the active Atlas preset (0..2) and the Atlas passive respec points.</summary>
    public const string AtlasActiveKey = "atlas.active", AtlasRespecKey = "atlas.respec";

    /// <summary>The preset the client's tree shows and allocates into (C→S 0x2a8 picks it).</summary>
    public static int AtlasActivePreset(Player player) => AtlasActivePreset(CharacterName(player), player.Character.AtlasPassives);

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

    public static void SetAtlasActivePreset(Player player, int preset) =>
        CharacterSaves.ChangeModuleData(CharacterName(player), AtlasActiveKey, _ => preset.ToString());

    /// <summary>Atlas passive respec points: a refund (C→S 0x3f) spends one, an Orb of Unmaking grants one.</summary>
    public static ushort AtlasRespecPoints(Player player) =>
        ushort.TryParse(CharacterSaves.ModuleData(CharacterName(player), AtlasRespecKey), out ushort points) ? points : (ushort)0;

    public static void SetAtlasRespecPoints(Player player, ushort points) =>
        CharacterSaves.ChangeModuleData(CharacterName(player), AtlasRespecKey, _ => points.ToString());

    // One Atlas preset, sorted (the client diffs and binary-searches the list): u28 the respec points, u2a the points
    // before what allocated nodes grant.
    public static byte[] AtlasSlots(Player player, int preset) =>
        InstanceEntities.PassiveSlots(AtlasRespecPoints(player), player.Character.AtlasPassivePoints, (byte)(1 << (2 + preset)),
            [.. player.Character.AtlasPassives[preset].Order()]);

    /// <summary>The three presets, the active one last: a single-preset 0x3d also makes that preset the client's active
    /// one, which the client never changes by itself.</summary>
    public static IEnumerable<(string Log, byte[] Message)> AtlasPresetMessages(Player player, string label)
    {
        int active = AtlasActivePreset(player);
        foreach (int preset in Enumerable.Range(0, player.Character.AtlasPassives.Length).Where(p => p != active).Append(active))
            yield return ($"0x003d {label}: Atlas preset {preset}{(preset == active ? " (active)" : "")}, " +
                $"{player.Character.AtlasPassives[preset].Count}/{player.Character.AtlasPassivePoints} points, {AtlasRespecPoints(player)} respec",
                AtlasSlots(player, preset));
    }

    public static async Task SendAtlasSlots(Player player,
        string label)
    {
        foreach (var (log, message) in AtlasPresetMessages(player, label))
            await Send(player, message, log);
    }

    /// <summary>The allocated Atlas passives' stats of the active preset, by stat name: what the tree adds to a map's.</summary>
    public static Dictionary<uint, int> AtlasStats(Player player)
    {
        Dictionary<uint, int> stats = [];
        int preset = player.Character.AtlasPreset is >= 0 and < 3 ? player.Character.AtlasPreset
            : Enumerable.Range(0, player.Character.AtlasPassives.Length).MaxBy(p => player.Character.AtlasPassives[p].Count);
        foreach (ushort graphId in player.Character.AtlasPassives[preset])
            if (AtlasPassiveNodes.Find(graphId) is { } node)
                foreach (var (key, value) in node.Stats)
                {
                    uint name = key;
                    stats[name] = stats.GetValueOrDefault(name) + value;
                }
        return stats;
    }

    /// <summary>The stats of the area the player is in that league mechanics read: a map's own (its affixes, scarabs and
    /// fragments) and, in a map, the Atlas passives'. Empty outside a map.</summary>
    public static Dictionary<uint, int> AreaLeagueStats(Player player)
    {
        Dictionary<uint, int> stats = [];
        if (MapModsOf(player) is { } mods)
            foreach (var (name, value) in mods.Stats) stats[name] = stats.GetValueOrDefault(name) + value;
        if (MapDevices.IsMapArea(player.Layout.Hash))
            foreach (var (name, value) in AtlasStats(player)) stats[name] = stats.GetValueOrDefault(name) + value;
        return stats;
    }
}
