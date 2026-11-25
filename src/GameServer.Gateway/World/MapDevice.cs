using System.Collections.Concurrent;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core;

/// <summary>
/// The hideout's map device (Agents/docs/protocol/21-hideouts.md §5): what its slots (Inventories row 13 <c>Map1</c>)
/// may hold when Activate (C→S 0x00d8) is pressed, which area a map opens (Maps.dat <c>Regular_WorldAreasKey</c>), and
/// each character's open map: its area, its instance once someone entered it, and the portals left.
/// </summary>
public sealed class MapDevice
{
    /// <summary>Portals one activation opens; each takes one player into the map once.</summary>
    public const int PortalCount = 6;

    public const string CampaignDeviceArea = "2_7_5_1", CampaignMapArea = "2_7_5_map",
        CampaignDeviceType = "Metadata/QuestObjects/Act7/MaligaroOrrery",
        CampaignMapItem = "Metadata/Items/QuestItems/Act7/MaligaroMap";
    public static readonly uint CampaignMapHash = InstanceEntities.ObjectTypeHash(CampaignMapItem);

    public static bool AcceptsItem(uint typeHash, string itemClass) =>
        typeHash == CampaignMapHash || IsRecipeItem(typeHash) || AcceptsClass(itemClass);

    /// <summary>ItemClasses Ids the slots take.</summary>
    public const string MapClass = "Map", MapKeyClass = "MapKey", FragmentClass = "MapFragment",
        MiscClass = "MiscMapItem", VaultClass = "VaultKey", MemoryLineClass = "MemoryLine";

    public static bool AcceptsClass(string itemClass) =>
        itemClass is MapClass or MapKeyClass or FragmentClass or MiscClass or VaultClass;

    public static bool AcceptsMemoryLineClass(string itemClass) =>
        AcceptsClass(itemClass) || itemClass == MemoryLineClass;

    static HashSet<uint>? _mavenInvitationHashes;
    static HashSet<uint>? _recipeItemHashes;

    /// <summary>Whether <paramref name="typeHash"/> is one of the Maven invitations (MapDeviceRecipes <c>MavenFight</c>
    /// / <c>MavenFights.BaseItemType</c>, including QuestItem invitations <c>MavenMapAtlas1..4</c>).</summary>
    public static bool IsMavenInvitation(uint typeHash)
    {
        if (typeHash == 0) return false;
        var hashes = _mavenInvitationHashes ??= LoadRecipeHashes(mavenOnly: true);
        return hashes.Contains(typeHash);
    }

    /// <summary>Whether <paramref name="typeHash"/> is listed in any MapDeviceRecipes row (e.g. QuestItem invitations).</summary>
    public static bool IsRecipeItem(uint typeHash)
    {
        if (typeHash == 0) return false;
        var hashes = _recipeItemHashes ??= LoadRecipeHashes(mavenOnly: false);
        return hashes.Contains(typeHash);
    }

    static HashSet<uint> LoadRecipeHashes(bool mavenOnly)
    {
        var hashes = new HashSet<uint>();
        if (!ClientTables.Has("BaseItemTypes")) return hashes;
        var baseItems = ClientTables.Table("BaseItemTypes");
        int baseCount = baseItems.Count;
        void AddRow(int row)
        {
            if (row < 0 || row >= baseCount) return;
            if (baseItems[row].Text("Id") is { Length: > 0 } id)
                hashes.Add(InstanceEntities.ObjectTypeHash(id));
        }
        if (mavenOnly)
        {
            if (ClientTables.Has("MavenFights"))
                foreach (var row in ClientTables.Table("MavenFights"))
                    if (row.IntOrNull("BaseItemType") is int itemRow) AddRow(itemRow);
            if (ClientTables.Has("MapDeviceRecipes"))
                foreach (var row in ClientTables.Table("MapDeviceRecipes"))
                    if (string.Equals(row.Text("Id"), "MavenFight", StringComparison.Ordinal))
                        foreach (int itemRow in row.Ints("RecipeItems")) AddRow(itemRow);
        }
        else if (ClientTables.Has("MapDeviceRecipes"))
        {
            foreach (var row in ClientTables.Table("MapDeviceRecipes"))
            {
                if ((row.IntOrNull("WorldArea") ?? 0) <= 0) continue;
                foreach (int itemRow in row.Ints("RecipeItems")) AddRow(itemRow);
            }
        }
        return hashes;
    }

