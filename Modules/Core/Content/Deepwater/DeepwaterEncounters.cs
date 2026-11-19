using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Deepwater;

/// <summary>The Allflame encounters in ordinary areas (the wiki's "Overworld encounters"): Coral Nest, Cursed Treasure and
/// Brinerot Plunder. The level generator places their 3×3-tile room (ExtraTerrainFeatures 66-68, rolled by
/// <c>map_deepwater_league</c>); the server creates the room's chest and parts (Level.RoomObjects), places the bone-pile
/// spawner of the two locked chests, spawns the guards when the encounter starts, and once they are dead lets the chest be
/// opened for one Chart and ordinary chest loot. Per instance, shared by a party and kept in the instance's content state.
/// The flow, counts and trigger are guesses (G, world.db <c>deepwater_encounter_*</c>) where the client data says nothing:
/// research in the PR. The buffs the opener gets are not given here.</summary>
internal static class DeepwaterEncounters
{
    public enum EncounterKind { CoralNest, CursedTreasure, BrinerotPlunder }
    public enum Phase { Idle, Fighting, Ready, Opened }

    enum PartKind { CrabSpawner, GolemSpawner, Lamp, Barrel, BonePile }

    sealed class Part
    {
        public required PartKind Kind { get; init; }
        public required string Type { get; init; }
        public required int X { get; init; }
        public required int Y { get; init; }
        public float Rotation { get; init; }
        public string? Model { get; init; }
        public uint Id { get; set; }
    }

    sealed class Encounter
    {
        public required EncounterKind Kind { get; init; }
        public required int Index { get; init; }
        public required string Type { get; init; }
        public required int X { get; init; }
        public required int Y { get; init; }
        public float Rotation { get; init; }
        public string? Model { get; init; }
        /// <summary>The chest's object id now: an opened chest is created again under a new id (Chest has no update reader).</summary>
        public uint Id { get; set; }
        public Phase Phase { get; set; }
        public List<Part> Parts { get; } = [];
        public HashSet<uint> Guards { get; } = [];
        public List<AreaPopulation.Spawn> Pending { get; } = [];
        /// <summary>The characters who opened it (one grant per instance and character; the buffs are part of a later change).</summary>
        public HashSet<string> Granted { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    sealed class Area
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly List<Encounter> Encounters = [];
        /// <summary>The Brinerot Plunder guards (the Ducat roll on their death), read per kill.</summary>
        public readonly HashSet<uint> Brinerot = [];
    }

    static readonly RuleTable<InstanceState, Area> Areas = new();

    const string SaveKey = "Deepwater/encounters";
    const string Chests = "Metadata/Chests/LeagueDeepwater/";
    const string Objects = "Metadata/Terrain/Leagues/Deepwater/Objects/";
    const string Monsters_ = "Metadata/Monsters/DeepwaterLeague/";

    static readonly (EncounterKind Kind, string Type)[] ChestTypes =
    [
        (EncounterKind.CoralNest, Chests + "GiantCoralChest"),
        (EncounterKind.CursedTreasure, Chests + "CursedTreasureChestEncounter"),
        (EncounterKind.BrinerotPlunder, Chests + "BrinerotStoresChestEncounter"),
    ];

    static readonly (PartKind Kind, string Type)[] PartTypes =
    [
        (PartKind.CrabSpawner, Objects + "CoralEncounterGiantCoralCrabSpawner"),
        (PartKind.GolemSpawner, Objects + "CoralEncounterGiantCoralGolemSpawner"),
        (PartKind.Lamp, Objects + "CursedTreasureChestEncounterLamp"),
        (PartKind.Barrel, "Metadata/Terrain/Leagues/Deepwater/BrinerotEncounter/BrinerotEmergeBarrel"),
    ];

    /// <summary>The bone piles the guards come out of: not in the rooms, the server places them (G).</summary>
    static string? BonePile(EncounterKind kind) => kind switch
    {
        EncounterKind.CursedTreasure => Objects + "CursedTreasureChestEncounterSpawner",
        EncounterKind.BrinerotPlunder => Objects + "BrinerotStoresChestEncounterSpawner",
        _ => null,
    };

