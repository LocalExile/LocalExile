using System.Text.Json;
using System.Text.Json.Serialization;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Settlers.Operations;

/// <summary>
/// Kingsmarch Shipping (3 ships, 6 destination ports, Port Quotas, Risk/Failures, Pirate Commandeering, and Barter Rewards).
/// <para>
/// Implements exact 3.29.3c client formulas verified in <c>Client/Game.exe</c>:
/// shipment value &amp; dust multiplier (<c>0x1424c60e0</c>), risk % (<c>0x1417698e0</c>), travel time (<c>0x1424c7c10</c>),
/// <c>VillageShippingFailures</c> rows 1..20 indexing <c>VillageExports</c> 0..15, and port quotas bit-swap (<c>0x1417685b0</c>).
/// All state reads/mutations flow exclusively through <see cref="IOperationsHost"/> using <c>town-idle</c>'s exact
/// <c>ShipCargoBlob</c> schema and state enum (<c>3 = Commandeered</c>).
/// </para>
/// </summary>
public sealed class VillageShippingService(IOperationsHost host)
{
    // Matches town-idle VillageTownState.TownShip.State enum (0=Idle, 1=Sailing, 2=Returned/RewardsReady, 3=Commandeered).
    public const int ShipStateIdle = 0;
    public const int ShipStateSailing = 1;
    public const int ShipStateReturned = 2;
    public const int ShipStateCommandeered = 3;
    const int ShipStateResolving = 4;

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Exact match to town-idle's VillageTownState.ShipCargoBlob.
    sealed record ShipCargoBlob(uint[] Exports, uint Elapsed, uint Total);

    static readonly string[] CommonCurrencyNames =
    [
        "Orb of Transmutation", "Orb of Augmentation", "Orb of Alteration", "Orb of Chance",
        "Orb of Alchemy", "Chromatic Orb", "Jeweller's Orb", "Orb of Fusing",
        "Orb of Scouring", "Orb of Binding", "Cartographer's Chisel", "Glassblower's Bauble",
        "Gemcutter's Prism", "Blessed Orb", "Chaos Orb", "Orb of Regret", "Vaal Orb",
        "Regal Orb", "Orb of Unmaking", "Instilling Orb", "Enkindling Orb",
    ];

    static readonly string[] MidCurrencyNames =
    [
        "Chaos Orb", "Vaal Orb", "Regal Orb", "Orb of Regret", "Orb of Scouring",
        "Orb of Horizons", "Harbinger's Orb", "Ancient Orb", "Orb of Annulment",
        "Exalted Orb", "Eldritch Chaos Orb", "Eldritch Exalted Orb", "Eldritch Orb of Annulment",
    ];

    static readonly string[] HighCurrencyNames =
    [
        "Exalted Orb", "Divine Orb", "Orb of Annulment", "Sacred Orb", "Fracturing Shard",
        "Mirror Shard",
    ];

    public static string SerializeCargoBlob(uint[] exports, uint elapsed, uint total)
    {
        var arr = new uint[OperationsData.ExportCount];
        Array.Copy(exports, arr, Math.Min(exports.Length, arr.Length));
        return JsonSerializer.Serialize(new ShipCargoBlob(arr, elapsed, total), JsonOpts);
    }

    public static uint[] ParseCargo(string cargoJson)
    {
        var result = new uint[OperationsData.ExportCount];
        if (string.IsNullOrWhiteSpace(cargoJson)) return result;
        try
        {
            using var doc = JsonDocument.Parse(cargoJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if ((doc.RootElement.TryGetProperty("exports", out var ex) || doc.RootElement.TryGetProperty("Exports", out ex))
                    && ex.ValueKind == JsonValueKind.Array)
                {
                    int i = 0;
                    foreach (var el in ex.EnumerateArray())
                    {
                        if (i >= result.Length) break;
                        if (el.TryGetUInt32(out uint v)) result[i] = v;
                        i++;
                    }
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (i >= result.Length) break;
                    if (el.TryGetUInt32(out uint v)) result[i] = v;
                    i++;
                }
            }
        }
        catch { }
        return result;
    }

    /// <summary>Exact shipment value formula (<c>0x1424c60e0</c>).</summary>
    public static (ulong BaseValue, double DustMultiplier, ulong ShipmentValue) ComputeShipmentValue(ReadOnlySpan<uint> exportAmounts)
    {
        OperationsData.EnsureLoaded();
        var exports = OperationsData.Exports;
        ulong baseValue = 0;
        int limit = Math.Min(exportAmounts.Length, Math.Min(exports.Length, OperationsData.MagicDustExportRow));
        for (int i = 0; i < limit; i++)
            baseValue += (ulong)exportAmounts[i] * (ulong)Math.Max(0, exports[i].ShippingValue);

        ulong dust = exportAmounts.Length > OperationsData.MagicDustExportRow
            ? exportAmounts[OperationsData.MagicDustExportRow]
            : 0ul;

        double dustMult;
        if (baseValue == 0)
            dustMult = 1.0;
        else if (dust <= baseValue)
            dustMult = (baseValue + dust) / (double)baseValue;
        else
            dustMult = Math.Sqrt(dust / (double)baseValue) + 1.0;

        ulong total = (ulong)Math.Round(baseValue * dustMult);
        return (baseValue, dustMult, total);
    }

    /// <summary>Exact risk % formula (<c>0x1417698e0</c>, UI <c>0x14057f050</c>) scaled by <see cref="SettlersSettings.ShippingRiskMultiplier"/>.</summary>
    public static int ComputeRiskPercent(ulong shipmentValue, int portRow, int crewPower, int crewCount, double riskMultiplierSetting)
    {
        OperationsData.EnsureLoaded();
        if (crewCount <= 0) return 0;
        var port = portRow >= 1 && portRow < OperationsData.Ports.Length ? OperationsData.Ports[portRow] : OperationsData.Ports[1];
        double clampedVal = Math.Clamp((double)shipmentValue, 1.0, 5_000_000.0);
        double effVal = Math.Max(1.0, port.RiskMultiplier * clampedVal);
        double expectedCrew = Math.Max(1.0, OperationsData.ExpectedCrewValue);
        double expectedShip = Math.Max(1.0, OperationsData.ExpectedShipmentValue);
        double success = Math.Pow(Math.Max(0, crewPower) / expectedCrew, 1.1) * Math.Pow(expectedShip / effVal, 0.5) * 0.9 * 100.0;
        int successPct = Math.Clamp((int)Math.Round(success), 0, 100);
        int baseRisk = 100 - successPct;
        return Math.Clamp((int)Math.Round(baseRisk * Math.Max(0.0, riskMultiplierSetting)), 0, 100);
    }

