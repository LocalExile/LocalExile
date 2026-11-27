using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using GameServer.Core.Protocol.Instance;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain.Generation;
using G = global::Game;

namespace GameServer.Modules.Settlers.Town;

/// <summary>
/// Parses <c>metadata/terrain/leagues/settlers/hubtown/rooms/kingsmarch.arm</c> (room offset <c>(138, 138)</c>, code <c>0</c>)
/// and spawns all 3.29.3c Kingsmarch buildings, props, ships, farms, map devices, disenchanting stations, recombinator,
/// noticeboards, and ambient workers natively via S→C <c>0x02bb</c> with zero client/Frida patches.
/// </summary>
public static class KingsmarchHubWorld
{
    public const string AreaId = "KalguuranSettlersLeague";
    const string ArmPath = "metadata/terrain/leagues/settlers/hubtown/rooms/kingsmarch.arm";
    const int RoomOffsetX = 138;
    const int RoomOffsetY = 138;

    const string Prefix = "Metadata/Terrain/Leagues/Settlers/HubTown/Objects/";
    const string TypeDecoBase = Prefix + "DecoUpgradeBase";
    const string TypeDecoLarge = Prefix + "DecoUpgradeLarge";
    const string TypeDecoAnimated = Prefix + "DecoUpgradeAnimated";
    const string TypeDecoLargeAnimate = Prefix + "DecoUpgradeLargeAnimate";
    const string TypeDecoNpc = Prefix + "DecoUpgradeNPC";
    const string TypeDecoNpcIdle = Prefix + "DecoUpgradeNPCIdle";
    const string TypeDecoAnimalCtrl = Prefix + "DecoUpgradeAnimalController";
    const string TypeSheepUpgradeable = Prefix + "SheepUpgradeable";
    const string TypeDecoShip = Prefix + "DecoUpgradeShip";
    const string TypeFarmingPlot = Prefix + "FarmingPlot";
    const string TypeFarmingInteractable = Prefix + "FarmingPlotInteractable";
    const string TypeMapDevice = Prefix + "MapDevice";
    const string TypeMapDeviceButton = Prefix + "MapDeviceButton";
    const string TypeMapDevicePortal = Prefix + "MapDevicePortal";
    const string TypeDisenchantingWorker = Prefix + "DisenchantingWorker";
    const string TypeDisenchantingItem = Prefix + "DisenchantingItem";
    const string TypeNoticeboard = Prefix + "NoticeboardInteractable";
    const string TypeNoticeboardSmelting = Prefix + "NoticeboardInteractableSmelting";
    const string TypeRecombinatingBench = "Metadata/MiscellaneousObjects/Settlers/RecombinatingBench";
    const string TypeWorkerController = Prefix + "WorkerController";
    const string TypeTempWorker = Prefix + "TemporaryNPCWorker";

    static readonly HashSet<string> StaticDecoTypes = new(StringComparer.Ordinal)
    {
        TypeDecoBase,
        TypeDecoLarge,
        TypeDecoAnimated,
        TypeDecoLargeAnimate,
        TypeDecoNpc,
        TypeDecoNpcIdle,
        TypeDecoAnimalCtrl,
        TypeSheepUpgradeable,
    };

    static readonly HashSet<string> AllManagedTypes = new(StringComparer.Ordinal)
    {
        TypeDecoBase, TypeDecoLarge, TypeDecoAnimated, TypeDecoLargeAnimate,
        TypeDecoNpc, TypeDecoNpcIdle, TypeDecoAnimalCtrl, TypeSheepUpgradeable,
        TypeDecoShip, TypeFarmingPlot, TypeFarmingInteractable,
        TypeMapDevice, TypeMapDeviceButton, TypeMapDevicePortal,
        TypeDisenchantingWorker, TypeDisenchantingItem,
        TypeNoticeboard, TypeNoticeboardSmelting, TypeRecombinatingBench,
        TypeWorkerController, TypeTempWorker,
    };

    public sealed record ArmObject(
        int X,
        int Y,
        float Rotation,
        float? Z,
        string Model,
        string Type,
        IReadOnlyDictionary<string, int> IntParams,
        int AssociatedIndex = -1);

    sealed record TypeSpec(
        string Type,
        IReadOnlyList<string> WireComponents,
        byte StateMachineIndex,
        StateMachineData DefaultStates,
        bool Blocking);

    sealed record InteractableEntry(uint EntityId, int X, int Y, byte ScreenKind, uint Param1, string Label);

    sealed record LiveTrackedObject(uint EntityId, string Type, byte StateMachineIndex, int Index, StateMachineData LastSent);

    sealed record SpawnedRecord(
        uint EntityId,
        ArmObject Arm,
        TypeSpec Spec,
        IReadOnlyDictionary<uint, int> IntVariables,
        StateMachineData CurrentStates);

