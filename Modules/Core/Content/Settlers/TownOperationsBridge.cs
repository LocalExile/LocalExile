using System.Collections.Concurrent;
using GameServer.Core.Protocol.Instance;
using GameServer.Modules.Settlers.Bosses;
using GameServer.Modules.Settlers.Operations;
using GameServer.Modules.Settlers.Town;
using GameServer.Storage;

namespace GameServer.Modules.Settlers;

/// <summary>
/// Authoritative bridge implementing both <see cref="IOperationsHost"/> and <see cref="IVillageOreSink"/> directly
/// over <see cref="VillageTownService"/> (<see cref="VillageTownState"/> + <see cref="VillageTownState.Gate"/> +
/// <see cref="VillageStore"/>). Eliminates any split-brain cache between Town, Operations, MapOres, and Bosses.
/// </summary>
public sealed class TownOperationsBridge : IOperationsHost, IVillageOreSink
{
    readonly ConcurrentDictionary<TownScope, byte> _dirty = new();
    readonly SaveDatabase? _dbOverride;

    public TownOperationsBridge(SaveDatabase? dbOverride = null) => _dbOverride = dbOverride;

    public Func<SettlersSettings>? SettingsOverride { get; set; }

    public SettlersSettings Settings => SettingsOverride?.Invoke() ?? SettlersModule.Settings;

    VillageTownState StateFor(string account, string league) =>
        VillageTownService.GetState(account, league, _dbOverride);

    public void Clear() => _dirty.Clear();

    public bool HasActiveWork(string account, string league)
    {
        var state = StateFor(account, league);
        lock (state.Gate)
        {
            for (int i = 0; i < state.Ships.Length; i++)
                if (state.Ships[i].State == VillageShippingService.ShipStateSailing) return true;
            for (int i = 0; i < state.MapDevices.Length; i++)
                if (state.MapDevices[i].State == VillageMappingService.DeviceStateRunning) return true;
            if (state.DisenchantQueue.Count > 0) return true;
            return false;
        }
    }

    public VillageStore.TownSnapshot? ReadSnapshot(string account, string league)
    {
        var state = StateFor(account, league);
        lock (state.Gate)
            return state.ToStoreSnapshot();
    }

    public T MutateSnapshot<T>(
        string account,
        string league,
        Func<VillageStore.TownSnapshot, (VillageStore.TownSnapshot? Next, T Result)> mutate,
        bool persistNow = true)
    {
        var state = StateFor(account, league);
        lock (state.Gate)
        {
            var current = state.ToStoreSnapshot();
            var (next, result) = mutate(current);
            if (next is not null)
            {
                state.ApplyFromStoreSnapshot(next);
                if (persistNow)
                {
                    VillageTownService.SaveLocked(state, _dbOverride);
                    _dirty.TryRemove(state.Scope, out _);
                }
                else
                {
                    _dirty[state.Scope] = 1;
                }
            }
            return result;
        }
    }

    public void FlushIfDirty(string account, string league)
    {
        var scope = TownScope.Of(account, league);
        if (!_dirty.TryRemove(scope, out _)) return;
        var state = StateFor(account, league);
        lock (state.Gate)
            VillageTownService.SaveLocked(state, _dbOverride);
    }

    public uint GetResource(string account, string league, int resourceId)
    {
        if (resourceId is < 0 or >= VillageData.ResourceCount) return 0u;
        var state = StateFor(account, league);
        lock (state.Gate)
            return state.Resources[resourceId];
    }

    public uint AddResource(string account, string league, int resourceId, long delta)
    {
        uint updated = VillageTownService.AddResource(account, league, resourceId, delta, _dbOverride);
        _dirty.TryRemove(TownScope.Of(account, league), out _);
        return updated;
    }

    public bool TrySpendResources(string account, string league, IReadOnlyList<(int ResourceId, uint Amount)> costs)
    {
        var state = StateFor(account, league);
        var totals = new Dictionary<int, uint>();
        for (int i = 0; i < costs.Count; i++)
        {
            var (rid, amt) = costs[i];
            if (amt == 0) continue;
            totals[rid] = (uint)Math.Min((long)totals.GetValueOrDefault(rid) + amt, uint.MaxValue);
        }
        lock (state.Gate)
        {
            if (!VillageTownService.TrySpendResourcesLocked(state, totals))
                return false;
            VillageTownService.SaveLocked(state, _dbOverride);
            _dirty.TryRemove(state.Scope, out _);
            return true;
        }
    }

    public uint GetTreasuryGold(string account, string league)
    {
        var state = StateFor(account, league);
        lock (state.Gate)
            return state.TreasuryGold;
    }

    public async Task<bool> TrySpendPlayerOrTreasuryGoldAsync(Player player, uint amount, string reason)
    {
        if (amount == 0) return true;
        if (await VillageTownService.TrySpendGoldAsync(player, amount, reason))
            return true;
        var (account, league) = VillageStore.ScopeOf(player);
        var state = StateFor(account, league);
        lock (state.Gate)
        {
            if (!VillageTownService.TrySpendTreasuryGoldLocked(state, amount))
                return false;
            VillageTownService.SaveLocked(state, _dbOverride);
            _dirty.TryRemove(state.Scope, out _);
            return true;
        }
    }