    /// <summary>Exact travel target and duration (<c>0x1424c7c10</c>) scaled by <see cref="SettlersSettings.ShippingSpeedMultiplier"/>.</summary>
    public static (uint ProgressTarget, uint TravelSeconds) ComputeTravelTime(int portRow, int shippingSpeedStat, double speedMultiplierSetting)
    {
        OperationsData.EnsureLoaded();
        var port = portRow >= 1 && portRow < OperationsData.Ports.Length ? OperationsData.Ports[portRow] : OperationsData.Ports[1];
        uint progressTarget = (uint)(((port.DistanceFromPort / 20) + 100) * 6000);
        double rate = Math.Max(1.0, (100.0 + shippingSpeedStat) * Math.Max(0.01, speedMultiplierSetting));
        uint travelSec = (uint)Math.Max(1, Math.Ceiling(progressTarget / rate));
        return (progressTarget, travelSec);
    }

    public static InstanceVillage.TimerState[] BuildShipTimers(VillageStore.TownSnapshot snap, ulong nowUtc)
    {
        var timers = new InstanceVillage.TimerState[OperationsData.ShipCount];
        foreach (var s in snap.Ships)
        {
            if (s.ShipIndex < 0 || s.ShipIndex >= timers.Length) continue;
            if (s.State == ShipStateSailing && s.ArrivalUtc > s.DepartedUtc)
            {
                long totalSec = Math.Max(1, s.ArrivalUtc - s.DepartedUtc);
                long elapsedSec = Math.Clamp((long)nowUtc - s.DepartedUtc, 0, totalSec);
                var (target, _) = ComputeTravelTime(s.PortId, 0, 1.0);
                uint elapsedProg = (uint)Math.Clamp((long)Math.Round(target * (elapsedSec / (double)totalSec)), 0, target);
                timers[s.ShipIndex] = new InstanceVillage.TimerState(elapsedProg, target);
            }
            else if (s.State is ShipStateReturned or ShipStateCommandeered)
            {
                var (target, _) = ComputeTravelTime(s.PortId, 0, 1.0);
                timers[s.ShipIndex] = new InstanceVillage.TimerState(target, target);
            }
        }
        return timers;
    }

    public static (byte Slot1Based, byte Flag) GetShipKidnapState(VillageStore.TownSnapshot snap)
    {
        foreach (var s in snap.Ships)
            if (s.BossPending && s.State == ShipStateCommandeered && s.ShipIndex is >= 0 and < OperationsData.ShipCount)
                return ((byte)(s.ShipIndex + 1), 1);
        return (0, 0);
    }

    /// <summary>Ensures all 6 ports have 3 quotas persisted and sends <c>0x02</c> + <c>0x03</c> to the client.</summary>
    public async Task SyncAllShipsAndQuotasAsync(Player player)
    {
        OperationsData.EnsureLoaded();
        var (account, league) = VillageStore.ScopeOf(player);
        var (ships, quotasByPort) = host.MutateSnapshot(account, league, snap =>
        {
            var ensured = EnsurePortQuotas(snap);
            var byPort = new Dictionary<int, List<VillageStore.PortQuotaRow>>();
            foreach (var q in ensured.PortQuotas)
                (byPort.TryGetValue(q.PortId, out var l) ? l : byPort[q.PortId] = []).Add(q);
            return (ReferenceEquals(ensured, snap) ? null : ensured, (ensured.Ships.ToList(), byPort));
        }, persistNow: true);

        for (int i = 0; i < OperationsData.ShipCount; i++)
        {
            var ship = ships.FirstOrDefault(s => s.ShipIndex == i);
            byte portRow = ship is { State: ShipStateSailing or ShipStateReturned or ShipStateCommandeered } && ship.PortId is >= 1 and <= OperationsData.PortCount
                ? (byte)ship.PortId
                : InstanceVillage.NoneRow;
            byte failRow = ship is { EventId: > 0 } ? (byte)ship.EventId : InstanceVillage.NoneRow;
            uint[] cargo = ship is not null ? ParseCargo(ship.CargoJson) : new uint[OperationsData.ExportCount];
            await Send(player, InstanceVillage.ShipUpdate((byte)i, portRow, failRow, cargo),
                $"0x02c9 village ShipUpdate(ship={i}, port={portRow}, fail={failRow})");
        }

        for (int portRow = 1; portRow <= OperationsData.PortCount; portRow++)
        {
            var slots = BuildQuotaWireSlots(quotasByPort.GetValueOrDefault(portRow));
            await Send(player, InstanceVillage.PortQuotas((byte)(portRow - 1), slots),
                $"0x02c9 village PortQuotas(portIdx={portRow - 1})");
        }
    }