    /// <summary>The area a map item opens: WorldAreas HASH16, Id and name, the map's tier and the level it plays at.</summary>
    public sealed record MapArea(ushort Hash16, string Id, string Name, int Tier)
    {
        /// <summary>The area level: the WorldAreas level for old area-named maps, or the map tier's level for generic
        /// maps. 0 leaves the client's own WorldAreas level in place.</summary>
        public int Level { get; init; }

        /// <summary>Whether a map device run for this area uses the owner's Atlas stats.</summary>
        public bool UsesAtlas { get; init; } = true;
        public string? DivinationCardArea { get; init; }
    }

    private readonly Dictionary<uint, MapArea> _byItemType;
    private readonly Dictionary<uint, MapArea> _uniqueByWord;
    private readonly AtlasCatalog? _atlas;
    private readonly MapRunCatalog _runs;
    private readonly List<MapArea?> _worldAreas;
    private readonly HashSet<ushort> _recipeAreas;

    /// <param name="uniqueByWord">The legendary maps (UniqueMaps) by their name's Words HASH32: the area a unique map
    /// opens. 3.29 builds most of them on the generic tier-1 base, so the base alone names no area of its own.</param>
    public MapDevice(IReadOnlyDictionary<uint, MapArea> byItemType, AtlasCatalog? atlas = null,
        MapRunCatalog? runs = null, IReadOnlyList<MapArea?>? worldAreas = null,
        IReadOnlyDictionary<uint, MapArea>? uniqueByWord = null)
    {
        _byItemType = new(byItemType);
        _uniqueByWord = uniqueByWord is null ? [] : new(uniqueByWord);
        _atlas = atlas;
        _runs = runs ?? new MapRunCatalog(new Dictionary<int, MapRunCatalog.Fragment>(), []);
        _worldAreas = worldAreas is null ? [] : [.. worldAreas];
        _recipeAreas = [.. _runs.Recipes
            .Where(r => r.WorldArea >= 0 && r.WorldArea < _worldAreas.Count && _worldAreas[r.WorldArea] is not null)
            .Select(r => _worldAreas[r.WorldArea]!.Hash16)];
    }

    /// <summary>Maps.dat: each map's BaseItemTypes row, by its HASH32 (<paramref name="hashOfRow"/>), to its regular area.
    /// Empty when the tables are missing.</summary>
    public static MapDevice Load(string folder, Func<int, uint?> hashOfRow, ItemCatalog? catalog = null)
    {
        var byType = new Dictionary<uint, MapArea>();
        if (ClientTables.Has("Maps") && ClientTables.Has("WorldAreas"))
        {
            var m = ClientTables.Table("Maps");
            var a = ClientTables.Table("WorldAreas");
            int areaCount = a.Count;
            foreach (var row in m)
            {
                if (row.IntOrNull("BaseItemTypesKey") is not int item
                    || row.IntOrNull("Regular_WorldAreasKey") is not int area
                    || area >= areaCount || hashOfRow(item) is not uint hash) continue;
                var wa = a[area];
                byType.TryAdd(hash, new MapArea((ushort)wa.Int("HASH16"), wa.Text("Id") ?? "",
                    wa.Text("Name") ?? "", row.Int("Tier"))
                {
                    Level = wa.Int("AreaLevel"),
                });
            }
        }
        var worldAreas = LoadWorldAreas(folder);
        return new MapDevice(byType, catalog is null ? null : new AtlasCatalog(catalog, folder),
            MapRunCatalog.Load(folder), worldAreas, LoadUniqueMaps());
    }

