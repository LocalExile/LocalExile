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
    public const string Content = "Ambush";
    public const uint CountStat = Stat.MapNumExtraStrongboxes;
    public const string Reopen = "Ambush.HiddenCompartments", Discernment = "Ambush.Discernment", Containment = "Ambush.Containment",
        Potency = "Ambush.Potency";
    const string SaveKey = "Ambush/boxes";

    /// <param name="Phase">0 closed, 1 opened with its guards alive, 2 looted.</param>
    public sealed record Box(uint Id, int Index, int X, int Y, string Type, int Phase = 0, int Cycle = 0)
    {
        public List<uint> Guards { get; init; } = [];
        public List<AreaPopulation.Spawn> Pending { get; init; } = [];
        /// <summary>The daemons' later waves (StrongboxDaemons.cs), released into <see cref="Pending"/> when due.</summary>
        public List<Wave> Waves { get; init; } = [];
        /// <summary>Normal 0, magic 1 or rare 2 (<see cref="StrongboxMods.RollRarity"/>).</summary>
        public byte Rarity { get; init; }
        /// <summary>The rolled explicit mods; the type's own (implicit) mods are not kept here.</summary>
        public RolledMod[] Mods { get; init; } = [];
        /// <summary>No currency changes it any more (<c>map_strongboxes_are_corrupted</c>, a Vaal Orb).</summary>
        public bool Corrupted { get; init; }
        /// <summary>The earliest the items drop (UTC ticks): the daemons' finish (<see cref="DropFrames"/>), never
        /// before a summon wave the daemons still owe. The drop still waits for every guard to die. 0 for a box whose
        /// mods do not delay the drop (<c>chest_delay_drops_until_daemons_finish</c>).</summary>
        public long DropAtTicks { get; init; }
        /// <summary>A unique box's UniqueChests Id (<see cref="StrongboxCatalog.Uniques"/>); its mods are in <see cref="Mods"/>.</summary>
        public string? Unique { get; init; }
        /// <summary>Optional pack-pool area id for guard spawns in biome-populated instances where <c>Packs.For(Layout.Id)</c> is empty.</summary>
        public string? PackArea { get; init; }

        [System.Text.Json.Serialization.JsonIgnore]
        public IReadOnlyList<ItemMod> Explicits => [.. Mods.Select(m => new ItemMod(ModSection.Explicit, m.Hash, m.Values))];
    }

    /// <summary>A rolled mod as the box keeps it: its Mods.dat HASH16 and its values.</summary>
    public sealed record RolledMod(ushort Hash, int[] Values);

    /// <summary>The hand-over for "Guarded by N Rogue Exiles" (<c>chest_spawn_rogue_exiles</c>): a module that spawns rogue
    /// exiles (Anarchy) answers with the monster ids of the exiles it created around the point, at the area level; they
    /// guard the box. With no module answering, the mod has no guard of its own.</summary>
    public static readonly GameEvent<Func<Player, (int X, int Y), int, Task<IReadOnlyList<uint>>>> RogueExiles = new();

    /// <summary>A box's chance to be unique, in percent, before <c>map_strongboxes_chance_to_be_unique_+%</c>. From a sample:
    /// poedb's 2016 count of 2218 boxes in tier 13+ maps, 14 unique (0.6%), which poedb notes may be out of date; the
    /// client data gives no rate.</summary>
    public const double UniqueChance = 0.6;

    static RolledMod[] Kept(IEnumerable<ItemMod> mods) => [.. mods.Select(m => new RolledMod(m.Hash, [.. m.Values]))];

    sealed class Area
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public List<Box>? Boxes;
        // Each box as the area's clients were last shown it (its object is the area's, created once per change).
        public readonly Dictionary<uint, Box> Shown = [];
        /// <summary>The closed glow each box wears (box id → effect id and art): the object is the area's, created once
        /// and shown to every client (a per-viewer copy doubled the glow in a party).</summary>
        public readonly Dictionary<uint, (uint Id, string Art)> Worn = [];
        /// <summary>The daemons' ground effects (StrongboxDaemonSpells.cs).</summary>
        public readonly List<Zone> Zones = [];
    }
    static readonly Store<InstanceState, Area> Areas = new();
    static readonly Store<Player, HashSet<uint>> Presented = new();

    public static void Opening(MapOpening request)
    {
        if (request.Campaign || request.Opening is not { } opening
            || !StrongboxCatalog.All.Any(t => StrongboxCatalog.Weight(t, opening.Area.Level, LeagueFor(request.Player).HardMode, opening.Mods) > 0)) return;
        request.HandledContent.UnionWith([Content, Reopen, Discernment, Containment, Potency]);
        MapScarabPacks.RollFragmentCount(request, CountStat, Game.AreaRng(request.Player));
    }

    /// <summary>The area level: the map's, else the campaign area's.</summary>
    static int AreaLevel(Player player) =>
        Math.Clamp(MapRunOf(player) is { Area.Level: > 0 } map ? map.Area.Level : ServerLevels.AreaLevel(player.Layout.Id), 1, 100);

    /// <summary>The area's stats by name: the map's (<see cref="MapModsOf"/>), none in the campaign.</summary>
    static MapMods AreaMods(Player player) => MapModsOf(player) ?? MapMods.None;

    /// <summary>A seed of the strongboxes' own for one purpose, from the instance seed: no generator draw moves.</summary>
    static uint Seed(uint seed, string purpose) => unchecked(seed * 0x9e3779b1u ^ InstanceEntities.ObjectTypeHash("Strongbox/" + purpose));

    /// <summary>The CoreLeagues row of strongboxes ("Ambush"), or −1.</summary>
    static int AmbushRow = -1;

    internal static void LoadGameData()
    {
        AmbushRow = -1;
        foreach (var row in ClientTables.TableOrEmpty("CoreLeagues"))
            if (row.Text("Id") == "Ambush") { AmbushRow = row.Index; break; }
    }

    /// <summary>How many boxes the area gets, and the roll for the log. Strongboxes have no room in the level generator
    /// (the Ambush row has no ExtraTerrainFeatures row): the count is the client's own league roll
    /// (<see cref="ExtraTerrainFeatures.LeagueCount"/>) over the area's WorldAreaLeagueChances row and its area stats, which
    /// add <see cref="CountStat"/> (the Ambush scarabs) and <c>map_leaguestone_override_base_num_strongboxes</c> to the
    /// base and apply the exclusions (<c>map_no_strongboxes</c>, <c>map_disable_strongboxes_from_chance</c>,
    /// <c>map_disable_leagues</c>, <c>atlas_keystone_no_extra_content</c>, …); never fewer than <see cref="CountStat"/>.</summary>
    public static (int Count, string Roll) Count(Player player, int level)
    {
        int extra = AreaMods(player).Get(CountStat);
        if (AmbushRow < 0) return (extra, "no Ambush row in CoreLeagues");
        AreaInfo area;
        try
        {
            area = AreaInfo.Load(p => GamePack.Default.TryRead(p), player.Layout.Id, level)
                with { Stats = (player.Instance?.KeptAreaStats ?? new Dictionary<uint, int>()).ToDictionary(s => (int)s.Key, s => s.Value) };
        }
        catch (Exception ex)
        {
            return (extra, $"area row not read ({ex.GetType().Name}: {ex.Message})");
        }
        var s = ExtraTerrainFeatures.LeagueStruct(area, AmbushRow);
        int count = ExtraTerrainFeatures.LeagueCount(area, new LevelRandom(Seed(player.Seed, "count")), AmbushRow);
        // Jumbled Abundance: each of its X additional contents is a strongbox, an imprisoned monster, a shrine or a rogue
        // exile. INVENTED split: one in four is a strongbox (no source says how the X are divided). Not when the area
        // excludes strongboxes.
        int jumbled = 0;
        if (s.Max > 0 && AreaMods(player).Get(Stat.MapContainsXAdditionalStrongboxesEssencesShrinesOrRogueExiles) is int x and > 0)
        {
            var rng = new Random(unchecked((int)Seed(player.Seed, "jumbled")));
            for (int i = 0; i < x; i++) if (rng.Next(4) == 0) jumbled++;
        }
        return (Math.Max(count, extra) + jumbled, $"chance {s.C0}%, follow-up {s.C1}%, bonus {s.C2}%, base {s.Base}, most {s.Max}" +
            (jumbled > 0 ? $", Jumbled Abundance +{jumbled}" : ""));
    }

    /// <summary>A box type: an Operative's Strongbox by <c>map_strongboxes_chance_to_be_operatives_%</c> (its weight is 0,
    /// so only this places one), else <see cref="StrongboxCatalog.Roll"/>.</summary>
    static StrongboxCatalog.Definition? Pick(int level, bool hardMode, MapMods mods, Random random)
    {
        if (mods.Get(Stat.MapStrongboxesChanceToBeOperativesPct) is int operatives and > 0 && random.Next(100) < operatives
            && StrongboxCatalog.All.FirstOrDefault(t => t.Kind == StrongboxCatalog.Contents.Scarab
                && t.Id.EndsWith("HardMode", StringComparison.Ordinal) == hardMode && level >= t.MinLevel && level <= t.MaxLevel) is { } operative)
            return operative;
        return StrongboxCatalog.Roll(level, hardMode, mods, random);
    }

    public static IReadOnlyList<(int X, int Y)> Positions(AreaLayout layout, uint seed, int count) =>
        Positions(layout, seed, count, preferAmbushPoints: true);

    public static IReadOnlyList<(int X, int Y)> Positions(AreaLayout layout, uint seed, int count, bool preferAmbushPoints)
    {
        if (count <= 0 || layout.Walkable is not { } walk) return [];
        var reach = AreaPopulation.Reachable(layout.GridW, layout.GridH, walk, (layout.SpawnX, layout.SpawnY));
        if (reach is null) return [];
        var random = new Random(unchecked((int)(seed ^ 0x414d4255)));
        var result = new List<(int X, int Y)>();
        // Prefer authored .arm AmbushPoint / AmbushPointSmall markers when present in the generated layout.
        if (preferAmbushPoints && layout.Level?.RoomObjects is { Count: > 0 } roomObjects)
        {
            var markers = new List<(int X, int Y)>();
            foreach (var o in roomObjects)
            {
                if (!o.Type.Equals("Metadata/MiscellaneousObjects/AmbushPoint", StringComparison.OrdinalIgnoreCase)
                    && !o.Type.Equals("Metadata/MiscellaneousObjects/AmbushPointSmall", StringComparison.OrdinalIgnoreCase))
                    continue;
                var spot = (uint)o.X < (uint)layout.GridW && (uint)o.Y < (uint)layout.GridH && layout.WalkableFor(o.X, o.Y, 2)
                    ? (X: o.X, Y: o.Y) : layout.Near((o.X, o.Y));
                if ((uint)spot.X >= (uint)layout.GridW || (uint)spot.Y >= (uint)layout.GridH || !reach[spot.Y * layout.GridW + spot.X]) continue;
                if (MapSulphite.Distance(spot, (layout.SpawnX, layout.SpawnY)) < 45
                    || layout.AreaTransitions.Any(t => MapSulphite.Distance(spot, (t.Transition.GridX, t.Transition.GridY)) < 30))
                    continue;
                markers.Add(spot);
            }
            for (int i = markers.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (markers[i], markers[j]) = (markers[j], markers[i]);
            }
            foreach (var p in markers)
            {
                if (result.Count >= count) break;
                if (result.All(other => MapSulphite.Distance(p, other) >= 30)) result.Add(p);
            }
            if (result.Count >= count) return result;
        }
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
        foreach (var p in candidates)
        {
            if (result.Count >= count) break;
            if (result.All(other => MapSulphite.Distance(p, other) >= 30)) result.Add(p);
        }
        return result;
    }

    static void Keep(Player player, Area area) => player.Instance.ContentState[SaveKey] = JsonSerializer.Serialize(area.Boxes);

    static IEnumerable<Player> Members(Player player) => player.Instance.PlayersExcept(player.Id)
        .Prepend(player)
        .Where(p => !p.Closed && !p.LeavingArea && p.Instance == player.Instance).Distinct();

    /// <summary>A player still in the instance who can carry a deferred presentation send: the opener, else any member.
    /// A callback keyed to the opener must not be lost when the opener leaves within the clip, and the effect objects
    /// and the opened box belong to the area, not to one client.</summary>
    static Player? Survivor(Player player) =>
        player.Instance is null ? null : Members(player).FirstOrDefault(p => p.Connection.PlayerSent);

    public readonly record struct SpawnRequest(int X, int Y, StrongboxCatalog.Contents? Kind, int Level, MapMods Mods, string? PackArea = null);

    static StrongboxCatalog.Definition? PickKind(StrongboxCatalog.Contents kind, int level, bool hardMode, MapMods mods, Random random)
    {
        static bool Exact(StrongboxCatalog.Definition t, StrongboxCatalog.Contents k, bool hm) => k switch
        {
            StrongboxCatalog.Contents.Currency => t.Id == (hm ? "Metadata/Chests/StrongBoxes/ArcanistHardMode" : "Metadata/Chests/StrongBoxes/Arcanist"),
            StrongboxCatalog.Contents.Card => t.Id == "Metadata/Chests/StrongBoxes/StrongboxDivination",
            StrongboxCatalog.Contents.Scarab => t.Id == (hm ? "Metadata/Chests/StrongBoxes/StrongboxScarabHardMode" : "Metadata/Chests/StrongBoxes/StrongboxScarab"),
            _ => t.Kind == k && t.Rollable && t.Id.EndsWith("HardMode", StringComparison.Ordinal) == hm,
        };
        return StrongboxCatalog.All.FirstOrDefault(t => Exact(t, kind, hardMode) && level >= t.MinLevel && level <= t.MaxLevel)
            ?? StrongboxCatalog.All.FirstOrDefault(t => Exact(t, kind, hardMode))
            ?? StrongboxCatalog.All.FirstOrDefault(t => Exact(t, kind, false))
            ?? Pick(level, hardMode, mods, random);
    }

    /// <summary>Places additional strongboxes in the current instance, optionally constrained to a content kind
    /// (Arcanist, Diviner, Operative, etc.), respecting each request's area level and <see cref="MapMods"/>.</summary>
    public static async Task<int> SpawnAt(Player player, IReadOnlyList<SpawnRequest> requests, Random random)
    {
        if (player.Instance is null || requests.Count == 0 || StrongboxCatalog.All.Count == 0) return 0;
        var area = Areas.GetOrCreateValue(player.Instance);
        await area.Gate.WaitAsync();
        try
        {
            if (area.Boxes is null)
            {
                if (player.Instance.ContentState.TryGetValue(SaveKey, out string? saved))
                    area.Boxes = JsonSerializer.Deserialize<List<Box>>(saved) ?? [];
                else
                    area.Boxes = [];
            }
            bool hardMode = LeagueFor(player).HardMode;
            var modCatalog = ItemsFor(player).Mods;
            int added = 0;
            var created = new List<Box>(requests.Count);
            foreach (var req in requests)
            {
                int level = Math.Clamp(req.Level, 1, 100);
                var mods = req.Mods;
                bool corrupted = mods.Get(Stat.MapStrongboxesAreCorrupted) != 0;
                Box box;
                if (req.Kind is null
                    && random.NextDouble() * 100 < UniqueChance * Math.Max(0, 100 + mods.Get(Stat.MapStrongboxesChanceToBeUniquePlusPct)) / 100.0
                    && StrongboxCatalog.RollUnique(level, random) is { } special)
                {
                    box = new(player.Instance.World.TakeObjectId(), area.Boxes.Count, req.X, req.Y, special.Type.Id)
                    {
                        Unique = special.Unique.Id, Rarity = 3, Corrupted = corrupted, PackArea = req.PackArea,
                        Mods = Kept(special.Unique.ModRows.Select(r => modCatalog.Row(r)?.Roll(ModSection.Explicit, random)).OfType<ItemMod>()),
                    };
                }
                else
                {
                    var type = req.Kind is { } k ? PickKind(k, level, hardMode, mods, random) : Pick(level, hardMode, mods, random);
                    if (type is null) continue;
                    byte rarity = type.Rollable ? StrongboxMods.RollRarity(mods, random) : (byte)0;
                    var rolled = StrongboxMods.Roll(modCatalog, type, rarity, level, mods, random);
                    box = new(player.Instance.World.TakeObjectId(), area.Boxes.Count, req.X, req.Y, type.Id)
                    {
                        Rarity = rarity, Mods = Kept(rolled), Corrupted = corrupted, PackArea = req.PackArea,
                    };
                }
                area.Boxes.Add(box);
                created.Add(box);
                added++;
            }
            if (added > 0)
            {
                Keep(player, area);
                foreach (var member in Members(player))
                    foreach (var box in created)
                        await Present(member, box);
            }
            return added;
        }
        finally { area.Gate.Release(); }
    }

    public static async Task Enter(Player player)
    {
        if (player.Instance is null || IsNonCombatHub(player) || player.Layout.Walkable is null) return;
        var area = Areas.GetOrCreateValue(player.Instance);
        await area.Gate.WaitAsync();
        try
        {
            if (area.Boxes is null)
            {
                if (player.Instance.ContentState.TryGetValue(SaveKey, out string? saved))
                    area.Boxes = JsonSerializer.Deserialize<List<Box>>(saved) ?? [];
                else
                {
                    area.Boxes = Plan(player);
                    Keep(player, area);
                }
            }
            foreach (var box in area.Boxes) await Present(player, box);
        }
        finally { area.Gate.Release(); }
    }

    static List<Box> Plan(Player player)
    {
        var boxes = new List<Box>();
        int level = AreaLevel(player);
        var (count, roll) = Count(player, level);
        if (count <= 0)
        {
            Console.WriteLine($"[map-content] strongboxes area={player.Layout.Id} level={level}: Ambush roll {roll} → 0");
            return boxes;
        }
        var mods = AreaMods(player);
        var random = new Random(unchecked((int)(player.Seed ^ 0x53544258)));
        bool hardMode = LeagueFor(player).HardMode;
        bool corrupted = mods.Get(Stat.MapStrongboxesAreCorrupted) != 0;
        var catalog = ItemsFor(player).Mods;
        double unique = UniqueChance * Math.Max(0, 100 + mods.Get(Stat.MapStrongboxesChanceToBeUniquePlusPct)) / 100.0;
        foreach (var (x, y) in Positions(player.Layout, player.Seed, count))
        {
            // map_area_contains_unique_strongbox: the area's first box is unique.
            bool forced = boxes.Count == 0 && mods.Get(Stat.MapAreaContainsUniqueStrongbox) != 0;
            if ((forced || random.NextDouble() * 100 < unique) && StrongboxCatalog.RollUnique(level, random) is { } special)
            {
                boxes.Add(new(player.Instance.World.TakeObjectId(), boxes.Count, x, y, special.Type.Id)
                {
                    Unique = special.Unique.Id, Rarity = 3, Corrupted = corrupted,
                    Mods = Kept(special.Unique.ModRows.Select(r => catalog.Row(r)?.Roll(ModSection.Explicit, random)).OfType<ItemMod>()),
                });
                continue;
            }
            if (Pick(level, hardMode, mods, random) is { } type)
            {
                byte rarity = type.Rollable ? StrongboxMods.RollRarity(mods, random) : (byte)0;
                var rolled = StrongboxMods.Roll(ItemsFor(player).Mods, type, rarity, level, mods, random);
                boxes.Add(new(player.Instance.World.TakeObjectId(), boxes.Count, x, y, type.Id)
                {
                    Rarity = rarity, Mods = Kept(rolled), Corrupted = corrupted,
                });
            }
        }
        Console.WriteLine($"[map-content] strongboxes area={player.Layout.Id} level={level}{(hardMode ? " (HardMode weights)" : "")}: " +
            $"Ambush roll {roll} → {count}; placed {boxes.Count}" + string.Concat(boxes.Select(b => $"; {Label(player, b)} 0x{b.Id:x} at ({b.X}, {b.Y})")));
        return boxes;
    }

    static readonly string[] RarityNames = ["Normal", "Magic", "Rare", "Unique"];

    /// <summary>The box for the log: its rarity, type and mods.</summary>
    static string Label(Player player, Box box)
    {
        var catalog = ItemsFor(player).Mods;
        string mods = string.Join(", ", box.Mods.Select(m => catalog.ByHash(m.Hash) is { } mod
            ? $"{mod.Id}[{string.Join("/", m.Values)}]" : $"0x{m.Hash:x4}"));
        string name = box.Unique is { } unique ? $"{StrongboxCatalog.FindUnique(unique)?.Name ?? unique} ({box.Type})" : box.Type;
        return $"{RarityNames[Math.Min(box.Rarity, (byte)3)]}{(box.Corrupted ? " corrupted" : "")} {name}" + (mods.Length > 0 ? $" {{{mods}}}" : "");
    }

    static async Task Present(Player player, Box box, bool replace = false)
    {
        var shown = Presented.GetOrCreateValue(player);
        if (!replace && shown.Contains(box.Id)) return;
        if (StrongboxCatalog.Find(box.Type) is not { } type) return;
        var area = Areas.GetOrCreateValue(player.Instance);
        if (!area.Shown.TryGetValue(box.Id, out var was) || !ReferenceEquals(was, box))
        {
            if (was is not null)
            {
                int miIdx = type.Components.ToList().IndexOf("MinimapIcon");
                if (miIdx >= 0)
                    await SendWorldRecord(player, box.Id, InstanceEntities.HideMinimapIcon(box.Id, (byte)miIdx), null);
                await SendWorldRemoval(player, box.Id, null);
            }
            // Chest bit 1 is the box's activated flag: the client plays a trap mod's warning sound (chest_play_warning_sound)
            // and drops the nameplate. Set only once the box is open — a closed box created with it showed no label at all
            // (live 2026-11-22).
            byte chestFlags = ChestFlags(player, type, box);
            await SendWorldCreate(player, box.Id,
                InstanceEntities.CreateObject(box.Id, InstanceEntities.ObjectTypeHash(type.Id),
                    StrongboxCatalog.Blob(type, box.X, box.Y, box.Phase > 0, box.Phase > 0, box.Rarity,
                        box.Mods.Length > 0 ? [.. type.Mods, .. box.Explicits] : null, null,
                        box.Unique is { } unique ? StrongboxCatalog.FindUnique(unique) : null, chestFlags, box.Corrupted)),
                $"strongbox 0x{box.Id:x} {Label(player, box)} phase={box.Phase} cycle={box.Cycle} at ({box.X}, {box.Y})");
            await SendWorldRecord(player, box.Id, InstanceProtocol.EnterWakeZone(box.Id), null);
            area.Shown[box.Id] = box;
        }
        // The box blocks each player's own walk.
        player.SetObjectBlocker(box.Id, box.X, box.Y, 1, true);
        shown.Add(box.Id);
        await Wear(player, box);
    }

    /// <summary>The Chest component's extra flags for this box: bit 1 (0x02) once it is open, when one of its mods carries
    /// <c>chest_play_warning_sound</c> — the client plays the trap mod's warning then (and shows no nameplate). A closed
    /// box carries none, whatever its mods.</summary>
    static byte ChestFlags(Player player, StrongboxCatalog.Definition type, Box box) =>
        box.Phase > 0 && StrongboxMods.Stat(ItemsFor(player).Mods, type, box.Explicits, "chest_play_warning_sound") != 0
            ? (byte)0x02 : (byte)0;

    /// <summary>The rarity's looping glow on a closed box, and none on an opened one: the ChestEffects row's
    /// <c>…_Closed_AOFile</c> (white/blue/gold/brown for normal/magic/rare/unique; their default animation is "loop") is
    /// an effect object at the box, replaced or removed whenever the box is presented again. Without it every box looks
    /// alike — the client never wears it by itself, and the base model is the same for all rarities.</summary>
    static async Task Wear(Player player, Box box)
    {
        if (!Areas.TryGetValue(player.Instance, out var area)) return;
        string? art = box.Phase != 0 || StrongboxCatalog.Find(box.Type) is not { } type
            ? null : StrongboxCatalog.ClosedEffect(type, box.Rarity);
        if (area.Worn.TryGetValue(box.Id, out var worn))
        {
            if (worn.Art == art) return;
            area.Worn.Remove(box.Id);
            await SendWorldRemoval(player, worn.Id, null);
        }
        if (art is null) return;
        uint id = player.TakeObjectId();
        await SendWorldCreate(player, id, InstanceEntities.CreateObject(id,
            InstanceEntities.ObjectTypeHash(EffectObject.TypePath), new EffectObject(box.X, box.Y, art).Blob()),
            $"strongbox 0x{box.Id:x} closed glow {art}");
        await SendWorldRecord(player, id, InstanceProtocol.EnterWakeZone(id), null);
        area.Worn[box.Id] = (id, art);
    }

    /// <summary>The client's summoned-monster effect (MiscAnimated <c>SummonedMonsterEffect</c>): a dust column whose
    /// one-shot "animate" clip (4958 ms, `.amd`) starts the spawn sounds, which the client files under the chests'
    /// trap triggers (Audio/…/Chests/TrapTrigger/MonsterSpawn/Spawn_$(#).ogg and Layer_$(#).ogg) — the effect the
    /// official game plays where a chest's monsters appear. Nothing creates it for a monster the server spawns, so the
    /// guards of a strongbox appear with it.</summary>
    const string SummonEffectAo = "Metadata/Effects/Environment/chests/Strongboxes/chestsummon.ao";
    const double SummonEffectSeconds = 5;

    /// <summary>The spawn column and its sound at a cell, removed when its 5 s clip is over.</summary>
    static async Task SummonEffect(Player player, int x, int y)
    {
        uint id = player.TakeObjectId();
        await SendWorldCreate(player, id, InstanceEntities.CreateObject(id,
            InstanceEntities.ObjectTypeHash(EffectObject.TypePath), new EffectObject(x, y, SummonEffectAo).Blob()),
            $"summoned monster effect at ({x}, {y})");
        await SendWorldRecord(player, id, InstanceProtocol.EnterWakeZone(id), null);
        await SendWorldRecord(player, id, InstanceProtocol.AnimationUpdate(id, 0, 0, 1),
            "summoned monster effect: the spawn sounds");
        SendAfter(player, SummonEffectSeconds, async () =>
        {
            if (Survivor(player) is not { } viewer) return;
            await SendWorldRemoval(viewer, id, null);
        });
    }

    /// <summary>The box's "open" clip's length (its .amd: 1166 ms) and a margin: after it the box is presented opened.</summary>
    const double OpenClipSeconds = 1.2;

    /// <summary>How long the open effect object lives: its "openfx" clip is 166 ms (the animation metadata), and the
    /// particle bursts it starts need a little longer to play out.</summary>
    const double OpenEffectSeconds = 2;

    /// <summary>A box's opening, on the object <see cref="Present"/> made. The box's .ao declares one animation, "open"
    /// (Art/Models/Chests/Strongboxes/<c>&lt;box&gt;.amd</c>: <c>1 "open" once 1166</c>), whose SoundEvents are the
    /// model's own lid sounds (Small_Open, Large_Open, Armoury_Open, Ornate_Open, SB_Open, or a unique box's
    /// Kaoms/Malachais/Pendarus). The client's chest code looks the clip up by name when the opened flag changes
    /// (<c>0x141e17130</c>, Chest vtable +0xc0), but live that lookup resolves to nothing for these objects; selecting
    /// clip index 0 fires the sounds. The Chest component itself has no 0x2bc reader (slot 20 is <c>ret</c>), so the
    /// opened flag only reaches the client in a created object: when the clip ends the box is presented again, opened
    /// and without its nameplate. The StateMachine's opened state hides the marker at once (the .ot's
    /// <c>on_or_create_state_opened_1</c>). The ChestEffects row's open effect
    /// (<see cref="StrongboxCatalog.OpenEffect"/>, by rarity) is created at the box and its own default "openfx" clip
    /// started, as the Delve effects start theirs: its SoundEvents are the open swoosh and it starts its particles.</summary>
    static async Task OpenPresentation(Player player, Box box)
    {
        if (StrongboxCatalog.Find(box.Type) is not { } type) return;
        if (type.Components.ToList().IndexOf("StateMachine") is int machine and >= 0 && type.States is { } states)
            await SendWorldRecord(player, box.Id,
                InstanceEntities.StateMachineUpdate(box.Id, (byte)machine, states.Set("started", 1).Set("opened", 1)),
                $"strongbox 0x{box.Id:x} opened state (the marker hides)");
        // The clip itself: the opened flag alone does not start it. Index 0 is the .ao's only animation, "open", and
        // playing it is what fires the model's SoundEvents (live: the lid's creak, 0 s and 0.52 s).
        await SendWorldRecord(player, box.Id, InstanceProtocol.AnimationUpdate(box.Id, 0, 0, 1),
            $"strongbox 0x{box.Id:x} plays its open clip (lid sound)");
        await Wear(player, box);
        // The opened flag only arrives in a created object: once the clip has played, present the box opened, which
        // drops its nameplate (and the marker, should the state script not). Any live member carries the send: the opener
        // may have left within the clip, and the box is the area's.
        SendAfter(player, OpenClipSeconds, async () =>
        {
            if (player.Instance is null) return;
            // The area's shown/worn state is the gate's: the clip's callback runs outside the opening's own gate.
            var area = Areas.GetOrCreateValue(player.Instance);
            await area.Gate.WaitAsync();
            try
            {
                if (Survivor(player) is not { } viewer) return;
                if (area.Boxes is { } boxes && box.Index < boxes.Count && boxes[box.Index] is { } current && current.Id == box.Id)
                    await Present(viewer, current, true);
            }
            finally { area.Gate.Release(); }
        });
        if (StrongboxCatalog.OpenEffect(type, box.Rarity) is not { } art) return;
        uint id = player.TakeObjectId();
        await SendWorldCreate(player, id, InstanceEntities.CreateObject(id,
            InstanceEntities.ObjectTypeHash(EffectObject.TypePath), new EffectObject(box.X, box.Y, art).Blob()),
            $"strongbox 0x{box.Id:x} open effect {art}");
        await SendWorldRecord(player, id, InstanceProtocol.EnterWakeZone(id), null);
        // As the Delve completion effect does: an effect object's own clip must be started for its SoundEvents to fire
        // (its default "openfx" animation's sound is Audio/…/Chests/Strongbox/Effect_$(#).ogg).
        await SendWorldRecord(player, id, InstanceProtocol.AnimationUpdate(id, 0, 0, 1),
            $"strongbox 0x{box.Id:x} open effect sound");
        SendAfter(player, OpenEffectSeconds, async () =>
        {
            if (Survivor(player) is not { } viewer) return;
            await SendWorldRemoval(viewer, id, null);
        });
    }

    public static async Task<bool> Use(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (!Areas.TryGetValue(player.Instance, out var area) || area.Boxes?.FirstOrDefault(b => b.Id == use.TargetId) is not { } box) return false;
        if (player.PlayerLife == 0 || player.Closed || player.LeavingArea) return true;
        if (box.Phase != 0)
        {
            int current = player.ActionCount;
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null,
                still: () => PlayerActionCurrent(player, current));
            return true;
        }
        if (!TryBeginObjectWalk(player, requestedAt, box.X, box.Y, 1, out var walk)) return true;
        var (action, now, here, stop, arrive) = walk;
        if (!use.Started)
            await SendWorldRecord(player, player.Id,
                InstanceProtocol.ActionStarted(player.Id, here.X, here.Y, box.X, box.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: box.Id), null,
                still: () => PlayerActionCurrent(player, action));
        if (arrive > now) await AreaDelay(player, arrive - now);
        await area.Gate.WaitAsync();
        try
        {
            box = area.Boxes!.First(b => b.Id == use.TargetId);
            if (box.Phase != 0 || !PlayerActionCurrent(player, action) || player.PlayerLife == 0
                || !PlayerWalk.InWalkUpRange(player.Walk.Position(AreaTime(player)), (box.X, box.Y), 1)) return true;
            await Open(player, area, box);
        }
        finally
        {
            area.Gate.Release();
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null,
                still: () => PlayerActionCurrent(player, action));
        }
        return true;
    }

    /// <summary>GameEvents.ChestsOpened: a warcry that opens nearby chests opens the closed boxes in its radius.</summary>
    public static async Task OpenNear(Player player, (int X, int Y) at, int radius)
    {
        if (player.Instance is null || !Areas.TryGetValue(player.Instance, out var area) || area.Boxes is null) return;
        await area.Gate.WaitAsync();
        try
        {
            foreach (var box in area.Boxes.ToArray())
                if (box.Phase == 0 && (long)(box.X - at.X) * (box.X - at.X) + (long)(box.Y - at.Y) * (box.Y - at.Y) <= (long)radius * radius)
                    await Open(player, area, box);
        }
        finally { area.Gate.Release(); }
    }

    /// <summary>The frames after the click at which a box's guards arrive, one third of them each time.</summary>
    static readonly int[] GuardWaveFrames = [7, 14, 22];

    const double FramesPerSecond = 30;

    /// <summary>Opens a closed box (under the area's gate): its guards spawn, and its loot drops once every guard is dead
    /// (and, when its mods delay the drop, not before its daemons are gone).</summary>
    static async Task Open(Player player, Area area, Box box)
    {
        bool first = area.Boxes!.All(b => b.Phase == 0 && b.Cycle == 0);
        var now = DateTime.UtcNow;
        var waves = StrongboxCatalog.Find(box.Type) is { } type
            ? SummonWaves(player, type, box, now, new Random(unchecked((int)player.Seed ^ box.Index * 7349 ^ box.Cycle * 13)))
            : [];
        var guards = Guards(player, box, first, waves.Count > 0);
        if (guards.Count == 0 && waves.Count == 0)
        {
            Console.WriteLine($"[map-content] strongbox {box.Type}: no supported guards or free cells; remains closed");
            return;
        }
        // The guards come in three waves, 7, 14 and 22 frames after the click (as in every opened box of the official
        // recordings); Tick releases each and spawns it with its dust columns and spawn sounds.
        var arrivals = new List<Wave>();
        for (int i = 0; i < GuardWaveFrames.Length; i++)
        {
            int size = (guards.Count + GuardWaveFrames.Length - 1 - i) / GuardWaveFrames.Length;
            int from = Enumerable.Range(0, i).Sum(j => (guards.Count + GuardWaveFrames.Length - 1 - j) / GuardWaveFrames.Length);
            if (size > 0) arrivals.Add(new(now.AddSeconds(GuardWaveFrames[i] / FramesPerSecond).Ticks, guards.GetRange(from, size)));
        }
        long dropAt = 0;
        if (StrongboxCatalog.Find(box.Type) is { } dropType
            && StrongboxMods.Stat(ItemsFor(player).Mods, dropType, box.Explicits, "chest_delay_drops_until_daemons_finish") > 0)
        {
            // Never before a summon wave the daemons still owe.
            dropAt = Math.Max(now.AddSeconds(DropFrames(Daemons(player, dropType, box).Select(d => d.Daemon)) / FramesPerSecond).Ticks,
                waves.Select(w => w.AtTicks).DefaultIfEmpty(0).Max());
        }
        box = ReleaseWaves(box with { Phase = 1, Pending = [], DropAtTicks = dropAt, Waves = [.. arrivals.Concat(waves).OrderBy(w => w.AtTicks)] }, now);
        area.Boxes![box.Index] = box;
        Keep(player, area);
        // The object the client has stays while its clip plays; OpenPresentation presents it opened once the clip is over.
        // One call: world objects are the area's (created once and shown to every client), so the open effect must not
        // be made once per member.
        await OpenPresentation(player, box);
        await SpawnPending(player, area, box);
        if (StrongboxCatalog.Find(box.Type) is { } exiled
            && StrongboxMods.Stat(ItemsFor(player).Mods, exiled, box.Explicits, "chest_spawn_rogue_exiles") is int exiles and > 0)
        {
            var ids = new List<uint>();
            foreach (var spawn in RogueExiles.For(player)) ids.AddRange(await spawn(player, (box.X, box.Y), exiles));
            box.Guards.AddRange(ids);
            Keep(player, area);
            Console.WriteLine($"[map-content] strongbox 0x{box.Id:x}: {exiles} rogue exile(s) asked for, {ids.Count} spawned" +
                (RogueExiles.Any ? "" : " (no module spawns rogue exiles)"));
        }
        if (StrongboxCatalog.Find(box.Type) is { } opened)
            // After the lid's creak, as the guards: a daemon's spell at 0 s buried it under the skill's own sound. A
            // live member of the instance runs them: the trap daemons must not be skipped when the opener left by then.
            SendAfter(player, OpenClipSeconds, () =>
            {
                if (Survivor(player) is { } viewer)
                    CastDaemons(viewer, area, opened, box, new Random(unchecked((int)viewer.Seed ^ box.Index * 9973 ^ box.Cycle * 31)));
                return Task.CompletedTask;
            });
    }

    public static List<AreaPopulation.Spawn> Guards(Player player, Box box) => Guards(player, box, false);

    /// <summary>The box's guards: what its mods summon (each mod with <c>summoned_monster_rarity</c>: its
    /// <c>number_of_packs_to_summon</c> packs and <c>number_of_monsters_to_summon</c> single monsters at that rarity; the
    /// implicit ChestSummonNormals is 3 normal packs), one more normal pack by <c>map_strongboxes_additional_pack_chance_%</c>,
    /// and on the instance's first opened box <c>map_first_strongbox_contains_x_additional_rare_monsters</c> rares. Packs
    /// are the instance's own scattered packs by weight (<see cref="InstancePacks(Player)"/>).</summary>
    public static List<AreaPopulation.Spawn> Guards(Player player, Box box, bool first) => Guards(player, box, first, false);

    /// <param name="summoned">The box's daemons bring guards of their own: no stand-in packs.</param>
    static List<AreaPopulation.Spawn> Guards(Player player, Box box, bool first, bool summoned)
    {
        var packs = (box.PackArea is { Length: > 0 } ? Packs.For(box.PackArea) : null) ?? InstancePacks(player);
        if (packs is null || StrongboxCatalog.Find(box.Type) is not { } type) return [];
        var pool = packs.Scattered.Where(p => p.Pack.Entries.Count > 0
            && p.Pack.Entries.All(e => Monsters.Find(e.Variety.ToString()) is { Supported: true })).ToList();
        if (pool.Count == 0) return [];
        var mods = AreaMods(player);
        var random = new Random(unchecked((int)player.Seed ^ box.Index * 397 ^ box.Cycle * 7919));
        var groups = new List<(int Packs, int Monsters, MonsterRarity Rarity)>();
        foreach (var line in StrongboxMods.Lines(ItemsFor(player).Mods, type, box.Explicits))
            if (line.Mod.Stats.Any(s => s.Id == Stat.SummonedMonsterRarity))
                groups.Add((StrongboxMods.Value(line, "number_of_packs_to_summon"), StrongboxMods.Value(line, "number_of_monsters_to_summon"),
                    (MonsterRarity)Math.Clamp(StrongboxMods.Value(line, "summoned_monster_rarity"), 0, 2)));
        // A warband cache's warband (chest_display_summons_<element>_warband).
        var warband = new List<AreaPopulation.Spawn>();
        foreach (var line in StrongboxMods.Lines(ItemsFor(player).Mods, type, box.Explicits))
            foreach (var (stat, id) in Warbands)
                if (StrongboxMods.Value(line, stat) > 0) warband.AddRange(Warband(player, id, box, random));
        // No mod summons anything the server spawns (the stream daemons: a Strange Barrel's spiders, Weylam's pirates,
        // the Renegades' warband, which has no WarbandsPackMonsters row): the implicit three normal packs stand in.
        if (groups.Sum(g => g.Packs + g.Monsters) == 0 && warband.Count == 0 && !summoned) groups = [(3, 0, MonsterRarity.Normal)];
        if (mods.Get(Stat.MapStrongboxesAdditionalPackChancePct) is int extra and > 0 && random.Next(100) < extra)
            groups.Add((1, 0, MonsterRarity.Normal));
        if (first && mods.Get(Stat.MapFirstStrongboxContainsXAdditionalRareMonsters) is int rares and > 0)
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
                var (x, y) = player.Layout.Near(Around(6));
                singles.Add(new(pack.PickVariety(random), x, y, rarity, pack.Id));
            }
        }
        double now = AreaTime(player);
        var occupied = player.Area.Monsters.Values.Where(m => !m.Dead).Select(m => m.Walk.Position(now)).Append((box.X, box.Y));
        var spawns = AreaPopulation.AtPacks(plans, (x, y) => player.Layout.WalkableFor(x, y, 1),
            row => Monsters.Find(row.ToString()) is { Supported: true }, random.Next(), MapModsOf(player)?.PackSizeFactor ?? 1, occupied);
        spawns.AddRange(singles.Where(s => Monsters.Find(s.Variety.ToString()) is { Supported: true }));
        spawns.AddRange(warband);
        return spawns;
    }

    static readonly (string Stat, string Warband)[] Warbands =
    [
        ("chest_display_summons_fire_warband", "WarbandsFire1"), ("chest_display_summons_cold_warband", "WarbandsCold1"),
        ("chest_display_summons_lightning_warband", "WarbandsLightning1"),
    ];

    /// <summary>A warband around the box: a WarbandsPackNumbers row for the area level by its SpawnChance, its count of each
    /// tier (each with its chance; the grunts always), each member a random variety of that tier's WarbandsPackMonsters
    /// list. The leader tier is rare and the elites magic: OUR choice, the tables give no rarity.</summary>
    static List<AreaPopulation.Spawn> Warband(Player player, string id, Box box, Random random)
    {
        var result = new List<AreaPopulation.Spawn>();
        if (ClientTables.TableOrEmpty("WarbandsPackMonsters").Where(r => r.Text("Id") == id).ToArray() is not [var monsters, ..]) return result;
        int level = AreaLevel(player);
        var rows = ClientTables.TableOrEmpty("WarbandsPackNumbers")
            .Where(r => level >= r.Int("MinLevel") && level <= r.Int("MaxLevel") && r.Int("SpawnChance") > 0).ToArray();
        if (rows.Length == 0) return result;
        int roll = random.Next(rows.Sum(r => r.Int("SpawnChance")));
        var numbers = rows.First(r => (roll -= r.Int("SpawnChance")) < 0);
        var tiers = new (string Count, string? Chance, string Varieties, MonsterRarity Rarity)[]
        {
            ("Tier4Number", "__col5", "Tier4_MonsterVarietiesKeys", MonsterRarity.Rare),
            ("Tier3Number", "__col7", "Tier3_MonsterVarietiesKeys", MonsterRarity.Magic),
            ("Tier2Number", "__col9", "Tier2_MonsterVarietiesKeys", MonsterRarity.Normal),
            ("Tier1Number", null, "Tier1_MonsterVarietiesKeys", MonsterRarity.Normal),
        };
        int spot = 0;
        foreach (var (countColumn, chance, column, rarity) in tiers)
        {
            var varieties = monsters.Ints(column).Where(v => Monsters.Find(v.ToString()) is { Supported: true }).ToArray();
            int count = numbers.Int(countColumn);
            if (varieties.Length == 0 || count <= 0 || chance is not null && random.Next(100) >= numbers.Int(chance)) continue;
            for (int i = 0; i < count; i++)
            {
                double angle = spot++ * 2.399963229728653;
                var (x, y) = player.Layout.Near((box.X + (int)Math.Round(Math.Cos(angle) * 8), box.Y + (int)Math.Round(Math.Sin(angle) * 8)));
                result.Add(new(varieties[random.Next(varieties.Length)], x, y, rarity, id));
            }
        }
        return result;
    }

    public static bool ClosesAgain(int chance, Random random) => random.Next(100) < chance;

    static async Task SpawnPending(Player player, Area area, Box box)
    {
        byte level = (byte)AreaLevel(player);
        // The area's strongbox monster stats (life, damage, speeds, size) and their drops' item quantity.
        var mods = AreaMods(player);
        int Both(uint mapStat, uint baseStat) => mods.Get(mapStat) + mods.Get(baseStat);
        var effects = new Dictionary<uint, int>
        {
            [Stat.MaximumLifePlusPct] = Both(Stat.MapStrongboxMonstersLifePlusPct, Stat.StrongboxMonstersLifePlusPct),
            [Stat.DamagePlusPct] = Both(Stat.MapStrongboxMonstersDamagePlusPct, Stat.StrongboxMonstersDamagePlusPct),
            [Stat.AttackSpeedPlusPct] = Both(Stat.MapStrongboxMonstersAttackSpeedPlusPct, Stat.StrongboxMonstersAttackSpeedPlusPct),
            [Stat.BaseMovementVelocityPlusPct] = Both(Stat.MapStrongboxMonstersMovementVelocityPlusPct, Stat.StrongboxMonstersMovementVelocityPlusPct),
            [Stat.BaseActorScalePlusPct] = Both(Stat.MapStrongboxMonstersActorScalePlusPct, Stat.StrongboxMonstersActorScalePlusPct),
        }.Where(e => e.Value != 0).ToDictionary(e => e.Key, e => e.Value);
        var extraMods = effects.Count == 0 ? null : new MonsterModSet([], [], new MonsterModEffects(effects));
        int quantity = Both(Stat.MapStrongboxMonstersItemQuantityPlusPct, Stat.StrongboxMonstersItemQuantityPlusPct);
        while (box.Pending.Count > 0 && !player.Closed && !player.LeavingArea)
        {
            var spawn = box.Pending[0];
            if (Monsters.Find(spawn.Variety.ToString()) is not { } variety) return;
            uint id = await SpawnMonster(player, variety, level, spawn.Rarity, spawn.X, spawn.Y,
                unrisen: false, alone: false, extraMods: extraMods, owned: (_, monster) =>
                {
                    monster.Aggro = true;
                    // Item quantity of the guards' own drops, as the Sulphite enrage's (UtilityFlasks.ItemQuantityFactor).
                    if (quantity != 0) monster.EncounterStats = new Dictionary<uint, int> { [MapSulphite.EnragedQuantity] = quantity * 100 };
                });
            if (id == 0) return;
            await SummonEffect(player, spawn.X, spawn.Y);
            box.Guards.Add(id);
            box.Pending.RemoveAt(0);
            Keep(player, area);
        }
    }

    /// <summary>The tests' way past the opening clip's real-time wait (tests never wait on real time): release every
    /// waiting wave of the opened boxes and spawn it now.</summary>
    internal static async Task SpawnWavesNow(Player player)
    {
        if (player.Instance is null || !Areas.TryGetValue(player.Instance, out var area) || area.Boxes is null) return;
        await area.Gate.WaitAsync();
        try
        {
            foreach (var waiting in area.Boxes.ToArray())
            {
                if (waiting.Phase != 1 || waiting.Waves.Count == 0) continue;
                var released = waiting with
                {
                    Pending = [.. waiting.Pending, .. waiting.Waves.SelectMany(w => w.Spawns)],
                    Waves = [],
                };
                area.Boxes[waiting.Index] = released;
                Keep(player, area);
                await SpawnPending(player, area, released);
            }
        }
        finally { area.Gate.Release(); }
    }

    /// <summary>The tests' way past the daemons' real-time wait: every open box's drop time is due now. The guard gate
    /// still applies: a box drops only once its guards are dead.</summary>
    internal static async Task DropNow(Player player)
    {
        if (player.Instance is null || !Areas.TryGetValue(player.Instance, out var area) || area.Boxes is null) return;
        await area.Gate.WaitAsync();
        try
        {
            foreach (var open in area.Boxes.Where(b => b.Phase == 1 && b.DropAtTicks > 0).ToArray()) area.Boxes[open.Index] = open with { DropAtTicks = 1 };
            Keep(player, area);
        }
        finally { area.Gate.Release(); }
    }

    public static async Task Tick(Player player)
    {
        if (!Areas.TryGetValue(player.Instance, out var area) || area.Boxes is not { } boxes || player.Closed || player.LeavingArea) return;
        if (area.Zones.Count > 0 || ZoneShare.TryGetValue(player, out var share) && share.Dps > 0) TickZones(player, area);
        if (!boxes.Any(b => b.Phase == 1)) return;
        await area.Gate.WaitAsync();
        try
        {
            foreach (var waiting in area.Boxes!.Where(b => b.Phase == 1).ToArray())
            {
                var prior = ReleaseWaves(waiting, DateTime.UtcNow);
                if (!ReferenceEquals(prior, waiting))
                {
                    area.Boxes[prior.Index] = prior;
                    Keep(player, area);
                }
                if (prior.Pending.Count > 0) await SpawnPending(player, area, prior);
                // The drop waits for every guard to die (chest_delay_drops_until_daemons_finish): no branch drops
                // while a wave is still unspawned, a spawn still pending, or a guard still standing. DropAtTicks is
                // only an extra minimum floor on top of that gate.
                if (prior.Pending.Count > 0 || prior.Waves.Count > 0 || prior.Guards.Count == 0) continue;
                if (prior.Guards.Any(id => player.Area.Monsters.TryGetValue(id, out var m) && !m.Dead && m.Life > 0)) continue;
                if (prior.DropAtTicks > 0 && DateTime.UtcNow.Ticks < prior.DropAtTicks) continue;
                if (!player.Instance.SetState($"Ambush/rewards/{prior.Index}/{prior.Cycle}")) continue;
                var opened = prior with { Phase = 2 };
                area.Boxes[prior.Index] = opened;
                Keep(player, area);
                // No re-present: the client's box was opened by the component change (OpenPresentation) and stays so.
                await Rewards(player, opened);
                int close = AreaMods(player).Get(Stat.MapStrongboxCloseAfterOpeningChancePct)
                    + (StrongboxCatalog.Find(opened.Type) is { } type
                        ? StrongboxMods.Stat(ItemsFor(player).Mods, type, opened.Explicits, "strongbox_close_after_opening_chance") : 0);
                if (ClosesAgain(close, AreaRng(player)))
                {
                    var closed = opened with { Phase = 0, Cycle = opened.Cycle + 1, Guards = [], Waves = [] };
                    area.Boxes[prior.Index] = closed;
                    Keep(player, area);
                    foreach (var member in Members(player)) await Present(member, closed, true);
                }
            }
        }
        finally { area.Gate.Release(); }
    }

    const double ItemDistanceMin = 3.0, ItemDistanceMax = 8.5;

    /// <summary>The box's items (<see cref="StrongboxLoot"/>) on the ground around it.</summary>
    static async Task Rewards(Player player, Box box)
    {
        if (Environment.GetEnvironmentVariable("Game_DROPS") == "0" || !GameEvents.AllowsLoot(player)
            || StrongboxCatalog.Find(box.Type) is not { } type) return;
        var catalog = ItemsFor(player).Mods;
        var mods = box.Explicits;
        var contents = StrongboxLoot.Roll(player, type,
            id => StrongboxMods.Stat(catalog, type, mods, id) + StrongboxMods.RarityStat(catalog, box.Rarity, id),
            AreaLevel(player), AreaMods(player), AreaRng(player), box.Corrupted);
        // The party bonus, as on a kill (Game.PartyCurrency, the client's own constants); poewiki (Strongbox): Diviner's
        // and Cartographer's Strongboxes "do not drop extra items for additional party members".
        if (type.Kind is not (StrongboxCatalog.Contents.Card or StrongboxCatalog.Contents.Map))
            contents.AddRange(PartyCurrency(player, contents, AreaRng(player)));
        var items = ItemDrops.Stacked(contents);
        foreach (var drop in items)
        {
            // 3.0 to 8.5 cells from the box, as the recorded drops lie.
            double reach = ItemDistanceMin + AreaRng(player).NextDouble() * (ItemDistanceMax - ItemDistanceMin), turn = AreaRng(player).NextDouble() * Math.Tau;
            var at = player.Layout.Near((box.X + (int)Math.Round(Math.Cos(turn) * reach), box.Y + (int)Math.Round(Math.Sin(turn) * reach)));
            var ground = new WorldItemObject(at.X, at.Y, drop.Item);
            uint id = player.Instance.Ground.Add(ground, player);
            await SendGroundItem(player, id, ground, $"strongbox 0x{box.Id:x} drops {drop.Type.Name}", justDropped: true);
        }
        Console.WriteLine($"[map-content] strongbox 0x{box.Id:x} {Label(player, box)} ({type.Kind}, {type.Items} base items) dropped {items.Count} item(s)");
    }

    public static void Describe(Player player, JsonObject state)
    {
        if (!Areas.TryGetValue(player.Instance, out var area) || area.Boxes is null) return;
        state["strongboxes"] = JsonSerializer.SerializeToNode(area.Boxes);
    }

    public static void Leave(Player player)
    {
        Presented.Remove(player);
        LeaveZones(player);
    }
}