    sealed class InstanceVisuals
    {
        public readonly Lock Gate = new();
        public bool Built;
        public readonly HashSet<Player> PresentedPlayers = [];
        public readonly List<SpawnedRecord> AllObjects = [];
        public readonly Dictionary<uint, int> RecordIndexById = [];
        public readonly Dictionary<uint, InteractableEntry> Interactables = [];
        public readonly List<LiveTrackedObject> Ships = [];
        public readonly List<LiveTrackedObject> FarmPlots = [];
        public readonly List<LiveTrackedObject> FarmButtons = [];
        public readonly List<LiveTrackedObject> MapDevices = [];
        public readonly List<LiveTrackedObject> MapButtons = [];
        public readonly List<LiveTrackedObject> MapPortals = [];
        public readonly List<LiveTrackedObject> DisenchantWorkers = [];
        public readonly List<LiveTrackedObject> DisenchantItems = [];
        public readonly List<LiveTrackedObject> TempWorkers = [];
        public LiveTrackedObject? WorkerController;
    }

    static readonly ConditionalWeakTable<GameArea, InstanceVisuals> AreaVisuals = new();

    static IReadOnlyList<ArmObject> _armObjects = [];
    static IReadOnlyDictionary<string, TypeSpec> _specs = new Dictionary<string, TypeSpec>(StringComparer.Ordinal);

    public static bool IsKingsmarch(Player player) =>
        player.Layout.Id.Equals(AreaId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Loads <c>kingsmarch.arm</c> from <c>data/game-files.zip</c> and caches component/StateMachine specs from <c>client.db</c>.</summary>
    public static void LoadGameData()
    {
        _specs = LoadTypeSpecs();
        _armObjects = LoadArmObjects();
    }

    public static int LoadedObjectCount => _armObjects.Count;

    // ---- AreaGenerated / PlayerEntered: build once per instance, present to every entering/re-entering player ------

    public static Task OnAreaGeneratedAsync(Player player) => EnsureAndPresentAsync(player);
    public static Task OnPlayerEnteredAsync(Player player) => EnsureAndPresentAsync(player);

    public static void OnPlayerLeaving(Player player)
    {
        if (AreaVisuals.TryGetValue(player.Area, out var visuals))
        {
            lock (visuals.Gate) visuals.PresentedPlayers.Remove(player);
        }
    }

    static async Task EnsureAndPresentAsync(Player player)
    {
        if (!IsKingsmarch(player) || _armObjects.Count == 0) return;

        var visuals = AreaVisuals.GetOrCreateValue(player.Area);
        var townState = VillageTownService.GetState(player);
        var toPresent = new List<(uint Id, string Type, QuestObject Obj, string Log)>(_armObjects.Count);

        lock (townState.Gate)
        {
            VillageTownService.SimulateLocked(townState, VillageTownService.ClockUtcSeconds());

            lock (visuals.Gate)
            {
                if (!visuals.Built)
                {
                    int activeWorkerBudget = CountActiveTownWorkersLocked(townState);
                    int tempWorkerOrdinal = 0;

                    foreach (var obj in _armObjects)
                    {
                        if (!_specs.TryGetValue(obj.Type, out var spec)) continue;
                        if (obj.IntParams.TryGetValue("upgrade_type", out int upgType) && upgType > 8)
                            continue;

                        StateMachineData sm = BuildInitialStateMachineLocked(obj, spec.DefaultStates, townState, ref tempWorkerOrdinal, activeWorkerBudget);
                        var intVars = BuildEntityIntVariables(obj);
                        uint id = player.TakeObjectId();
                        var rec = new SpawnedRecord(id, obj, spec, intVars, sm);
                        visuals.RecordIndexById[id] = visuals.AllObjects.Count;
                        visuals.AllObjects.Add(rec);
                        TrackSpawnedLocked(visuals, id, obj, spec, sm);
                    }
                    visuals.Built = true;
                }

                if (!visuals.PresentedPlayers.Add(player))
                    return; // Already presented to this connection.

                for (int i = 0; i < visuals.AllObjects.Count; i++)
                {
                    var rec = visuals.AllObjects[i];
                    var qObj = new QuestObject(
                        rec.Arm.X,
                        rec.Arm.Y,
                        rec.Spec.WireComponents,
                        Rotation: rec.Arm.Rotation,
                        Height: rec.Arm.Z,
                        Model: string.IsNullOrEmpty(rec.Arm.Model) ? null : rec.Arm.Model,
                        Blocked: false)
                    {
                        StateMachine = rec.CurrentStates,
                        IntVariables = rec.IntVariables,
                    };
                    toPresent.Add((rec.EntityId, rec.Arm.Type, qObj,
                        $"0x02bb kingsmarch {rec.Arm.Type.Split('/')[^1]} 0x{rec.EntityId:x} at ({rec.Arm.X}, {rec.Arm.Y})"));
                }
            }
        }

        // Send all creates cleanly in one non-stalling batch (wake: true, awaitBirth: false prevents a 349-frame lockstep stall).
        var previousView = Program.ViewOf.Value;
        Program.ViewOf.Value = player;
        try
        {
            foreach (var (id, type, qObj, log) in toPresent)
            {
                byte[] create = InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(type), qObj.Blob());
                await G.SendWorldCreate(player, id, () => create, log, wake: true, awaitBirth: false);
            }
        }
        finally { Program.ViewOf.Value = previousView; }
    }

