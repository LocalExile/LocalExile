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
    /// <summary>The Azurite Mine Encampment (<c>WorldAreas</c> Delve_Hub).</summary>
    public const ushort Hub = 0x8b9b;
    const string HubId = "Delve_Hub";
    /// <summary>Where a module saves: the account's equipment and the character's sulphite.</summary>
    public const string EquipmentKey = "delve", SulphiteKey = "delve.sulphite", DepthKey = "delve.depth";

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
        public bool ResetNeeded;
        public readonly List<uint> Trail = [];
        public DelveRailRoute? Rail;
        /// <summary>What the next Delve chart (S→C 0x240) carries; /delve_chart sets it (one cell at 0, 0 by default).</summary>
        public (uint A, ushort B, List<InstanceDelve.Cell> Cells)? ChartProbe;
    }

    readonly ConditionalWeakTable<IPlayer, State> _states = new();
    static readonly Lazy<string?[]> StatIds = new(() =>
    {
        if (!ClientTables.Exists("data/tables/Stats.json")) return [];
        using var doc = GameTables.Open("data/tables/Stats.json");
        return [.. doc.RootElement.EnumerateArray().Select(r => r.GetProperty("Id").GetString())];
    });

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
        return InstanceDelve.DelveData(Sulphite(world.CharacterData), equipment.Azurite, equipment.Flares, equipment.Dynamite, equipment.Upgrades);
    }

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        RegisterSulphite(module);
        RegisterTrips(module);
        // The gauge and the Subterranean Chart read the character's sulphite from it, in the encampment and the mine.
        // Two arrays: sending encrypts a message in place, so the same one cannot go twice.
        GameEvents.EnterMessages.Add(module.Manifest.Id, conn =>
        {
            var player = PlayerOf(conn);
            if (!player.AreaId.StartsWith("Delve", StringComparison.Ordinal)) return [];
            var world = WorldOf(player);
            ushort amount = Sulphite(world.CharacterData);
            return [("0x0228 Delve resources and equipment", DelveData(world)),
                ("Synchronize Delve sulphite gauge", InstanceDelve.SulphiteChanged(amount, amount)),
                ("Synchronize Delve sulphite gauge", InstanceDelve.SulphiteChanged(amount, amount))];
        });
        GameEvents.AreaHeader.Add(module.Manifest.Id, conn => PlayerOf(conn).AreaId switch
        {
            "Delve_Main" => new AreaHeader(DelveTerrain.Data(PlayerOf(conn).Seed), [2]) { Level = MineLevel(module, PlayerOf(conn)) },
            HubId => new AreaHeader(null, [2]),
            _ => null,
        });
        GameEvents.Stats.Add(module.Manifest.Id, sheet =>
        {
            foreach (var (key, value) in DelveEquipment.Stats(Equipment(WorldOf(sheet.Player).AccountData)))
                if (key >= 1 && key <= StatIds.Value.Length && StatIds.Value[key - 1] is { } id
                    && (id == "delve_sulphite_capacity" || sheet.Player.AreaId.StartsWith("Delve", StringComparison.Ordinal))) sheet.Add(id, value);
        });
        GameEvents.AreaGenerated.Add(module.Manifest.Id, async conn =>
        {
            var player = PlayerOf(conn);
            await SpawnCrawler(module, player);
            await SpawnGenerator(module, player);
            await StartPendingTrip(module, player);
        });
        GameEvents.ObjectUse.Add(module.Manifest.Id, async (conn, use, at) =>
        {
            if (!_states.TryGetValue(PlayerOf(conn), out var state)) return false;
            var world = WorldOf(PlayerOf(conn));
            if (state.Checkpoints.TryGetValue(use.TargetId, out var checkpoint))
            {
                if (await world.WalkUpTo(checkpoint.X, checkpoint.Y, ObjectReach, at))
                    await OpenChart(module, PlayerOf(conn));
                return true;
            }
            if (state.Generator != 0 && use.TargetId == state.Generator)
            {
                if (await world.WalkUpTo(GeneratorAt.X, GeneratorAt.Y, ObjectReach, at))
                {
                    state.GeneratorOpen = true;
                    await PlayerOf(conn).Send(DelveData(world), "Voltaxic Generator resources");
                    await PlayerOf(conn).Send(InstanceWaypoints.OpenUi(InstanceDelve.UpgradesUi, 1), "Open Delve equipment upgrades");
                }
                return true;
            }
            if (state.Crawler != 0 && use.TargetId == state.Crawler)
            {
                // Walk there, then the Subterranean Chart (S→C 0x7b kind 0x1c with 1).
                if (await world.WalkUpTo(state.CrawlerCell.X, state.CrawlerCell.Y, ObjectReach, at))
                    await OpenChart(module, PlayerOf(conn));
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
                world.NpcEndTalk();
                await OpenChart(module, talk.Player);
                return true;
            }
            // Niko's Visit Mine Encampment: BeginDelve(), to the Azurite Mine Encampment, as /goto does.
            if (talk.Script.Contains("BeginDelve", StringComparison.Ordinal))
            {
                world.NpcEndTalk();
                var conn = ConnOf(talk.Player); var wire = conn.Wire;
                if (conn.Layout.Hash != Hub)
                    await ChangeArea(wire.Remote, wire.Stream, wire.Ciphers, conn,
                        new PendingTransfer(Hub, 0, false, AtWaypoint: true), $"{talk.Npc}: {talk.Option}");
                return true;
            }
            return false;
        });
        GameEvents.Packet(InstanceDelve.RouteId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            await RequestTrip(module, PlayerOf(conn), InstanceDelve.ReadRoute(bytes));
            return true;
        });
        foreach (ushort id in new[] { InstanceDelve.UpgradesRequestId, InstanceDelve.UpgradeId, InstanceDelve.PurchaseId })
            GameEvents.Packet(id).Add(module.Manifest.Id, async (conn, bytes) =>
            {
                await UseEquipment(module, PlayerOf(conn), id, bytes);
                return true;
            });
        // C→S 0x23f: the Delve chart (opened by Niko) asks for its data; S→C 0x240 answers (an empty chart for now).
        GameEvents.Packet(InstanceDelve.ChartRequestId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            if (bytes.Length != 2) return false;
            var mine = Mine(module, PlayerOf(conn));
            var probe = _states.GetOrCreateValue(PlayerOf(conn)).ChartProbe;
            uint a = probe?.A ?? mine.Seed; ushort b = probe?.B ?? 0;
            IReadOnlyList<InstanceDelve.Cell> cells = probe is { } value ? value.Cells : mine.Chart();
            await PlayerOf(conn).Send(InstanceDelve.Chart(a, b, cells), $"0x0240 Delve chart: {a}, {b}, {cells.Count} cell(s) {string.Join(" ", cells)}");
            var world = WorldOf(PlayerOf(conn));
            await PlayerOf(conn).Send(DelveData(world), "Delve chart resource snapshot");
            await RefreshGauge(PlayerOf(conn), world);
            return true;
        });
        GameEvents.Command(module.Manifest.Id, "delve_crawler", "/delve_crawler [x y degrees] (dev: create the Delve Crawler in the Azurite Mine Encampment, or move and turn the one there; a click on it opens the Subterranean Chart)", (conn, args) => CrawlerCommand(module, PlayerOf(conn), args));
        GameEvents.Command(module.Manifest.Id, "sulphite", "/sulphite <amount 0-65535> (dev: the character's Voltaxic Sulphite, saved and sent as the Delve data 0x228)", async (conn, args) =>
        {
            if (args.Length != 1 || !ushort.TryParse(args[0], out ushort amount)) throw new ArgumentException("/sulphite <amount 0-65535>");
            var world = WorldOf(PlayerOf(conn));
            world.CharacterData.Change(SulphiteKey, _ => amount.ToString());
            await PlayerOf(conn).Send(DelveData(world), $"0x0228 Delve data: {amount} sulphite (/sulphite)");
            await RefreshGauge(PlayerOf(conn), world);
            await PlayerOf(conn).Tell($"Sulphite: {amount}");
        });
        GameEvents.Command(module.Manifest.Id, "delve_chart", "/delve_chart <u32> <u16> [<u16> <u16> <u64> <u8> <u32> ...] (dev: what the next Delve chart (S→C 0x240) carries, to work out its fields)", async (conn, args) =>
        {
            try
            {
                var n = args.Select(a => a.StartsWith("0x") ? Convert.ToUInt64(a[2..], 16) : ulong.Parse(a)).ToArray();
                var cells = new List<InstanceDelve.Cell>();
                for (int i = 2; i + 4 < n.Length; i += 5) cells.Add(new((ushort)n[i], (ushort)n[i + 1], n[i + 2], (byte)n[i + 3], (uint)n[i + 4]));
                var state = _states.GetOrCreateValue(PlayerOf(conn));
                state.ChartProbe = (n.Length > 0 ? (uint)n[0] : 0, n.Length > 1 ? (ushort)n[1] : (ushort)0, cells);
                await PlayerOf(conn).Tell($"Next Delve chart: {state.ChartProbe.Value.A}, {state.ChartProbe.Value.B}, {cells.Count} cell(s). Open it from Niko.");
            }
            catch (FormatException) { await PlayerOf(conn).Tell("Numbers only (decimal or 0x…)."); }
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
                ConnOf(player).Instance.SetState(DestinationFlag(player));
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
        if (changed) await world.RefreshStats("Delve equipment upgrade");
    }

    // The Delve Crawler in the Azurite Mine Encampment. A click on it opens the Subterranean Chart, as Niko's option does.
    async Task SpawnCrawler(IModuleContext module, IPlayer player)
    {
        var world = WorldOf(player);
        var state = _states.GetOrCreateValue(player);
        if (world.AreaHash is not (Hub or InstanceDelve.MineArea) || state.Crawler != 0) return;
        var rails = world.AreaHash == InstanceDelve.MineArea ? DelveRailRoute.Load(player.AreaId, ConnOf(player).Seed) : null;
        state.Rail = rails;
        if (world.AreaHash == InstanceDelve.MineArea && rails is null) return;
        bool savedDestination = world.AreaHash == InstanceDelve.MineArea && ConnOf(player).Instance.HasState(DestinationFlag(player));
        var (x, y) = world.AreaHash == Hub ? CrawlerAt : rails!.Points[savedDestination ? ^1 : 0];
        const uint id = 0x0d200000;
        state.Crawler = id;
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
    }

    static Task RemoveSceneObject(IPlayer player, uint id)
    {
        var conn = ConnOf(player); var wire = conn.Wire;
        return global::Game.SendWorldRemoval(wire.Remote, wire.Stream, wire.Ciphers, conn, id);
    }

    async Task OpenChart(IModuleContext module, IPlayer player)
    {
        await player.Send(InstanceWaypoints.OpenUi(InstanceNpcs.DelveMapUi, 1), "Open Delve chart");
        await PublishChart(module, player);
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