    /// <summary>UniqueMaps: each legendary map's name word (Words HASH32) to its WorldAreasKey. The level is the higher
    /// of the Maps row's tier level (Rewritten Distant Memory: tier 15, level 82, where its WorldAreas level is 68) and
    /// its WorldAreas level (Cortex: WorldAreas level 79, while its legacy Maps row is tier 4). Names on more than one
    /// row (The Beachhead) keep the first as the fallback when the base itself does not name a tier.</summary>
    static Dictionary<uint, MapArea> LoadUniqueMaps()
    {
        var byWord = new Dictionary<uint, MapArea>();
        if (!ClientTables.Has("UniqueMaps") || !ClientTables.Has("Words") || !ClientTables.Has("WorldAreas")) return byWord;
        var words = ClientTables.Table("Words");
        var areas = ClientTables.Table("WorldAreas");
        var tierOf = new Dictionary<int, int>();
        if (ClientTables.Has("Maps"))
            foreach (var row in ClientTables.Table("Maps"))
                if (row.IntOrNull("Unique_WorldAreasKey") is int ua) tierOf.TryAdd(ua, row.Int("Tier"));
        foreach (var unique in ClientTables.Table("UniqueMaps"))
        {
            if (unique.IntOrNull("WordsKey") is not int w || w < 0 || w >= words.Count
                || unique.IntOrNull("WorldAreasKey") is not int area || area < 0 || area >= areas.Count) continue;
            var wa = areas[area];
            int areaLevel = wa.Int("AreaLevel");
            int tier = tierOf.GetValueOrDefault(area);
            int level = tier > 0 ? Math.Max(MapLevel(tier), areaLevel) : areaLevel;
            byWord.TryAdd(words[w].UInt("HASH32"), new MapArea((ushort)wa.Int("HASH16"), wa.Text("Id") ?? "",
                unique.Text("Name") ?? wa.Text("Name") ?? "", tier)
            {
                Level = level,
            });
        }
        return byWord;
    }

    /// <summary>The area a unique map with these name words opens, or null when none is a legendary map's.</summary>
    public MapArea? UniqueAreaOf(IReadOnlyList<uint> nameWords)
    {
        foreach (uint word in nameWords)
            if (_uniqueByWord.TryGetValue(word, out var area)) return area;
        return null;
    }

    static List<MapArea?> LoadWorldAreas(string folder)
    {
        if (!ClientTables.Has("WorldAreas")) return [];
        var a = ClientTables.Table("WorldAreas");
        var list = new List<MapArea?>(a.Count);
        foreach (var wa in a)
        {
            if (wa.IntOrNull("HASH16") is not int hash)
            {
                list.Add(null);
                continue;
            }
            list.Add(new MapArea((ushort)hash, wa.Text("Id") ?? "",
                wa.Text("Name") ?? "", 0)
            {
                Level = (wa.IntOrNull("AreaLevel") ?? 0),
            });
        }
        return list;
    }

    public AtlasCatalog? Atlas => _atlas;

    public int Count => _byItemType.Count;

    public MapArea? AreaOf(uint itemType) => _byItemType.GetValueOrDefault(itemType);

    /// <summary>Whether a map opens this area (WorldAreas HASH16): it gets a portal back to the hideout.</summary>
    public bool IsMapArea(ushort hash16) => _byItemType.Values.Any(a => a.Hash16 == hash16)
        || _uniqueByWord.Values.Any(a => a.Hash16 == hash16)
        || _atlas?.Layouts.Any(l => l.Hash16 == hash16) == true || _recipeAreas.Contains(hash16);

