using System.Text.Json;
using System.Text.Json.Serialization;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Settlers.Operations;

/// <summary>
/// Kingsmarch Atlas Runners (3 map devices backed by <c>KalguurMapping1</c> inventory row 88, 12 slots x 3 devices).
/// <para>
/// Implements exact 3.29.3c T1–T17 simulation formulas from <c>Client/Game.exe</c> (<c>0x1424c7140</c>, <c>0x1424c7580</c>,
/// <c>0x1424c6880</c>), Bandit Lord capture ambush (Sasan in Abandoned Port), and map loot generation into <c>village_reward_items</c>.
/// All state flows exclusively through <see cref="IOperationsHost"/> using <c>town-idle</c>'s exact <c>MapSummaryBlob</c>
/// JSON schema and state enum (<c>3 = Kidnapped</c>), with in-memory progress updates (<c>persistNow: false</c>) during active runs.
/// </para>
/// </summary>
public sealed class VillageMappingService(IOperationsHost host)
{
    // Matches town-idle VillageTownState.TownMapDevice.State enum (0=Idle, 1=Running, 2=Paused/Done, 3=Kidnapped).
    public const int DeviceStateIdle = 0;
    public const int DeviceStateRunning = 1;
    public const int DeviceStateKidnapped = 3;

    // MapDeviceState StateFlags1 bits: bit 0 = running, bit 1 = pause after current map, bit 2 = failed/lost mapper ack needed.
    public const byte FlagRunning = 0x01;
    public const byte FlagPauseAfterCurrent = 0x02;
    public const byte FlagFailedAckPending = 0x04;

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Exact match to town-idle's VillageTownState.MapSummaryBlob.
    sealed record MapSummaryBlob(uint Elapsed, uint Total, byte F1, byte F2, List<byte> Percents, string CurrentMapName, string? Extra);

    // Extra run simulation parameters stored cleanly inside MapSummaryBlob.Extra.
    sealed record MapRunExtra(
        int MapTier,
        int MapIIQ,
        int MinPct,
        int MaxPct,
        int DeathChancePct,
        double ElapsedProgress,
        double ProgressPerSecond);

    sealed record DeviceSummary(
        string CurrentMapName,
        byte[] CompletionPercents,
        bool PauseAfterCurrent,
        bool FailedAckPending,
        int MapTier,
        int MapIIQ,
        int MinPct,
        int MaxPct,
        int DeathChancePct,
        uint ProgressTarget,
        double ElapsedProgress,
        double ProgressPerSecond);

    /// <summary>Exact map difficulty % (<c>0x1424c7140</c>).</summary>
    public static int ComputeDifficultyPercent(Item mapItem, int tier, ItemCatalog catalog)
    {
        OperationsData.EnsureLoaded();
        int explicitModCount = 0;
        int mapIIQ = mapItem.Quality;
        int bonusFromStats = 0;

        var bonusByStat = OperationsData.MappingBonusStats;
        foreach (var mod in mapItem.Mods)
        {
            if (mod.Section == ModSection.Explicit) explicitModCount++;
            if (catalog.Mods.ByHash(mod.Hash) is not { } def) continue;
            for (int i = 0; i < def.Stats.Count && i < mod.Values.Count; i++)
            {
                uint statId = def.Stats[i].Id;
                int val = mod.Values[i];
                if (val == 0) continue;
                if (statId == Stat.MapItemDropQuantityPlusPct) mapIIQ += val;
                for (int b = 0; b < bonusByStat.Length; b++)
                    if (bonusByStat[b].StatId == statId) bonusFromStats += bonusByStat[b].PercentChance;
            }
        }

        int scaledIIQ = (mapIIQ * (100 + OperationsData.MappingModEffectPct)) / 100;
        int t16Bonus = tier >= 16 && OperationsData.MappingTier16EldritchOrMaven != 0 ? 25 : 0;
        return Math.Max(0, scaledIIQ + bonusFromStats + t16Bonus + explicitModCount * 10);
    }