    // ---- ObjectUse: clicking Kingsmarch interactables opens exact 0x007b windows ------------------------------------

    public static async Task<bool> OnObjectUseAsync(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (!use.InitialPress || !IsKingsmarch(player)) return false;
        if (!AreaVisuals.TryGetValue(player.Area, out var visuals)) return false;

        InteractableEntry? entry;
        lock (visuals.Gate)
            visuals.Interactables.TryGetValue(use.TargetId, out entry);
        if (entry is null) return false;

        if (!G.TryBeginObjectWalk(player, requestedAt, entry.X, entry.Y, 4, out var walk))
            return true;

        var (action, now, _, _, arrive) = walk;
        if (arrive > now) await G.AreaDelay(player, arrive - now);
        if (!G.PlayerActionCurrent(player, action)) return true;

        // Sync fresh town state right before opening the window so timers & balances are exact.
        await VillageTownService.TickPlayerAsync(player);
        await VillageTownService.SyncStateAsync(player);
        await Program.Send(player, InstanceWaypoints.OpenUi(entry.ScreenKind, entry.Param1),
            $"0x007b kingsmarch open {entry.Label} (kind=0x{entry.ScreenKind:x2}, p1={entry.Param1})",
            still: () => G.PlayerActionCurrent(player, action));
        return true;
    }

    // ---- Live StateMachine updates when town state mutates ----------------------------------------------------------