    // The guards' MonsterVarieties by Id (resolved to rows at first use): Coral's golem and crabs, the crawler, the plain
    // Brinerots (the ...EncounterBarrel varieties carry AbyssCantTouchThis and a `free` state the server does not model:
    // the plain ones stand in at the broken barrels, G).
    const string Golem = Monsters_ + "CoralGolemEncounter", Crawler = Monsters_ + "DrownedCrawlerEncounter";
    static readonly string[] Crabs = [Monsters_ + "CoconutCrabEncounter", Monsters_ + "NimbleCrabEncounter", Monsters_ + "QuillCrabEncounter"];
    static readonly string[] Brinerots = [Monsters_ + "BrinerotSpearmanEncounter", Monsters_ + "BrinerotJavelineerEncounter", Monsters_ + "BrinerotSwordsmanEncounter"];

    // The Charts an encounter chest drops (BaseItemTypes 5413-5415; ChartThermalVents 5416 never shipped).
    static readonly string[] Charts = ["Metadata/Items/Deepwater/ChartSandy", "Metadata/Items/Deepwater/ChartCoralForest", "Metadata/Items/Deepwater/ChartCoralReef"];
    // The regular Ducats (BaseItemTypes 5418, 5419, 5438-5446; not 5420 "[DNT] UNUSED"): a Brinerot guard's drop.
    static readonly string[] Ducats =
    [
        "Metadata/Items/Deepwater/DeepwaterAbyssCurrency", "Metadata/Items/Deepwater/DeepwaterMercenaryCurrency",
        "Metadata/Items/Deepwater/DeepwaterBlankCurrency", "Metadata/Items/Deepwater/DeepwaterAttributeSwap",
        "Metadata/Items/Deepwater/DeepwaterItemSplit", "Metadata/Items/Deepwater/DeepwaterChartReroll",
        "Metadata/Items/Deepwater/DeepwaterSaboteur", "Metadata/Items/Deepwater/DeepwaterPantheon",
        "Metadata/Items/Deepwater/DeepwaterTatttoo", "Metadata/Items/Deepwater/DeepwaterGhostbuster",
        "Metadata/Items/Deepwater/DeepwaterEldritch",
    ];

    // G: the room is 3×3 tiles (69 cells); a part belongs to the chest within this distance. The bone pile's ring.
    const int PartRange = 50, PileNear = 8, PileFar = 12;

    static readonly Lazy<Dictionary<string, int>> VarietyRows = new(() => ClientTables.TableOrEmpty("MonsterVarieties")
        .Where(r => r.Text("Id") is not null).GroupBy(r => r.Text("Id")!).ToDictionary(g => g.Key, g => g.First().Index, StringComparer.Ordinal));

    static readonly Lazy<Dictionary<string, int>> ChestItems = new(() => ClientTables.TableOrEmpty("Chests")
        .Where(r => r.Text("Id") is { } id && id.StartsWith(Chests, StringComparison.Ordinal))
        .ToDictionary(r => r.Text("Id")!, r => r.IntOrNull("__col2") ?? 0, StringComparer.Ordinal));

    static readonly Lazy<Dictionary<string, StateMachineData?>> States = new(() =>
    {
        var types = ChestTypes.Select(c => c.Type).Concat(PartTypes.Select(p => p.Type))
            .Concat(ChestTypes.Select(c => BonePile(c.Kind)).OfType<string>()).ToHashSet(StringComparer.Ordinal);
        return DeepwaterLoot.StatesOf(types);
    });

    static int Setting(string key) => DeepwaterLoot.Setting(key);

    /// <summary>A guard variety's row, when the server spawns it; null (logged once) otherwise.</summary>
    static int? Variety(IModuleContext module, string id)
    {
        if (VarietyRows.Value.TryGetValue(id, out int row) && Monsters.Find(row.ToString()) is { Supported: true }) return row;
        if (Unsupported.Add(id)) module.Log($"Allflame encounters: {id} is not a monster the server spawns; that guard is left out");
        return null;
    }
    static readonly HashSet<string> Unsupported = new(StringComparer.Ordinal);