    /// <summary>Exact T1–T17 completion range, run time, progress rate, and death chance (<c>0x1424c7580</c>, <c>0x1424c6880</c>).</summary>
    public static (int MinPct, int MaxPct, uint ProgressTarget, double ProgressPerSec, int DeathChancePct) ComputeRunOutcome(
        int tier,
        int diffPct,
        int teamPower,
        int mappingSpeedStat,
        double speedMultiplier,
        double deathChanceMultiplier)
    {
        OperationsData.EnsureLoaded();
        var tierRow = OperationsData.MappingTierFor(tier);
        double ratio = Math.Pow((tierRow.BaseDifficulty + 100.0) / (diffPct + 100.0), 0.95)
            * Math.Pow(Math.Max(1.0, teamPower) / Math.Max(1.0, tierRow.BasePower), 1.2)
            * 0.9;

        int pct = (int)Math.Round(ratio * 100.0);
        int minPct = (pct * 50) / 100;
        int maxPct = (pct * 150) / 100;

        int runTimeSec = (int)Math.Max(1, Math.Round(tierRow.BaseTime * Math.Pow(1.0 + diffPct * 0.01, 0.5)));
        uint progressTarget = (uint)(runTimeSec * 100);
        double progressPerSec = Math.Max(1.0, ((teamPower + 100.0) * (100.0 + mappingSpeedStat) / 100.0) * Math.Max(0.01, speedMultiplier));

        int baseDeath = Math.Clamp((int)Math.Round((1.0 - Math.Pow(Math.Max(ratio, 0.0001), 0.25)) * 100.0), 1, 100);
        int scaledDeath = deathChanceMultiplier <= 0 ? 0 : Math.Clamp((int)Math.Round(baseDeath * deathChanceMultiplier), 1, 100);
        return (minPct, maxPct, progressTarget, progressPerSec, scaledDeath);
    }

    public static InstanceVillage.MapDeviceState[] BuildMapDeviceStates(VillageStore.TownSnapshot snap, ulong nowUtc)
    {
        var states = new InstanceVillage.MapDeviceState[OperationsData.MapDeviceCount];
        for (int i = 0; i < states.Length; i++)
            states[i] = new InstanceVillage.MapDeviceState(0, 0, 0, 0, []);

        foreach (var d in snap.MapDevices)
        {
            if (d.DeviceIndex < 0 || d.DeviceIndex >= states.Length) continue;
            var summary = ParseSummary(d.SummaryJson);
            byte flags1 = BuildFlags1(d.State == DeviceStateRunning, summary.PauseAfterCurrent, summary.FailedAckPending);
            uint target = summary.ProgressTarget > 0 ? summary.ProgressTarget : (uint)Math.Max(0, d.MapDurationSeconds * 100);
            uint elapsed = d.State == DeviceStateRunning
                ? (uint)Math.Clamp((long)Math.Round(summary.ElapsedProgress), 0, target)
                : 0u;
            states[d.DeviceIndex] = new InstanceVillage.MapDeviceState(
                elapsed,
                target,
                flags1,
                (byte)(summary.PauseAfterCurrent ? 1 : 0),
                summary.CompletionPercents);
        }
        return states;
    }

    public static (byte Slot1Based, byte Flag) GetMapKidnapState(VillageStore.TownSnapshot snap)
    {
        foreach (var d in snap.MapDevices)
            if (d.BossAmbushPending && d.State == DeviceStateKidnapped && d.DeviceIndex is >= 0 and < OperationsData.MapDeviceCount)
                return ((byte)(d.DeviceIndex + 1), 1);
        return (0, 0);
    }

    public string? ValidatePlacement(Player player, Item item)
    {
        var baseType = ItemsFor(player).ByHash(item.TypeHash);
        if (baseType is null) return "unknown item type";
        if (baseType.Class is not (MapDevice.MapClass or MapDevice.MapKeyClass))
            return "only Maps can be placed into Kingsmarch Atlas Runner map devices";
        return null;
    }

    public Task OnInventoryChangedAsync(Player player) => Task.CompletedTask;

    public async Task SyncCurrentMapNamesAsync(Player player)
    {
        var (account, league) = VillageStore.ScopeOf(player);
        var snap = host.ReadSnapshot(account, league);
        var names = new string[OperationsData.MapDeviceCount];
        Array.Fill(names, "");
        if (snap is not null)
        {
            foreach (var d in snap.MapDevices)
                if (d.DeviceIndex >= 0 && d.DeviceIndex < names.Length && d.State == DeviceStateRunning)
                    names[d.DeviceIndex] = ParseSummary(d.SummaryJson).CurrentMapName;
        }

        for (int i = 0; i < names.Length; i++)
            await Send(player, InstanceVillage.MapDeviceCurrentMapName((byte)i, names[i]),
                $"0x02c9 village MapDeviceCurrentMapName(device={i}, '{names[i]}')");
    }

    /// <summary>C→S <c>0x02ca</c> subtag <c>0x0c</c>: StartMapping.</summary>
    public async Task<bool> HandleStartMappingAsync(Player player, InstanceVillage.StartMappingCommand cmd)
    {
        if (cmd.MapDeviceIndex >= OperationsData.MapDeviceCount) return true;
        var (account, league) = VillageStore.ScopeOf(player);

        if (await TryStartNextMapAsync(player, account, league, cmd.MapDeviceIndex, clearPause: true))
            await host.SyncFullVillageStateAsync(player);
        return true;
    }