    /// <summary>One item in the slots: its type, class, BaseItemTypes row, rolled mods and quality.</summary>
    public readonly record struct Slot(uint TypeHash, string Class, string Name, int BaseRow, IReadOnlyList<ItemMod> Mods, byte Quality)
    {
        /// <summary>AlternateQualityTypes row from a map chisel; null is quantity quality when <see cref="Quality"/> is set.</summary>
        public byte? AlternateQualityType { get; init; }

        /// <summary>The item's .it components when known; Map / MapKey admit a slot even if ItemClasses later rename it.</summary>
        public IReadOnlyList<string>? Components { get; init; }

        /// <summary>The item's rarity and corruption and its BaseItemTypes Id: the area stats name them (map_rarity,
        /// map_is_corrupted, the scarabs by kind; <see cref="MapMods"/>).</summary>
        public ItemRarity Rarity { get; init; }
        public bool Corrupted { get; init; }
        public string BaseId { get; init; } = "";

        /// <summary>The item's name words (Words HASH32): a unique map's name, which picks its legendary area.</summary>
        public IReadOnlyList<uint> NameWords { get; init; } = [];

        public bool IsMap =>
            Class is MapClass or MapKeyClass
            || Components is { } c && (c.Contains("Map") || c.Contains("MapKey"));
    }

    /// <summary>The area a successful Activate opens, the mods that instance plays with, and the first slot's
    /// BaseItemTypes row (a Maven invitation: <see cref="MapBossFights.Witnesses"/>).</summary>
    public sealed record Opening(MapArea Area, MapMods Mods, int ItemRow = 0)
    {
        /// <summary>AtlasPrimordialBossOptions selection from C→S 0xd8/0x297 (0 none, 1 Maven, 2 Eater, 3 Exarch).</summary>
        public byte BossOption { get; init; }

        /// <summary>Pantheon soul to capture when a Divine Vessel was placed with a matching map.</summary>
        public PantheonCatalog.Soul? PantheonSoul { get; init; }
    }

    /// <summary>
    /// What Activate does with these slots: one map (its affixes and the scarabs/fragments' MapFragmentMods), or a
    /// MapDeviceRecipes set with no map (Maven's Writ, four Sacrifice fragments). Returns the area, or null with the
    /// reason the player is told.
    /// </summary>
    public Opening? Check(IReadOnlyList<Slot> slots, out string why) => Check(slots, null, null, out why);

