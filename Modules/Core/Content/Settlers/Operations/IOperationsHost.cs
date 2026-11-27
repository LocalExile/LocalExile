using System.Collections.Concurrent;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Settlers.Operations;

/// <summary>
/// Minimal view of a hired Kingsmarch worker exposed to Operations services (Shipping crew, Atlas Runners, Disenchanters).
/// </summary>
public sealed record OperationsWorkerView(
    long WorkerId,
    int ListIndex,
    byte AssignedJobRow,
    int DisenchantRank,
    int MappingRank,
    int ShippingRank,
    bool IsAlive = true);

/// <summary>
/// Bridge between Operations (Shipping, Mapping, Rewards, Disenchanting, Recombinator) and Town / Boss subsystems.
/// Defaults to <see cref="StandaloneOperationsHost"/> backed directly by <see cref="VillageStore"/> so Operations compiles
/// and runs cleanly standalone on top of framework commit <c>49364de9f</c>, and can be replaced or augmented by
/// <c>@town-idle</c> and <c>@boss-arenas</c> during integration.
/// </summary>
public interface IOperationsHost
{
    SettlersSettings Settings { get; }

    /// <summary>Executes an exclusive, per-(Account, League) synchronized mutation against the town snapshot.</summary>
    T WithTownLock<T>(string account, string league, Func<T> action);

    /// <summary>Returns the current resource balance for <c>VillageResources</c> row <paramref name="resourceId"/>.</summary>
    uint GetResource(string account, string league, int resourceId);

    /// <summary>Atomically adds <paramref name="delta"/> (positive or negative) to a town resource, clamped to [0, uint.MaxValue].</summary>
    uint AddResource(string account, string league, int resourceId, long delta);

    /// <summary>Atomically deducts multiple <c>(ResourceId, Amount)</c> pairs if all are available; returns false if any is short.</summary>
    bool TrySpendResources(string account, string league, IReadOnlyList<(int ResourceId, uint Amount)> costs);

    /// <summary>Current gold in Kingsmarch treasury.</summary>
    uint GetTreasuryGold(string account, string league);

    /// <summary>Tries to spend gold from the player's account gold (or treasury fallback) and syncs <c>S→C 0x02aa</c>.</summary>
    Task<bool> TrySpendPlayerOrTreasuryGoldAsync(Player player, uint amount, string reason);

    /// <summary>Workers currently assigned to a specific <c>VillageJobs</c> row (17=Disenchanting, 18..20=Mapping, 21..23=Shipping).</summary>
    IReadOnlyList<OperationsWorkerView> GetWorkersForJob(string account, string league, byte villageJobRow);

    /// <summary>Removes/kills a hired worker (e.g. shipping crew casualty or mapper slain in Atlas) and syncs removal to the client.</summary>
    Task<bool> KillWorkerAsync(Player? player, string account, string league, long workerId, byte lostJobRow);

    /// <summary>Marks all workers assigned to <paramref name="villageJobRow"/> as lost (abandoned ambush) and removes them.</summary>
    Task<int> LoseAllWorkersOnJobAsync(Player? player, string account, string league, byte villageJobRow, byte lostJobRow);

    /// <summary>Persists and sends a full <c>S→C 0x02c9</c> subtag <c>0x04</c> state sync to the player.</summary>
    Task SyncFullVillageStateAsync(Player player);

    /// <summary>Opens a Sailor's Folly portal to Admiral Valerius to rescue a commandeered ship.</summary>
    Task<bool> OpenPirateBossPortalAsync(Player player, int shipIndex);

    /// <summary>Opens an Abandoned Port portal to Sasan, the Bandit Lord to rescue kidnapped mappers.</summary>
    Task<bool> OpenBanditBossPortalAsync(Player player, int deviceIndex);
}

/// <summary>
/// Standalone <see cref="IOperationsHost"/> backed directly by <see cref="VillageStore"/> (<c>Saves.Db</c>) and
/// pluggable boss-portal delegates so Operations works both standalone and when integrated with <c>@town-idle</c>/<c>@boss-arenas</c>.
/// </summary>
public sealed class StandaloneOperationsHost : IOperationsHost
{
    readonly ConcurrentDictionary<string, Lock> _locks = new(StringComparer.OrdinalIgnoreCase);