    /// <summary>C→S <c>0x02ca</c> subtag <c>0x06</c>: SendShipment (atomic check + resource deduction + ship launch in one lock).</summary>
    public async Task<bool> HandleSendShipmentAsync(Player player, InstanceVillage.SendShipmentCommand cmd)
    {
        OperationsData.EnsureLoaded();
        if (cmd.ShipIndex >= OperationsData.ShipCount) return true;
        if (cmd.PortRow < 1 || cmd.PortRow > OperationsData.PortCount) return true;

        var (account, league) = VillageStore.ScopeOf(player);
        var (_, _, shipmentValue) = ComputeShipmentValue(cmd.ExportAmounts);
        if (shipmentValue < OperationsData.MinShipmentValue || shipmentValue > OperationsData.MaxShipmentValue)
        {
            if (player.Commands is { } cmds)
                await cmds.Reply($"Shipment value ({shipmentValue:N0}) must be between {OperationsData.MinShipmentValue:N0} and {OperationsData.MaxShipmentValue:N0}.");
            return true;
        }

        var costs = new List<(int ResourceId, uint Amount)>();
        var exports = OperationsData.Exports;
        for (int i = 0; i < OperationsData.ExportCount && i < cmd.ExportAmounts.Length; i++)
        {
            uint amt = cmd.ExportAmounts[i];
            if (amt == 0) continue;
            int resRow = i < exports.Length ? exports[i].ResourceRow : OperationsData.DefaultExportToResource[i];
            costs.Add((resRow, amt));
        }

        var settings = host.Settings;
        var (progressTarget, travelSec) = ComputeTravelTime(cmd.PortRow, 0, settings.ShippingSpeedMultiplier);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long arrival = now + travelSec;
        byte shipJobRow = (byte)(OperationsData.JobShippingFirst + cmd.ShipIndex);

        var launch = host.MutateSnapshot<(string? Error, int RiskPct)>(account, league, snap =>
        {
            // Check uncollected rewards, idle ship, assigned crew, and deduct resources ATOMICALLY inside one lock.
            if (snap.RewardItems.Any(r => r.SourceKind == VillageRewardsService.SourceShipping && r.SourceIndex == cmd.ShipIndex))
                return (null, ("Claim or discard this ship's previous rewards before sending a new shipment.", 0));

            var ship = snap.Ships.FirstOrDefault(s => s.ShipIndex == cmd.ShipIndex);
            if (ship is not null && ship.State != ShipStateIdle)
                return (null, ("This ship is already busy.", 0));

            var crew = new List<(long Id, int ShipRank)>();
            foreach (var w in snap.Workers)
            {
                if (w.Status == 0 && (byte)w.AssignedJob == shipJobRow)
                {
                    var (_, _, shipRank) = StandaloneOperationsHost.ParseWorkerRanks(w.SkillsJson);
                    crew.Add((w.WorkerId, shipRank));
                }
            }
            if (crew.Count == 0)
                return (null, ("Assign at least one sailor to this ship before sending a shipment.", 0));

            if (!StandaloneOperationsHost.TryDeductResourcesFromSnapshot(snap, costs, out var afterSpend))
                return (null, ("Not enough resources in Kingsmarch storage for this shipment.", 0));

            int crewPower = crew.Sum(c => OperationsData.WorkerPowerForRank(c.ShipRank));
            int riskPct = ComputeRiskPercent(shipmentValue, cmd.PortRow, crewPower, crew.Count, settings.ShippingRiskMultiplier);
            string cargoJson = SerializeCargoBlob(cmd.ExportAmounts, 0, progressTarget);
            string crewJson = JsonSerializer.Serialize(crew.Select(c => c.Id).ToArray());

            var ships = afterSpend.Ships.Where(s => s.ShipIndex != cmd.ShipIndex).ToList();
            ships.Add(new VillageStore.ShipRow(
                cmd.ShipIndex,
                ShipStateSailing,
                cmd.PortRow,
                now,
                arrival,
                riskPct,
                (long)shipmentValue,
                cargoJson,
                crewJson,
                0,
                0,
                false));
            ships.Sort((a, b) => a.ShipIndex.CompareTo(b.ShipIndex));

            var next = afterSpend with
            {
                Town = afterSpend.Town with { NextShipmentId = afterSpend.Town.NextShipmentId + 1 },
                Ships = ships,
            };
            return (next, (null, riskPct));
        }, persistNow: true);

        if (launch.Error is { } err)
        {
            if (player.Commands is { } cmds) await cmds.Reply(err);
            return true;
        }

        await Send(player, InstanceVillage.ShipUpdate(cmd.ShipIndex, cmd.PortRow, InstanceVillage.NoneRow, cmd.ExportAmounts),
            $"0x02c9 village ShipUpdate(ship={cmd.ShipIndex}, port={cmd.PortRow}, val={shipmentValue}, risk={launch.RiskPct}%, eta={travelSec}s)");
        await host.SyncFullVillageStateAsync(player);
        return true;
    }

    /// <summary>C→S <c>0x02ca</c> subtag <c>0x07</c>: AbandonOrCancelShipment.</summary>
    public async Task<bool> HandleCancelShipmentAsync(Player player, InstanceVillage.AbandonOrCancelShipmentCommand cmd)
    {
        if (cmd.ShipIndex >= OperationsData.ShipCount) return true;
        var (account, league) = VillageStore.ScopeOf(player);
        bool cancelled = host.MutateSnapshot(account, league, snap =>
        {
            var ship = snap.Ships.FirstOrDefault(s => s.ShipIndex == cmd.ShipIndex);
            if (ship is null || ship.State != ShipStateSailing) return (null, false);
            var ships = snap.Ships.Where(s => s.ShipIndex != cmd.ShipIndex).ToList();
            ships.Add(new VillageStore.ShipRow(cmd.ShipIndex, ShipStateIdle, 0, 0, 0, 0, 0,
                SerializeCargoBlob(new uint[OperationsData.ExportCount], 0, 0), "[]", 0, 0, false));
            ships.Sort((a, b) => a.ShipIndex.CompareTo(b.ShipIndex));
            return (snap with { Ships = ships }, true);
        }, persistNow: true);

        if (!cancelled) return true;

        await Send(player, InstanceVillage.ShipUpdate(cmd.ShipIndex, InstanceVillage.NoneRow, InstanceVillage.NoneRow, new uint[OperationsData.ExportCount]),
            $"0x02c9 village ShipUpdate(ship={cmd.ShipIndex}, cancelled)");
        await host.SyncFullVillageStateAsync(player);
        return true;
    }

