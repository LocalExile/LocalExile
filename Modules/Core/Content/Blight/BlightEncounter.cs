using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Modding;

namespace GameServer.Modules.Blight;

/// <summary>A pump's foundation, and the tower that stands on it after a build. <see cref="Row"/> is the BlightTowers row (0: empty).</summary>
public sealed class Spot(uint id, int x, int y)
{
    public uint Id = id;
    public int X = x, Y = y, Row;
    /// <summary>The next of <see cref="BlightEncounter.RaiseSteps"/> to send; 0 when the tower stands.</summary>
    public int Raising;
    public bool Upgrade;
    public double NextFire;
    /// <summary>Registered GrantedEffects skill id (0x4100+) for native cast animations, or 0 if none.</summary>
    public ushort SkillId;
    public ushort ActionSeq = 1;
    /// <summary>Active Empowering aura visual buff instance on this tower (BuffDefinitions 1406).</summary>
    public ushort EmpowerBuffInstance;
    public int EmpowerBuffTier;
    /// <summary>Living friendly monster ids summoned by a Minion/Scout/Sentinel tower on this spot.</summary>
    public readonly List<uint> Minions = [];
}

/// <summary>A spot as one send reads it: copied under the defence's lock, so a tick that raises the tower cannot change it mid-send.</summary>
public readonly record struct SpotView(uint Id, int X, int Y, int Row, int Raising, bool Upgrade);

/// <summary>One active Meteor Tower burning ground zone created by the Burning Ground ring anoint.</summary>
public readonly record struct BurningGroundZone(int X, int Y, double Until, int Dps, double NextTick);

public enum Phase { Ready, Building, Running, Won, Lost }

/// <summary>One pump defence of an instance. Its state is the instance's: whoever is in the instance sees and drives the same one.</summary>
public sealed class Defence(uint pump, (int X, int Y) cell, float rotation, byte level)
{
    public readonly uint Pump = pump;
    public readonly (int X, int Y) Cell = cell;
    public readonly float Rotation = rotation;
    public readonly byte Level = level;
    public Phase Phase;
    public int Durability, MaxDurability;
    public uint Resource;
    public double StartedAt, EndedAt = double.PositiveInfinity;
    public double EncounterDuration;
    public readonly int[] PumpStates = new int[BlightEncounter.PumpStateNames.Length];
    public byte PumpTransition = 1;
    public int PumpStep;
    public BlightLayout.Plan? Plan;
    public readonly Dictionary<uint, Spot> Spots = [];
    public readonly Dictionary<int, double> LaneNext = [];
    public readonly Dictionary<int, int> LaneSpawned = [];
    public readonly Dictionary<int, int> LaneEncounterTypes = [];
    public readonly Dictionary<int, string> LaneRewards = [];
    public readonly HashSet<int> BossLanes = [];
    public readonly HashSet<int> BossOnlyLanes = [];
    public int MonstersPerLane;
    public readonly Dictionary<uint, (int Lane, MonsterRarity Rarity)> Monsters = [];
    public readonly Dictionary<uint, int> PathIndex = [];
    public readonly HashSet<uint> Resolved = [];
    /// <summary>End-of-lane Blight reward chests, GigaChest, or salvaged tower chests spawned when the defence succeeds.</summary>
    public readonly Dictionary<uint, BlightChestState> Chests = [];
    /// <summary>Timed tower control debuff expiries by (monsterId, statsKey).</summary>
    public readonly Dictionary<(uint MonsterId, string Key), double> TimedControls = [];
    /// <summary>Remaining hits before Stone Gaze petrification breaks for each petrified monster.</summary>
    public readonly Dictionary<uint, int> PetrifyHitsLeft = [];
    /// <summary>Active Meteor Tower Burning Ground zones.</summary>
    public readonly List<BurningGroundZone> BurningGrounds = [];
    /// <summary>Monsters enhanced by Empowering/Imbuing/Smothering towers under Scarab of Invigoration (Buff 2462).</summary>
    public readonly HashSet<uint> EnhancedMonsters = [];
    public readonly Dictionary<uint, ushort> InvigoratedBuffInstances = [];
    public ushort NextTowerBuffInstance = 0x2000;
    /// <summary>Monster loot banked during the defence to burst at the pump on victory (lootsplosion).</summary>
    public readonly List<Item> BankedPumpLoot = [];
    /// <summary>Total Blight monsters slain (scales Blightheart GigaChest rewards).</summary>
    public int KilledCount;
    /// <summary>Extra lane chests awarded by slain Blight bosses (Atlas Notable Blightspawn).</summary>
    public int ExtraBossChests;
    /// <summary>Spawned Sister Cassia NPC entity id and cell beside this pump (0 when not spawned).</summary>
    public uint CassiaId;
    public (int X, int Y) CassiaCell;
    /// <summary>The pump and its foundations were created for the area (its object records give them to whoever joins later).</summary>
    public bool Shown;
    /// <summary>The map is a Blighted one (its own lane tree, 20-HP pump, 5-minute encounter).</summary>
    public bool Blighted;
    /// <summary>The map is a Blight-ravaged (Uber Blighted) map (ilvl 85, 9 anoints, 50% IIQ chest scaling, Stranglegasp drops).</summary>
    public bool UberBlighted;
    /// <summary>The seed the lanes were laid with: the same seed lays the same lanes again after a module reload.</summary>
    public int PlanSeed;
    /// <summary>The lane that opens first (the plan's smallest delay), set with the plan.</summary>
    public int FirstLane;
}

/// <summary>The defences of an instance.</summary>
public sealed class Run
{
    public readonly List<Defence> Defences = [];
    public readonly object Gate = new();
    /// <summary>The area time at which the instance's saved copy is next written.</summary>
    public double NextSave;
}

/// <summary>A defence as it is kept in the instance's ContentState: what a reloaded module needs to carry on (the lanes are laid again from the seed).</summary>
public sealed class SavedDefence
{
    public uint Pump, CassiaId;
    public int CellX, CellY, CassiaX, CassiaY;
    public float Rotation;
    public byte Level;
    public bool Blighted, UberBlighted, Shown;
    public Phase Phase;
    public int Durability, MaxDurability, PumpTransition, PumpStep, MonstersPerLane, PlanSeed, KilledCount, ExtraBossChests;
    public uint Resource;
    public double StartedAt, EndedAt = double.PositiveInfinity, EncounterDuration;
    public int[] PumpStates = [];
    public List<SavedSpot> Spots = [];
    public Dictionary<int, double> LaneNext = [];
    public Dictionary<int, int> LaneSpawned = [];
    public Dictionary<int, int> LaneEncounterTypes = [];
    public Dictionary<int, string> LaneRewards = [];
    public List<int> BossLanes = [];
    public List<int> BossOnlyLanes = [];
    public List<SavedMonster> Monsters = [];
    public Dictionary<uint, int> PathIndex = [];
    public List<uint> Resolved = [];
    public List<BlightChestState> Chests = [];
}

public sealed class SavedSpot
{
    public uint Id;
    public int X, Y, Row, Raising;
    public ushort SkillId;
    public bool Upgrade;
    public double NextFire;
    public List<uint> Minions = [];
}

public sealed class SavedMonster
{
    public uint Id;
    public int Lane;
    public MonsterRarity Rarity;
}

public sealed class SavedRun
{
    public List<SavedDefence> Defences = [];
}