    public Opening? Check(IReadOnlyList<Slot> slots, string? character, ModCatalog? mods, out string why, bool campaign = false)
    {
        why = "";
        if (campaign)
        {
            if (slots.Count != 1 || slots[0].TypeHash != CampaignMapHash)
            {
                why = "Place only Maligaro's Map in this device.";
                return null;
            }
            var sanctum = _worldAreas.FirstOrDefault(a => a?.Id == CampaignMapArea);
            if (sanctum is not null) return new Opening(sanctum, MapMods.None);
            why = "Maligaro's Sanctum is not available.";
            return null;
        }
        if (slots.Any(s => s.Class == MemoryLineClass))
        {
            why = "Atlas Memory Lines are not yet implemented.";
            return null;
        }
        var maps = slots.Where(s => s.IsMap).ToList();
        if (maps.Count == 0 && _runs.Match([.. slots.Select(s => s.BaseRow)]) is { } earlyRecipe
            && !string.Equals(earlyRecipe.Id, "MaligaroMap", StringComparison.Ordinal)
            && AreaOfWorld(earlyRecipe) is { } earlyArea)
            return new Opening(earlyArea, mods is null ? MapMods.None : _runs.OfRecipe(earlyRecipe, mods),
                slots.Count > 0 ? slots[0].BaseRow : 0);
        if (slots.FirstOrDefault(s => !AcceptsClass(s.Class) && !s.IsMap && s.Class.Length > 0) is { Class: not null } wrong)
        {
            why = $"{wrong.Name} cannot be used in the Map Device: only maps and map fragments can.";
            return null;
        }
        if (maps.Count > 1) { why = "Only one map can be activated at a time."; return null; }
        if (maps.Count == 1)
        {
            // A legendary map opens its own area, whatever base it is built on (the tier-1 key, or a MapWorlds base
            // whose regular area is the ordinary map's). Keep a dedicated unique base (The Beachhead's mid/high bases)
            // when it already points directly at a unique area.
            MapArea? baseArea = AreaOf(maps[0].TypeHash);
            MapArea? uniqueArea = maps[0].Rarity == ItemRarity.Unique ? UniqueAreaOf(maps[0].NameWords) : null;
            MapArea? area = baseArea is not null && uniqueArea is not null
                && string.Equals(baseArea.Name, uniqueArea.Name, StringComparison.Ordinal)
                ? baseArea
                : uniqueArea ?? baseArea;
            if (area is null && _atlas?.TierOf(maps[0].TypeHash) is int tier)
            {
                if (_atlas.Selected(character) is { } layout)
                {
                    int level;
                    if (layout.RequiresSpecificItem)
                    {
                        if (layout.RequiredKeyHash is not uint required || maps[0].TypeHash != required)
                        {
                            why = $"{layout.Name} requires its own map item.";
                            return null;
                        }
                        level = Math.Max(MapLevel(tier), layout.AreaLevel);
                    }
                    else
                    {
                        if (_atlas.IsSpecialKey(maps[0].TypeHash))
                        {
                            why = $"{maps[0].Name} cannot be used on {layout.Name}.";
                            return null;
                        }
                        level = MapLevel(tier);
                    }
                    area = new(layout.Hash16, layout.Id, layout.Name, tier) { Level = level };
                }
                else
                {
                    why = "No Atlas layout is available: extract AtlasNode.dat and WorldAreas.dat.";
                    return null;
                }
            }
            if (area is null) { why = $"{maps[0].Name} opens no known area."; return null; }
            var extras = slots.Where(s => !s.IsMap).ToList();
            if (!_runs.FragmentsAllowed(extras, area.Level, out why)) return null;
            PantheonCatalog.Soul? pantheonSoul = null;
            if (extras.Any(s => PantheonCatalog.IsDivineVessel(s.BaseId)))
            {
                pantheonSoul = PantheonCatalog.Default?.SoulForArea(area.Id);
                if (pantheonSoul is null)
                {
                    why = $"{area.Name} has no Pantheon soul to capture with a Divine Vessel.";
                    return null;
                }
            }
            return new Opening(area, mods is null ? MapMods.None : _runs.Of(slots, mods)) { PantheonSoul = pantheonSoul };
        }
        if (_runs.Match([.. slots.Select(s => s.BaseRow)]) is { } recipe && AreaOfWorld(recipe) is { } recipeArea)
            return new Opening(recipeArea, mods is null ? MapMods.None : _runs.OfRecipe(recipe, mods),
                slots.Count > 0 ? slots[0].BaseRow : 0);
        why = maps.Count == 0 && slots.Count > 0
            ? "These items do not open an area."
            : "Put a map into the Map Device first.";
        return null;
    }

    MapArea? AreaOfWorld(MapRunCatalog.Recipe recipe)
    {
        if (recipe.WorldArea < 0 || recipe.WorldArea >= _worldAreas.Count || _worldAreas[recipe.WorldArea] is not { } area)
            return null;
        return area with { Level = recipe.AreaLevel > 0 ? recipe.AreaLevel : area.Level };
    }

    /// <summary>A generic map's area level: tier 1 is 68 and tier 16 is 83 (3.29 rules).</summary>
    public static int MapLevel(int tier) => Math.Clamp(tier + 67, 1, 100);

    /// <summary>A character's opened map: its area, its instance (0 until the first portal is taken) and the places around
    /// the device (0 to <see cref="PortalCount"/> - 1, <see cref="PortalCell"/>) whose portal is still there.</summary>
    public sealed record OpenMap(MapArea Area, ulong Instance, IReadOnlyList<int> Left, MapMods Mods, int ItemRow = 0)
    {
        public Guid Run { get; init; } = Guid.NewGuid();
        public string Owner { get; init; } = "";
        public bool EntryPending { get; init; }
        public int PortalsLeft => Left.Count;
        public Origin? ReturnTo { get; init; }
        /// <summary>The device's exact source instance and arrival cell, retained independently of return routing.</summary>
        public Origin? Entrance { get; init; }
        /// <summary>Selected AtlasPrimordialBossOptions value (0 none, 1 Maven, 2 Eater of Worlds, 3 Searing Exarch).</summary>
        public byte BossOption { get; init; }

