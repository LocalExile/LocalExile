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

    /// <summary>The ten tribes' tradeable tattoo pools and their vendor-recipe weights. The client tables carry the
    /// tribe (<c>PassiveSkillTattoos.Tribe</c>) but not the recipe weights; the wiki's 60,000-vendoring sample does:
    /// 3:3:3:1:1 per tribe, and Kitava 2:2:2:4:1. The tribe ids follow the client rows, 0 Ngamahu through 9 Valako
    /// (<see cref="VendorPool"/>). Only these tradeable tattoos come out: the Loyalty, Makanga, Honoured, Journey,
    /// Ancestral and Forbidden rows are not in the output pool, although any tattoo with a tribe can go in.</summary>
    static readonly (int Tribe, (string Name, int Weight)[] Pool)[] VendorPools =
    [
        (0, [("Tattoo of the Ngamahu Firewalker", 3), ("Tattoo of the Ngamahu Shaman", 3),
             ("Tattoo of the Ngamahu Warmonger", 1), ("Tattoo of the Ngamahu Warrior", 3),
             ("Tattoo of the Ngamahu Woodcarver", 1)]),
        (1, [("Tattoo of the Kitava Blood Drinker", 2), ("Tattoo of the Kitava Rebel", 2),
             ("Tattoo of the Kitava Warrior", 2), ("Tattoo of the Kitava Shaman", 1),
             ("Tattoo of the Kitava Heart Eater", 4)]),
        (2, [("Tattoo of the Tukohama Shaman", 3), ("Tattoo of the Tukohama Brawler", 3),
             ("Tattoo of the Tukohama Warcaller", 1), ("Tattoo of the Tukohama Warmonger", 1),
             ("Tattoo of the Tukohama Warrior", 3)]),
        (3, [("Tattoo of the Rongokurai Brute", 3), ("Tattoo of the Rongokurai Goliath", 3),
             ("Tattoo of the Rongokurai Guard", 1), ("Tattoo of the Rongokurai Warrior", 3),
             ("Tattoo of the Rongokurai Turtle", 1)]),
        (4, [("Tattoo of the Tasalio Bladedancer", 3), ("Tattoo of the Tasalio Scout", 1),
             ("Tattoo of the Tasalio Tideshifter", 3), ("Tattoo of the Tasalio Warrior", 1),
             ("Tattoo of the Tasalio Shaman", 3)]),
        (5, [("Tattoo of the Arohongui Moonwarden", 3), ("Tattoo of the Arohongui Scout", 3),
             ("Tattoo of the Arohongui Warrior", 3), ("Tattoo of the Arohongui Shaman", 1),
             ("Tattoo of the Arohongui Warmonger", 1)]),
        (6, [("Tattoo of the Ramako Archer", 3), ("Tattoo of the Ramako Fleetfoot", 1),
             ("Tattoo of the Ramako Scout", 3), ("Tattoo of the Ramako Shaman", 1),
             ("Tattoo of the Ramako Sniper", 3)]),
        (7, [("Tattoo of the Hinekora Deathwarden", 3), ("Tattoo of the Hinekora Shaman", 3),
             ("Tattoo of the Hinekora Storyteller", 1), ("Tattoo of the Hinekora Warmonger", 1),
             ("Tattoo of the Hinekora Warrior", 3)]),
        (8, [("Tattoo of the Tawhoa Herbalist", 1), ("Tattoo of the Tawhoa Naturalist", 3),
             ("Tattoo of the Tawhoa Scout", 3), ("Tattoo of the Tawhoa Shaman", 1),
             ("Tattoo of the Tawhoa Warrior", 3)]),
        (9, [("Tattoo of the Valako Shieldbearer", 1), ("Tattoo of the Valako Scout", 3),
             ("Tattoo of the Valako Shaman", 1), ("Tattoo of the Valako Stormrider", 3),
             ("Tattoo of the Valako Warrior", 3)]),
    ];

    /// <summary>The tattoos three same-tribe tattoos vendor into, with their weights, or empty for a tribe without a
    /// pool.</summary>
    public static IReadOnlyList<(string Name, int Weight)> VendorPool(int tribe) =>
        VendorPools.Where(pool => pool.Tribe == tribe).Select(pool => pool.Pool).FirstOrDefault() ?? [];

    /// <summary>A weighted random pick from <see cref="VendorPool"/>, or null for a tribe without one.</summary>
    public static string? VendorTattoo(int tribe, Random rng)
    {
        var pool = VendorPool(tribe);
        int total = pool.Sum(entry => entry.Weight);
        if (total <= 0) return null;
        int roll = rng.Next(total);
        foreach (var (name, weight) in pool)
            if ((roll -= weight) < 0) return name;
        return pool[^1].Name;
    }
}