/// <summary>
/// The Blight pump defence (CoreLeagues row 16) for campaign areas, maps, Blighted maps, and Uber-Blight (Blight-ravaged) maps:
/// manages pump placement, build phase &amp; Fast-Forward (0x0264), lane waves &amp; bosses, all 30 towers, Scarabs, Atlas passives,
/// Fungal Horizons Blightreach buff (2512), Cassia's Pride / Sturdy Construction damage rules, and victory chests/lootsplosion.
/// </summary>
public sealed class BlightEncounter
{
    public const string Content = "Blight";

    /// <summary>The terrain object the defence is built round, written as the capture's create has it.</summary>
    public const string PumpObject = "Metadata/Terrain/Leagues/Blight/Objects/BlightPump";

    /// <summary>The pump's shared states, in its .ot order.</summary>
    public static readonly string[] PumpStateNames =
        ["ready_to_build", "health", "ui_description", "next_path", "activated", "success", "fail", "build_step", "ready_to_start"];

    const int PumpHealth = 1, PumpNextPath = 3, PumpActivated = 4, PumpSuccess = 5, PumpFail = 6, PumpBuildStep = 7, PumpReadyToStart = 8;

    /// <summary>The pump's component order (the .ot): the indexes of the components a defence updates.</summary>
    const byte PumpStateMachine = 6, PumpTransitionable = 7, PumpInteraction = 10;

    /// <summary>A foundation's and a tower's StateMachine and Transitionable, in their component order.</summary>
    const byte SpotStateMachine = 11, SpotTransitionable = 12;

    const uint VirtualOnFullLife = Stat.VirtualOnFullLife;

    /// <summary>How many monsters one lane sends in a normal map (scaled by pack size &amp; Blightheart sub-waves).</summary>
    public const int BaseMonstersPerLane = 50;

    /// <summary>Boss-only lanes (Atlas Notable Blightspawn) send repeated Unique Blight Bosses (4 per lane by default).</summary>
    public const int BossOnlyLaneCount = 4;

    /// <summary>The build phase: 758 frames before the first wave starts unless skipped via Fast-Forward (0x0264).</summary>
    public const int BuildFrames = 758;

    /// <summary>The spawn interval per lane, in frames (scaled by Immune Response / Teal Oil spawn rate).</summary>
    public const int FirstLaneFrames = 9, OtherLaneFrames = 22;

    /// <summary>Base Blighted / Blight-ravaged encounter duration in seconds (300s = 5 minutes, reduced by Teal Oil).</summary>
    public const double BaseBlightedDurationSeconds = 300.0;

    /// <summary>The empty foundations go 44 frames after the defence ends.</summary>
    public const int FoundationsGoFrames = 44;

    static readonly (int Frame, int State, int Value, int Transition)[] PumpTimeline =
    [
        (4, PumpBuildStep, 1, -1), (7, PumpBuildStep, 2, -1), (10, PumpBuildStep, 3, -1), (13, PumpBuildStep, 4, -1), (16, PumpBuildStep, 5, -1),
        (19, PumpBuildStep, 6, -1), (22, PumpBuildStep, 7, -1), (25, PumpBuildStep, 8, -1), (28, PumpBuildStep, 9, -1), (28, PumpNextPath, 1, 2),
        (43, PumpActivated, 2, -1), (58, PumpNextPath, 0, -1), (736, -1, 0, 3),
    ];

    /// <summary>Starting resource (710 standard; higher for 20-portal Blighted / Blight-ravaged maps).</summary>
    public const uint StartResource = 710, BlightedStartResource = 1200, UberBlightedStartResource = 1500;

    /// <summary>Fallback pump durability when BlightBalancePerLevel isn't loaded.</summary>
    public const int MapDurability = 10, BlightedMapDurability = 20;

    public const int RaiseSteps = 9;
    const double TowerInterval = 1.0;
    const int PumpReach = 4;
    const int MaxBankedLootsplosionItems = 24;
    const string StateKey = "Blight", NoPumps = "[]";