    public Func<SettlersSettings>? GetSettings { get; set; }
    public Func<Player, int, Task<bool>>? PirateBossPortalHandler { get; set; }
    public Func<Player, int, Task<bool>>? BanditBossPortalHandler { get; set; }
    public Func<Player, Task>? CustomStateSyncHandler { get; set; }

    public SettlersSettings Settings => GetSettings?.Invoke() ?? SettlersModule.Settings;

    Lock LockFor(string account, string league) => _locks.GetOrAdd($"{account}\0{league}", static _ => new Lock());

    public T WithTownLock<T>(string account, string league, Func<T> action)
    {
        lock (LockFor(account, league)) return action();
    }

    public static VillageStore.TownSnapshot LoadOrCreate(string account, string league)
    {
        if (VillageStore.Load(account, league) is { } existing) return existing;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var resources = new List<VillageStore.ResourceRow>(OperationsData.ResourceCount);
        for (int i = 0; i < OperationsData.ResourceCount; i++) resources.Add(new(i, 0, 0.0));
        var plots = new List<VillageStore.FarmPlotRow>(InstanceVillage.FarmPlotCount);
        for (int i = 0; i < InstanceVillage.FarmPlotCount; i++) plots.Add(new(i, InstanceVillage.EmptyFarmPlot, 0.0));
        var ships = new List<VillageStore.ShipRow>(OperationsData.ShipCount);
        for (int i = 0; i < OperationsData.ShipCount; i++) ships.Add(new(i, 0, 0, 0, 0, 0, 0, "[]", "[]", 0, 0, false));
        var devices = new List<VillageStore.MapDeviceRow>(OperationsData.MapDeviceCount);
        for (int i = 0; i < OperationsData.MapDeviceCount; i++) devices.Add(new(i, 0, -1, 0, 0, 0, 0, false, 0, "[]", "{}"));
        var town = new VillageStore.TownRow(account, league, 0, now, 0, 2000, 16000, Random.Shared.Next(1, int.MaxValue), 1, 1, 1, 0, "{}");
        var snap = new VillageStore.TownSnapshot(town, resources, plots, [], [], ships, [], devices, [], []);
        VillageStore.Save(snap);
        return snap;
    }

    public uint GetResource(string account, string league, int resourceId) => WithTownLock(account, league, () =>
    {
        var snap = VillageStore.Load(account, league);
        if (snap is null) return 0u;
        foreach (var r in snap.Resources)
            if (r.ResourceId == resourceId) return (uint)Math.Clamp(r.Amount, 0, uint.MaxValue);
        return 0u;
    });

    public uint AddResource(string account, string league, int resourceId, long delta) => WithTownLock(account, league, () =>
    {
        long next = VillageStore.AddResource(account, league, resourceId, delta);
        return (uint)Math.Clamp(next, 0, uint.MaxValue);
    });

    public bool TrySpendResources(string account, string league, IReadOnlyList<(int ResourceId, uint Amount)> costs) =>
        WithTownLock(account, league, () =>
        {
            var snap = LoadOrCreate(account, league);
            var map = new Dictionary<int, VillageStore.ResourceRow>();
            foreach (var r in snap.Resources) map[r.ResourceId] = r;

            var totals = new Dictionary<int, long>();
            foreach (var (rid, amt) in costs)
            {
                if (amt == 0) continue;
                totals[rid] = totals.GetValueOrDefault(rid) + amt;
            }
            foreach (var (rid, need) in totals)
            {
                long have = map.TryGetValue(rid, out var row) ? row.Amount : 0;
                if (have < need) return false;
            }
            foreach (var (rid, need) in totals)
            {
                var row = map[rid];
                map[rid] = row with { Amount = row.Amount - need };
            }
            VillageStore.Save(snap with { Resources = [.. map.Values.OrderBy(r => r.ResourceId)] });
            return true;
        });

    public uint GetTreasuryGold(string account, string league) => WithTownLock(account, league, () =>
    {
        var snap = VillageStore.Load(account, league);
        return snap is null ? 0u : (uint)Math.Clamp(snap.Town.TreasuryGold, 0, uint.MaxValue);
    });

