/// <summary>
/// The game data the Gateway reads from data/ (extracted from Client/Content.ggpk by scripts/dev/extract-data.ps1,
/// which live.ps1 runs on every start). Each feature turns itself off without its file; this lists the missing ones in
/// one startup warning. Keep in step with extract-data.ps1's manifest.
/// </summary>
static class GameDataFiles
{
    public static readonly (string File, string Feature)[] All =
    [
        ("data/tables/WorldAreas.json", "the game tables (areas, waypoints, monsters, NPCs)"),
        ("data/tables/BaseItemTypes.json", "item bases (items, drops and /item)"),
        ("data/tables/ItemClasses.json", "item classes (socket caps and weapon restrictions)"),
        ("data/tables/Stats.json", "stats and the editable stat reference"),
        ("data/tables/Mods.json", "mods, affix rolls and the editable mod reference"),
        ("data/tables/Tags.json", "mod spawn tags (affix rolls)"),
        ("data/tables/InfluenceTags.json", "influence mods (rolls on influenced items)"),
        ("data/tables/UniqueStashTypes.json", "unique collection categories"),
        ("data/tables/UniqueStashLayout.json", "unique item names and artwork"),
        ("data/tables/VillageUniqueDisenchantValues.json", "unique drop weights"),
        ("data/tables/PassiveJewelSlots.json", "ordinary passive tree jewel sockets"),
        ("data/tables/ModType.json", "vendor affix sale-price categories"),
        ("data/tables/Essences.json", "essence crafting and class-specific guaranteed modifiers"),
        ("data/tables/CraftingBenchOptions.json", "crafting bench recipes, unlocks and costs"),
        ("data/tables/CraftingItemClassCategories.json", "crafting bench item compatibility"),
        ("data/tables/DelveUpgrades.json", "Delve equipment upgrade prices, limits and stats"),
        ("data/delve-paths.json", "authored Delve rail routes"),
        ("data/tables/DelveLevelScaling.json", "Delve trip costs and monster levels"),
        ("data/tables/DelveFlares.json", "Delve flare prices"),
        ("data/tables/DelveDynamite.json", "Delve dynamite prices"),
        ("data/tables/UniqueMaps.json", "unique map shop identities"),
        ("data/tables/Words.json", "item names"),
        ("data/tables/VillageUniqueDisenchantValues.json", "unique drop weights"),
        ("data/tables/ItemVisualIdentity.json", "unique map artwork"),
        // object-types manifest v4 combines inherited Life flags with Positioned/Animated collision metadata; v6 adds the
        // rooms' server objects (room-points.json) and the quest tile and room objects (ot_components.py QUEST_TILE_TYPES,
        // QUEST_ROOM_TYPES). item-types v2: the quest items' .it Quest flags.
        ("data/object-types.json", "monsters (emergence and inherited Life corpse flags), NPCs, area transitions, quest objects and chests, stationary collision footprints"),
        ("data/item-types.json", "items, quest items' Quest flags"),
        ("data/area-transitions.json", "area transitions of fixed layouts"),
        ("data/room-points.json", "unique bosses (mapboss<n>[WG], force:mapboss, mapbosspack, sub-graph rooms), The Twilight Strand's encounters and support-gem chest, quest room objects"),
        ("data/hideouts.json", "hideouts and native stationary model collision radii"),
        ("data/animation-contacts.json", "class spell contacts, ACT-selected ordinary wand release timing and the bow/wand release points"),
        ("data/monster-takehit.json", "monster hit reactions (the TakeHit flinch and its sound)"),
        ("data/monster-contacts.json", "monster projectile release timing and points (a Sand Spitter mortar leaves at half the attack from the default height without it)"),
        ("data/projectile-clips.json", "monster projectile flight and impact clips (a mortar is invisible without its flight clip)"),
        ("data/passive-skill-graphs.json", "ordinary and Phrecia passive graph membership/connections/geometry (passive-graphs v5)"),
        ("data/phrecia-passive-skillgraph.psg", "merged passive graph analysis artifact (not installed)"),
        ("data/game-files.zip", "levels the Gateway generates itself, the Phrecia classes' passive graph check"),
        ("data/client-constants.json", "the Gateway does not start without its HNC mask"),
        ("data/stat-descriptions.txt", "mod wording for tools' item crafter (the crafter mode)"),
    ];

    public static IEnumerable<(string File, string Feature)> Missing(string root = ".") =>
        All.Where(f => !File.Exists(Path.Combine(root, f.File)));

    /// <summary>Generated files whose older versions still load but lack what a feature reads (extract-data.ps1's manifest
    /// entry, the version that added it): with an older one the feature runs without it, silently.</summary>
    public static readonly (string Entry, int Version, string Feature)[] Needed =
    [
        ("item-types", 2, "quest items' .it Quest flags (held, picked up, handed in, used): without them quest items set no flags"),
        ("object-types", 6, "quest objects and chests (rooms' server objects, quest tile and room types): without them they get the wrong blob or none"),
    ];

    /// <summary>The <see cref="Needed"/> entries data/extract-stamps.json (written by extract-data.ps1) records at an older
    /// version; none without the file (it cannot tell).</summary>
    public static IEnumerable<(string Entry, int Have, int Version, string Feature)> Outdated(string root = ".")
    {
        string file = Path.Combine(root, "data", "extract-stamps.json");
        if (!File.Exists(file)) yield break;
        Dictionary<string, int> stamps = [];
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file).TrimStart('﻿'));
            foreach (var e in doc.RootElement.EnumerateObject())
                if (e.Value.ValueKind == System.Text.Json.JsonValueKind.Object && e.Value.TryGetProperty("version", out var v) && v.TryGetInt32(out int n))
                    stamps[e.Name] = n;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { stamps.Clear(); }
        foreach (var (entry, version, feature) in Needed)
            if (stamps.TryGetValue(entry, out int have) && have < version) yield return (entry, have, version, feature);
    }

    /// <summary>One warning naming every generated file older than a feature needs, or null.</summary>
    public static string? OutdatedWarning(string root = ".")
    {
        var old = Outdated(root).ToList();
        if (old.Count == 0) return null;
        return $"[gateway] WARNING: {old.Count} game data file(s) older than this build reads: " +
            string.Join("; ", old.Select(o => $"{o.Entry} v{o.Have} (needs v{o.Version}: {o.Feature})")) +
            ". Run .\\scripts\\dev\\extract-data.ps1 (live.ps1 runs it unless -NoExtract).";
    }

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
