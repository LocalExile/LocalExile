using System.Collections.Concurrent;
using GameServer.Core.Protocol.Instance;
using GameServer.Storage;
using G = global::Game;

namespace GameServer.Modules.Settlers.Town;

/// <summary>
/// League+account-scoped authoritative manager for Kingsmarch state, single-clock simulation + offline catch-up,
/// SQLite persistence via <see cref="VillageStore"/>, and full/incremental wire sync via <see cref="InstanceVillage"/>.
/// Sibling Settlers subsystems (<c>Operations</c>, <c>MapOres</c>, <c>BossArenas</c>) interact with town state exclusively
/// through this service so there is never split-brain state or unlocked mutation.
/// </summary>
public static class VillageTownService
{
    /// <summary>Upper bound on a single offline catch-up window (30 days) to prevent runaway jumps on skewed clocks.</summary>
    const long MaxOfflineCatchupSeconds = 30L * 24 * 3600;
    /// <summary>When <see cref="SettlersSettings.OfflineCatchupEnabled"/> is false, cap each step to live-tick granularity.</summary>
    const long MaxNoOfflineStepSeconds = 15L;

    static readonly ConcurrentDictionary<TownScope, VillageTownState> States = new();
    static readonly ConcurrentDictionary<Player, byte> OnlinePlayers = new();

    /// <summary>Optional external simulation callback invoked inside the state lock with <c>(state, productiveSeconds, nowUtc)</c>.
    /// Return <c>true</c> only when the handler actually mutated state so idle ticks avoid redundant SQLite writes and <c>0x04</c> syncs.</summary>
    public static event Func<VillageTownState, double, long, bool>? OnSimulateTick;

    /// <summary>Optional async notification fired after a full/incremental state sync for a player (e.g. Kingsmarch visual updates).</summary>
    public static event Func<Player, VillageTownState, Task>? OnStateSynced;