    /// <summary>C→S <c>0x02ca</c> subtag <c>0x0d</c>: StopMappingImmediate (Exit Map).</summary>
    public async Task<bool> HandleStopMappingImmediateAsync(Player player, InstanceVillage.StopMappingImmediateCommand cmd)
    {
        if (cmd.MapDeviceIndex >= OperationsData.MapDeviceCount) return true;
        var (account, league) = VillageStore.ScopeOf(player);

        bool stopped = host.MutateSnapshot(account, league, snap =>
        {
            var dev = snap.MapDevices.FirstOrDefault(d => d.DeviceIndex == cmd.MapDeviceIndex);
            if (dev is null || dev.State != DeviceStateRunning) return (null, false);
            var summary = ParseSummary(dev.SummaryJson);
            var nextSummary = summary with { CurrentMapName = "", PauseAfterCurrent = false, ElapsedProgress = 0, ProgressTarget = 0 };
            var devices = snap.MapDevices.Where(d => d.DeviceIndex != cmd.MapDeviceIndex).ToList();
            devices.Add(dev with
            {
                State = DeviceStateIdle,
                ActiveMapSlot = -1,
                MapStartedUtc = 0,
                MapDurationSeconds = 0,
                SummaryJson = SerializeSummary(nextSummary, running: false),
            });
            devices.Sort((a, b) => a.DeviceIndex.CompareTo(b.DeviceIndex));
            return (snap with { MapDevices = devices }, true);
        }, persistNow: true);

        if (stopped)
        {
            await Send(player, InstanceVillage.MapDeviceCurrentMapName(cmd.MapDeviceIndex, ""),
                $"0x02c9 village MapDeviceCurrentMapName(device={cmd.MapDeviceIndex}, '')");
            await host.SyncFullVillageStateAsync(player);
        }
        return true;
    }

    /// <summary>C→S <c>0x02ca</c> subtag <c>0x0e</c>: SetMappingDelayedStop (pause after current map).</summary>
    public async Task<bool> HandleSetDelayedStopAsync(Player player, InstanceVillage.SetMappingDelayedStopCommand cmd)
    {
        if (cmd.MapDeviceIndex >= OperationsData.MapDeviceCount) return true;
        var (account, league) = VillageStore.ScopeOf(player);

        host.MutateSnapshot(account, league, snap =>
        {
            var dev = snap.MapDevices.FirstOrDefault(d => d.DeviceIndex == cmd.MapDeviceIndex)
                ?? new VillageStore.MapDeviceRow(cmd.MapDeviceIndex, DeviceStateIdle, -1, 0, 0, 0, 0, false, 0, "[]", "{}");
            var summary = ParseSummary(dev.SummaryJson) with { PauseAfterCurrent = cmd.Suspend };
            var devices = snap.MapDevices.Where(d => d.DeviceIndex != cmd.MapDeviceIndex).ToList();
            devices.Add(dev with { SummaryJson = SerializeSummary(summary, running: dev.State == DeviceStateRunning) });
            devices.Sort((a, b) => a.DeviceIndex.CompareTo(b.DeviceIndex));
            return (snap with { MapDevices = devices }, true);
        }, persistNow: true);

        await host.SyncFullVillageStateAsync(player);
        return true;
    }

    /// <summary>C→S <c>0x02ca</c> subtag <c>0x1d</c>: AckFailedMapRun.</summary>
    public async Task<bool> HandleAckFailedMapRunAsync(Player player, InstanceVillage.AckFailedMapRunCommand cmd)
    {
        if (cmd.MapDeviceIndex >= OperationsData.MapDeviceCount) return true;
        var (account, league) = VillageStore.ScopeOf(player);

        host.MutateSnapshot(account, league, snap =>
        {
            var dev = snap.MapDevices.FirstOrDefault(d => d.DeviceIndex == cmd.MapDeviceIndex);
            if (dev is null) return (null, false);
            var summary = ParseSummary(dev.SummaryJson) with { FailedAckPending = false };
            var devices = snap.MapDevices.Where(d => d.DeviceIndex != cmd.MapDeviceIndex).ToList();
            devices.Add(dev with { SummaryJson = SerializeSummary(summary, running: dev.State == DeviceStateRunning) });
            devices.Sort((a, b) => a.DeviceIndex.CompareTo(b.DeviceIndex));
            return (snap with { MapDevices = devices }, true);
        }, persistNow: true);

        await host.SyncFullVillageStateAsync(player);
        return true;
    }

    /// <summary>C→S <c>0x02ca</c> subtag <c>0x0f</c>: RescueKidnappedMappers (opens Abandoned Port portal to Sasan).</summary>
    public async Task<bool> HandleRescueMappersAsync(Player player)
    {
        var (account, league) = VillageStore.ScopeOf(player);
        int devIdx = FindKidnappedDevice(account, league);
        if (devIdx < 0) return true;
        await host.OpenBanditBossPortalAsync(player, devIdx);
        return true;
    }

