using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain.Generation;
using GameServer.Modding;

namespace GameServer.Modules.Settlers.MapOres;

/// <summary>
/// Per-instance controller for Settlers map-side Ore Deposits (Crimson Iron, Orichalcum, Petrified Amber,
/// Bismuth, Verisium, Thaumaturgic Dust Hourglass), Lost Shipments, and the Starfall Crater entrance.
/// <para>
/// Reads native <c>ExtraTerrainFeatures</c> row 46 (<c>Village</c> -&gt; <c>ore_node.arm</c>) placements
/// (<c>OreNode</c> + 8 linked <c>MineralClaimedMarker</c>s) from <see cref="ServerLevels.Level.RoomObjects"/>,
/// with deterministic <see cref="AreaPopulation.EncounterSpot"/> fallback when overrides/Astrolabes/Scarabs
/// request additional slots beyond what the terrain generator fitted.
/// </para>
/// </summary>
public sealed class MapOreEncounter
{
    const uint RollSalt = 0x5345544c; // "SETL"
    const double BaseVerisiumChannelSeconds = 5.0;
    const double VerisiumZoneRadius = 22.0;
    const double BaseOrichalcumBuffSeconds = 30.0;
    const double BismuthTornadoPulseInterval = 2.0;
    const double AmberAttackTickInterval = 1.0;

    public enum NodeStage : byte { Ready, Channelling, Active, Claimed, Failed }

    public sealed class MarkerObj
    {
        public required uint Id;
        public required int X;
        public required int Y;
        public required float Rotation;
        public int ClaimedState = 1; // 1 hidden, 2 rising/activating, 3 lit orange smoke beacon
    }

    public sealed class OreNodeState
    {
        public required int Index;
        public required MapOreSlotKind Kind;
        public required int X;
        public required int Y;
        public required float Rotation;
        public required uint PrimaryId;
        public uint AltarId;              // copper_altar / hourglass_altar
        public uint EntranceId;           // VerisiumBossSubAreaEntrance
        public uint TransitionId;         // VerisiumBossSubAreaTransition
        public ulong SubAreaInstance;     // SettlersBossFallenStar instance id once opened
        public int Variant;               // Orichalcum: 1=Fire(Demon), 2=Lightning(Pale); LostShipment: 0=Kalguuran, 1=Karui
        public NodeStage Stage = NodeStage.Ready;
        public bool Presented;
        public List<MarkerObj> Markers = [];
        public List<uint> DecoObjects = [];

        // Active encounter tracking
        public double StartedAt;
        public double NextWaveAt;
        public int WavesSpawned;
        public int TotalWaves;
        public readonly ConcurrentDictionary<uint, byte> AliveMonsters = new();
        public readonly ConcurrentDictionary<uint, byte> AlivePods = new();

        // Petrified Amber defense tree
        public uint TreeMonsterId;
        public int TreeLife;
        public int TreeMaxLife;
        public double NextTreeAttackAt;

        // Verisium hold-to-channel
        public uint ChannellingPlayerId;
        public double ChannelStartedAt;
        public double ChannelDuration;
        public double MaxChannelSeconds = BaseVerisiumChannelSeconds;
        public double ChannelChargeRatio;

        // Bismuth tornadoes (map_bismuth_tornados)
        public readonly List<uint> TornadoIds = [];
        public double NextTornadoPulseAt;

        // Marker light-up timer (claimed 2 -> 3)
        public double LightUpMarkersAt = double.PositiveInfinity;
    }

