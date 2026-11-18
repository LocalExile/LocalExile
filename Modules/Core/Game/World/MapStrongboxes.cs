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

/// <summary>Instance-owned strongboxes (the Ambush mechanic): the area's league roll and the Ambush scarabs place them,
/// each with a rarity and chest mods; interaction (a click or a chest-opening warcry) starts their guards; rewards follow
/// the last guard's death (<see cref="StrongboxLoot"/>); currency changes a closed box (StrongboxCurrency.cs).</summary>
public static partial class MapStrongboxes
{
    public const string Content = "Ambush", CountStat = "map_num_extra_strongboxes";
    public const string Reopen = "Ambush.HiddenCompartments", Discernment = "Ambush.Discernment", Containment = "Ambush.Containment",
        Potency = "Ambush.Potency";
    const string SaveKey = "Ambush/boxes";

    /// <param name="Phase">0 closed, 1 opened with its guards alive, 2 looted.</param>
    public sealed record Box(uint Id, int Index, int X, int Y, string Type, int Phase = 0, int Cycle = 0)
    {
        public List<uint> Guards { get; init; } = [];
        public List<AreaPopulation.Spawn> Pending { get; init; } = [];
        /// <summary>Normal 0, magic 1 or rare 2 (<see cref="StrongboxMods.RollRarity"/>).</summary>
        public byte Rarity { get; init; }
        /// <summary>The rolled explicit mods; the type's own (implicit) mods are not kept here.</summary>
        public RolledMod[] Mods { get; init; } = [];
        /// <summary>No currency changes it any more (<c>map_strongboxes_are_corrupted</c>, a Vaal Orb).</summary>
        public bool Corrupted { get; init; }
        /// <summary>Corrupted by a Vaal Orb: its unstackable contents drop corrupted.</summary>
        public bool VaalOrb { get; init; }

        [System.Text.Json.Serialization.JsonIgnore]
        public IReadOnlyList<ItemMod> Explicits => [.. Mods.Select(m => new ItemMod(ModSection.Explicit, m.Hash, m.Values))];
    }

    /// <summary>A rolled mod as the box keeps it: its Mods.dat HASH16 and its values.</summary>
    public sealed record RolledMod(ushort Hash, int[] Values);

    static RolledMod[] Kept(IEnumerable<ItemMod> mods) => [.. mods.Select(m => new RolledMod(m.Hash, [.. m.Values]))];

