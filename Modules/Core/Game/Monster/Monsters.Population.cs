using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    /// <summary>What a new instance's packs are and where they stand, decided before any is created
    /// (<see cref="PopulateArea"/> creates them). Made for one arrival layout and the character's quest flags of then.</summary>
    sealed record PopulationPlan(AreaLayout Layout, HashSet<string> QuestFlags, PackCatalog.AreaPacks Area,
        List<AreaPopulation.Spawn> Spawns, List<string> Notes, Dictionary<int, (int X, int Y)> LaterForms, double Milliseconds);

    /// <summary>The packs of the instance this player plays: the area's own packs (<see cref="PackCatalog.For"/>) with
    /// this instance's draw from the shared map pool joined to their scattered ones
    /// (<see cref="AreaPopulation.ForInstance"/>). Everything that picks a monster out of "the area's packs" asks here
    /// rather than reading <c>Packs.For(...).Scattered</c>, so the scattered monsters, a strongbox's guards and summons,
    /// an Essence's monster and the like draw from the one set: on a map whose signature list is one single-entry pack
    /// (Forbidden Woods: Vendigo) they were otherwise all that one variety. A pure function of (area, area hash, seed),
    /// so no two callers can disagree; kept for the instance, as the pool is walked once per draw.
    /// Null for an area the pack catalog does not know.</summary>
    public static PackCatalog.AreaPacks? InstancePacks(Player player)
    {
        var layout = player.Layout;
        if (player.Instance is null) return InstancePacks(layout.Id, layout.Hash, player.Seed);
        var cached = InstancePackCache.TryGetValue(player.Instance, out var hit) ? hit : null;
        if (cached is not null && cached.AreaId == layout.Id && cached.Hash == layout.Hash && cached.Seed == player.Seed)
            return cached.Packs;
        var packs = InstancePacks(layout.Id, layout.Hash, player.Seed);
        InstancePackCache.AddOrUpdate(player.Instance, new InstancePackEntry(layout.Id, layout.Hash, player.Seed, packs));
        return packs;
    }

    /// <summary>The packs of an instance by its area, hash and seed (<see cref="InstancePacks(Player)"/>, uncached).</summary>
    public static PackCatalog.AreaPacks? InstancePacks(string areaId, ushort areaHash, uint areaSeed) =>
        Packs.For(areaId) is not { } area ? null
            : AreaPopulation.ForInstance(area, AreaPopulation.PopulationSeed(areaHash, areaSeed), PoolSpawnable);

    sealed record InstancePackEntry(string AreaId, ushort Hash, uint Seed, PackCatalog.AreaPacks? Packs);

    static readonly ConditionalWeakTable<InstanceState, InstancePackEntry> InstancePackCache = new();

    internal static void ClearInstancePackCache()
    {
        InstancePackCache.Clear();
        PopulationsAhead.Clear();
    }

    /// <summary>Whether a pool pack has one variety this server can write. The draw asks no more than that, so every
    /// caller of <see cref="InstancePacks(Player)"/> draws the same packs; a pack's members are filtered again where
    /// they are placed.</summary>
    static bool PoolSpawnable(int row) => Monsters.Find(row.ToString()) is { Supported: true };

    // Plans begun when the area's 0x1d went out (PlanPopulationAhead), one per connection until its 0x7d takes it. A
    // reloaded module starts with none, and its 0x7d plans there.
    static readonly ConditionalWeakTable<Player, Task<PopulationPlan?>> PopulationsAhead = new();

    /// <summary>Plans the area's packs while the client generates the area (from S→C 0x1d to C→S 0x7d, about as long as
    /// the plan takes), so its 0x7d only creates them. Nothing when the area would get no plan there either.</summary>
    public static void PlanPopulationAhead(Player player)
    {
        if (Environment.GetEnvironmentVariable("Game_MONSTERS") == "0" || player.Instance.Monsters is not null
            || CaptureTour.Active(CharacterName(player)) || Monsters.Error is not null) return;
        var layout = ArrivalLayout(player).Layout;
        if (layout.Walkable is null || layout.Id == Sirus.Area
            || Packs.For(layout.Id) is not { } area || (area.Scattered.Count == 0 && area.Pool.Count == 0 && area.Bosses.Count == 0)) return;
        if (layout.Id == "AzmeriLeagueBoss") area = area with { Scattered = [], Pool = [] };
        PopulationsAhead.AddOrUpdate(player, Task.Run(() =>
        {
            try { return PlanPopulation(player, layout, area); }
            catch (Exception ex)
            {
                Console.WriteLine($"[gateway] area {layout.Id} monsters: the plan ahead failed, planned on arrival instead: {ex}");
                return null;
            }
        }));
    }

    // The plan made ahead, when it was made for what the area is now: the arrival that 0x7d settled on and the quest
    // flags (a quest the entry advanced can bring or hold a boss).
    static async Task<PopulationPlan?> PlannedPopulation(Player player, AreaLayout layout)
    {
        if (!PopulationsAhead.TryGetValue(player, out var ahead)) return null;
        PopulationsAhead.Remove(player);
        if (await ahead is not { } plan) return null;
        if (plan.Layout == layout && plan.QuestFlags.SetEquals(player.Character.QuestFlagsSnapshot())) return plan;
        Console.WriteLine($"[gateway] {player.Connection.Remote} area {layout.Id} monsters: planned again on arrival ({(plan.Layout == layout ? "quest flags" : "arrival")} changed)");
        return null;
    }

    // Pure: reads the layout, the catalogs and the connection's quest flags, map and seed, and changes nothing, so it may
    // run on another thread while the client generates the area.
    static PopulationPlan PlanPopulation(Player player, AreaLayout layout, PackCatalog.AreaPacks area)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var questFlags = player.Character.QuestFlagsSnapshot();
        var walkable = layout.Walkable!;
        // Emerge monsters (Gravel Eater, Tide Strider …) are placed waiting and rise near the player (MonsterEmerge.cs).
        var quests = QuestsOf(player);
        bool useWetlandsFallback = false;
        // A boss a quest brings (an NPC that turns into it, a second form, an object's use: Quests.QuestSpawned) is not
        // placed with the area.
        bool Spawnable(int row) => Monsters.Find(row.ToString()) is { Supported: true } v && Emerges.CanPlace(v) && (!quests.QuestSpawned(layout.Id, v.Id) || useWetlandsFallback && v.Id == RyslathaEncounter.Boss);
        // A boss's later form (Abberath2, Shakari2, Malachai's second form, The Depraved Trinity) comes with its fight.
        area = AreaPopulation.LaterForms(area, row => Monsters.Find(row.ToString()) is { } v ? (v.Id, v.Name) : null);
        // Device-recipe phases hold the last WorldAreas boss (Maven's Nucleus, Sirus, Atziri) until the first wave is dead.
        area = MapBossFights.Current.HoldFinal(area, row => Monsters.Find(row.ToString())?.Id);
        // The Ashen Fields' boss depends on the character's Act 2 bandit choice (MapBossOverride).
        area = MapBossOverride.Apply(area, Quests.Names(PlayerQuestFlags(player)), row => Monsters.Find(row.ToString())?.Id);
        var notes = new List<string>();
        // This instance's draw from the shared map pool, before anything reads the area's packs (a no-op where the
        // area has no pool, or where the override above emptied it).
        area = AreaPopulation.ForInstance(area, AreaPopulation.PopulationSeed(layout.Hash, player.Seed), PoolSpawnable, notes);
        var placedRooms = new List<AreaPopulation.PlacedRoom>();
        var (points, objects, capturedRooms) = AreaPopulation.FitRooms(layout.Id, player.Seed, notes, "data", placedRooms: placedRooms);
        // A boss whose room or tile has a spawner object for it (The Quay's Tolman, The Marketplace's Marceus) stands at the spawner.
        points.AddRange(AreaPopulation.SpawnerPoints(area, objects, row => Monsters.Find(row.ToString())?.Id));
        if (layout.Level?.TileBossSpawners is { Count: > 0 } tileSpawners)
            points.AddRange(AreaPopulation.SpawnerPoints(area, tileSpawners, row => Monsters.Find(row.ToString())?.Id));
        // A shrine the level generator placed is guarded (ShrineGuards): its own pack rows when the Shrines row names
        // them, else one of the area's packs at that pack's own count. A shrine too close to an entrance or waypoint is not
        // guarded (the 1.0.0d note). The guards are planned with the area's monsters, so an instance keeps them and a
        // re-entry or an activation never spawns them again.
        var safePoints = layout.AreaTransitions.Select(t => (t.Transition.GridX, t.Transition.GridY)).ToList();
        if (layout.Waypoint is { } wp) safePoints.Add((wp.GridX, wp.GridY));
        var earlyMapMods = MapModsOf(player);
        bool isMapArea = MapDevices.IsMapArea(layout.Hash);
        bool magicShrineGuards = earlyMapMods?.Get(Stat.MapShrinesHaveGuaranteedMagicPack) > 0;
        int extraShrinePackChance = earlyMapMods?.Get(Stat.MapShrinesAdditionalPackChancePct) ?? 0;
        var shrineGuards = ServerLevels.For(layout.Id, player.Seed) is { } shrineLevel
            ? MapShrines.Plan(shrineLevel.RoomObjects, walkable, player.Seed, earlyMapMods)
                .Where(p => !ShrineGuards.TooNearEntrance((p.X, p.Y), (layout.SpawnX, layout.SpawnY),
                    safePoints, AreaPopulation.TransitionClearance))
                .SelectMany(p =>
                {
                    var (rule, packs) = ShrineGuards.Resolve(p.Shrine, isMapArea, Packs.Find, magic: magicShrineGuards);
                    var entry = (Shrine: p.Shrine.Id, Rule: rule, Packs: packs, X: p.X, Y: p.Y);
                    if (isMapArea && extraShrinePackChance > 0
                        && GameServer.Core.World.RandomRolls.Chance(extraShrinePackChance, 100,
                            new Random(HashCode.Combine(player.Seed, p.X, p.Y, 0x53485249))))
                        return new[] { entry, entry };
                    return [entry];
                }).ToList()
            : [];
        // A Trial of Ascendancy's rooms (live 2026-10-31, the original): no monsters among the traps, so the area's
        // scattered packs keep away from them (LabyrinthTrials.TrapClearance); what the room file places stands there:
        // its normal / magic / rare pack points (the Lower Prison trial's plaque platform), its monsters (the Imperial
        // Gardens trial's four Devourers) and its spawners' monsters (LabyrinthTrials.Spawners), rare.
        var trialRooms = placedRooms.Where(r => r.Objects.Any(o => o.Type is LabyrinthTrials.Plaque or LabyrinthTrials.IntroDoor)).ToList();
        // Any other room of a generated layout places its pack points the same way: the rooms the layout gives a magic
        // or rare pack of their own (The Chamber of Innocence: twelve magic packs).
        // A sub-area an ExtraTerrainFeatures row adds is that row's world area; the map's population stays out (the
        // Sacred Grove beside a map: ServerLevels.Level.FeatureAreas).
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
        var layoutPacks = (layout.Level?.PackRooms ?? [])
            .Where(r => !trialRooms.Any(t => t.Path.Equals(r.Path, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(r => r.Points).Where(p => !InFeatureArea(p.X, p.Y));
        // Rooms placed by the generated level that room_point_areas did not index (e.g. Imperial Gardens open_*_boss.arm
        // for Thistlesage's mapboss + %area%-bosspack, and campaign rooms with pre-placed Metadata/Monsters/* guards).
        var capturedSet = new HashSet<string>(capturedRooms, StringComparer.OrdinalIgnoreCase);
        var trialSet = new HashSet<string>(trialRooms.Select(t => t.Path), StringComparer.OrdinalIgnoreCase);
        var layoutDirectMonsters = new List<(string Room, AreaPopulation.RoomPoint Point)>();
        if (layout.Level?.Records is { Count: > 0 } records)
        {
            var (extraPoints, directMonsters) = AreaPopulation.LayoutRoomPoints(records);
            foreach (var ep in extraPoints)
            {
                if (InFeatureArea(ep.X, ep.Y) || ep.Room is { } r && (capturedSet.Contains(r) || trialSet.Contains(r))) continue;
                if (!points.Any(existing => existing.Name == ep.Name && Math.Abs(existing.X - ep.X) <= 3 && Math.Abs(existing.Y - ep.Y) <= 3))
                    points.Add(ep);
            }
            layoutDirectMonsters = directMonsters;
        }
        var trapClear = new HashSet<(int X, int Y)>();
        // The Labyrinth's own rooms keep their packs off their traps too: the generated rooms' and tiles' trap lines.
        var labyrinthTraps = LabyrinthTrials.InLabyrinth(layout.Id) && ServerLevels.For(layout.Id, player.Seed) is { } labyrinthLevel
            ? labyrinthLevel.RoomObjects.Concat(labyrinthLevel.TileLabyrinthObjects).Select(o => (o.Type, o.X, o.Y))
            : [];
        foreach (var trap in trialRooms.SelectMany(r => r.Objects).Select(o => (o.Type, o.X, o.Y)).Concat(labyrinthTraps)
                     .Where(o => o.Type.StartsWith("Metadata/Terrain/Labyrinth/Traps/", StringComparison.Ordinal)))
            for (int dy = -LabyrinthTrials.TrapClearance; dy <= LabyrinthTrials.TrapClearance; dy++)
                for (int dx = -LabyrinthTrials.TrapClearance; dx <= LabyrinthTrials.TrapClearance; dx++)
                    trapClear.Add((trap.X + dx, trap.Y + dy));
        // Where the later forms the area leaves out would stand, for a quest that forms one (Quests.FormsRule).
        var laterForms = AreaPopulation.LaterFormPoints(area, points, walkable);
        foreach (var form in quests.FormPlacements(layout.Id))
            if (points.FirstOrDefault(p => p.Name.Equals(form.BossPoint, StringComparison.OrdinalIgnoreCase)) is { } point
                && AreaPopulation.BossCell(point, walkable) is { } cell
                && Monsters.Find(form.Next) is { } formVariety)
                laterForms[formVariety.Row] = cell;
        var nests = new List<AreaPopulation.Spawn>();
        if (layout.Id == RyslathaEncounter.Area && quests.FormsAfter(layout.Id, RyslathaEncounter.Nest).Count > 0)
        {
            if (RyslathaEncounter.Place(points, walkable, nests) is { } arena)
                laterForms[Monsters.Find(RyslathaEncounter.Boss)!.Row] = arena;
            else
                useWetlandsFallback = true;
        }
        var mapMods = MapModsOf(player);
        var scale = mapMods?.Packs ?? default;
        Func<int, int, bool>? scatterable = trapClear.Count == 0 && featureAreas.Count == 0 ? null
            : (x, y) => !trapClear.Contains((x, y)) && !InFeatureArea(x, y);
        var spawns = AreaPopulation.Plan(area, layout.GridW, layout.GridH, walkable, (layout.SpawnX, layout.SpawnY),
            safePoints, points,
            AreaPopulation.PopulationSeed(layout.Hash, player.Seed), Spawnable, notes, scale,
            unassignedAtArrival: MapDevices.IsMapArea(layout.Hash), roomPacks: [.. trialRooms.SelectMany(r => r.Points), .. layoutPacks], shrineGuards: shrineGuards,
            // A shrine's guards must be seen standing: not an emerge variety, which lies buried until the player is near.
            guardSpawnable: row => Spawnable(row) && Monsters.Find(row.ToString()) is { } guardVariety && Emerges.Kind(guardVariety) == EmergeKind.None,
            scatterable: scatterable,
            localTransitions: [.. layout.AreaTransitions.Where(t => t.Transition.DestinationHash16 == layout.Hash
                    && t.Transition.TeleportTo.HasValue)
                .Select(t => (layout.Near((t.Transition.GridX, t.Transition.GridY)), layout.Near(t.Transition.TeleportTo!.Value)))],
            additionalPacks: [.. MapScarabPacks.Requests(mapMods), .. MapInfluence.Requests(mapMods)],
            ordinaryPacks: mapMods?.Get(Stat.MapMonstrousTreasureNoMonsters) is not > 0,
            grounds: layout.Level?.Grounds);
        // Twinned doubles the area's own bosses, not the shrines' Atlas guards.
        spawns = WithTwinBosses(spawns, mapMods).ToList();
        spawns.AddRange(MapInfluence.Accompany(player, layout, spawns));
        if (mapMods?.Get(Stat.MapShrinesHaveStandaloneMapBoss) > 0 && ServerLevels.For(layout.Id, player.Seed) is { } shrineRooms)
            spawns.AddRange(MapScarabBosses.Guards(player,
                [.. MapShrines.Plan(shrineRooms.RoomObjects, walkable, player.Seed, mapMods).Select(p => (p.X, p.Y))], spawns, layout));
        spawns.AddRange(nests);
        var directPlaced = new HashSet<(int Variety, int X, int Y)>();
        foreach (var chest in QuestsOf(player).Chests(layout.Id).Where(r => r.Guards is not null))
            foreach (var room in placedRooms.Where(r => !trialRooms.Contains(r) && r.Objects.Any(o => o.Type == chest.ChestType)))
                foreach (var point in room.Points.Where(p => chest.Guards!.Contains(p.Name)))
                    if (Monsters.Find(point.Name) is { } guard && guard.Id == point.Name && Spawnable(guard.Row)
                        && directPlaced.Add((guard.Row, point.X, point.Y)))
                        spawns.Add(new AreaPopulation.Spawn(guard.Row, point.X, point.Y, MonsterRarity.Normal, "quest chest guard"));
        // Pre-placed .arm campaign monsters (Act 1..10 campaign areas only, strict allowlist of ordinary room guards):
        // skips towns/hideouts/maps/Labyrinth/league areas, trial rooms, sub-areas, already-captured rooms (prevents
        // 1-cell offset duplication), quest chest guards, and Act 2 bandit guards while BanditChoices holds the camp.
        if (area.Act is >= 1 and <= 10 && !InTown(player) && HideoutCatalog.ByArea(layout.Hash) is null
            && !MapDevices.IsMapArea(layout.Hash) && !LabyrinthTrials.InLabyrinth(layout.Id)
            && (mapMods?.Get(Stat.MapMonstrousTreasureNoMonsters) is not > 0))
        {
            bool banditCampHeld = BanditChoices.In(player) is { } bandit && BanditChoices.Held(player, bandit);
            var preplacedCandidates = placedRooms.Where(r => !trialRooms.Contains(r))
                .SelectMany(r => r.Points.Where(p => p.Name.StartsWith("Metadata/Monsters/", StringComparison.Ordinal)).Select(p => (Room: r.Path, Point: p)))
                .Concat(layoutDirectMonsters.Where(d => !trialSet.Contains(d.Room) && !capturedSet.Contains(d.Room)));
            int directPackId = spawns.Count == 0 ? 1 : spawns.Max(s => s.PackId) + 1;
            var roomDirectPackIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (roomPath, p) in preplacedCandidates)
            {
                if (InFeatureArea(p.X, p.Y) || !IsAllowedCampaignPreplacedMonster(p.Name, banditCampHeld)) continue;
                if (Monsters.Find(p.Name) is not { } v || !v.Id.Equals(p.Name, StringComparison.OrdinalIgnoreCase) || !Spawnable(v.Row)) continue;
                var cell = walkable(p.X, p.Y) ? (X: p.X, Y: p.Y) : layout.Near((p.X, p.Y));
                if (!walkable(cell.X, cell.Y) || !directPlaced.Add((v.Row, cell.X, cell.Y))) continue;
                if (!roomDirectPackIds.TryGetValue(roomPath, out int pid)) roomDirectPackIds[roomPath] = pid = directPackId++;
                spawns.Add(new AreaPopulation.Spawn(v.Row, cell.X, cell.Y, MonsterRarity.Normal, "room guard") { PackId = pid });
            }
        }
        foreach (var room in trialRooms)
            foreach (var p in room.Points)
            {
                bool spawner = LabyrinthTrials.Spawners.TryGetValue(p.Name, out var spawned);
                string? id = spawner ? spawned : p.Name.StartsWith("Metadata/Monsters/", StringComparison.Ordinal) ? p.Name : null;
                if (id is null) continue;
                if (Monsters.Find(id) is { } v && v.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && Spawnable(v.Row))
                    spawns.Add(new AreaPopulation.Spawn(v.Row, p.X, p.Y, spawner ? MonsterRarity.Rare : MonsterRarity.Normal, "trial room"));
                else notes.Add($"trial room {room.Path.Split('/')[^1]}: {p.Name} at ({p.X}, {p.Y}) cannot be created");
            }
        return new(layout, questFlags, area, spawns, notes, laterForms, watch.Elapsed.TotalMilliseconds);
    }

    static readonly HashSet<string> AllowedCampaignRoomGuards = new(StringComparer.OrdinalIgnoreCase)
    {
        "Metadata/Monsters/Axis/AxisCasterGuardGravicius",
        "Metadata/Monsters/Axis/AxisEliteSoldierGuardShieldGravicius",
        "Metadata/Monsters/CrabMonsters/CrabCoconutEmerge",
        "Metadata/Monsters/RootSpiders/RootSpiderHidden",
        "Metadata/Monsters/Totems/TotemMonsterHealthRegen",
        "Metadata/Monsters/Totems/TotemMonsterSpeed",
        "Metadata/Monsters/Totems/TotemSlowPlayer",
        "Metadata/Monsters/CorpseWheel/CorpseWheelAggroCharge",
        "Metadata/Monsters/Bandits/BanditRangedBarragePetrified",
        "Metadata/Monsters/Bandits/BanditRangedTornadoShotPetrified",
        "Metadata/Monsters/Bandits/BanditSwordShieldPetrified",
        "Metadata/Monsters/LeagueHeist/Robot/RobotMannequinRefinery",
    };

    static readonly HashSet<string> AllowedBanditCampGuards = new(StringComparer.OrdinalIgnoreCase)
    {
        "Metadata/Monsters/Bandits/BanditGuardBow",
        "Metadata/Monsters/Bandits/BanditGuardBowBurningArrow",
        "Metadata/Monsters/Bandits/BanditGuardBowExplosiveArrow",
        "Metadata/Monsters/Bandits/BanditGuardBowPoison",
        "Metadata/Monsters/Bandits/BanditGuardMeleeChaos",
        "Metadata/Monsters/Bandits/BanditGuardMeleePuncture",
        "Metadata/Monsters/Bandits/BanditGuardMeleeWarlordsMark",
    };

    static bool IsAllowedCampaignPreplacedMonster(string id, bool banditCampHeld) =>
        AllowedCampaignRoomGuards.Contains(id)
        || (!banditCampHeld && AllowedBanditCampGuards.Contains(id));
}