    public sealed class InstanceOres
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public required int AreaLevel;
        public required bool IsMap;
        public required int Seed;
        public required MapOreStats Stats;
        public readonly List<OreNodeState> Nodes = [];
        public int CompletedOres;
        public readonly ConcurrentDictionary<uint, OreNodeState> MonsterOwner = new();
        public readonly ConcurrentDictionary<uint, int> EscalationStacks = new();
        public readonly ConcurrentDictionary<uint, byte> BismuthEmpowered = new();
        public readonly ConcurrentDictionary<uint, double> OrichalcumBuffUntil = new();
        public readonly ConcurrentDictionary<uint, byte> VerisiumShieldPlayers = new();
        public bool BismuthActive;
        public int BismuthEmpowerCount;
        public double Now;
    }

    readonly ConditionalWeakTable<InstanceState, InstanceOres> _instances = new();

    public void Clear() => _instances.Clear();

    public InstanceOres? Of(Player player) =>
        player.Instance is { } inst && _instances.TryGetValue(inst, out var state) ? state : null;

    public bool IsActivePause(Player player) =>
        Of(player) is { } state && state.Nodes.Any(n => n.Stage is NodeStage.Channelling or NodeStage.Active);

    public bool IsOreMonster(Player player, uint monsterId) =>
        Of(player) is { } state && state.MonsterOwner.ContainsKey(monsterId);

    // ---- Map Device Opening: Claim all 4 Kalguuran Scarabs ----

    public void OnMapOpening(MapOpening request)
    {
        if (request.Opening is not { } opening || request.Campaign) return;
        foreach (var slot in request.Slots)
        {
            if (slot.IsMap) continue;
            if (!slot.BaseId.StartsWith("Metadata/Items/Scarabs/ScarabSettlers", StringComparison.Ordinal)) continue;
            request.HandledContent.Add(MapScarabContent.KalguuranOre);
            request.HandledContent.Add(MapScarabContent.KalguuranGuardedRiches);
            request.HandledContent.Add(MapScarabContent.KalguuranRefinement);
            request.HandledContent.Add(MapScarabContent.KalguuranEnriching);
        }
    }

    // ---- Placement & Native ore_node.arm Discovery ----

    public static int? ForcedChance(string? setting)
    {
        foreach (var entry in (setting ?? "").Split(',', StringSplitOptions.TrimEntries))
            if (entry.Split('=') is [var id, var value] && id.Equals("Settlers", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int c))
                return c;
        return null;
    }

    static int DesiredDepositCount(AreaPopulated area, IReadOnlyDictionary<uint, int> rawStats, MapOreStats stats, bool isMap)
    {
        if (stats.Disabled) return 0;

        // Explicit test override: Game_LEAGUE_CHANCES=Settlers=100
        if (ForcedChance(Environment.GetEnvironmentVariable("Game_LEAGUE_CHANCES")) is int forced)
        {
            if (forced <= 0) return 0;
            var forcedRng = new Random(unchecked(area.Seed ^ (int)RollSalt));
            int n = forced >= 100 || forcedRng.Next(100) < forced ? 1 : 0;
            return n + stats.AdditionalOres;
        }

        if (!isMap) return 0;

        int nativeCount = 0;
        try
        {
            var info = AreaInfo.Load(GamePack.Default.TryRead, area.Player.Layout.Id, area.Level);
            var byId = rawStats.ToDictionary(s => (int)s.Key, s => s.Value);
            nativeCount = ExtraTerrainFeatures.LeagueCount(info with { Stats = byId },
                new LevelRandom(unchecked((uint)area.Seed ^ RollSalt), false), LeagueChances.Settlers);
        }
        catch (Exception e) when (e is ArgumentException or IOException or KeyNotFoundException)
        {
            // Fallback to LeagueChances if AreaInfo is unavailable.
            var rng = new Random(unchecked(area.Seed ^ (int)RollSalt));
            if (!stats.DisabledFromChance)
                nativeCount = LeagueChances.Current.Count(area.Player.Layout.Id, LeagueChances.Settlers, rng,
                    Program.LeagueFor(area.Player).HardMode, stats.ChancePlusPct, stats.AdditionalChancePct);
        }

        int total = nativeCount + stats.AdditionalOres;
        if (stats.ForcedSpawns > 0 && total < stats.ForcedSpawns)
            total = stats.ForcedSpawns;
        if (stats.ProspectingAstrolabe && total < 1)
            total = 1;
        return Math.Clamp(total, 0, 12);
    }

    public string? Populate(AreaPopulated area)
    {
        var player = area.Player;
        if (player.Instance is not { } instance) return "no instance";
        if (_instances.TryGetValue(instance, out _)) return "already populated";
        var layout = player.Layout;
        if (InTown(player) || HideoutCatalog.ByArea(layout.Hash) is not null || layout.Walkable is null)
            return "town, hideout, or no walk grid";

        // Never spawn map ores inside Kingsmarch or Settlers boss arenas themselves.
        if (layout.Id is "KalguuranSettlersLeague" or MapOreTables.StarfallAreaId
            or "SettlersBossPirateCove" or "SettlersBossAlleyway")
            return "settlers sub-area";

        bool isMap = MapDevices.IsMapArea(layout.Hash);
        var rawStats = instance.KeptAreaStats ?? AreaLeagueStats(player) ?? new Dictionary<uint, int>();
        var stats = MapOreStats.From(rawStats);
        if (stats.Disabled) return "disabled by map/atlas stats";

        // 1. Read native ore_node.arm placements from the level generator (ExtraTerrainFeatures row 46 Village).
        var placements = ExtractNativeOrePlacements(layout);

        // 2. Check if additional slots are needed (Game_LEAGUE_CHANCES, Astrolabe, or terrain couldn't fit all rooms).
        int desired = DesiredDepositCount(area, rawStats, stats, isMap);
        int targetCount = Math.Max(placements.Count, desired);
        if (targetCount <= 0) return "no ore deposits rolled";

        var rng = new Random(unchecked(area.Seed ^ (int)RollSalt ^ 0x4f5245));
        if (placements.Count < targetCount)
        {
            var taken = area.Taken.Concat(placements.Select(p => (p.X, p.Y))).ToList();
            for (int needed = targetCount - placements.Count; needed > 0; needed--)
            {
                var spot = AreaPopulation.EncounterSpot(area, taken, rng);
                if (spot is not { } cell) break;
                taken.Add(cell);
                var markers = MapOreTables.RelativeMarkerOffsets
                    .Select(o =>
                    {
                        var mc = layout.Near((cell.X + o.Dx, cell.Y + o.Dy));
                        return (mc.X, mc.Y, 0f);
                    })
                    .ToList();
                placements.Add((cell.X, cell.Y, 0f, markers));
            }
        }
        if (placements.Count == 0) return "no walkable spot";

        int level = Math.Clamp((int)area.Level, 1, 100);
        var state = new InstanceOres
        {
            AreaLevel = level,
            IsMap = isMap,
            Seed = area.Seed,
            Stats = stats,
        };

        bool starfallPlaced = false;
        for (int i = 0; i < placements.Count; i++)
        {
            var (x, y, rot, markerCells) = placements[i];
            var kind = stats.PickSlotKind(rng, level, isMap, allowStarfallCrater: !starfallPlaced);
            if (kind == MapOreSlotKind.StarfallCrater) starfallPlaced = true;

            var node = new OreNodeState
            {
                Index = i,
                Kind = kind,
                X = x,
                Y = y,
                Rotation = rot,
                PrimaryId = instance.World.TakeObjectId(),
                Variant = kind == MapOreSlotKind.Orichalcum ? rng.Next(1, 3) : rng.Next(2),
                MaxChannelSeconds = BaseVerisiumChannelSeconds * Math.Max(0.5, (100.0 + stats.VerisiumMaximumChannelPlusPct) / 100.0),
            };

            if (kind is MapOreSlotKind.Orichalcum or MapOreSlotKind.DustHourglass)
                node.AltarId = instance.World.TakeObjectId();
            if (kind == MapOreSlotKind.StarfallCrater)
            {
                node.EntranceId = node.PrimaryId;
                node.TransitionId = instance.World.TakeObjectId();
            }

            if (kind is not (MapOreSlotKind.LostShipment or MapOreSlotKind.StarfallCrater))
            {
                foreach (var (mx, my, mrot) in markerCells)
                    node.Markers.Add(new MarkerObj { Id = instance.World.TakeObjectId(), X = mx, Y = my, Rotation = mrot });
            }

            state.Nodes.Add(node);
        }

        _instances.AddOrUpdate(instance, state);
        return null;
    }

    static List<(int X, int Y, float Rotation, List<(int X, int Y, float Rotation)> Markers)> ExtractNativeOrePlacements(AreaLayout layout)
    {
        var result = new List<(int, int, float, List<(int, int, float)>)>();
        if (layout.Level is not { RoomObjects.Count: > 0 } level) return result;

        var objects = level.RoomObjects;
        var links = level.RoomLinks;

        for (int i = 0; i < objects.Count; i++)
        {
            var o = objects[i];
            if (!o.Type.Equals(MapOreTables.OreNodeMarker, StringComparison.OrdinalIgnoreCase)) continue;

            var markers = new List<(int X, int Y, float Rotation)>();
            foreach (var link in links)
            {
                if (link.From == i && link.To >= 0 && link.To < objects.Count
                    && objects[link.To].Type.Equals(MapOreTables.ClaimedMarker, StringComparison.OrdinalIgnoreCase))
                {
                    var m = objects[link.To];
                    markers.Add((m.X, m.Y, NormalizeRotation(m.Rotation)));
                }
            }

            // Fallback: find nearest MineralClaimedMarkers if links weren't preserved.
            if (markers.Count == 0)
            {
                foreach (var m in objects)
                    if (m.Type.Equals(MapOreTables.ClaimedMarker, StringComparison.OrdinalIgnoreCase)
                        && Distance((o.X, o.Y), (m.X, m.Y)) <= 40)
                        markers.Add((m.X, m.Y, NormalizeRotation(m.Rotation)));
            }

            if (markers.Count == 0)
            {
                foreach (var (dx, dy) in MapOreTables.RelativeMarkerOffsets)
                {
                    var mc = layout.Near((o.X + dx, o.Y + dy));
                    markers.Add((mc.X, mc.Y, 0f));
                }
            }

            result.Add((o.X, o.Y, NormalizeRotation(o.Rotation), markers));
        }

        return result;
    }

    // ---- Presentation & Late-Joiner / Re-entry Sync ----

    public async Task PresentAll(Player player)
    {
        if (Of(player) is not { } state || !Current(player)) return;
        await state.Gate.WaitAsync();
        try
        {
            foreach (var node in state.Nodes)
            {
                if (!node.Presented)
                {
                    await CreateNodeObjects(player, state, node);
                    node.Presented = true;
                }
                else if (node.Kind == MapOreSlotKind.StarfallCrater && node.TransitionId != 0)
                {
                    // Ensure late-joining/re-entering player's layout knows about the Starfall Crater transition.
                    if (!player.Layout.AreaTransitions.Any(t => t.Id == node.TransitionId))
                        player.AddAreaTransition(node.TransitionId, MakeStarfallTransition(node));
                }
            }
        }
        finally { state.Gate.Release(); }
    }

    async Task CreateNodeObjects(Player player, InstanceOres state, OreNodeState node)
    {
        var layout = player.Layout;
        float? height = layout.GroundHeight(node.X, node.Y);

        switch (node.Kind)
        {
            case MapOreSlotKind.CrimsonIron:
            {
                var sm = MapOreTables.MakeStates(MapOreTables.CrimsonIronStates,
                    ("activated", 0), ("in_progress", 0), ("ore_empty", 0), ("interactable", 1), ("growths_alive", 1));
                var q = new QuestObject(node.X, node.Y, MapOreTables.CrimsonIronComponents, node.Rotation, height) { StateMachine = sm };
                await CreateSharedObject(player, node.PrimaryId, MapOreTables.CrimsonIronObject, q.Blob(), $"Settlers Crimson Iron #{node.Index + 1}");
                break;
            }
            case MapOreSlotKind.Orichalcum:
            {
                var sm = MapOreTables.MakeStates(MapOreTables.DemonCopperStates,
                    ("activated", 0), ("in_progress", 0), ("ore_empty", 0), ("encounter", 0), ("type", node.Variant), ("lights", 0));
                var q = new QuestObject(node.X, node.Y, MapOreTables.DemonCopperComponents, node.Rotation, height) { StateMachine = sm };
                await CreateSharedObject(player, node.PrimaryId, MapOreTables.DemonCopperObject, q.Blob(), $"Settlers Orichalcum #{node.Index + 1}");

                var altarCell = layout.Near((node.X + 7, node.Y));
                var asm = MapOreTables.MakeStates(MapOreTables.AltarStates,
                    ("encounter", 0), ("type", node.Variant), ("claimed", 0));
                var aq = new QuestObject(altarCell.X, altarCell.Y, MapOreTables.CopperAltarComponents, node.Rotation, layout.GroundHeight(altarCell.X, altarCell.Y)) { StateMachine = asm };
                await CreateSharedObject(player, node.AltarId, MapOreTables.CopperAltarObject, aq.Blob(), $"Settlers Orichalcum Altar #{node.Index + 1}");
                break;
            }
            case MapOreSlotKind.PetrifiedAmber:
            {
                var sm = MapOreTables.MakeStates(MapOreTables.PetrifiedWoodStates,
                    ("activated", 0), ("in_progress", 0), ("ore_empty", 0), ("am_dead", 0));
                var q = new QuestObject(node.X, node.Y, MapOreTables.PetrifiedWoodComponents, node.Rotation, height, Blocked: false) { StateMachine = sm };
                await CreateSharedObject(player, node.PrimaryId, MapOreTables.PetrifiedWoodObject, q.Blob(), $"Settlers Petrified Amber #{node.Index + 1}");
                break;
            }
            case MapOreSlotKind.Bismuth:
            {
                var sm = MapOreTables.MakeStates(MapOreTables.BismuthStates,
                    ("activated", 0), ("in_progress", 0), ("ore_empty", 0), ("effect_off", 0));
                var q = new QuestObject(node.X, node.Y, MapOreTables.BismuthComponents, node.Rotation, height) { StateMachine = sm };
                await CreateSharedObject(player, node.PrimaryId, MapOreTables.BismuthObject, q.Blob(), $"Settlers Bismuth #{node.Index + 1}");
                break;
            }
            case MapOreSlotKind.Verisium:
            {
                var sm = MapOreTables.MakeStates(MapOreTables.VerisiumStates,
                    ("activated", 0), ("in_progress", 0), ("ore_empty", 0), ("encounter", 0), ("begun_channelling", 0), ("channelling", 0), ("in_zone", 0));
                var q = new QuestObject(node.X, node.Y, MapOreTables.VerisiumComponents, node.Rotation, height) { StateMachine = sm };
                await CreateSharedObject(player, node.PrimaryId, MapOreTables.VerisiumObject, q.Blob(), $"Settlers Verisium #{node.Index + 1}");
                break;
            }
            case MapOreSlotKind.DustHourglass:
            {
                var sm = MapOreTables.MakeStates(MapOreTables.HourglassStates,
                    ("activated", 0), ("in_progress", 0), ("ore_empty", 0), ("encounter", 0), ("type", 1), ("lights", 0));
                var q = new QuestObject(node.X, node.Y, MapOreTables.HourglassComponents, node.Rotation, height) { StateMachine = sm };
                await CreateSharedObject(player, node.PrimaryId, MapOreTables.HourglassObject, q.Blob(), $"Settlers Dust Hourglass #{node.Index + 1}");

                var altarCell = layout.Near((node.X + 4, node.Y));
                var asm = MapOreTables.MakeStates(MapOreTables.AltarStates,
                    ("encounter", 0), ("type", 1), ("claimed", 0));
                var aq = new QuestObject(altarCell.X, altarCell.Y, MapOreTables.HourglassAltarComponents, node.Rotation, layout.GroundHeight(altarCell.X, altarCell.Y)) { StateMachine = asm };
                await CreateSharedObject(player, node.AltarId, MapOreTables.HourglassAltarObject, aq.Blob(), $"Settlers Hourglass Altar #{node.Index + 1}");
                break;
            }
            case MapOreSlotKind.LostShipment:
            {
                string model = node.Variant == 0 ? MapOreTables.KalguuranShipmentModel : MapOreTables.KaruiShipmentModel;
                var q = new QuestObject(node.X, node.Y, MapOreTables.LostShipmentComponents, node.Rotation, height, Model: model, Opened: false);
                await CreateSharedObject(player, node.PrimaryId, MapOreTables.LostShipmentChestObject, q.Blob(), $"Settlers Lost Shipment #{node.Index + 1}");
                break;
            }
            case MapOreSlotKind.StarfallCrater:
            {
                var entrance = new QuestObject(node.X, node.Y, MapOreTables.StarfallEntranceComponents, node.Rotation, height, Blocked: false);
                await CreateSharedObject(player, node.EntranceId, MapOreTables.StarfallEntranceObject, entrance.Blob(), $"Starfall Crater entrance #{node.Index + 1}");
                var gate = MakeStarfallTransition(node);
                foreach (var member in InstanceMembers(player))
                    if (!member.Layout.AreaTransitions.Any(t => t.Id == node.TransitionId))
                        member.AddAreaTransition(node.TransitionId, gate);
                await CreateSharedObject(player, node.TransitionId, MapOreTables.StarfallTransitionObject, gate.Blob(), $"Starfall Crater transition #{node.Index + 1}");
                break;
            }
        }

        // Spawn the 8 MineralClaimedMarkers (initially hidden via claimed=1).
        foreach (var marker in node.Markers)
        {
            var msm = MapOreTables.MakeStates(MapOreTables.ClaimedMarkerStates,
                ("claimed", marker.ClaimedState), ("activated", 0), ("light_colour", 0));
            var mq = new QuestObject(marker.X, marker.Y, MapOreTables.ClaimedMarkerComponents, marker.Rotation, layout.GroundHeight(marker.X, marker.Y))
            {
                StateMachine = msm,
                HideMinimapIcon = true,
            };
            await CreateSharedObject(player, marker.Id, MapOreTables.ClaimedMarker, mq.Blob(), null);
        }
    }

    static AreaTransitionObject MakeStarfallTransition(OreNodeState node) =>
        new(node.X, node.Y, MapOreTables.StarfallAreaHash16, MapOreTables.StarfallTransitionModel, Rotation: node.Rotation) { Kind = 2 };

    // ---- Player Interaction (ObjectUse) ----

    public async Task<bool> OnUse(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (Of(player) is not { } state) return false;
        var node = state.Nodes.FirstOrDefault(n =>
            n.PrimaryId == use.TargetId || (n.AltarId != 0 && n.AltarId == use.TargetId)
            || (n.TransitionId != 0 && n.TransitionId == use.TargetId));
        if (node is null) return false;

        if (player.PlayerLife == 0 || player.Closed || player.LeavingArea) return true;

        // Walk up to the clicked object.
        if (!TryBeginObjectWalk(player, requestedAt, node.X, node.Y, 15, out var walk)) return true;
        if (walk.Arrive > walk.Now) await AreaDelay(player, walk.Arrive - walk.Now);
        if (!PlayerActionCurrent(player, walk.Action)) return true;

        // Starfall Crater sub-area portal transition.
        if (node.Kind == MapOreSlotKind.StarfallCrater && use.TargetId == node.TransitionId)
        {
            await EnterStarfallCrater(player, state, node);
            return true;
        }

        // Lost Shipment chest opening.
        if (node.Kind == MapOreSlotKind.LostShipment)
        {
            await OpenLostShipment(player, state, node);
            return true;
        }

        // Verisium: click once to begin channelling, click again (or walk out / hit max) to release.
        if (node.Kind == MapOreSlotKind.Verisium)
        {
            await HandleVerisiumInteract(player, state, node);
            return true;
        }

        await state.Gate.WaitAsync();
        try
        {
            if (node.Stage != NodeStage.Ready) return true;
            double now = AreaTime(player);
            state.Now = now;
            node.StartedAt = now;

            switch (node.Kind)
            {
                case MapOreSlotKind.CrimsonIron:
                    await StartCrimsonIron(player, state, node);
                    break;
                case MapOreSlotKind.Orichalcum:
                    await StartOrichalcum(player, state, node);
                    break;
                case MapOreSlotKind.PetrifiedAmber:
                    await StartPetrifiedAmber(player, state, node);
                    break;
                case MapOreSlotKind.Bismuth:
                    await ClaimBismuth(player, state, node);
                    break;
                case MapOreSlotKind.DustHourglass:
                    await StartDustHourglass(player, state, node);
                    break;
            }
        }
        finally { state.Gate.Release(); }
        return true;
    }

    // ---- 1. Crimson Iron ----

    async Task StartCrimsonIron(Player player, InstanceOres state, OreNodeState node)
    {
        node.Stage = NodeStage.Active;
        var sm = MapOreTables.MakeStates(MapOreTables.CrimsonIronStates,
            ("activated", 0), ("in_progress", 1), ("ore_empty", 0), ("interactable", 0), ("growths_alive", 1));
        await UpdateStateMachine(player, node.PrimaryId, MapOreTables.OreNodeSmIndex, sm);

        int podCount = 3 + state.Stats.CrimsonAdditionalPods;
        node.TotalWaves = 2;
        node.WavesSpawned = 1;
        node.NextWaveAt = state.Now + 6.0;

        var rng = new Random(unchecked(state.Seed ^ (node.Index * 7919) ^ 0x101));
        string podPath = state.IsMap ? MapOreTables.CrimsonPodMap : MapOreTables.CrimsonPodCampaign;
        var podVariety = Monsters.Find(podPath) ?? Monsters.Find(MapOreTables.CrimsonPodCampaign);

        for (int p = 0; p < podCount; p++)
        {
            double angle = p * Math.Tau / podCount + 0.3;
            var pos = player.Layout.Near((node.X + (int)(16 * Math.Cos(angle)), node.Y + (int)(16 * Math.Sin(angle))));
            if (podVariety is { Supported: true })
            {
                uint podId = await SpawnOreMonster(player, state, node, podVariety, MonsterRarity.Rare, pos.X, pos.Y, isPod: true);
                if (podId != 0) node.AlivePods.TryAdd(podId, 0);
            }
            await SpawnWavePack(player, state, node, MapOreTables.CrimsonFodder, MapOreTables.CrimsonElites, pos, 3, rng);
        }
    }

    // ---- 2. Orichalcum (Demon Copper) ----

    async Task StartOrichalcum(Player player, InstanceOres state, OreNodeState node)
    {
        node.Stage = NodeStage.Active;
        node.TotalWaves = 3;
        node.WavesSpawned = 1;
        node.NextWaveAt = state.Now + 5.5;

        var nsm = MapOreTables.MakeStates(MapOreTables.DemonCopperStates,
            ("activated", 2), ("in_progress", 1), ("ore_empty", 0), ("encounter", 1), ("type", node.Variant), ("lights", 1));
        await UpdateStateMachine(player, node.PrimaryId, MapOreTables.OreNodeSmIndex, nsm);

        var asm = MapOreTables.MakeStates(MapOreTables.AltarStates,
            ("encounter", 1), ("type", node.Variant), ("claimed", 0));
        await UpdateStateMachine(player, node.AltarId, MapOreTables.AltarSmIndex, asm);

        // Grant Orichalcum elemental buff (K'Tash's Anger / Beidat's Wrath) scaled by Orichalcum Infusion.
        double duration = BaseOrichalcumBuffSeconds * Math.Max(0.25, (100.0 + state.Stats.OrichalcumBuffDurationPlusPct) / 100.0);
        ushort buffRow = node.Variant == 1 ? MapOreTables.BuffOrichalcumFire : MapOreTables.BuffOrichalcumLightning;
        foreach (var member in InstanceMembers(player))
        {
            member.Area.Buffs.Set(member.Id, new Buff(buffRow, node.AltarId, U68: Buff.Seconds(duration)));
            state.OrichalcumBuffUntil[member.Id] = state.Now + duration;
        }

        // Spawn CopperSpawner1/2 portals around the node.
        var rng = new Random(unchecked(state.Seed ^ (node.Index * 7919) ^ 0x202));
        for (int s = 0; s < 3; s++)
        {
            double angle = s * Math.Tau / 3.0;
            var pos = player.Layout.Near((node.X + (int)(18 * Math.Cos(angle)), node.Y + (int)(18 * Math.Sin(angle))));
            uint sid = player.Instance.World.TakeObjectId();
            string spawnerType = s % 2 == 0 ? MapOreTables.CopperSpawner1Object : MapOreTables.CopperSpawner2Object;
            var sq = new QuestObject(pos.X, pos.Y, MapOreTables.CopperSpawnerComponents, (float)angle, player.Layout.GroundHeight(pos.X, pos.Y), Blocked: false);
            await CreateSharedObject(player, sid, spawnerType, sq.Blob(), null);
            node.DecoObjects.Add(sid);

            var (fodder, elites) = node.Variant == 1
                ? (MapOreTables.OrichalcumDemonFodder, MapOreTables.OrichalcumDemonElites)
                : (MapOreTables.OrichalcumPaleFodder, MapOreTables.OrichalcumPaleElites);
            await SpawnWavePack(player, state, node, fodder, elites, pos, 4, rng);
        }
    }

    // ---- 3. Petrified Amber ----

    async Task StartPetrifiedAmber(Player player, InstanceOres state, OreNodeState node)
    {
        node.Stage = NodeStage.Active;
        node.TotalWaves = 3;
        node.WavesSpawned = 1;
        node.NextWaveAt = state.Now + 6.0;
        node.NextTreeAttackAt = state.Now + AmberAttackTickInterval;

        var sm = MapOreTables.MakeStates(MapOreTables.PetrifiedWoodStates,
            ("activated", 2), ("in_progress", 1), ("ore_empty", 0), ("am_dead", 0));
        await UpdateStateMachine(player, node.PrimaryId, MapOreTables.OreNodeSmIndex, sm);

        // Tree defense health scaled to area level (friendly target).
        node.TreeMaxLife = Math.Max(500, state.AreaLevel * 85);
        node.TreeLife = node.TreeMaxLife;
        if (Monsters.Find(MapOreTables.AmberTreeTarget) is { Supported: true } treeVariety)
        {
            node.TreeMonsterId = await SpawnMonster(player, treeVariety, (byte)state.AreaLevel, MonsterRarity.Unique,
                node.X, node.Y, name: "Petrified Amber Tree", friendly: true, log: false);
        }

        // Spawn FungalSpawner1..5 around the tree and first wave of attackers.
        var rng = new Random(unchecked(state.Seed ^ (node.Index * 7919) ^ 0x303));
        for (int f = 0; f < MapOreTables.FungalSpawnerObjects.Length; f++)
        {
            double angle = f * Math.Tau / MapOreTables.FungalSpawnerObjects.Length;
            var pos = player.Layout.Near((node.X + (int)(22 * Math.Cos(angle)), node.Y + (int)(22 * Math.Sin(angle))));
            uint fid = player.Instance.World.TakeObjectId();
            var fq = new QuestObject(pos.X, pos.Y, MapOreTables.FungalSpawnerComponents, (float)angle, player.Layout.GroundHeight(pos.X, pos.Y));
            await CreateSharedObject(player, fid, MapOreTables.FungalSpawnerObjects[f], fq.Blob(), null);
            node.DecoObjects.Add(fid);

            if (f < 3)
                await SpawnWavePack(player, state, node, MapOreTables.AmberAttackers, MapOreTables.AmberElites, pos, 3, rng);
        }
    }

    // ---- 4. Bismuth (Instant Claim + Elemental Map Empowerment + Optional Tornadoes) ----

    async Task ClaimBismuth(Player player, InstanceOres state, OreNodeState node)
    {
        var sm = MapOreTables.MakeStates(MapOreTables.BismuthStates,
            ("activated", 1), ("in_progress", 0), ("ore_empty", 0), ("effect_off", 1));
        await UpdateStateMachine(player, node.PrimaryId, MapOreTables.OreNodeSmIndex, sm);

        // Empower alive Magic and Rare monsters across the map with Bismuth elemental energy.
        state.BismuthActive = true;
        var rng = new Random(unchecked(state.Seed ^ (node.Index * 7919) ^ 0x404));
        int empowered = 0;
        foreach (var (id, monster) in player.Area.Monsters)
        {
            if (monster.Dead || !monster.Hostile || monster.Life <= 0) continue;
            if (monster.Rarity is not (MonsterRarity.Magic or MonsterRarity.Rare)) continue;
            if (!state.BismuthEmpowered.TryAdd(id, 0)) continue;

            // Attach Bismuth visual buff + elemental damage/reward stats.
            player.Area.Buffs.Set(id, new Buff(MapOreTables.BuffBismuthEmpowered, node.PrimaryId));
            int extraMods = 1 + (state.Stats.BismuthAdditionalModifierPctChance > 0 && rng.Next(100) < state.Stats.BismuthAdditionalModifierPctChance ? 1 : 0);
            var stats = monster.EncounterStats is null ? new Dictionary<uint, int>() : new Dictionary<uint, int>(monster.EncounterStats);
            stats[Stat.FromLeagueItemQuantityPlusPctPermyriad] = stats.GetValueOrDefault(Stat.FromLeagueItemQuantityPlusPctPermyriad) + 2000 * extraMods;
            stats[Stat.FromLeagueItemRarityPlusPctPermyriad] = stats.GetValueOrDefault(Stat.FromLeagueItemRarityPlusPctPermyriad) + 3500 * extraMods;
            monster.EncounterStats = stats;
            empowered++;
        }
        state.BismuthEmpowerCount += empowered;

        // Spawn Bismuth elemental tornadoes when map_bismuth_tornados > 0.
        if (state.Stats.BismuthTornados > 0)
        {
            int count = Math.Clamp(state.Stats.BismuthTornados, 1, 6);
            node.NextTornadoPulseAt = state.Now + BismuthTornadoPulseInterval;
            for (int t = 0; t < count; t++)
            {
                string tPath = MapOreTables.BismuthTornados[t % MapOreTables.BismuthTornados.Length];
                if (Monsters.Find(tPath) is not { Supported: true } tv) continue;
                double angle = t * Math.Tau / count;
                var pos = player.Layout.Near((node.X + (int)(12 * Math.Cos(angle)), node.Y + (int)(12 * Math.Sin(angle))));
                uint tid = await SpawnMonster(player, tv, (byte)state.AreaLevel, MonsterRarity.Normal, pos.X, pos.Y, log: false);
                if (tid != 0) node.TornadoIds.Add(tid);
            }
        }

        await CompleteOreNode(player, state, node, encounterMultiplier: 1.0);
        if (empowered > 0)
            await PlayerOf(player).Tell($"Bismuth energy empowers {empowered} Magic and Rare monsters in the area!");
    }

    // ---- 5. Verisium (Hold-to-Channel ControlZone Scaling) ----

    async Task HandleVerisiumInteract(Player player, InstanceOres state, OreNodeState node)
    {
        await state.Gate.WaitAsync();
        try
        {
            double now = AreaTime(player);
            state.Now = now;

            if (node.Stage == NodeStage.Ready)
            {
                node.Stage = NodeStage.Channelling;
                node.ChannellingPlayerId = player.Id;
                node.ChannelStartedAt = now;
                node.ChannelDuration = 0;
                node.ChannelChargeRatio = 0;

                var sm = MapOreTables.MakeStates(MapOreTables.VerisiumStates,
                    ("activated", 0), ("in_progress", 1), ("ore_empty", 0), ("encounter", 0),
                    ("begun_channelling", 1), ("channelling", 1), ("in_zone", 1));
                await UpdateStateMachine(player, node.PrimaryId, MapOreTables.OreNodeSmIndex, sm);

                // Grant Verisium Energy shield (cannot be stunned or damaged while channelling).
                player.Area.Buffs.Set(player.Id, new Buff(MapOreTables.BuffVerisiumChannelShield, node.PrimaryId,
                    U68: Buff.Seconds(node.MaxChannelSeconds + 1.0)));
                state.VerisiumShieldPlayers[player.Id] = 1;
                await PlayerOf(player).Tell($"Channelling Verisium (up to {node.MaxChannelSeconds:0.0}s) — click again or move away to release.");
            }
            else if (node.Stage == NodeStage.Channelling)
            {
                await ReleaseVerisiumChannel(player, state, node, now);
            }
        }
        finally { state.Gate.Release(); }
    }

    async Task ReleaseVerisiumChannel(Player player, InstanceOres state, OreNodeState node, double now)
    {
        node.Stage = NodeStage.Active;
        node.ChannelDuration = Math.Clamp(now - node.ChannelStartedAt, 0.25, node.MaxChannelSeconds);
        node.ChannelChargeRatio = Math.Clamp(node.ChannelDuration / BaseVerisiumChannelSeconds, 0.1,
            Math.Max(1.0, (100.0 + state.Stats.VerisiumMaximumChannelPlusPct) / 100.0));

        // Remove Verisium channel shield.
        foreach (var member in InstanceMembers(player))
        {
            if (state.VerisiumShieldPlayers.TryRemove(member.Id, out _))
                member.Area.Buffs.Clear(member.Id, MapOreTables.BuffVerisiumChannelShield, node.PrimaryId);
        }

        var sm = MapOreTables.MakeStates(MapOreTables.VerisiumStates,
            ("activated", 0), ("in_progress", 1), ("ore_empty", 0), ("encounter", 1),
            ("begun_channelling", 1), ("channelling", 0), ("in_zone", 0));
        await UpdateStateMachine(player, node.PrimaryId, MapOreTables.OreNodeSmIndex, sm);

        // Spawn Verisium guardians scaled by channel intensity (0.1..1.3+).
        var rng = new Random(unchecked(state.Seed ^ (node.Index * 7919) ^ 0x505));
        int packs = Math.Clamp((int)Math.Ceiling(1 + node.ChannelChargeRatio * 3.0), 1, 5);
        int perPack = Math.Clamp((int)Math.Round(3 + node.ChannelChargeRatio * 3.0), 3, 7);
        node.TotalWaves = 1;
        node.WavesSpawned = 1;

        for (int p = 0; p < packs; p++)
        {
            double angle = p * Math.Tau / packs;
            var pos = player.Layout.Near((node.X + (int)(16 * Math.Cos(angle)), node.Y + (int)(16 * Math.Sin(angle))));
            await SpawnWavePack(player, state, node, MapOreTables.VerisiumFodder, MapOreTables.VerisiumElites, pos, perPack, rng,
                extraRareBias: (int)(node.ChannelChargeRatio * 30));
        }

        // High channel charge (>= 85%) summons a Forged Unique guardian!
        if (node.ChannelChargeRatio >= 0.85 && MapOreTables.PickSupported(MapOreTables.VerisiumUniques, rng) is { } boss)
        {
            var bossPos = player.Layout.Near((node.X + 10, node.Y + 6));
            await SpawnOreMonster(player, state, node, boss, MonsterRarity.Unique, bossPos.X, bossPos.Y);
        }
    }

    // ---- 6. Thaumaturgic Dust Hourglass ----

    async Task StartDustHourglass(Player player, InstanceOres state, OreNodeState node)
    {
        node.Stage = NodeStage.Active;
        node.TotalWaves = 2;
        node.WavesSpawned = 1;
        node.NextWaveAt = state.Now + 6.0;

        var nsm = MapOreTables.MakeStates(MapOreTables.HourglassStates,
            ("activated", 2), ("in_progress", 1), ("ore_empty", 0), ("encounter", 1), ("type", 1), ("lights", 1));
        await UpdateStateMachine(player, node.PrimaryId, MapOreTables.OreNodeSmIndex, nsm);

        var asm = MapOreTables.MakeStates(MapOreTables.AltarStates,
            ("encounter", 1), ("type", 1), ("claimed", 0));
        await UpdateStateMachine(player, node.AltarId, MapOreTables.AltarSmIndex, asm);

        var rng = new Random(unchecked(state.Seed ^ (node.Index * 7919) ^ 0x606));
        for (int s = 0; s < 3; s++)
        {
            double angle = s * Math.Tau / 3.0;
            var pos = player.Layout.Near((node.X + (int)(16 * Math.Cos(angle)), node.Y + (int)(16 * Math.Sin(angle))));
            await SpawnWavePack(player, state, node, MapOreTables.HourglassFodder, MapOreTables.HourglassElites, pos, 4, rng);
        }
    }

    // ---- 7. Lost Shipment Chest (Lost in Transit) ----

    async Task OpenLostShipment(Player player, InstanceOres state, OreNodeState node)
    {
        await state.Gate.WaitAsync();
        try
        {
            if (node.Stage != NodeStage.Ready) return;
            node.Stage = NodeStage.Claimed;

            // Update chest to opened state.
            string model = node.Variant == 0 ? MapOreTables.KalguuranShipmentModel : MapOreTables.KaruiShipmentModel;
            var opened = new QuestObject(node.X, node.Y, MapOreTables.LostShipmentComponents, node.Rotation,
                player.Layout.GroundHeight(node.X, node.Y), Model: model, Opened: true);
            await CreateSharedObject(player, node.PrimaryId, MapOreTables.LostShipmentChestObject, opened.Blob(), "Open Lost Shipment");
        }
        finally { state.Gate.Release(); }

        // Drop Runegrafts, currency, and gold piles.
        var rng = new Random(unchecked(state.Seed ^ (node.Index * 7919) ^ 0x707));
        var drops = DropsFor(player);
        var items = new List<ItemDrops.Drop>();

        // 1-2 Runegrafts from 3.29 Runegraft pool.
        if (MapOreTables.Runegrafts.Count > 0)
        {
            int runegraftCount = rng.Next(100) < 35 ? 2 : 1;
            for (int r = 0; r < runegraftCount; r++)
            {
                string rgId = MapOreTables.Runegrafts[rng.Next(MapOreTables.Runegrafts.Count)];
                if (ItemsFor(player).Find(rgId) is { Supported: true } rgType)
                    items.Add(new ItemDrops.Drop(ItemCatalog.Create(rgType) with { ItemLevel = (byte)state.AreaLevel }, rgType));
            }
        }

        // 2-4 currency drops scaled to area level.
        int currCount = rng.Next(2, 5);
        for (int c = 0; c < currCount; c++)
            if (drops.CurrencyDrop(state.AreaLevel, rng) is { } curr)
                items.Add(curr);

        // Generous gold pile (equivalent to a Unique/chest shipment payout).
        if (drops.Gold(state.AreaLevel, MonsterRarity.Unique, rng, 2.0 * SystemSettings.Default.GoldDropMultiplier) is { } gold)
            items.Add(gold);

        foreach (var drop in items)
        {
            var pos = ScatterPos(player.Layout, (node.X, node.Y), rng);
            var ground = new WorldItemObject(pos.X, pos.Y, drop.Item);
            uint gid = player.Instance.Ground.Add(ground, player);
            await SendGroundItem(player, gid, ground, $"Lost Shipment drop: {drop.Type.Name}", justDropped: true);
        }
        await PlayerOf(player).Tell("Opened a Lost Shipment!");
    }

    // ---- 8. Starfall Crater Sub-Area Entrance ----

    async Task EnterStarfallCrater(Player player, InstanceOres state, OreNodeState node)
    {
        await state.Gate.WaitAsync();
        try
        {
            var area = new MapDevice.MapArea(MapOreTables.StarfallAreaHash16, MapOreTables.StarfallAreaId, "Starfall Crater", 0)
            {
                Level = Math.Max(83, state.AreaLevel),
            };
            var run = new MapDevice.Run(player.Character.Name!, area, MapMods.None,
                ReturnTo: new(player.Layout.Hash, player.InstanceNumber, node.X, node.Y))
            {
                Atlas = false,
            };
            await ChangeArea(player, new PendingTransfer(area.Hash16, node.SubAreaInstance, node.SubAreaInstance == 0, AtSpawn: true)
            {
                Map = run,
                Done = response => { if (response.Ok) node.SubAreaInstance = response.Instance; },
            }, "Starfall Crater entrance");
        }
        finally { state.Gate.Release(); }
    }

    // ---- Tick Loop: Waves, Verisium Channel, Amber Tree Defense, Marker Light-Up, Tornadoes ----

    public async Task Tick(Player player, double seconds)
    {
        if (Of(player) is not { } state || !Current(player)) return;
        double now = AreaTime(player);
        state.Now = now;

        await state.Gate.WaitAsync();
        try
        {
            // Clean up expired Orichalcum player buffs.
            foreach (var (pid, until) in state.OrichalcumBuffUntil)
                if (now >= until) state.OrichalcumBuffUntil.TryRemove(pid, out _);

            foreach (var node in state.Nodes)
            {
                // Light up markers (claimed 2 -> 3) after rise animation.
                if (now >= node.LightUpMarkersAt)
                {
                    node.LightUpMarkersAt = double.PositiveInfinity;
                    foreach (var marker in node.Markers)
                    {
                        marker.ClaimedState = 3;
                        var msm = MapOreTables.MakeStates(MapOreTables.ClaimedMarkerStates,
                            ("claimed", 3), ("activated", 1), ("light_colour", 1));
                        await UpdateStateMachine(player, marker.Id, MapOreTables.ClaimedMarkerSmIndex, msm);
                    }
                }

                // Verisium hold-to-channel progression.
                if (node.Kind == MapOreSlotKind.Verisium && node.Stage == NodeStage.Channelling)
                {
                    var channeller = InstanceMembers(player).FirstOrDefault(p => p.Id == node.ChannellingPlayerId);
                    bool leftZone = channeller is null || channeller.PlayerLife == 0
                        || Distance(channeller.Walk.Position(now), (node.X, node.Y)) > VerisiumZoneRadius;
                    bool reachedMax = now - node.ChannelStartedAt >= node.MaxChannelSeconds;
                    if (leftZone || reachedMax)
                        await ReleaseVerisiumChannel(channeller ?? player, state, node, now);
                }

                // Active wave & completion checks.
                if (node.Stage == NodeStage.Active)
                {
                    PruneDeadMonsters(player, node);

                    // Subsequent waves.
                    if (node.WavesSpawned < node.TotalWaves && (now >= node.NextWaveAt || node.AliveMonsters.IsEmpty))
                    {
                        node.WavesSpawned++;
                        node.NextWaveAt = now + 6.5;
                        await SpawnAdditionalWave(player, state, node);
                    }

                    // Petrified Amber tree defense simulation.
                    if (node.Kind == MapOreSlotKind.PetrifiedAmber && now >= node.NextTreeAttackAt)
                    {
                        node.NextTreeAttackAt = now + AmberAttackTickInterval;
                        int attackersNear = 0;
                        foreach (uint mid in node.AliveMonsters.Keys)
                            if (player.Area.Monsters.TryGetValue(mid, out var m) && !m.Dead && Distance(m.Walk.Position(now), (node.X, node.Y)) <= 18)
                                attackersNear++;
                        if (attackersNear > 0)
                        {
                            double dmgScale = Math.Max(0.1, (100.0 + state.Stats.AmberTreeDamageTakenPlusPct) / 100.0);
                            int dmg = Math.Max(1, (int)Math.Round(attackersNear * Math.Max(8, state.AreaLevel * 0.65) * dmgScale));
                            node.TreeLife = Math.Max(0, node.TreeLife - dmg);
                            if (node.TreeLife == 0)
                            {
                                await FailPetrifiedAmber(player, state, node);
                                continue;
                            }
                        }
                    }

                    // Check encounter completion.
                    if (node.WavesSpawned >= node.TotalWaves && node.AliveMonsters.IsEmpty && node.AlivePods.IsEmpty)
                    {
                        double mult = node.Kind == MapOreSlotKind.Verisium
                            ? 1.0 + node.ChannelChargeRatio * 2.0 // 1.0x .. 3.6x at 130% channel
                            : 1.0;
                        await CompleteOreNode(player, state, node, mult);
                    }
                }
            }
        }
        finally { state.Gate.Release(); }
    }

    async Task SpawnAdditionalWave(Player player, InstanceOres state, OreNodeState node)
    {
        var rng = new Random(unchecked(state.Seed ^ (node.Index * 7919) ^ (node.WavesSpawned * 1009)));
        var (fodder, elites) = node.Kind switch
        {
            MapOreSlotKind.CrimsonIron => (MapOreTables.CrimsonFodder, MapOreTables.CrimsonElites),
            MapOreSlotKind.Orichalcum when node.Variant == 1 => (MapOreTables.OrichalcumDemonFodder, MapOreTables.OrichalcumDemonElites),
            MapOreSlotKind.Orichalcum => (MapOreTables.OrichalcumPaleFodder, MapOreTables.OrichalcumPaleElites),
            MapOreSlotKind.PetrifiedAmber => (MapOreTables.AmberAttackers, MapOreTables.AmberElites),
            MapOreSlotKind.DustHourglass => (MapOreTables.HourglassFodder, MapOreTables.HourglassElites),
            _ => (MapOreTables.VerisiumFodder, MapOreTables.VerisiumElites),
        };

        for (int s = 0; s < 3; s++)
        {
            double angle = s * Math.Tau / 3.0 + node.WavesSpawned * 0.5;
            var pos = player.Layout.Near((node.X + (int)(18 * Math.Cos(angle)), node.Y + (int)(18 * Math.Sin(angle))));
            await SpawnWavePack(player, state, node, fodder, elites, pos, 4, rng);
        }
    }

    async Task FailPetrifiedAmber(Player player, InstanceOres state, OreNodeState node)
    {
        node.Stage = NodeStage.Failed;
        var sm = MapOreTables.MakeStates(MapOreTables.PetrifiedWoodStates,
            ("activated", 1), ("in_progress", 0), ("ore_empty", 1), ("am_dead", 1));
        await UpdateStateMachine(player, node.PrimaryId, MapOreTables.OreNodeSmIndex, sm);

        if (node.TreeMonsterId != 0)
        {
            player.Area.Monsters.Remove(node.TreeMonsterId, out _);
            await SendWorldRemoval(player, node.TreeMonsterId, "Petrified Amber tree destroyed");
            node.TreeMonsterId = 0;
        }
        await PlayerOf(player).Tell("The Petrified Amber tree was destroyed!");
    }

    async Task CompleteOreNode(Player player, InstanceOres state, OreNodeState node, double encounterMultiplier)
    {
        node.Stage = NodeStage.Claimed;

        // Send completion StateMachine updates per ore type.
        switch (node.Kind)
        {
            case MapOreSlotKind.CrimsonIron:
                await UpdateStateMachine(player, node.PrimaryId, MapOreTables.OreNodeSmIndex,
                    MapOreTables.MakeStates(MapOreTables.CrimsonIronStates,
                        ("activated", 1), ("in_progress", 0), ("ore_empty", 0), ("interactable", 0), ("growths_alive", 0)));
                break;
            case MapOreSlotKind.Orichalcum:
                await UpdateStateMachine(player, node.PrimaryId, MapOreTables.OreNodeSmIndex,
                    MapOreTables.MakeStates(MapOreTables.DemonCopperStates,
                        ("activated", 1), ("in_progress", 0), ("ore_empty", 0), ("encounter", 2), ("type", node.Variant), ("lights", 2)));
                await UpdateStateMachine(player, node.AltarId, MapOreTables.AltarSmIndex,
                    MapOreTables.MakeStates(MapOreTables.AltarStates, ("encounter", 1), ("type", node.Variant), ("claimed", 1)));
                break;
            case MapOreSlotKind.PetrifiedAmber:
                await UpdateStateMachine(player, node.PrimaryId, MapOreTables.OreNodeSmIndex,
                    MapOreTables.MakeStates(MapOreTables.PetrifiedWoodStates,
                        ("activated", 1), ("in_progress", 0), ("ore_empty", 0), ("am_dead", 0)));
                if (node.TreeMonsterId != 0)
                {
                    player.Area.Monsters.Remove(node.TreeMonsterId, out _);
                    await SendWorldRemoval(player, node.TreeMonsterId, "Petrified Amber defended");
                    node.TreeMonsterId = 0;
                }
                break;
            case MapOreSlotKind.Verisium:
                await UpdateStateMachine(player, node.PrimaryId, MapOreTables.OreNodeSmIndex,
                    MapOreTables.MakeStates(MapOreTables.VerisiumStates,
                        ("activated", 1), ("in_progress", 0), ("ore_empty", 0), ("encounter", 2),
                        ("begun_channelling", 1), ("channelling", 0), ("in_zone", 0)));
                break;
            case MapOreSlotKind.DustHourglass:
                await UpdateStateMachine(player, node.PrimaryId, MapOreTables.OreNodeSmIndex,
                    MapOreTables.MakeStates(MapOreTables.HourglassStates,
                        ("activated", 1), ("in_progress", 0), ("ore_empty", 0), ("encounter", 2), ("type", 1), ("lights", 2)));
                await UpdateStateMachine(player, node.AltarId, MapOreTables.AltarSmIndex,
                    MapOreTables.MakeStates(MapOreTables.AltarStates, ("encounter", 1), ("type", 1), ("claimed", 1)));
                break;
        }

        // Trigger the 8 MineralClaimedMarkers: claimed = 2 (rise animation), then claimed = 3 (smoke & orange beacon).
        foreach (var marker in node.Markers)
        {
            marker.ClaimedState = 2;
            var msm = MapOreTables.MakeStates(MapOreTables.ClaimedMarkerStates,
                ("claimed", 2), ("activated", 1), ("light_colour", 1));
            await UpdateStateMachine(player, marker.Id, MapOreTables.ClaimedMarkerSmIndex, msm);
        }
        node.LightUpMarkersAt = state.Now + 0.9;

        // Calculate and grant Ore / Bars / Thaumaturgic Dust.
        int completedBefore = state.CompletedOres;
        state.CompletedOres++;

        bool refined = state.Stats.OreFoundAsBarsInstead && node.Kind != MapOreSlotKind.DustHourglass;
        long amount = node.Kind == MapOreSlotKind.DustHourglass
            ? state.Stats.ComputeHourglassDustYield(state.AreaLevel, completedBefore)
            : state.Stats.ComputeOreYield(node.Kind, state.AreaLevel, completedBefore, encounterMultiplier);

        var resource = refined ? MapOreTables.RefinedBarOf(node.Kind) : MapOreTables.PendingResourceOf(node.Kind);
        string label = MapOreTables.DisplayName(node.Kind, refined);

        foreach (var member in InstanceMembers(player))
        {
            VillageResourceHooks.GrantResource(member, resource, amount, $"map-ore:{node.Kind}");
            await PlayerOf(member).Tell(refined
                ? $"Refined {amount:N0} {label} directly for Kingsmarch!"
                : node.Kind == MapOreSlotKind.DustHourglass
                    ? $"Collected {amount:N0} Thaumaturgic Dust for Kingsmarch!"
                    : $"Marked {amount:N0} {label} for mining in Kingsmarch!");
        }
    }

    // ---- Monster Spawning & Kill / Drop / Stat Hooks ----

    async Task SpawnWavePack(Player player, InstanceOres state, OreNodeState node,
        IReadOnlyList<string> fodderPool, IReadOnlyList<string> elitePool,
        (int X, int Y) center, int count, Random rng, int extraRareBias = 0)
    {
        bool guardedRiches = state.Stats.GuardedRichesRareChancePct > 0;
        int rareChance = (guardedRiches ? Math.Max(20, state.Stats.GuardedRichesRareChancePct / 4) : 8) + extraRareBias;
        int magicChance = guardedRiches ? 100 : 35;

        for (int i = 0; i < count; i++)
        {
            bool elite = i == 0;
            var variety = MapOreTables.PickSupported(elite ? elitePool : fodderPool, rng)
                ?? MapOreTables.PickSupported(fodderPool, rng);
            if (variety is null) continue;

            MonsterRarity rarity = elite && rng.Next(100) < rareChance ? MonsterRarity.Rare
                : rng.Next(100) < magicChance ? MonsterRarity.Magic
                : MonsterRarity.Normal;

            double angle = i * Math.Tau / Math.Max(1, count) + rng.NextDouble();
            int radius = rng.Next(3, 10);
            var pos = player.Layout.Near((center.X + (int)(radius * Math.Cos(angle)), center.Y + (int)(radius * Math.Sin(angle))));
            await SpawnOreMonster(player, state, node, variety, rarity, pos.X, pos.Y);
        }
    }

    async Task<uint> SpawnOreMonster(Player player, InstanceOres state, OreNodeState node,
        MonsterCatalog.Variety variety, MonsterRarity rarity, int x, int y, bool isPod = false)
    {
        int escalating = state.Stats.EscalatingEncounters ? state.CompletedOres : 0;
        int lifeMore = MapOreTables.MonsterMoreLifePct(state.AreaLevel) + escalating * 20;
        var extraStats = new List<(uint Key, int Value)>
        {
            (Stat.IsSettlersOreMonster, 1),
        };
        if (lifeMore > 0)
            extraStats.Add((Stat.MaximumLifePlusPct, lifeMore));
        if (escalating > 0)
            extraStats.Add((Stat.DamagePlusPct, escalating * 15));

        uint id = await SpawnMonster(player, variety, (byte)state.AreaLevel, rarity, x, y,
            log: false, extraStats: extraStats);
        if (id == 0) return 0;

        state.MonsterOwner[id] = node;
        if (escalating > 0) state.EscalationStacks[id] = escalating;
        if (!isPod) node.AliveMonsters.TryAdd(id, 0);
        return id;
    }

    public void OnKill(KillContext kill)
    {
        if (Of(kill.Player) is not { } state) return;
        uint id = kill.Monster.ObjectId;
        if (state.MonsterOwner.TryRemove(id, out var node))
        {
            node.AliveMonsters.TryRemove(id, out _);
            node.AlivePods.TryRemove(id, out _);
        }
    }

    public void OnDrops(DropRoll roll)
    {
        if (Of(roll.Player) is not { } state) return;
        uint id = roll.Monster.ObjectId;

        // Kalguuran Scarab of Enriching: increased rewards (+25% IIQ & IIR per prior completed ore deposit).
        if (state.EscalationStacks.TryGetValue(id, out int stacks) && stacks > 0)
        {
            roll.Quantity *= 1.0 + 0.25 * stacks;
            roll.Rarity *= 1.0 + 0.25 * stacks;
        }

        // Prospecting Astrolabe: enhanced ore monster rewards.
        if (state.Stats.ProspectingAstrolabe && state.MonsterOwner.ContainsKey(id))
        {
            roll.Quantity *= 1.25;
            roll.Rarity *= 1.25;
        }

        // Bismuth empowered Magic/Rare monsters grant extra item quantity & rarity.
        if (state.BismuthEmpowered.ContainsKey(id))
        {
            roll.Quantity *= 1.20;
            roll.Rarity *= 1.35;
        }
    }

    public void OnStats(StatSheet sheet)
    {
        if (Of(sheet.Player) is not { } state) return;
        if (state.OrichalcumBuffUntil.TryGetValue(sheet.Player.Id, out double until) && until > state.Now)
            sheet.Add(Stat.DamagePlusPct, 35);
    }

    public int OnIncomingDamage(Player player, int damage)
    {
        if (damage <= 0 || Of(player) is not { } state) return damage;
        // Verisium Energy shield: cannot be damaged while channelling Verisium.
        return state.VerisiumShieldPlayers.ContainsKey(player.Id) ? 0 : damage;
    }

    public Task OnChestsOpened(Player player, (int X, int Y) origin, int radius)
    {
        if (Of(player) is not { } state) return Task.CompletedTask;
        var tasks = new List<Task>();
        foreach (var node in state.Nodes)
            if (node.Kind == MapOreSlotKind.LostShipment && node.Stage == NodeStage.Ready
                && Distance(origin, (node.X, node.Y)) <= radius)
                tasks.Add(OpenLostShipment(player, state, node));
        return tasks.Count == 0 ? Task.CompletedTask : Task.WhenAll(tasks);
    }

    // ---- Helpers ----

    static void PruneDeadMonsters(Player player, OreNodeState node)
    {
        foreach (uint id in node.AliveMonsters.Keys)
            if (!player.Area.Monsters.TryGetValue(id, out var m) || m.Dead || m.Life <= 0)
                node.AliveMonsters.TryRemove(id, out _);
        foreach (uint id in node.AlivePods.Keys)
            if (!player.Area.Monsters.TryGetValue(id, out var m) || m.Dead || m.Life <= 0)
                node.AlivePods.TryRemove(id, out _);
    }

    static bool Current(Player player) => player.Connection.PlayerSent && !player.LeavingArea && !player.Closed;

    static List<Player> InstanceMembers(Player player)
    {
        var list = InGame.Keys.Where(p => p.HasClient && ReferenceEquals(p.Instance, player.Instance) && Current(p)).ToList();
        if (!list.Contains(player) && Current(player)) list.Insert(0, player);
        return list;
    }

    static async Task CreateSharedObject(Player player, uint id, string type, byte[] blob, string? log)
    {
        uint typeHash = InstanceEntities.ObjectTypeHash(type);
        await SendWorldCreate(player, id, () => InstanceEntities.CreateObject(id, typeHash, blob), log, wake: true, awaitBirth: false);
    }

    static Task UpdateStateMachine(Player player, uint id, byte smIndex, StateMachineData sm) =>
        SendWorldRecord(player, id, InstanceEntities.StateMachineUpdate(id, smIndex, sm), null);

    static float NormalizeRotation(float r) => r < 0 ? r + MathF.Tau : r;

    static double Distance((int X, int Y) a, (int X, int Y) b) =>
        Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y));

    static (int X, int Y) ScatterPos(AreaLayout layout, (int X, int Y) origin, Random rng)
    {
        double a = rng.NextDouble() * Math.Tau;
        double r = 3 + rng.NextDouble() * 4;
        return layout.Near((origin.X + (int)Math.Round(Math.Cos(a) * r), origin.Y + (int)Math.Round(Math.Sin(a) * r)));
    }
}
