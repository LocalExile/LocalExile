using GameServer.Core;

/// <summary>
/// The seven Forbidden ascendancy tattoos (issue #372): each grants a random non-multiple-choice ascendancy notable
/// from its class's base ascendancies. The client's PassiveSkillOverrides rows of Type 17-23 are the pool - their
/// HASH16 is the notable's PassiveSkillGraphId and AllocatedPassiveSkill the PassiveSkills row to read its notability
/// and choice flags from - and the item's row names the class through Type 1 "DisplayRandomAscendancyNotableX".
/// A row's TattooBlockingPassive lists the passives its granted notable cannot replace (client row positions).
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

    sealed record PoolsData(Dictionary<string, ushort[]> Pools, Dictionary<ushort, HashSet<int>> Blocking,
        Dictionary<ushort, int> Positions);

    public static bool IsForbidden(string name) =>
        name.StartsWith("Forbidden Tattoo of the ", StringComparison.Ordinal);

    /// <summary>The class every Forbidden tattoo's name ends with ("Forbidden Tattoo of the Witch").</summary>
    static string ClassOf(string name) => name[(name.LastIndexOf(' ') + 1)..];

    static readonly Lazy<PoolsData> Pools = new(Load);

    /// <summary>The class's non-multiple-choice ascendancy notables as PassiveSkillGraphIds; null when the name is not
    /// a Forbidden tattoo or the class's data is missing.</summary>
    public static IReadOnlyList<ushort>? Pool(string name) =>
        IsForbidden(name) && Pools.Value.Pools.TryGetValue(ClassOf(name), out ushort[]? pool) && pool.Length > 0
            ? pool : null;

    /// <summary>A random notable of the class's pool that the character does not already have allocated, that is not
    /// <paramref name="node"/> (the replaced notable) and whose back-passive row does not list the replaced passive:
    /// a rolled duplicate would count its stats twice, and the client's TattooBlockingPassive forbids the swap.
    /// Null when the name is not a Forbidden tattoo or no free notable is left.</summary>
    public static ushort? Pick(string name, ushort node, IReadOnlyCollection<ushort>? allocated = null)
    {
        if (Pool(name) is not { } pool) return null;
        var data = Pools.Value;
        var taken = new HashSet<ushort>(allocated ?? []) { node };
        int replaced = data.Positions.GetValueOrDefault(node, -1);
        var candidates = pool.Where(id => !taken.Contains(id)
            && !(data.Blocking.TryGetValue(id, out var blocked) && blocked.Contains(replaced))).ToArray();
        return candidates.Length == 0 ? null : candidates[Random.Shared.Next(candidates.Length)];
    }

    /// <summary>Whether the client's row for this granted notable lists the replaced passive as TattooBlockingPassive.</summary>
    internal static bool Blocks(ushort granted, ushort node) =>
        Pools.Value.Blocking.TryGetValue(granted, out var blocked)
        && blocked.Contains(Pools.Value.Positions.GetValueOrDefault(node, -1));

    /// <summary>The replacement stats of the notable a Forbidden tattoo saved on this node: the chosen ascendancy
    /// passive's own stats, or null when the saved id is missing.</summary>
    public static IReadOnlyList<(uint Key, int Value)>? Stats(PassiveTree tree, ushort? chosen) =>
        chosen is ushort notable && tree.Find(notable) is { IsAscendancy: true } passive ? passive.Stats : null;

    static PoolsData Load()
    {
        var pools = new Dictionary<string, ushort[]>(StringComparer.Ordinal);
        var blocking = new Dictionary<ushort, HashSet<int>>();
        var positions = new Dictionary<ushort, int>();
        if (!ClientTables.Has("PassiveSkillOverrides") || !ClientTables.Has("PassiveSkills"))
            return new(pools, blocking, positions);

        // PassiveSkills rows by their position: the Forbidden pool rows' AllocatedPassiveSkill is that index. Scion's
        // base "Ascendant" notables are multiple-choice options whose nodes are not Notables, and the Shadow pool's
        // Knife in the Back and For the Jugular are options too: their row is the pool.
        var notables = new List<(int GraphId, bool IsNotable, bool IsOption, bool MultipleChoice)>();
        foreach (var row in ClientTables.Table("PassiveSkills"))
        {
            int graphId = row.Int("PassiveSkillGraphId");
            if (graphId is > 0 and <= ushort.MaxValue) positions[(ushort)graphId] = notables.Count;
            notables.Add((graphId, row.Int("IsNotable") != 0, row.Int("IsMultipleChoiceOption") != 0,
                row.Int("IsMultipleChoice") != 0));
        }

        var byType = new Dictionary<int, List<ushort>>();
        foreach (var row in ClientTables.Table("PassiveSkillOverrides"))
        {
            int type = row.Int("Type");
            if (type is < 17 or > 23) continue;
            int allocated = row.Int("AllocatedPassiveSkill"), hash = row.Int("HASH16");
            if (allocated < 0 || allocated >= notables.Count || hash is <= 0 or > ushort.MaxValue) continue;
            var notable = notables[allocated];
            bool eligible = notable.IsNotable || notable.IsOption;
            if (!eligible || notable.MultipleChoice || notable.GraphId != hash) continue;
            if (!byType.TryGetValue(type, out var list)) byType[type] = list = [];
            if (!list.Contains((ushort)hash)) list.Add((ushort)hash);
            if (row.Ints("TattooBlockingPassive") is { Length: > 0 } blocked)
                blocking[(ushort)hash] = [.. blocked];
        }
        foreach (var (name, type) in ClassTypes)
            if (byType.TryGetValue(type, out var list)) pools[name] = [.. list];
        return new(pools, blocking, positions);
    }
}
