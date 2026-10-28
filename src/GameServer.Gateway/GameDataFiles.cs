/// <summary>
/// The game data the Gateway reads from data/ (extracted from Client/Content.ggpk by scripts/dev/extract-data.ps1,
/// which live.ps1 runs on every start). Each feature turns itself off without its file; this lists the missing ones in
/// one startup warning. Keep in step with extract-data.ps1's manifest.
/// </summary>
static class GameDataFiles
{
    public static readonly (string File, string Feature)[] All =
    [
        ("data/tables/WorldAreas.json", "the game tables (stats, areas, waypoints, items, monsters, NPCs)"),
        ("data/tables/ModType.json", "vendor affix sale-price categories"),
        ("data/tables/Essences.json", "essence crafting and class-specific guaranteed modifiers"),
        ("data/tables/CraftingBenchOptions.json", "crafting bench recipes, unlocks and costs"),
        ("data/tables/CraftingItemClassCategories.json", "crafting bench item compatibility"),
        ("data/tables/UniqueMaps.json", "unique map shop identities"),
        ("data/tables/Words.json", "item names"),
        ("data/tables/ItemVisualIdentity.json", "unique map artwork"),
        // object-types manifest v4 combines inherited Life flags with Positioned/Animated collision metadata.
        ("data/object-types.json", "monsters (emergence and inherited Life corpse flags), NPCs, area transitions, stationary collision footprints"),
        ("data/item-types.json", "items"),
        ("data/area-transitions.json", "area transitions of fixed layouts"),
        ("data/room-points.json", "unique bosses, The Twilight Strand's encounters and support-gem chest"),
        ("data/hideouts.json", "hideouts and native stationary model collision radii"),
        ("data/animation-contacts.json", "class spell timing (the Witch's is used)"),
        ("data/monster-takehit.json", "monster hit reactions (the TakeHit flinch and its sound)"),
        ("data/passive-skill-graphs.json", "ordinary and Phrecia passive graph membership/connections (passive-graphs v4)"),
        ("data/phrecia-passive-skillgraph.psg", "merged passive graph analysis artifact (not installed)"),
    ];

    public static IEnumerable<(string File, string Feature)> Missing(string root = ".") =>
        All.Where(f => !File.Exists(Path.Combine(root, f.File)));

    /// <summary>One warning naming every missing file and what goes without it, or null when nothing is missing.</summary>
    public static string? Warning(string root = ".")
    {
        var missing = Missing(root).ToList();
        if (missing.Count == 0) return null;
        return $"[gateway] WARNING: {missing.Count} game data file(s) missing, these features are off: " +
            string.Join("; ", missing.Select(m => $"{m.File} ({m.Feature})")) +
            ". Run .\\scripts\\dev\\extract-data.ps1 (live.ps1 runs it unless -NoExtract).";
    }
}