    public async Task<bool> TrySpendPlayerOrTreasuryGoldAsync(Player player, uint amount, string reason)
    {
        if (amount == 0) return true;
        var stash = PersonalStashOf(player);
        if (stash.TrySpendGold(amount) is uint left)
        {
            await Send(player, InstanceEntities.Gold(left), $"0x02aa gold: {left} ({reason}, -{amount})");
            return true;
        }
        var (account, league) = VillageStore.ScopeOf(player);
        bool spentFromTreasury = WithTownLock(account, league, () =>
        {
            var snap = LoadOrCreate(account, league);
            if (snap.Town.TreasuryGold < amount) return false;
            VillageStore.Save(snap with { Town = snap.Town with { TreasuryGold = snap.Town.TreasuryGold - amount } });
            return true;
        });
        return spentFromTreasury;
    }

    public IReadOnlyList<OperationsWorkerView> GetWorkersForJob(string account, string league, byte villageJobRow) =>
        WithTownLock(account, league, () =>
        {
            var snap = VillageStore.Load(account, league);
            if (snap is null || snap.Workers.Count == 0) return [];
            var result = new List<OperationsWorkerView>();
            for (int i = 0; i < snap.Workers.Count; i++)
            {
                var w = snap.Workers[i];
                if (w.Status != 0) continue;
                if ((byte)w.AssignedJob != villageJobRow) continue;
                var (disRank, mapRank, shipRank) = ParseWorkerRanks(w.SkillsJson);
                result.Add(new OperationsWorkerView(w.WorkerId, i, (byte)w.AssignedJob, disRank, mapRank, shipRank, true));
            }
            return (IReadOnlyList<OperationsWorkerView>)result;
        });

    public async Task<bool> KillWorkerAsync(Player? player, string account, string league, long workerId, byte lostJobRow)
    {
        int removedIdx = WithTownLock(account, league, () =>
        {
            var snap = VillageStore.Load(account, league);
            if (snap is null) return -1;
            var list = snap.Workers.ToList();
            int idx = list.FindIndex(w => w.WorkerId == workerId);
            if (idx < 0) return -1;
            list.RemoveAt(idx);
            VillageStore.Save(snap with { Workers = list });
            return idx;
        });
        if (removedIdx < 0) return false;
        if (player is not null && removedIdx <= byte.MaxValue)
            await Send(player, InstanceVillage.RemoveWorker((byte)removedIdx), $"0x02c9 village RemoveWorker({removedIdx})");
        return true;
    }

