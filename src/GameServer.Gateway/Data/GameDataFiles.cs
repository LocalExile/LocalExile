using GameServer.Core;
/// <summary>
/// The game data the Gateway reads from data/ (extracted from the client in Client/ by scripts/dev/extract-data.ps1,
/// which live.ps1 runs on every start; the client's tables and the generated ones come from client.db through
/// ClientTables). Each feature turns itself off without its table or file; this lists the missing ones in
/// one startup warning. Keep in step with extract-data.ps1's manifest.
/// </summary>
public static class GameDataFiles
{
    /// <summary>The client tables (client.db, <see cref="ClientTables.Table"/>) features turn off without.</summary>
    public static readonly (string Table, string Feature)[] Tables =
    [
        ("KiracLevels", "Kirac map progression and refresh costs"),
        ("WorldAreas", "the game tables (areas, waypoints, monsters, NPCs)"),
        ("BaseItemTypes", "item bases (items, drops and /item)"),
        ("ItemClasses", "item classes (socket caps and weapon restrictions)"),
        ("Stats", "stats and the editable stat reference"),
        ("Mods", "mods, affix rolls and the editable mod reference"),
        ("Tags", "mod spawn tags (affix rolls)"),
        ("InfluenceTags", "influence mods (rolls on influenced items)"),
        ("UniqueStashTypes", "unique collection categories"),
        ("UniqueStashLayout", "unique item names and artwork"),
        ("VillageUniqueDisenchantValues", "unique drop weights"),
        ("PassiveJewelSlots", "ordinary passive tree jewel sockets"),
        ("PassiveTreeExpansionJewels", "cluster jewel templates, passive ranges and socket layouts"),
        ("PassiveTreeExpansionSkills", "cluster jewel small-passive themes and notable pools"),
        ("ModType", "vendor affix sale-price categories"),
        ("Essences", "essence crafting and class-specific guaranteed modifiers"),
        ("CraftingBenchOptions", "crafting bench recipes, unlocks and costs"),
        ("CraftingItemClassCategories", "crafting bench item compatibility"),
        ("DelveUpgrades", "Delve equipment upgrade prices, limits and stats"),
        ("DelveLevelScaling", "Delve trip costs and monster levels"),
        ("DelveResourcePerLevel", "sulphite deposits' native area-level rewards"),
        ("DelveFlares", "Delve flare prices"),
        ("DelveDynamite", "Delve dynamite prices"),
        ("UniqueMaps", "unique map shop identities"),
        ("LabyrinthTrials", "the Trials of Ascendancy (their areas and completion flags)"),
        ("GrantedEffectStatSets", "the Labyrinth spike traps' damage (LbRetractableTrap)"),
        ("GameConstants", "golem actor-level spell damage scaling"),
        ("SkillTrapVariations", "native placed-trap object identities"),
        ("BuffDefinitions", "the Labyrinth spike traps' bleeding buff"),
        ("MapFragmentMods", "map scarabs and fragments"),
        ("MapDeviceRecipes", "map device recipes (Maven, Atziri)"),
        ("MavenFights", "Maven invitation witness fights"),
        ("AtlasVoidstoneSlots", "Atlas Voidstones"),
        ("Words", "item names"),
        ("ItemVisualIdentity", "unique map artwork"),
        ("CharacterAudioEvents", "player character voice line events"),
        ("CharacterEventTextAudio", "player character voice line class mappings"),
    ];