    sealed class Area
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public List<Box>? Boxes;
    }
    static readonly RuleTable<InstanceState, Area> Areas = new();
    static readonly RuleTable<ClientState, HashSet<uint>> Presented = new();

    public static void Opening(MapOpening request)
    {
        if (request.Campaign || request.Opening is not { } opening
            || !StrongboxCatalog.All.Any(t => StrongboxCatalog.Weight(t, opening.Area.Level, LeagueFor(request.Conn).HardMode, opening.Mods) > 0)) return;
        request.HandledContent.UnionWith([Content, Reopen, Discernment, Containment, Potency]);
        MapScarabPacks.RollFragmentCount(request, CountStat, Random.Shared);
        if (opening.Mods.Get(CountStat) > 0)
            request.ContentWarnings.Add(("Ambush.Affixes",
                $"area={opening.Area.Id} content=Ambush: display-only box mods (traps, ground effects, curses, explosions, extra summons) and unique strongboxes remain unsupported"));
    }

    /// <summary>The area level: the map's, else the campaign area's.</summary>
    static int AreaLevel(ClientState conn) =>
        Math.Clamp(MapRunOf(conn) is { Area.Level: > 0 } map ? map.Area.Level : ServerLevels.AreaLevel(conn.Layout.Id), 1, 100);

    /// <summary>The area's stats by name: the map's (<see cref="MapModsOf"/>), none in the campaign.</summary>
    static MapMods AreaMods(ClientState conn) => MapModsOf(conn) ?? MapMods.None;

    /// <summary>A seed of the strongboxes' own for one purpose, from the instance seed: no generator draw moves.</summary>
    static uint Seed(uint seed, string purpose) => unchecked(seed * 0x9e3779b1u ^ InstanceEntities.ObjectTypeHash("Strongbox/" + purpose));

    /// <summary>The CoreLeagues row of strongboxes ("Ambush"), or −1.</summary>
    static readonly Lazy<int> AmbushRow = new(() =>
    {
        foreach (var row in ClientTables.TableOrEmpty("CoreLeagues"))
            if (row.Text("Id") == "Ambush") return row.Index;
        return -1;
    });

    /// <summary>How many boxes the area gets, and the roll for the log. Strongboxes have no room in the level generator
    /// (the Ambush row has no ExtraTerrainFeatures row): the count is the client's own league roll
    /// (<see cref="ExtraTerrainFeatures.LeagueCount"/>) over the area's WorldAreaLeagueChances row and its area stats, which
    /// add <see cref="CountStat"/> (the Ambush scarabs) and <c>map_leaguestone_override_base_num_strongboxes</c> to the
    /// base and apply the exclusions (<c>map_no_strongboxes</c>, <c>map_disable_strongboxes_from_chance</c>,
    /// <c>map_disable_leagues</c>, <c>atlas_keystone_no_extra_content</c>, …); never fewer than <see cref="CountStat"/>.</summary>
    public static (int Count, string Roll) Count(ClientState conn, int level)
    {
        int extra = AreaMods(conn).Get(CountStat);
        if (AmbushRow.Value < 0) return (extra, "no Ambush row in CoreLeagues");
        AreaInfo area;
        try
        {
            area = AreaInfo.Load(p => GamePack.Default.TryRead(p), conn.Layout.Id, level)
                with { Stats = (conn.Instance?.KeptAreaStats ?? new Dictionary<uint, int>()).ToDictionary(s => (int)s.Key, s => s.Value) };
        }
        catch (Exception ex)
        {
            return (extra, $"area row not read ({ex.GetType().Name}: {ex.Message})");
        }
        var s = ExtraTerrainFeatures.LeagueStruct(area, AmbushRow.Value);
        int count = ExtraTerrainFeatures.LeagueCount(area, new LevelRandom(Seed(conn.Seed, "count")), AmbushRow.Value);
        // Jumbled Abundance: each of its X additional contents is a strongbox, an imprisoned monster, a shrine or a rogue
        // exile. INVENTED split: one in four is a strongbox (no source says how the X are divided). Not when the area
        // excludes strongboxes.
        int jumbled = 0;
        if (s.Max > 0 && AreaMods(conn).Get("map_contains_X_additional_strongboxes_essences_shrines_or_rogue_exiles") is int x and > 0)
        {
            var rng = new Random(unchecked((int)Seed(conn.Seed, "jumbled")));
            for (int i = 0; i < x; i++) if (rng.Next(4) == 0) jumbled++;
        }
        return (Math.Max(count, extra) + jumbled, $"chance {s.C0}%, follow-up {s.C1}%, bonus {s.C2}%, base {s.Base}, most {s.Max}" +
            (jumbled > 0 ? $", Jumbled Abundance +{jumbled}" : ""));
    }

    /// <summary>A box type: an Operative's Strongbox by <c>map_strongboxes_chance_to_be_operatives_%</c> (its weight is 0,
    /// so only this places one), else <see cref="StrongboxCatalog.Roll"/>.</summary>
    static StrongboxCatalog.Definition? Pick(int level, bool hardMode, MapMods mods, Random random)
    {
        if (mods.Get("map_strongboxes_chance_to_be_operatives_%") is int operatives and > 0 && random.Next(100) < operatives
            && StrongboxCatalog.All.FirstOrDefault(t => t.Kind == StrongboxCatalog.Contents.Scarab
                && t.Id.EndsWith("HardMode", StringComparison.Ordinal) == hardMode && level >= t.MinLevel && level <= t.MaxLevel) is { } operative)
            return operative;
        return StrongboxCatalog.Roll(level, hardMode, mods, random);
    }

    public static IReadOnlyList<(int X, int Y)> Positions(AreaLayout layout, uint seed, int count)
    {
        if (count <= 0 || layout.Walkable is not { } walk) return [];
        var reach = AreaPopulation.Reachable(layout.GridW, layout.GridH, walk, (layout.SpawnX, layout.SpawnY));
        if (reach is null) return [];
        var random = new Random(unchecked((int)(seed ^ 0x414d4255)));
        var candidates = new List<(int X, int Y)>();
        for (int y = 3; y < layout.GridH - 3; y += 7)
            for (int x = 3; x < layout.GridW - 3; x += 7)
                if (reach[y * layout.GridW + x] && layout.WalkableFor(x, y, 3)
                    && MapSulphite.Distance((x, y), (layout.SpawnX, layout.SpawnY)) >= 60
                    && !layout.AreaTransitions.Any(t => MapSulphite.Distance((x, y), (t.Transition.GridX, t.Transition.GridY)) < 40))
                    candidates.Add((x, y));
        for (int i = candidates.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }
        var result = new List<(int X, int Y)>();
        foreach (var p in candidates)
        {
            if (result.Count >= count) break;
            if (result.All(other => MapSulphite.Distance(p, other) >= 30)) result.Add(p);
        }
        return result;
    }

    static void Keep(ClientState conn, Area area) => conn.Instance.ContentState[SaveKey] = JsonSerializer.Serialize(area.Boxes);

    static IEnumerable<ClientState> Members(ClientState conn) => conn.Instance.PlayersExcept(conn.PeerId)
        .Select(p => p.Owner).OfType<ClientState>().Prepend(conn)
        .Where(p => !p.Closed && !p.LeavingArea && p.Instance == conn.Instance).Distinct();

    public static async Task Enter(ClientState conn)
    {
        if (conn.Instance is null || InTown(conn) || conn.Layout.Walkable is null) return;
        var area = Areas.GetOrCreateValue(conn.Instance);
        await area.Gate.WaitAsync();
        try
        {
            if (area.Boxes is null)
            {
                if (conn.Instance.ContentState.TryGetValue(SaveKey, out string? saved))
                    area.Boxes = JsonSerializer.Deserialize<List<Box>>(saved) ?? [];
                else
                {
                    area.Boxes = Plan(conn);
                    Keep(conn, area);
                }
            }
            foreach (var box in area.Boxes) await Present(conn, box);
        }
        finally { area.Gate.Release(); }
    }

    static List<Box> Plan(ClientState conn)
    {
        var boxes = new List<Box>();
        int level = AreaLevel(conn);
        var (count, roll) = Count(conn, level);
        if (count <= 0) return boxes;
        var mods = AreaMods(conn);
        var random = new Random(unchecked((int)(conn.Seed ^ 0x53544258)));
        bool hardMode = LeagueFor(conn).HardMode;
        bool corrupted = mods.Get("map_strongboxes_are_corrupted") != 0;
        foreach (var (x, y) in Positions(conn.Layout, conn.Seed, count))
            if (Pick(level, hardMode, mods, random) is { } type)
            {
                byte rarity = type.Rollable ? StrongboxMods.RollRarity(mods, random) : (byte)0;
                var rolled = StrongboxMods.Roll(ItemsFor(conn).Mods, type, rarity, level, mods, random);
                boxes.Add(new(conn.Instance.World.NewMonsterId(), boxes.Count, x, y, type.Id)
                {
                    Rarity = rarity, Mods = Kept(rolled), Corrupted = corrupted,
                });
            }
        Console.WriteLine($"[map-content] strongboxes area={conn.Layout.Id} level={level}{(hardMode ? " (HardMode weights)" : "")}: " +
            $"Ambush roll {roll} → {count}; placed {boxes.Count}" + string.Concat(boxes.Select(b => $"; {Label(conn, b)} 0x{b.Id:x} at ({b.X}, {b.Y})")));
        return boxes;
    }

    static readonly string[] RarityNames = ["Normal", "Magic", "Rare"];

    /// <summary>The box for the log: its rarity, type and mods.</summary>
    static string Label(ClientState conn, Box box)
    {
        var catalog = ItemsFor(conn).Mods;
        string mods = string.Join(", ", box.Mods.Select(m => catalog.ByHash(m.Hash) is { } mod
            ? $"{mod.Id}[{string.Join("/", m.Values)}]" : $"0x{m.Hash:x4}"));
        return $"{RarityNames[Math.Min(box.Rarity, (byte)2)]}{(box.Corrupted ? " corrupted" : "")} {box.Type}" + (mods.Length > 0 ? $" {{{mods}}}" : "");
    }

    static async Task Present(ClientState conn, Box box, bool replace = false)
    {
        var shown = Presented.GetOrCreateValue(conn);
        if (!replace && shown.Contains(box.Id)) return;
        if (StrongboxCatalog.Find(box.Type) is not { } type) return;
        var (remote, stream, ciphers) = conn.Wire;
        if (shown.Contains(box.Id)) await SendWorldRemoval(remote, stream, ciphers, conn, box.Id, null);
        await SendWorldCreate(remote, stream, ciphers, conn, box.Id,
            InstanceEntities.CreateObject(box.Id, InstanceEntities.ObjectTypeHash(type.Id),
                StrongboxCatalog.Blob(type, box.X, box.Y, box.Phase > 0, box.Phase == 2, box.Rarity,
                    box.Mods.Length > 0 ? [.. type.Mods, .. box.Explicits] : null)),
            $"strongbox 0x{box.Id:x} {Label(conn, box)} phase={box.Phase} cycle={box.Cycle} at ({box.X}, {box.Y})");
        await SendWorldRecord(remote, stream, ciphers, conn, box.Id, InstanceProtocol.EnterWakeZone(box.Id), null);
        conn.SetBlocker(box.Id, box.X, box.Y, 1);
        shown.Add(box.Id);
    }

    public static async Task<bool> Use(ClientState conn, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (!Areas.TryGetValue(conn.Instance, out var area) || area.Boxes?.FirstOrDefault(b => b.Id == use.TargetId) is not { } box) return false;
        if (conn.PlayerLife == 0 || conn.Closed || conn.LeavingArea) return true;
        if (box.Phase != 0)
        {
            int current = conn.ActionCount;
            var wire = conn.Wire;
            await SendWorldRecord(wire.Remote, wire.Stream, wire.Ciphers, conn, PlayerId, InstanceProtocol.ActionEnded(PlayerId), null,
                still: () => PlayerActionCurrent(conn, current));
            return true;
        }
        if (!TryBeginObjectWalk(conn, requestedAt, box.X, box.Y, 1, out var walk)) return true;
        var (action, now, here, stop, arrive) = walk;
        var (remote, stream, ciphers) = conn.Wire;
        if (!use.Started)
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId,
                InstanceProtocol.ActionStarted(PlayerId, here.X, here.Y, box.X, box.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: box.Id), null,
                still: () => PlayerActionCurrent(conn, action));
        if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        await area.Gate.WaitAsync();
        try
        {
            box = area.Boxes!.First(b => b.Id == use.TargetId);
            if (box.Phase != 0 || !PlayerActionCurrent(conn, action) || conn.PlayerLife == 0
                || !PlayerWalk.InWalkUpRange(conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds), (box.X, box.Y), 1)) return true;
            await Open(conn, area, box);
        }
        finally
        {
            area.Gate.Release();
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.ActionEnded(PlayerId), null,
                still: () => PlayerActionCurrent(conn, action));
        }
        return true;
    }

    /// <summary>GameEvents.ChestsOpened: a warcry that opens nearby chests opens the closed boxes in its radius.</summary>
    public static async Task OpenNear(ClientState conn, (int X, int Y) at, int radius)
    {
        if (conn.Instance is null || !Areas.TryGetValue(conn.Instance, out var area) || area.Boxes is null) return;
        await area.Gate.WaitAsync();
        try
        {
            foreach (var box in area.Boxes.ToArray())
                if (box.Phase == 0 && (long)(box.X - at.X) * (box.X - at.X) + (long)(box.Y - at.Y) * (box.Y - at.Y) <= (long)radius * radius)
                    await Open(conn, area, box);
        }
        finally { area.Gate.Release(); }
    }

    /// <summary>Opens a closed box (under the area's gate): its guards spawn, and its loot waits for them.</summary>
    static async Task Open(ClientState conn, Area area, Box box)
    {
        bool first = area.Boxes!.All(b => b.Phase == 0 && b.Cycle == 0);
        var guards = Guards(conn, box, first);
        if (guards.Count == 0)
        {
            Console.WriteLine($"[map-content] strongbox {box.Type}: no supported guards or free cells; remains closed");
            return;
        }
        box = box with { Phase = 1, Pending = guards };
        area.Boxes![box.Index] = box;
        Keep(conn, area);
        foreach (var member in Members(conn)) await Present(member, box, true);
        await SpawnPending(conn, area, box);
    }

    public static List<AreaPopulation.Spawn> Guards(ClientState conn, Box box) => Guards(conn, box, false);

    /// <summary>The box's guards: what its mods summon (each mod with <c>summoned_monster_rarity</c>: its
    /// <c>number_of_packs_to_summon</c> packs and <c>number_of_monsters_to_summon</c> single monsters at that rarity; the
    /// implicit ChestSummonNormals is 3 normal packs), one more normal pack by <c>map_strongboxes_additional_pack_chance_%</c>,
    /// and on the instance's first opened box <c>map_first_strongbox_contains_x_additional_rare_monsters</c> rares. Packs
    /// are the area's own scattered packs by weight.</summary>
    public static List<AreaPopulation.Spawn> Guards(ClientState conn, Box box, bool first)
    {
        if (Packs.For(conn.Layout.Id) is not { } packs || StrongboxCatalog.Find(box.Type) is not { } type) return [];
        var pool = packs.Scattered.Where(p => p.Pack.Entries.Count > 0
            && p.Pack.Entries.All(e => Monsters.Find(e.Variety.ToString()) is { Supported: true })).ToList();
        if (pool.Count == 0) return [];
        var mods = AreaMods(conn);
        var random = new Random(unchecked((int)conn.Seed ^ box.Index * 397 ^ box.Cycle * 7919));
        var groups = new List<(int Packs, int Monsters, MonsterRarity Rarity)>();
        foreach (var line in StrongboxMods.Lines(ItemsFor(conn).Mods, type, box.Explicits))
            if (line.Mod.Stats.Any(s => s.Id == "summoned_monster_rarity"))
                groups.Add((StrongboxMods.Value(line, "number_of_packs_to_summon"), StrongboxMods.Value(line, "number_of_monsters_to_summon"),
                    (MonsterRarity)Math.Clamp(StrongboxMods.Value(line, "summoned_monster_rarity"), 0, 2)));
        if (groups.Count == 0) groups.Add((3, 0, MonsterRarity.Normal));
        if (mods.Get("map_strongboxes_additional_pack_chance_%") is int extra and > 0 && random.Next(100) < extra)
            groups.Add((1, 0, MonsterRarity.Normal));
        if (first && mods.Get("map_first_strongbox_contains_x_additional_rare_monsters") is int rares and > 0)
            groups.Add((0, rares, MonsterRarity.Rare));
        PackCatalog.Pack PickPack()
        {
            int roll = random.Next(pool.Sum(p => p.Weight));
            foreach (var p in pool) if ((roll -= p.Weight) < 0) return p.Pack;
            return pool[^1].Pack;
        }
        int total = groups.Sum(g => g.Packs + g.Monsters), spot = 0;
        (int X, int Y) Around(int radius)
        {
            double angle = spot++ * Math.Tau / Math.Max(1, total);
            return (box.X + (int)Math.Round(Math.Cos(angle) * radius), box.Y + (int)Math.Round(Math.Sin(angle) * radius));
        }
        var plans = new List<(PackCatalog.Pack, int, int, MonsterRarity)>();
        var singles = new List<AreaPopulation.Spawn>();
        foreach (var (packCount, monsters, rarity) in groups)
        {
            for (int i = 0; i < packCount; i++)
            {
                var (x, y) = Around(10);
                plans.Add((PickPack(), x, y, rarity));
            }
            for (int i = 0; i < monsters; i++)
            {
                var pack = PickPack();
                var (x, y) = conn.Layout.Near(Around(6));
                singles.Add(new(pack.PickVariety(random), x, y, rarity, pack.Id));
            }
        }
        var occupied = conn.Monsters.Values.Where(m => !m.Dead).Select(m => m.Walk.Position(conn.Clock.Elapsed.TotalSeconds)).Append((box.X, box.Y));
        var spawns = AreaPopulation.AtPacks(plans, (x, y) => conn.Layout.WalkableFor(x, y, 1),
            row => Monsters.Find(row.ToString()) is { Supported: true }, random.Next(), MapModsOf(conn)?.PackSizeFactor ?? 1, occupied);
        spawns.AddRange(singles.Where(s => Monsters.Find(s.Variety.ToString()) is { Supported: true }));
        return spawns;
    }

    public static bool ClosesAgain(int chance, Random random) => random.Next(100) < chance;

    static async Task SpawnPending(ClientState conn, Area area, Box box)
    {
        byte level = (byte)AreaLevel(conn);
        var (remote, stream, ciphers) = conn.Wire;
        // The area's strongbox monster stats (life, damage, speeds, size) and their drops' item quantity.
        var mods = AreaMods(conn);
        int Both(string name) => mods.Get("map_" + name) + mods.Get(name);
        var effects = new Dictionary<string, int>
        {
            ["maximum_life_+%"] = Both("strongbox_monsters_life_+%"), ["damage_+%"] = Both("strongbox_monsters_damage_+%"),
            ["attack_speed_+%"] = Both("strongbox_monsters_attack_speed_+%"),
            ["base_movement_velocity_+%"] = Both("strongbox_monsters_movement_velocity_+%"),
            ["base_actor_scale_+%"] = Both("strongbox_monsters_actor_scale_+%"),
        }.Where(e => e.Value != 0).ToDictionary(e => e.Key, e => e.Value);
        var extraMods = effects.Count == 0 ? null : new MonsterModSet([], [], new MonsterModEffects(effects));
        int quantity = Both("strongbox_monsters_item_quantity_+%");
        while (box.Pending.Count > 0 && !conn.Closed && !conn.LeavingArea)
        {
            var spawn = box.Pending[0];
            if (Monsters.Find(spawn.Variety.ToString()) is not { } variety) return;
            uint id = await SpawnMonster(remote, stream, ciphers, conn, variety, level, spawn.Rarity, spawn.X, spawn.Y,
                unrisen: false, alone: false, extraMods: extraMods, owned: (_, monster) =>
                {
                    monster.Aggro = true;
                    // Item quantity of the guards' own drops, as the Sulphite enrage's (UtilityFlasks.ItemQuantityFactor).
                    if (quantity != 0) monster.EncounterStats = new Dictionary<string, int> { [MapSulphite.EnragedQuantity] = quantity * 100 };
                });
            if (id == 0) return;
            box.Guards.Add(id);
            box.Pending.RemoveAt(0);
            Keep(conn, area);
        }
    }

    public static async Task Tick(ClientState conn)
    {
        if (!Areas.TryGetValue(conn.Instance, out var area) || area.Boxes is not { } boxes || conn.Closed || conn.LeavingArea
            || !boxes.Any(b => b.Phase == 1)) return;
        await area.Gate.WaitAsync();
        try
        {
            foreach (var prior in area.Boxes!.Where(b => b.Phase == 1).ToArray())
            {
                if (prior.Pending.Count > 0) await SpawnPending(conn, area, prior);
                if (prior.Pending.Count > 0 || prior.Guards.Count == 0) continue;
                lock (conn.Monsters)
                    if (prior.Guards.Any(id => conn.Monsters.TryGetValue(id, out var m) && !m.Dead && m.Life > 0)) continue;
                if (!conn.Instance.SetState($"Ambush/rewards/{prior.Index}/{prior.Cycle}")) continue;
                var opened = prior with { Phase = 2 };
                area.Boxes[prior.Index] = opened;
                Keep(conn, area);
                foreach (var member in Members(conn)) await Present(member, opened, true);
                await Rewards(conn, opened);
                int close = AreaMods(conn).Get("map_strongbox_close_after_opening_chance_%")
                    + (StrongboxCatalog.Find(opened.Type) is { } type
                        ? StrongboxMods.Stat(ItemsFor(conn).Mods, type, opened.Explicits, "strongbox_close_after_opening_chance") : 0);
                if (ClosesAgain(close, Random.Shared))
                {
                    var closed = opened with { Phase = 0, Cycle = opened.Cycle + 1, Guards = [] };
                    area.Boxes[prior.Index] = closed;
                    Keep(conn, area);
                    foreach (var member in Members(conn)) await Present(member, closed, true);
                }
            }
        }
        finally { area.Gate.Release(); }
    }

    /// <summary>The box's items (<see cref="StrongboxLoot"/>) on the ground around it.</summary>
    static async Task Rewards(ClientState conn, Box box)
    {
        if (Environment.GetEnvironmentVariable("Game_DROPS") == "0" || !GameEvents.AllowsLoot(conn)
            || StrongboxCatalog.Find(box.Type) is not { } type) return;
        var catalog = ItemsFor(conn).Mods;
        var mods = box.Explicits;
        var items = ItemDrops.Stacked(StrongboxLoot.Roll(conn, type, id => StrongboxMods.Stat(catalog, type, mods, id),
            AreaLevel(conn), AreaMods(conn), Random.Shared, box.VaalOrb));
        foreach (var drop in items)
        {
            var at = conn.Layout.Near((box.X + Random.Shared.Next(-5, 6), box.Y + Random.Shared.Next(-5, 6)));
            var ground = new WorldItemObject(at.X, at.Y, drop.Item);
            uint id = conn.Instance.Ground.Add(ground, conn);
            var (remote, stream, ciphers) = conn.Wire;
            await SendGroundItem(remote, stream, ciphers, conn, id, ground, $"strongbox 0x{box.Id:x} drops {drop.Type.Name}", justDropped: true);
        }
        Console.WriteLine($"[map-content] strongbox 0x{box.Id:x} {Label(conn, box)} ({type.Kind}, {type.Items} base items) dropped {items.Count} item(s)");
    }

    public static void Describe(ClientState conn, JsonObject state)
    {
        if (!Areas.TryGetValue(conn.Instance, out var area) || area.Boxes is null) return;
        state["strongboxes"] = JsonSerializer.SerializeToNode(area.Boxes);
    }

    public static void Leave(ClientState conn) => Presented.Remove(conn);
}
