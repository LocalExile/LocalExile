using System.Text.Json;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Data.Terrain.Generation;
using GameServer.Modding;

namespace GameServer.Modules.Harvest;

/// <summary>
/// The Sacred Grove of a map: the sub-area the generator adds beside a map that rolled the Harvest encounter
/// (ExtraTerrainFeatures HarvestEncounter, its world area HarvestLeague as a record of the map's own level). Its room
/// (NightGarden garden.arm) holds seven plot sites: per site two crops (an Extractor and its Irrigator) and a Pole and a
/// Storage Tank between them. The grove's state belongs to the instance and is shared by the party: which sites hold
/// crops, each crop's colour and seeds, which crop runs, which withered, and the monsters each crop hatched.
/// The memory-line grove (garden_memory_line.arm: ExtraTerrainFeatures HarvestEncounterMemoryLine in a map, or the whole
/// HarvestLeagueMemoryLine area) has five sites of one crop each and no withering.
/// </summary>
public static class SacredGrove
{
    public const string Content = "SacredGrove";
    public const string ObjectRoot = "Metadata/MiscellaneousObjects/Harvest/";
    const string ExtractorType = ObjectRoot + "Extractor", IrrigatorType = ObjectRoot + "Irrigator", PoleType = ObjectRoot + "Pole",
        TankType = ObjectRoot + "StorageTank", SeedType = ObjectRoot + "MonsterSeed";
    const string SaveKey = "Harvest/grove";
    /// <summary>A module-defined monster stat for HarvestLifeScalingPerLevel's more life (handed over in HarvestModule).</summary>
    public const string LevelMoreLife = "harvest_level_scaling_life_+%_final";
    /// <summary>The monster stat the Doubling scarab's map_harvest_monster_life_+%_final_from_sextant gives.</summary>
    public const string SextantMoreLife = "harvest_monster_life_+%_final_from_sextant";
    internal static readonly string[] FeatureRows = ["HarvestEncounter", MemoryLineFeature];
    const string MemoryLineFeature = "HarvestEncounterMemoryLine";
    /// <summary>The Overgrown Grove's world area (MapDeviceRecipes RandomContentHarvestGrove): the grove is the whole level.</summary>
    const string MemoryLineArea = "HarvestLeagueMemoryLine";

    public enum Phase { Idle, Running, Done, Withered }

    /// <summary>One grove object as it is created: type, place, model, the states set on top of the type's defaults and
    /// its HarvestWorldObject seed list (row, count).</summary>
    public sealed class GroveObject
    {
        public uint Id { get; set; }
        public string Type { get; set; } = "";
        public int X { get; set; }
        public int Y { get; set; }
        public float Rotation { get; set; }
        public string? Model { get; set; }
        public Dictionary<string, int> States { get; set; } = [];
        public List<GroveCrops.SeedCount> Seeds { get; set; } = [];
    }

    public sealed class CropState
    {
        public int Site { get; set; }
        public int Slot { get; set; }
        public int Colour { get; set; }
        public List<GroveCrops.SeedCount> Seeds { get; set; } = [];
        public uint Extractor { get; set; }
        public uint Irrigator { get; set; }
        /// <summary>The seed objects, one per seed, in the order of <see cref="Seeds"/> expanded.</summary>
        public List<uint> SeedObjects { get; set; } = [];
        public Phase Phase { get; set; }
        public List<uint> Monsters { get; set; } = [];
    }

    /// <summary>What the lifeforce and experience rules (<see cref="HarvestLifeforce"/>) need of a hatched monster: its
    /// seed's colour and tier, its crop, and whether it grew from a Faridun seed.</summary>
    public sealed record MonsterTag(int Colour, int Tier, int Crop, bool Faridun = false);

    public sealed class Grove
    {
        public List<CropState> Crops { get; set; } = [];
        public Dictionary<uint, GroveObject> Objects { get; set; } = [];
        public Dictionary<uint, MonsterTag> Monsters { get; set; } = [];
        public int Activations { get; set; }
        public int Level { get; set; }
        /// <summary>The key of the replacement (<see cref="AddReplacement"/>) that took the grove, which then has no crops;
        /// null for a grove of crops.</summary>
        public string? ReplacedBy { get; set; }
    }