        /// <summary>Pantheon soul bound to this map run when opened with a Divine Vessel.</summary>
        public PantheonCatalog.Soul? PantheonSoul { get; init; }
    }

    public sealed record Origin(ushort Area, ulong Instance, int X, int Y);

    /// <summary>What a map instance plays with for its whole life (<see cref="InstanceState.Map"/>): who opened it, its
    /// area, level, tier and mods, the first slot's row and the way back. Unlike <see cref="OpenOf"/>, it stays when the
    /// last portal is taken or the owner opens another map, and guests see the same one.</summary>
    public sealed record Run(string Owner, MapArea Area, MapMods Mods, int ItemRow = 0, Origin? ReturnTo = null)
    {
        public static Run Of(string owner, OpenMap map) => new(owner, map.Area, map.Mods, map.ItemRow, map.ReturnTo)
        {
            Atlas = map.Area.UsesAtlas,
            BossOption = map.BossOption,
            PantheonSoul = map.PantheonSoul,
        };

        /// <summary>The run is an Atlas map: the atlas tree, the tier block and the device stats apply. False for a run a
        /// module starts with its own mods (only those mods' stats apply).</summary>
        public bool Atlas { get; init; } = true;

        /// <summary>Selected AtlasPrimordialBossOptions value (0 none, 1 Maven, 2 Eater of Worlds, 3 Searing Exarch).</summary>
        public byte BossOption { get; init; }

        /// <summary>Pantheon soul bound to this map run when opened with a Divine Vessel.</summary>
        public PantheonCatalog.Soul? PantheonSoul { get; init; }
    }

    /// <summary>The map a transfer's ticket opens its instance with (<see cref="PendingTransfer.Map"/>), until that
    /// connection opens.</summary>
    public static readonly ConcurrentDictionary<uint, Run> TicketArrivals = new();

    /// <summary>A connection with <paramref name="ticket"/> opens in <paramref name="instance"/> (Auth's
    /// <paramref name="instanceId"/>) of area <paramref name="areaHash"/>: the map it plays with. The portal's map (also
    /// its last one), else the map whose instance this is (a guest, a login inside it); the first one fixes it.</summary>
    public static Run? Admit(InstanceState instance, uint ticket, ushort areaHash, ulong instanceId)
    {
        var retained = ForInstance(areaHash, instanceId, instance);
        var brought = TicketArrivals.TryRemove(ticket, out var arriving) ? arriving
            : retained is { } open ? Run.Of(open.Owner, open) : null;
        return instance.OpenedWith(brought?.Area.Hash16 == areaHash ? brought : null) is { } run && run.Area.Hash16 == areaHash
            ? run : null;
    }

    private static readonly ConcurrentDictionary<string, OpenMap> Open = new(StringComparer.OrdinalIgnoreCase);
    // A transfer can finish after its owner has replaced the device. Retain that arrival until the destination world
    // owns it; an abandoned client must not retain it indefinitely.
    static readonly ConcurrentDictionary<(ushort Area, ulong Instance), (OpenMap Map, long Until)> Arrivals = new();

    /// <summary>The character's open map, or null.</summary>
    public static OpenMap? OpenOf(string character) => Open.GetValueOrDefault(character);

    /// <summary>Opens a map for the character with a portal at every place, replacing the one open before.</summary>
    public static OpenMap Activate(string character, MapArea area, MapMods? mods = null, int itemRow = 0, Origin? returnTo = null,
        Origin? entrance = null, byte bossOption = 0, PantheonCatalog.Soul? pantheonSoul = null) =>
        Activate(character, area, mods, itemRow, returnTo, entrance, bossOption, pantheonSoul, PortalCount);