    /// <summary>C→S <c>0x02ca</c> subtag <c>0x10</c>: PayMapperRansom (<c>50,000</c> gold).</summary>
    public async Task<bool> HandlePayMapperRansomAsync(Player player)
    {
        OperationsData.EnsureLoaded();
        var (account, league) = VillageStore.ScopeOf(player);
        int devIdx = FindKidnappedDevice(account, league);
        if (devIdx < 0) return true;

        if (!await host.TrySpendPlayerOrTreasuryGoldAsync(player, OperationsData.RansomCost, "Kingsmarch mapper ransom"))
        {
            if (player.Commands is { } cmds) await cmds.Reply($"Need {OperationsData.RansomCost:N0} gold to ransom the kidnapped Atlas Runners.");
            return true;
        }

        await ReleaseKidnappedDeviceAsync(player, account, league, devIdx, rescueBonus: false);
        return true;
    }

    /// <summary>C→S <c>0x02ca</c> subtag <c>0x11</c>: AbandonKidnappedMappers (loses kidnapped mappers on that device).</summary>
    public async Task<bool> HandleAbandonMappersAsync(Player player)
    {
        var (account, league) = VillageStore.ScopeOf(player);
        int devIdx = FindKidnappedDevice(account, league);
        if (devIdx < 0) return true;

        await host.LoseAllWorkersOnJobAsync(player, account, league,
            (byte)(OperationsData.JobMappingFirst + devIdx),
            (byte)(OperationsData.JobLostMappingFirst + devIdx));

        host.MutateSnapshot(account, league, snap =>
        {
            var dev = snap.MapDevices.FirstOrDefault(d => d.DeviceIndex == devIdx);
            if (dev is null) return (null, false);
            var summary = ParseSummary(dev.SummaryJson) with { CurrentMapName = "", ElapsedProgress = 0, ProgressTarget = 0, FailedAckPending = true };
            var devices = snap.MapDevices.Where(d => d.DeviceIndex != devIdx).ToList();
            devices.Add(dev with
            {
                State = DeviceStateIdle,
                ActiveMapSlot = -1,
                BossAmbushPending = false,
                RansomGold = 0,
                SummaryJson = SerializeSummary(summary, running: false),
            });
            devices.Sort((a, b) => a.DeviceIndex.CompareTo(b.DeviceIndex));
            return (snap with { MapDevices = devices }, true);
        }, persistNow: true);

        await host.SyncFullVillageStateAsync(player);
        return true;
    }

    /// <summary>Called when Sasan, the Bandit Lord is defeated in Abandoned Port — frees kidnapped mappers + bonus loot.</summary>
    public async Task ResolveBanditRescueAsync(Player player)
    {
        var (account, league) = VillageStore.ScopeOf(player);
        int devIdx = FindKidnappedDevice(account, league);
        if (devIdx < 0) return;
        await ReleaseKidnappedDeviceAsync(player, account, league, devIdx, rescueBonus: true);
    }

    /// <summary>Advances active map devices in memory (<c>persistNow: false</c>) and flushes SQLite on map completion.</summary>
    public async Task<bool> TickAsync(Player player, double elapsedSeconds)
    {
        OperationsData.EnsureLoaded();
        var (account, league) = VillageStore.ScopeOf(player);

        if (host.Settings.GoldBurnRateMultiplier > 0 && host.GetTreasuryGold(account, league) == 0)
            return false;

        var snapNow = host.ReadSnapshot(account, league);
        if (snapNow is null) return false;
        var activeDevices = snapNow.MapDevices.Where(d => d.State == DeviceStateRunning).Select(d => d.DeviceIndex).ToList();
        if (activeDevices.Count == 0) return false;

        bool anyChanged = false;
        foreach (int devIdx in activeDevices)
        {
            double remaining = elapsedSeconds;
            int safety = 0;
            while (remaining > 0 && safety++ < 12)
            {
                var step = host.MutateSnapshot<(bool Completed, double Left, DeviceSummary? Summary, VillageStore.MapDeviceRow? Device)>(
                    account,
                    league,
                    snap =>
                    {
                        var dev = snap.MapDevices.FirstOrDefault(d => d.DeviceIndex == devIdx);
                        if (dev is null || dev.State != DeviceStateRunning)
                            return (null, (false, 0.0, null, null));

                        var summary = ParseSummary(dev.SummaryJson);
                        double rate = Math.Max(1.0, summary.ProgressPerSecond);
                        double target = Math.Max(100.0, summary.ProgressTarget);
                        double next = summary.ElapsedProgress + remaining * rate;
                        if (next < target)
                        {
                            var updated = summary with { ElapsedProgress = next };
                            var devices = snap.MapDevices.Where(d => d.DeviceIndex != devIdx).ToList();
                            devices.Add(dev with { SummaryJson = SerializeSummary(updated, running: true) });
                            devices.Sort((a, b) => a.DeviceIndex.CompareTo(b.DeviceIndex));
                            return (snap with { MapDevices = devices }, (false, 0.0, updated, dev));
                        }

                        double usedSec = Math.Max(0.0, (target - summary.ElapsedProgress) / rate);
                        // Transition to Idle inside lock so concurrent ticks cannot double-complete the same map.
                        var devicesDone = snap.MapDevices.Where(d => d.DeviceIndex != devIdx).ToList();
                        devicesDone.Add(dev with { State = DeviceStateIdle });
                        devicesDone.Sort((a, b) => a.DeviceIndex.CompareTo(b.DeviceIndex));
                        return (snap with { MapDevices = devicesDone }, (true, Math.Max(0.0, remaining - usedSec), summary, dev));
                    },
                    persistNow: false);

                if (step.Summary is null || step.Device is null) break;
                anyChanged = true;
                if (!step.Completed) break;

                remaining = step.Left;
                bool canContinue = await CompleteActiveMapAsync(player, account, league, devIdx, step.Device, step.Summary);
                if (!canContinue) break;
            }
        }

        return anyChanged;
    }

