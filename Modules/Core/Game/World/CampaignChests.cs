using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Data.Terrain.Generation;
using static Game;

/// <summary>
/// Ordinary campaign chests and breakable containers (Barrels, Pots, Urns, Crates, Baskets, Cairns, Boulders,
/// Sarcophagi, Copper/Silver/Golden/Large Chests, Vases, Eggs, Blooms). Every container type is verified against
/// <c>client.db</c> <c>Chests</c> + <c>object_types</c> (exact or <c>InheritsFrom</c>) and
/// <see cref="QuestObject.CanSerialize"/> at startup. Opened state is persisted per <see cref="InstanceState"/>
/// via <see cref="InstanceState.OpenChest(int, int)"/> so party members and re-entries see consistent containers.
/// </summary>
public static class CampaignChests
{
    const string BaseChestType = "Metadata/Chests/Chest";
    const string ChemistGrainGateType = "Metadata/Chests/StrongBoxes/ChemistGrainGate";
    const string GrainGateChemistStrongboxType = "Metadata/Chests/GrainGate/GrainGateChemistStrongbox";
    const string ProminadeGardenChestType = "Metadata/Chests/QuestChests/ProminadeGardenChest";
    enum ChestTier { Breakable, Standard, Rich }

    static readonly HashSet<string> SpecialStaticChests = new(StringComparer.OrdinalIgnoreCase)
    {
        ChemistGrainGateType,
        GrainGateChemistStrongboxType,
        ProminadeGardenChestType,
    };

    sealed record ChestDef(string Id, string Name, string Model, IReadOnlyList<string> Components, int States,
        int MinLevel, int MaxLevel, ChestTier Tier);

    sealed record ClusterDef(string Id, IReadOnlyList<(ChestDef Chest, int Weight)> Entries, int MinCount, int MaxCount, int Radius);

    sealed record Theme(string[] Clusters, string[] Standalones);

    sealed record PlacedChest(uint Id, int Index, string Type, int X, int Y, float Rotation, float? Z);