    /// <summary>C→S <c>0x02ca</c> subtag <c>0x0b</c>: RerollPortQuota (<c>QuotaRerollCost = 15,000</c> gold; rejected if ship en route).</summary>
    public async Task<bool> HandleRerollPortQuotaAsync(Player player, InstanceVillage.RerollPortQuotaCommand cmd)
    {
        OperationsData.EnsureLoaded();
        if (cmd.PortRow < 1 || cmd.PortRow > OperationsData.PortCount) return true;

        var (account, league) = VillageStore.ScopeOf(player);
        bool shipEnRoute = host.ReadSnapshot(account, league)?.Ships
            .Any(s => s.State == ShipStateSailing && s.PortId == cmd.PortRow) ?? false;
        if (shipEnRoute)
        {
            if (player.Commands is { } cmds) await cmds.Reply("Cannot reroll port quotas while a ship is en route to that port.");
            return true;
        }

        if (!await host.TrySpendPlayerOrTreasuryGoldAsync(player, OperationsData.QuotaRerollCost, "Kingsmarch port quota reroll"))
        {
            if (player.Commands is { } cmds) await cmds.Reply($"Need {OperationsData.QuotaRerollCost:N0} gold to reroll port quotas.");
            return true;
        }

        List<VillageStore.PortQuotaRow> newQuotas = host.MutateSnapshot(account, league, snap =>
        {
            int seed = unchecked((int)(snap.Town.ShippingSeed ^ (cmd.PortRow * 7919) ^ DateTimeOffset.UtcNow.Ticks));
            var generated = GeneratePortQuotas(cmd.PortRow, new Random(seed));
            var all = snap.PortQuotas.Where(q => q.PortId != cmd.PortRow).Concat(generated)
                .OrderBy(q => q.PortId).ThenBy(q => q.QuotaIndex).ToList();
            return (snap with
            {
                Town = snap.Town with { ShippingSeed = snap.Town.ShippingSeed + 1 },
                PortQuotas = all,
            }, generated);
        }, persistNow: true);

        await Send(player, InstanceVillage.PortQuotas((byte)(cmd.PortRow - 1), BuildQuotaWireSlots(newQuotas)),
            $"0x02c9 village PortQuotas(portIdx={cmd.PortRow - 1}, rerolled)");
        await host.SyncFullVillageStateAsync(player);
        return true;
    }

    /// <summary>C→S <c>0x02ca</c> subtag <c>0x08</c>: RescueCommandeeredShip (opens Sailor's Folly portal to Admiral Valerius).</summary>
    public async Task<bool> HandleRescueShipAsync(Player player)
    {
        var (account, league) = VillageStore.ScopeOf(player);
        int shipIdx = FindCommandeeredShip(account, league);
        if (shipIdx < 0) return true;
        await host.OpenPirateBossPortalAsync(player, shipIdx);
        return true;
    }

    /// <summary>C→S <c>0x02ca</c> subtag <c>0x09</c>: PayShipRansom (<c>RansomCost = 50,000</c> gold).</summary>
    public async Task<bool> HandlePayShipRansomAsync(Player player)
    {
        OperationsData.EnsureLoaded();
        var (account, league) = VillageStore.ScopeOf(player);
        int shipIdx = FindCommandeeredShip(account, league);
        if (shipIdx < 0) return true;

        if (!await host.TrySpendPlayerOrTreasuryGoldAsync(player, OperationsData.RansomCost, "Kingsmarch ship ransom"))
        {
            if (player.Commands is { } cmds) await cmds.Reply($"Need {OperationsData.RansomCost:N0} gold to ransom the commandeered ship.");
            return true;
        }

        await CompleteCommandeeredShipAsync(player, shipIdx, rescueBonus: false);
        return true;
    }

    /// <summary>C→S <c>0x02ca</c> subtag <c>0x0a</c>: AbandonCommandeeredShip (loses crew and shipment).</summary>
    public async Task<bool> HandleAbandonShipAsync(Player player)
    {
        var (account, league) = VillageStore.ScopeOf(player);
        int shipIdx = FindCommandeeredShip(account, league);
        if (shipIdx < 0) return true;

        await host.LoseAllWorkersOnJobAsync(player, account, league,
            (byte)(OperationsData.JobShippingFirst + shipIdx),
            (byte)(OperationsData.JobLostAtSeaFirst + shipIdx));

        host.MutateSnapshot(account, league, snap =>
        {
            var ships = snap.Ships.Where(s => s.ShipIndex != shipIdx).ToList();
            ships.Add(new VillageStore.ShipRow(shipIdx, ShipStateIdle, 0, 0, 0, 0, 0,
                SerializeCargoBlob(new uint[OperationsData.ExportCount], 0, 0), "[]", 0, 0, false));
            ships.Sort((a, b) => a.ShipIndex.CompareTo(b.ShipIndex));
            return (snap with { Ships = ships }, true);
        }, persistNow: true);

        await Send(player, InstanceVillage.ShipUpdate((byte)shipIdx, InstanceVillage.NoneRow, InstanceVillage.NoneRow, new uint[OperationsData.ExportCount]),
            $"0x02c9 village ShipUpdate(ship={shipIdx}, abandoned)");
        await host.SyncFullVillageStateAsync(player);
        return true;
    }

    /// <summary>Called when Admiral Valerius is defeated in Sailor's Folly — rescues crew, frees ship, and delivers bonus rewards.</summary>
    public async Task ResolvePirateRescueAsync(Player player)
    {
        var (account, league) = VillageStore.ScopeOf(player);
        int shipIdx = FindCommandeeredShip(account, league);
        if (shipIdx < 0) return;
        await CompleteCommandeeredShipAsync(player, shipIdx, rescueBonus: true);
    }