    async Task<bool> TryStartNextMapAsync(Player player, string account, string league, int deviceIdx, bool clearPause)
    {
        OperationsData.EnsureLoaded();
        var mappers = host.GetWorkersForJob(account, league, (byte)(OperationsData.JobMappingFirst + deviceIdx));
        if (mappers.Count == 0) return false;

        var inv = ItemsOf(player);
        var queued = inv.Items(PlayerInventories.KalguurMapping)
            .Where(i => i.Y == deviceIdx)
            .OrderBy(i => i.X)
            .FirstOrDefault();
        if (queued is null) return false;

        var catalog = ItemsFor(player);
        var (tier, mapName) = ResolveMapInfo(queued.Item, catalog);
        int diffPct = ComputeDifficultyPercent(queued.Item, tier, catalog);
        int teamPower = mappers.Sum(m => OperationsData.WorkerPowerForRank(m.MappingRank));
        var settings = host.Settings;
        var (minPct, maxPct, progressTarget, progressPerSec, deathPct) = ComputeRunOutcome(
            tier, diffPct, teamPower, 0, settings.MappingSpeedMultiplier, settings.MappingDeathChanceMultiplier);

        int mapIIQ = queued.Item.Quality;
        foreach (var mod in queued.Item.Mods)
            if (catalog.Mods.ByHash(mod.Hash) is { } def)
                for (int i = 0; i < def.Stats.Count && i < mod.Values.Count; i++)
                    if (def.Stats[i].Id == Stat.MapItemDropQuantityPlusPct) mapIIQ += mod.Values[i];

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long estDurationSec = Math.Max(1, (long)Math.Ceiling(progressTarget / Math.Max(1.0, progressPerSec)));

        bool started = host.MutateSnapshot(account, league, snap =>
        {
            var existing = snap.MapDevices.FirstOrDefault(d => d.DeviceIndex == deviceIdx);
            if (existing is { State: DeviceStateRunning or DeviceStateKidnapped }) return (null, false);
            var prevSummary = existing is not null ? ParseSummary(existing.SummaryJson) : DefaultSummary();
            var nextSummary = new DeviceSummary(
                mapName,
                prevSummary.CompletionPercents,
                clearPause ? false : prevSummary.PauseAfterCurrent,
                false,
                tier,
                mapIIQ,
                minPct,
                maxPct,
                deathPct,
                progressTarget,
                0.0,
                progressPerSec);

            var devices = snap.MapDevices.Where(d => d.DeviceIndex != deviceIdx).ToList();
            devices.Add(new VillageStore.MapDeviceRow(
                deviceIdx,
                DeviceStateRunning,
                queued.X,
                now,
                estDurationSec,
                0,
                6,
                false,
                0,
                JsonSerializer.Serialize(mappers.Select(m => m.WorkerId).ToArray()),
                SerializeSummary(nextSummary, running: true)));
            devices.Sort((a, b) => a.DeviceIndex.CompareTo(b.DeviceIndex));
            return (snap with
            {
                Town = snap.Town with { NextMapRunId = snap.Town.NextMapRunId + 1 },
                MapDevices = devices,
            }, true);
        }, persistNow: true);

        if (started && OperationsSubsystem.IsVillageArea(player))
            await Send(player, InstanceVillage.MapDeviceCurrentMapName((byte)deviceIdx, mapName),
                $"0x02c9 village MapDeviceCurrentMapName(device={deviceIdx}, '{mapName}')");

        return started;
    }