    /// <summary>Opens a map with the requested number of portal places, up to the device's capacity.</summary>
    public static OpenMap Activate(string character, MapArea area, MapMods? mods, int itemRow, Origin? returnTo,
        Origin? entrance, byte bossOption, PantheonCatalog.Soul? pantheonSoul, int portalCount)
    {
        lock (Open)
        {
            if ((entrance ?? returnTo) is { Instance: not 0 } origin)
                foreach (var other in Open.Where(p => (p.Value.Entrance ?? p.Value.ReturnTo) is { } at
                    && at.Area == origin.Area && at.Instance == origin.Instance).ToArray())
                    Open.TryRemove(other);
            return Open[character] = new OpenMap(area, 0, [.. Enumerable.Range(0, Math.Clamp(portalCount, 1, PortalCount))],
                mods ?? MapMods.None, itemRow)
                { Owner = character, ReturnTo = returnTo, Entrance = entrance, BossOption = bossOption, PantheonSoul = pantheonSoul };
        }
    }

    // Keep the original callable signature for compiled consumers.
    public static OpenMap Activate(string character, MapArea area, MapMods? mods, int itemRow, Origin? returnTo) =>
        Activate(character, area, mods, itemRow, returnTo, null, 0, null);

    /// <summary>The run whose portals belong to this exact device instance.</summary>
    public static OpenMap? AtEntrance(ushort area, ulong instance) => instance == 0 ? null
        : Open.Values.FirstOrDefault(m => (m.Entrance ?? m.ReturnTo) is { } o && o.Area == area && o.Instance == instance);

    /// <summary>The device's area is a new instance of the same area (a hideout's instance is gone six minutes after its
    /// last player leaves, and the device with it): the run's entrance and way back move to that instance, so the
    /// portals left stand at the new device and the map's portal back leads to where the character is. The character's
    /// open map after the move, or null without one; unchanged when neither points at the old instance.</summary>
    public static OpenMap? Rebind(string character, ushort area, ulong instance)
    {
        if (instance == 0) return null;
        while (Open.TryGetValue(character, out var map))
        {
            ulong[] gone = [.. new[] { map.Entrance, map.ReturnTo }.OfType<Origin>()
                .Where(o => o.Area == area && o.Instance != 0 && o.Instance != instance)
                .Select(o => o.Instance).Distinct()];
            if (gone.Length == 0) return map;
            var after = map with { Entrance = At(map.Entrance), ReturnTo = At(map.ReturnTo) };
            if (Open.TryUpdate(character, after, map)) return after;
            Origin? At(Origin? origin) => origin is { } at && at.Area == area && gone.Contains(at.Instance)
                ? at with { Instance = instance } : origin;
        }
        return null;
    }

    sealed class InstanceMap(OpenMap map) { public OpenMap Map { get; } = map; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<InstanceState, InstanceMap> InstanceMaps = new();

    /// <summary>The entered instance's rules and return route survive spending the last portal or replacing the device's run.</summary>
    public static OpenMap? ForInstance(Player player) => ForInstance(player.Layout.Hash, player.InstanceNumber, player.Instance);

    public static OpenMap? ForInstance(ushort area, ulong number, InstanceState? state)
    {
        if (state is not null && InstanceMaps.TryGetValue(state, out var remembered))
        {
            var kept = remembered.Map;
            // The device's instance can move while this map stays alive (an expired hideout, <see cref="Rebind"/>): the
            // owner's live run of this same map carries the current way back, the kept copy the one from its entry.
            return OpenOf(kept.Owner) is { } live && live.Run == kept.Run
                ? kept with { Entrance = live.Entrance, ReturnTo = live.ReturnTo } : kept;
        }
        var map = InInstance(area, number)?.Map;
        if (map is not null && state is not null)
        {
            map = InstanceMaps.GetValue(state, _ => new(map)).Map;
            Arrivals.TryRemove((area, number), out _);
        }
        return map;
    }

    /// <summary>The character's open map when they are in that area.</summary>
    public static OpenMap? Current(string character, ushort areaHash) =>
        OpenOf(character) is { } open && open.Area.Hash16 == areaHash ? open : null;