    /// <summary>Atomically claims arrived voyages and resolves failures, ambushes, quotas, and rewards (never double-resolves).</summary>
    public async Task<bool> TickAsync(Player player, double elapsedSeconds)
    {
        OperationsData.EnsureLoaded();
        var (account, league) = VillageStore.ScopeOf(player);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Atomically transition arrived Sailing ships -> ShipStateResolving inside one lock so concurrent ticks cannot double-award.
        List<VillageStore.ShipRow> arrived = host.MutateSnapshot(account, league, snap =>
        {
            var due = snap.Ships.Where(s => s.State == ShipStateSailing && s.ArrivalUtc > 0 && now >= s.ArrivalUtc).ToList();
            if (due.Count == 0) return (null, new List<VillageStore.ShipRow>());
            var dueIds = due.Select(s => s.ShipIndex).ToHashSet();
            var nextShips = snap.Ships.Select(s => dueIds.Contains(s.ShipIndex) ? s with { State = ShipStateResolving } : s).ToList();
            return (snap with { Ships = nextShips }, due);
        }, persistNow: false);

        if (arrived.Count == 0) return false;

        foreach (var ship in arrived)
            await ResolveArrivedShipAsync(player, account, league, ship, now);

        return true;
    }

    async Task ResolveArrivedShipAsync(Player player, string account, string league, VillageStore.ShipRow ship, long now)
    {
        var settings = host.Settings;
        var rng = Random.Shared;
        uint[] cargo = ParseCargo(ship.CargoJson);

        // 1. Check Pirate Commandeering (Row 1 ShipCommandeered) — only if no other ship is already commandeered.
        bool anotherCommandeered = FindCommandeeredShip(account, league) >= 0;
        double ambushChance = settings.BossAmbushChanceMultiplier <= 0 || anotherCommandeered
            ? 0.0
            : Math.Clamp((0.02 + ship.RiskPercent * 0.0025) * settings.BossAmbushChanceMultiplier, 0.0, 0.85);

        if (ambushChance > 0 && rng.NextDouble() < ambushChance)
        {
            var (target, _) = ComputeTravelTime(ship.PortId, 0, 1.0);
            host.MutateSnapshot(account, league, snap =>
            {
                var ships = snap.Ships.Where(s => s.ShipIndex != ship.ShipIndex).ToList();
                ships.Add(ship with
                {
                    State = ShipStateCommandeered,
                    EventId = 1,
                    RansomGold = OperationsData.RansomCost,
                    BossPending = true,
                    CargoJson = SerializeCargoBlob(cargo, target, target),
                });
                ships.Sort((a, b) => a.ShipIndex.CompareTo(b.ShipIndex));
                return (snap with { Ships = ships }, true);
            }, persistNow: true);

            if (OperationsSubsystem.IsVillageArea(player))
            {
                await Send(player, InstanceVillage.ShipUpdate((byte)ship.ShipIndex, (byte)ship.PortId, 1, cargo),
                    $"0x02c9 village ShipUpdate(ship={ship.ShipIndex}, commandeered)");
                await Send(player, InstanceVillage.ToastNotificationA(1, (byte)ship.ShipIndex, 1),
                    "0x02c9 village ToastNotificationA(ShipCommandeered)");
            }
            return;
        }

        // 2. Roll failure risk against VillageShippingFailures rows 2..20 (CRITICAL: Resource indexes VillageExports 0..15).
        int failureRow = 0;
        double rewardMultiplier = 1.0;

        if (ship.RiskPercent > 0 && rng.Next(100) < ship.RiskPercent)
        {
            var eligible = new List<OperationsData.ShippingFailure>();
            for (int r = 2; r < OperationsData.Failures.Length; r++)
            {
                var f = OperationsData.Failures[r];
                if (f.ExportIndices.Length > 0)
                {
                    bool hasMatchingCargo = false;
                    foreach (int expIdx in f.ExportIndices)
                        if (expIdx >= 0 && expIdx < cargo.Length && cargo[expIdx] > 0) { hasMatchingCargo = true; break; }
                    if (!hasMatchingCargo) continue;
                }
                eligible.Add(f);
            }

            if (eligible.Count > 0)
            {
                var failure = eligible[rng.Next(eligible.Count)];
                failureRow = failure.Row;

                // Row 18: ShipmentDelayed100% — extend voyage and keep sailing!
                if (failure.DelayPercent > 0)
                {
                    long origDur = Math.Max(60, ship.ArrivalUtc - ship.DepartedUtc);
                    long extra = Math.Max(30, origDur * failure.DelayPercent / 100);
                    host.MutateSnapshot(account, league, snap =>
                    {
                        var ships = snap.Ships.Where(s => s.ShipIndex != ship.ShipIndex).ToList();
                        ships.Add(ship with
                        {
                            State = ShipStateSailing,
                            ArrivalUtc = now + extra,
                            EventId = failure.Row,
                            RiskPercent = 0, // Do not re-roll failure after delay
                        });
                        ships.Sort((a, b) => a.ShipIndex.CompareTo(b.ShipIndex));
                        return (snap with { Ships = ships }, true);
                    }, persistNow: true);

                    if (OperationsSubsystem.IsVillageArea(player))
                        await Send(player, InstanceVillage.ShipUpdate((byte)ship.ShipIndex, (byte)ship.PortId, (byte)failure.Row, cargo),
                            $"0x02c9 village ShipUpdate(ship={ship.ShipIndex}, delayed +{extra}s)");
                    return;
                }

                if (failure.ResourceLostPercent > 0)
                {
                    if (failure.ExportIndices.Length > 0)
                    {
                        foreach (int expIdx in failure.ExportIndices)
                            if (expIdx >= 0 && expIdx < cargo.Length)
                                cargo[expIdx] = (uint)(cargo[expIdx] * (100L - failure.ResourceLostPercent) / 100L);
                    }
                    else
                    {
                        for (int i = 0; i < cargo.Length; i++)
                            cargo[i] = (uint)(cargo[i] * (100L - failure.ResourceLostPercent) / 100L);
                    }
                }

                if (failure.PortBonusLostPercent > 0)
                    rewardMultiplier *= Math.Max(0.0, (100 - failure.PortBonusLostPercent) / 100.0);

                if (failure.CrewMembersLost > 0)
                {
                    var crew = host.GetWorkersForJob(account, league, (byte)(OperationsData.JobShippingFirst + ship.ShipIndex));
                    for (int k = 0; k < failure.CrewMembersLost && k < crew.Count; k++)
                    {
                        var victim = crew[rng.Next(crew.Count)];
                        await host.KillWorkerAsync(player, account, league, victim.WorkerId,
                            (byte)(OperationsData.JobLostAtSeaFirst + ship.ShipIndex));
                    }
                }
            }
        }

        await FinalizeShipmentRewardsAsync(player, account, league, ship, cargo, failureRow, rewardMultiplier);
    }