    /// <summary>The generated tables (client.db: extract-data.ps1's generators, scripts/dev/generated_tables.py) features
    /// turn off without, by their required tables.</summary>
    public static readonly (string Table, string Feature)[] Generated =
    [
        ("vendor_shops", "ordinary vendor roles and quest/class gem membership"),
        ("delve_path_rooms", "authored Delve rail routes"),
        // object-types manifest v4 combines inherited Life flags with Positioned/Animated collision metadata; v6 adds the
        // rooms' server objects (room points) and the quest tile and room objects (ot_components.py QUEST_TILE_TYPES,
        // QUEST_ROOM_TYPES). item-types v2: the quest items' .it Quest flags.
        ("object_types", "monsters (emergence, inherited Life corpse flags and Actor slow-animation policy), NPCs, area transitions, quest objects and chests, stationary collision footprints"),
        ("item_types", "items, quest items' Quest flags"),
        ("area_transition_areas", "area transitions of fixed layouts"),
        ("room_point_rooms", "unique bosses (mapboss<n>[WG], force:mapboss, mapbosspack, sub-graph rooms), The Twilight Strand's encounters and support-gem chest, quest room objects and their fixed heights"),
        // hideouts v4: the collision radii of the Decorations panel's map device variants too, and the device models' own
        // portal places (hideout_portal_spawns) and the MapDevices row a model picks (hideout_device_variations).
        ("hideout_layouts", "hideouts and native stationary model collision radii"),
        ("animation_rigs", "class spell contacts, rig animation indices, the default attack's stance clips with their contact and lock_cancelling keyframes, ACT-selected ordinary wand release timing, the bow/wand release points and every contact frame of a clip"),
        ("animation_clip_events", "named animation events and the lengths of clips without a contact keyframe"),
        ("monster_takehit", "monster hit reactions (the TakeHit flinch and its sound)"),
        ("monster_contacts", "monster projectile release timing/points, every contact of a clip with several, and boss stage contacts"),
        ("monster_contact_clip_events", "all named monster clip events (trigger_proj, slam, move, face, hit_ground, etc.) and .ao views (monster-contacts v9)"),
        ("monster_melee_clocks", "ordinary monster melee clocks from the variety AO, including slow spans and ACT budgets (monster-contacts v8)"),
        ("monster_stances", "when a weapon-stanced monster's skill lands (the Rogue Exiles' stance clips)"),
        ("projectile_clips", "monster projectile flight and impact clips (a mortar is invisible without its flight clip) and the flight clip's contact, a spell mortar's landing time, also through .ao views and groups (projectile-clips v4)"),
        ("passive_graphs", "ordinary, Phrecia and Atlas passive graph membership/connections/geometry (passive-graphs v7)"),
        ("client_constants", "the Gateway does not start without its HNC mask"),
        ("stat_descriptions", "mod wording for tools' item crafter (the crafter mode)"),
    ];

    /// <summary>The files read from data/ itself.</summary>
    public static readonly (string File, string Feature)[] Files =
    [
        ("data/game-files.zip", "levels the Gateway generates itself, the Phrecia classes' passive graph check"),
    ];

    public static IEnumerable<(string File, string Feature)> Missing(string root = ".") =>
        Tables.Concat(Generated).Where(t => !ClientTables.Has(t.Table)).Select(t => ($"the table {t.Table}", t.Feature))
            .Concat(Files.Where(f => !File.Exists(Path.Combine(root, f.File))));

    /// <summary>Generated files whose older versions still load but lack what a feature reads (extract-data.ps1's manifest
    /// entry, the version that added it): with an older one the feature runs without it, silently.</summary>
    public static readonly (string Entry, int Version, string Feature)[] Needed =
    [
        ("game-pack", 5, "native Delve encounter rooms (4), the terrain plugins and the Deepwater rooms (5): without the last, the Allflame encounter areas are not generated"),
        ("tables", 4, "Kirac progression, gold refresh prices and native trap identities"),
        ("vendor-stock", 2, "vendor-specific quest/class gem membership and captured layout groups"),
        ("item-types", 2, "quest items' .it Quest flags (held, picked up, handed in, used): without them quest items set no flags"),
        ("animation-contacts", 11, "class rig animation indices, spell cancellation events, every contact frame, all named event times, and single-clip skill actions, including the ShieldBash waves"),
        ("monster-contacts", 9, "monster clip contact frames, all Animation.dat pools (including effect_locked, item_animation using, with_main_hand, @alias, and .ao views), all named clip events (profile-normalized), Throw-stage contacts, variety AO slow spans, ACT budgets, and weapon-stanced monsters' skill clips (monster-stances.json)"),
        ("projectile-clips", 4, "flight contact times, including animation views, groups and default animations"),
        ("monster-takehit", 5, "death clips' body-clear times: without them a lockstep client's corpses block paths for 150 ms whatever the monster"),
        ("room-points", 10, "the Labyrinth's rooms, so its arenas keep their doors, elevator and objective marker"),
        ("object-types", 13, "quest objects and chests (rooms' server objects, quest tile and room types, the Labyrinth's portals, its arena doors, elevator and objective marker, Oshabi's soul tree, the Allflame encounters' lamp and barrels) and inherited monster Actor slow-animation policy, phasing and immobility"),
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
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file).TrimStart('\uFEFF'));
            foreach (var e in doc.RootElement.EnumerateObject())
                if (e.Value.ValueKind == System.Text.Json.JsonValueKind.Object && e.Value.TryGetProperty("version", out var v) && v.TryGetInt32(out int n))
                    stamps[e.Name] = n;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { stamps.Clear(); }
        foreach (var (entry, version, feature) in Needed)
            if (stamps.TryGetValue(entry, out int have) && have < version && !InClientDb(root, entry)) yield return (entry, have, version, feature);
    }

    // An entry whose output the servers read from client.db (built from sql/, whatever the local extraction's version).
    static bool InClientDb(string root, string entry) => ClientTables.Has(entry switch
    {
        "tables" => "WorldAreas",
        "vendor-stock" => "vendor_shops",
        "animation-contacts" => "animation_rigs",
        _ => entry.Replace('-', '_'),
    });

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