    /// <summary>The map owner and run for this exact instance, including guests after its owner leaves.</summary>
    public static (string Owner, OpenMap Map)? InInstance(ushort areaHash, ulong instance)
    {
        if (instance == 0) return null;
        if (Arrivals.TryGetValue((areaHash, instance), out var arrival) && arrival.Until > Environment.TickCount64)
            return (arrival.Map.Owner, arrival.Map);
        return Open.Where(p => p.Value.Area.Hash16 == areaHash && p.Value.Instance == instance)
            .Select(p => ((string Owner, OpenMap Map)?)(p.Key, p.Value)).FirstOrDefault();
    }

    /// <summary>Takes the portal at place <paramref name="index"/> of the character's open map, so it does not come back.
    /// Returns the map as it was (its instance to enter, 0 for a new one) and how many portals are left, or null when no
    /// map is open or that portal was taken already. Only one entrant can create its first instance.</summary>
    public static (OpenMap Map, int Left)? TakePortal(string character, int index) => TakePortal(character, index, null);

    public static (OpenMap Map, int Left)? TakePortal(string character, int index, Guid? run)
    {
        while (Open.TryGetValue(character, out var map))
        {
            if (run is { } expected && map.Run != expected || map.EntryPending || !map.Left.Contains(index)) return null;
            var after = map with { Left = [.. map.Left.Where(i => i != index)], EntryPending = map.Instance == 0 };
            if (Open.TryUpdate(character, after, map)) return (map, after.Left.Count);
        }
        return null;
    }

    /// <summary>The first portal's transfer created the map's instance: the other portals lead there.</summary>
    public static void Entered(string character, MapArea area, ulong instance) => Entered(character, area, instance, null);

    public static void Entered(string character, MapArea area, ulong instance, Guid? run)
    {
        while (Open.TryGetValue(character, out var map) && map.Area == area && map.Instance == 0
            && (run is null || map.Run == run))
            if (Open.TryUpdate(character, map with { Instance = instance, EntryPending = false }, map)) return;
    }

    public static void Entered(OpenMap run, ulong instance)
    {
        long now = Environment.TickCount64;
        foreach (var old in Arrivals.Where(p => p.Value.Until <= now)) Arrivals.TryRemove(old.Key, out _);
        Arrivals[(run.Area.Hash16, instance)] = (run with { Instance = instance, EntryPending = false }, now + 600_000);
        Entered(run.Owner, run.Area, instance, run.Run);
    }

    /// <summary>The map's instance is gone (expired): the map closes.</summary>
    public static void Close(string character) => Open.TryRemove(character, out _);

    public static void Close(string character, Guid run)
    {
        if (Open.TryGetValue(character, out var map) && map.Run == run) Open.TryRemove(new(character, map));
    }

    /// <summary>Where portal <paramref name="index"/> of <paramref name="count"/> stands: on a ring around the device,
    /// starting in front of it (south, +y).</summary>
    public static (int X, int Y) PortalCell(int deviceX, int deviceY, int index, int count = PortalCount, int radius = 16)
    {
        double angle = Math.PI / 2 + 2 * Math.PI * index / Math.Max(count, 1);
        return (deviceX + (int)Math.Round(radius * Math.Cos(angle)), deviceY + (int)Math.Round(radius * Math.Sin(angle)));
    }

    /// <summary>Radial facing angle (in radians, matching PoE Positioned's atan2(dx, -dy) convention) for a portal
    /// on the ring around the map device, so each of the 6 portals faces radially instead of all 6 sharing 0°.</summary>
    public static float PortalRotation(int deviceX, int deviceY, int portalX, int portalY, int index, int count = PortalCount)
    {
        int dx = portalX - deviceX, dy = portalY - deviceY;
        if (dx == 0 && dy == 0) return 2f * MathF.PI * index / Math.Max(count, 1);
        return MathF.Atan2(dx, -dy);
    }
}