    public static async Task SyncWorldVisualsAsync(Player player, VillageTownState townState)
    {
        if (!IsKingsmarch(player) || !AreaVisuals.TryGetValue(player.Area, out var visuals))
            return;

        var updates = new List<(uint Id, byte SmIdx, StateMachineData Sm, string Log)>();

        lock (townState.Gate)
        {
            bool outOfGold = townState.TreasuryGold == 0;
            int idleWorkers = CountWorkersByJobTypeLocked(townState, VillageData.JobTypeIdling);
            int miningWorkers = CountWorkersByJobTypeLocked(townState, VillageData.JobTypeMining);
            int smeltingWorkers = CountWorkersByJobTypeLocked(townState, VillageData.JobTypeSmelting);
            int disenchantWorkers = CountWorkersByJobTypeLocked(townState, VillageData.JobTypeDisenchanting);
            bool haveDisenchantItems = townState.DisenchantQueue.Count > 0 || townState.DisenchantTotal > 0;
            bool isMining = miningWorkers > 0 && HasAnyPendingOreLocked(townState) && !outOfGold;
            bool isSmelting = smeltingWorkers > 0 && HasAnyRawOreLocked(townState) && !outOfGold;
            bool isDisenchanting = disenchantWorkers > 0 && haveDisenchantItems && !outOfGold;
            int activeBudget = CountActiveTownWorkersLocked(townState);

            lock (visuals.Gate)
            {
                // 1. WorkerController (SM idx 2, 8 shared states)
                if (visuals.WorkerController is { } wc)
                {
                    var next = new StateMachineData(
                        [outOfGold ? 1 : 0, idleWorkers, disenchantWorkers, miningWorkers, smeltingWorkers,
                         haveDisenchantItems ? 1 : 0, isMining ? 1 : 0, isSmelting ? 1 : 0],
                        wc.LastSent.SharedTimers, wc.LastSent.Names);
                    if (!StateEquals(wc.LastSent, next))
                    {
                        visuals.WorkerController = wc with { LastSent = next };
                        updates.Add((wc.EntityId, wc.StateMachineIndex, next, "kingsmarch WorkerController"));
                    }
                }

                // 2. Ships (visible=1, blocking=0, epk=0, depart, num_workers)
                for (int i = 0; i < visuals.Ships.Count; i++)
                {
                    var tracked = visuals.Ships[i];
                    int sIdx = Math.Clamp(tracked.Index, 0, VillageData.ShipCount - 1);
                    var ship = townState.Ships[sIdx];
                    int crew = CountWorkersOnJobLocked(townState, (byte)(VillageData.JobShippingFirst + sIdx));
                    var next = new StateMachineData([1, 0, 0, ship.IsAtSea ? 1 : 0, crew > 0 ? 1 : 0],
                        tracked.LastSent.SharedTimers, tracked.LastSent.Names);
                    if (!StateEquals(tracked.LastSent, next))
                    {
                        visuals.Ships[i] = tracked with { LastSent = next };
                        updates.Add((tracked.EntityId, tracked.StateMachineIndex, next, $"kingsmarch ship {sIdx}"));
                    }
                }

                // 3. FarmingPlots (plant, num_workers, out_of_gold)
                for (int i = 0; i < visuals.FarmPlots.Count; i++)
                {
                    var tracked = visuals.FarmPlots[i];
                    int pIdx = Math.Clamp(tracked.Index, 0, VillageData.FarmPlotCount - 1);
                    int plant = CropToPlantState(townState.FarmPlotCropResource[pIdx]);
                    int farmers = CountWorkersOnJobLocked(townState, (byte)(VillageData.JobFarmingFirst + pIdx));
                    var next = new StateMachineData([plant, farmers, outOfGold ? 1 : 0],
                        tracked.LastSent.SharedTimers, tracked.LastSent.Names);
                    if (!StateEquals(tracked.LastSent, next))
                    {
                        visuals.FarmPlots[i] = tracked with { LastSent = next };
                        updates.Add((tracked.EntityId, tracked.StateMachineIndex, next, $"kingsmarch farm plot {pIdx}"));
                    }
                }

                // 4. FarmingPlotInteractables (visible=1, blocking=0, epk=0, num_workers, out_of_gold)
                for (int i = 0; i < visuals.FarmButtons.Count; i++)
                {
                    var tracked = visuals.FarmButtons[i];
                    int pIdx = Math.Clamp(tracked.Index, 0, VillageData.FarmPlotCount - 1);
                    int farmers = CountWorkersOnJobLocked(townState, (byte)(VillageData.JobFarmingFirst + pIdx));
                    var next = new StateMachineData([1, 0, 0, farmers, outOfGold ? 1 : 0],
                        tracked.LastSent.SharedTimers, tracked.LastSent.Names);
                    if (!StateEquals(tracked.LastSent, next))
                    {
                        visuals.FarmButtons[i] = tracked with { LastSent = next };
                        updates.Add((tracked.EntityId, tracked.StateMachineIndex, next, $"kingsmarch farm button {pIdx}"));
                    }
                }

                // 5. MapDevices (visible=1, blocking=0, epk=0, active)
                for (int i = 0; i < visuals.MapDevices.Count; i++)
                {
                    var tracked = visuals.MapDevices[i];
                    int dIdx = Math.Clamp(tracked.Index, 0, VillageData.MapDeviceCount - 1);
                    int active = MapDeviceActiveState(townState.MapDevices[dIdx]);
                    var next = new StateMachineData([1, 0, 0, active], tracked.LastSent.SharedTimers, tracked.LastSent.Names);
                    if (!StateEquals(tracked.LastSent, next))
                    {
                        visuals.MapDevices[i] = tracked with { LastSent = next };
                        updates.Add((tracked.EntityId, tracked.StateMachineIndex, next, $"kingsmarch map device {dIdx}"));
                    }
                }

                // 6. MapDeviceButtons (visible=1, blocking=0, epk=0, active, num_workers)
                for (int i = 0; i < visuals.MapButtons.Count; i++)
                {
                    var tracked = visuals.MapButtons[i];
                    int dIdx = Math.Clamp(tracked.Index, 0, VillageData.MapDeviceCount - 1);
                    int active = MapDeviceActiveState(townState.MapDevices[dIdx]);
                    int runners = CountWorkersOnJobLocked(townState, (byte)(VillageData.JobMappingFirst + dIdx));
                    var next = new StateMachineData([1, 0, 0, active, runners], tracked.LastSent.SharedTimers, tracked.LastSent.Names);
                    if (!StateEquals(tracked.LastSent, next))
                    {
                        visuals.MapButtons[i] = tracked with { LastSent = next };
                        updates.Add((tracked.EntityId, tracked.StateMachineIndex, next, $"kingsmarch map button {dIdx}"));
                    }
                }

                // 7. MapDevicePortals (active)
                for (int i = 0; i < visuals.MapPortals.Count; i++)
                {
                    var tracked = visuals.MapPortals[i];
                    int dIdx = Math.Clamp(tracked.Index, 0, VillageData.MapDeviceCount - 1);
                    int portalActive = townState.MapDevices[dIdx].IsRunning ? 1 : 0;
                    var next = new StateMachineData([portalActive], tracked.LastSent.SharedTimers, tracked.LastSent.Names);
                    if (!StateEquals(tracked.LastSent, next))
                    {
                        visuals.MapPortals[i] = tracked with { LastSent = next };
                        updates.Add((tracked.EntityId, tracked.StateMachineIndex, next, $"kingsmarch map portal {dIdx}"));
                    }
                }

                // 8. DisenchantingWorkers & DisenchantingItems (visible, working)
                for (int i = 0; i < visuals.DisenchantWorkers.Count; i++)
                {
                    var tracked = visuals.DisenchantWorkers[i];
                    int vis = disenchantWorkers > i ? 1 : 0;
                    int work = vis == 1 && isDisenchanting ? 1 : 0;
                    var next = new StateMachineData([vis, work], tracked.LastSent.SharedTimers, tracked.LastSent.Names);
                    if (!StateEquals(tracked.LastSent, next))
                    {
                        visuals.DisenchantWorkers[i] = tracked with { LastSent = next };
                        updates.Add((tracked.EntityId, tracked.StateMachineIndex, next, $"kingsmarch disenchant worker {i}"));
                    }
                }
                for (int i = 0; i < visuals.DisenchantItems.Count; i++)
                {
                    var tracked = visuals.DisenchantItems[i];
                    int vis = haveDisenchantItems ? 1 : 0;
                    int work = isDisenchanting ? 1 : 0;
                    var next = new StateMachineData([vis, work], tracked.LastSent.SharedTimers, tracked.LastSent.Names);
                    if (!StateEquals(tracked.LastSent, next))
                    {
                        visuals.DisenchantItems[i] = tracked with { LastSent = next };
                        updates.Add((tracked.EntityId, tracked.StateMachineIndex, next, $"kingsmarch disenchant item {i}"));
                    }
                }

                // 9. TemporaryNPCWorkers (visible, working)
                for (int i = 0; i < visuals.TempWorkers.Count; i++)
                {
                    var tracked = visuals.TempWorkers[i];
                    int vis = i < activeBudget ? 1 : 0;
                    int work = vis == 1 && !outOfGold ? 1 : 0;
                    var next = new StateMachineData([vis, work], tracked.LastSent.SharedTimers, tracked.LastSent.Names);
                    if (!StateEquals(tracked.LastSent, next))
                    {
                        visuals.TempWorkers[i] = tracked with { LastSent = next };
                        updates.Add((tracked.EntityId, tracked.StateMachineIndex, next, $"kingsmarch temp worker {i}"));
                    }
                }
            }
        }

        Player[] recipients;
        lock (visuals.Gate)
        {
            foreach (var (id, _, sm, _) in updates)
            {
                if (visuals.RecordIndexById.TryGetValue(id, out int recIdx))
                    visuals.AllObjects[recIdx] = visuals.AllObjects[recIdx] with { CurrentStates = sm };
            }
            recipients = [.. visuals.PresentedPlayers.Where(p => !p.Closed && !p.LeavingArea)];
        }

        if (updates.Count == 0 || recipients.Length == 0) return;
        foreach (var recipient in recipients)
        {
            foreach (var (id, smIdx, sm, log) in updates)
            {
                try { await G.SendWorldRecord(recipient, id, InstanceEntities.StateMachineUpdate(id, smIdx, sm), $"0x02bc {log}"); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
            }
        }
    }

    // ---- Initial StateMachine & variable builders -------------------------------------------------------------------

    static StateMachineData BuildInitialStateMachineLocked(
        ArmObject obj,
        StateMachineData defaults,
        VillageTownState state,
        ref int tempWorkerOrdinal,
        int activeWorkerBudget)
    {
        bool outOfGold = state.TreasuryGold == 0;
        int timers = defaults.SharedTimers;
        var names = defaults.Names;

        switch (obj.Type)
        {
            case TypeDecoShip:
            {
                int sIdx = Math.Clamp(obj.AssociatedIndex, 0, VillageData.ShipCount - 1);
                var ship = state.Ships[sIdx];
                int crew = CountWorkersOnJobLocked(state, (byte)(VillageData.JobShippingFirst + sIdx));
                return new([1, 0, 0, ship.IsAtSea ? 1 : 0, crew > 0 ? 1 : 0], timers, names);
            }
            case TypeFarmingPlot:
            {
                int pIdx = Math.Clamp(obj.AssociatedIndex, 0, VillageData.FarmPlotCount - 1);
                int plant = CropToPlantState(state.FarmPlotCropResource[pIdx]);
                int farmers = CountWorkersOnJobLocked(state, (byte)(VillageData.JobFarmingFirst + pIdx));
                return new([plant, farmers, outOfGold ? 1 : 0], timers, names);
            }
            case TypeFarmingInteractable:
            {
                int pIdx = Math.Clamp(obj.AssociatedIndex, 0, VillageData.FarmPlotCount - 1);
                int farmers = CountWorkersOnJobLocked(state, (byte)(VillageData.JobFarmingFirst + pIdx));
                return new([1, 0, 0, farmers, outOfGold ? 1 : 0], timers, names);
            }
            case TypeMapDevice:
            {
                int dIdx = Math.Clamp(obj.AssociatedIndex, 0, VillageData.MapDeviceCount - 1);
                int active = MapDeviceActiveState(state.MapDevices[dIdx]);
                return new([1, 0, 0, active], timers, names);
            }
            case TypeMapDeviceButton:
            {
                int dIdx = Math.Clamp(obj.AssociatedIndex, 0, VillageData.MapDeviceCount - 1);
                int active = MapDeviceActiveState(state.MapDevices[dIdx]);
                int runners = CountWorkersOnJobLocked(state, (byte)(VillageData.JobMappingFirst + dIdx));
                return new([1, 0, 0, active, runners], timers, names);
            }
            case TypeMapDevicePortal:
            {
                int dIdx = Math.Clamp(obj.AssociatedIndex, 0, VillageData.MapDeviceCount - 1);
                return new([state.MapDevices[dIdx].IsRunning ? 1 : 0], timers, names);
            }
            case TypeDisenchantingWorker:
            {
                int dis = CountWorkersByJobTypeLocked(state, VillageData.JobTypeDisenchanting);
                bool haveItems = state.DisenchantQueue.Count > 0 || state.DisenchantTotal > 0;
                int idx = Math.Max(0, obj.AssociatedIndex);
                int vis = dis > idx ? 1 : 0;
                return new([vis, vis == 1 && haveItems && !outOfGold ? 1 : 0], timers, names);
            }
            case TypeDisenchantingItem:
            {
                int dis = CountWorkersByJobTypeLocked(state, VillageData.JobTypeDisenchanting);
                bool haveItems = state.DisenchantQueue.Count > 0 || state.DisenchantTotal > 0;
                return new([haveItems ? 1 : 0, dis > 0 && haveItems && !outOfGold ? 1 : 0], timers, names);
            }
            case TypeTempWorker:
            {
                int ord = tempWorkerOrdinal++;
                int vis = ord < activeWorkerBudget ? 1 : 0;
                return new([vis, vis == 1 && !outOfGold ? 1 : 0], timers, names);
            }
            case TypeWorkerController:
            {
                int idle = CountWorkersByJobTypeLocked(state, VillageData.JobTypeIdling);
                int dis = CountWorkersByJobTypeLocked(state, VillageData.JobTypeDisenchanting);
                int mine = CountWorkersByJobTypeLocked(state, VillageData.JobTypeMining);
                int smelt = CountWorkersByJobTypeLocked(state, VillageData.JobTypeSmelting);
                bool haveDis = state.DisenchantQueue.Count > 0 || state.DisenchantTotal > 0;
                bool isMin = mine > 0 && HasAnyPendingOreLocked(state) && !outOfGold;
                bool isSme = smelt > 0 && HasAnyRawOreLocked(state) && !outOfGold;
                return new([outOfGold ? 1 : 0, idle, dis, mine, smelt, haveDis ? 1 : 0, isMin ? 1 : 0, isSme ? 1 : 0], timers, names);
            }
            default:
            {
                // DecoUpgrade*, NoticeboardInteractable*, RecombinatingBench: visible=1, blocking=0, epk=0.
                int[] values = [.. defaults.SharedStates];
                if (values.Length > 0) values[0] = 1; // visible = 1
                if (values.Length > 1) values[1] = 0; // blocking = 0
                return new(values, timers, names);
            }
        }
    }

    static Dictionary<uint, int> BuildEntityIntVariables(ArmObject obj)
    {
        var vars = new Dictionary<uint, int>();
        foreach (var (k, v) in obj.IntParams)
        {
            if (k.Equals("delete_on_console", StringComparison.OrdinalIgnoreCase)) continue;
            vars[QuestObject.VariableHash(k)] = v;
        }
        return vars;
    }

    static void TrackSpawnedLocked(InstanceVisuals visuals, uint id, ArmObject obj, TypeSpec spec, StateMachineData sm)
    {
        switch (obj.Type)
        {
            case TypeNoticeboard:
            {
                int upg = obj.IntParams.GetValueOrDefault("upgrade_type", 0);
                byte screen = upg == 1 ? InstanceVillage.MineWindowUi : InstanceVillage.TownMapWindowUi;
                string label = upg == 1 ? "Mining Noticeboard" : "Town Map Noticeboard";
                visuals.Interactables[id] = new(id, obj.X, obj.Y, screen, 0, label);
                break;
            }
            case TypeNoticeboardSmelting:
                visuals.Interactables[id] = new(id, obj.X, obj.Y, InstanceVillage.BlackSmithWindowUi, 0, "Smelting Noticeboard");
                break;
            case TypeRecombinatingBench:
                visuals.Interactables[id] = new(id, obj.X, obj.Y, InstanceVillage.RecombinatorWindowUi, 0, "Recombinating Bench");
                break;
            case TypeFarmingInteractable:
            {
                int plotIdx = Math.Clamp(obj.AssociatedIndex, 0, VillageData.FarmPlotCount - 1);
                visuals.Interactables[id] = new(id, obj.X, obj.Y, InstanceVillage.FarmWindowUi, (uint)plotIdx, $"Farming Plot {plotIdx + 1}");
                visuals.FarmButtons.Add(new(id, obj.Type, spec.StateMachineIndex, plotIdx, sm));
                break;
            }
            case TypeMapDeviceButton:
            {
                int devIdx = Math.Clamp(obj.AssociatedIndex, 0, VillageData.MapDeviceCount - 1);
                visuals.Interactables[id] = new(id, obj.X, obj.Y, InstanceVillage.MapRunnersWindowUi, (uint)devIdx, $"Map Device {devIdx + 1}");
                visuals.MapButtons.Add(new(id, obj.Type, spec.StateMachineIndex, devIdx, sm));
                break;
            }
            case TypeDecoShip:
                visuals.Ships.Add(new(id, obj.Type, spec.StateMachineIndex, Math.Clamp(obj.AssociatedIndex, 0, VillageData.ShipCount - 1), sm));
                break;
            case TypeFarmingPlot:
                visuals.FarmPlots.Add(new(id, obj.Type, spec.StateMachineIndex, Math.Clamp(obj.AssociatedIndex, 0, VillageData.FarmPlotCount - 1), sm));
                break;
            case TypeMapDevice:
                visuals.MapDevices.Add(new(id, obj.Type, spec.StateMachineIndex, Math.Clamp(obj.AssociatedIndex, 0, VillageData.MapDeviceCount - 1), sm));
                break;
            case TypeMapDevicePortal:
                visuals.MapPortals.Add(new(id, obj.Type, spec.StateMachineIndex, Math.Clamp(obj.AssociatedIndex, 0, VillageData.MapDeviceCount - 1), sm));
                break;
            case TypeDisenchantingWorker:
                visuals.DisenchantWorkers.Add(new(id, obj.Type, spec.StateMachineIndex, obj.AssociatedIndex, sm));
                break;
            case TypeDisenchantingItem:
                visuals.DisenchantItems.Add(new(id, obj.Type, spec.StateMachineIndex, obj.AssociatedIndex, sm));
                break;
            case TypeTempWorker:
                visuals.TempWorkers.Add(new(id, obj.Type, spec.StateMachineIndex, visuals.TempWorkers.Count, sm));
                break;
            case TypeWorkerController:
                visuals.WorkerController = new(id, obj.Type, spec.StateMachineIndex, 0, sm);
                break;
        }
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    static int CropToPlantState(byte cropResource) => cropResource switch
    {
        VillageData.ResWheat => 1,
        VillageData.ResCorn => 2,
        VillageData.ResPumpkin => 3,
        VillageData.ResGlobulora => 4,
        VillageData.ResOrbisfera => 5,
        _ => 0,
    };

    static int MapDeviceActiveState(TownMapDevice dev) => dev.State switch
    {
        1 when !dev.BossAmbushPending => 1,
        2 or 3 => 2,
        _ => 0,
    };

    static int CountWorkersByJobTypeLocked(VillageTownState state, byte jobType)
    {
        int count = 0;
        for (int i = 0; i < state.Workers.Count; i++)
        {
            byte job = state.Workers[i].AssignedJobRow;
            if (job == VillageData.JobIdling || job == InstanceVillage.NoneRow)
            {
                if (jobType == VillageData.JobTypeIdling) count++;
                continue;
            }
            if (job < VillageData.Jobs.Count && VillageData.Jobs[job].JobType == jobType)
                count++;
        }
        return count;
    }

    static int CountWorkersOnJobLocked(VillageTownState state, byte jobRow)
    {
        int count = 0;
        for (int i = 0; i < state.Workers.Count; i++)
            if (state.Workers[i].AssignedJobRow == jobRow) count++;
        return count;
    }

    static int CountActiveTownWorkersLocked(VillageTownState state) =>
        Math.Min(42, Math.Max(4, state.Workers.Count));

    static bool HasAnyPendingOreLocked(VillageTownState state)
    {
        for (int r = VillageData.ResPendingIron; r <= VillageData.ResPendingVerisium; r++)
            if (state.Resources[r] > 0) return true;
        return false;
    }

    static bool HasAnyRawOreLocked(VillageTownState state)
    {
        for (int r = VillageData.ResIronOre; r <= VillageData.ResVerisiumOre; r++)
            if (state.Resources[r] > 0) return true;
        return false;
    }

    static bool StateEquals(StateMachineData a, StateMachineData b)
    {
        if (a.SharedStates.Count != b.SharedStates.Count) return false;
        for (int i = 0; i < a.SharedStates.Count; i++)
            if (a.SharedStates[i] != b.SharedStates[i]) return false;
        return true;
    }

    // ---- kingsmarch.arm & client.db object_types loading ------------------------------------------------------------

    static Dictionary<string, TypeSpec> LoadTypeSpecs()
    {
        var specs = new Dictionary<string, TypeSpec>(StringComparer.Ordinal);
        try
        {
            var sharedStates = ObjectTypes.SharedStates();
            foreach (var (type, row) in ObjectTypes.All())
            {
                if (!AllManagedTypes.Contains(type)) continue;
                var rawComps = ObjectTypes.Components(row);
                // Brackets is a 0-byte client component (slot17 = ret) not listed in QuestObject.Supported; strip it.
                var wireComps = rawComps.Where(c => c != "Brackets").ToArray();
                int smIdx = Array.IndexOf(wireComps, "StateMachine");
                if (smIdx < 0) continue;
                var sm = ObjectTypes.StateMachine(row, sharedStates);
                if (sm is null || !QuestObject.CanSerialize(wireComps, haveStateMachine: true)) continue;
                bool blocking = ObjectTypes.Flag(row, "positioned_blocking") == true;
                specs[type] = new TypeSpec(type, wireComps, (byte)smIdx, sm, blocking);
            }
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            Console.WriteLine($"[settlers] KingsmarchHubWorld type spec load skipped: {ex.Message}");
        }
        return specs;
    }

    static List<ArmObject> LoadArmObjects()
    {
        byte[]? raw;
        try { raw = GamePack.Default.TryRead(ArmPath); }
        catch { return []; }
        if (raw is null || raw.Length == 0) return [];

        string text = DecodeArmText(raw);
        var parsed = new List<ArmObject>(350);

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || !line.Contains('"')) continue;
            if (TryParseArmLine(line) is { } obj && AllManagedTypes.Contains(obj.Type))
                parsed.Add(obj);
        }

        // Associate FarmingPlots -> nearest FarmingPlotInteractable (plot_index 0..5),
        // MapDevices & MapDevicePortals -> nearest MapDeviceButton (map_device_index 0..2),
        // DisenchantingWorkers & DisenchantingItems -> ordinal 0..1.
        var farmButtons = parsed.Where(o => o.Type == TypeFarmingInteractable).ToList();
        var mapButtons = parsed.Where(o => o.Type == TypeMapDeviceButton).ToList();
        int disWorkerIdx = 0, disItemIdx = 0;

        for (int i = 0; i < parsed.Count; i++)
        {
            var o = parsed[i];
            int assoc = -1;
            if (o.Type == TypeDecoShip)
                assoc = o.IntParams.GetValueOrDefault("ship_index", 0);
            else if (o.Type == TypeFarmingInteractable)
                assoc = o.IntParams.GetValueOrDefault("plot_index", 0);
            else if (o.Type == TypeMapDeviceButton)
                assoc = o.IntParams.GetValueOrDefault("map_device_index", 0);
            else if (o.Type == TypeFarmingPlot && farmButtons.Count > 0)
            {
                var nearest = farmButtons.MinBy(b => DistSq(o.X, o.Y, b.X, b.Y))!;
                assoc = nearest.IntParams.GetValueOrDefault("plot_index", 0);
            }
            else if ((o.Type == TypeMapDevice || o.Type == TypeMapDevicePortal) && mapButtons.Count > 0)
            {
                var nearest = mapButtons.MinBy(b => DistSq(o.X, o.Y, b.X, b.Y))!;
                assoc = nearest.IntParams.GetValueOrDefault("map_device_index", 0);
            }
            else if (o.Type == TypeDisenchantingWorker)
                assoc = disWorkerIdx++;
            else if (o.Type == TypeDisenchantingItem)
                assoc = disItemIdx++;

            if (assoc >= 0) parsed[i] = o with { AssociatedIndex = assoc };
        }

        return parsed;
    }

