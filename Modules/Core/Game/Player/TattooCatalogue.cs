using System.Text.Json;
using GameServer.Core.Protocol.Instance;


/// <summary>
/// The client's tattoo items: BaseItemTypes rows whose name contains "Tattoo", joined to
/// <c>PassiveSkillTattoos</c> for the target set and tribe. The effect itself is the replacement passive; the
/// currency item carries no mods. Runegrafts share the apply action and are excluded by the name filter.
/// </summary>
static class TattooCatalogue
{
    public sealed record Entry(int Row, string Id, string Name, int Set, string Target, int Tribe, int Override,
        int OverrideHash, int RequiresAdjacent, int MaxAdjacent, int Limit, bool Forbidden)
    {
        public bool DropDisabled => Name.StartsWith("Honoured ", StringComparison.Ordinal)
            || Name.StartsWith("Loyalty ", StringComparison.Ordinal)
            || Name.StartsWith("Ancestral ", StringComparison.Ordinal)
            || Name.Contains(" Makanga", StringComparison.Ordinal)
            || Name == "Journey Tattoo of Makanui";

        public bool EventOnly => Forbidden;

        /// <summary>The Makanga and Honoured Makanga adjacency gate: at least <see cref="RequiresAdjacent"/>
        /// allocated neighbours, or at most <see cref="MaxAdjacent"/> when that is non-zero.</summary>
        public bool HasAdjacency => RequiresAdjacent > 0 || MaxAdjacent > 0;
    }

    static readonly Lazy<List<Entry>> Entries = new(Load);

    public static IReadOnlyList<Entry> All => Entries.Value;

    public static string[] Names => [.. All.Select(entry => entry.Name)];

    /// <summary>The client's limit category for a tattoo, or null when the item has no special limit beyond the
    /// 50-tattoo cap. The client only renders the "Limited to: 1 ..." line from its own tables; the server enforces
    /// the same group independently.</summary>
    public static string? LimitName(int limit) => limit switch
    {
        0 => "Loyalty Tattoo",
        4 => "Attribute Notable Tattoo",
        5 => "Ancestral Tattoo",
        6 => "Keystone Tattoo",
        40 => "Ascendancy Tattoo",
        _ => null,
    };

    public static Entry? Find(string query)
    {
        if (All.FirstOrDefault(entry => entry.Name.Equals(query, StringComparison.OrdinalIgnoreCase)) is { } exact)
            return exact;
        return All.FirstOrDefault(entry => entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Creates one tattoo currency item. The client reads the effect from its own tattoo data; the item is
    /// just the BaseItemTypes row with a stack size of one.</summary>
    public static Item? Create(ItemCatalog items, string query)
    {
        if (Find(query) is not { } entry || items.Find(entry.Id) is not { } baseType) return null;
        return ItemCatalog.Create(baseType) with { StackSize = 1 };
    }

    static List<Entry> Load()
    {
        var result = new List<Entry>();
        string basePath = Path.Combine("data", "tables", "BaseItemTypes.json");
        string tattooPath = Path.Combine("data", "tables", "PassiveSkillTattoos.json");
        string setPath = Path.Combine("data", "tables", "PassiveSkillTattooTargetSets.json");
        string overridePath = Path.Combine("data", "tables", "PassiveSkillOverrides.json");
        if (!File.Exists(basePath) || !File.Exists(tattooPath) || !File.Exists(setPath)) return result;

        var overrideHashes = new List<int>();
        var overrideRequires = new List<int>();
        var overrideMax = new List<int>();
        var overrideLimits = new List<int>();
        if (File.Exists(overridePath))
            using (var document = JsonDocument.Parse(File.ReadAllBytes(overridePath)))
                foreach (var row in document.RootElement.EnumerateArray())
                {
                    overrideHashes.Add(row.GetProperty("HASH16").GetInt32());
                    overrideRequires.Add(row.GetProperty("RequiresAdjacent").GetInt32());
                    overrideMax.Add(row.GetProperty("MaxAdjacent").GetInt32());
                    overrideLimits.Add(row.TryGetProperty("Limit", out var limit) && limit.ValueKind == JsonValueKind.Number
                        ? limit.GetInt32() : -1);
                }

        var sets = new List<string>();
        using (var document = JsonDocument.Parse(File.ReadAllBytes(setPath)))
            foreach (var row in document.RootElement.EnumerateArray())
            {
                string name = row.GetProperty("Name").GetString() ?? "";
                string qualifier = row.GetProperty("Qualifier").GetString() ?? "";
                sets.Add(qualifier.Length > 0 ? $"{name} ({qualifier})" : name);
            }

        var targets = new Dictionary<int, (int Set, int Tribe, int Override)>();
        using (var document = JsonDocument.Parse(File.ReadAllBytes(tattooPath)))
            foreach (var row in document.RootElement.EnumerateArray())
            {
                int item = row.GetProperty("Tattoo").GetInt32();
                targets[item] = (row.GetProperty("Set").GetInt32(), row.GetProperty("Tribe").GetInt32(),
                    row.GetProperty("Override").GetInt32());
            }

        using var bases = JsonDocument.Parse(File.ReadAllBytes(basePath));
        int index = 0;
        foreach (var row in bases.RootElement.EnumerateArray())
        {
            string name = row.GetProperty("Name").GetString() ?? "";
            string id = row.GetProperty("Id").GetString() ?? "";
            if (name.Contains("Tattoo", StringComparison.Ordinal))
            {
                if (!targets.TryGetValue(index, out var target)) continue;
                string targetName = target.Set >= 0 && target.Set < sets.Count ? sets[target.Set] : "unknown";
                bool forbidden = name.StartsWith("Forbidden Tattoo", StringComparison.Ordinal);
                int overrideHash = target.Override >= 0 && target.Override < overrideHashes.Count
                    ? overrideHashes[target.Override] : 0;
                int requiresAdjacent = target.Override >= 0 && target.Override < overrideRequires.Count
                    ? overrideRequires[target.Override] : 0;
                int maxAdjacent = target.Override >= 0 && target.Override < overrideMax.Count
                    ? overrideMax[target.Override] : 0;
                int limit = target.Override >= 0 && target.Override < overrideLimits.Count
                    ? overrideLimits[target.Override] : -1;
                result.Add(new Entry(index, id, name, target.Set, targetName, target.Tribe, target.Override,
                    overrideHash, requiresAdjacent, maxAdjacent, limit, forbidden));
            }
            index++;
        }
        return result;
    }
}