    static QuestObjectTypes.Info? Info(string type) =>
        QuestObjectTypes.Find(type) is { } info && QuestObject.CanSerialize(info.Components, States.Value.GetValueOrDefault(type) is not null) ? info : null;

    static int AreaLevel(ClientState conn) =>
        Math.Clamp(MapRunOf(conn) is { Area.Level: > 0 } map ? map.Area.Level : ServerLevels.AreaLevel(conn.Layout.Id), 1, 100);

    static bool Near((int X, int Y) a, (int X, int Y) b, double distance)
    {
        long dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy <= distance * distance;
    }

    // ---- the plan ----

    /// <summary>At every entry of an ordinary area: the instance's encounters planned once (the rooms' chests and parts,
    /// the bone piles, the saved phases), then created on this connection.</summary>
    public static async Task Enter(IModuleContext module, ClientState conn)
    {
        if (conn.Instance is not { } instance || conn.Layout.Level is null) return;
        Area? area;
        if (!Areas.TryGetValue(instance, out area))
        {
            var planned = Plan(module, conn);
            if (planned.Count == 0) return;
            lock (Areas)
            {
                if (!Areas.TryGetValue(instance, out area))
                {
                    area = Areas.GetOrCreateValue(instance);
                    lock (area.Encounters) area.Encounters.AddRange(planned);
                }
            }
        }
        await area.Gate.WaitAsync();
        try
        {
            foreach (var e in area.Encounters)
            {
                await Create(conn, e.Type, e.Id, e.X, e.Y, e.Rotation, e.Model, ChestStates(e), e.Phase == Phase.Opened);
                foreach (var p in e.Parts) await Create(conn, p.Type, p.Id, p.X, p.Y, p.Rotation, p.Model, PartStates(e, p), false);
            }
        }
        finally { area.Gate.Release(); }
    }

    sealed record Saved(EncounterKind Kind, int Index, Phase Phase, string[] Granted);

