using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Modding;
using GameServer.Core;

namespace GameServer.Modules.Delve;

/// <summary>Delve: the Crawler and the Voltaxic Generator in the Azurite Mine Encampment, the chart, Niko's options, the equipment
/// upgrades, account checkpoints and route trips. Darkness and biome-specific encounter scripts remain partial.</summary>
public sealed partial class DelveModule : IModule
{
    /// <summary>Fills this type's game data at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadDelveModuleData()
    {
    }

    /// <summary>The Azurite Mine Encampment (<c>WorldAreas</c> Delve_Hub).</summary>
    public const ushort Hub = 0x8b9b;
    const string HubId = "Delve_Hub";
    /// <summary>Where a module saves: the account's equipment and the character's sulphite.</summary>
    public const string EquipmentKey = "delve", SulphiteKey = "delve.sulphite", DepthKey = "delve.depth", ReturnAreaKey = "delve.return";

    /// <summary>Where the Crawler stands in the encampment, on the rails by the mine entrance, and its facing
    /// (placed live with /delve_crawler 408 310 90, 2026-10-30).</summary>
    static readonly (int X, int Y) CrawlerAt = (408, 310);
    const float CrawlerRotation = MathF.PI / 2;
    // hub.arm: console (337,307), room origin (69,69), size621, orientation3 => (376,352).
    static readonly (int X, int Y) GeneratorAt = (376, 352);
    /// <summary>How far the Crawler's walk blockers stand from its middle along its length (cells).</summary>
    const int CrawlerHalfLength = 6;
    const int ObjectReach = WalkBlockerObject.Size - 1; // Positioned object_size 5 is stored as radius 4.

    sealed class State
    {
        public uint Crawler;
        public (int X, int Y) CrawlerCell;
        public readonly List<uint> Blockers = [];
        public readonly List<uint> Attachments = [];
        public readonly Dictionary<uint, (int X, int Y)> Checkpoints = [];
        public uint Generator;
        public bool GeneratorOpen;
        public DelveJourney? Journey;
        public uint StartTrigger;
        public bool GaugePending = true;
        public uint DestinationLight;
        public uint CompletionEffect;
        public bool ResetNeeded;
        public readonly List<uint> Trail = [];
        public DelveRailRoute? Rail;
        /// <summary>What the next Delve chart (S→C 0x240) carries; /delve_chart sets it (one cell at 0, 0 by default).</summary>
        public (uint A, ushort B, List<InstanceDelve.Cell> Cells)? ChartProbe;
    }

    readonly ConditionalWeakTable<IPlayer, State> _states = new();
    readonly ConcurrentDictionary<string, ushort> _lastWorldArea = new(StringComparer.OrdinalIgnoreCase);

    void RememberReturnArea(Player player, ushort areaHash)
    {
        if (areaHash is 0 or Hub or InstanceDelve.MineArea) return;
        _lastWorldArea[CharacterName(player)] = areaHash;
        WorldOf(player).CharacterData.Change(ReturnAreaKey, _ => areaHash.ToString());
    }

    ushort ResolveReturnArea(Player player)
    {
        if (ushort.TryParse(WorldOf(player).CharacterData.Get(ReturnAreaKey), out ushort saved)
            && saved is not (0 or Hub or InstanceDelve.MineArea))
            return saved;
        if (_lastWorldArea.TryGetValue(CharacterName(player), out ushort recent)
            && recent is not (0 or Hub or InstanceDelve.MineArea))
            return recent;
        return Hub;
    }

    /// <summary>The account's equipment, in the league.</summary>
    public static DelveProgress Equipment(ISavedData account) =>
        account.Get(EquipmentKey) is { } text ? JsonSerializer.Deserialize<DelveProgress>(text, Json) ?? new() : new();

    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Changes the equipment; a null result changes nothing.</summary>
    public static bool ChangeEquipment(ISavedData account, Func<DelveProgress, DelveProgress?> change) => account.Change(EquipmentKey, text =>
        change(text is null ? new() : JsonSerializer.Deserialize<DelveProgress>(text, Json) ?? new()) is { } next ? JsonSerializer.Serialize(next, Json) : null);