    public async Task<int> LoseAllWorkersOnJobAsync(Player? player, string account, string league, byte villageJobRow, byte lostJobRow)
    {
        List<int> removedIndices = WithTownLock(account, league, () =>
        {
            var snap = VillageStore.Load(account, league);
            if (snap is null) return [];
            var list = snap.Workers.ToList();
            var indices = new List<int>();
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].Status == 0 && (byte)list[i].AssignedJob == villageJobRow)
                {
                    indices.Add(i);
                    list.RemoveAt(i);
                }
            }
            if (indices.Count > 0) VillageStore.Save(snap with { Workers = list });
            return indices;
        });
        if (player is not null)
        {
            foreach (int idx in removedIndices)
                if (idx <= byte.MaxValue)
                    await Send(player, InstanceVillage.RemoveWorker((byte)idx), $"0x02c9 village RemoveWorker({idx})");
        }
        return removedIndices.Count;
    }

    public async Task SyncFullVillageStateAsync(Player player)
    {
        if (CustomStateSyncHandler is { } custom)
        {
            await custom(player);
            return;
        }
        var (account, league) = VillageStore.ScopeOf(player);
        var snapshot = BuildFullStateSnapshot(account, league);
        await Send(player, InstanceVillage.FullVillageState(snapshot), "0x02c9 village FullVillageState");
    }

    public InstanceVillage.VillageStateSnapshot BuildFullStateSnapshot(string account, string league) =>
        WithTownLock(account, league, () =>
        {
            var snap = LoadOrCreate(account, league);
            ulong now = (ulong)Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            var resources = new uint[OperationsData.ResourceCount];
            foreach (var r in snap.Resources)
                if (r.ResourceId >= 0 && r.ResourceId < resources.Length)
                    resources[r.ResourceId] = (uint)Math.Clamp(r.Amount, 0, uint.MaxValue);

            var prodProgress = new uint[InstanceVillage.ProductionCount];
            foreach (var r in snap.Resources)
                if (r.ResourceId >= 0 && r.ResourceId < prodProgress.Length)
                    prodProgress[r.ResourceId] = (uint)Math.Clamp((long)Math.Round(r.FractionalProgress), 0, uint.MaxValue);

            var farmPlots = new byte[InstanceVillage.FarmPlotCount];
            Array.Fill(farmPlots, InstanceVillage.EmptyFarmPlot);
            foreach (var fp in snap.FarmPlots)
                if (fp.PlotIndex >= 0 && fp.PlotIndex < farmPlots.Length)
                    farmPlots[fp.PlotIndex] = (byte)Math.Clamp(fp.CropResourceId, 0, InstanceVillage.EmptyFarmPlot);

            var shipTimers = VillageShippingService.BuildShipTimers(snap, now);
            var (shipKidnapSlot, shipKidnapFlag) = VillageShippingService.GetShipKidnapState(snap);
            var mapDevices = VillageMappingService.BuildMapDeviceStates(snap, now);
            var (mapKidnapSlot, mapKidnapFlag) = VillageMappingService.GetMapKidnapState(snap);
            var disenchantTimer = VillageDisenchantingService.BuildDisenchantTimer(snap);

            return new InstanceVillage.VillageStateSnapshot(
                resources,
                prodProgress,
                (uint)Math.Clamp(snap.Town.TreasuryGold, 0, uint.MaxValue),
                0u,
                farmPlots,
                shipTimers,
                mapDevices,
                disenchantTimer,
                0,
                0,
                (ulong)Math.Max(0, snap.Town.LastSimulatedUtc > 0 ? snap.Town.LastSimulatedUtc : (long)now),
                shipKidnapSlot,
                shipKidnapFlag,
                mapKidnapSlot,
                mapKidnapFlag,
                now);
        });

    public async Task<bool> OpenPirateBossPortalAsync(Player player, int shipIndex)
    {
        if (PirateBossPortalHandler is { } handler) return await handler(player, shipIndex);
        if (player.Commands is { } cmds)
            await cmds.Reply("Admiral Valerius awaits in Sailor's Folly (boss arena portal handler not yet attached).");
        return false;
    }

    public async Task<bool> OpenBanditBossPortalAsync(Player player, int deviceIndex)
    {
        if (BanditBossPortalHandler is { } handler) return await handler(player, deviceIndex);
        if (player.Commands is { } cmds)
            await cmds.Reply("Sasan, the Bandit Lord awaits in Abandoned Port (boss arena portal handler not yet attached).");
        return false;
    }

    static (int DisenchantRank, int MappingRank, int ShippingRank) ParseWorkerRanks(string skillsJson)
    {
        int dis = 0, map = 0, ship = 0;
        if (string.IsNullOrWhiteSpace(skillsJson)) return (1, 1, 1);
        try
        {
            using var doc = JsonDocument.Parse(skillsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    int jobType = el.TryGetProperty("jobType", out var jt) ? jt.GetInt32()
                        : el.TryGetProperty("JobTypeRow", out var jtr) ? jtr.GetInt32()
                        : el.TryGetProperty("JobType", out var jte) ? jte.GetInt32() : -1;
                    int rank = el.TryGetProperty("rank", out var rk) ? rk.GetInt32()
                        : el.TryGetProperty("Rank", out var rke) ? rke.GetInt32() : 0;
                    if (jobType == OperationsData.JobTypeDisenchanting) dis = Math.Max(dis, rank);
                    else if (jobType == OperationsData.JobTypeMapping) map = Math.Max(map, rank);
                    else if (jobType == OperationsData.JobTypeShipping) ship = Math.Max(ship, rank);
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (!prop.Value.TryGetInt32(out int rank)) continue;
                    if (prop.Name is "4" or "Disenchanting") dis = Math.Max(dis, rank);
                    else if (prop.Name is "5" or "Mapping") map = Math.Max(map, rank);
                    else if (prop.Name is "6" or "Shipping") ship = Math.Max(ship, rank);
                }
            }
        }
        catch { }
        return (dis, map, ship);
    }
}

/// <summary>
/// Single clean entry point for wiring all 5 Operations services (Shipping, Mapping, Rewards, Disenchanting, Recombinator)
/// into <see cref="SettlersModule"/>.
/// </summary>
public static class OperationsSubsystem
{
    public static StandaloneOperationsHost DefaultHost { get; } = new();
    public static IOperationsHost Host { get; private set; } = DefaultHost;

    public static VillageShippingService Shipping { get; private set; } = new(DefaultHost);
    public static VillageMappingService Mapping { get; private set; } = new(DefaultHost);
    public static VillageRewardsService Rewards { get; private set; } = new(DefaultHost);
    public static VillageDisenchantingService Disenchanting { get; private set; } = new(DefaultHost);
    public static RecombinatorService Recombinator { get; private set; } = new(DefaultHost);