    static List<Encounter> Plan(IModuleContext module, ClientState conn)
    {
        var layout = conn.Layout;
        var lines = layout.Level?.RoomObjects ?? [];
        var encounters = new List<Encounter>();
        foreach (var o in lines)
        {
            if (ChestTypes.FirstOrDefault(c => c.Type == o.Type) is not { Type: not null } chest) continue;
            if (Info(o.Type) is not { } info)
            {
                module.Log($"Allflame encounters: {o.Type} at ({o.X}, {o.Y}) is not an object the server writes; left out");
                continue;
            }
            encounters.Add(new Encounter
            {
                Kind = chest.Kind, Index = encounters.Count, Type = o.Type, X = o.X, Y = o.Y, Rotation = o.Rotation,
                Model = string.IsNullOrEmpty(o.Model) || o.Model == info.AnimatedModel ? null : o.Model, Id = conn.TakeObjectId(),
            });
        }
        if (encounters.Count == 0) return encounters;
        foreach (var o in lines)
        {
            if (PartTypes.FirstOrDefault(p => p.Type == o.Type) is not { Type: not null } part) continue;
            var owner = encounters.Where(e => Near((e.X, e.Y), (o.X, o.Y), PartRange))
                .OrderBy(e => (long)(e.X - o.X) * (e.X - o.X) + (long)(e.Y - o.Y) * (e.Y - o.Y)).FirstOrDefault();
            if (owner is null) continue;
            // A part whose type the server cannot write (no .ot data) is still a place for the guards, just not shown.
            owner.Parts.Add(new Part
            {
                Kind = part.Kind, Type = o.Type, X = o.X, Y = o.Y, Rotation = o.Rotation,
                Model = string.IsNullOrEmpty(o.Model) || o.Model == QuestObjectTypes.Find(o.Type)?.AnimatedModel ? null : o.Model,
                Id = conn.TakeObjectId(),
            });
        }
        // The bone pile: PileNear-PileFar cells from the chest on a walkable cell, from the instance seed (the same spot for
        // every member).
        foreach (var e in encounters)
        {
            if (BonePile(e.Kind) is not { } pile) continue;
            var random = new Random(unchecked((int)(conn.Seed * 0x9e3779b1u) ^ e.Index * 7919 ^ 0x424f4e45));
            (int X, int Y)? at = null;
            for (int t = 0; t < 64 && at is null; t++)
            {
                double angle = random.NextDouble() * Math.Tau, r = PileNear + random.NextDouble() * (PileFar - PileNear);
                int x = e.X + (int)Math.Round(Math.Cos(angle) * r), y = e.Y + (int)Math.Round(Math.Sin(angle) * r);
                if (layout.WalkableFor(x, y, 2)) at = (x, y);
            }
            at ??= layout.Near((e.X, e.Y));
            e.Parts.Add(new Part { Kind = PartKind.BonePile, Type = pile, X = at.Value.X, Y = at.Value.Y, Rotation = (float)(random.NextDouble() * Math.Tau), Id = conn.TakeObjectId() });
        }
        // A re-made instance (a saved one) shows the end states; a fight that was going on starts again.
        if (conn.Instance.ContentState.TryGetValue(SaveKey, out string? saved))
        {
            try
            {
                foreach (var s in JsonSerializer.Deserialize<List<Saved>>(saved) ?? [])
                    if (encounters.FirstOrDefault(e => e.Index == s.Index && e.Kind == s.Kind) is { } e)
                    {
                        e.Phase = s.Phase == Phase.Fighting ? Phase.Idle : s.Phase;
                        e.Granted.UnionWith(s.Granted);
                    }
            }
            catch (JsonException ex) { module.Log($"Allflame encounters: the saved state is not read ({ex.Message})"); }
        }
        module.Log($"{CharacterName(conn)}: Allflame encounters in {conn.Layout.Id}: " + string.Join("; ", encounters.Select(e =>
            $"{e.Kind} 0x{e.Id:x} at ({e.X}, {e.Y}) {e.Phase}, parts {string.Join(", ", e.Parts.GroupBy(p => p.Kind).Select(g => $"{g.Count()} {g.Key}"))}")));
        return encounters;
    }

    static void Keep(ClientState conn, Area area) =>
        conn.Instance.ContentState[SaveKey] = JsonSerializer.Serialize(area.Encounters.Select(e => new Saved(e.Kind, e.Index, e.Phase, [.. e.Granted])).ToList());

    // ---- the objects ----

    static StateMachineData? ChestStates(Encounter e)
    {
        if (States.Value.GetValueOrDefault(e.Type) is not { } states) return null;
        bool started = e.Phase != Phase.Idle, cleared = e.Phase is Phase.Ready or Phase.Opened, opened = e.Phase == Phase.Opened;
        return e.Kind switch
        {
            // buff 1: the shatter (created: at the animation's end).
            EncounterKind.CoralNest => states.Set("buff", opened ? 1 : 0),
            // spawning 1: untargetable with the chains while end > -1; end -1: targetable (the chains open); ghost/buff 1: no
            // minimap icon (Cursed also untargetable).
            EncounterKind.CursedTreasure => states.Set("spawning", started ? 1 : 0).Set("end", cleared ? -1 : 0).Set("ghost", opened ? 1 : 0),
            _ => states.Set("spawning", started ? 1 : 0).Set("end", cleared ? -1 : 0).Set("buff", opened ? 1 : 0),
        };
    }

    static StateMachineData? PartStates(Encounter e, Part p)
    {
        if (States.Value.GetValueOrDefault(p.Type) is not { } states) return null;
        bool started = e.Phase != Phase.Idle;
        return p.Kind switch
        {
            PartKind.CrabSpawner or PartKind.GolemSpawner => states.Set("triggered", started ? 1 : 0),
            // active 1: lit (on, idle); 2: out with GhostflameSurge.
            PartKind.Lamp => states.Set("active", e.Phase switch { Phase.Idle => 0, Phase.Fighting => 1, _ => 2 }),
            PartKind.Barrel => states.Set("break", started ? 1 : 0),
            _ => states,
        };
    }