    async Task CompleteCommandeeredShipAsync(Player player, int shipIdx, bool rescueBonus)
    {
        var (account, league) = VillageStore.ScopeOf(player);
        var ship = host.MutateSnapshot<VillageStore.ShipRow?>(account, league, snap =>
        {
            var found = snap.Ships.FirstOrDefault(s => s.ShipIndex == shipIdx && s.BossPending && s.State == ShipStateCommandeered);
            if (found is null) return (null, null);
            var ships = snap.Ships.Select(s => s.ShipIndex == shipIdx ? s with { State = ShipStateResolving, BossPending = false } : s).ToList();
            return (snap with { Ships = ships }, found);
        }, persistNow: false);

        if (ship is null) return;
        uint[] cargo = ParseCargo(ship.CargoJson);
        double bonus = rescueBonus ? 1.35 : 1.0;
        await FinalizeShipmentRewardsAsync(player, account, league, ship, cargo, failureRow: 0, rewardMultiplier: bonus);
        if (OperationsSubsystem.IsVillageArea(player))
            await host.SyncFullVillageStateAsync(player);
    }

    async Task FinalizeShipmentRewardsAsync(
        Player player,
        string account,
        string league,
        VillageStore.ShipRow ship,
        uint[] survivingCargo,
        int failureRow,
        double rewardMultiplier)
    {
        var (_, dustMult, survivingValue) = ComputeShipmentValue(survivingCargo);
        var (quotaBonusMult, updatedQuotas) = ApplyQuotaProgress(account, league, ship.PortId, survivingCargo);

        double finalValue = survivingValue * rewardMultiplier * quotaBonusMult * Math.Max(0.0, host.Settings.ShippingRewardMultiplier);
        if (finalValue > 0)
        {
            var rewards = GenerateShipmentRewards(player, ship.PortId, survivingCargo, finalValue, dustMult, quotaBonusMult > 1.0, Random.Shared);
            OperationsSubsystem.Rewards.AddRewards(account, league, VillageRewardsService.SourceShipping, ship.ShipIndex, rewards, ItemsFor(player).Mods);
        }

        bool hasRewards = OperationsSubsystem.Rewards.HasAnyRewards(account, league, VillageRewardsService.SourceShipping, ship.ShipIndex);
        var (target, _) = ComputeTravelTime(ship.PortId, 0, 1.0);
        uint[] wireCargo = hasRewards ? survivingCargo : new uint[OperationsData.ExportCount];
        byte wirePort = hasRewards && ship.PortId is >= 1 and <= OperationsData.PortCount
            ? (byte)ship.PortId
            : InstanceVillage.NoneRow;

        host.MutateSnapshot(account, league, snap =>
        {
            var ships = snap.Ships.Where(s => s.ShipIndex != ship.ShipIndex).ToList();
            if (hasRewards)
            {
                ships.Add(new VillageStore.ShipRow(
                    ship.ShipIndex, ShipStateReturned, ship.PortId,
                    ship.DepartedUtc, ship.ArrivalUtc, ship.RiskPercent, ship.ShipmentValue,
                    SerializeCargoBlob(survivingCargo, target, target), "[]", failureRow, 0, false));
            }
            else
            {
                ships.Add(new VillageStore.ShipRow(ship.ShipIndex, ShipStateIdle, 0, 0, 0, 0, 0,
                    SerializeCargoBlob(new uint[OperationsData.ExportCount], 0, 0), "[]", failureRow, 0, false));
            }
            ships.Sort((a, b) => a.ShipIndex.CompareTo(b.ShipIndex));
            return (snap with { Ships = ships }, true);
        }, persistNow: true);

        if (OperationsSubsystem.IsVillageArea(player))
        {
            byte wireFail = failureRow > 0 ? (byte)failureRow : InstanceVillage.NoneRow;
            await Send(player, InstanceVillage.ShipUpdate((byte)ship.ShipIndex, wirePort, wireFail, wireCargo),
                $"0x02c9 village ShipUpdate(ship={ship.ShipIndex}, {(hasRewards ? "returned-rewards" : "idle")}, port={wirePort}, fail={wireFail})");
            if (updatedQuotas is not null && ship.PortId is >= 1 and <= OperationsData.PortCount)
            {
                await Send(player, InstanceVillage.PortQuotas((byte)(ship.PortId - 1), BuildQuotaWireSlots(updatedQuotas)),
                    $"0x02c9 village PortQuotas(portIdx={ship.PortId - 1}, updated)");
            }
            await host.SyncFullVillageStateAsync(player);
            await OperationsSubsystem.Rewards.SyncRewardVisibilityAsync(player);
        }
    }

    /// <summary>Called when the last shipping reward item for <paramref name="shipIndex"/> is claimed or discarded — returns the ship to Docked/Idle.</summary>
    public async Task OnRewardsEmptiedAsync(Player player, int shipIndex)
    {
        if (shipIndex < 0 || shipIndex >= OperationsData.ShipCount) return;
        var (account, league) = VillageStore.ScopeOf(player);
        bool cleared = host.MutateSnapshot(account, league, snap =>
        {
            var existing = snap.Ships.FirstOrDefault(s => s.ShipIndex == shipIndex);
            if (existing is null || existing.State != ShipStateReturned) return (null, false);
            var ships = snap.Ships.Where(s => s.ShipIndex != shipIndex).ToList();
            ships.Add(new VillageStore.ShipRow(shipIndex, ShipStateIdle, 0, 0, 0, 0, 0,
                SerializeCargoBlob(new uint[OperationsData.ExportCount], 0, 0), "[]", 0, 0, false));
            ships.Sort((a, b) => a.ShipIndex.CompareTo(b.ShipIndex));
            return (snap with { Ships = ships }, true);
        }, persistNow: true);

        if (cleared && OperationsSubsystem.IsVillageArea(player))
        {
            await Send(player, InstanceVillage.ShipUpdate((byte)shipIndex, InstanceVillage.NoneRow, InstanceVillage.NoneRow, new uint[OperationsData.ExportCount]),
                $"0x02c9 village ShipUpdate(ship={shipIndex}, idle-after-claim)");
            await host.SyncFullVillageStateAsync(player);
        }
    }

