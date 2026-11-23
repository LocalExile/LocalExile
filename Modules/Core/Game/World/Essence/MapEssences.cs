using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain.Generation;
using GameServer.Modding;
using static Game;
using static Program;

/// <summary>Retained Essence monoliths, released through the ordinary monster and loot paths.
/// <para>This folder is the encounter as the area holds it: the monolith and its minis, the encased monsters and the
/// panel, corruption, the atlas cluster, the catalogue and the drops. The behaviour a released monster carries — its
/// spell kit, the periodic caster, each essence's own spell, its buffs and the tier scaling — lives in
/// <c>Monster/Mods/</c> beside the other essence monster-mod consumers (EssenceDaemons, EssenceAuras,
/// EssenceDeathEffects). Both halves are partials of this class.</para></summary>
public static partial class MapEssences
{
    public const string Type = "Metadata/MiscellaneousObjects/Monolith", Content = "Essence", Ascent = "Essence.Ascent",
        Calcification = "Essence.Calcification", Stability = "Essence.Stability", Adaptation = "Essence.Adaptation";
    const string SaveKey = "Essence/monoliths";
    public sealed record Monolith(uint Id, int Index, int X, int Y, string Variety, IReadOnlyList<int> Essences,
        byte Stage = 1, uint Monster = 0)
    {
        public string? Source { get; init; }
        public bool Corrupted { get; init; }
        public bool ForcedRelease { get; init; }
        public bool ReleaseEffectsApplied { get; init; }
        /// <summary>The imprisoned pack: small crystals around this one, each holding a magic monster of the same pack.
        /// They share this monolith's essences and break when it releases.</summary>
        public IReadOnlyList<Mini> Minis { get; init; } = [];
        /// <summary>The encased rare's preview monster object (the model/bar/panel in the crystal), 0 until spawned, and the
        /// buff id of its frozen state (re-sent to a player who joins later).</summary>
        public uint Preview { get; init; }
        public ushort PreviewBuff { get; init; }
    }
    /// <summary>A MiniMonolith crystal of a monolith's pack: intact (Stage 1) until the monolith releases, then broken
    /// (Stage 2) with its magic monster spawned (Monster).</summary>
    public sealed record Mini(uint Id, int X, int Y, string Variety, byte Stage = 1, uint Monster = 0)
    {
        public uint Preview { get; init; }
        public ushort PreviewBuff { get; init; }
    }
    sealed class Area
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public List<Monolith>? Monoliths;
        // Each monolith as the area's clients were last shown it.
        public readonly Dictionary<uint, Monolith> Shown = [];
        // The pack crystals the area's clients were shown (MiniMonolith objects): a corruption recreate removes and
        // recreates the main monolith only, so its crystals must not be created a second time.
        public readonly HashSet<uint> ShownMinis = [];
        public readonly HashSet<int> Dirty = [];
        /// <summary>Rage's fire runes (EssenceRage.cs): armed traps on the ground that burst when stepped on.</summary>
        public readonly List<Rune> Runes = [];
    }
    static readonly RuleTable<InstanceState, Area> Areas = new();
    static readonly RuleTable<Player, HashSet<uint>> Presented = new();
    sealed record Snapshot(string Text, List<Monolith> List);
    sealed class Parse { public volatile Snapshot? Last; }
    static readonly RuleTable<InstanceState, Parse> Parsed = new();
    static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Area, int Level), AreaInfo> AreaInfos = new();
    static HashSet<string> TrappedBossAreas = new(StringComparer.Ordinal);

    /// <summary>Loads campaign WorldAreas whose ModsKeys include MapBossTrappedInEssence / MapBossDocksTrappedInEssence.</summary>
    public static void LoadGameData()
    {
        var areas = new HashSet<string>(StringComparer.Ordinal);
        if (ClientTables.Has("WorldAreas") && ClientTables.Has("Mods"))
        {
            var essenceMods = new HashSet<int>();
            foreach (var mod in ClientTables.Table("Mods"))
                if (mod.Text("Id") is "MapBossTrappedInEssence" or "MapBossDocksTrappedInEssence")
                    essenceMods.Add(mod.Index);
            if (essenceMods.Count > 0)
                foreach (var area in ClientTables.Table("WorldAreas"))
                    if (area.Text("Id") is { Length: > 0 } id && !area.Bool("IsTown") && !area.Bool("IsHideout")
                        && area.Ints("ModsKeys").Any(essenceMods.Contains))
                        areas.Add(id);
        }
        TrappedBossAreas = areas;
    }

    public static void Opening(MapOpening request)
    {
        if (request.Campaign || request.Opening is not { } opening || MapEssenceCatalog.Pool(opening.Area.Level).Count == 0) return;
        if (Packs.For(opening.Area.Id)?.Scattered.Any(p => p.Pack.Entries.Any(e =>
            Monsters.Find(e.Variety.ToString()) is { Supported: true })) != true) return;
        request.HandledContent.UnionWith([Content, Ascent, Calcification, Stability, Adaptation]);
        if (opening.Mods.Get(Stat.MapEssenceImprisonedMonstersEnhancedForEachEssence) != 0)
            request.ContentWarnings.Add(("Essence.AdaptationScaling",
                $"area={opening.Area.Id} content=Essence.Adaptation: release transfers an essence modifier; per-essence difficulty/reward enhancement is not implemented"));
        if (opening.Mods.Get(Stat.MapHasMonoliths) > 0 || opening.Mods.Get(Stat.MapRareMonstersTrappedInEssences) > 0
            || opening.Mods.Get(Stat.MapEssencesAre1TierHigherChancePct) > 0)
            request.ContentWarnings.Add(("Essence.Parity",
                $"area={opening.Area.Id} content=Essence: release/corruption/reward lifecycle and monster skills enabled; base essence multiplicity/weights use replica balance"));
    }

    static IEnumerable<Player> Members(Player player) => player.Instance.PlayersExcept(player.Id)
        .Prepend(player)
        .Where(c => !c.Closed && !c.LeavingArea && c.Instance == player.Instance).Distinct();
    static void Keep(Player player, Area area) => player.Instance.ContentState[SaveKey] = JsonSerializer.Serialize(area.Monoliths);

    /// <summary>The level the encounter's monsters and spells are built at: the map's own, else the area's.</summary>
    static int AreaLevel(Player conn) =>
        Math.Clamp(MapRunOf(conn) is { Area.Level: > 0 } map ? map.Area.Level : ServerLevels.AreaLevel(conn.Layout.Id), 1, 100);

    public static int Count(Player player)
    {
        if (player.Instance is null || InTown(player) || HideoutCatalog.ByArea(player.Layout.Hash) is not null || player.Layout.Walkable is null) return 0;
        var map = MapRunOf(player);
        string areaId = map?.Area.Id ?? player.Layout.Id;
        int level = AreaLevel(player);
        var stats = player.Instance.KeptAreaStats ?? new Dictionary<uint, int>();
        // Loading the WorldAreas row costs ~3 ms on every new instance; the row does not change.
        var area = AreaInfos.GetOrAdd((areaId, level), k => AreaInfo.Load(GamePack.Default.TryRead, k.Area, k.Level)) with
        { Stats = stats.ToDictionary(s => (int)s.Key, s => s.Value) };
        int count = ExtraTerrainFeatures.LeagueCount(area, new LevelRandom(player.Seed ^ 0x45535345, false), 6);
        if (map is null && TrappedBossAreas.Contains(areaId)) count = Math.Max(count, 1);
        return count;
    }

    static double Dist((int X, int Y) a, (int X, int Y) b) => Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>The pack's magic monsters trapped in small crystals around the monolith. The group size and ring are the
    /// replica's choice (the official server's pack count is not in the client data); the varieties are the pack's own.
    /// Each crystal takes a random bearing on the ring (radius 10..22) and keeps it only
    /// when the exact cell is walkable for its size, clear of the area's bodies and objects, and far enough from every
    /// crystal already placed that the two never overlap. A candidate that fails is retried at a new bearing, never
    /// snapped to a nearest cell: the snap pulled crystals off the ring and onto each other.</summary>
    static IReadOnlyList<Mini> MakeMinis(Player conn, Monolith monolith, (int Variety, int Weight)[] pool, Random random)
    {
        var minis = new List<Mini>();
        if (pool.Length == 0 || pool.Sum(e => e.Weight) <= 0) return minis;
        int count = random.Next(3, 9);
        var blockers = conn.MonsterPlacementBlockers(0);
        bool Floor(int x, int y) => conn.Layout.WalkableFor(x, y, 2);
        var used = new List<(int X, int Y)> { (monolith.X, monolith.Y) };
        for (int i = 0; i < count; i++)
        {
            (int X, int Y)? at = null;
            for (int attempt = 0; attempt < 32 && at is null; attempt++)
            {
                double angle = random.NextDouble() * 2 * Math.PI;
                int radius = 10 + random.Next(13);
                var want = (monolith.X + (int)Math.Round(Math.Cos(angle) * radius), monolith.Y + (int)Math.Round(Math.Sin(angle) * radius));
                if (!CollisionEscape.Clear(want, conn.Layout.GridW, conn.Layout.GridH, Floor, blockers, 2)) continue;
                if (used.Any(u => Dist(u, want) < 6)) continue;
                at = want;
            }
            if (at is null) continue;
            used.Add(at.Value);
            int roll = random.Next(pool.Sum(e => e.Weight));
            int variety = pool[^1].Variety;
            foreach (var e in pool) if ((roll -= e.Weight) < 0) { variety = e.Variety; break; }
            if (Monsters.Find(variety.ToString()) is not { Supported: true } v) continue;
            minis.Add(new Mini(conn.Instance.World.NewMonsterId(), at.Value.X, at.Value.Y, v.Id));
        }
        return minis;
    }

    /// <summary>Replaces a planned natural rare before it exists; it earns no kill rewards until actually released and killed.
    /// Calcification imprisons every natural rare of the map — scattered, room-point (which draw from the same scattered
    /// packs) and shrine-guard rares alike; only scarab packs and unsupported varieties are left out.</summary>
    public static async Task<bool> Imprison(Player player, AreaPopulation.Spawn spawn)
    {
        if (spawn.Rarity != MonsterRarity.Rare || MapModsOf(player) is not { } mods
            || mods.Get(Stat.MapRareMonstersTrappedInEssences) <= 0
            || MapScarabPacks.Packs.Any(p => p.Id == spawn.Pack)
            || Monsters.Find(spawn.Variety.ToString()) is not { Supported: true } variety) return false;
        await Enter(player);
        var area = Areas.GetValue(player.Instance, _ => new());
        await area.Gate.WaitAsync();
        try
        {
            if (area.Monoliths is not { } monoliths) return false;
            string source = $"{spawn.Pack}/{spawn.Variety}/{spawn.X}/{spawn.Y}";
            if (monoliths.Any(m => m.Source == source)) return true;
            var essences = MapEssenceCatalog.Roll(MapRunOf(player)!.Area.Level, mods,
                new Random(unchecked((int)player.Seed ^ spawn.X * 397 ^ spawn.Y)));
            if (essences.Count == 0) return false;
            bool Floor(int x, int y) => player.Layout.WalkableFor(x, y, 4);
            var blockers = player.MonsterPlacementBlockers(0);
            (int X, int Y)? at = CollisionEscape.Clear((spawn.X, spawn.Y), player.Layout.GridW, player.Layout.GridH, Floor, blockers, 4)
                ? (spawn.X, spawn.Y) : CollisionEscape.Find((spawn.X, spawn.Y), player.Layout.GridW, player.Layout.GridH, Floor, blockers, moverSize: 4);
            if (at is null)
            {
                Console.WriteLine($"[map-content] Calcification area={player.Layout.Id}: no clear monolith cell for natural rare {source}; retaining the rare");
                return false;
            }
            var monolith = new Monolith(player.Instance.World.NewMonsterId(), monoliths.Count, at.Value.X, at.Value.Y,
                variety.Id, essences) { Source = source };
            var weights = (Packs.For(player.Layout.Id)?.Scattered.SelectMany(p => p.Pack.Entries)
                .Where(e => Monsters.Find(e.Variety.ToString()) is { Supported: true })
                .Select(e => (e.Variety, e.Weight)).ToArray()) ?? [];
            monolith = monolith with { Minis = MakeMinis(player, monolith, weights, new Random(unchecked((int)player.Seed ^ spawn.X * 31 ^ spawn.Y))) };
            // Keep the returned record: it carries the encased previews' ids, which the release removes and whose rare
            // the monolith's +0x104 links to for the client's essence panel.
            monolith = await SpawnEncasedPreviews(player, monolith);
            monoliths.Add(monolith);
            Keep(player, area);
            foreach (var member in Members(player)) await Present(member, monolith);
            return true;
        }
        finally { area.Gate.Release(); }
    }

    public readonly record struct MonolithRequest(int X, int Y, string VarietyId, int Level, MapMods Mods);

    /// <summary>Places additional Essence monoliths at explicit coordinates with explicit monster varieties and
    /// per-location <see cref="MapMods"/> (respecting <c>map_essences_are_1_tier_higher_chance_%</c>).</summary>
    public static async Task<int> SpawnMonolithsAt(Player player, IReadOnlyList<MonolithRequest> requests, Random random)
    {
        if (player.Instance is null || requests.Count == 0) return 0;
        var area = Areas.GetValue(player.Instance, _ => new());
        await area.Gate.WaitAsync();
        try
        {
            if (area.Monoliths is null)
            {
                if (player.Instance.ContentState.TryGetValue(SaveKey, out string? saved))
                    area.Monoliths = JsonSerializer.Deserialize<List<Monolith>>(saved) ?? [];
                else
                    area.Monoliths = [];
            }
            var created = new List<Monolith>(requests.Count);
            foreach (var req in requests)
            {
                if (Monsters.Find(req.VarietyId) is not { Supported: true } variety) continue;
                var essences = MapEssenceCatalog.Roll(Math.Clamp(req.Level, 1, 100), req.Mods, random);
                if (essences.Count == 0) continue;
                var monolith = await SpawnEncasedPreviews(player,
                    new Monolith(player.Instance.World.NewMonsterId(), area.Monoliths.Count, req.X, req.Y, variety.Id, essences));
                area.Monoliths.Add(monolith);
                created.Add(monolith);
            }
            if (created.Count > 0)
            {
                Keep(player, area);
                foreach (var member in Members(player))
                    foreach (var monolith in created)
                        await Present(member, monolith);
            }
            return created.Count;
        }
        finally { area.Gate.Release(); }
    }

    /// <summary>Imprisons a rare monster of an explicit variety in an Essence monolith (for areas whose monster
    /// packs come from biomes rather than <c>Packs.For(Layout.Id)</c>).</summary>
    public static async Task<bool> ImprisonVariety(Player player, int x, int y, string varietyId, string source, int level,
        MapMods mods, Random random)
    {
        if (player.Instance is null || Monsters.Find(varietyId) is not { Supported: true } variety) return false;
        var area = Areas.GetValue(player.Instance, _ => new());
        await area.Gate.WaitAsync();
        try
        {
            if (area.Monoliths is null)
            {
                if (player.Instance.ContentState.TryGetValue(SaveKey, out string? saved))
                    area.Monoliths = JsonSerializer.Deserialize<List<Monolith>>(saved) ?? [];
                else
                    area.Monoliths = [];
            }
            if (area.Monoliths.Any(m => m.Source == source)) return true;
            var essences = MapEssenceCatalog.Roll(Math.Clamp(level, 1, 100), mods, random);
            if (essences.Count == 0) return false;
            bool Floor(int cx, int cy) => player.Layout.WalkableFor(cx, cy, 4);
            var blockers = player.MonsterPlacementBlockers(0);
            (int X, int Y)? at = CollisionEscape.Clear((x, y), player.Layout.GridW, player.Layout.GridH, Floor, blockers, 4)
                ? (x, y) : CollisionEscape.Find((x, y), player.Layout.GridW, player.Layout.GridH, Floor, blockers, moverSize: 4);
            if (at is null) return false;
            var monolith = await SpawnEncasedPreviews(player, new Monolith(player.Instance.World.NewMonsterId(), area.Monoliths.Count,
                at.Value.X, at.Value.Y, variety.Id, essences) { Source = source });
            area.Monoliths.Add(monolith);
            Keep(player, area);
            foreach (var member in Members(player)) await Present(member, monolith);
            return true;
        }
        finally { area.Gate.Release(); }
    }

    public static async Task Enter(Player player)
    {
        if (player.Instance is null || InTown(player) || HideoutCatalog.ByArea(player.Layout.Hash) is not null || player.Layout.Walkable is null) return;
        int level = AreaLevel(player);
        var mods = MapModsOf(player) ?? MapMods.None;
        var area = Areas.GetValue(player.Instance, _ => new());
        await area.Gate.WaitAsync();
        try
        {
            if (area.Monoliths is null)
            {
                if (player.Instance.ContentState.TryGetValue(SaveKey, out string? saved))
                    area.Monoliths = JsonSerializer.Deserialize<List<Monolith>>(saved) ?? [];
                else
                {
                    area.Monoliths = [];
                    int count = Count(player);
                    var pool = Packs.For(player.Layout.Id)?.Scattered.SelectMany(p => p.Pack.Entries)
                        .Where(e => Monsters.Find(e.Variety.ToString()) is { Supported: true }).ToArray() ?? [];
                    var random = new Random(unchecked((int)(player.Seed ^ 0x45535345)));
                    var weights = pool.Select(e => (e.Variety, e.Weight)).ToArray();
                    if (pool.Length > 0)
                        foreach (var (x, y) in MapStrongboxes.Positions(player.Layout, player.Seed ^ 0x45535345, count, preferAmbushPoints: false))
                        {
                            var essences = MapEssenceCatalog.Roll(level, mods, random);
                            if (essences.Count == 0) break;
                            int roll = random.Next(pool.Sum(e => e.Weight));
                            int variety = pool[^1].Variety;
                            foreach (var e in pool) if ((roll -= e.Weight) < 0) { variety = e.Variety; break; }
                            var monolith = new Monolith(player.Instance.World.NewMonsterId(), area.Monoliths.Count, x, y,
                                Monsters.Find(variety.ToString())!.Id, essences);
                            monolith = monolith with { Minis = MakeMinis(player, monolith, weights, random) };
                            area.Monoliths.Add(await SpawnEncasedPreviews(player, monolith));
                        }
                    Keep(player, area);
                    if (count > 0) Console.WriteLine($"[map-content] essences area={player.Layout.Id} level={level}: placed {area.Monoliths.Count}/{count}");
                }
            }
            bool respawned = false;
            for (int i = 0; i < area.Monoliths!.Count; i++)
            {
                var monolith = area.Monoliths[i];
                // A save's monoliths keep object ids a rebuilt instance does not: the encased previews are made again.
                var current = await EnsureEncasedPreviews(player, monolith);
                if (!ReferenceEquals(current, monolith)) { area.Monoliths[i] = current; respawned = true; monolith = current; }
                await Present(player, monolith);
                // The entering player's client needs the previews' frozen buff and paused animation again.
                if (monolith.Stage < 4) await PresentEncasedState(player, monolith);
            }
            if (respawned) Keep(player, area);
        }
        finally { area.Gate.Release(); }
    }

    /// <summary>Places a monolith with its imprisoned pack at a cell: a general hand-over a dev command uses to test the
    /// encounter live without rolling a map's essence content. Works in any area with a supported pack.</summary>
    public static async Task<bool> SpawnAt(Player conn, int x, int y, IReadOnlyList<int>? forceEssences = null)
    {
        var area = Areas.GetValue(conn.Instance, _ => new());
        await area.Gate.WaitAsync();
        try
        {
            area.Monoliths ??= [];
            var monoliths = area.Monoliths;
            var pool = Packs.For(conn.Layout.Id)?.Scattered.SelectMany(p => p.Pack.Entries)
                .Where(e => Monsters.Find(e.Variety.ToString()) is { Supported: true }).ToArray() ?? [];
            if (pool.Length == 0 || pool.Sum(e => e.Weight) <= 0) return false;
            var random = new Random(unchecked((int)conn.Seed ^ x * 397 ^ y));
            var essences = forceEssences is { Count: > 0 } ? forceEssences
                : MapEssenceCatalog.Roll((byte)AreaLevel(conn), MapModsOf(conn) ?? MapMods.None, random);
            if (essences.Count == 0) return false;
            int roll = random.Next(pool.Sum(e => e.Weight));
            int variety = pool[^1].Variety;
            foreach (var e in pool) if ((roll -= e.Weight) < 0) { variety = e.Variety; break; }
            var monolith = new Monolith(conn.Instance.World.NewMonsterId(), monoliths.Count, x, y,
                Monsters.Find(variety.ToString())!.Id, essences);
            // The dev test spawns it at state 2: the client's create_state_2 plays stage_01b (the full crystal)
            // instead of state 1's stage_01 + PauseAnimation, which shows only the base ring. Two activations
            // release it (2 → 3 → release), against the natural monolith's three.
            monolith = monolith with { Stage = 2 };
            monolith = monolith with { Minis = MakeMinis(conn, monolith, pool.Select(e => (e.Variety, e.Weight)).ToArray(), random) };
            monolith = await SpawnEncasedPreviews(conn, monolith);
            monoliths.Add(monolith);
            Keep(conn, area);
            foreach (var member in Members(conn)) await Present(member, monolith);
            Console.WriteLine($"[map-content] essence test monolith 0x{monolith.Id:x} at ({x}, {y}) essences={essences.Count} minis={monolith.Minis.Count}");
            return true;
        }
        finally { area.Gate.Release(); }
    }

    static async Task Present(Player player, Monolith monolith, bool replace = false)
    {
        var shown = Presented.GetValue(player, _ => []);
        if (!replace && shown.Contains(monolith.Id)) return;
        if (Monsters.Find(monolith.Variety) is not { } variety) return;
        // The monolith is the area's: created once, and each stage once, for every client.
        var area = Areas.GetOrCreateValue(player.Instance);
        uint[] hashes = [.. monolith.Essences.Select(MapEssenceCatalog.Find).OfType<MapEssenceCatalog.Essence>().Select(e => e.Hash)];
        if (!area.Shown.TryGetValue(monolith.Id, out var was))
        {
            // The imprisoned monster's object id at +0x104: the client builds the monolith's panel (the rare's name and
            // mod lines, then the essences and the helper text) from that object's ObjectMagicProperties.
            uint linked = monolith.Preview != 0 && player.Area.Monsters.ContainsKey(monolith.Preview) ? monolith.Preview : 0;
            var obj = new MonolithObject(monolith.X, monolith.Y, variety.Hash16, hashes, monolith.Stage, linked, monolith.Corrupted);
            await SendWorldCreate(player, monolith.Id,
                InstanceEntities.CreateObject(monolith.Id, InstanceEntities.ObjectTypeHash(Type), obj.Blob()),
                $"essence monolith 0x{monolith.Id:x} stage={monolith.Stage} essences={monolith.Essences.Count} at ({monolith.X}, {monolith.Y})");
            await SendWorldRecord(player, monolith.Id, InstanceProtocol.EnterWakeZone(monolith.Id), null);
            // Set before the crystals: a failure there must not make a second create for the monolith itself.
            area.Shown[monolith.Id] = monolith;
            foreach (var mini in monolith.Minis)
            {
                if (Monsters.Find(mini.Variety) is not { } mv || !area.ShownMinis.Add(mini.Id)) continue;
                // A mini crystal holds a magic monster with no essence list and no linked entity: the main monolith's
                // panel is the only one the client builds.
                var mobj = new MiniMonolithObject(mini.X, mini.Y, mv.Hash16, [], mini.Stage);
                await SendWorldCreate(player, mini.Id,
                    InstanceEntities.CreateObject(mini.Id, InstanceEntities.ObjectTypeHash(MiniMonolithObject.TypePath), mobj.Blob()),
                    $"essence mini monolith 0x{mini.Id:x} stage={mini.Stage} at ({mini.X}, {mini.Y})");
                await SendWorldRecord(player, mini.Id, InstanceProtocol.EnterWakeZone(mini.Id), null);
            }
        }
        else if (!ReferenceEquals(was, monolith))
        {
            await SendWorldRecord(player, monolith.Id, InstanceEntities.TransitionableUpdate(monolith.Id, 4, monolith.Stage), null);
            await SendWorldRecord(player, monolith.Id,
                InstanceEntities.StateMachineUpdate(monolith.Id, 6, MonolithObject.States(monolith.Stage)), null);
            // The pack's crystals break with the monolith (MiniMonolith Transitionable is component index 2).
            if (monolith.Stage >= 4)
                foreach (var mini in monolith.Minis)
                    if (area.ShownMinis.Contains(mini.Id))
                        await SendWorldRecord(player, mini.Id, InstanceEntities.TransitionableUpdate(mini.Id, 2, 2), null);
            area.Shown[monolith.Id] = monolith;
        }
        // It blocks each player's own walk until it is opened.
        if (monolith.Stage < 4) player.SetBlocker(monolith.Id, monolith.X, monolith.Y, 4);
        else player.RemoveBlocker(monolith.Id);
        foreach (var mini in monolith.Minis)
        {
            if (Monsters.Find(mini.Variety) is null) continue;
            if (mini.Stage < 2) player.SetBlocker(mini.Id, mini.X, mini.Y, 2);
            else player.RemoveBlocker(mini.Id);
        }
        shown.Add(monolith.Id);
    }

    public static async Task<bool> Use(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (!Areas.TryGetValue(player.Instance, out var area) || area.Monoliths?.FirstOrDefault(m => m.Id == use.TargetId) is not { } monolith) return false;
        if (player.Closed || player.LeavingArea || player.PlayerLife == 0) return true;
        if (monolith.Stage >= 4)
        {
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null);
            return true;
        }
        // The native .ot overrides its interaction distance to twenty cells.
        if (!TryBeginObjectWalk(player, requestedAt, monolith.X, monolith.Y, 20, out var walk)) return true;
        var (action, now, here, stop, arrive) = walk;
        if (!use.Started)
            await SendWorldRecord(player, player.Id,
                InstanceProtocol.ActionStarted(player.Id, here.X, here.Y, monolith.X, monolith.Y, use.SkillId, use.Sequence, use.Flags,
                    targetObjectId: monolith.Id), null, still: () => PlayerActionCurrent(player, action));
        if (arrive > now) await AreaDelay(player, arrive - now);
        await area.Gate.WaitAsync();
        try
        {
            monolith = area.Monoliths!.First(m => m.Id == use.TargetId);
            if (!PlayerActionCurrent(player, action) || player.PlayerLife == 0 || monolith.Stage >= 4
                || !PlayerWalk.InWalkUpRange(player.Walk.Position(AreaTime(player)), (monolith.X, monolith.Y), 20)) return true;
            if (monolith.Stage == 3)
            {
                await Release(player, area, monolith);
                return true;
            }
            monolith = monolith with { Stage = (byte)(monolith.Stage + 1) };
            area.Monoliths[monolith.Index] = monolith;
            Keep(player, area);
            foreach (var member in Members(player)) await Present(member, monolith, true);
        }
        finally
        {
            area.Gate.Release();
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null,
                still: () => PlayerActionCurrent(player, action));
        }
        return true;
    }

    public static void Drops(DropRoll roll)
    {
        if (roll.Monster.ObjectId == 0) return;
        // Crystal Resonance: a slain map boss drops the essences it inherited from imprisoned monsters.
        BossEssenceDrops(roll);
        if (!roll.Player.Instance.ContentState.TryGetValue(SaveKey, out string? saved)) return;
        // Per kill: parse the snapshot only when Keep wrote a new one.
        var cache = Parsed.GetOrCreateValue(roll.Player.Instance);
        if (cache.Last is not { } last || last.Text != saved)
            cache.Last = last = new(saved, JsonSerializer.Deserialize<List<Monolith>>(saved) ?? []);
        var monolith = last.List.FirstOrDefault(m => m.Monster == roll.Monster.ObjectId && m.Stage == 4);
        if (monolith is null || !roll.Player.Instance.SetState($"Essence/rewards/{monolith.Index}")) return;
        foreach (int row in monolith.Essences)
            if (MapEssenceCatalog.Find(row) is { } essence) roll.Items.Add(essence.Item);
        // Crystal Resonance: the slain released rare hands random essence modifiers to the map boss.
        InheritBossEssences(roll.Player, monolith);
        // Essence Rare Chance (the atlas small nodes atlas_essence_1_1/1_2/1_4/1_5, and the Watchstone/Relic mods):
        // map_essence_monsters_drop_rare_item_with_random_essence_mod_%_chance — the released rare also drops a rare
        // item carrying a random essence's guaranteed mod. Rolled only when the chance hits, so the pool build stays
        // off the per-kill path otherwise.
        if (MapModsOf(roll.Player) is { } mods
            && Random.Shared.Next(100) < mods.Get(Stat.MapEssenceMonstersDropRareItemWithRandomEssenceModPctChance)
            && EssenceModdedRare(roll.Player, roll.Monster.Level, Random.Shared) is { } rare)
            roll.Made.Add(rare);
    }

    /// <summary>A rare item with a random essence's guaranteed mod, as the essence itself would craft it: a random
    /// essence that drops at the monster's level, a random class it crafts, a random droppable base of that class, then
    /// the essence's own reforge (EssenceCrafting.Apply) rolls the rest. Unidentified, like the kill's other equipment.
    /// Null when no essence/base combination fits.</summary>
    static Item? EssenceModdedRare(Player conn, int level, Random random)
    {
        var crafting = EssencesFor(conn);
        var items = ItemsFor(conn);
        var pool = MapEssenceCatalog.Pool(level).Where(e => crafting.All.Any(c => c.Hash == e.Hash)).ToArray();
        if (pool.Length == 0) return null;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var essence = pool[random.Next(pool.Length)];
            var craft = crafting.All.First(c => c.Hash == essence.Hash);
            var classes = craft.ModRows.Keys.ToArray();
            string cls = classes[random.Next(classes.Length)];
            var bases = items.All.Where(t => t.Class == cls && t.DropLevel <= level && ItemDrops.Droppable(t)).ToArray();
            if (bases.Length == 0) continue;
            var chosen = bases[random.Next(bases.Length)];
            var item = ItemCatalog.Create(chosen, random: random) with
            { ItemLevel = (byte)Math.Clamp(level + ItemDrops.ItemLevelBonus(MonsterRarity.Rare), 1, 100) };
            if (crafting.Apply(essence.Hash, item, random) is { } made) return made with { Identified = false };
        }
        return null;
    }

    public static void Describe(Player player, JsonObject state)
    {
        if (Areas.TryGetValue(player.Instance, out var area) && area.Monoliths is not null)
            state["essences"] = JsonSerializer.SerializeToNode(area.Monoliths);
    }
    public static void Leave(Player player)
    {
        Presented.Remove(player);
        LeaveEffects(player);
        // The last player out tears the instance down: its Misery shields must not leave the hot-path counter raised.
        if (!player.Instance.PlayersExcept(player.Id).Any(p => !p.Closed && !p.LeavingArea)) ClearProximityShields(player);
    }
}