    static async Task Create(ClientState conn, string type, uint id, int x, int y, float rotation, string? model, StateMachineData? states, bool opened)
    {
        if (QuestObjectTypes.Find(type) is not { } info || !QuestObject.CanSerialize(info.Components, states is not null)) return;
        var blob = new QuestObject(x, y, info.Components, rotation, null, model, Opened: opened)
        {
            StateMachine = states,
            HideMinimapIcon = opened,
        }.Blob();
        var player = PlayerOf(conn);
        if (!await global::Game.SendWorldCreate(player, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(type), blob),
            $"0x02bb Allflame encounter 0x{id:x} {type.Split('/')[^1]} at ({x}, {y}){(opened ? " (opened)" : "")}")) return;
        await global::Game.SendWorldRecord(player, id, InstanceProtocol.EnterWakeZone(id));
        if (info.Blocking) conn.SetBlocker(id, x, y, Math.Max(1, info.Size));
    }

    static async Task SendStates(ClientState conn, string type, uint id, StateMachineData? states, string what)
    {
        if (states is null || QuestObjectTypes.Find(type) is not { } info) return;
        int index = info.Components.ToList().IndexOf("StateMachine");
        if (index < 0) return;
        await global::Game.SendWorldRecord(PlayerOf(conn), id, InstanceEntities.StateMachineUpdate(id, (byte)index, states),
            $"0x02bc Allflame encounter 0x{id:x} {type.Split('/')[^1]}: {what}");
    }

    // Every member sees the encounter's states now (the chest and its parts).
    static async Task Broadcast(ClientState conn, Encounter e, string what)
    {
        foreach (var member in DeepwaterLoot.Members(conn))
        {
            await SendStates(member, e.Type, e.Id, ChestStates(e), what);
            foreach (var p in e.Parts) await SendStates(member, p.Type, p.Id, PartStates(e, p), what);
        }
    }

    // ---- start ----

    /// <summary>Starts an idle encounter (under the gate): the states, and its guards queued and spawned.</summary>
    static async Task Start(IModuleContext module, ClientState conn, Area area, Encounter e)
    {
        if (e.Phase != Phase.Idle) return;
        e.Phase = Phase.Fighting;
        e.Pending.AddRange(Guards(module, conn, e));
        Keep(conn, area);
        module.Log($"{CharacterName(conn)}: Allflame {e.Kind} 0x{e.Id:x} at ({e.X}, {e.Y}) starts, {e.Pending.Count} guards");
        await Broadcast(conn, e, "started");
        await SpawnPending(conn, area, e);
    }