    async Task<bool> CompleteActiveMapAsync(
        Player player,
        string account,
        string league,
        int deviceIdx,
        VillageStore.MapDeviceRow dev,
        DeviceSummary summary)
    {
        var rng = Random.Shared;
        var inv = ItemsOf(player);
        var activeMap = inv.Items(PlayerInventories.KalguurMapping)
            .FirstOrDefault(i => i.Y == deviceIdx && i.X == dev.ActiveMapSlot)
            ?? inv.Items(PlayerInventories.KalguurMapping).Where(i => i.Y == deviceIdx).OrderBy(i => i.X).FirstOrDefault();

        if (activeMap is not null && inv.Remove(PlayerInventories.KalguurMapping, activeMap.Id) is { } removed)
        {
            await Send(player, InstanceItems.InventoryChange(
                PlayerInventories.InventoryId(PlayerInventories.KalguurMapping),
                inv.NextRevision(PlayerInventories.KalguurMapping),
                [removed.Id], []),
                $"0x00bf village mapping consumed map {removed.Id} from device {deviceIdx}");
        }

        bool alreadyKidnapped = FindKidnappedDevice(account, league) >= 0;
        double ambushChance = host.Settings.BossAmbushChanceMultiplier <= 0 || alreadyKidnapped
            ? 0.0
            : Math.Clamp((0.02 + summary.MapTier * 0.0015) * host.Settings.BossAmbushChanceMultiplier, 0.0, 0.85);

        if (ambushChance > 0 && rng.NextDouble() < ambushChance)
        {
            host.MutateSnapshot(account, league, snap =>
            {
                var devices = snap.MapDevices.Where(d => d.DeviceIndex != deviceIdx).ToList();
                devices.Add(dev with
                {
                    State = DeviceStateKidnapped,
                    BossAmbushPending = true,
                    RansomGold = OperationsData.RansomCost,
                    SummaryJson = SerializeSummary(summary, running: false),
                });
                devices.Sort((a, b) => a.DeviceIndex.CompareTo(b.DeviceIndex));
                return (snap with { MapDevices = devices }, true);
            }, persistNow: true);

            if (OperationsSubsystem.IsVillageArea(player))
                await Send(player, InstanceVillage.ToastNotificationB(1, (byte)deviceIdx, 1),
                    "0x02c9 village ToastNotificationB(MappersKidnapped)");
            return false;
        }

        int lo = Math.Clamp(summary.MinPct, 1, 100);
        int hi = Math.Clamp(Math.Max(lo, summary.MaxPct), 1, 100);
        byte completionPct = (byte)rng.Next(lo, hi + 1);
        bool lostMapper = false;

        if (summary.DeathChancePct > 0 && rng.Next(100) < summary.DeathChancePct)
        {
            var mappers = host.GetWorkersForJob(account, league, (byte)(OperationsData.JobMappingFirst + deviceIdx));
            if (mappers.Count > 0)
            {
                var victim = mappers[rng.Next(mappers.Count)];
                lostMapper = await host.KillWorkerAsync(player, account, league, victim.WorkerId,
                    (byte)(OperationsData.JobLostMappingFirst + deviceIdx));
            }
        }

        var loot = GenerateMapRunLoot(player, summary.MapTier, summary.MapIIQ, completionPct, rng);
        OperationsSubsystem.Rewards.AddRewards(account, league, VillageRewardsService.SourceMapping, deviceIdx, loot, ItemsFor(player).Mods);

        var nextCompletions = summary.CompletionPercents.Append(completionPct).TakeLast(12).ToArray();
        bool pauseAfter = summary.PauseAfterCurrent;

        host.MutateSnapshot(account, league, snap =>
        {
            var nextSummary = summary with
            {
                CurrentMapName = "",
                CompletionPercents = nextCompletions,
                FailedAckPending = summary.FailedAckPending || lostMapper,
                ElapsedProgress = 0,
                ProgressTarget = 0,
            };
            var devices = snap.MapDevices.Where(d => d.DeviceIndex != deviceIdx).ToList();
            devices.Add(dev with
            {
                State = DeviceStateIdle,
                ActiveMapSlot = -1,
                MapStartedUtc = 0,
                MapDurationSeconds = 0,
                CompletionPercent = completionPct,
                SummaryJson = SerializeSummary(nextSummary, running: false),
            });
            devices.Sort((a, b) => a.DeviceIndex.CompareTo(b.DeviceIndex));
            return (snap with { MapDevices = devices }, true);
        }, persistNow: true);

        if (OperationsSubsystem.IsVillageArea(player))
        {
            await Send(player, InstanceVillage.MapDeviceCurrentMapName((byte)deviceIdx, ""),
                $"0x02c9 village MapDeviceCurrentMapName(device={deviceIdx}, completed {completionPct}%)");
            await OperationsSubsystem.Rewards.SyncRewardVisibilityAsync(player);
        }

        if (pauseAfter) return false;
        return await TryStartNextMapAsync(player, account, league, deviceIdx, clearPause: false);
    }