    (double BonusMultiplier, List<VillageStore.PortQuotaRow>? UpdatedQuotas) ApplyQuotaProgress(
        string account, string league, int portRow, uint[] cargo)
    {
        if (portRow < 1 || portRow > OperationsData.PortCount) return (1.0, null);
        return host.MutateSnapshot<(double, List<VillageStore.PortQuotaRow>?)>(account, league, snap =>
        {
            var ensured = EnsurePortQuotas(snap);
            var portQuotas = ensured.PortQuotas.Where(q => q.PortId == portRow).OrderBy(q => q.QuotaIndex).ToList();
            if (portQuotas.Count == 0) return (ReferenceEquals(ensured, snap) ? null : ensured, (1.0, null));

            double bonus = 1.0;
            bool changed = false;
            var nextPortQuotas = new List<VillageStore.PortQuotaRow>(portQuotas.Count);
            foreach (var q in portQuotas)
            {
                int expIdx = q.ResourceId;
                uint shipped = expIdx >= 0 && expIdx < cargo.Length ? cargo[expIdx] : 0u;
                long prev = q.FulfilledAmount;
                long now = Math.Min(q.RequiredAmount, prev + shipped);
                if (now > prev)
                {
                    changed = true;
                    double quotaEff = (100.0 + OperationsData.PortQuotaEffectivenessPct) / 100.0;
                    double frac = q.RequiredAmount > 0 ? (now - prev) / (double)q.RequiredAmount : 0;
                    bonus += frac * (q.BonusPercent / 100.0) * quotaEff;
                }
                nextPortQuotas.Add(q with { FulfilledAmount = now });
            }

            if (nextPortQuotas.All(q => q.RequiredAmount > 0 && q.FulfilledAmount >= q.RequiredAmount))
            {
                bonus += 0.25;
                int seed = unchecked((int)(ensured.Town.ShippingSeed ^ (portRow * 3571) ^ DateTimeOffset.UtcNow.Ticks));
                nextPortQuotas = GeneratePortQuotas(portRow, new Random(seed));
                changed = true;
            }

            if (!changed) return (ReferenceEquals(ensured, snap) ? null : ensured, (1.0, null));

            var all = ensured.PortQuotas.Where(q => q.PortId != portRow).Concat(nextPortQuotas)
                .OrderBy(q => q.PortId).ThenBy(q => q.QuotaIndex).ToList();
            var next = ensured with
            {
                Town = ensured.Town with { ShippingSeed = ensured.Town.ShippingSeed + 1 },
                PortQuotas = all,
            };
            return (next, (bonus, nextPortQuotas));
        }, persistNow: true);
    }

    static List<Item> GenerateShipmentRewards(
        Player player,
        int portRow,
        uint[] cargo,
        double effectiveValue,
        double dustMult,
        bool hadQuotaBonus,
        Random rng)
    {
        var catalog = ItemsFor(player);
        var rolls = RollsFor(player);
        var uniques = LeagueFor(player).HardMode ? HardModeUniques : Uniques;
        var results = new List<Item>();

        var port = portRow >= 1 && portRow < OperationsData.Ports.Length ? OperationsData.Ports[portRow] : OperationsData.Ports[1];
        bool isKarui = port.IsKarui;

        ulong oreVal = 0, cropVal = 0;
        for (int i = 0; i < 10 && i < cargo.Length; i++)
            oreVal += (ulong)cargo[i] * (ulong)OperationsData.Exports[i].ShippingValue;
        for (int i = 10; i < 15 && i < cargo.Length; i++)
            cropVal += (ulong)cargo[i] * (ulong)OperationsData.Exports[i].ShippingValue;

        double totalResourceVal = Math.Max(1.0, oreVal + cropVal);
        double cropShare = cropVal / totalResourceVal;
        double oreShare = oreVal / totalResourceVal;

        double valueScale = Math.Log10(Math.Max(10_000.0, effectiveValue)) - 3.5;
        int totalRolls = Math.Clamp((int)Math.Round(valueScale * 9.5 * (hadQuotaBonus ? 1.15 : 1.0)), 3, 48);

        if (OperationsData.ActiveRunegrafts.Length > 0)
        {
            double runegraftBudget = (effectiveValue / 250_000.0) * (isKarui ? 0.85 : 1.35);
            int runegraftCount = Math.Clamp((int)Math.Floor(runegraftBudget) + (rng.NextDouble() < (runegraftBudget % 1.0) ? 1 : 0),
                effectiveValue >= 100_000 ? 1 : 0, 8);
            for (int i = 0; i < runegraftCount; i++)
            {
                var rg = OperationsData.ActiveRunegrafts[rng.Next(OperationsData.ActiveRunegrafts.Length)];
                if (catalog.ByHash(rg.Hash) is { } bt && bt.Supported)
                    results.Add(ItemCatalog.Create(bt));
            }
        }

        int currencyRolls = Math.Clamp((int)Math.Round(totalRolls * (0.35 + cropShare * 0.45)), 2, 32);
        var currencyCounts = new Dictionary<uint, (ItemCatalog.BaseType Type, int Count)>();
        for (int i = 0; i < currencyRolls; i++)
        {
            double tierRoll = rng.NextDouble();
            string[] pool = effectiveValue >= 2_000_000 && tierRoll < 0.14 ? HighCurrencyNames
                : effectiveValue >= 300_000 && tierRoll < 0.42 ? MidCurrencyNames
                : CommonCurrencyNames;

            string name = pool[rng.Next(pool.Length)];
            if (name == "Mirror Shard" && effectiveValue < 15_000_000) name = "Divine Orb";
            if (catalog.Find(name) is not { Supported: true } bt) continue;
            int qty = pool == HighCurrencyNames ? 1
                : pool == MidCurrencyNames ? rng.Next(1, 4)
                : rng.Next(2, 9);
            if (currencyCounts.TryGetValue(bt.Hash, out var cur))
                currencyCounts[bt.Hash] = (bt, cur.Count + qty);
            else
                currencyCounts[bt.Hash] = (bt, qty);
        }
        foreach (var (_, (bt, totalQty)) in currencyCounts)
        {
            int left = totalQty;
            int maxStack = Math.Max(1, (int)bt.MaxStack);
            while (left > 0)
            {
                int stack = Math.Min(left, maxStack);
                results.Add(ItemCatalog.Create(bt) with { StackSize = (ushort)stack });
                left -= stack;
            }
        }

        int equipRolls = Math.Clamp((int)Math.Round(totalRolls * (0.20 + oreShare * 0.45)), 1, 16);
        int itemLevel = Math.Clamp(Math.Max(75, (int)player.Pools.Level + 2), 68, 86);
        var equipBases = catalog.All.Where(t =>
            t.Supported && !t.Stackable && t.DropLevel <= itemLevel
            && OperationsData.IsRecombinableClass(t.Class)
            && t.Class != "AtlasRelic" && t.Class != "FishingRod").ToList();

        double uniqueChance = Math.Clamp(0.05 + Math.Max(0, dustMult - 1.0) * 0.12 + (isKarui ? 0.04 : 0.0), 0.05, 0.35);
        for (int i = 0; i < equipRolls && equipBases.Count > 0; i++)
        {
            if (rng.NextDouble() < uniqueChance && uniques.All.Count > 0)
            {
                var def = uniques.All[rng.Next(uniques.All.Count)];
                if (uniques.Create(def, rng) is { } uniqueItem && catalog.ByHash(uniqueItem.TypeHash) is { Supported: true })
                {
                    results.Add(uniqueItem with { ItemLevel = (byte)itemLevel, Identified = true });
                    continue;
                }
            }

            var baseType = equipBases[rng.Next(equipBases.Count)];
            var rolled = rolls.Roll(ItemCatalog.Create(baseType) with { ItemLevel = (byte)itemLevel }, ItemRarity.Rare, rng);
            results.Add(rolled with { Identified = true });
        }

        return results;
    }