    /// <summary>Replaces the host bridge (e.g. when <c>@town-idle</c> supplies its authoritative state manager).</summary>
    public static void SetHost(IOperationsHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        Host = host;
        Shipping = new VillageShippingService(host);
        Mapping = new VillageMappingService(host);
        Rewards = new VillageRewardsService(host);
        Disenchanting = new VillageDisenchantingService(host);
        Recombinator = new RecombinatorService(host);
    }

    /// <summary>Registers boss-arena portal openers on <see cref="DefaultHost"/> (for <c>@boss-arenas</c>).</summary>
    public static void RegisterBossPortals(
        Func<Player, int, Task<bool>>? openPiratePortal,
        Func<Player, int, Task<bool>>? openBanditPortal)
    {
        DefaultHost.PirateBossPortalHandler = openPiratePortal;
        DefaultHost.BanditBossPortalHandler = openBanditPortal;
    }

    /// <summary>Callback for <c>@boss-arenas</c> when Admiral Valerius is defeated in Sailor's Folly.</summary>
    public static Task OnPirateBossDefeatedAsync(Player player) => Shipping.ResolvePirateRescueAsync(player);

    /// <summary>Callback for <c>@boss-arenas</c> when Sasan, the Bandit Lord is defeated in Abandoned Port.</summary>
    public static Task OnBanditBossDefeatedAsync(Player player) => Mapping.ResolveBanditRescueAsync(player);

    public static void LoadGameData() => OperationsData.LoadGameData();

    public static void Register(IModuleContext module)
    {
        string id = module.Manifest.Id;
        LoadGameData();

        // Dispatch Operations C→S 0x02ca subtags for Ruleset 0x9b (returns false for Town/Worker subtags 0x00..0x05, 0x18, 0x19).
        GameEvents.RulesetAction(InstanceVillage.RulesetRow).Add(id, HandleRulesetCommandAsync);

        // Recombinator C→S 0x010b (quick-move) and 0x028f (preview / execute).
        GameEvents.Packet(InstanceVillage.RecombinatorQuickMoveId).Add(id, Recombinator.HandleQuickMoveAsync);
        GameEvents.Packet(InstanceVillage.RecombinateId).Add(id, Recombinator.HandleRecombinateAsync);

        // Inventory placement validation and post-change notifications.
        GameEvents.InventoryRefusal.Add(id, ValidateInventoryRefusal);
        GameEvents.InventoryChanged.Add(id, OnInventoryChangedAsync);

        // Area enter / leave lifecycle: sync ships, quotas, reward badges, and catch-up.
        GameEvents.PlayerEntered.Add(id, OnPlayerEnteredAsync);
        GameEvents.PlayerLeaving.Add(id, (player, _) =>
        {
            Rewards.ClearSession(player);
            return Task.CompletedTask;
        });

        // 1-second simulation tick for active voyages, map runners, and Rog disenchanting.
        GameEvents.AreaTick.Add(id, _ =>
        {
            double accum = 0;
            return async (player, dt) =>
            {
                accum += dt;
                if (accum < 1.0) return;
                double step = accum;
                accum = 0;
                await TickOperationsAsync(player, step);
            };
        });
    }