    /// <summary>Optional time source override for deterministic tests; defaults to <see cref="DateTimeOffset.UtcNow"/>.</summary>
    public static Func<long> ClockUtcSeconds { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Clears cached state and online player tracking on module unload / reload.</summary>
    public static void Reset()
    {
        States.Clear();
        OnlinePlayers.Clear();
        OnSimulateTick = null;
        OnStateSynced = null;
    }

    public static void TrackPlayer(Player player) => OnlinePlayers[player] = 0;
    public static void UntrackPlayer(Player player) => OnlinePlayers.TryRemove(player, out _);

    public static TownScope ScopeOf(Player player)
    {
        var (account, league) = VillageStore.ScopeOf(player);
        return TownScope.Of(account, league);
    }

    /// <summary>Returns the authoritative <see cref="VillageTownState"/> for <paramref name="player"/>, loading from
    /// <see cref="VillageStore"/> or initializing a fresh 3.29 Kingsmarch settlement on first access.</summary>
    public static VillageTownState GetState(Player player)
    {
        var (account, league) = VillageStore.ScopeOf(player);
        return GetState(account, league);
    }

    public static VillageTownState GetState(string account, string league, SaveDatabase? db = null)
    {
        var scope = TownScope.Of(account, league);
        return States.GetOrAdd(scope, _ => LoadOrCreate(account, league, db));
    }

    static VillageTownState LoadOrCreate(string account, string league, SaveDatabase? db)
    {
        var snapshot = db is null ? VillageStore.Load(account, league) : VillageStore.Load(db, account, league);
        if (snapshot is not null)
        {
            var loaded = VillageTownState.FromStoreSnapshot(snapshot);
            if (loaded.Recruits.Count == 0)
            {
                VillageWorkersAndEconomy.PopulateRecruitsLocked(loaded, isSuper: false, SettlersModule.Settings, Random.Shared);
                SaveLocked(loaded, db);
            }
            return loaded;
        }

        long now = ClockUtcSeconds();
        var fresh = new VillageTownState(account, league)
        {
            LastSimulatedUtc = now,
            LastRerollUtc = now,
            RerollCostGold = VillageData.RerollCost(false),
            SuperRerollCostGold = VillageData.RerollCost(true),
            ShippingSeed = Random.Shared.NextInt64(1, long.MaxValue),
        };
        VillageWorkersAndEconomy.PopulateRecruitsLocked(fresh, isSuper: false, SettlersModule.Settings, Random.Shared);
        SeedDefaultPortQuotasLocked(fresh, Random.Shared);
        SaveLocked(fresh, db);
        return fresh;
    }

    /// <summary>Generates initial 3.29 favored port quotas (3 slots per destination port 1..6) for a brand-new town.</summary>
    public static void SeedDefaultPortQuotasLocked(VillageTownState state, Random rng)
    {
        Span<byte> chosen = stackalloc byte[VillageData.QuotasPerPort];
        for (int p = 0; p < VillageData.PortCount; p++)
        {
            // Pick 3 distinct non-dust exports (0..14: ores 0..4, bars 5..9, crops 10..14).
            int count = 0;
            while (count < VillageData.QuotasPerPort)
            {
                byte exp = (byte)rng.Next(0, VillageData.ExportCount - 1); // 0..14 (exclude MagicDust=15)
                bool dup = false;
                for (int k = 0; k < count; k++) if (chosen[k] == exp) { dup = true; break; }
                if (dup) continue;
                chosen[count++] = exp;
            }
            for (int q = 0; q < VillageData.QuotasPerPort; q++)
            {
                byte exp = chosen[q];
                uint required = exp switch
                {
                    < 5 => (uint)(rng.Next(4, 15) * 100),    // Ores
                    < 10 => (uint)(rng.Next(2, 10) * 100),   // Bars
                    _ => (uint)(rng.Next(5, 20) * 100),      // Crops
                };
                state.PortQuotas[p][q].ExportRow = exp;
                state.PortQuotas[p][q].RequiredAmount = required;
                state.PortQuotas[p][q].FulfilledAmount = 0;
                state.PortQuotas[p][q].BonusPercent = 30;
            }
        }
    }

    /// <summary>Persists <paramref name="state"/> atomically to SQLite. Caller must hold <see cref="VillageTownState.Gate"/>.</summary>
    public static void SaveLocked(VillageTownState state, SaveDatabase? db = null)
    {
        var snap = state.ToStoreSnapshot();
        if (db is null) VillageStore.Save(snap);
        else VillageStore.Save(db, snap);
        state.Dirty = false;
        state.LastSavedUtc = ClockUtcSeconds();
    }

    /// <summary>Acquires <see cref="VillageTownState.Gate"/> and persists to SQLite.</summary>
    public static void Save(VillageTownState state, SaveDatabase? db = null)
    {
        lock (state.Gate) SaveLocked(state, db);
    }

    /// <summary>
    /// Advances the single authoritative simulation clock for <paramref name="state"/> up to <paramref name="nowUtc"/>.
    /// Caller must hold <see cref="VillageTownState.Gate"/>. Returns true if any state changed.
    /// </summary>
    public static bool SimulateLocked(VillageTownState state, long nowUtc, SettlersSettings? settingsOverride = null)
    {
        var settings = settingsOverride ?? SettlersModule.Settings;
        if (state.LastSimulatedUtc <= 0)
        {
            state.LastSimulatedUtc = nowUtc;
            return true;
        }

        long rawElapsed = nowUtc - state.LastSimulatedUtc;
        if (rawElapsed <= 0) return false;

        long dt = settings.OfflineCatchupEnabled
            ? Math.Min(rawElapsed, MaxOfflineCatchupSeconds)
            : Math.Min(rawElapsed, MaxNoOfflineStepSeconds);

        // Always advance the authoritative clock to nowUtc so we never double-simulate the same seconds.
        state.LastSimulatedUtc = nowUtc;

        double burnMult = Math.Max(0.0, settings.GoldBurnRateMultiplier);
        double remainingDt = dt;
        double totalProductiveSeconds = 0.0;
        bool changed = false;

        // Event-stepped simulation (mirrors Game::Village::Simulate @ 0x1424c5250):
        // Step to whichever happens first: end of interval, treasury hits 0, or a Mining/Smelting input depletes,
        // so workers whose ore runs out stop burning treasury wages cleanly mid-offline-catchup.
        const int MaxSteps = 64;
        for (int step = 0; step < MaxSteps && remainingDt > 1e-6; step++)
        {
            long activeWageSum = ComputeActiveWageSumLocked(state);
            double effectiveWagePerHour = activeWageSum * burnMult;

            double maxAffordableDt = remainingDt;
            bool treasuryRunsOut = false;
            if (effectiveWagePerHour > 0.0)
            {
                if (state.TreasuryGold == 0)
                {
                    state.GoldBurnRemainder = 0;
                    break;
                }
                decimal totalUnitsAvailable = (decimal)state.TreasuryGold * 3600m - state.GoldBurnRemainder;
                if (totalUnitsAvailable <= 0m)
                {
                    state.TreasuryGold = 0;
                    state.GoldBurnRemainder = 0;
                    changed = true;
                    break;
                }
                decimal unitsNeeded = (decimal)effectiveWagePerHour * (decimal)remainingDt;
                if (unitsNeeded > totalUnitsAvailable)
                {
                    maxAffordableDt = (double)(totalUnitsAvailable / (decimal)effectiveWagePerHour);
                    treasuryRunsOut = true;
                }
            }

            double nextDepleteDt = EarliestInputDepletionSecondsLocked(state, settings);
            double stepDt = Math.Min(maxAffordableDt, nextDepleteDt);
            if (stepDt <= 1e-6)
            {
                if (treasuryRunsOut)
                {
                    state.TreasuryGold = 0;
                    state.GoldBurnRemainder = 0;
                    changed = true;
                }
                break;
            }

            if (effectiveWagePerHour > 0.0)
            {
                if (treasuryRunsOut && stepDt >= maxAffordableDt - 1e-6)
                {
                    state.TreasuryGold = 0;
                    state.GoldBurnRemainder = 0;
                    changed = true;
                }
                else
                {
                    decimal accum = (decimal)effectiveWagePerHour * (decimal)stepDt + state.GoldBurnRemainder;
                    long wholeUnits = (long)Math.Floor(accum);
                    long goldConsumed = wholeUnits / 3600L;
                    uint newRemainder = (uint)(wholeUnits % 3600L);
                    state.TreasuryGold = goldConsumed >= state.TreasuryGold ? 0u : state.TreasuryGold - (uint)goldConsumed;
                    state.GoldBurnRemainder = state.TreasuryGold == 0 ? 0u : newRemainder;
                    changed = true;
                }
            }

            if (VillageWorkersAndEconomy.SimulateProductionLocked(state, stepDt, settings))
                changed = true;

            totalProductiveSeconds += stepDt;
            remainingDt -= stepDt;

            if (treasuryRunsOut && state.TreasuryGold == 0)
                break;
        }

        // Notify sibling operations (shipping, mapping, disenchanting) on the same authoritative clock & cutoff.
        if (OnSimulateTick is { } tickHandlers)
        {
            foreach (Func<VillageTownState, double, long, bool> handler in Delegate.EnumerateInvocationList(tickHandlers))
            {
                try
                {
                    if (handler(state, totalProductiveSeconds, nowUtc))
                        changed = true;
                }
                catch (Exception ex) { Console.WriteLine($"[settlers] OnSimulateTick handler failed: {ex.Message}"); }
            }
        }

        return changed;
    }

    static double EarliestInputDepletionSecondsLocked(VillageTownState state, SettlersSettings settings)
    {
        double miningMult = Math.Max(0.0, settings.MiningSpeedMultiplier);
        double smeltingMult = Math.Max(0.0, settings.SmeltingSpeedMultiplier);
        double earliest = double.PositiveInfinity;

        var prods = VillageData.Productions;
        for (int i = 0; i < prods.Count; i++)
        {
            var prod = prods[i];
            if (prod.Row >= VillageData.ProductionCount) continue;
            if (prod.InputResource is not int inRes || prod.JobRow is not byte jobRow) continue;
            uint stock = state.Resources[inRes];
            if (stock == 0) continue;

            byte jobType = jobRow < VillageData.Jobs.Count ? VillageData.Jobs[jobRow].JobType : VillageData.JobTypeIdling;
            double mult = jobType == VillageData.JobTypeMining ? miningMult
                : jobType == VillageData.JobTypeSmelting ? smeltingMult
                : 1.0;
            if (mult <= 0.0) continue;

            long baseSpeed = VillageWorkersAndEconomy.JobSpeedLocked(state, jobRow, jobType);
            long effectiveSpeed = (long)Math.Round(baseSpeed * mult);
            if (effectiveSpeed <= 0) continue;

            long costPerItem = (long)prod.BaseProductionTime * 100L;
            decimal workRemaining = (decimal)stock * costPerItem - state.ProductionProgress[prod.Row];
            if (workRemaining <= 0m) return 0.001;
            double sec = (double)(workRemaining / effectiveSpeed);
            if (sec < earliest) earliest = sec;
        }
        return earliest;
    }

    /// <summary>
    /// Computes the hourly wage sum of workers actively working right now (matching <c>0x1424c382f</c>).
    /// Idle workers, kidnapped/lost workers, and workers whose station has no pending input/crop/voyage/map do not burn gold.
    /// </summary>
    public static long ComputeActiveWageSumLocked(VillageTownState state)
    {
        long sum = 0;
        for (int i = 0; i < state.Workers.Count; i++)
        {
            var w = state.Workers[i];
            if (IsWorkerActivelyConsumingWagesLocked(state, w))
                sum += w.WagePerHour;
        }
        return sum;
    }

    static bool IsWorkerActivelyConsumingWagesLocked(VillageTownState state, TownWorker worker)
    {
        if (worker.Status != 0) return false;
        byte job = worker.AssignedJobRow;
        if (job == VillageData.JobIdling || job == InstanceVillage.NoneRow || job >= VillageData.Jobs.Count)
            return false;

        var info = VillageData.Jobs[job];
        return info.JobType switch
        {
            VillageData.JobTypeMining => info.SubIndex < 5 && state.Resources[VillageData.ResPendingIron + info.SubIndex] > 0,
            VillageData.JobTypeSmelting => info.SubIndex < 5 && state.Resources[VillageData.ResIronOre + info.SubIndex] > 0,
            VillageData.JobTypeFarming => info.SubIndex < VillageData.FarmPlotCount
                && state.FarmPlotCropResource[info.SubIndex] is >= VillageData.ResWheat and <= VillageData.ResOrbisfera,
            VillageData.JobTypeDisenchanting => state.DisenchantQueue.Count > 0 || state.DisenchantTotal > 0,
            VillageData.JobTypeMapping => info.SubIndex < VillageData.MapDeviceCount && state.MapDevices[info.SubIndex].IsRunning,
            VillageData.JobTypeShipping => info.SubIndex < VillageData.ShipCount && state.Ships[info.SubIndex].IsAtSea,
            VillageData.JobTypeFishing => true,
            _ => false,
        };
    }

    // ---- Cross-subsystem mutation helpers (for @shipping-mapping, @map-ores, @boss-arenas) --------------------------

    /// <summary>
    /// Atomically adds (or subtracts) a resource in <c>VillageResources</c> (<c>0..20</c>), updating both the cached
    /// authoritative state and SQLite <see cref="VillageStore"/>, then syncing any online players in that scope.
    /// </summary>
    public static uint AddResource(string account, string league, int resourceId, long delta, SaveDatabase? db = null)
    {
        if (resourceId is < 0 or >= VillageData.ResourceCount) return 0;
        var state = GetState(account, league, db);
        uint updated;
        lock (state.Gate)
        {
            long next = (long)state.Resources[resourceId] + delta;
            updated = state.Resources[resourceId] = (uint)Math.Clamp(next, 0L, uint.MaxValue);
            SaveLocked(state, db);
        }
        return updated;
    }

    public static async Task<uint> AddResourceAsync(Player player, int resourceId, long delta)
    {
        var (account, league) = VillageStore.ScopeOf(player);
        uint updated = AddResource(account, league, resourceId, delta);
        await SyncStateAsync(player);
        return updated;
    }

    /// <summary>Atomically spends <paramref name="amount"/> gold from the player's account gold (sending S→C <c>0x02aa</c>).</summary>
    public static async Task<bool> TrySpendGoldAsync(Player player, uint amount, string reason)
    {
        if (amount == 0) return true;
        var stash = G.PersonalStashOf(player);
        if (stash.TrySpendGold(amount) is not uint after) return false;
        await Program.Send(player, InstanceEntities.Gold(after), $"0x02aa {reason} (-{amount}g, now {after}g)");
        return true;
    }

    /// <summary>Atomically deducts <paramref name="amount"/> from the town treasury under lock. Caller must hold <see cref="VillageTownState.Gate"/>.</summary>
    public static bool TrySpendTreasuryGoldLocked(VillageTownState state, uint amount)
    {
        if (amount == 0) return true;
        if (state.TreasuryGold < amount) return false;
        state.TreasuryGold -= amount;
        if (state.TreasuryGold == 0) state.GoldBurnRemainder = 0;
        return true;
    }

    /// <summary>Atomically deducts multiple resource costs under lock, or deducts nothing and returns false.</summary>
    public static bool TrySpendResourcesLocked(VillageTownState state, IReadOnlyDictionary<int, uint> costs)
    {
        foreach (var (resId, amount) in costs)
        {
            if (amount == 0) continue;
            if (resId is < 0 or >= VillageData.ResourceCount || state.Resources[resId] < amount)
                return false;
        }
        foreach (var (resId, amount) in costs)
            if (amount > 0) state.Resources[resId] -= amount;
        return true;
    }

    /// <summary>Runs a state mutation under lock, persists if it returned true, and syncs to the player.</summary>
    public static async Task<bool> SaveAndSyncAsync(
        Player player,
        Func<VillageTownState, bool> mutate,
        bool syncWorkers = false,
        bool syncRecruits = false,
        bool syncShips = false,
        bool syncQuotas = false)
    {
        var state = GetState(player);
        long now = ClockUtcSeconds();
        bool changed;
        lock (state.Gate)
        {
            SimulateLocked(state, now);
            changed = mutate(state);
            if (changed) SaveLocked(state);
        }
        if (!changed) return false;

        if (syncWorkers) await SyncWorkersAsync(player, state);
        if (syncRecruits) await SyncRecruitsAsync(player, state);
        if (syncShips) await SyncShipsAsync(player, state);
        if (syncQuotas) await SyncQuotasAsync(player, state);
        await SyncStateAsync(player, state);
        return true;
    }

    /// <summary>Runs one simulation tick for <paramref name="player"/> and pushes updated state if anything changed.
    /// Keeps tick progress in memory and avoids 1 Hz full-table SQLite rewrites; state flushes on mutations, area leave, or periodic 60s checkpoint.</summary>
    public static async Task TickPlayerAsync(Player player)
    {
        var state = GetState(player);
        long now = ClockUtcSeconds();
        bool changed;
        lock (state.Gate)
        {
            changed = SimulateLocked(state, now);
            if (changed)
            {
                state.Dirty = true;
                if (now - state.LastSavedUtc >= 60)
                    SaveLocked(state);
            }
        }
        if (changed) await SyncStateAsync(player, state);
    }

    // ---- Wire sync helpers (S→C 0x02c9 subtags 0x00..0x0c) -----------------------------------------------------------

    /// <summary>Sends the full initial Kingsmarch synchronization sequence on area entry (workers, recruits, ships, quotas, full state).</summary>
    public static async Task SyncFullAsync(Player player)
    {
        var state = GetState(player);
        long now = ClockUtcSeconds();
        byte[] workerPkt, recruitPkt, statePkt;
        byte[][] shipPkts = new byte[VillageData.ShipCount][];
        byte[][] quotaPkts = new byte[VillageData.PortCount][];
        byte[]?[] mapNamePkts = new byte[VillageData.MapDeviceCount][];
        byte rewardNotice;

        lock (state.Gate)
        {
            if (SimulateLocked(state, now)) SaveLocked(state);
            workerPkt = InstanceVillage.FullWorkerList(state.ToWireWorkers());
            recruitPkt = InstanceVillage.RecruitPool(state.RecruitPoolFlag, state.ToWireRecruits());
            for (int i = 0; i < VillageData.ShipCount; i++)
            {
                var s = state.Ships[i];
                byte portRow = s.State == 0 ? InstanceVillage.NoneRow : s.PortRow;
                shipPkts[i] = InstanceVillage.ShipUpdate((byte)i, portRow, s.FailureRow, s.CargoExports);
            }
            for (int p = 0; p < VillageData.PortCount; p++)
            {
                var slots = new InstanceVillage.PortQuotaSlot[VillageData.QuotasPerPort];
                for (int q = 0; q < VillageData.QuotasPerPort; q++)
                    slots[q] = state.PortQuotas[p][q].ToWire();
                quotaPkts[p] = InstanceVillage.PortQuotas((byte)p, slots);
            }
            for (int d = 0; d < VillageData.MapDeviceCount; d++)
            {
                string mapName = state.MapDevices[d].CurrentMapName;
                if (!string.IsNullOrEmpty(mapName))
                    mapNamePkts[d] = InstanceVillage.MapDeviceCurrentMapName((byte)d, mapName);
            }
            statePkt = InstanceVillage.FullVillageState(state.ToWireSnapshot((ulong)Math.Max(0, now)));
            rewardNotice = state.ActiveRewardNotice;
        }

        await Program.Send(player, workerPkt, "0x02c9[0x00] village full worker list");
        await Program.Send(player, recruitPkt, "0x02c9[0x01] village recruit pool");
        for (int i = 0; i < VillageData.ShipCount; i++)
            await Program.Send(player, shipPkts[i], $"0x02c9[0x02] village ship {i} state");
        for (int p = 0; p < VillageData.PortCount; p++)
            await Program.Send(player, quotaPkts[p], $"0x02c9[0x03] village port {p + 1} quotas");
        await Program.Send(player, statePkt, "0x02c9[0x04] full village state");
        for (int d = 0; d < VillageData.MapDeviceCount; d++)
            if (mapNamePkts[d] is { } pkt)
                await Program.Send(player, pkt, $"0x02c9[0x0d] map device {d} current map name");
        if (rewardNotice > 0)
            await Program.Send(player, InstanceVillage.ActiveRewardNotice(rewardNotice), $"0x02c9[0x08] active reward notice {rewardNotice}");

        await NotifyStateSyncedAsync(player, state);
    }

    public static async Task SyncStateAsync(Player player, VillageTownState? existing = null)
    {
        var state = existing ?? GetState(player);
        long now = ClockUtcSeconds();
        byte[] statePkt;
        lock (state.Gate)
            statePkt = InstanceVillage.FullVillageState(state.ToWireSnapshot((ulong)Math.Max(0, now)));
        await Program.Send(player, statePkt, "0x02c9[0x04] full village state");
        await NotifyStateSyncedAsync(player, state);
    }

    public static async Task SyncWorkersAsync(Player player, VillageTownState? existing = null)
    {
        var state = existing ?? GetState(player);
        byte[] pkt;
        lock (state.Gate) pkt = InstanceVillage.FullWorkerList(state.ToWireWorkers());
        await Program.Send(player, pkt, "0x02c9[0x00] village full worker list");
    }

    public static async Task SyncRecruitsAsync(Player player, VillageTownState? existing = null)
    {
        var state = existing ?? GetState(player);
        byte[] pkt;
        lock (state.Gate) pkt = InstanceVillage.RecruitPool(state.RecruitPoolFlag, state.ToWireRecruits());
        await Program.Send(player, pkt, "0x02c9[0x01] village recruit pool");
    }

    public static async Task SyncShipsAsync(Player player, VillageTownState? existing = null)
    {
        var state = existing ?? GetState(player);
        byte[][] pkts = new byte[VillageData.ShipCount][];
        lock (state.Gate)
        {
            for (int i = 0; i < VillageData.ShipCount; i++)
            {
                var s = state.Ships[i];
                pkts[i] = InstanceVillage.ShipUpdate((byte)i, s.PortRow, s.FailureRow, s.CargoExports);
            }
        }
        for (int i = 0; i < VillageData.ShipCount; i++)
            await Program.Send(player, pkts[i], $"0x02c9[0x02] village ship {i} update");
    }

    public static async Task SyncQuotasAsync(Player player, VillageTownState? existing = null)
    {
        var state = existing ?? GetState(player);
        byte[][] pkts = new byte[VillageData.PortCount][];
        lock (state.Gate)
        {
            for (int p = 0; p < VillageData.PortCount; p++)
            {
                var slots = new InstanceVillage.PortQuotaSlot[VillageData.QuotasPerPort];
                for (int q = 0; q < VillageData.QuotasPerPort; q++)
                    slots[q] = state.PortQuotas[p][q].ToWire();
                pkts[p] = InstanceVillage.PortQuotas((byte)p, slots);
            }
        }
        for (int p = 0; p < VillageData.PortCount; p++)
            await Program.Send(player, pkts[p], $"0x02c9[0x03] village port {p + 1} quotas");
    }

    public static Task SendToastAsync(Player player, byte a, byte b, byte c, bool kindB = false)
    {
        byte[] pkt = kindB ? InstanceVillage.ToastNotificationB(a, b, c) : InstanceVillage.ToastNotificationA(a, b, c);
        return Program.Send(player, pkt, $"0x02c9[{(kindB ? "0x0c" : "0x0b")}] village toast ({a}, {b}, {c})");
    }

    /// <summary>Broadcasts a full state sync to all tracked online players belonging to <paramref name="scope"/>.</summary>
    public static async Task BroadcastStateToScopeAsync(TownScope scope)
    {
        if (!States.TryGetValue(scope, out var state)) return;
        foreach (var player in OnlinePlayers.Keys)
        {
            if (player.Closed || player.LeavingArea) continue;
            if (ScopeOf(player) != scope) continue;
            try { await SyncStateAsync(player, state); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        }
    }

    static async Task NotifyStateSyncedAsync(Player player, VillageTownState state)
    {
        if (OnStateSynced is not { } handlers) return;
        foreach (Func<Player, VillageTownState, Task> handler in Delegate.EnumerateInvocationList(handlers))
        {
            try { await handler(player, state); }
            catch (Exception ex) { Console.WriteLine($"[settlers] OnStateSynced handler failed: {ex.Message}"); }
        }
    }
}