    async Task ReleaseKidnappedDeviceAsync(Player player, string account, string league, int devIdx, bool rescueBonus)
    {
        DeviceSummary? summary = host.MutateSnapshot<DeviceSummary?>(account, league, snap =>
        {
            var dev = snap.MapDevices.FirstOrDefault(d => d.DeviceIndex == devIdx && d.BossAmbushPending && d.State == DeviceStateKidnapped);
            if (dev is null) return (null, null);
            var s = ParseSummary(dev.SummaryJson);
            var next = s with { CurrentMapName = "", ElapsedProgress = 0, ProgressTarget = 0, FailedAckPending = false };
            var devices = snap.MapDevices.Where(d => d.DeviceIndex != devIdx).ToList();
            devices.Add(dev with
            {
                State = DeviceStateIdle,
                ActiveMapSlot = -1,
                BossAmbushPending = false,
                RansomGold = 0,
                SummaryJson = SerializeSummary(next, running: false),
            });
            devices.Sort((a, b) => a.DeviceIndex.CompareTo(b.DeviceIndex));
            return (snap with { MapDevices = devices }, s);
        }, persistNow: true);

        if (summary is not null)
        {
            int tier = Math.Clamp(summary.MapTier, 1, 17);
            var loot = GenerateMapRunLoot(player, tier, summary.MapIIQ + (rescueBonus ? 60 : 0), 100, Random.Shared);
            OperationsSubsystem.Rewards.AddRewards(account, league, VillageRewardsService.SourceMapping, devIdx, loot, ItemsFor(player).Mods);
        }

        if (OperationsSubsystem.IsVillageArea(player))
        {
            await OperationsSubsystem.Rewards.SyncRewardVisibilityAsync(player);
            await host.SyncFullVillageStateAsync(player);
        }
    }

    static List<Item> GenerateMapRunLoot(Player player, int tier, int mapIIQ, int completionPct, Random rng)
    {
        var catalog = ItemsFor(player);
        var rolls = RollsFor(player);
        var uniques = LeagueFor(player).HardMode ? HardModeUniques : Uniques;
        var results = new List<Item>();

        int areaLevel = Math.Clamp(67 + tier, 68, 84);
        double factor = (completionPct / 100.0) * (1.0 + Math.Max(0, mapIIQ) / 150.0);
        int dropCount = Math.Clamp((int)Math.Round((4 + tier * 0.65) * factor), 2, 22);

        var currencyCounts = new Dictionary<uint, (ItemCatalog.BaseType Type, int Count)>();
        int currRolls = Math.Max(2, (int)Math.Round(dropCount * 0.55));
        for (int i = 0; i < currRolls; i++)
        {
            if (RollWeightedCurrency(catalog, areaLevel, rng) is not { } drop) continue;
            if (drop.Type.Class == ItemDrops.GoldClass || !drop.Type.Supported) continue;
            int qty = Math.Max(1, (int)drop.Item.StackSize);
            if (currencyCounts.TryGetValue(drop.Type.Hash, out var cur))
                currencyCounts[drop.Type.Hash] = (drop.Type, cur.Count + qty);
            else
                currencyCounts[drop.Type.Hash] = (drop.Type, qty);
        }
        foreach (var (_, (bt, total)) in currencyCounts)
        {
            int left = total;
            int maxStack = Math.Max(1, (int)bt.MaxStack);
            while (left > 0)
            {
                int stack = Math.Min(left, maxStack);
                results.Add(ItemCatalog.Create(bt) with { StackSize = (ushort)stack });
                left -= stack;
            }
        }

        int scarabRolls = tier >= 11 ? 2 : 1;
        for (int i = 0; i < scarabRolls; i++)
        {
            if (rng.NextDouble() < 0.45 * factor && MapScarab(Drops, rng, areaLevel, null) is { } scarab && scarab.Type.Supported)
                results.Add(scarab.Item);
        }

        if (rng.NextDouble() < 0.55 * factor && RollMapDropForKill(player, areaLevel, MonsterRarity.Rare, tier, rng, 1.0) is { } mapDrop && mapDrop.Type.Supported)
            results.Add(mapDrop.Item);

        int gearRolls = Math.Clamp(dropCount - currRolls, 1, 8);
        var gearBases = catalog.All.Where(t =>
            t.Supported && !t.Stackable && t.DropLevel <= areaLevel
            && OperationsData.IsRecombinableClass(t.Class)
            && t.Class != "AtlasRelic" && t.Class != "FishingRod").ToList();

        for (int i = 0; i < gearRolls && gearBases.Count > 0; i++)
        {
            if (rng.NextDouble() < 0.08 && uniques.All.Count > 0)
            {
                var def = uniques.All[rng.Next(uniques.All.Count)];
                if (uniques.Create(def, rng) is { } unique && catalog.ByHash(unique.TypeHash) is { Supported: true })
                {
                    results.Add(unique with { ItemLevel = (byte)areaLevel, Identified = true });
                    continue;
                }
            }
            var bt = gearBases[rng.Next(gearBases.Count)];
            var rolled = rolls.Roll(ItemCatalog.Create(bt) with { ItemLevel = (byte)areaLevel }, ItemRarity.Rare, rng);
            results.Add(rolled with { Identified = true });
        }

        return results;
    }

