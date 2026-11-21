using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    /// <summary>What a new instance's packs are and where they stand, decided before any is created
    /// (<see cref="PopulateArea"/> creates them). Made for one arrival layout and the character's quest flags of then.</summary>
    sealed record PopulationPlan(AreaLayout Layout, HashSet<string> QuestFlags, PackCatalog.AreaPacks Area,
        List<AreaPopulation.Spawn> Spawns, List<string> Notes, Dictionary<int, (int X, int Y)> LaterForms, double Milliseconds);

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
            || Packs.For(layout.Id) is not { } area || (area.Scattered.Count == 0 && area.Bosses.Count == 0)) return;
        if (layout.Id == "AzmeriLeagueBoss") area = area with { Scattered = [] };
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
        var placedRooms = new List<AreaPopulation.PlacedRoom>();
        var (points, objects, _) = AreaPopulation.FitRooms(layout.Id, player.Seed, notes, "data", placedRooms: placedRooms);
        // A boss whose room has a spawner object for it (The Quay's Tolman) stands at the spawner.
        points.AddRange(AreaPopulation.SpawnerPoints(area, objects, row => Monsters.Find(row.ToString())?.Id));
        // A shrine the level generator placed is guarded (ShrineGuards): its own pack rows when the Shrines row names
        // them, else one of the area's packs at that pack's own count. A shrine too close to an entrance or waypoint is not
        // guarded (the 1.0.0d note). The guards are planned with the area's monsters, so an instance keeps them and a
        // re-entry or an activation never spawns them again.
        var shrineGuards = ServerLevels.For(layout.Id, player.Seed) is { } shrineLevel
            ? ShrinePlacement.Plan(shrineLevel.RoomObjects, walkable, player.Seed)
                .Where(p => !ShrineGuards.TooNearEntrance((p.X, p.Y), (layout.SpawnX, layout.SpawnY),
                    [.. layout.AreaTransitions.Select(t => (t.Transition.GridX, t.Transition.GridY))], AreaPopulation.TransitionClearance))
                .Select(p =>
                {
                    var (rule, packs) = ShrineGuards.Resolve(p.Shrine, MapDevices.IsMapArea(layout.Hash), Packs.Find);
                    return (Shrine: p.Shrine.Id, Rule: rule, Packs: packs, X: p.X, Y: p.Y);
                }).ToList()
            : [];
        // A Trial of Ascendancy's rooms (live 2026-10-31, the original): no monsters among the traps, so the area's
        // scattered packs keep away from them (LabyrinthTrials.TrapClearance); what the room file places stands there:
        // its normal / magic / rare pack points (the Lower Prison trial's plaque platform), its monsters (the Imperial
        // Gardens trial's four Devourers) and its spawners' monsters (LabyrinthTrials.Spawners), rare.
        var trialRooms = placedRooms.Where(r => r.Objects.Any(o => o.Type is LabyrinthTrials.Plaque or LabyrinthTrials.IntroDoor)).ToList();
        // Any other room of a generated layout places its pack points the same way: the rooms the layout gives a magic
        // or rare pack of their own (The Chamber of Innocence: twelve magic packs).
        var layoutPacks = (layout.Level?.PackRooms ?? [])
            .Where(r => !trialRooms.Any(t => t.Path.Equals(r.Path, StringComparison.OrdinalIgnoreCase))).SelectMany(r => r.Points);
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
        var spawns = AreaPopulation.Plan(area, layout.GridW, layout.GridH, walkable, (layout.SpawnX, layout.SpawnY),
            [.. layout.AreaTransitions.Select(t => (t.Transition.GridX, t.Transition.GridY))], points,
            AreaPopulation.PopulationSeed(layout.Hash, player.Seed), Spawnable, notes, scale,
            unassignedAtArrival: MapDevices.IsMapArea(layout.Hash), roomPacks: [.. trialRooms.SelectMany(r => r.Points), .. layoutPacks], shrineGuards: shrineGuards,
            // A shrine's guards must be seen standing: not an emerge variety, which lies buried until the player is near.
            guardSpawnable: row => Spawnable(row) && Monsters.Find(row.ToString()) is { } guardVariety && Emerges.Kind(guardVariety) == EmergeKind.None,
            scatterable: trapClear.Count == 0 ? null : (x, y) => !trapClear.Contains((x, y)),
            additionalPacks: MapScarabPacks.Requests(mapMods),
            ordinaryPacks: mapMods?.Get(Stat.MapMonstrousTreasureNoMonsters) is not > 0);
        // Twinned doubles the area's own bosses, not the shrines' Atlas guards.
        spawns = WithTwinBosses(spawns, mapMods).ToList();
        if (mapMods?.Get(Stat.MapShrinesHaveStandaloneMapBoss) > 0 && ServerLevels.For(layout.Id, player.Seed) is { } shrineRooms)
            spawns.AddRange(MapScarabBosses.Guards(player,
                [.. ShrinePlacement.Plan(shrineRooms.RoomObjects, walkable, player.Seed).Select(p => (p.X, p.Y))], spawns, layout));
        spawns.AddRange(nests);
        foreach (var chest in QuestsOf(player).Chests(layout.Id).Where(r => r.Guards is not null))
            foreach (var room in placedRooms.Where(r => !trialRooms.Contains(r) && r.Objects.Any(o => o.Type == chest.ChestType)))
                foreach (var point in room.Points.Where(p => chest.Guards!.Contains(p.Name)))
                    if (Monsters.Find(point.Name) is { } guard && guard.Id == point.Name && Spawnable(guard.Row))
                        spawns.Add(new AreaPopulation.Spawn(guard.Row, point.X, point.Y, MonsterRarity.Normal, "quest chest guard"));
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
}
