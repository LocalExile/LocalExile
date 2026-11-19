/// <summary>
/// The Atlas tree's rules on its own stats, applied before they join a map's area stats (Game.MapAreaStats): the client
/// shows and the level generator reads only the composed list. Small nodes are those that are not Notable, Keystone or
/// Gateway (ReminderTextSmallAtlasPassives); Extra Content is the families of ReminderTextAtlasExtraContent.
/// </summary>
public static class AtlasStats
{
    // The node id families (atlas_<family>_…, the AtlasPassiveSkillTreeGroupType of their group's mastery) of the leagues
    // ReminderTextAtlasExtraContent names: Delve is Niko's missions, Betrayal (jun) Jun's, Incursion (alva) Alva's,
    // Bestiary Einhar's, masters all four, Domination Shrines, Harvest the Sacred Grove, Heist Smuggler's Caches, Anarchy
    // Rogue Exiles, Settlers (Kalguur) Ore Deposits.
    public static readonly IReadOnlySet<string> ExtraContentFamilies = new HashSet<string>(StringComparer.Ordinal)
    {
        "abyss", "alva", "anarchy", "bestiary", "beyond", "blight", "breach", "delirium", "delve", "domination", "essence",
        "expedition", "harvest", "heist", "jun", "legion", "masters", "mercenary", "ritual", "settlers", "strongbox",
        "torment", "ultimatum",
    };

    static uint? Key(string id) => Program.Stats.Key(id);

    static int Sum(IEnumerable<AtlasPassives.Node> nodes, string id) =>
        Key(id) is uint key ? nodes.Sum(n => n.Stats.Where(s => s.Key == key).Sum(s => s.Value)) : 0;

    public static bool IsSmall(AtlasPassives.Node node) => !node.IsNotable && !node.IsKeystone && !node.IsWormhole && !node.IsJustIcon;

    public static string Family(AtlasPassives.Node node) => node.Id.Split('_') is { Length: > 1 } parts ? parts[1] : "";

    /// <summary>The allocated nodes' stats as the tree grants them: Notables grant nothing under
    /// display_notable_atlas_passives_grant_nothing, Small nodes nothing under display_small_…; a Small node's values are
    /// scaled by small_atlas_passive_effect_+% (plus map_atlas_extra_content_small_node_effect_+% for Extra Content),
    /// truncated; map_pack_size_+%_per_atlas_notable_passive_allocated adds pack size per allocated Notable.</summary>
    public static Dictionary<uint, int> Compose(IReadOnlyCollection<AtlasPassives.Node> nodes)
    {
        bool notablesNothing = Sum(nodes, "display_notable_atlas_passives_grant_nothing") > 0;
        bool smallNothing = Sum(nodes, "display_small_atlas_passives_grant_nothing") > 0;
        int smallEffect = Sum(nodes, "small_atlas_passive_effect_+%");
        int extraEffect = Sum(nodes, "map_atlas_extra_content_small_node_effect_+%");
        var stats = new Dictionary<uint, int>();
        foreach (var node in nodes)
        {
            bool small = IsSmall(node);
            if (small ? smallNothing : node.IsNotable && notablesNothing) continue;
            int effect = small ? 100 + smallEffect + (ExtraContentFamilies.Contains(Family(node)) ? extraEffect : 0) : 100;
            foreach (var (key, value) in node.Stats)
                stats[key] = stats.GetValueOrDefault(key) + (int)Math.Clamp((long)value * effect / 100, int.MinValue, int.MaxValue);
        }
        if (Sum(nodes, "map_pack_size_+%_per_atlas_notable_passive_allocated") is int perNotable and not 0
            && Stat.MapPackSizePlusPct is var packSize)
            stats[packSize] = stats.GetValueOrDefault(packSize) + perNotable * nodes.Count(n => n.IsNotable);
        return stats;
    }

    /// <summary>The increased effect of a map's explicit mods from the composed tree: map_mod_effect_+%_for_non_unique_maps
    /// (not on a unique map), plus map_mod_effect_+%_per_mod per explicit mod and map_mod_effect_+%_per_fragment_used per
    /// fragment.</summary>
    public static int ModEffect(IReadOnlyDictionary<uint, int> atlas, bool unique, int explicitMods, int fragments)
    {
        int Get(string id) => Key(id) is uint key ? atlas.GetValueOrDefault(key) : 0;
        return (unique ? 0 : Get("map_mod_effect_+%_for_non_unique_maps")) + Get("map_mod_effect_+%_per_mod") * explicitMods
            + Get("map_mod_effect_+%_per_fragment_used") * fragments;
    }
}