    sealed class Area
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public List<PlacedChest>? Chests;
        public readonly Dictionary<uint, bool> Shown = [];
    }

    static Dictionary<string, ChestDef> Catalog = new(StringComparer.OrdinalIgnoreCase);
    static Dictionary<string, ClusterDef> Clusters = new(StringComparer.OrdinalIgnoreCase);
    static Dictionary<string, Theme> AreaThemes = new(StringComparer.Ordinal);
    static readonly RuleTable<InstanceState, Area> Areas = new();
    static readonly RuleTable<Player, HashSet<uint>> Presented = new();

    static readonly Theme DefaultTheme = new(
        ["BarrelsCrates", "Baskets", "Pots", "BarrelsOld"],
        ["Metadata/Chests/CopperChest1", "Metadata/Chests/CopperChest2", "Metadata/Chests/CopperChest3", "Metadata/Chests/Boulder1", "Metadata/Chests/Boulder2"]);

    static readonly Theme ShoreTheme = new(
        ["Baskets", "BasketsOld", "BarrelsOld", "CairnsSmall"],
        ["Metadata/Chests/Boulder1", "Metadata/Chests/Boulder2", "Metadata/Chests/Boulder3", "Metadata/Chests/CopperChest1", "Metadata/Chests/CopperChest2", "Metadata/Chests/BonePile1"]);

    static readonly Theme CavernTheme = new(
        ["BarrelsCratesOld", "CairnsSmall", "BarrelsSmallOld", "InfestationEggsGreenSmall"],
        ["Metadata/Chests/Boulder1", "Metadata/Chests/Boulder2", "Metadata/Chests/CopperChest1", "Metadata/Chests/CopperChest2", "Metadata/Chests/CopperChest3", "Metadata/Chests/BonePile1"]);

    static readonly Theme PrisonTheme = new(
        ["BarrelsCrates", "Barrels", "BarrelsSmall"],
        ["Metadata/Chests/Crate1", "Metadata/Chests/Crate2", "Metadata/Chests/CopperChest1", "Metadata/Chests/CopperChest2", "Metadata/Chests/SilverChest1"]);

    static readonly Theme ForestTheme = new(
        ["Baskets", "CairnsSmall", "BarrelsOld"],
        ["Metadata/Chests/Boulder1", "Metadata/Chests/Boulder2", "Metadata/Chests/TribalChest1", "Metadata/Chests/TribalChest2", "Metadata/Chests/CopperChest1"]);

    static readonly Theme CryptTheme = new(
        ["Urns", "Pots", "VaseOldCluster"],
        ["Metadata/Chests/ChurchSarcophagus1", "Metadata/Chests/ChurchSarcophagus2", "Metadata/Chests/Sarcophagus1", "Metadata/Chests/SilverChest1", "Metadata/Chests/SilverChest2", "Metadata/Chests/GoldenChest1"]);

    static readonly Theme VaalTheme = new(
        ["GoldPots", "DarkPots", "Urns", "GoldPotsBroken"],
        ["Metadata/Chests/VaalBoneChest1", "Metadata/Chests/Sarcophagus1", "Metadata/Chests/GoldenChest1", "Metadata/Chests/GoldenChest2", "Metadata/Chests/SilverChest1"]);

    static readonly Theme CityTheme = new(
        ["BarrelsCrates", "VaseCeramicCluster", "VaseNewCluster", "Pots", "VaseOrnateCluster"],
        ["Metadata/Chests/CityBoulder1", "Metadata/Chests/CityBoulder2", "Metadata/Chests/CopperChest1", "Metadata/Chests/SilverChest1", "Metadata/Chests/SilverChest2", "Metadata/Chests/CopperChestEpic3"]);

    static readonly Theme SewerTheme = new(
        ["BarrelsOld", "InfestationEggsPurpleSmall", "FungalBloomCluster", "BarrelsSmallOld"],
        ["Metadata/Chests/BonePile1", "Metadata/Chests/CopperChest1", "Metadata/Chests/CopperChest2", "Metadata/Chests/Boulder1"]);

    static readonly Theme TempleOrnateTheme = new(
        ["GoldPots", "SilverPots", "VaseOrnateCluster", "VaseMetalCluster"],
        ["Metadata/Chests/SilverChest1", "Metadata/Chests/SilverChest2", "Metadata/Chests/SilverChest3", "Metadata/Chests/GoldenChest1", "Metadata/Chests/GoldenChest2", "Metadata/Chests/GoldenChest3", "Metadata/Chests/TemplarChest01", "Metadata/Chests/TemplarChest02"]);

    static readonly Theme DesertTheme = new(
        ["MarakethUrns", "CairnsSmall", "Urns", "BasketsOld"],
        ["Metadata/Chests/BonePile1", "Metadata/Chests/TribalChest1", "Metadata/Chests/TribalChest2", "Metadata/Chests/TribalChest3", "Metadata/Chests/CopperChest1", "Metadata/Chests/CopperChest2", "Metadata/Chests/AmphoraChest1", "Metadata/Chests/AmphoraChest2"]);

    /// <summary>True when <paramref name="areaId"/> resolved to an explicit biome theme rather than <see cref="DefaultTheme"/>.</summary>
    public static bool HasSpecificTheme(string areaId) =>
        AreaThemes.TryGetValue(areaId, out var theme) && !ReferenceEquals(theme, DefaultTheme);

    /// <summary>Loads verified ordinary chests, chest clusters, and area themes from client.db at startup.</summary>
    public static void LoadGameData()
    {
        var catalog = new Dictionary<string, ChestDef>(StringComparer.OrdinalIgnoreCase);
        var byRow = new Dictionary<int, ChestDef>();
        var clusters = new Dictionary<string, ClusterDef>(StringComparer.OrdinalIgnoreCase);
        var themes = new Dictionary<string, Theme>(StringComparer.Ordinal);

        if (ClientTables.Has("Chests"))
        {
            var types = ObjectTypes.All().ToDictionary(t => t.Type, t => t.Row, StringComparer.OrdinalIgnoreCase);
            foreach (var row in ClientTables.Table("Chests"))
            {
                string id = row.Text("Id") ?? "";
                string? inheritsFrom = row.TextOrNull("InheritsFrom");
                bool isSpecial = SpecialStaticChests.Contains(id)
                    || (inheritsFrom is { Length: > 0 } && SpecialStaticChests.Contains(inheritsFrom));
                bool isOrdinary = IsOrdinaryChestRow(id, inheritsFrom);
                if (!isOrdinary && !isSpecial) continue;
                if (!types.TryGetValue(id, out var ot)
                    && !(inheritsFrom is { Length: > 0 } parent && types.TryGetValue(parent, out ot)))
                    continue;
                var components = ObjectTypes.Components(ot);
                if (!QuestObject.CanSerialize(components, haveStateMachine: false)) continue;
                if (!components.Contains("Chest") || !components.Contains("InteractionAction") || components.Contains("MinimapIcon")) continue;
                if (row.Texts("AOFiles").FirstOrDefault() is not { Length: > 0 } model) continue;
                int states = Math.Max(2, ot.IntOrNull("transitionable_num_states") ?? 2);
                int minLevel = row.IntOrNull("MinLevel") ?? 0;
                int maxLevel = row.IntOrNull("MaxLevel") ?? 100;
                string name = row.Text("Name") ?? id.Split('/')[^1];
                var tier = isSpecial ? ChestTier.Rich : ClassifyTier(id, name);
                var def = new ChestDef(id, name, model, components, states, minLevel, maxLevel, tier);
                catalog[id] = def;
                if (isSpecial && inheritsFrom is { Length: > 0 } specialParent && SpecialStaticChests.Contains(specialParent))
                    catalog.TryAdd(specialParent, def with { Id = specialParent });
                if (isOrdinary)
                    byRow[row.Index] = def;
            }
        }

        if (ClientTables.Has("ChestClusters"))
        {
            foreach (var row in ClientTables.Table("ChestClusters"))
            {
                string id = row.Text("Id") ?? "";
                if (string.IsNullOrEmpty(id) || id.Contains("Synthesis", StringComparison.OrdinalIgnoreCase)
                    || id.Contains("Explosive", StringComparison.OrdinalIgnoreCase)
                    || id.Contains("Wealthy", StringComparison.OrdinalIgnoreCase)
                    || id.Contains("Volatile", StringComparison.OrdinalIgnoreCase)
                    || id.Contains("Bloodworm", StringComparison.OrdinalIgnoreCase)
                    || id.Contains("Parasite", StringComparison.OrdinalIgnoreCase)
                    || id.Contains("Beacon", StringComparison.OrdinalIgnoreCase))
                    continue;
                int[] keys = row.Ints("ChestsKeys");
                int[] weights = row.Ints("__col2");
                var entries = new List<(ChestDef Chest, int Weight)>();
                for (int i = 0; i < keys.Length; i++)
                {
                    if (!byRow.TryGetValue(keys[i], out var chest)) continue;
                    int weight = i < weights.Length && weights[i] > 0 ? weights[i] : 1;
                    entries.Add((chest, weight));
                }
                if (entries.Count == 0) continue;
                int minCount = Math.Clamp(row.IntOrNull("__col3") ?? 3, 2, 6);
                int maxCount = Math.Clamp(row.IntOrNull("__col4") ?? 5, minCount, 7);
                int radius = Math.Clamp(row.IntOrNull("__col5") ?? 8, 5, 12);
                clusters[id] = new ClusterDef(id, entries, minCount, maxCount, radius);
            }
        }

        if (ClientTables.Has("WorldAreas") && ClientTables.Has("Tags"))
        {
            var tagNames = ClientTables.Table("Tags").ToDictionary(r => r.Index, r => r.Text("Id") ?? "");
            foreach (var area in ClientTables.Table("WorldAreas"))
            {
                if (area.Text("Id") is not { Length: > 0 } areaId) continue;
                var tags = area.Ints("AreaTypeTags").Concat(area.Ints("Tags"))
                    .Select(k => tagNames.GetValueOrDefault(k, ""))
                    .Where(t => t.Length > 0)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                themes[areaId] = ResolveTheme(tags);
            }
        }

        Catalog = catalog;
        Clusters = clusters;
        AreaThemes = themes;
    }

    static bool IsOrdinaryChestRow(string id, string? inheritsFrom)
    {
        if (!id.StartsWith("Metadata/Chests/", StringComparison.OrdinalIgnoreCase)) return false;
        if (id.Equals(BaseChestType, StringComparison.OrdinalIgnoreCase)) return false;
        string rest = id["Metadata/Chests/".Length..];
        bool nestedOk = rest.StartsWith("FungalBloom/", StringComparison.OrdinalIgnoreCase);
        if (rest.Contains('/') && !nestedOk) return false;
        if (!string.IsNullOrEmpty(inheritsFrom) && !inheritsFrom.Equals(BaseChestType, StringComparison.OrdinalIgnoreCase))
            return false;
        foreach (string banned in BannedChestTokens)
            if (id.Contains(banned, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    static readonly string[] BannedChestTokens =
    [
        "StrongBox", "Quest", "League", "Labyrinth", "Heist", "Delve", "Incursion", "Expedition", "Sanctum",
        "Blight", "Legion", "Breach", "Abyss", "Harvest", "Betrayal", "Ritual", "Ultimatum", "Settlers",
        "Synthesis", "AtlasBarrel", "AtlasPass", "RandomAtlasContent", "LakeChests", "MemoriesOfZana",
        "LostShipment", "Faridun", "Corrupted", "AlreadyOpened", "Laboratory", "VaultTreasurePile",
        "NoDrops", "Explode", "BarrelTar", "Trick", "Tutorial", "Reliquary", "Valdo", "Mission",
        "Prophecy", "Descent", "Royale", "TreasureHunter", "SideArea", "BootyChest", "SilverChestBathhouse",
        "StatueMakersTools", "ChestGroundDrop", "TidalIsland", "SouthernForest", "VastiriDesert",
    ];

    static ChestTier ClassifyTier(string id, string name)
    {
        if (id.Contains("Epic", StringComparison.OrdinalIgnoreCase)
            || id.Contains("GoldenChest", StringComparison.OrdinalIgnoreCase)
            || id.Contains("SilverChest", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Large Chest", StringComparison.OrdinalIgnoreCase))
            return ChestTier.Rich;
        if (id.Contains("Chest", StringComparison.OrdinalIgnoreCase)
            || id.Contains("Sarcophagus", StringComparison.OrdinalIgnoreCase)
            || id.Contains("Boulder", StringComparison.OrdinalIgnoreCase)
            || id.Contains("Rack", StringComparison.OrdinalIgnoreCase)
            || id.Contains("Corpse", StringComparison.OrdinalIgnoreCase)
            || id.Contains("Bundle", StringComparison.OrdinalIgnoreCase))
            return ChestTier.Standard;
        return ChestTier.Breakable;
    }

    static Theme ResolveTheme(HashSet<string> tags)
    {
        if (tags.Contains("inca") || tags.Contains("temple")
            || tags.Contains("vaal_ruin_area") || tags.Contains("vaal_pyramid_area")) return VaalTheme;
        if (tags.Contains("solaris") || tags.Contains("lunaris")) return TempleOrnateTheme;
        if (tags.Contains("desert_area")) return DesertTheme;
        if (tags.Contains("forestdark")) return ForestTheme;
        if (tags.Contains("church") || tags.Contains("crypt_area") || tags.Contains("library")
            || tags.Contains("mausoleum_area") || tags.Contains("sins")
            || tags.Contains("chamber_of_sins_area") || tags.Contains("catacombs")) return CryptTheme;
        if (tags.Contains("sewers") || tags.Contains("belly_area") || tags.Contains("sewer_area")) return SewerTheme;
        if (tags.Contains("prison") || tags.Contains("barracks") || tags.Contains("prison_area")) return PrisonTheme;
        if (tags.Contains("cavern") || tags.Contains("cave_area") || tags.Contains("mine_area")
            || tags.Contains("mountain") || tags.Contains("snow_area")) return CavernTheme;
        if (tags.Contains("city1") || tags.Contains("city2") || tags.Contains("city3")
            || tags.Contains("urban_sarn_area") || tags.Contains("gardens")
            || tags.Contains("docks") || tags.Contains("aqueduct_area")) return CityTheme;
        if (tags.Contains("shore")) return ShoreTheme;
        if (tags.Contains("southernforest") || tags.Contains("southernforest2")
            || tags.Contains("inland") || tags.Contains("fields_area")) return ForestTheme;
        return DefaultTheme;
    }

    static double DensityMultiplier() =>
        Math.Clamp(SystemSettings.Default.CampaignChestDensityMultiplier, 0, 10);

    static IEnumerable<Player> Members(Player player) => player.Instance.PlayersExcept(player.Id)
        .Prepend(player)
        .Where(p => !p.Closed && !p.LeavingArea && p.Instance == player.Instance).Distinct();

    public static Task OnAreaGenerated(Player player) => EnsureAndPresent(player);
    public static Task OnPlayerEntered(Player player) => EnsureAndPresent(player);

    public static void Leave(Player player) => Presented.Remove(player);

    static async Task EnsureAndPresent(Player player)
    {
        if (player.Instance is null || InTown(player)
            || HideoutCatalog.ByArea(player.Layout.Hash) is not null
            || player.Layout.Walkable is null
            || player.Layout.Id == TutorialObjects.Strand
            || MapDevices.IsMapArea(player.Layout.Hash)
            || LabyrinthTrials.InLabyrinth(player.Layout.Id))
            return;
        var area = Areas.GetOrCreateValue(player.Instance);
        await area.Gate.WaitAsync();
        try
        {
            area.Chests ??= Plan(player);
            foreach (var chest in area.Chests)
                await Present(player, area, chest);
        }
        finally { area.Gate.Release(); }
    }

    static List<(int X, int Y)> QuestChestPositions(Player player, AreaLayout layout)
    {
        var cells = new List<(int X, int Y)>();
        var questRules = QuestsOf(player).Chests(layout.Id);
        if (questRules.Count > 0)
        {
            var placed = new QuestObjectTypes.AreaPlacements(layout.Id, player.Seed, []);
            foreach (var rule in questRules)
                foreach (var p in placed.Of(rule.ChestType))
                    cells.Add((p.X, p.Y));
        }
        if (layout.Level is { } lvl)
        {
            foreach (var o in lvl.TileQuestObjects)
                if (o.Type.StartsWith("Metadata/Chests/", StringComparison.OrdinalIgnoreCase) && !SpecialStaticChests.Contains(o.Type))
                    cells.Add((o.X, o.Y));
            foreach (var o in lvl.RoomObjects)
                if (o.Type.StartsWith("Metadata/Chests/QuestChests/", StringComparison.OrdinalIgnoreCase) && !SpecialStaticChests.Contains(o.Type))
                    cells.Add((o.X, o.Y));
        }
        lock (player.QuestChests)
            foreach (var q in player.QuestChests.Values)
                cells.Add((q.At.X, q.At.Y));
        return cells;
    }

    static List<PlacedChest> Plan(Player player)
    {
        var result = new List<PlacedChest>();
        double density = DensityMultiplier();
        if (density <= 0 || Catalog.Count == 0 || player.Layout.Walkable is not { } walkable) return result;
        var layout = player.Layout;
        int level = Math.Clamp(ServerLevels.AreaLevel(layout.Id), 1, 100);
        var featureAreas = layout.Level?.FeatureAreas ?? [];
        bool InFeatureArea(int x, int y)
        {
            for (int i = 0; i < featureAreas.Count; i++)
            {
                var a = featureAreas[i];
                if (x >= a.X0 && y >= a.Y0 && x < a.X1 && y < a.Y1) return true;
            }
            return false;
        }
        var transitions = layout.AreaTransitions.Select(t => (X: t.Transition.GridX, Y: t.Transition.GridY)).ToList();
        if (layout.Waypoint is { } wp) transitions.Add((wp.GridX, wp.GridY));
        var arrival = (X: layout.SpawnX, Y: layout.SpawnY);
        static bool Near((int X, int Y) a, (int X, int Y) b, int d) =>
            (long)(a.X - b.X) * (a.X - b.X) + (long)(a.Y - b.Y) * (a.Y - b.Y) < (long)d * d;

        var questChestCells = QuestChestPositions(player, layout);

        // 1. Static ordinary chests authored in .tmd tiles and .arm rooms (e.g. CopperChestEpic3 in golden_chest.arm).
        if (layout.Level is { } lvl)
        {
            var staticSources = lvl.TileChests.Concat(lvl.RoomObjects.Where(o => ServerLevels.IsOrdinaryChestType(o.Type)));
            foreach (var o in staticSources)
            {
                if (!Catalog.TryGetValue(o.Type, out var def)) continue;
                if (InFeatureArea(o.X, o.Y) || Near((o.X, o.Y), arrival, 20) || transitions.Any(t => Near((o.X, o.Y), t, 14))) continue;
                if (questChestCells.Any(q => Near(q, (o.X, o.Y), 8))) continue;
                if (result.Any(c => Near((c.X, c.Y), (o.X, o.Y), 3))) continue;
                result.Add(new PlacedChest(player.Instance.World.TakeObjectId(), result.Count, def.Id, o.X, o.Y, o.Rotation,
                    float.IsNaN(o.Z) ? null : o.Z));
            }
        }

        // 2. Seeded ChestClusters + standalone containers scattered over reachable walkable cells.
        var reach = AreaPopulation.Reachable(layout.GridW, layout.GridH, walkable, arrival);
        if (reach is null) return result;
        int reachableCells = 0;
        var candidates = new List<(int X, int Y)>();
        for (int y = 4; y < layout.GridH - 4; y++)
            for (int x = 4; x < layout.GridW - 4; x++)
            {
                if (!reach[y * layout.GridW + x]) continue;
                reachableCells++;
                if ((x & 7) == 0 && (y & 7) == 0 && layout.WalkableFor(x, y, 2)
                    && !InFeatureArea(x, y) && !Near((x, y), arrival, 45)
                    && !transitions.Any(t => Near((x, y), t, 30)))
                    candidates.Add((x, y));
            }
        if (candidates.Count == 0) return result;

        int seed = unchecked((int)InstanceEntities.ObjectTypeHash(
            FormattableString.Invariant($"campaign-chests:{layout.Hash:x4}:{player.Seed:x8}")));
        var rng = new Random(seed);
        for (int i = candidates.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }

        var theme = AreaThemes.GetValueOrDefault(layout.Id, DefaultTheme);
        var themeClusters = theme.Clusters.Select(id => Clusters.GetValueOrDefault(id)).OfType<ClusterDef>().ToList();
        var themeStandalones = theme.Standalones.Select(id => Catalog.GetValueOrDefault(id))
            .OfType<ChestDef>().Where(c => level >= c.MinLevel && level <= c.MaxLevel).ToList();
        if (themeClusters.Count == 0)
            themeClusters = DefaultTheme.Clusters.Select(id => Clusters.GetValueOrDefault(id)).OfType<ClusterDef>().ToList();
        if (themeStandalones.Count == 0)
            themeStandalones = DefaultTheme.Standalones.Select(id => Catalog.GetValueOrDefault(id)).OfType<ChestDef>().ToList();
        if (themeClusters.Count == 0 && themeStandalones.Count == 0) return result;

        int targetGroups = Math.Clamp((int)Math.Round(reachableCells / 18000.0 * density), 3, 14);
        int maxScattered = Math.Clamp((int)Math.Round(48 * density), 8, 96);
        int scatteredStart = result.Count;
        var centres = new List<(int X, int Y)>(result.Select(c => (c.X, c.Y)));
        int placedGroups = 0;

        foreach (var c in candidates)
        {
            if (placedGroups >= targetGroups || result.Count - scatteredStart >= maxScattered) break;
            if (centres.Any(other => Near(other, c, 24)) || questChestCells.Any(q => Near(q, c, 12))) continue;

            bool standalone = themeStandalones.Count > 0 && (themeClusters.Count == 0 || rng.Next(4) == 0);
            if (standalone)
            {
                var def = themeStandalones[rng.Next(themeStandalones.Count)];
                result.Add(new PlacedChest(player.Instance.World.TakeObjectId(), result.Count, def.Id, c.X, c.Y,
                    (float)(rng.NextDouble() * Math.Tau), null));
                centres.Add(c);
                placedGroups++;
                continue;
            }

            var cluster = themeClusters[rng.Next(themeClusters.Count)];
            int count = rng.Next(cluster.MinCount, cluster.MaxCount + 1);
            int totalWeight = cluster.Entries.Sum(e => e.Weight);
            int added = 0;
            for (int k = 0; k < count && result.Count - scatteredStart < maxScattered; k++)
            {
                for (int attempt = 0; attempt < 12; attempt++)
                {
                    int dx = k == 0 && attempt == 0 ? 0 : rng.Next(-cluster.Radius, cluster.Radius + 1);
                    int dy = k == 0 && attempt == 0 ? 0 : rng.Next(-cluster.Radius, cluster.Radius + 1);
                    int x = c.X + dx, y = c.Y + dy;
                    if ((uint)x >= (uint)layout.GridW || (uint)y >= (uint)layout.GridH || !reach[y * layout.GridW + x]) continue;
                    if (!layout.WalkableFor(x, y, 1) || InFeatureArea(x, y)) continue;
                    if (questChestCells.Any(q => Near(q, (x, y), 8))) continue;
                    if (result.Any(existing => Near((existing.X, existing.Y), (x, y), 3))) continue;
                    int roll = rng.Next(Math.Max(1, totalWeight));
                    var picked = cluster.Entries[^1].Chest;
                    foreach (var (chest, weight) in cluster.Entries)
                        if ((roll -= weight) < 0) { picked = chest; break; }
                    result.Add(new PlacedChest(player.Instance.World.TakeObjectId(), result.Count, picked.Id, x, y,
                        (float)(rng.NextDouble() * Math.Tau), null));
                    added++;
                    break;
                }
            }
            if (added > 0)
            {
                centres.Add(c);
                placedGroups++;
            }
        }

        return result;
    }

    static async Task Present(Player player, Area area, PlacedChest chest)
    {
        var shown = Presented.GetOrCreateValue(player);
        if (shown.Contains(chest.Id)) return;
        if (!Catalog.TryGetValue(chest.Type, out var def)) return;
        bool opened = player.Instance.ChestOpened(chest.X, chest.Y);
        if (!area.Shown.ContainsKey(chest.Id))
        {
            var blob = new QuestObject(chest.X, chest.Y, def.Components, chest.Rotation, chest.Z, def.Model,
                TransitionState: (byte)(opened ? def.States : 1), Opened: opened).Blob();
            await SendWorldCreate(player, chest.Id,
                () => InstanceEntities.CreateObject(chest.Id, InstanceEntities.ObjectTypeHash(chest.Type), blob),
                null, wake: true, awaitBirth: false);
            area.Shown[chest.Id] = opened;
        }
        if (opened && def.Tier == ChestTier.Breakable) player.RemoveBlocker(chest.Id);
        else player.SetBlocker(chest.Id, chest.X, chest.Y, 1);
        shown.Add(chest.Id);
    }

    public static async Task<bool> Use(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (player.Instance is null || !Areas.TryGetValue(player.Instance, out var area)
            || area.Chests?.FirstOrDefault(c => c.Id == use.TargetId) is not { } chest)
            return false;
        if (player.Closed || player.LeavingArea || player.PlayerLife == 0) return true;
        if (player.Instance.ChestOpened(chest.X, chest.Y))
        {
            int current = player.ActionCount;
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null,
                still: () => PlayerActionCurrent(player, current));
            return true;
        }
        if (!TryBeginObjectWalk(player, requestedAt, chest.X, chest.Y, 1, out var walk)) return true;
        var (action, now, here, _, arrive) = walk;
        if (!use.Started)
            await SendWorldRecord(player, player.Id,
                InstanceProtocol.ActionStarted(player.Id, here.X, here.Y, chest.X, chest.Y, use.SkillId, use.Sequence, use.Flags,
                    targetObjectId: chest.Id), null, still: () => PlayerActionCurrent(player, action));
        if (arrive > now) await AreaDelay(player, arrive - now);
        await area.Gate.WaitAsync();
        try
        {
            chest = area.Chests!.FirstOrDefault(c => c.Id == use.TargetId) ?? chest;
            if (!PlayerActionCurrent(player, action) || player.PlayerLife == 0 || player.Closed || player.LeavingArea
                || !GameServer.Core.World.PlayerWalk.InWalkUpRange(player.Walk.Position(AreaTime(player)), (chest.X, chest.Y), 1))
                return true;
            await Open(player, area, chest);
        }
        finally
        {
            area.Gate.Release();
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null,
                still: () => PlayerActionCurrent(player, action));
        }
        return true;
    }

    /// <summary>GameEvents.ChestsOpened: chest-opening warcries open nearby campaign chests and breakables.</summary>
    public static async Task OpenNear(Player player, (int X, int Y) at, int radius)
    {
        if (player.Instance is null || !Areas.TryGetValue(player.Instance, out var area) || area.Chests is null) return;
        await area.Gate.WaitAsync();
        try
        {
            foreach (var chest in area.Chests.ToArray())
                if (!player.Instance.ChestOpened(chest.X, chest.Y)
                    && (long)(chest.X - at.X) * (chest.X - at.X) + (long)(chest.Y - at.Y) * (chest.Y - at.Y) <= (long)radius * radius)
                    await Open(player, area, chest);
        }
        finally { area.Gate.Release(); }
    }

    static async Task Open(Player player, Area area, PlacedChest chest)
    {
        if (!Catalog.TryGetValue(chest.Type, out var def)) return;
        if (!player.Instance.OpenChest(chest.X, chest.Y)) return;

        // Chest has no 0x2bc update reader: always recreate under a new 0x04xxxxxx object id with Opened = true
        // (matching QuestEngine.OpenQuestChest so Chest.Opened and nameplate removal sync reliably).
        uint oldId = chest.Id;
        uint openedId = player.Instance.World.TakeObjectId();
        var opened = chest with { Id = openedId };
        if (chest.Index < area.Chests!.Count) area.Chests[chest.Index] = opened;
        area.Shown.Remove(oldId);
        area.Shown[openedId] = true;
        await SendWorldRemoval(player, oldId, null);
        var blob = new QuestObject(opened.X, opened.Y, def.Components, opened.Rotation, opened.Z, def.Model,
            TransitionState: (byte)def.States, Opened: true).Blob();
        await SendWorldCreate(player, openedId,
            () => InstanceEntities.CreateObject(openedId, InstanceEntities.ObjectTypeHash(opened.Type), blob),
            $"campaign chest 0x{openedId:x} {def.Name} opened at ({opened.X}, {opened.Y})",
            wake: true, awaitBirth: false);
        foreach (var member in Members(player))
        {
            member.RemoveBlocker(oldId);
            if (def.Tier != ChestTier.Breakable) member.SetBlocker(openedId, opened.X, opened.Y, 1);
            var presented = Presented.GetOrCreateValue(member);
            presented.Remove(oldId);
            presented.Add(openedId);
        }

        await DropLoot(player, chest, def);
    }

    static async Task DropLoot(Player player, PlacedChest chest, ChestDef def)
    {
        if (Environment.GetEnvironmentVariable("Game_DROPS") == "0" || !GameEvents.AllowsLoot(player)) return;
        int level = Math.Clamp(ServerLevels.AreaLevel(player.Layout.Id), 1, 100);
        int charLevel = player.Pools.Level;
        string areaId = player.Layout.Id;
        var mapMods = MapModsOf(player);
        int areaIir = mapMods?.Rarity ?? 0;
        double areaIiq = mapMods?.QuantityFactor ?? 1;
        var rng = AreaRng(player);
        var drops = DropsFor(player);
        var league = LeagueFor(player);
        var chestContext = DropContext.Container(areaId: areaId, leagueId: league.Id, hardMode: league.HardMode);
        var rolled = new List<ItemDrops.Drop>();
        switch (def.Tier)
        {
            case ChestTier.Rich:
                // 2–4 Rare-rarity rolls + gold (matching Large/Silver/Golden Chests), plus Tier 1 map chance at L58+.
                rolled.AddRange(drops.Roll(level, MonsterRarity.Rare, rng, increasedRarity: 0, quantityMultiplier: 1,
                    rarityMultiplier: 1, mapTier: 0, mods: null, areaId: areaId, characterLevel: charLevel, areaLevel: level, context: chestContext));
                if (SpecialStaticChests.Contains(def.Id))
                    rolled.AddRange(drops.Roll(level, MonsterRarity.Magic, rng, increasedRarity: 0, quantityMultiplier: 1,
                        rarityMultiplier: 1, mapTier: 0, mods: null, areaId: areaId, characterLevel: charLevel, areaLevel: level, context: chestContext));
                if (level >= 58 && drops.Map(level, MonsterRarity.Rare, mapTier: 0, rng, chanceFactor: areaIiq) is { } richMap)
                    rolled.Add(richMap);
                if (drops.Gold(level, MonsterRarity.Rare, rng, multiplier: 0.8, increasedRarity: areaIir, quantityFactor: areaIiq) is { } richGold) rolled.Add(richGold);
                break;
            case ChestTier.Standard:
                // 1–2 Magic-rarity rolls + moderate gold (Copper/Tribal/Sarcophagus/Boulder), plus Tier 1 map chance at L58+.
                rolled.AddRange(drops.Roll(level, MonsterRarity.Magic, rng, increasedRarity: 0, quantityMultiplier: 1,
                    rarityMultiplier: 1, mapTier: 0, mods: null, areaId: areaId, characterLevel: charLevel, areaLevel: level, context: chestContext));
                if (level >= 58 && drops.Map(level, MonsterRarity.Magic, mapTier: 0, rng, chanceFactor: areaIiq) is { } stdMap)
                    rolled.Add(stdMap);
                if (rng.Next(100) < 60 && drops.Gold(level, MonsterRarity.Magic, rng, multiplier: 0.6, increasedRarity: areaIir, quantityFactor: areaIiq) is { } stdGold) rolled.Add(stdGold);
                break;
            default:
                // Breakables (Barrels/Pots/Urns/Baskets/Crates/Cairns/Vases): Normal-rarity roll (~35% for 1 item) + small gold.
                rolled.AddRange(drops.Roll(level, MonsterRarity.Normal, rng, increasedRarity: 0, quantityMultiplier: 1,
                    rarityMultiplier: 1, mapTier: 0, mods: null, areaId: areaId, characterLevel: charLevel, areaLevel: level, context: chestContext));
                if (rng.Next(100) < 25 && drops.Gold(level, MonsterRarity.Normal, rng, multiplier: 0.4, increasedRarity: areaIir, quantityFactor: areaIiq) is { } smallGold) rolled.Add(smallGold);
                break;
        }
        if (rolled.Count == 0) return;
        var stacked = ItemDrops.Stacked(rolled);
        foreach (var drop in stacked)
        {
            var at = player.Layout.Near((chest.X + rng.Next(-3, 4), chest.Y + rng.Next(-3, 4)));
            var ground = new WorldItemObject(at.X, at.Y, drop.Item);
            uint id = player.Instance.Ground.Add(ground, player);
            await SendGroundItem(player, id, ground, $"campaign chest {def.Name} drops {drop.Type.Name}", justDropped: true);
        }
    }
}