    public IReadOnlyList<OperationsWorkerView> GetWorkersForJob(string account, string league, byte villageJobRow)
    {
        var state = StateFor(account, league);
        lock (state.Gate)
        {
            if (state.Workers.Count == 0) return [];
            var list = new List<OperationsWorkerView>();
            for (int i = 0; i < state.Workers.Count; i++)
            {
                var w = state.Workers[i];
                if (w.Status != 0 || w.AssignedJobRow != villageJobRow) continue;
                int dis = 0, map = 0, ship = 0;
                for (int s = 0; s < w.Skills.Count; s++)
                {
                    var sk = w.Skills[s];
                    if (sk.JobTypeRow == OperationsData.JobTypeDisenchanting) dis = Math.Max(dis, sk.Rank);
                    else if (sk.JobTypeRow == OperationsData.JobTypeMapping) map = Math.Max(map, sk.Rank);
                    else if (sk.JobTypeRow == OperationsData.JobTypeShipping) ship = Math.Max(ship, sk.Rank);
                }
                list.Add(new OperationsWorkerView(w.WorkerId, i, w.AssignedJobRow, dis, map, ship, IsAlive: true));
            }
            return list;
        }
    }

    public async Task<bool> KillWorkerAsync(Player? player, string account, string league, long workerId, byte lostJobRow)
    {
        var state = StateFor(account, league);
        int removedIdx = -1;
        lock (state.Gate)
        {
            removedIdx = state.Workers.FindIndex(w => w.WorkerId == workerId);
            if (removedIdx < 0) return false;
            state.Workers.RemoveAt(removedIdx);
            VillageTownService.SaveLocked(state, _dbOverride);
            _dirty.TryRemove(state.Scope, out _);
        }
        if (player is not null && removedIdx <= byte.MaxValue)
            await Send(player, InstanceVillage.RemoveWorker((byte)removedIdx), $"0x02c9[0x06] village RemoveWorker({removedIdx})");
        return true;
    }

    public async Task<int> LoseAllWorkersOnJobAsync(Player? player, string account, string league, byte villageJobRow, byte lostJobRow)
    {
        var state = StateFor(account, league);
        var removedIndices = new List<int>();
        lock (state.Gate)
        {
            for (int i = state.Workers.Count - 1; i >= 0; i--)
            {
                if (state.Workers[i].Status == 0 && state.Workers[i].AssignedJobRow == villageJobRow)
                {
                    removedIndices.Add(i);
                    state.Workers.RemoveAt(i);
                }
            }
            if (removedIndices.Count == 0) return 0;
            VillageTownService.SaveLocked(state, _dbOverride);
            _dirty.TryRemove(state.Scope, out _);
        }
        if (player is not null)
        {
            foreach (int idx in removedIndices)
                if (idx <= byte.MaxValue)
                    await Send(player, InstanceVillage.RemoveWorker((byte)idx), $"0x02c9[0x06] village RemoveWorker({idx})");
        }
        return removedIndices.Count;
    }

    public Task SyncFullVillageStateAsync(Player player) =>
        VillageTownService.SyncStateAsync(player);

    public async Task<bool> OpenPirateBossPortalAsync(Player player, int shipIndex)
    {
        var at = player.Layout.Near(player.Walk.Position(AreaTime(player)));
        uint? portalId = await SettlersBossCallbacks.OpenBossArenaPortal(player, at, SettlersBossKind.AdmiralValerius);
        return portalId is not null;
    }

    public async Task<bool> OpenBanditBossPortalAsync(Player player, int deviceIndex)
    {
        var at = player.Layout.Near(player.Walk.Position(AreaTime(player)));
        uint? portalId = await SettlersBossCallbacks.OpenBossArenaPortal(player, at, SettlersBossKind.BanditLord);
        return portalId is not null;
    }

    /// <summary>
    /// <see cref="IVillageOreSink"/> entry point called by <c>MapOres</c> (all 6 ore/dust encounters + Refiner's Bargain).
    /// Updates the authoritative <see cref="VillageTownState"/> + SQLite and pushes a live sync when applicable.
    /// </summary>
    public void GrantResource(Player player, VillageResourceKind resource, long amount, string source)
    {
        if (amount <= 0) return;
        var (account, league) = VillageStore.ScopeOf(player);
        AddResource(account, league, (int)resource, amount);
        if (!player.Closed && !player.LeavingArea && KaruiShoresIntro.NeedsVillageRuleset(player))
            _ = VillageTownService.SyncStateAsync(player);
    }

    /// <summary>
    /// Hooked to <see cref="VillageTownService.OnSimulateTick"/> (called inside <see cref="VillageTownState.Gate"/>)
    /// to keep live sailing ship countdown timers in <c>0x1424c7c10</c> progress units (<c>((dist/20) + 100) * 6000</c>)
    /// in memory without forcing a per-second SQLite write (<c>DepartedUtc</c>/<c>ArrivalUtc</c> are already persisted).
    /// </summary>
    public bool OnSimulateTickLocked(VillageTownState state, double productiveSeconds, long nowUtc)
    {
        for (int i = 0; i < state.Ships.Length; i++)
        {
            var s = state.Ships[i];
            if (s.State != VillageShippingService.ShipStateSailing || s.ArrivalUtc <= s.DepartedUtc) continue;
            long totalSec = Math.Max(1, s.ArrivalUtc - s.DepartedUtc);
            long elapsedSec = Math.Clamp(nowUtc - s.DepartedUtc, 0, totalSec);
            uint target = VillageShippingService.ComputeTravelTime(s.PortRow, 0, 1.0).ProgressTarget;
            uint elapsedProg = (uint)Math.Clamp((long)Math.Round(target * (elapsedSec / (double)totalSec)), 0, target);
            s.TimerTotal = target;
            s.TimerElapsed = elapsedProg;
        }
        return false;
    }
}