    /// <summary>A replaced grove's place: the centre of the grove's sub-area (on a walkable cell) and the area level.</summary>
    public readonly record struct GroveSite((int X, int Y) Centre, int Level);

    /// <summary>A grove replacement (<see cref="AddReplacement"/>).</summary>
    /// <param name="Key">Saved with the grove, so that <paramref name="Place"/> runs again on a later entry.</param>
    /// <param name="Roll">Runs once when the grove is built, before its crops, with the grove's stats and the area's own
    /// Random; true replaces the grove.</param>
    /// <param name="Place">Puts the replacement's content in the replaced grove, on every entry to the area.</param>
    public sealed record Replacement(string Key, Func<ClientState, HarvestStats, Random, bool> Roll, Func<ClientState, GroveSite, Task> Place);

    static Replacement[] Replacements = [];
    static readonly Lock ReplacementsLock = new();

    /// <summary>Lets another module replace a map's Sacred Grove with content of its own. Disposing the result removes
    /// it.</summary>
    public static IDisposable AddReplacement(Replacement replacement)
    {
        lock (ReplacementsLock) Replacements = [.. Replacements, replacement];
        return new Removal(replacement);
    }

    sealed class Removal(Replacement replacement) : IDisposable
    {
        public void Dispose()
        {
            lock (ReplacementsLock) Replacements = [.. Replacements.Where(r => r != replacement)];
        }
    }

    static GroveSite SiteOf(ClientState conn, Grove grove)
    {
        var r = GroveRecords(conn.Layout.Level!).First();
        return new(conn.Layout.Near(((r.X * 2 + r.W) * 23 / 2, (r.Y * 2 + r.H) * 23 / 2)), grove.Level);
    }

