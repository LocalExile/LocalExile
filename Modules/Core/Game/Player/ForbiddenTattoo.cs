using System.Text.Json;
using GameServer.Core;

/// <summary>
/// The seven Forbidden ascendancy tattoos (issue #372): each grants a random non-multiple-choice ascendancy notable
/// from its class's base ascendancies. The client's PassiveSkillOverrides rows of Type 17-23 are the pool - their
/// HASH16 is the notable's PassiveSkillGraphId and AllocatedPassiveSkill the PassiveSkills row to read its notability
/// and choice flags from - and the item's row names the class through Type 1 "DisplayRandomAscendancyNotableX".
/// The pick is saved in TattooKeystones (a node -> chosen GraphId map, like Journey Makanui's keystone) so a relog
/// cannot reroll it; its stats replace the ascendancy node's and 0x33 sends the chosen notable's HASH16.
/// </summary>
public static class ForbiddenTattoo
{
    /// <summary>The item name's class word -> the PassiveSkillOverrides pool Type.</summary>
    static readonly Dictionary<string, int> ClassTypes = new(StringComparer.Ordinal)
    {
        ["Ranger"] = 17, ["Duelist"] = 18, ["Marauder"] = 19, ["Templar"] = 20,
        ["Witch"] = 21, ["Shadow"] = 22, ["Scion"] = 23,
    };

    public static bool IsForbidden(string name) =>
        name.StartsWith("Forbidden Tattoo of the ", StringComparison.Ordinal);

    /// <summary>The class every Forbidden tattoo's name ends with ("Forbidden Tattoo of the Witch").</summary>
    static string ClassOf(string name) => name[(name.LastIndexOf(' ') + 1)..];

    static readonly Lazy<Dictionary<string, ushort[]>> Pools = new(Load);

    /// <summary>The class's non-multiple-choice ascendancy notables as PassiveSkillGraphIds; null when the name is not
    /// a Forbidden tattoo or the class's data is missing.</summary>
    public static IReadOnlyList<ushort>? Pool(string name) =>
        IsForbidden(name) && Pools.Value.TryGetValue(ClassOf(name), out ushort[]? pool) && pool.Length > 0 ? pool : null;

    /// <summary>A random notable of the class's pool that the character does not already have allocated and that is
    /// not <paramref name="node"/> (the replaced notable): a rolled duplicate would count its stats twice. Null when the
    /// name is not a Forbidden tattoo or no free notable is left.</summary>
    public static ushort? Pick(string name, ushort node, IReadOnlyCollection<ushort>? allocated = null)
    {
        if (Pool(name) is not { } pool) return null;
        var taken = new HashSet<ushort>(allocated ?? []) { node };
        var candidates = pool.Where(id => !taken.Contains(id)).ToArray();
        return candidates.Length == 0 ? null : candidates[Random.Shared.Next(candidates.Length)];
    }

    /// <summary>The replacement stats of the notable a Forbidden tattoo saved on this node: the chosen ascendancy
    /// passive's own stats, or null when the saved id is missing.</summary>
    public static IReadOnlyList<(uint Key, int Value)>? Stats(PassiveTree tree, ushort? chosen) =>
        chosen is ushort notable && tree.Find(notable) is { IsAscendancy: true } passive ? passive.Stats : null;

    static Dictionary<string, ushort[]> Load()
    {
        var pools = new Dictionary<string, ushort[]>(StringComparer.Ordinal);
        string overridesPath = Path.Combine("data", "tables", "PassiveSkillOverrides.json");
        string passivesPath = Path.Combine("data", "tables", "PassiveSkills.json");
        if (!ClientTables.Exists(overridesPath) || !ClientTables.Exists(passivesPath)) return pools;

        // PassiveSkills rows by their position: the Forbidden pool rows' AllocatedPassiveSkill is that index. Scion's
        // base "Ascendant" notables are multiple-choice options whose nodes are not Notables: their row is the pool.
        var notables = new List<(int GraphId, bool IsNotable, bool IsOption, bool MultipleChoice)>();
        using (var passives = JsonDocument.Parse(ClientTables.ReadAllBytes(passivesPath)))
            foreach (var row in passives.RootElement.EnumerateArray())
                notables.Add((Int(row, "PassiveSkillGraphId"), Int(row, "IsNotable") != 0,
                    Int(row, "IsMultipleChoiceOption") != 0, Int(row, "IsMultipleChoice") != 0));

        var byType = new Dictionary<int, List<ushort>>();
        using (var document = JsonDocument.Parse(ClientTables.ReadAllBytes(overridesPath)))
            foreach (var row in document.RootElement.EnumerateArray())
            {
                int type = Int(row, "Type");
                if (type is < 17 or > 23) continue;
                int allocated = Int(row, "AllocatedPassiveSkill"), hash = Int(row, "HASH16");
                if (allocated < 0 || allocated >= notables.Count || hash is <= 0 or > ushort.MaxValue) continue;
                var notable = notables[allocated];
                bool eligible = notable.IsNotable || type == 23 && notable.IsOption;
                if (!eligible || notable.MultipleChoice || notable.GraphId != hash) continue;
                if (!byType.TryGetValue(type, out var list)) byType[type] = list = [];
                if (!list.Contains((ushort)hash)) list.Add((ushort)hash);
            }
        foreach (var (name, type) in ClassTypes)
            if (byType.TryGetValue(type, out var list)) pools[name] = [.. list];
        return pools;
    }

    static int Int(JsonElement row, string property) =>
        row.TryGetProperty(property, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.Number => value.GetInt32(),
                JsonValueKind.True => 1,
                _ => 0,
            }
            : 0;
}