    static long DistSq(int x1, int y1, int x2, int y2)
    {
        long dx = x1 - x2, dy = y1 - y2;
        return dx * dx + dy * dy;
    }

    static string DecodeArmText(byte[] raw)
    {
        if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
            return Encoding.Unicode.GetString(raw, 2, raw.Length - 2);
        if (raw.Length >= 4 && raw[1] == 0 && raw[3] == 0)
            return Encoding.Unicode.GetString(raw);
        return Encoding.UTF8.GetString(raw);
    }

    /// <summary>
    /// Parses one <c>kingsmarch.arm</c> object placement line:
    /// <list type="bullet">
    /// <item>Format 0: <c>rx ry 0 angle qx qy qz qw a b hasZ [z] scale "model.ao" "Metadata/..." paramCount [k=v ...]</c></item>
    /// <item>Format 1: <c>rx ry 1 wx wy angle qx qy qz qw a b hasZ [z] scale "model.ao" "Metadata/..." paramCount [k=v ...]</c></item>
    /// </list>
    /// </summary>
    internal static ArmObject? TryParseArmLine(string line)
    {
        int q1 = line.IndexOf('"');
        if (q1 <= 0) return null;
        int q2 = line.IndexOf('"', q1 + 1);
        if (q2 < 0) return null;
        int q3 = line.IndexOf('"', q2 + 1);
        if (q3 < 0) return null;
        int q4 = line.IndexOf('"', q3 + 1);
        if (q4 < 0) return null;

        string model = line[(q1 + 1)..q2];
        string type = line[(q3 + 1)..q4];
        if (!type.StartsWith("Metadata/", StringComparison.Ordinal)) return null;

        string[] nums = line[..q1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (nums.Length < 11) return null;
        if (!int.TryParse(nums[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rx)) return null;
        if (!int.TryParse(nums[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ry)) return null;
        if (!int.TryParse(nums[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int coordFlag)) return null;

        int cursor = coordFlag == 1 ? 5 : 3; // coordFlag == 1 carries two extra world-unit floats (wx, wy)
        if (nums.Length < cursor + 8) return null;

        if (!float.TryParse(nums[cursor], NumberStyles.Float, CultureInfo.InvariantCulture, out float angle)) return null;
        // Skip qx, qy, qz, qw, a, b (6 tokens) -> hasZ at cursor + 7
        int hasZIdx = cursor + 7;
        float? z = null;
        if (hasZIdx < nums.Length && int.TryParse(nums[hasZIdx], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hasZ) && hasZ == 1)
        {
            if (hasZIdx + 1 < nums.Length && float.TryParse(nums[hasZIdx + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float zVal))
                z = zVal;
        }

        // Normalize rotation into [0, 2π) matching TownNpcs.Room.Angle (code 0).
        const float TwoPi = MathF.PI * 2f;
        float rot = angle % TwoPi;
        if (rot < 0f) rot += TwoPi;

        var intParams = new Dictionary<string, int>(StringComparer.Ordinal);
        string tail = line[(q4 + 1)..].Trim();
        if (tail.Length > 0)
        {
            foreach (string tok in tail.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = tok.IndexOf('=');
                if (eq <= 0 || eq >= tok.Length - 1) continue;
                string key = tok[..eq];
                if (int.TryParse(tok[(eq + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int val))
                    intParams[key] = val;
            }
        }

        return new ArmObject(rx + RoomOffsetX, ry + RoomOffsetY, rot, z, model, type, intParams);
    }
}