    int FindCommandeeredShip(string account, string league)
    {
        var snap = host.ReadSnapshot(account, league);
        if (snap is null) return -1;
        foreach (var s in snap.Ships)
            if (s.BossPending && s.State == ShipStateCommandeered) return s.ShipIndex;
        return -1;
    }

    static VillageStore.TownSnapshot EnsurePortQuotas(VillageStore.TownSnapshot snap)
    {
        var existingPorts = snap.PortQuotas.Select(q => q.PortId).ToHashSet();
        bool missing = false;
        for (int p = 1; p <= OperationsData.PortCount; p++)
            if (!existingPorts.Contains(p)) { missing = true; break; }
        if (!missing) return snap;

        var list = snap.PortQuotas.ToList();
        for (int p = 1; p <= OperationsData.PortCount; p++)
        {
            if (existingPorts.Contains(p)) continue;
            int seed = unchecked((int)(snap.Town.ShippingSeed ^ (p * 4219)));
            list.AddRange(GeneratePortQuotas(p, new Random(seed)));
        }
        list.Sort((a, b) => a.PortId != b.PortId ? a.PortId.CompareTo(b.PortId) : a.QuotaIndex.CompareTo(b.QuotaIndex));
        return snap with { PortQuotas = list };
    }

    static List<VillageStore.PortQuotaRow> GeneratePortQuotas(int portRow, Random rng)
    {
        var pool = Enumerable.Range(0, 15).ToList();
        var port = portRow >= 1 && portRow < OperationsData.Ports.Length ? OperationsData.Ports[portRow] : OperationsData.Ports[1];
        double distScale = 1.0 + port.DistanceFromPort / 1200.0;
        double tierScale = Math.Max(1.0, OperationsData.QuotaTier / 4.0);

        var quotas = new List<VillageStore.PortQuotaRow>(OperationsData.QuotasPerPort);
        for (int i = 0; i < OperationsData.QuotasPerPort; i++)
        {
            int pickIdx = rng.Next(pool.Count);
            int expRow = pool[pickIdx];
            pool.RemoveAt(pickIdx);
            int unitVal = Math.Max(4, OperationsData.Exports[expRow].ShippingValue);
            long targetVal = (long)(20_000 * distScale * tierScale * (0.75 + rng.NextDouble() * 0.75));
            long req = Math.Clamp((targetVal / unitVal / 50) * 50, 50, 250_000);
            int bonusPct = 20 + i * 10 + (port.RiskMultiplier > 1.0 ? 15 : 0);
            quotas.Add(new VillageStore.PortQuotaRow(portRow, i, expRow, req, 0, bonusPct));
        }
        return quotas;
    }

    static InstanceVillage.PortQuotaSlot[] BuildQuotaWireSlots(List<VillageStore.PortQuotaRow>? rows)
    {
        var slots = new InstanceVillage.PortQuotaSlot[OperationsData.QuotasPerPort];
        if (rows is null) return slots;
        foreach (var r in rows)
        {
            if (r.QuotaIndex < 0 || r.QuotaIndex >= slots.Length) continue;
            slots[r.QuotaIndex] = new InstanceVillage.PortQuotaSlot(
                (byte)Math.Clamp(r.ResourceId, 0, 15),
                (uint)Math.Clamp(r.RequiredAmount, 0, 0x0fffffffL),
                (uint)Math.Clamp(r.FulfilledAmount, 0, 0x0fffffffL));
        }
        return slots;
    }
}