    static readonly JsonSerializerOptions Json = new() { IncludeFields = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
    const double SaveEvery = 5;

    /// <summary>Instances currently affected by Fungal Horizons' Blightreach buff (BuffDefinitions 2512).</summary>
    static readonly Store<InstanceState, object> BlightreachInstances = new();
    static readonly object BlightreachMarker = new();

    public readonly Store<InstanceState, Run> Runs = new();

    /// <summary>
    /// Reads the instance's authoritative area stats WITHOUT double-counting Atlas passives
    /// (<c>AreaLeagueStats</c> in Atlas.cs adds AtlasStats on top of MapModsOf which already returns KeptAreaStats).
    /// </summary>
    public static IReadOnlyDictionary<uint, int> AreaStats(Player player)
    {
        if (player.Instance?.KeptAreaStats is { Count: > 0 } kept) return kept;
        if (MapModsOf(player) is { } mods && mods.Stats.Count > 0) return mods.Stats;
        return AreaLeagueStats(player);
    }

    /// <summary>GameEvents.MapOpening: claims "Blight", disables Atlas passives on Blighted/Blight-ravaged maps,
    /// and enforces <c>map_item_level_override</c> (level 85 on Blight-ravaged maps).</summary>
    public static void OnMapOpening(MapOpening request)
    {
        request.HandledContent.Add(Content);
        if (request.Opening is not { } opening || request.Campaign) return;
        bool blighted = opening.Mods.Get(Stat.IsBlightedMap) > 0 || opening.Mods.Get(Stat.IsUberBlightedMap) > 0;
        if (!blighted) return;
        int levelOverride = opening.Mods.Get(Stat.MapItemLevelOverride);
        var area = opening.Area with
        {
            UsesAtlas = false,
            Level = levelOverride > 0 ? levelOverride : opening.Area.Level,
        };
        request.Opening = opening with { Area = area };
    }

    static double Frames(int frames) => frames * Program.FrameMs / 1000.0;
    static bool Current(Player player) => player.Connection.PlayerSent && !player.LeavingArea && !player.Closed;

    static List<Player> Members(Player player)
    {
        var members = InGame.Keys.Where(c => c.HasClient && ReferenceEquals(c.Instance, player.Instance) && Current(c)).ToList();
        if (!members.Contains(player) && Current(player)) members.Insert(0, player);
        return members;
    }

    public static byte LevelOf(Player player)
    {
        int overrideLvl = AreaStats(player).GetValueOrDefault(Stat.MapItemLevelOverride);
        if (overrideLvl > 0) return (byte)Math.Clamp(overrideLvl, 1, 100);
        return (byte)Math.Clamp(MapRunOf(player) is { Area.Level: > 0 } map ? map.Area.Level : ServerLevels.AreaLevel(player.Layout.Id), 1, 100);
    }

    static bool Framed(Player player) => player.Area.Owned && player.Connection.WorldOutput.Started;

    static async Task Shared(Player player, Func<Player, Task> send)
    {
        if (Framed(player)) { await send(player); return; }
        foreach (var member in Members(player)) await send(member);
    }

    static Task Update(Player player, uint id, Func<byte[]> make, string log) => Shared(player, member => SendWorldRecord(member, id, make(), log));
    static Task Remove(Player player, uint id, string log) => Shared(player, member => SendWorldRemoval(member, id, log));

    static async Task Tell(Player player, Func<byte[]> make, string log)
    {
        foreach (var member in Members(player)) await Send(member, make(), log);
    }

    static async Task RegisterType(Player player, int row, byte level, string log)
    {
        if (!player.MonsterTypes.Add((row, level))) return;
        foreach (var member in Members(player)) await Send(member, InstanceEntities.MonsterTypes([((ushort)row, level)]), log);
    }

    // ---- The instance's defences, kept with the instance ----------------------------------------------------------------------

    Run? Of(Player player)
    {
        if (player.Instance is not { } instance) return null;
        if (Runs.TryGetValue(instance, out var run)) return run;
        if (!instance.ContentState.TryGetValue(StateKey, out var text) || text == NoPumps) return null;
        try { return Runs.GetValue(instance, _ => Restore(player, text)); }
        catch (JsonException e)
        {
            Console.WriteLine($"[blight] {player.Layout.Id}: the saved defences cannot be read ({e.Message}); the instance starts them anew");
            instance.ContentState.TryRemove(StateKey, out _);
            return null;
        }
    }

    /// <summary>Whether an area can hold a pump defence (either a map or any campaign area whose generated level places a BlightPump).</summary>
    public bool MayHaveDefence(Player player) =>
        MapDevices.IsMapArea(player.Layout.Hash)
        || Of(player) is not null
        || (player.Layout.Level?.RoomObjects.Any(o => o.Type == PumpObject) ?? false);

    Run? Place(Player player)
    {
        if (player.Instance is null) return null;
        var pumps = player.Layout.Level?.RoomObjects.Where(o => o.Type == PumpObject).Select(o => (o.X, o.Y, o.Rotation)).ToList() ?? [];
        if (pumps.Count == 0)
        {
            player.Instance.ContentState[StateKey] = NoPumps;
            return null;
        }
        return Place(player, pumps);
    }

    /// <summary>The instance's defences, one at each of <paramref name="pumps"/> (honoring Blighted/Uber-Blighted,
    /// BlightBalancePerLevel durability, Sturdy Construction 2x durability, and Valdo's 1 durability).</summary>
    public Run Place(Player player, IEnumerable<(int X, int Y, float Rotation)> pumps)
    {
        byte level = LevelOf(player);
        var stats = AreaStats(player);
        bool uberBlighted = stats.GetValueOrDefault(Stat.IsUberBlightedMap) > 0 || stats.GetValueOrDefault(Stat.IsBlightedMap) >= 9;
        bool blighted = uberBlighted || stats.GetValueOrDefault(Stat.IsBlightedMap) > 0;
        var balance = BlightTables.BalanceFor(level, blighted, uberBlighted);
        int baseDurability = balance.PumpDurability > 0 ? balance.PumpDurability : (blighted ? BlightedMapDurability : MapDurability);
        if (stats.GetValueOrDefault(Stat.MapInchorPumpHasDoubleDurability) > 0) baseDurability *= 2;
        if (stats.GetValueOrDefault(Stat.MapIchorPumpOneDurability) > 0) baseDurability = 1;
        double duration = blighted
            ? Math.Max(60.0, BaseBlightedDurationSeconds - Math.Max(0, stats.GetValueOrDefault(Stat.MapBlightedMapEncounterDurationMinusSec)))
            : 0.0;

        return Runs.GetValue(player.Instance, _ =>
        {
            var run = new Run();
            foreach (var (x, y, rotation) in pumps)
            {
                var d = new Defence(player.Instance.World.TakeObjectId(), (x, y), rotation, level)
                {
                    Blighted = blighted,
                    UberBlighted = uberBlighted,
                    MaxDurability = baseDurability,
                    Durability = baseDurability,
                    EncounterDuration = duration,
                };
                d.PumpStates[PumpHealth] = d.Durability;
                d.PumpStates[PumpReadyToStart] = 1;
                run.Defences.Add(d);
            }
            return run;
        });
    }

    Run Restore(Player player, string text)
    {
        var saved = JsonSerializer.Deserialize<SavedRun>(text, Json) ?? new();
        var run = new Run();
        foreach (var s in saved.Defences)
        {
            var d = new Defence(s.Pump, (s.CellX, s.CellY), s.Rotation, s.Level)
            {
                Phase = s.Phase, Durability = s.Durability, MaxDurability = s.MaxDurability, Resource = s.Resource, StartedAt = s.StartedAt,
                EndedAt = s.EndedAt, EncounterDuration = s.EncounterDuration, PumpTransition = (byte)s.PumpTransition, PumpStep = s.PumpStep,
                MonstersPerLane = s.MonstersPerLane, Shown = s.Shown, Blighted = s.Blighted,
                UberBlighted = s.UberBlighted, PlanSeed = s.PlanSeed, CassiaId = s.CassiaId, CassiaCell = (s.CassiaX, s.CassiaY),
                KilledCount = s.KilledCount, ExtraBossChests = s.ExtraBossChests,
            };
            Array.Copy(s.PumpStates, d.PumpStates, Math.Min(s.PumpStates.Length, d.PumpStates.Length));
            foreach (var p in s.Spots)
            {
                var spot = new Spot(p.Id, p.X, p.Y) { Row = p.Row, Raising = p.Raising, Upgrade = p.Upgrade, NextFire = p.NextFire, SkillId = p.SkillId };
                if (p.Minions is { Count: > 0 }) spot.Minions.AddRange(p.Minions);
                d.Spots[p.Id] = spot;
            }
            foreach (var (lane, at) in s.LaneNext) d.LaneNext[lane] = at;
            foreach (var (lane, n) in s.LaneSpawned) d.LaneSpawned[lane] = n;
            foreach (var (lane, et) in s.LaneEncounterTypes) d.LaneEncounterTypes[lane] = et;
            foreach (var (lane, rw) in s.LaneRewards) d.LaneRewards[lane] = rw;
            d.BossLanes.UnionWith(s.BossLanes);
            d.BossOnlyLanes.UnionWith(s.BossOnlyLanes);
            foreach (var m in s.Monsters) d.Monsters[m.Id] = (m.Lane, m.Rarity);
            foreach (var (id, i) in s.PathIndex) d.PathIndex[id] = i;
            d.Resolved.UnionWith(s.Resolved);
            foreach (var c in s.Chests) d.Chests[c.Id] = c;
            if (d.Phase != Phase.Ready) d.Plan = MakePlan(player, d);
            run.Defences.Add(d);
        }
        return run;
    }

    void Save(Player player, Run run)
    {
        if (player.Instance is not { } instance) return;
        var saved = new SavedRun();
        lock (run.Gate)
            foreach (var d in run.Defences)
                lock (d)
                    saved.Defences.Add(new SavedDefence
                    {
                        Pump = d.Pump, CassiaId = d.CassiaId, CassiaX = d.CassiaCell.X, CassiaY = d.CassiaCell.Y,
                        CellX = d.Cell.X, CellY = d.Cell.Y, Rotation = d.Rotation, Level = d.Level, Blighted = d.Blighted,
                        UberBlighted = d.UberBlighted, Shown = d.Shown, Phase = d.Phase, Durability = d.Durability,
                        MaxDurability = d.MaxDurability, PumpTransition = d.PumpTransition, PumpStep = d.PumpStep,
                        MonstersPerLane = d.MonstersPerLane, PlanSeed = d.PlanSeed, Resource = d.Resource, StartedAt = d.StartedAt,
                        EndedAt = d.EndedAt, EncounterDuration = d.EncounterDuration,
                        KilledCount = d.KilledCount, ExtraBossChests = d.ExtraBossChests, PumpStates = [.. d.PumpStates],
                        Spots = [.. d.Spots.Values.Select(s => new SavedSpot { Id = s.Id, X = s.X, Y = s.Y, Row = s.Row, Raising = s.Raising, SkillId = s.SkillId, Upgrade = s.Upgrade, NextFire = s.NextFire, Minions = [.. s.Minions] })],
                        LaneNext = new(d.LaneNext), LaneSpawned = new(d.LaneSpawned), LaneEncounterTypes = new(d.LaneEncounterTypes),
                        LaneRewards = new(d.LaneRewards), BossLanes = [.. d.BossLanes], BossOnlyLanes = [.. d.BossOnlyLanes],
                        PathIndex = new(d.PathIndex), Resolved = [.. d.Resolved],
                        Monsters = [.. d.Monsters.Select(m => new SavedMonster { Id = m.Key, Lane = m.Value.Lane, Rarity = m.Value.Rarity })],
                        Chests = [.. d.Chests.Values],
                    });
        instance.ContentState[StateKey] = JsonSerializer.Serialize(saved, Json);
    }

    // ---- Showing the defences ---------------------------------------------------------------------------------------------------

    public async Task Enter(Player player)
    {
        var run = Of(player) ?? Place(player);
        if (run is null) return;
        List<Defence> defences;
        lock (run.Gate) defences = [.. run.Defences];
        bool changed = false;
        foreach (var d in defences)
        {
            bool first;
            List<SpotView> spots;
            List<BlightChestState> chests;
            lock (d)
            {
                first = !d.Shown;
                d.Shown = true;
                spots = [.. d.Spots.Values.Select(View)];
                chests = [.. d.Chests.Values];
            }
            if (first)
            {
                changed = true;
                await ShowPump(player, d);
                await BlightCassia.SpawnNearPump(player, d, Members);
                foreach (var spot in spots) await ShowSpot(player, d, spot);
            }
            else
            {
                // Register existing wild Cassia in late-joining or returning player's Npcs dictionary.
                await BlightCassia.SpawnNearPump(player, d, Members);
            }
            foreach (var chest in chests)
                await BlightChests.ShowChest(player, chest, Shared, Members);
            bool running;
            lock (d) running = d.Phase != Phase.Ready;
            if (running) await SendResource(player, d);
        }
        if (changed) Save(player, run);
    }

    static SpotView View(Spot s) => new(s.Id, s.X, s.Y, s.Row, s.Raising, s.Upgrade);

    async Task ShowPump(Player player, Defence d)
    {
        byte[] blob;
        lock (d) blob = PumpBlob(d);
        uint type = InstanceEntities.ObjectTypeHash(PumpObject);
        await Shared(player, member => SendWorldCreate(member, d.Pump, () => InstanceEntities.CreateObject(d.Pump, type, blob),
            $"Blight pump 0x{d.Pump:x} at {d.Cell}", wake: true, awaitBirth: false));
    }

    /// <summary>The variety of a BlightTowers row (the foundation for row 0), when its create can be written.</summary>
    static MonsterCatalog.Variety? VarietyOf(int row) =>
        BlightTables.Towers.ElementAtOrDefault(row) is { Variety: >= 0 } t && Monsters.Find(t.Variety.ToString()) is { Supported: true, StateMachine: not null } v ? v : null;

    static List<GrantedSkill> BuildTowerSkills(Player player, MonsterCatalog.Variety variety, byte level)
    {
        var skills = new List<GrantedSkill>();
        if (variety.GrantedEffects is not { Count: > 0 } effects) return skills;
        var catalog = SkillsFor(player);
        ushort nextId = 0x4100; // Disjoint from player gem skill IDs (0x0103..0x0142) and monster defaults.
        foreach (int effectRow in effects)
        {
            if (catalog.FindByRow(nextId, effectRow, level) is { } granted)
            {
                skills.Add(granted);
                nextId++;
            }
        }
        return skills;
    }

    async Task ShowSpot(Player player, Defence d, SpotView spot)
    {
        if (VarietyOf(spot.Row) is not { } variety) return;
        byte level = d.Level;
        await RegisterType(player, variety.Row, level, $"0x0020 monster type {variety.Id}@{level}");
        var states = SpotStates(variety, spot);
        var skills = spot.Row > 0 ? BuildTowerSkills(player, variety, level) : [];
        if (skills.Count > 0)
            lock (d)
                if (d.Spots.TryGetValue(spot.Id, out var liveSpot)) liveSpot.SkillId = skills[0].Id;
        uint life = (uint)Math.Max(1, Monsters.BaseLife(variety, level) ?? 1);
        var blob = new MonsterObject(spot.X, spot.Y, life, variety.ComponentsOrBase, StateMachine: states, TransitionState: variety.TransitionStates,
            Team: 0, Skills: skills.Count > 0 ? skills : null, Stats: [(Stat.MaximumLife, (int)life)]) { LifeData = variety.LifeData }.Blob();
        uint type = InstanceEntities.ObjectTypeHash(MonsterObject.TypePath(variety.Id, level));
        await Shared(player, member => SendWorldCreate(member, spot.Id, () => InstanceEntities.CreateObject(spot.Id, type, blob),
            $"Blight {variety.Name} 0x{spot.Id:x}", wake: true, awaitBirth: false));
    }

    static StateMachineData SpotStates(MonsterCatalog.Variety variety, SpotView spot)
    {
        var s = variety.StateMachine!;
        if (spot.Row == 0) return s;
        int step = spot.Raising;
        if (step is > 0 and < RaiseSteps) return s.Set("show_segments", step).Set("building", 1).Set("build_pending", 1);
        return s.Set("show_segments", 8).Set("building", spot.Upgrade ? 0 : 1).Set("show_effects", 1).Set("tower_active", 1);
    }

    public static byte[] PumpBlob(Defence d)
    {
        var w = new BlobWriter();
        w.U8(0).U8(0);
        w.I32(d.Cell.X).I32(d.Cell.Y).F32(d.Rotation).U16(1).U8(1).U8(0);
        w.Stats([(VirtualOnFullLife, 1)]).U32(0).F32(3f).U8(1);
        w.U16(0);
        Pool(w, 1); Pool(w, 0); Pool(w, 0); w.U8(0);
        w.U8(0x40);
        new StateMachineData(d.PumpStates, 0).Write(w);
        w.U8(d.PumpTransition).U8(0).U8((byte)(d.PumpStates[PumpReadyToStart] != 0 && d.Phase == Phase.Ready ? 1 : 0)).U8(0);
        return w.ToArray();

        static void Pool(BlobWriter w, uint current) => w.U32(current).U32(0).U16(0).U32(0).U32(0);
    }

    // ---- Telling the instance ----------------------------------------------------------------------------------------------------

    async Task SendPumpStates(Player player, Defence d, string why)
    {
        int[] states;
        lock (d) states = [.. d.PumpStates];
        await Update(player, d.Pump, () => InstanceEntities.StateMachineUpdate(d.Pump, PumpStateMachine, new StateMachineData(states, 0)), $"Blight pump 0x{d.Pump:x} {why}");
    }

    Task SendPumpTransition(Player player, Defence d, byte to)
    {
        lock (d) d.PumpTransition = to;
        return Update(player, d.Pump, () => InstanceEntities.TransitionableUpdate(d.Pump, PumpTransitionable, to), $"Blight pump 0x{d.Pump:x} Transitionable {to}");
    }

    static Task SendResource(Player player, Defence d)
    {
        uint total;
        lock (d) total = d.Resource;
        return Tell(player, () => InstanceBlight.Resource(total), $"0x026a Blight resource {total}");
    }

    // ---- Starting & Fast-Forward -------------------------------------------------------------------------------------------------

    public async Task<bool> Use(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (Of(player) is not { } run) return false;
        Defence? d;
        lock (run.Gate) d = run.Defences.FirstOrDefault(x => x.Pump == use.TargetId);
        if (d is null) return await BlightChests.UseChest(player, run, use, requestedAt, Shared, Members, Save);
        if (player.PlayerLife == 0 || player.Closed || player.LeavingArea || InGracePeriod(player)) return true;
        bool ready;
        lock (d) ready = d.Phase == Phase.Ready;
        if (!ready)
        {
            int current = player.ActionCount;
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null, still: () => PlayerActionCurrent(player, current));
            return true;
        }
        int reach = Math.Max(0, StationaryCollision.Resolve(PumpObject).Size);
        if (!TryBeginObjectWalk(player, requestedAt, d.Cell.X, d.Cell.Y, reach, out var walk)) return true;
        var (action, now, here, _, arrive) = walk;
        if (!use.Started)
            await SendWorldRecord(player, player.Id,
                InstanceProtocol.ActionStarted(player.Id, here.X, here.Y, d.Cell.X, d.Cell.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: d.Pump), null,
                still: () => PlayerActionCurrent(player, action));
        try
        {
            if (arrive > now) await AreaDelay(player, arrive - now);
            if (!PlayerActionCurrent(player, action) || player.PlayerLife == 0 || player.Closed || player.LeavingArea
                || !GameServer.Core.World.PlayerWalk.InWalkUpRange(player.Walk.Position(AreaTime(player)), d.Cell, reach)) return true;
            await Start(player, d);
        }
        finally
        {
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null, still: () => PlayerActionCurrent(player, action));
        }
        return true;
    }

    /// <summary>C→S 0x0264: Fast-Forward button — skips remaining build-phase timer on any Building defence in the instance.</summary>
    public Task<bool> FastForward(Player player, byte[] bytes)
    {
        if (Of(player) is not { } run) return Task.FromResult(false);
        double now = AreaTime(player);
        bool skipped = false;
        lock (run.Gate)
            foreach (var d in run.Defences)
                lock (d)
                {
                    if (d.Phase != Phase.Building || d.Plan is not { } plan) continue;
                    double remaining = Math.Max(0, (d.StartedAt + Frames(BuildFrames)) - now);
                    d.Phase = Phase.Running;
                    d.StartedAt -= remaining;
                    for (int lane = 0; lane < plan.Lanes.Count; lane++)
                        d.LaneNext[lane] = Math.Max(now, d.LaneNext.GetValueOrDefault(lane) - remaining);
                    skipped = true;
                }
        if (skipped) Save(player, run);
        return Task.FromResult(skipped);
    }

    BlightLayout.Plan? MakePlan(Player player, Defence d)
    {
        var rng = new Random(d.PlanSeed);
        bool Walkable(int x, int y) => x >= 0 && y >= 0 && x < player.Layout.GridW && y < player.Layout.GridH && player.Layout.WalkableFor(x, y, 1);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (BlightLayout.Pick(BlightTables.Topologies, d.Level, rng, d.Blighted) is not { } topology) return null;
            var plan = BlightLayout.Lay(BlightTables.Nodes, topology, d.Cell, Walkable, rng);
            if (plan.Lanes.Count > 0)
            {
                d.FirstLane = plan.Lanes.Select((l, i) => (l.Delay, i)).Min().i;
                return plan;
            }
        }
        return null;
    }

    public async Task Start(Player player, Defence d)
    {
        double now = AreaTime(player);
        var stats = AreaStats(player);
        List<SpotView> spots;
        lock (d)
        {
            if (d.Phase != Phase.Ready) return;
            d.PlanSeed = HashCode.Combine(player.Seed, d.Cell.X, d.Cell.Y);
            if (MakePlan(player, d) is not { } plan)
            {
                Console.WriteLine($"[blight] {player.Layout.Id}: pump 0x{d.Pump:x} at {d.Cell} has no walkable lane; the defence does not start");
                return;
            }
            d.Plan = plan;
            d.Phase = Phase.Building;
            d.StartedAt = now;
            d.PumpStates[PumpReadyToStart] = 0;
            d.PumpStates[PumpActivated] = 1;
            d.PumpStep = 0;
            int pack = Math.Max(10, 100 + stats.GetValueOrDefault(Stat.MapBlightEncounterPackSizePlusPctFinal));
            int subWaves = Math.Max(0, stats.GetValueOrDefault(Stat.MapBlightHasXSubWaves));
            int baseCount = d.UberBlighted ? 75 : d.Blighted ? 60 : BaseMonstersPerLane;
            d.MonstersPerLane = Math.Max(1, baseCount * pack / 100 + subWaves * 10);
            d.Resource = d.UberBlighted ? UberBlightedStartResource : d.Blighted ? BlightedStartResource : StartResource;

            var laneRng = new Random(d.PlanSeed ^ 0x517cc1b7);
            int bossOnlyCount = Math.Max(0, stats.GetValueOrDefault(Stat.MapBlightHasXBossLanes));
            int extraBosses = Math.Max(0, stats.GetValueOrDefault(Stat.MapBlightUpToXAdditionalBosses));
            d.LaneEncounterTypes.Clear();
            var laneTypes = BlightWaves.PickLaneTypes(plan.Lanes.Count, bossOnlyCount, laneRng, out var bossOnly);
            foreach (var (lane, encType) in laneTypes)
                d.LaneEncounterTypes[lane] = encType;
            d.BossOnlyLanes.Clear();
            d.BossOnlyLanes.UnionWith(bossOnly);

            d.LaneRewards.Clear();
            foreach (var (lane, reward) in BlightChests.PickLaneRewards(plan.Lanes.Count, Program.LeagueFor(player).HardMode, stats, laneRng))
                d.LaneRewards[lane] = reward;

            d.BossLanes.Clear();
            d.BossLanes.UnionWith(BlightWaves.PickBossLanes(plan.Lanes.Count, d.Level, d.Blighted, d.UberBlighted, extraBosses, d.BossOnlyLanes, laneRng));

            for (int lane = 0; lane < plan.Lanes.Count; lane++)
                d.LaneNext[lane] = now + Frames(BuildFrames) + plan.Lanes[lane].Delay;
            foreach (var (x, y) in plan.Foundations)
            {
                uint id = player.Instance.World.TakeObjectId();
                d.Spots[id] = new Spot(id, x, y);
            }
            spots = [.. d.Spots.Values.Select(View)];
        }
        await SendPumpStates(player, d, "activated");
        await Update(player, d.Pump, () => InstanceEntities.ComponentUpdate(d.Pump, PumpInteraction, [0]), $"Blight pump 0x{d.Pump:x} InteractionAction 0");
        foreach (var spot in spots) await ShowSpot(player, d, spot);
        await SendResource(player, d);
        await BlightCassia.Say(player, BlightCassia.StartVoice, Members);
        foreach (var member in Members(player))
            await BlightCassia.GrantFlags(member, [BlightTables.QuestFlagIds.Received, BlightTables.QuestFlagIds.Started]);
        if (Of(player) is { } run) Save(player, run);
    }

    // ---- The tick --------------------------------------------------------------------------------------------------------------

    public async Task Step(Player player)
    {
        if (!Current(player) || player.Paused || Of(player) is null || !IsMonsterRunner(player)) return;
        await Tick(player, AreaTime(player));
    }

    public Task Tick(Player player) => Tick(player, AreaTime(player));

    public async Task Tick(Player player, double now)
    {
        if (Of(player) is not { } run) return;
        List<Defence> defences;
        lock (run.Gate) defences = [.. run.Defences];
        foreach (var d in defences)
        {
            Phase phase;
            lock (d) phase = d.Phase;
            if (phase is Phase.Ready) continue;
            await Timeline(player, d, now);
            if (phase is Phase.Won or Phase.Lost) { await RemoveFoundations(player, d, now); continue; }
            await Settle(player, d, now);
            await RaiseTowers(player, d, now);
            if (d.Phase == Phase.Running)
            {
                await Spawn(player, d, now);
                await March(player, d, now);
                await BlightTowers.StepTowers(player, d, now, Members, VarietyOf, Update);
                await CheckEnd(player, d, now);
            }
            else if (d.Phase == Phase.Building)
            {
                await BlightTowers.StepTowers(player, d, now, Members, VarietyOf, Update);
                if (now >= d.StartedAt + Frames(BuildFrames))
                    lock (d) d.Phase = Phase.Running;
            }
        }
        if (now >= run.NextSave)
        {
            run.NextSave = now + SaveEvery;
            Save(player, run);
        }
    }

    async Task Timeline(Player player, Defence d, double now)
    {
        while (true)
        {
            bool changed = false;
            int transition = -1;
            lock (d)
            {
                if (d.Phase is Phase.Won or Phase.Lost || d.PumpStep >= PumpTimeline.Length || now < d.StartedAt + Frames(PumpTimeline[d.PumpStep].Frame)) break;
                int frame = PumpTimeline[d.PumpStep].Frame;
                while (d.PumpStep < PumpTimeline.Length && PumpTimeline[d.PumpStep].Frame == frame)
                {
                    var entry = PumpTimeline[d.PumpStep++];
                    if (entry.State >= 0 && d.PumpStates[entry.State] != entry.Value) { d.PumpStates[entry.State] = entry.Value; changed = true; }
                    if (entry.Transition >= 0) transition = entry.Transition;
                }
            }
            if (changed) await SendPumpStates(player, d, "timeline");
            if (transition >= 0) await SendPumpTransition(player, d, (byte)transition);
        }
    }

    async Task Settle(Player player, Defence d, double now)
    {
        List<(uint Id, SpawnedMonster M, MonsterRarity Rarity)> leaked = [];
        lock (d)
            foreach (var (id, info) in d.Monsters)
            {
                if (d.Resolved.Contains(id)) continue;
                if (!player.Area.Monsters.TryGetValue(id, out var m) || m.Dead || m.Life == 0) { d.Resolved.Add(id); continue; }
                var (x, y) = m.Walk.Position(now);
                if (Math.Max(Math.Abs(x - d.Cell.X), Math.Abs(y - d.Cell.Y)) <= PumpReach) leaked.Add((id, m, info.Rarity));
            }
        foreach (var (id, m, rarity) in leaked)
        {
            int loss = rarity == MonsterRarity.Unique ? BlightWaves.BossDurabilityLoss : 1;
            lock (d)
            {
                if (d.Phase is Phase.Won or Phase.Lost || !d.Resolved.Add(id)) continue;
                d.Durability = Math.Max(0, d.Durability - loss);
                d.PumpStates[PumpHealth] = d.Durability;
            }
            await RemoveMonster(player, id, m);
            await SendPumpStates(player, d, $"health {d.Durability}");
            if (d.Durability == 0) { await Finish(player, d, now, won: false); return; }
        }
    }

    static async Task RemoveMonster(Player player, uint id, SpawnedMonster m)
    {
        using (ProjectileMutation(player))
        {
            if (m.Dead || !player.Area.Monsters.ContainsKey(id)) return;
            m.Dead = true;
            m.Aggro = false;
            m.Life = 0;
            m.ActionGeneration++;
            player.Area.Monsters.Remove(id);
        }
        await Remove(player, id, $"Blight monster 0x{id:x} leaves the fight");
        await SendRemainingMonsters(player);
    }

    int LaneTargetCount(Defence d, int lane) =>
        d.BossOnlyLanes.Contains(lane) ? Math.Min(d.MonstersPerLane, BossOnlyLaneCount) : d.MonstersPerLane;

    async Task Spawn(Player player, Defence d, double now)
    {
        if (d.Plan is not { } plan) return;
        var rng = AreaRng(player);
        var stats = AreaStats(player);
        double spawnRateFactor = Math.Max(0.25, 1.0 + stats.GetValueOrDefault(Stat.MapBlightEncounterSpawnRatePlusPct) / 100.0);
        // If a Blighted/Blight-ravaged timer has elapsed (300s - Teal Oil), stop spawning new wave monsters.
        bool timedOut = d.EncounterDuration > 0 && now >= d.StartedAt + Frames(BuildFrames) + d.EncounterDuration;

        for (int lane = 0; lane < plan.Lanes.Count; lane++)
        {
            int index, encType, totalPerLane;
            bool hasBoss, bossOnly;
            lock (d)
            {
                totalPerLane = LaneTargetCount(d, lane);
                if (timedOut) { d.LaneSpawned[lane] = totalPerLane; continue; }
                if (now < d.LaneNext.GetValueOrDefault(lane) || d.LaneSpawned.GetValueOrDefault(lane) >= totalPerLane) continue;
                double baseStep = Frames(lane == d.FirstLane ? FirstLaneFrames : OtherLaneFrames);
                if (d.BossOnlyLanes.Contains(lane)) baseStep *= 4.0;
                d.LaneNext[lane] = Math.Max(d.LaneNext[lane] + baseStep / spawnRateFactor, now);
                index = d.LaneSpawned.GetValueOrDefault(lane) + 1;
                encType = d.LaneEncounterTypes.GetValueOrDefault(lane, 0);
                hasBoss = d.BossLanes.Contains(lane);
                bossOnly = d.BossOnlyLanes.Contains(lane);
            }
            var rarity = BlightWaves.RarityFor(index, totalPerLane, hasBoss, bossOnly, rng);
            if (BlightWaves.VarietyFor(encType, lane, index, rarity) is not { } variety)
            {
                lock (d) d.LaneSpawned[lane] = totalPerLane;
                continue;
            }
            var end = plan.Lanes[lane].End;
            uint? ownedLife = rarity == MonsterRarity.Unique ? BlightWaves.ComputeBossLife(variety, d.Level, stats) : null;
            uint id = await SpawnMonster(player, variety, d.Level, rarity, end.X, end.Y,
                log: rarity == MonsterRarity.Unique, alone: false, instanceOwned: true, ownedMaximumLife: ownedLife);
            if (id == 0) continue;
            lock (d)
            {
                d.LaneSpawned[lane] = index;
                d.Monsters[id] = (lane, rarity);
                d.PathIndex[id] = plan.Lanes[lane].Path.Count - 1;
            }
        }
    }

    async Task March(Player player, Defence d, double now)
    {
        if (d.Plan is not { } plan) return;
        List<(uint Id, (int X, int Y) From, (int X, int Y) To)> orders = [];
        lock (d)
            foreach (var (id, info) in d.Monsters)
            {
                if (d.Resolved.Contains(id) || !d.PathIndex.TryGetValue(id, out int index) || index <= 0) continue;
                if (!player.Area.Monsters.TryGetValue(id, out var m) || m.Dead || m.Life == 0 || m.Aggro || now < m.Walk.ArrivesAt) continue;
                if (m.Ailments.ActionFactor(now) <= 0 || m.Effects[Stat.ActionSpeedMinusPct] >= 100) continue;
                var path = plan.Lanes[info.Lane].Path;
                var from = m.Walk.Position(now);
                int next = Math.Max(0, Math.Min(index, path.Count - 1) - 8);
                while (next > 0 && Math.Max(Math.Abs(path[next].X - from.X), Math.Abs(path[next].Y - from.Y)) < 3) next = Math.Max(0, next - 8);
                d.PathIndex[id] = next;
                m.ActionGeneration++;
                m.Walk.Order(path[next].X, path[next].Y, now);
                orders.Add((id, from, path[next]));
            }
        foreach (var (id, from, to) in orders)
            await Update(player, id, () => InstanceProtocol.ActionStarted(id, from.X, from.Y, to.X, to.Y, 0x2909, 0xffff, player.MonsterOrderFlags),
                "Blight enemy walks its lane");
    }

    async Task CheckEnd(Player player, Defence d, double now)
    {
        bool done;
        lock (d)
            done = d.Plan is { } plan && plan.Lanes.Count > 0
                && Enumerable.Range(0, plan.Lanes.Count).All(l => d.LaneSpawned.GetValueOrDefault(l) >= LaneTargetCount(d, l))
                && d.Monsters.Keys.All(d.Resolved.Contains);
        if (done) await Finish(player, d, now, won: true);
    }

    async Task Finish(Player player, Defence d, double now, bool won)
    {
        List<(uint Id, SpawnedMonster M)> left = [];
        lock (d)
        {
            if (d.Phase is Phase.Won or Phase.Lost) return;
            d.Phase = won ? Phase.Won : Phase.Lost;
            d.EndedAt = now;
            d.PumpStates[PumpNextPath] = 0;
            d.PumpStates[PumpActivated] = 3;
            d.PumpStates[won ? PumpSuccess : PumpFail] = 1;
            foreach (var id in d.Monsters.Keys.Where(id => !d.Resolved.Contains(id)).ToArray())
            {
                d.Resolved.Add(id);
                if (player.Area.Monsters.TryGetValue(id, out var m)) left.Add((id, m));
            }
        }
        await BlightTowers.CleanupDefence(player, d, Members);
        foreach (var (id, m) in left) await RemoveMonster(player, id, m);
        await SendPumpStates(player, d, won ? "success" : "fail");
        await SendPumpTransition(player, d, 4);

        var stats = AreaStats(player);
        var members = Members(player);
        if (won)
        {
            await BlightChests.SpawnVictoryChests(player, d, Shared, Members);
            // Fungal Horizons: grant Blightreach buff (BuffDefinitions 2512) so remaining map drops convert to Blighted maps!
            if (stats.GetValueOrDefault(Stat.MapBlightTouchedBuffOnBlightEncounterComplete) > 0 && player.Instance is { } inst)
            {
                BlightreachInstances.AddOrUpdate(inst, BlightreachMarker);
                foreach (var member in members)
                {
                    ushort buffInst = member.NextBuff++;
                    if (buffInst == 0) buffInst = member.NextBuff++;
                    await SendWorldRecord(member, member.Id, InstanceEntities.AddBuff(member.Id, buffInst, BlightTowers.BlightreachBuffRow),
                        "0x01c1 Fungal Horizons Blightreach buff 2512");
                }
            }
            var winFlags = new List<string>
            {
                BlightTables.QuestFlagIds.Success,
                BlightTables.QuestFlagIds.Complete,
                BlightTables.QuestFlagIds.QuestComplete,
                BlightTables.QuestFlagIds.AnointingUnlocked,
            };
            if (d.Level >= 68) winFlags.Add(BlightTables.QuestFlagIds.EndgameUnlocked);
            foreach (var member in members) await BlightCassia.GrantFlags(member, winFlags);
        }
        else
        {
            foreach (var member in members) await BlightCassia.GrantFlags(member, [BlightTables.QuestFlagIds.Failed]);
        }
        await BlightCassia.Say(player, won ? BlightCassia.VictoryVoice : BlightCassia.FailureVoice, Members);
        if (Of(player) is { } run) Save(player, run);
    }

    async Task RemoveFoundations(Player player, Defence d, double now)
    {
        List<uint> go;
        lock (d)
        {
            if (now < d.EndedAt + Frames(FoundationsGoFrames)) return;
            go = [.. d.Spots.Where(s => s.Value.Row == 0).Select(s => s.Key)];
            foreach (uint id in go) d.Spots.Remove(id);
        }
        foreach (uint id in go) await Remove(player, id, $"Blight foundation 0x{id:x} goes");
        if (go.Count > 0 && Of(player) is { } run) Save(player, run);
    }

    // ---- Kills, Damage Scaling & Drops -------------------------------------------------------------------------------------------

    /// <summary>GameEvents.OutgoingDamage: applies Cassia's Pride (-75% player damage vs Blight monsters) and
    /// Sturdy Construction (+12% + small nodes increased damage taken by Blight monsters). O(1) no-op outside Blight.</summary>
    public DamagePacket OnOutgoingDamage(Player player, IReadOnlyDictionary<uint, int> playerStats, SpawnedMonster target, DamagePacket rolled)
    {
        if (Of(player) is not { } run) return rolled;
        bool isBlightMonster = false;
        lock (run.Gate)
            foreach (var d in run.Defences)
                lock (d)
                    if (d.Monsters.ContainsKey(target.ObjectId)) { isBlightMonster = true; break; }
        if (!isBlightMonster) return rolled;

        var stats = AreaStats(player);
        double factor = 1.0;
        int cassiasPride = stats.GetValueOrDefault(Stat.MapBlightMonstersDamageTakenFromPlayerSourcesPlusPctFinal);
        // Atlas Keystone Cassia's Pride (PassiveSkills 4794) grants atlas_keystone_cassias_pride = 1 -> -75% player damage.
        if (cassiasPride == 0 && stats.GetValueOrDefault(Stat.AtlasKeystoneCassiasPride) > 0) cassiasPride = -75;
        if (cassiasPride != 0) factor *= Math.Max(0.05, 1.0 + cassiasPride / 100.0);
        int sturdyTakePct = stats.GetValueOrDefault(Stat.MapBlightMonstersTakePlusPctDamage);
        if (sturdyTakePct != 0) factor *= Math.Max(0.1, 1.0 + sturdyTakePct / 100.0);
        return Math.Abs(factor - 1.0) < 1e-6 ? rolled : rolled.Scale(factor);
    }

    /// <summary>GameEvents.Drops: scales Blight monster drops (Atlas IIQ + Scarab of Invigoration), banks drops for
    /// the Pump Victory Lootsplosion, drops anointed items on bosses (if stat active), and converts maps under Blightreach.</summary>
    public void OnDrops(DropRoll roll)
    {
        var player = roll.Player;
        var inst = player.Instance;
        if (inst is null) return;

        // 1. Fungal Horizons Blightreach buff: convert dropped regular maps into Blighted Maps for the rest of the area.
        if (BlightreachInstances.TryGetValue(inst, out _))
        {
            var stats = AreaStats(player);
            int convPct = stats.GetValueOrDefault(Stat.MapDropChanceToConvertToBlightMapPct);
            int chance = convPct > 0 ? convPct : 100;
            bool blooming = stats.GetValueOrDefault(Stat.MapBlightMapsDropAsUberBlightMaps) > 0;
            bool randomAnoints = stats.GetValueOrDefault(Stat.MapBlightedMapsDropWithRandomAnoints) > 0;
            var modCatalog = ItemsFor(player).Mods;
            roll.Changes.Add((drop, rng) =>
            {
                if (!CurrencyEffects.IsMap(drop.Item, drop.Type) || rng.Next(100) >= chance) return [drop];
                bool uber = blooming && drop.Item.ItemLevel >= 81;
                return [drop with { Item = BlightChests.MakeBlightedMap(drop.Item, uber, randomAnoints, rng, modCatalog) }];
            });
        }

        // 2. Blight defence monster drop scaling & lootsplosion banking.
        if (Of(player) is not { } run) return;
        Defence? owner = null;
        bool enhanced = false;
        lock (run.Gate)
            foreach (var d in run.Defences)
                lock (d)
                    if (d.Monsters.ContainsKey(roll.Monster.ObjectId))
                    {
                        owner = d;
                        enhanced = d.EnhancedMonsters.Contains(roll.Monster.ObjectId);
                        break;
                    }
        if (owner is null) return;

        var areaStats = AreaStats(player);
        int monsterIiq = areaStats.GetValueOrDefault(Stat.MapBlightMonsterItemQuantityPlusPct);
        if (monsterIiq > 0) roll.Quantity *= 1.0 + monsterIiq / 100.0;
        if (enhanced)
        {
            // Scarab of Invigoration: enhanced monsters grant substantially more item quantity & rarity.
            roll.Quantity *= 1.5;
            roll.Rarity *= 1.5;
        }
        // Blight bosses drop X anointed items (map_blight_bosses_drop_X_anointed_items).
        if (roll.Monster.Rarity == MonsterRarity.Unique)
        {
            int anointedDrops = areaStats.GetValueOrDefault(Stat.MapBlightBossesDropXAnointedItems);
            if (anointedDrops > 0)
            {
                var rng = AreaRng(player);
                for (int i = 0; i < anointedDrops; i++)
                    if (BlightAnointing.RollRandomAnointedJewellery(player, owner.Level, rng) is { } jew)
                        roll.Made.Add(jew);
            }
        }
        // Bank a sample of monster drops for the Pump Victory Lootsplosion when defence finishes.
        roll.AddTransform(drops =>
        {
            if (drops.Count == 0) return;
            lock (owner)
            {
                if (owner.Phase != Phase.Running || owner.BankedPumpLoot.Count >= MaxBankedLootsplosionItems) return;
                // Keep up to 1 item per qualifying kill banked for the pump explosion (rest drop normally).
                int take = Math.Min(1, MaxBankedLootsplosionItems - owner.BankedPumpLoot.Count);
                for (int i = 0; i < take && drops.Count > 0; i++)
                {
                    owner.BankedPumpLoot.Add(drops[^1].Item);
                    drops.RemoveAt(drops.Count - 1);
                }
            }
        });
    }

    /// <summary>A kill (the player's or a minion's): grants Blight resource, increments kill counter, and rolls Blightspawn boss extra chests.</summary>
    public async Task Killed(KillContext kill)
    {
        var player = kill.Player;
        if (Of(player) is not { } run) return;
        Defence? d = null;
        int gained = 0;
        bool wasBoss = false;
        lock (run.Gate)
            foreach (var x in run.Defences)
                lock (x)
                    if (x.Monsters.TryGetValue(kill.Monster.ObjectId, out var info) && x.Resolved.Add(kill.Monster.ObjectId))
                    {
                        d = x;
                        wasBoss = info.Rarity == MonsterRarity.Unique;
                        var balance = BlightTables.BalanceFor(x.Level, x.Blighted, x.UberBlighted);
                        var res = balance.KillResource.Count > 0 ? balance.KillResource : BlightTables.KillResource;
                        gained = (int)info.Rarity < res.Count ? res[(int)info.Rarity] : 0;
                        x.Resource += (uint)gained;
                        x.KilledCount++;
                        break;
                    }
        if (d is null) return;
        if (wasBoss)
        {
            int bossChestChance = AreaStats(player).GetValueOrDefault(Stat.MapBlightBossesHavePctChanceToAddAdditionalReward);
            if (bossChestChance > 0 && AreaRng(player).Next(100) < bossChestChance)
                lock (d) d.ExtraBossChests++;
        }
        if (gained > 0) await SendResource(player, d);
    }

    // ---- Towers ----------------------------------------------------------------------------------------------------------------

    public async Task<bool> Build(Player player, byte[] bytes)
    {
        if (bytes.Length < InstanceBlight.TowerBuildLength) return true;
        var (target, row) = InstanceBlight.ReadTowerBuild(bytes);
        if (Of(player) is not { } run) return false;
        Defence? d = null;
        lock (run.Gate)
            foreach (var x in run.Defences)
                lock (x)
                    if (x.Spots.ContainsKey(target)) { d = x; break; }
        if (d is null) return false;
        if (player.PlayerLife == 0 || player.Closed || player.LeavingArea) return true;
        var tables = BlightTables.Towers;
        if (row >= tables.Count || VarietyOf((int)row) is null) return true;
        var stats = AreaStats(player);
        var wanted = tables[(int)row];
        // Enforce BlightTowersPerLevel MinLevel (Chilling/Flame lvl 1, Minion lvl 6, Shocking lvl 11, Empowering/Seismic lvl 15)
        // unless map_can_have_underlevelled_blight overrides it.
        if (wanted.MinLevel > d.Level && stats.GetValueOrDefault(Stat.MapCanHaveUnderlevelledBlight) <= 0)
            return true;

        int percent = 100 + stats.GetValueOrDefault(Stat.MapBlightTowerCostPlusPct);
        long cost = (long)wanted.Cost * Math.Max(0, percent) / 100 * (stats.GetValueOrDefault(Stat.MapBlightTowerCostDoubled) > 0 ? 2 : 1);
        uint oldId;
        SpotView built;
        Spot upgradedSpot;
        lock (d)
        {
            if (!d.Spots.TryGetValue(target, out var spot)) return true;
            var current = tables[spot.Row];
            if (d.Phase is not (Phase.Building or Phase.Running) || spot.Raising != 0 || !current.Next.Contains((int)row) || d.Resource < cost) return true;
            d.Resource -= (uint)cost;
            oldId = spot.Id;
            d.Spots.Remove(oldId);
            spot.Upgrade = spot.Row > 0;
            spot.Row = (int)row;
            spot.Raising = spot.Upgrade ? RaiseSteps : 1;
            spot.SkillId = 0;
            spot.EmpowerBuffInstance = 0;
            spot.EmpowerBuffTier = 0;
            spot.Id = player.Instance.World.TakeObjectId();
            d.Spots[spot.Id] = spot;
            built = View(spot);
            upgradedSpot = spot;
        }
        await BlightTowers.DismissSpotMinions(player, d, upgradedSpot);
        await SendResource(player, d);
        await ShowSpot(player, d, built);
        await Remove(player, oldId, $"Blight 0x{oldId:x} replaced by tower 0x{built.Id:x}");
        await BlightCassia.GrantFlags(player, [BlightTables.QuestFlagIds.TowerBuilt]);
        Save(player, run);
        return true;
    }

    async Task RaiseTowers(Player player, Defence d, double now)
    {
        List<(SpotView View, MonsterCatalog.Variety Variety)> raising = [];
        lock (d)
            foreach (var spot in d.Spots.Values.Where(s => s.Raising != 0))
                if (VarietyOf(spot.Row) is { } variety) raising.Add((View(spot), variety));
        foreach (var (view, variety) in raising)
        {
            StateMachineData states;
            lock (d)
            {
                if (!d.Spots.TryGetValue(view.Id, out var spot) || spot.Raising != view.Raising) continue;
                states = SpotStates(variety, View(spot));
                spot.Raising = view.Raising < RaiseSteps ? view.Raising + 1 : 0;
                if (spot.Raising == 0) spot.NextFire = now + TowerInterval;
            }
            await Update(player, view.Id, () => InstanceEntities.StateMachineUpdate(view.Id, SpotStateMachine, states),
                $"Blight tower 0x{view.Id:x} raising {view.Raising}/{RaiseSteps}");
        }
    }

    /// <summary>Clears static Blightreach instance state on module unload.</summary>
    public static void ClearBlightreach() => BlightreachInstances.Clear();

    /// <summary>Whether Fungal Horizons' Blightreach buff is currently active in <paramref name="instance"/>.</summary>
    public static bool HasBlightreach(InstanceState? instance) => instance is not null && BlightreachInstances.TryGetValue(instance, out _);
}