    int FindKidnappedDevice(string account, string league)
    {
        var snap = host.ReadSnapshot(account, league);
        if (snap is null) return -1;
        foreach (var d in snap.MapDevices)
            if (d.BossAmbushPending && d.State == DeviceStateKidnapped) return d.DeviceIndex;
        return -1;
    }

    static (int Tier, string Name) ResolveMapInfo(Item item, ItemCatalog catalog)
    {
        var bt = catalog.ByHash(item.TypeHash);
        string name = bt?.Name ?? "Map";
        if (item.TypeHash == OperationsData.SpecialTier17MapHash) return (17, name);
        if (bt is not null)
        {
            if (bt.Id.Contains("Tier17", StringComparison.OrdinalIgnoreCase) || bt.DropLevel >= 84) return (17, name);
            if (bt.DropLevel >= 68) return (Math.Clamp(bt.DropLevel - 67, 1, 17), name);
        }
        return (Math.Clamp(item.ItemLevel >= 68 ? item.ItemLevel - 67 : 1, 1, 17), name);
    }

    static byte BuildFlags1(bool running, bool pauseAfterCurrent, bool failedAckPending)
    {
        byte f = 0;
        if (running) f |= FlagRunning;
        if (pauseAfterCurrent) f |= FlagPauseAfterCurrent;
        if (failedAckPending) f |= FlagFailedAckPending;
        return f;
    }

    static string SerializeSummary(DeviceSummary s, bool running)
    {
        var extra = new MapRunExtra(s.MapTier, s.MapIIQ, s.MinPct, s.MaxPct, s.DeathChancePct, s.ElapsedProgress, s.ProgressPerSecond);
        uint elapsed = (uint)Math.Clamp((long)Math.Round(s.ElapsedProgress), 0, Math.Max(0, s.ProgressTarget));
        var blob = new MapSummaryBlob(
            elapsed,
            s.ProgressTarget,
            BuildFlags1(running, s.PauseAfterCurrent, s.FailedAckPending),
            (byte)(s.PauseAfterCurrent ? 1 : 0),
            [.. s.CompletionPercents],
            s.CurrentMapName,
            JsonSerializer.Serialize(extra, JsonOpts));
        return JsonSerializer.Serialize(blob, JsonOpts);
    }

    static DeviceSummary DefaultSummary() => new("", [], false, false, 1, 0, 50, 100, 1, 0, 0, 100);

    static DeviceSummary ParseSummary(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return DefaultSummary();
        try
        {
            var blob = JsonSerializer.Deserialize<MapSummaryBlob>(json, JsonOpts);
            if (blob is not null && (blob.Percents is not null || blob.CurrentMapName is not null || blob.Extra is not null))
            {
                MapRunExtra? extra = null;
                if (!string.IsNullOrWhiteSpace(blob.Extra))
                {
                    try { extra = JsonSerializer.Deserialize<MapRunExtra>(blob.Extra, JsonOpts); }
                    catch { }
                }
                return new DeviceSummary(
                    blob.CurrentMapName ?? "",
                    blob.Percents?.ToArray() ?? [],
                    (blob.F1 & FlagPauseAfterCurrent) != 0 || blob.F2 != 0,
                    (blob.F1 & FlagFailedAckPending) != 0,
                    extra?.MapTier ?? 1,
                    extra?.MapIIQ ?? 0,
                    extra?.MinPct ?? 50,
                    extra?.MaxPct ?? 100,
                    extra?.DeathChancePct ?? 1,
                    blob.Total,
                    extra?.ElapsedProgress ?? blob.Elapsed,
                    extra?.ProgressPerSecond ?? 100.0);
            }
            return JsonSerializer.Deserialize<DeviceSummary>(json) ?? DefaultSummary();
        }
        catch { return DefaultSummary(); }
    }
}