    static async Task<bool> HandleRulesetCommandAsync(Player player, ReadOnlyMemory<byte> payload)
    {
        if (!InstanceVillage.TryReadCommand(payload.Span, out var cmd) || cmd is null) return false;
        return cmd switch
        {
            InstanceVillage.SendShipmentCommand send => await Shipping.HandleSendShipmentAsync(player, send),
            InstanceVillage.AbandonOrCancelShipmentCommand cancel => await Shipping.HandleCancelShipmentAsync(player, cancel),
            InstanceVillage.RescueCommandeeredShipCommand => await Shipping.HandleRescueShipAsync(player),
            InstanceVillage.PayShipRansomCommand => await Shipping.HandlePayShipRansomAsync(player),
            InstanceVillage.AbandonCommandeeredShipCommand => await Shipping.HandleAbandonShipAsync(player),
            InstanceVillage.RerollPortQuotaCommand reroll => await Shipping.HandleRerollPortQuotaAsync(player, reroll),

            InstanceVillage.StartMappingCommand start => await Mapping.HandleStartMappingAsync(player, start),
            InstanceVillage.StopMappingImmediateCommand stop => await Mapping.HandleStopMappingImmediateAsync(player, stop),
            InstanceVillage.SetMappingDelayedStopCommand pause => await Mapping.HandleSetDelayedStopAsync(player, pause),
            InstanceVillage.RescueKidnappedMappersCommand => await Mapping.HandleRescueMappersAsync(player),
            InstanceVillage.PayMapperRansomCommand => await Mapping.HandlePayMapperRansomAsync(player),
            InstanceVillage.AbandonKidnappedMappersCommand => await Mapping.HandleAbandonMappersAsync(player),
            InstanceVillage.AckFailedMapRunCommand ack => await Mapping.HandleAckFailedMapRunAsync(player, ack),

            InstanceVillage.OpenRewardInventoryCommand open => await Rewards.HandleOpenRewardInventoryAsync(player, open),
            InstanceVillage.TakeRewardCtrlClickCommand ctrl => await Rewards.HandleTakeRewardCtrlClickAsync(player, ctrl),
            InstanceVillage.TakeRewardToSlotCommand toSlot => await Rewards.HandleTakeRewardToSlotAsync(player, toSlot),
            InstanceVillage.TakeRewardAndSocketCommand socket => await Rewards.HandleTakeRewardAndSocketAsync(player, socket),
            InstanceVillage.TakeAllRewardsCommand takeAll => await Rewards.HandleTakeAllRewardsAsync(player, takeAll),
            InstanceVillage.DiscardAllRewardsCommand => await Rewards.HandleDiscardAllRewardsAsync(player),
            InstanceVillage.HarbourNpcNotifAckCommand ack => await Rewards.HandleHarbourNotifAckAsync(player, ack),
            InstanceVillage.MappingNpcNotifAckCommand ack => await Rewards.HandleMappingNotifAckAsync(player, ack),

            _ => false,
        };
    }

    static string? ValidateInventoryRefusal(Player player, byte row, Item item) => row switch
    {
        PlayerInventories.KalguurDisenchanting => Disenchanting.ValidatePlacement(player, item),
        PlayerInventories.KalguurMapping => Mapping.ValidatePlacement(player, item),
        PlayerInventories.RecombinatorInput => Recombinator.ValidateInputPlacement(player, item),
        PlayerInventories.RecombinatorOutput => "cannot place items into the Recombinator output slot",
        _ => null,
    };

    static async Task OnInventoryChangedAsync(Player player, byte row)
    {
        if (row == PlayerInventories.KalguurDisenchanting)
            await Disenchanting.OnInventoryChangedAsync(player);
        else if (row == PlayerInventories.KalguurMapping)
            await Mapping.OnInventoryChangedAsync(player);
        else if (row == PlayerInventories.RecombinatorInput)
            await Recombinator.OnInputInventoryChangedAsync(player);
    }

    static async Task OnPlayerEnteredAsync(Player player)
    {
        // Only perform village wire syncs in areas where VillageRules (0x9b) is relevant (Kingsmarch, Karui Shores, Hideouts).
        if (!IsVillageArea(player)) return;
        await Shipping.SyncAllShipsAndQuotasAsync(player);
        await Mapping.SyncCurrentMapNamesAsync(player);
        await Rewards.SyncRewardVisibilityAsync(player);
    }

    /// <summary>Advances Shipping, Mapping, and Disenchanting by <paramref name="elapsedSeconds"/> (online tick or offline catch-up).</summary>
    public static async Task TickOperationsAsync(Player player, double elapsedSeconds)
    {
        if (elapsedSeconds <= 0) return;
        bool changed = false;
        changed |= await Shipping.TickAsync(player, elapsedSeconds);
        changed |= await Mapping.TickAsync(player, elapsedSeconds);
        changed |= await Disenchanting.TickAsync(player, elapsedSeconds);
        if (changed && IsVillageArea(player))
            await Host.SyncFullVillageStateAsync(player);
    }

    public static bool IsVillageArea(Player player)
    {
        string id = player.Layout.Id;
        return id.Equals("KalguuranSettlersLeague", StringComparison.OrdinalIgnoreCase)
            || id.Equals("2_11_endgame_town", StringComparison.OrdinalIgnoreCase)
            || HideoutCatalog.ByArea(player.Layout.Hash) is not null;
    }
}