    /// <summary>The encounter's guards (G: counts from world.db; rarity as the league's packs, magic and rare possible:
    /// 3.29.3's "rare monsters in specific Allflame encounters").</summary>
    static List<AreaPopulation.Spawn> Guards(IModuleContext module, ClientState conn, Encounter e)
    {
        var layout = conn.Layout;
        var random = new Random(unchecked((int)(conn.Seed * 0x85ebca6bu) ^ e.Index * 104729));
        var spawns = new List<AreaPopulation.Spawn>();
        (int X, int Y) Spot(int x, int y, int spread)
        {
            for (int t = 0; t < 16; t++)
            {
                int px = x + random.Next(-spread, spread + 1), py = y + random.Next(-spread, spread + 1);
                if (layout.WalkableFor(px, py, 1)) return (px, py);
            }
            return layout.WalkableFor(x, y, 1) ? (x, y) : layout.Near((x, y));
        }
        void Add(string id, (int X, int Y) at)
        {
            if (Variety(module, id) is int row) spawns.Add(new(row, at.X, at.Y, DeepwaterMonsters.Rarity(random), "Allflame " + e.Kind));
        }
        int Between(string min, string max)
        {
            int lo = Setting(min), hi = Math.Max(lo, Setting(max));
            return random.Next(lo, hi + 1);
        }
        var pile = e.Parts.FirstOrDefault(p => p.Kind == PartKind.BonePile) is { } bp ? (bp.X, bp.Y) : (e.X, e.Y);
        switch (e.Kind)
        {
            case EncounterKind.CoralNest:
                foreach (var p in e.Parts.Where(p => p.Kind == PartKind.GolemSpawner)) Add(Golem, Spot(p.X, p.Y, 0));
                foreach (var p in e.Parts.Where(p => p.Kind == PartKind.CrabSpawner))
                    for (int i = Between("deepwater_encounter_coral_crabs_min", "deepwater_encounter_coral_crabs_max"); i > 0; i--)
                        Add(Crabs[random.Next(Crabs.Length)], Spot(p.X, p.Y, 3));
                break;
            case EncounterKind.CursedTreasure:
                for (int i = Between("deepwater_encounter_cursed_min", "deepwater_encounter_cursed_max"); i > 0; i--)
                    Add(Crawler, Spot(pile.Item1, pile.Item2, 4));
                break;
            case EncounterKind.BrinerotPlunder:
                foreach (var p in e.Parts.Where(p => p.Kind == PartKind.Barrel)) Add(Brinerots[random.Next(Brinerots.Length)], Spot(p.X, p.Y, 1));
                for (int i = Between("deepwater_encounter_brinerot_extra_min", "deepwater_encounter_brinerot_extra_max"); i > 0; i--)
                    Add(Brinerots[random.Next(Brinerots.Length)], Spot(pile.Item1, pile.Item2, 4));
                break;
        }
        return spawns;
    }

    // As MapStrongboxes.SpawnPending: at the area level, aggressive, each guard kept as it spawns.
    static async Task SpawnPending(ClientState conn, Area area, Encounter e)
    {
        byte level = (byte)AreaLevel(conn);
        var (remote, stream, ciphers) = conn.Wire;
        while (e.Pending.Count > 0 && !conn.Closed && !conn.LeavingArea)
        {
            var spawn = e.Pending[0];
            if (Monsters.Find(spawn.Variety.ToString()) is not { } variety)
            {
                e.Pending.RemoveAt(0);
                continue;
            }
            uint id = await SpawnMonster(remote, stream, ciphers, conn, variety, level, spawn.Rarity, spawn.X, spawn.Y,
                unrisen: false, alone: false, owned: (_, monster) => monster.Aggro = true);
            if (id == 0) return;
            e.Guards.Add(id);
            if (e.Kind == EncounterKind.BrinerotPlunder) lock (area.Brinerot) area.Brinerot.Add(id);
            e.Pending.RemoveAt(0);
        }
    }

    // ---- tick ----

    /// <summary>Each tick: a player near an idle locked chest starts it (G: proximity, the chests are <c>locked</c>); the
    /// fights' queued guards spawn; a fight whose guards are all dead is cleared. One table lookup when the instance has
    /// no encounter going.</summary>
    public static async Task Tick(IModuleContext module, ClientState conn)
    {
        if (conn.Instance is not { } instance || !Areas.TryGetValue(instance, out var area) || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0) return;
        Encounter[] due;
        lock (area.Encounters) due = [.. area.Encounters.Where(e => e.Phase is Phase.Idle or Phase.Fighting)];
        if (due.Length == 0) return;
        var at = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        int radius = Setting("deepwater_encounter_trigger_radius");
        bool near = due.Any(e => e.Phase == Phase.Fighting || e.Kind != EncounterKind.CoralNest && Near(at, (e.X, e.Y), radius));
        if (!near) return;
        await area.Gate.WaitAsync();
        try
        {
            foreach (var e in due)
            {
                if (e.Phase == Phase.Idle && e.Kind != EncounterKind.CoralNest && Near(at, (e.X, e.Y), radius)) await Start(module, conn, area, e);
                if (e.Phase != Phase.Fighting) continue;
                if (e.Pending.Count > 0) await SpawnPending(conn, area, e);
                if (e.Pending.Count > 0) continue;
                lock (conn.Monsters)
                    if (e.Guards.Any(id => conn.Monsters.TryGetValue(id, out var m) && !m.Dead && m.Life > 0)) continue;
                if (!conn.Instance.SetState($"Deepwater/encounter/{e.Index}/ready")) continue;
                e.Phase = Phase.Ready;
                Keep(conn, area);
                module.Log($"{CharacterName(conn)}: Allflame {e.Kind} 0x{e.Id:x} cleared ({e.Guards.Count} guards)");
                await Broadcast(conn, e, "cleared");
            }
        }
        finally { area.Gate.Release(); }
    }

