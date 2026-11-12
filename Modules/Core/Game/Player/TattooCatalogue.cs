using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core;


/// <summary>
/// The client's tattoo items: BaseItemTypes rows whose name contains "Tattoo", joined to
/// <c>PassiveSkillTattoos</c> for the target set and tribe. The effect itself is the replacement passive; the
/// currency item carries no mods. Runegrafts share the apply action and are excluded by the name filter.
/// </summary>
public static class TattooCatalogue
{
    public sealed record Entry(int Row, string Id, string Name, int Set, string Target, int Tribe, int Override,
        int OverrideHash, int RequiresAdjacent, int MaxAdjacent, int Limit, bool Forbidden, int Effect = -1)
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

        /// <summary>Whether the tattoo grants a skill (the Loyalty summons): <see cref="Effect"/> is its
        /// GrantedEffectsPerLevel row.</summary>
        public bool GrantsSkill => Effect >= 0;
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
        if (!ClientTables.Has("BaseItemTypes") || !ClientTables.Has("PassiveSkillTattoos") || !ClientTables.Has("PassiveSkillTattooTargetSets")) return result;

        var overrideHashes = new List<int>();
        var overrideRequires = new List<int>();
        var overrideMax = new List<int>();
        var overrideLimits = new List<int>();
        var overrideEffects = new List<int>();
        if (ClientTables.Has("PassiveSkillOverrides"))
            foreach (var row in ClientTables.Table("PassiveSkillOverrides"))
            {
                overrideHashes.Add(row.Int("HASH16"));
                overrideRequires.Add(row.Int("RequiresAdjacent"));
                overrideMax.Add(row.Int("MaxAdjacent"));
                overrideLimits.Add((row.IntOrNull("Limit") ?? -1));
                // The Loyalty rows replace a small passive with a granted skill: Effect is its
                // GrantedEffectsPerLevel row ("Trigger level 1 Summon Spirit of ...").
                overrideEffects.Add(row.IntOrNull("Effect") ?? -1);
            }

        var sets = new List<string>();
        foreach (var row in ClientTables.Table("PassiveSkillTattooTargetSets"))
        {
            string name = row.Text("Name") ?? "";
            string qualifier = row.Text("Qualifier") ?? "";
            sets.Add(qualifier.Length > 0 ? $"{name} ({qualifier})" : name);
        }

        var targets = new Dictionary<int, (int Set, int Tribe, int Override)>();
        foreach (var row in ClientTables.Table("PassiveSkillTattoos"))
        {
            int item = row.Int("Tattoo");
            targets[item] = (row.Int("Set"), row.Int("Tribe"),
                row.Int("Override"));
        }

        var bases = ClientTables.Table("BaseItemTypes");
        int index = 0;
        foreach (var row in bases)
        {
            string name = row.Text("Name") ?? "";
            string id = row.Text("Id") ?? "";
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
                int effect = target.Override >= 0 && target.Override < overrideEffects.Count
                    ? overrideEffects[target.Override] : -1;
                result.Add(new Entry(index, id, name, target.Set, targetName, target.Tribe, target.Override,
                    overrideHash, requiresAdjacent, maxAdjacent, limit, forbidden, effect));
            }
            index++;
        }
        return result;
    }
}