    static ushort Sulphite(ISavedData character) => ushort.TryParse(character.Get(SulphiteKey), out ushort amount) ? amount : (ushort)0;

    static byte[] DelveData(IPlayerWorld world)
    {
        var equipment = Equipment(world.AccountData);
        var current = DelveMine.Load(world.AccountData).Current;
        return InstanceDelve.DelveData(Sulphite(world.CharacterData), equipment.Azurite, equipment.Flares, equipment.Dynamite, equipment.Upgrades,
            (short)Math.Clamp(current.X, short.MinValue, short.MaxValue), (ushort)Math.Clamp(current.Y, 0, ushort.MaxValue));
    }

    /// <summary>This module's game data, built in a fixed order when it loads: before the Gateway listens, as the
    /// Gateway's own (WorldLoader). Tests that use the module call it once.</summary>
    public static void LoadData()
    {
        LoadDelveModuleData();
        LoadDelveRoutesData();
        DelveEquipment.LoadDelveProgressData();
    }

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        LoadData();
        DelveRailRoute.Warmup();
        module.Lifetime.Own(DelveRailRoute.ForgetOnUnload());
        RegisterSulphite(module);
        RegisterTrips(module);
        // The gauge and the Subterranean Chart read the character's sulphite from it, in the encampment and the mine.
        GameEvents.EnterMessages.Add(module.Manifest.Id, player =>
        {
            var modPlayer = PlayerOf(player);
            if (!modPlayer.AreaId.StartsWith("Delve", StringComparison.Ordinal)) return [];
            var world = WorldOf(modPlayer);
            return [("0x0228 Delve resources and equipment", DelveData(world))];
        });
        GameEvents.AreaHeader.Add(module.Manifest.Id, player =>
        {
            var modPlayer = PlayerOf(player);
            switch (modPlayer.AreaId)
            {
                case "Delve_Main":
                    if (player.CameFrom is ushort fromMine and not 0) RememberReturnArea(player, fromMine);
                    player.ReturnAreaHash = ResolveReturnArea(player);
                    return MineHeader(module, modPlayer);
                case HubId:
                    if (player.CameFrom is ushort fromHub and not 0) RememberReturnArea(player, fromHub);
                    else if (_lastWorldArea.TryGetValue(CharacterName(player), out ushort recent)) RememberReturnArea(player, recent);
                    return new AreaHeader(null, [2]);
                default:
                    if (player.Layout.Hash is not (0 or Hub or InstanceDelve.MineArea))
                        _lastWorldArea[CharacterName(player)] = player.Layout.Hash;
                    return null;
            }
        });
        GameEvents.Stats.Add(module.Manifest.Id, (Action<StatSheet>)(sheet =>
        {
            foreach (var (key, value) in DelveEquipment.Stats(Equipment(WorldOf(sheet.ModPlayer).AccountData)))
                if (key == Stat.DelveSulphiteCapacity || sheet.ModPlayer.AreaId.StartsWith("Delve", StringComparison.Ordinal)) sheet.Add(key, value);
        }));
        GameEvents.AreaTick.Add(module.Manifest.Id, start => !start.Layout.Id.StartsWith("Delve", StringComparison.Ordinal) ? null : async (player, _) =>
        {
            if (!player.Connection.PlayerSent || player.LeavingArea || player.Closed) return;
            var modPlayer = PlayerOf(player);
            var state = _states.GetOrCreateValue(modPlayer);
            if (!state.GaugePending) return;
            state.GaugePending = false;
            try { await RefreshGauge(modPlayer, WorldOf(modPlayer)); }
            catch { state.GaugePending = true; throw; }
        });
        // The Crawler and the Generator are each player's own (their Delve state): theirs alone to see.
        GameEvents.PlayerEntered.Add(module.Manifest.Id, async player =>
        {
            var modPlayer = PlayerOf(player);
            if (modPlayer.AreaId is HubId or "Delve_Main" && !player.Character.HasQuestFlag("DelveOwner"))
                await SetQuestFlags(player, ["DelveOwner"]);
            _states.GetOrCreateValue(modPlayer).GaugePending = true;
            Program.ViewOf.Value = player;
            try
            {
                await SpawnCrawler(module, modPlayer);
                await SpawnGenerator(module, modPlayer);
            }
            finally { Program.ViewOf.Value = null; }
            await StartPendingTrip(module, modPlayer);
        });
        GameEvents.ObjectUse.Add(module.Manifest.Id, async (player, use, at) =>
        {
            if (!_states.TryGetValue(PlayerOf(player), out var state)) return false;
            var world = WorldOf(PlayerOf(player));
            if (state.Checkpoints.TryGetValue(use.TargetId, out var checkpoint))
            {
                if (await world.WalkUpTo(checkpoint.X, checkpoint.Y, ObjectReach, at))
                    await OpenChart(module, PlayerOf(player));
                return true;
            }
            if (state.Generator != 0 && use.TargetId == state.Generator)
            {
                if (await world.WalkUpTo(GeneratorAt.X, GeneratorAt.Y, ObjectReach, at))
                {
                    state.GeneratorOpen = true;
                    await PlayerOf(player).Send(DelveData(world), "Voltaxic Generator resources");
                    await PlayerOf(player).Send(InstanceWaypoints.OpenUi(InstanceDelve.UpgradesUi, 1), "Open Delve equipment upgrades");
                }
                return true;
            }
            if (state.Crawler != 0 && use.TargetId == state.Crawler)
            {
                // Walk there, then the Subterranean Chart (S→C 0x7b kind 0x1c with 1).
                if (await world.WalkUpTo(state.CrawlerCell.X, state.CrawlerCell.Y, ObjectReach, at))
                    await OpenChart(module, PlayerOf(player));
                return true;
            }
            return false;
        });
        GameEvents.NpcOption.Add(module.Manifest.Id, async talk =>
        {
            // Text comes first, as for any NPC: these are the options with only a script.
            if (talk.HasText) return false;
            var world = WorldOf(talk.Player);
            // Niko's View Subterranean Chart: SendOpenScreen( DelveMapScreen, 1 ), S→C 0x7b kind 0x1c with 1.
            if (talk.Script.Contains("DelveMapScreen", StringComparison.Ordinal))
            {
                await world.NpcClose("0x0084 close NPC dialogue for Subterranean Chart");
                await OpenChart(module, talk.ModPlayer);
                return true;
            }
            // Niko's Visit Mine Encampment: BeginDelve(), to the Azurite Mine Encampment, as /goto does.
            if (talk.Script.Contains("BeginDelve", StringComparison.Ordinal))
            {
                await world.NpcClose("0x0084 close NPC dialogue for Mine Encampment");
                var player = talk.Player;
                if (player.Layout.Hash != Hub)
                {
                    RememberReturnArea(player, player.Layout.Hash);
                    await ChangeArea(player,
                        new PendingTransfer(Hub, 0, false, AtWaypoint: true), $"{talk.Npc}: {talk.Option}");
                }
                return true;
            }
            return false;
        });
        GameEvents.Packet(InstanceDelve.ReturnToWorldId).Add(module.Manifest.Id, async (player, _) =>
        {
            if (player.Layout.Hash is not (InstanceDelve.MineArea or Hub)) return false;
            ushort dest = ResolveReturnArea(player);
            if (dest == player.Layout.Hash) dest = Hub;
            bool atWaypoint = dest == Hub || WaypointCatalog.Areas.Find(dest) is not null || HideoutCatalog.ByArea(dest) is not null;
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0186 Delve return to world 0x{dest:x4}");
            await ChangeArea(player,
                new PendingTransfer(dest, 0, false, AtWaypoint: atWaypoint, AtSpawn: !atWaypoint), "Delve return to world");
            return true;
        });
        GameEvents.Packet(InstanceDelve.RouteId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await RequestTrip(module, PlayerOf(player), InstanceDelve.ReadRoute(bytes));
            return true;
        });
        foreach (ushort id in new[] { InstanceDelve.UpgradesRequestId, InstanceDelve.UpgradeId, InstanceDelve.PurchaseId })
            GameEvents.Packet(id).Add(module.Manifest.Id, async (player, bytes) =>
            {
                await UseEquipment(module, PlayerOf(player), id, bytes);
                return true;
            });
        // C→S 0x23f: the Delve chart (opened by Niko) asks for its data; S→C 0x240 answers (an empty chart for now).
        GameEvents.Packet(InstanceDelve.ChartRequestId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            if (bytes.Length != 2) return false;
            var mine = Mine(module, PlayerOf(player));
            var probe = _states.GetOrCreateValue(PlayerOf(player)).ChartProbe;
            uint a = probe?.A ?? mine.Seed; ushort b = probe?.B ?? 0;
            IReadOnlyList<InstanceDelve.Cell> cells = probe is { } value ? value.Cells : mine.Chart();
            await PlayerOf(player).Send(InstanceDelve.Chart(a, b, cells), $"0x0240 Delve chart: {a}, {b}, {cells.Count} cell(s) {string.Join(" ", cells)}");
            var world = WorldOf(PlayerOf(player));
            await PlayerOf(player).Send(DelveData(world), "Delve chart resource snapshot");
            await RefreshGauge(PlayerOf(player), world);
            return true;
        });
        GameEvents.Command(module.Manifest.Id, "delve_crawler", "/delve_crawler [x y degrees] (dev: create the Delve Crawler in the Azurite Mine Encampment, or move and turn the one there; a click on it opens the Subterranean Chart)", (player, args) => CrawlerCommand(module, PlayerOf(player), args));
        GameEvents.Command(module.Manifest.Id, "sulphite", "/sulphite <amount 0-65535> (dev: the character's Voltaxic Sulphite, saved and sent as the Delve data 0x228)", async (player, args) =>
        {
            if (args.Length != 1 || !ushort.TryParse(args[0], out ushort amount)) throw new ArgumentException("/sulphite <amount 0-65535>");
            var world = WorldOf(PlayerOf(player));
            world.CharacterData.Change(SulphiteKey, _ => amount.ToString());
            await PlayerOf(player).Send(DelveData(world), $"0x0228 Delve data: {amount} sulphite (/sulphite)");
            await RefreshGauge(PlayerOf(player), world);
            await PlayerOf(player).Tell($"Sulphite: {amount}");
        });
        GameEvents.Command(module.Manifest.Id, "delve_chart", "/delve_chart <u32> <u16> [<u16> <u16> <u64> <u8> <u32> ...] (dev: what the next Delve chart (S→C 0x240) carries, to work out its fields)", async (player, args) =>
        {
            try
            {
                var n = args.Select(a => a.StartsWith("0x") ? Convert.ToUInt64(a[2..], 16) : ulong.Parse(a)).ToArray();
                var cells = new List<InstanceDelve.Cell>();
                for (int i = 2; i + 4 < n.Length; i += 5) cells.Add(new((ushort)n[i], (ushort)n[i + 1], n[i + 2], (byte)n[i + 3], (uint)n[i + 4]));
                var state = _states.GetOrCreateValue(PlayerOf(player));
                state.ChartProbe = (n.Length > 0 ? (uint)n[0] : 0, n.Length > 1 ? (ushort)n[1] : (ushort)0, cells);
                await PlayerOf(player).Tell($"Next Delve chart: {state.ChartProbe.Value.A}, {state.ChartProbe.Value.B}, {cells.Count} cell(s). Open it from Niko.");
            }
            catch (FormatException) { await PlayerOf(player).Tell("Numbers only (decimal or 0x…)."); }
        });
    }

    async Task CrawlerCommand(IModuleContext module, IPlayer player, string[] args)
    {
        var world = WorldOf(player);
        if (world.AreaHash is not (Hub or InstanceDelve.MineArea)) { await player.Tell("Only in the Azurite Mine Encampment or mine"); return; }
        var state = _states.GetOrCreateValue(player);
        if (args.Length == 3 && int.TryParse(args[0], out int x) && int.TryParse(args[1], out int y) && float.TryParse(args[2],
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float degrees))
        {
            if (state.Crawler == 0) await SpawnCrawler(module, player);
            float radians = degrees * MathF.PI / 180;
            state.CrawlerCell = (x, y);
            if (world.AreaHash == InstanceDelve.MineArea && state.Rail?.Points[^1] == (x, y) && Mine(module, player).Pending is null)
                PlayerOf(player).Instance.SetState(DestinationFlag(player));
            await global::Game.SendWorldRecord(player, state.Crawler, InstanceProtocol.Teleport(state.Crawler, x, y, 0x7fffffff), $"Delve Crawler to ({x}, {y})");
            await global::Game.SendWorldRecord(player, state.Crawler, InstanceHideoutEditing.Rotation(state.Crawler, radians), $"Delve Crawler turned {degrees}°");
            foreach (uint part in state.Attachments)
                await global::Game.SendWorldRecord(player, part, InstanceProtocol.Teleport(part, x, y, 0x7fffffff));
            await BlockCrawler(module, player, state, x, y, radians);
            await player.Tell($"Delve Crawler at ({x}, {y}), {degrees}°");
            return;
        }
        await SpawnCrawler(module, player);
        await player.Tell($"Delve Crawler 0x{state.Crawler:x} at {state.CrawlerCell}");
    }

    async Task SpawnGenerator(IModuleContext module, IPlayer player)
    {
        var world = WorldOf(player);
        var state = _states.GetOrCreateValue(player);
        if (world.AreaHash != Hub || state.Generator != 0) return;
        uint id = world.NewObjectId();
        state.Generator = id;
        var (x, y) = GeneratorAt;
        await global::Game.SendWorldCreate(player, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(DelveGeneratorObject.MetadataPath),
            new DelveGeneratorObject(x, y, 5.12494f).Blob()), $"Voltaxic Generator 0x{id:x} at ({x}, {y})");
        await global::Game.SendWorldRecord(player, id, InstanceProtocol.EnterWakeZone(id));
        world.SetBlocker(id, x, y, ObjectReach);
        uint cables = world.NewObjectId();
        await global::Game.SendWorldCreate(player, cables, InstanceEntities.CreateObject(cables, InstanceEntities.ObjectTypeHash(DelveGeneratorCablesObject.MetadataPath),
            new DelveGeneratorCablesObject(382, 370, 5.15258f).Blob()), "Voltaxic Generator cables");
        await global::Game.SendWorldRecord(player, cables, InstanceProtocol.EnterWakeZone(cables));
    }

    async Task UseEquipment(IModuleContext module, IPlayer player, ushort id, byte[] bytes)
    {
        var world = WorldOf(player);
        if (!_states.TryGetValue(player, out var state) || !world.PlayerReady || world.AreaHash != Hub || !state.GeneratorOpen || state.Generator == 0) return;
        if (!PlayerWalk.InWalkUpRange(player.Position, GeneratorAt, ObjectReach))
        {
            state.GeneratorOpen = false;
            return;
        }
        bool changed = false;
        if (id == InstanceDelve.UpgradeId)
        {
            int type = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(2));
            changed = ChangeEquipment(world.AccountData, p => DelveEquipment.UpgradeEquipment(p, type));
        }
        else if (id == InstanceDelve.PurchaseId && bytes[2] <= 1)
            changed = ChangeEquipment(world.AccountData, p => DelveEquipment.Purchase(p, bytes[2] != 0, bytes[3]));
        await player.Send(DelveData(world), "Delve equipment state");
        if (changed)
        {
            await world.RefreshStats();
            if (id == InstanceDelve.PurchaseId)
            {
                var p = PlayerOf(player);
                var equipment = Equipment(world.AccountData);
                List<int> crafted = [];
                if (equipment.Dynamite >= 1 && !p.Character.LeagueQuestFlags.Contains(Quests.League.DelveCraftedDynamite))
                    crafted.Add(Quests.League.DelveCraftedDynamite);
                if (equipment.Flares >= 2 && !p.Character.LeagueQuestFlags.Contains(Quests.League.DelveCraftedFlares))
                    crafted.Add(Quests.League.DelveCraftedFlares);
                if (crafted.Count > 0)
                    await SetLeagueQuestFlags(p, crafted);
            }
        }
    }

    // The Delve Crawler in the Azurite Mine Encampment. A click on it opens the Subterranean Chart, as Niko's option does.
    async Task SpawnCrawler(IModuleContext module, IPlayer player)
    {
        var world = WorldOf(player);
        var state = _states.GetOrCreateValue(player);
        if (world.AreaHash is not (Hub or InstanceDelve.MineArea) || state.Crawler != 0) return;
        var rails = world.AreaHash == InstanceDelve.MineArea ? DelveRailRoute.Load(player.AreaId, PlayerOf(player).Seed) : null;
        state.Rail = rails;
        if (world.AreaHash == InstanceDelve.MineArea && rails is null) return;
        bool savedDestination = world.AreaHash == InstanceDelve.MineArea && PlayerOf(player).Instance.HasState(DestinationFlag(player));
        var (x, y) = world.AreaHash == Hub ? CrawlerAt : rails!.Points[savedDestination ? ^1 : 0];
        const uint id = 0x0d200000;
        state.Crawler = id;
        await ResetPresentation(player, state);
        state.CrawlerCell = (x, y);
        bool mine = world.AreaHash == InstanceDelve.MineArea;
        float rotation = mine ? new DelveJourney(rails!.Points, 0).Heading(0) : CrawlerRotation;
        await CreateCrawlerBody(player, state, x, y, rotation, roaming: mine && Mine(module, player).Pending is not null);
        await BlockCrawler(module, player, state, x, y, rotation);
        if (mine && rails is { } rail)
        {
            foreach (var (point, checkpointId, completed) in new[] { (rail.Points[0], id + 5, true), (rail.Points[^1], id + 4, savedDestination) })
            {
                state.Checkpoints[checkpointId] = point;
                await RemoveSceneObject(player, checkpointId);
                await global::Game.SendWorldCreate(player, checkpointId, InstanceEntities.CreateObject(checkpointId, InstanceEntities.ObjectTypeHash(DelveMapViewerObject.MetadataPath),
                    new DelveCheckpointObject(point.X, point.Y, completed).Blob()), $"Delve checkpoint pad at {point}");
                await global::Game.SendWorldRecord(player, checkpointId, InstanceProtocol.EnterWakeZone(checkpointId));
            }
        }
        if (mine && Mine(module, player).Pending is null)
            await ResetScene(module, player, state);
        // Recreate the waiting boundary even after a module reload cancelled the pending trip.
        if (mine && !savedDestination)
            await ShowStartBoundary(player, state, rails!.Points[0]);
    }

    static Task RemoveSceneObject(IPlayer modPlayer, uint id)
    {
        var player = PlayerOf(modPlayer);
        return global::Game.SendWorldRemoval(player, id);
    }

    async Task OpenChart(IModuleContext module, IPlayer player)
    {
        await PublishChart(module, player);
        await player.Send(InstanceWaypoints.OpenUi(InstanceNpcs.DelveMapUi, 1), "Open Delve chart at saved checkpoint");
    }

    async Task PublishChart(IModuleContext module, IPlayer player)
    {
        var mine = Mine(module, player);
        await player.Send(InstanceDelve.Chart(mine.Seed, 0, mine.Chart()), $"Delve chart saved checkpoint ({mine.Current.X}, {mine.Current.Y})");
        await player.Send(DelveData(WorldOf(player)), "Delve chart resource snapshot");
    }

    static async Task CreateCrawlerBody(IPlayer player, State state, int x, int y, float rotation, bool roaming)
    {
        uint id = state.Crawler;
        await RemoveSceneObject(player, id);
        foreach (uint part in Enumerable.Range(1, 3).Select(n => id + (uint)n))
            await RemoveSceneObject(player, part);
        state.Attachments.Clear();
        await global::Game.SendWorldCreate(player, id, InstanceEntities.CreateObject(id,
            InstanceEntities.ObjectTypeHash(roaming ? DelveRobotObject.MetadataPath : DelveMapViewerObject.MetadataPath),
            roaming ? new DelveRobotObject(x, y, rotation).Blob() : new DelveMapViewerObject(x, y, rotation).Blob()),
            $"0x02bb create the Delve Crawler 0x{id:x} at ({x}, {y})");
        await global::Game.SendWorldRecord(player, id, InstanceProtocol.EnterWakeZone(id), $"0x01a9 Delve Crawler 0x{id:x} enters wake zone");
        // DelveMapViewer's own transition callbacks select clips for its waypoint model, not this two-clip rig.
        // Explicitly select idle_01 (rig index 0), which loops the running engine's shake and sound.
        await global::Game.SendWorldRecord(player, id, InstanceProtocol.AnimationUpdate(id, 0, 0, 1), "Crawler idle_01");
        foreach (var (model, bone, light) in new[] {
            (DelveCrawlerAttachmentObject.LeftWheel, "aux_L_wheel_attachment", false),
            (DelveCrawlerAttachmentObject.RightWheel, "aux_R_wheel_attachment", false),
            (DelveCrawlerAttachmentObject.Torch, "aux_main_tip", true) })
        {
            // The real robot's Wheels component creates and rotates its own meshes from actual displacement.
            if (roaming && !light) continue;
            uint part = id + 1 + (uint)state.Attachments.Count;
            await RemoveSceneObject(player, part);
            state.Attachments.Add(part);
            string type = light ? DelveCrawlerAttachmentObject.LightType : EffectObject.TypePath;
            await global::Game.SendWorldCreate(player, part, InstanceEntities.CreateObject(part, InstanceEntities.ObjectTypeHash(type),
                new DelveCrawlerAttachmentObject(x, y, id, model, bone, light).Blob()), $"Crawler attachment 0x{part:x}: {bone}");
            await global::Game.SendWorldRecord(player, part, InstanceProtocol.EnterWakeZone(part));
        }
    }

    // The Crawler's type does not block (DelveRobot.ot: no blocking), so the player walked through it: three invisible
    // blockers (Metadata/NPC/Blocker) along its facing, and the same squares for the server's walk.
    static async Task BlockCrawler(IModuleContext module, IPlayer player, State state, int x, int y, float rotation)
    {
        var world = WorldOf(player);
        foreach (uint old in state.Blockers)
        {
            world.RemoveBlocker(old);
            await global::Game.SendWorldRemoval(player, old, $"0x02bd remove the Crawler blocker 0x{old:x}");
        }
        state.Blockers.Clear();
        foreach (int along in new[] { -CrawlerHalfLength, 0, CrawlerHalfLength })
        {
            int bx = x + (int)MathF.Round(along * MathF.Sin(rotation)), by = y - (int)MathF.Round(along * MathF.Cos(rotation));
            uint id = state.Crawler + 0x10 + (uint)state.Blockers.Count;
            world.RemoveBlocker(id);
            await RemoveSceneObject(player, id);
            state.Blockers.Add(id);
            world.SetBlocker(id, bx, by, ObjectReach);
            await global::Game.SendWorldCreate(player, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(WalkBlockerObject.MetadataPath),
                new WalkBlockerObject(bx, by).Blob()), $"0x02bb create the Crawler blocker 0x{id:x} at ({bx}, {by})");
            await global::Game.SendWorldRecord(player, id, InstanceProtocol.EnterWakeZone(id));
        }
        await world.PushOutOfCollision();
    }
}