    // ---- use and open ----

    /// <summary>C→S 0x24 on an encounter's chest: the Coral Nest starts at its first use (it is not <c>locked</c>), and a
    /// cleared chest opens; anything else ends the action. False when the object is no encounter's chest.</summary>
    public static async Task<bool> Use(IModuleContext module, ClientState conn, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (conn.Instance is not { } instance || !Areas.TryGetValue(instance, out var area)) return false;
        Encounter? e;
        lock (area.Encounters) e = area.Encounters.FirstOrDefault(x => x.Id == use.TargetId);
        if (e is null) return false;
        bool usable = e.Phase == Phase.Ready || e.Phase == Phase.Idle && e.Kind == EncounterKind.CoralNest;
        if (!usable || conn.PlayerLife == 0 || conn.Closed || conn.LeavingArea || QuestObjectTypes.Find(e.Type) is not { } info)
        {
            await DeepwaterLoot.EndAction(conn);
            return true;
        }
        int reach = Math.Max(1, info.Size) + 2;
        if (!TryBeginObjectWalk(conn, requestedAt, e.X, e.Y, reach, out var walk)) return true;
        var (action, start, here, _, arrive) = walk;
        if (!use.Started)
            await global::Game.SendWorldRecord(PlayerOf(conn), PlayerId,
                InstanceProtocol.ActionStarted(PlayerId, here.X, here.Y, e.X, e.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: e.Id));
        if (arrive > start) await Task.Delay(TimeSpan.FromSeconds(arrive - start));
        await area.Gate.WaitAsync();
        try
        {
            if (!PlayerActionCurrent(conn, action) || conn.PlayerLife == 0
                || !GameServer.Core.World.PlayerWalk.InWalkUpRange(conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds), (e.X, e.Y), reach)) return true;
            if (e.Phase == Phase.Idle && e.Kind == EncounterKind.CoralNest) await Start(module, conn, area, e);
            else if (e.Phase == Phase.Ready) await Open(module, conn, area, e);
        }
        finally
        {
            area.Gate.Release();
            await DeepwaterLoot.EndAction(conn, action);
        }
        return true;
    }

    /// <summary>GameEvents.ChestsOpened (a warcry, the caster mastery: 3.29.1's fix): the cleared chests in the radius open,
    /// and an idle Coral Nest starts (G).</summary>
    public static async Task OpenNear(IModuleContext module, ClientState conn, (int X, int Y) at, int radius)
    {
        if (conn.Instance is not { } instance || !Areas.TryGetValue(instance, out var area)) return;
        await area.Gate.WaitAsync();
        try
        {
            foreach (var e in area.Encounters.ToArray())
            {
                if (!Near(at, (e.X, e.Y), radius)) continue;
                if (e.Phase == Phase.Ready) await Open(module, conn, area, e);
                else if (e.Phase == Phase.Idle && e.Kind == EncounterKind.CoralNest) await Start(module, conn, area, e);
            }
        }
        finally { area.Gate.Release(); }
    }

    /// <summary>Opens a cleared chest (under the gate, one opener): the chest created again opened under a new id, one
    /// Chart and the chest's ordinary loot.</summary>
    static async Task Open(IModuleContext module, ClientState conn, Area area, Encounter e)
    {
        if (e.Phase != Phase.Ready || !conn.Instance.SetState($"Deepwater/encounter/{e.Index}/opened")) return;
        uint was = e.Id;
        e.Phase = Phase.Opened;
        e.Id = conn.TakeObjectId();
        e.Granted.Add(CharacterName(conn));
        Keep(conn, area);
        foreach (var member in DeepwaterLoot.Members(conn))
        {
            member.RemoveBlocker(was);
            await global::Game.SendWorldRemoval(PlayerOf(member), was);
            await Create(member, e.Type, e.Id, e.X, e.Y, e.Rotation, e.Model, ChestStates(e), true);
            foreach (var p in e.Parts) await SendStates(member, p.Type, p.Id, PartStates(e, p), "opened");
        }
        var drops = Loot(conn, e);
        module.Log($"{CharacterName(conn)}: Allflame {e.Kind} 0x{was:x} opened (now 0x{e.Id:x}): {string.Join(", ", drops.Select(d => d.Type.Name))}");
        int size = QuestObjectTypes.Find(e.Type)?.Size ?? 1;
        await DeepwaterLoot.Drop(conn, e.X, e.Y, size, $"Allflame {e.Kind} 0x{e.Id:x}", drops);
    }

    /// <summary>One Chart (uniform over the three biomes; normal, item level = area level, uncharted: room 0xff, G until
    /// chart mods exist) and the Chests row's <c>__col2</c> ordinary items (G: read as the item count; Coral 5, Cursed 3,
    /// Brinerot 2). No Sulphur and no Cursed Ducats: their sources are Charts and Voyages.</summary>
    static List<ItemDrops.Drop> Loot(ClientState conn, Encounter e)
    {
        var catalog = ItemsFor(conn);
        int level = AreaLevel(conn);
        var drops = new List<ItemDrops.Drop>();
        var charts = Charts.Select(catalog.Find).OfType<ItemCatalog.BaseType>().Where(t => t.Supported).ToArray();
        if (charts.Length > 0)
        {
            var chart = charts[Random.Shared.Next(charts.Length)];
            drops.Add(new(ItemCatalog.Create(chart) with { ItemLevel = (byte)level, Chart = new ChartData(0, 0, 0xff) }, chart));
        }
        int count = ChestItems.Value.GetValueOrDefault(e.Type);
        if (count > 0 && Environment.GetEnvironmentVariable("Game_DROPS") != "0")
            drops.AddRange(DropsFor(conn).Container(level, count, 0, Random.Shared));
        return drops;
    }

    // ---- drops ----

    /// <summary>GameEvents.Drops: a Brinerot Plunder guard drops a random Ducat at <c>deepwater_encounter_ducat_chance</c>
    /// percent (G; a plain drop, Ducat crafting is not built). Per kill: one table lookup and a set test.</summary>
    public static void Drops(DropRoll roll)
    {
        if (roll.Monster.ObjectId == 0 || roll.Conn.Instance is not { } instance || !Areas.TryGetValue(instance, out var area)) return;
        lock (area.Brinerot) if (!area.Brinerot.Remove(roll.Monster.ObjectId)) return;
        if (Random.Shared.Next(100) < Setting("deepwater_encounter_ducat_chance")) roll.Items.Add(Ducats[Random.Shared.Next(Ducats.Length)]);
    }

    /// <summary>The instance's encounters for /state.</summary>
    public static void State(ClientState conn, JsonObject json)
    {
        if (conn.Instance is not { } instance || !Areas.TryGetValue(instance, out var area)) return;
        lock (area.Encounters)
            json["deepwaterEncounters"] = new JsonArray([.. area.Encounters.Select(e => (JsonNode)new JsonObject
            {
                ["kind"] = e.Kind.ToString(), ["id"] = e.Id, ["x"] = e.X, ["y"] = e.Y, ["phase"] = e.Phase.ToString(),
                ["guards"] = e.Guards.Count, ["pending"] = e.Pending.Count,
                ["parts"] = new JsonArray([.. e.Parts.Select(p => (JsonNode)$"{p.Kind} 0x{p.Id:x} ({p.X}, {p.Y})")]),
                ["granted"] = new JsonArray([.. e.Granted.Select(g => (JsonNode)g)]),
            })]);
    }
}