    sealed class Area
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public Grove? Grove;
        public HarvestStats Stats = HarvestStats.None;
        public Random Random = new();
        /// <summary>Whether a crop runs: the tick reads only this while nothing does.</summary>
        public volatile bool Running;
    }

    static readonly RuleTable<InstanceState, Area> Areas = new();
    static readonly RuleTable<ClientState, HashSet<uint>> Presented = new();
    // Rulesets "HarvestRules" (row 105): the client's Harvest ruleset, created by S→C 0x1f.
    static readonly Lazy<byte?> HarvestRules = new(() => ClientTables.TableOrEmpty("Rulesets")
        .FirstOrDefault(r => r.Text("Id") == "HarvestRules") is { } r ? (byte)r.Index : null);
    static readonly Lazy<string> Refusal = new(() => ClientTables.TableOrEmpty("ClientStrings")
        .FirstOrDefault(r => r.Text("Id") == "HarvestDisabledDuringCombat") is { Table: not null } row && row.Text("Text") is { } text
        ? text : "You cannot perform that action while a Harvest encounter is in progress.");

    /// <summary>Whether a monster hatched from one of the instance's crops, with its tag and the grove's stats. Runs per kill:
    /// outside a grove instance it costs one table lookup.</summary>
    public static bool TryMonster(ClientState conn, uint id, out MonsterTag tag, out HarvestStats stats)
    {
        tag = null!;
        stats = HarvestStats.None;
        if (conn.Instance is not { } instance || !Areas.TryGetValue(instance, out var area) || area.Grove is not { } grove
            || grove.Monsters.Count == 0) return false;
        lock (grove.Monsters)
            if (!grove.Monsters.TryGetValue(id, out tag!)) return false;
        stats = area.Stats;
        return true;
    }

    public static void Opening(MapOpening request)
    {
        if (!request.Campaign && request.Opening is not null) request.HandledContent.Add(Content);
    }

    /// <summary>The level's grove records (the HarvestEncounter rows' sub-areas), none on any other layout.</summary>
    public static IEnumerable<DoodadRecord> GroveRecords(ServerLevels.Level level) =>
        level.Records.Where(r => r.FeatureRow is { } row && FeatureRows.Contains(row));

    /// <summary>A plot site: its crops (Extractor, Irrigator), two or in a memory-line grove one, and the Pole and Storage
    /// Tank beside them.</summary>
    public sealed record Site(IReadOnlyList<(PlacedTileObject Extractor, PlacedTileObject Irrigator)> Crops, PlacedTileObject? Pole,
        PlacedTileObject? Tank);

    static double Distance(PlacedTileObject a, PlacedTileObject b) => Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y));
    static double Distance((double X, double Y) a, PlacedTileObject b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // Greedy nearest matching: the closest pair first, each left item taking at most one right item, each right item at
    // most `capacity` left items.
    static List<(int Left, int Right)> Match(int left, int right, Func<int, int, double> distance, int capacity = 1)
    {
        var pairs = new List<(int, int, double)>();
        for (int l = 0; l < left; l++)
            for (int r = 0; r < right; r++) pairs.Add((l, r, distance(l, r)));
        var taken = new bool[left];
        var load = new int[right];
        var result = new List<(int, int)>();
        foreach (var (l, r, _) in pairs.OrderBy(p => p.Item3).ThenBy(p => p.Item1).ThenBy(p => p.Item2))
        {
            if (taken[l] || load[r] >= capacity) continue;
            taken[l] = true;
            load[r]++;
            result.Add((l, r));
        }
        return result;
    }

    /// <summary>The grove room's plot sites from its object lines: each Extractor with its nearest Irrigator (about 11
    /// cells apart), each Pole with the two nearest crops and the nearest Storage Tank, in Pole order. Sites without two
    /// crops are left out. With <paramref name="single"/> (the memory-line grove) each Pole takes its one nearest crop.</summary>
    public static List<Site> Sites(IEnumerable<PlacedTileObject> objects, bool single = false)
    {
        var all = objects.ToList();
        var extractors = all.Where(o => o.Type == ExtractorType).ToList();
        var irrigators = all.Where(o => o.Type == IrrigatorType).ToList();
        var poles = all.Where(o => o.Type == PoleType).ToList();
        var tanks = all.Where(o => o.Type == TankType).ToList();
        var crops = Match(extractors.Count, irrigators.Count, (e, i) => Distance(extractors[e], irrigators[i]))
            .OrderBy(p => p.Left).Select(p => (Extractor: extractors[p.Left], Irrigator: irrigators[p.Right])).ToList();
        var middles = crops.Select(c => ((c.Extractor.X + c.Irrigator.X) / 2.0, (c.Extractor.Y + c.Irrigator.Y) / 2.0)).ToList();
        var byPole = Match(crops.Count, poles.Count, (c, p) => Distance(middles[c], poles[p]), capacity: single ? 1 : 2);
        var tankOf = Match(poles.Count, tanks.Count, (p, t) => Distance(poles[p], tanks[t])).ToDictionary(p => p.Left, p => p.Right);
        var sites = new List<Site>();
        for (int p = 0; p < poles.Count; p++)
        {
            var own = byPole.Where(m => m.Right == p).Select(m => crops[m.Left]).ToList();
            if (own.Count == (single ? 1 : 2)) sites.Add(new(own, poles[p], tankOf.TryGetValue(p, out int t) ? tanks[t] : null));
        }
        return sites;
    }

    /// <summary>Where a crop's seeds lie: a grid of 4-cell steps on the Irrigator's side away from its Extractor, nearest
    /// first, on walkable cells not taken by another seed. Fewer cells than seeds put the rest on the Irrigator.</summary>
    public static List<(int X, int Y)> SeedCells(PlacedTileObject extractor, PlacedTileObject irrigator, int count,
        Func<int, int, bool> walkable, HashSet<(int X, int Y)> taken)
    {
        double dx = irrigator.X - extractor.X, dy = irrigator.Y - extractor.Y, length = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
        (double X, double Y) centre = (irrigator.X + dx / length * 10, irrigator.Y + dy / length * 10);
        var candidates = new List<(int X, int Y, double D)>();
        for (int gy = -5; gy <= 5; gy++)
            for (int gx = -5; gx <= 5; gx++)
            {
                int x = (int)Math.Round(centre.X) + gx * 4, y = (int)Math.Round(centre.Y) + gy * 4;
                double toIrrigator = Math.Sqrt((double)(x - irrigator.X) * (x - irrigator.X) + (double)(y - irrigator.Y) * (y - irrigator.Y));
                double toExtractor = Math.Sqrt((double)(x - extractor.X) * (x - extractor.X) + (double)(y - extractor.Y) * (y - extractor.Y));
                if (toIrrigator < 5 || toExtractor < 7 || taken.Contains((x, y)) || !walkable(x, y)) continue;
                candidates.Add((x, y, Math.Sqrt((x - centre.X) * (x - centre.X) + (y - centre.Y) * (y - centre.Y))));
            }
        var cells = candidates.OrderBy(c => c.D).ThenBy(c => c.Y).ThenBy(c => c.X).Take(count).Select(c => (c.X, c.Y)).ToList();
        foreach (var c in cells) taken.Add(c);
        while (cells.Count < count) cells.Add((irrigator.X, irrigator.Y));
        return cells;
    }

    static IEnumerable<int> Expand(IEnumerable<GroveCrops.SeedCount> seeds) => seeds.SelectMany(s => Enumerable.Repeat(s.Row, s.Count));

    /// <summary>Whether the layout is the whole-area Overgrown Grove (no grove sub-area of a map).</summary>
    static bool WholeArea(ClientState conn, ServerLevels.Level level) => conn.Layout.Id == MemoryLineArea && !GroveRecords(level).Any();

    static Grove Build(ClientState conn, HarvestTables tables, HarvestStats stats, int areaLevel, Random random)
    {
        var grove = new Grove { Level = areaLevel };
        var level = conn.Layout.Level!;
        bool whole = WholeArea(conn, level);
        bool memoryLine = whole || stats.BigHarvest || GroveRecords(level).Any(r => r.FeatureRow == MemoryLineFeature);
        var rects = GroveRecords(level).Select(r => (X0: r.X * 23, Y0: r.Y * 23, X1: (r.X + r.W) * 23, Y1: (r.Y + r.H) * 23)).ToList();
        var lines = level.RoomObjects.Where(o => o.Type.StartsWith(ObjectRoot, StringComparison.Ordinal)
            && (whole || rects.Any(r => o.X >= r.X0 && o.X < r.X1 && o.Y >= r.Y0 && o.Y < r.Y1))).ToList();
        var sites = Sites(lines, memoryLine);
        if (!whole && Replacements.FirstOrDefault(r => r.Roll(conn, stats, random)) is { } replacement)
        {
            grove.ReplacedBy = replacement.Key;
            Console.WriteLine($"[harvest] grove area={conn.Layout.Id} level={areaLevel}: replaced by {replacement.Key}, no crops");
            return grove;
        }
        if (tables.Level(areaLevel) is not { } values || sites.Count == 0)
        {
            Console.WriteLine($"[harvest] grove area={conn.Layout.Id}: {sites.Count} plot sites from {lines.Count} lines; no crops");
            return grove;
        }
        var crops = memoryLine ? GroveCrops.RollMemoryLine(random, tables, values, stats, sites.Count)
            : GroveCrops.Roll(random, tables, values, stats, sites.Count);
        var taken = new HashSet<(int X, int Y)>();
        GroveObject Add(string type, PlacedTileObject at, params (string, int)[] states)
        {
            var o = new GroveObject { Id = conn.Instance.World.NewMonsterId(), Type = type, X = at.X, Y = at.Y, Rotation = at.Rotation };
            foreach (var (name, value) in states) o.States[name] = value;
            grove.Objects[o.Id] = o;
            return o;
        }
        foreach (var group in crops.GroupBy(c => c.Site))
        {
            var site = sites[group.Key];
            int first = group.First().Colour;
            if (site.Pole is { } pole) Add(PoleType, pole, ("colour", first));
            if (site.Tank is { } tank) Add(TankType, tank, ("colour", first));
            foreach (var crop in group)
            {
                var (e, i) = site.Crops[crop.Slot];
                var seeds = crop.Seeds.ToList();
                var extractor = Add(ExtractorType, e, ("colour", crop.Colour), ("active", 1));
                var irrigator = Add(IrrigatorType, i, ("colour", crop.Colour), ("plot_colour", crop.Colour));
                extractor.Seeds = irrigator.Seeds = seeds;
                var state = new CropState
                {
                    Site = crop.Site, Slot = crop.Slot, Colour = crop.Colour, Seeds = seeds,
                    Extractor = extractor.Id, Irrigator = irrigator.Id,
                };
                var rows = Expand(seeds).ToList();
                var cells = SeedCells(e, i, rows.Count, (x, y) => conn.Layout.WalkableFor(x, y, 1), taken);
                for (int s = 0; s < rows.Count; s++)
                {
                    var seed = Add(SeedType, e with { X = cells[s].X, Y = cells[s].Y, Rotation = (float)(random.NextDouble() * Math.Tau) },
                        ("colour", SeedColour(tables, rows[s], crop.Colour)));
                    seed.Model = tables.Seeds[rows[s]].Model;
                    seed.Seeds = [new(rows[s], 1)];
                    state.SeedObjects.Add(seed.Id);
                }
                grove.Crops.Add(state);
            }
        }
        Console.WriteLine($"[harvest] grove area={conn.Layout.Id} level={areaLevel}: {sites.Count} sites, "
            + (memoryLine ? $"{crops.Count} single crops: " : $"{crops.Count / 2} pairs: ")
            + string.Join("; ", grove.Crops.Select(c => $"site {c.Site}.{c.Slot} colour {c.Colour} "
                + string.Join(",", c.Seeds.Select(s => $"{s.Count}x{tables.Seeds[s.Row].Id}")))));
        return grove;
    }

    /// <summary>A seed's own colour (a mixed-colour crop holds seeds of every colour), else its crop's.</summary>
    static int SeedColour(HarvestTables tables, int row, int cropColour) =>
        tables.SeedRow(row) is { Colour: >= HarvestTables.Red and <= HarvestTables.Blue } seed ? seed.Colour : cropColour;

    static void Keep(ClientState conn, Grove grove) => conn.Instance.ContentState[SaveKey] = JsonSerializer.Serialize(grove);

    /// <summary>The instance's grove, built on first entry or restored from its saved state (after a module reload);
    /// null on a layout without one. Call under no gate.</summary>
    static async Task<Area?> Load(ClientState conn)
    {
        if (conn.Instance is null || conn.Layout.Level is not { } level || !GroveRecords(level).Any() && !WholeArea(conn, level)
            || MapRunOf(conn) is not { } run) return null;
        var area = Areas.GetOrCreateValue(conn.Instance);
        if (area.Grove is not null) return area;
        await area.Gate.WaitAsync();
        try
        {
            if (area.Grove is not null) return area;
            area.Stats = HarvestStats.From(MapModsOf(conn));
            var grove = conn.Instance.ContentState.TryGetValue(SaveKey, out string? saved) ? JsonSerializer.Deserialize<Grove>(saved) : null;
            area.Random = new Random(unchecked((int)(conn.Seed ^ 0x48525654u) ^ (grove?.Activations ?? 0) * 7919));
            if (grove is null)
            {
                grove = Build(conn, HarvestTables.Current, area.Stats, run.Area.Level, area.Random);
                Keep(conn, grove);
            }
            area.Running = grove.Crops.Any(c => c.Phase == Phase.Running);
            area.Grove = grove;
            return area;
        }
        finally { area.Gate.Release(); }
    }

    internal static IEnumerable<ClientState> Members(ClientState conn) => conn.Instance.PlayersExcept(conn.PeerId)
        .Select(p => p.Owner).OfType<ClientState>().Prepend(conn)
        .Where(p => !p.Closed && !p.LeavingArea && p.Instance == conn.Instance).Distinct();

    internal static (QuestObject Object, HarvestTables.ObjectType Type)? Describe(GroveObject o)
    {
        if (!HarvestTables.Current.Objects.TryGetValue(o.Type, out var type) || type.States is not { } states) return null;
        foreach (var (name, value) in o.States) states = states.Set(name, value);
        var quest = new QuestObject(o.X, o.Y, type.Components, o.Rotation, Model: o.Model)
        {
            StateMachine = states,
            HarvestSeeds = [.. o.Seeds.Select(s => ((byte)s.Row, (byte)s.Count))],
        };
        return QuestObject.CanSerialize(type.Components, true) ? (quest, type) : null;
    }

    /// <summary>The object's footprint as the client's object type gives it (positioned_blocking; the soul tree has none).</summary>
    internal static void Block(ClientState conn, GroveObject o)
    {
        var (size, hull) = StationaryCollision.Resolve(o.Type);
        if (size >= 0) conn.SetBlocker(o.Id, o.X, o.Y, size, hull);
        else conn.RemoveBlocker(o.Id);
    }

    static bool Blocks(GroveObject o) => o.Type != SeedType || o.States.GetValueOrDefault("hatched") == 0 && o.States.GetValueOrDefault("withered") == 0;

    static async Task Present(ClientState conn, GroveObject o)
    {
        var shown = Presented.GetOrCreateValue(conn);
        if (shown.Contains(o.Id) || Describe(o) is not var (quest, _)) return;
        byte[] blob = InstanceEntities.CreateObject(o.Id, InstanceEntities.ObjectTypeHash(o.Type), quest.Blob());
        await SendWorldCreate(conn, o.Id, () => blob, $"grove {o.Type[ObjectRoot.Length..]} 0x{o.Id:x} at ({o.X}, {o.Y})",
            wake: true, awaitBirth: false);
        if (Blocks(o)) Block(conn, o);
        else conn.RemoveBlocker(o.Id);
        shown.Add(o.Id);
    }

    /// <summary>Shows the grove's objects to a connection entering the area (GameEvents.AreaGenerated).</summary>
    public static async Task Enter(ClientState conn)
    {
        if (await Load(conn) is not { Grove: { } grove } area) return;
        // The client's HarvestRules ruleset must exist before an Extractor turns targetable: its label looks the ruleset up
        // once and the client crashes without it (live 2026-11-18).
        if (HarvestRules.Value is byte rules)
        {
            var (remote, stream, ciphers) = conn.Wire;
            await Send(remote, stream, ciphers, InstanceProtocol.AddRuleset(rules), $"0x001f ruleset HarvestRules (row {rules})");
        }
        if (grove.ReplacedBy is { } key)
        {
            if (Replacements.FirstOrDefault(r => r.Key == key) is { } replacement) await replacement.Place(conn, SiteOf(conn, grove));
            else Console.WriteLine($"[harvest] grove area={conn.Layout.Id}: replaced by {key}, which is not loaded; the grove stays empty");
            return;
        }
        GroveObject[] objects;
        await area.Gate.WaitAsync();
        // Copies with their states taken under the gate: a crop starting meanwhile adds state keys (SetStates).
        try
        {
            objects = [.. grove.Objects.Values.Select(o => new GroveObject
                { Id = o.Id, Type = o.Type, X = o.X, Y = o.Y, Rotation = o.Rotation, Model = o.Model, States = new(o.States), Seeds = o.Seeds })];
        }
        finally { area.Gate.Release(); }
        foreach (var o in objects) await Present(conn, o);
    }

    // Sets states on grove objects and sends each changed object's StateMachine (0x2bc) to every member that has it.
    static async Task SetStates(ClientState conn, Grove grove, IEnumerable<(uint Id, string Name, int Value)> changes)
    {
        var changed = new List<GroveObject>();
        foreach (var (id, name, value) in changes)
            if (grove.Objects.TryGetValue(id, out var o))
            {
                o.States[name] = value;
                if (!changed.Contains(o)) changed.Add(o);
            }
        foreach (var member in Members(conn))
        {
            var shown = Presented.GetOrCreateValue(member);
            var (remote, stream, ciphers) = member.Wire;
            foreach (var o in changed)
            {
                if (Describe(o) is not var (quest, type)) continue;
                if (!Blocks(o)) member.RemoveBlocker(o.Id);
                if (!shown.Contains(o.Id)) continue;
                int index = type.Components.ToList().IndexOf("StateMachine");
                await SendWorldRecord(remote, stream, ciphers, member, o.Id,
                    InstanceEntities.StateMachineUpdate(o.Id, (byte)index, quest.StateMachine!), null);
            }
        }
    }

    /// <summary>A click on a crop (its Extractor or its Irrigator): walk up, then start it, unless another crop runs.</summary>
    public static async Task<bool> Use(ClientState conn, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (conn.Instance is null || !Areas.TryGetValue(conn.Instance, out var area) || area.Grove is not { } grove) return false;
        int index = grove.Crops.FindIndex(c => c.Extractor == use.TargetId || c.Irrigator == use.TargetId);
        if (index < 0) return false;
        if (conn.PlayerLife == 0 || conn.Closed || conn.LeavingArea) return true;
        var crop = grove.Crops[index];
        var target = grove.Objects[use.TargetId];
        var (remote, stream, ciphers) = conn.Wire;
        Task End(int action) => SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.ActionEnded(PlayerId), null,
            still: () => PlayerActionCurrent(conn, action));
        if (crop.Phase != Phase.Idle) { await End(conn.ActionCount); return true; }
        if (area.Running)
        {
            await PlayerOf(conn).Tell(Refusal.Value);
            await End(conn.ActionCount);
            return true;
        }
        // The walk stops at the post's footprint, as for an NPC: its blocker size is the reach.
        int reach = Math.Max(0, StationaryCollision.Resolve(target.Type).Size);
        if (!TryBeginObjectWalk(conn, requestedAt, target.X, target.Y, reach, out var walk)) return true;
        var (action, now, here, stop, arrive) = walk;
        if (!use.Started)
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId,
                InstanceProtocol.ActionStarted(PlayerId, here.X, here.Y, target.X, target.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: target.Id), null,
                still: () => PlayerActionCurrent(conn, action));
        if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        await area.Gate.WaitAsync();
        try
        {
            if (crop.Phase != Phase.Idle || area.Running || !PlayerActionCurrent(conn, action) || conn.PlayerLife == 0
                // The walk's stop, not the position now: the delay can end a frame before the last cell.
                || !PlayerWalk.InWalkUpRange(stop, (target.X, target.Y), reach)) return true;
            await Start(conn, area, grove, index);
        }
        finally
        {
            area.Gate.Release();
            await End(action);
        }
        return true;
    }

    /// <summary>The monsters one seed hatches: a Tier 4 seed its one boss; any other seed one, a second at the pack size
    /// chance (always above 100%, then a third at the excess), one more at the additional monster chance, and all of
    /// them twice at the duplicated monster chance.</summary>
    public static int MonsterCount(Random random, HarvestStats stats, int tier)
    {
        if (tier >= 4) return 1;
        int count = 1;
        if (stats.PackSize >= 100) { count++; if (random.Next(100) < stats.PackSize - 100) count++; }
        else if (random.Next(100) < stats.PackSize) count++;
        if (random.Next(100) < stats.AdditionalMonsterChance) count++;
        if (random.Next(100) < stats.DuplicatedMonsterChance) count *= 2;
        return count;
    }

    // Under the area's gate.
    static async Task Start(ClientState conn, Area area, Grove grove, int index)
    {
        var tables = HarvestTables.Current;
        var crop = grove.Crops[index];
        var random = area.Random;
        crop.Phase = Phase.Running;
        area.Running = true;
        grove.Activations++;
        var changes = new List<(uint, string, int)> { (crop.Extractor, "start_encounter", 1) };
        foreach (var other in grove.Crops.Where(c => c != crop))
            changes.AddRange([(other.Extractor, "in_combat", 1), (other.Irrigator, "in_combat", 1)]);
        string withered = "";
        if (grove.Crops.FirstOrDefault(c => c.Site == crop.Site && c != crop) is { Phase: Phase.Idle } pair)
        {
            if (random.Next(100) >= area.Stats.NotWitherChance)
            {
                pair.Phase = Phase.Withered;
                changes.Add((pair.Extractor, "current_state", 4));
                changes.AddRange(pair.SeedObjects.Select(id => (id, "withered", 1)));
                withered = $", site {pair.Site}.{pair.Slot} withered";
            }
            else withered = $", site {pair.Site}.{pair.Slot} kept (not-wither chance {area.Stats.NotWitherChance}%)";
        }
        changes.AddRange(crop.SeedObjects.Select(id => (id, "hatched", 1)));
        Keep(conn, grove);
        Console.WriteLine($"[harvest] {CharacterName(conn)} started site {crop.Site}.{crop.Slot} colour {crop.Colour}{withered}");
        await SetStates(conn, grove, changes);

        byte level = (byte)Math.Clamp(grove.Level, 1, 100);
        var life = new MonsterModSet([], [], new MonsterModEffects(new Dictionary<string, int>
        {
            [LevelMoreLife] = tables.MoreLife(grove.Level),
            [SextantMoreLife] = area.Stats.MonsterMoreLife,
        }.Where(kv => kv.Value != 0).ToDictionary()));
        var (remote, stream, ciphers) = conn.Wire;
        int spawned = 0, unsupported = 0;
        foreach (uint seedId in crop.SeedObjects)
        {
            if (conn.Closed || conn.LeavingArea) break;
            var seedObject = grove.Objects[seedId];
            if (seedObject.Seeds is not [{ } seedRow, ..] || tables.SeedRow(seedRow.Row) is not { } seed) continue;
            if (Monsters.Find(seed.Monster.ToString()) is not { Supported: true } variety) { unsupported++; continue; }
            int count = MonsterCount(random, area.Stats, seed.Tier);
            for (int k = 0; k < count; k++)
            {
                uint id = await SpawnMonster(remote, stream, ciphers, conn, variety, level,
                    seed.Tier >= 4 ? MonsterRarity.Unique : MonsterRarity.Normal, seedObject.X + k % 2 * 3, seedObject.Y + k / 2 * 3,
                    unrisen: false, alone: false, extraMods: life);
                if (id == 0) continue;
                crop.Monsters.Add(id);
                lock (grove.Monsters) grove.Monsters[id] = new(SeedColour(tables, seed.Row, crop.Colour), seed.Tier, index, seed.Faridun);
                spawned++;
            }
        }
        Keep(conn, grove);
        Console.WriteLine($"[harvest] site {crop.Site}.{crop.Slot}: {spawned} monsters hatched"
            + (unsupported > 0 ? $", {unsupported} seeds of unsupported varieties" : ""));
    }

    /// <summary>Completes a running crop whose monsters are all dead (GameEvents.Tick): its Extractor is done, the other
    /// crops can be targeted again. Crop Rotation (the other colours' unstarted seeds rising a tier) is not applied.</summary>
    public static async Task Tick(ClientState conn)
    {
        if (conn.Instance is null) return;
        if (!Areas.TryGetValue(conn.Instance, out var area))
        {
            // After a module reload: the saved grove comes back, and the players already in the area have its objects.
            if (!conn.Instance.ContentState.ContainsKey(SaveKey) || await Load(conn) is not { Grove: { } restored } loaded) return;
            foreach (var member in Members(conn)) Presented.GetOrCreateValue(member).UnionWith(restored.Objects.Keys);
            area = loaded;
        }
        if (!area.Running || area.Grove is not { } grove || conn.Closed || conn.LeavingArea || !area.Gate.Wait(0)) return;
        try
        {
            for (int index = 0; index < grove.Crops.Count; index++)
            {
                var crop = grove.Crops[index];
                if (crop.Phase != Phase.Running) continue;
                lock (conn.Monsters)
                    if (crop.Monsters.Any(id => conn.Monsters.TryGetValue(id, out var m) && !m.Dead && m.Life > 0)) continue;
                await Complete(conn, area, grove, crop);
            }
            area.Running = grove.Crops.Any(c => c.Phase == Phase.Running);
        }
        finally { area.Gate.Release(); }
    }

    static async Task Complete(ClientState conn, Area area, Grove grove, CropState crop)
    {
        crop.Phase = Phase.Done;
        var changes = new List<(uint, string, int)> { (crop.Extractor, "current_state", 4) };
        foreach (var other in grove.Crops.Where(c => c != crop))
            changes.AddRange([(other.Extractor, "in_combat", 0), (other.Irrigator, "in_combat", 0)]);
        Keep(conn, grove);
        Console.WriteLine($"[harvest] site {crop.Site}.{crop.Slot} complete");
        await SetStates(conn, grove, changes);
    }
}
