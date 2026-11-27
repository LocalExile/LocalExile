using GameServer.Core.Protocol.Instance;
using G = global::Game;

namespace GameServer.Modules.Settlers.Town;

/// <summary>
/// Core Kingsmarch idle economy and worker management:
/// handles C→S <c>0x02ca</c> (ruleset <c>0x9b</c>) subtags <c>0x00</c> RerollRecruits, <c>0x01</c> HireRecruit,
/// <c>0x02</c> FireWorker, <c>0x03</c> AssignWorker, <c>0x04</c> AssignFarm, <c>0x18</c> DepositGold, and <c>0x19</c> WithdrawGold,
/// plus exact 3.29.3c worker generation and Mining/Smelting/Farming production simulation (<c>0x1424c2e20</c>, <c>0x1424c5250</c>).
/// </summary>
public static class VillageWorkersAndEconomy
{
    // Active worker job types generated in Raulf's recruitment pool (Mining..Shipping: 1..6).
    static readonly byte[] RecruitableJobTypes =
    [
        VillageData.JobTypeMining,
        VillageData.JobTypeSmelting,
        VillageData.JobTypeFarming,
        VillageData.JobTypeDisenchanting,
        VillageData.JobTypeMapping,
        VillageData.JobTypeShipping,
    ];

    /// <summary>
    /// Handles core town C→S <c>0x02ca</c> subtags (<c>0x00..0x04</c>, <c>0x18</c>, <c>0x19</c>).
    /// Returns <c>false</c> for shipping/mapping/reward subtags so sibling handlers receive them.
    /// </summary>
    public static async Task<bool> HandleCommandAsync(Player player, ReadOnlyMemory<byte> payload)
    {
        if (!InstanceVillage.TryReadCommand(payload.Span, out var command) || command is null)
            return false;

        switch (command)
        {
            case InstanceVillage.RerollRecruitsCommand cmd:
                await HandleRerollRecruitsAsync(player, cmd.IsSuperReroll);
                return true;
            case InstanceVillage.HireRecruitCommand cmd:
                await HandleHireRecruitAsync(player, cmd.RecruitIndex);
                return true;
            case InstanceVillage.FireWorkerCommand cmd:
                await HandleFireWorkerAsync(player, cmd.WorkerIndex);
                return true;
            case InstanceVillage.AssignWorkerCommand cmd:
                await HandleAssignWorkerAsync(player, cmd.WorkerIndex, cmd.VillageJobRow);
                return true;
            case InstanceVillage.AssignFarmCommand cmd:
                await HandleAssignFarmAsync(player, cmd.PlotIndex, cmd.VillageResourceRow);
                return true;
            case InstanceVillage.DepositGoldCommand cmd:
                await HandleDepositGoldAsync(player, cmd.Amount);
                return true;
            case InstanceVillage.WithdrawGoldCommand cmd:
                await HandleWithdrawGoldAsync(player, cmd.Amount);
                return true;
            default:
                return false;
        }
    }

    // ---- 0x00 RerollRecruits ----------------------------------------------------------------------------------------

    static async Task HandleRerollRecruitsAsync(Player player, bool isSuper)
    {
        if (isSuper && !VillageData.CanSuperReroll) return;
        uint cost = VillageData.RerollCost(isSuper);
        if (!await VillageTownService.TrySpendGoldAsync(player, cost, isSuper ? "super reroll recruits" : "reroll recruits"))
            return;

        var state = VillageTownService.GetState(player);
        long now = VillageTownService.ClockUtcSeconds();
        byte[] recruitPkt;
        lock (state.Gate)
        {
            VillageTownService.SimulateLocked(state, now);
            PopulateRecruitsLocked(state, isSuper, SettlersModule.Settings, Random.Shared);
            state.LastRerollUtc = now;
            state.RerollCostGold = VillageData.RerollCost(false);
            state.SuperRerollCostGold = VillageData.RerollCost(true);
            VillageTownService.SaveLocked(state);
            recruitPkt = InstanceVillage.RecruitPool(state.RecruitPoolFlag, state.ToWireRecruits());
        }

        await Program.Send(player, recruitPkt, $"0x02c9[0x01] recruit pool ({(isSuper ? "super" : "normal")} reroll)");
        await VillageTownService.SyncStateAsync(player, state);
    }

    // ---- 0x01 HireRecruit -------------------------------------------------------------------------------------------

    static async Task HandleHireRecruitAsync(Player player, byte recruitIndex)
    {
        var state = VillageTownService.GetState(player);
        var stash = G.PersonalStashOf(player);
        long now = VillageTownService.ClockUtcSeconds();

        TownWorker? hired = null;
        uint hireCost = 0;
        uint goldAfter = 0;
        byte[]? addWorkerPkt = null;
        byte[]? recruitPkt = null;
        bool firstHire = false;

        lock (state.Gate)
        {
            VillageTownService.SimulateLocked(state, now);
            if (recruitIndex >= state.Recruits.Count || state.Workers.Count >= VillageData.MaxWorkers)
                return;

            var recruit = state.Recruits[recruitIndex];
            hireCost = recruit.HireCost;
            if (stash.TrySpendGold(hireCost) is not uint after)
                return;

            goldAfter = after;
            state.Recruits.RemoveAt(recruitIndex);
            hired = new TownWorker
            {
                WorkerId = state.NextWorkerId++,
                FirstNameWordKey = recruit.FirstNameWordKey,
                LastNameWordKey = recruit.LastNameWordKey,
                Name = recruit.Name,
                WagePerHour = recruit.WagePerHour,
                AssignedJobRow = VillageData.JobIdling,
                Status = 0,
                Skills = [.. recruit.Skills],
            };
            state.Workers.Add(hired);
            if (!state.TutorialFlags.HasFlag(VillageTutorialFlags.FirstRecruitHired))
            {
                state.TutorialFlags |= VillageTutorialFlags.FirstRecruitHired;
                firstHire = true;
            }
            VillageTownService.SaveLocked(state);
            addWorkerPkt = InstanceVillage.AddWorker(hired.ToWireHired());
            recruitPkt = InstanceVillage.RecruitPool(state.RecruitPoolFlag, state.ToWireRecruits());
        }

        await Program.Send(player, InstanceEntities.Gold(goldAfter), $"0x02aa hire worker {hired.Name} (-{hireCost}g, now {goldAfter}g)");
        await Program.Send(player, addWorkerPkt, $"0x02c9[0x05] add worker {hired.Name}");
        await Program.Send(player, recruitPkt, "0x02c9[0x01] recruit pool updated after hire");
        if (firstHire) await KaruiShoresIntro.OnFirstRecruitHiredAsync(player);
        await VillageTownService.SyncStateAsync(player, state);
    }

    // ---- 0x02 FireWorker --------------------------------------------------------------------------------------------

    static async Task HandleFireWorkerAsync(Player player, byte workerIndex)
    {
        var state = VillageTownService.GetState(player);
        long now = VillageTownService.ClockUtcSeconds();
        bool removed = false;

        lock (state.Gate)
        {
            VillageTownService.SimulateLocked(state, now);
            if (workerIndex >= state.Workers.Count) return;
            var worker = state.Workers[workerIndex];
            // Do not allow firing workers currently at sea, running a map, or kidnapped.
            if (IsWorkerLockedInActiveOperationLocked(state, worker)) return;
            state.Workers.RemoveAt(workerIndex);
            VillageTownService.SaveLocked(state);
            removed = true;
        }

        if (!removed) return;
        await Program.Send(player, InstanceVillage.RemoveWorker(workerIndex), $"0x02c9[0x06] remove worker {workerIndex}");
        await VillageTownService.SyncStateAsync(player, state);
    }

    // ---- 0x03 AssignWorker ------------------------------------------------------------------------------------------

    static async Task HandleAssignWorkerAsync(Player player, byte workerIndex, byte rawJobRow)
    {
        var state = VillageTownService.GetState(player);
        long now = VillageTownService.ClockUtcSeconds();
        byte targetJob = rawJobRow == InstanceVillage.NoneRow ? VillageData.JobIdling : rawJobRow;
        bool updated = false;
        bool firstAssign = false;

        lock (state.Gate)
        {
            VillageTownService.SimulateLocked(state, now);
            if (workerIndex >= state.Workers.Count) return;
            var worker = state.Workers[workerIndex];
            if (worker.AssignedJobRow == targetJob) return;
            if (IsWorkerLockedInActiveOperationLocked(state, worker)) return;

            if (targetJob != VillageData.JobIdling)
            {
                if (!CanAssignWorkerToJobLocked(state, worker, targetJob))
                    return;
            }

            worker.AssignedJobRow = targetJob;
            if (targetJob != VillageData.JobIdling && !state.TutorialFlags.HasFlag(VillageTutorialFlags.FirstWorkerAssigned))
            {
                state.TutorialFlags |= VillageTutorialFlags.FirstWorkerAssigned;
                firstAssign = true;
            }
            VillageTownService.SaveLocked(state);
            updated = true;
        }

        if (!updated) return;
        await Program.Send(player, InstanceVillage.SetWorkerJob(workerIndex, targetJob), $"0x02c9[0x07] set worker {workerIndex} -> job {targetJob}");
        if (firstAssign) await KaruiShoresIntro.OnFirstWorkerAssignedAsync(player, targetJob);
        await VillageTownService.SyncStateAsync(player, state);
    }

    /// <summary>Validates <c>VillageJobs</c> row, skill qualification, and <c>VillageBaseStats</c> worker caps.</summary>
    public static bool CanAssignWorkerToJobLocked(VillageTownState state, TownWorker worker, byte targetJob)
    {
        if (targetJob >= VillageData.Jobs.Count) return false;
        var job = VillageData.Jobs[targetJob];
        // Players cannot manually assign workers to LostAtSea (8) or LostMapping (9).
        if (job.JobType is VillageData.JobTypeLostAtSea or VillageData.JobTypeLostMapping) return false;

        // Check unlock stat gate (e.g. rank / plot / ship / device count in VillageBaseStats).
        if (job.RequiredStat is int reqStat && VillageData.GetBaseStat(reqStat, 0) < job.RequiredStatValue)
            return false;

        // Cannot assign to a ship currently sailing or commandeered, or a map device currently running or ambushed.
        if (job.JobType == VillageData.JobTypeShipping && job.SubIndex < VillageData.ShipCount)
        {
            var ship = state.Ships[job.SubIndex];
            if (ship.IsAtSea || ship.BossPending) return false;
        }
        if (job.JobType == VillageData.JobTypeMapping && job.SubIndex < VillageData.MapDeviceCount)
        {
            var dev = state.MapDevices[job.SubIndex];
            if (dev.IsRunning || dev.BossAmbushPending) return false;
        }

        // Worker must have rank >= 1 in the target JobType.
        if (worker.RankFor(job.JobType) == 0) return false;

        // Enforce VillageBaseStats cap (shared across JobType when IsSharedCap, otherwise per job row).
        var (cap, shared) = VillageData.JobWorkerCap(targetJob);
        if (cap <= 0) return false;

        int currentCount = 0;
        for (int i = 0; i < state.Workers.Count; i++)
        {
            var other = state.Workers[i];
            if (ReferenceEquals(other, worker)) continue;
            byte otherJob = other.AssignedJobRow;
            if (otherJob == VillageData.JobIdling || otherJob >= VillageData.Jobs.Count) continue;
            if (shared)
            {
                if (VillageData.Jobs[otherJob].JobType == job.JobType) currentCount++;
            }
            else if (otherJob == targetJob)
            {
                currentCount++;
            }
        }
        return currentCount < cap;
    }

    static bool IsWorkerLockedInActiveOperationLocked(VillageTownState state, TownWorker worker)
    {
        byte job = worker.AssignedJobRow;
        if (job == VillageData.JobIdling || job == InstanceVillage.NoneRow || job >= VillageData.Jobs.Count)
            return false;
        var info = VillageData.Jobs[job];
        return info.JobType switch
        {
            VillageData.JobTypeLostAtSea or VillageData.JobTypeLostMapping => true,
            VillageData.JobTypeShipping => info.SubIndex < VillageData.ShipCount
                && (state.Ships[info.SubIndex].IsAtSea || state.Ships[info.SubIndex].BossPending),
            VillageData.JobTypeMapping => info.SubIndex < VillageData.MapDeviceCount
                && (state.MapDevices[info.SubIndex].IsRunning || state.MapDevices[info.SubIndex].BossAmbushPending),
            _ => false,
        };
    }

    // ---- 0x04 AssignFarm --------------------------------------------------------------------------------------------

    static async Task HandleAssignFarmAsync(Player player, byte plotIndex, byte rawResourceRow)
    {
        if (plotIndex >= VillageData.FarmPlotCount) return;
        byte cropResource = rawResourceRow is >= VillageData.ResWheat and <= VillageData.ResOrbisfera
            ? rawResourceRow
            : (rawResourceRow == InstanceVillage.NoneRow || rawResourceRow == VillageData.EmptyFarmPlot
                ? VillageData.EmptyFarmPlot
                : byte.MaxValue);
        if (cropResource == byte.MaxValue) return;

        var state = VillageTownService.GetState(player);
        long now = VillageTownService.ClockUtcSeconds();
        bool changed = false;

        lock (state.Gate)
        {
            VillageTownService.SimulateLocked(state, now);
            if (state.FarmPlotCropResource[plotIndex] != cropResource)
            {
                state.FarmPlotCropResource[plotIndex] = cropResource;
                VillageTownService.SaveLocked(state);
                changed = true;
            }
        }

        if (changed) await VillageTownService.SyncStateAsync(player, state);
    }

    // ---- 0x18 DepositGold & 0x19 WithdrawGold -----------------------------------------------------------------------

    static async Task HandleDepositGoldAsync(Player player, uint amount)
    {
        if (amount == 0) return;
        var state = VillageTownService.GetState(player);
        var stash = G.PersonalStashOf(player);
        long now = VillageTownService.ClockUtcSeconds();

        uint actualDeposit = 0;
        uint playerGoldAfter = stash.Gold;
        bool firstDeposit = false;

        lock (state.Gate)
        {
            VillageTownService.SimulateLocked(state, now);
            uint treasuryRoom = uint.MaxValue - state.TreasuryGold;
            uint requested = Math.Min(amount, treasuryRoom);
            if (requested == 0) return;
            uint available = stash.Gold;
            actualDeposit = Math.Min(requested, available);
            if (actualDeposit == 0) return;
            if (stash.TrySpendGold(actualDeposit) is not uint after) return;

            playerGoldAfter = after;
            state.TreasuryGold += actualDeposit;
            if (!state.TutorialFlags.HasFlag(VillageTutorialFlags.FirstGoldDeposited))
            {
                state.TutorialFlags |= VillageTutorialFlags.FirstGoldDeposited;
                firstDeposit = true;
            }
            VillageTownService.SaveLocked(state);
        }

        await Program.Send(player, InstanceEntities.Gold(playerGoldAfter), $"0x02aa deposit gold into Kingsmarch treasury (-{actualDeposit}g, now {playerGoldAfter}g)");
        if (firstDeposit) await KaruiShoresIntro.OnFirstGoldDepositedAsync(player);
        await VillageTownService.SyncStateAsync(player, state);
    }

    static async Task HandleWithdrawGoldAsync(Player player, uint amount)
    {
        if (amount == 0) return;
        var state = VillageTownService.GetState(player);
        var stash = G.PersonalStashOf(player);
        long now = VillageTownService.ClockUtcSeconds();

        uint actualWithdraw = 0;
        uint playerGoldAfter = stash.Gold;

        lock (state.Gate)
        {
            VillageTownService.SimulateLocked(state, now);
            uint playerRoom = uint.MaxValue - stash.Gold;
            actualWithdraw = Math.Min(Math.Min(amount, state.TreasuryGold), playerRoom);
            if (actualWithdraw == 0) return;

            state.TreasuryGold -= actualWithdraw;
            if (state.TreasuryGold == 0) state.GoldBurnRemainder = 0;
            playerGoldAfter = stash.AddGold(actualWithdraw);
            VillageTownService.SaveLocked(state);
        }

        await Program.Send(player, InstanceEntities.Gold(playerGoldAfter), $"0x02aa withdraw gold from Kingsmarch treasury (+{actualWithdraw}g, now {playerGoldAfter}g)");
        await VillageTownService.SyncStateAsync(player, state);
    }

    // ---- Worker generation (Rank 1..5, Words.dat HASH32 names, WageMultiplier) --------------------------------------

    /// <summary>Populates Raulf's recruitment pool with 6 fresh workers (Rank 1..5, valid <c>Words.dat</c> HASH32 keys).</summary>
    public static void PopulateRecruitsLocked(VillageTownState state, bool isSuper, SettlersSettings settings, Random rng)
    {
        state.Recruits.Clear();
        state.RecruitPoolFlag = isSuper ? (byte)1 : (byte)0;
        int poolSize = VillageData.RecruitPoolSize;
        int maxRank = VillageData.MaxWorkerRank;
        double wageMult = Math.Max(0.0, settings.WageMultiplier);
        var firstNames = VillageData.FirstNames;
        var lastNames = VillageData.LastNames;
        Span<byte> used = stackalloc byte[RecruitableJobTypes.Length];

        for (int i = 0; i < poolSize; i++)
        {
            var first = firstNames[rng.Next(firstNames.Count)];
            var last = lastNames[rng.Next(lastNames.Count)];

            // Skill count: normal 1..3 (mostly 1-2), super reroll 1..4 (higher chance of multi-skill & high rank).
            int skillCount = isSuper
                ? (rng.Next(100) < 55 ? 2 : rng.Next(100) < 75 ? 3 : 1)
                : (rng.Next(100) < 60 ? 1 : rng.Next(100) < 85 ? 2 : 3);

            var skills = new List<InstanceVillage.WorkerSkill>(skillCount);
            int highestRank = 1;
            int powerSum = 0;

            for (int s = 0; s < skillCount; s++)
            {
                byte jobType;
                do
                {
                    jobType = RecruitableJobTypes[rng.Next(RecruitableJobTypes.Length)];
                } while (ContainsByte(used[..s], jobType));
                used[s] = jobType;

                byte rank = RollWorkerRank(maxRank, isSuper, rng);
                if (rank > highestRank) highestRank = rank;
                powerSum += VillageData.WorkerPower(rank);
                skills.Add(new InstanceVillage.WorkerSkill(jobType, rank));
            }

            // Sort skills by rank descending so primary specialty appears cleanly first.
            skills.Sort((a, b) => b.Rank.CompareTo(a.Rank));

            // Realistic 3.29 wage curve per hour scaled by highest rank + secondary skills + jitter, then WageMultiplier.
            double baseWage = highestRank switch
            {
                1 => rng.Next(55, 115),
                2 => rng.Next(140, 260),
                3 => rng.Next(320, 560),
                4 => rng.Next(680, 1180),
                _ => rng.Next(1450, 2450),
            };
            double multiSkillFactor = 1.0 + (skillCount - 1) * 0.12 + powerSum * 0.001;
            ushort wage = (ushort)Math.Clamp(Math.Round(baseWage * multiSkillFactor * wageMult), 0, ushort.MaxValue);

            // Hire cost scales with worker rank & skill breadth (retail 3.29 ranges ~600g R1 up to ~25,000g R5).
            double baseHire = highestRank switch
            {
                1 => rng.Next(450, 1100),
                2 => rng.Next(1400, 3200),
                3 => rng.Next(3800, 7500),
                4 => rng.Next(8500, 16500),
                _ => rng.Next(18000, 36000),
            };
            uint hireCost = (uint)Math.Clamp(Math.Round(baseHire * (1.0 + (skillCount - 1) * 0.15)), 100, uint.MaxValue);

            state.Recruits.Add(new TownRecruit
            {
                FirstNameWordKey = first.Hash32,
                LastNameWordKey = last.Hash32,
                Name = first.Text + last.Text,
                WagePerHour = wage,
                HireCost = hireCost,
                Skills = skills,
            });
        }
    }

    static byte RollWorkerRank(int maxRank, bool isSuper, Random rng)
    {
        if (maxRank <= 1) return 1;
        int roll = rng.Next(100);
        int rank = isSuper
            ? roll switch
            {
                < 10 => 2,
                < 35 => 3,
                < 72 => 4,
                _ => 5,
            }
            : roll switch
            {
                < 30 => 1,
                < 62 => 2,
                < 84 => 3,
                < 96 => 4,
                _ => 5,
            };
        return (byte)Math.Clamp(rank, 1, maxRank);
    }

    static bool ContainsByte(ReadOnlySpan<byte> span, byte val)
    {
        for (int i = 0; i < span.Length; i++)
            if (span[i] == val) return true;
        return false;
    }

    // ---- Production Tick: Mining (0..4), Smelting (5..9), Farming (10..14) ------------------------------------------

    /// <summary>
    /// Simulates Mining, Smelting, and Farming for <paramref name="dtSeconds"/> using the exact 3.29.3c formulas
    /// (<c>0x1424c2e20</c>, <c>0x1424c32e0</c>, <c>0x1424c5250</c>, <c>0x1424c57a0</c>).
    /// Caller must hold <see cref="VillageTownState.Gate"/>.
    /// </summary>
    public static bool SimulateProductionLocked(VillageTownState state, double dtSeconds, SettlersSettings settings)
    {
        if (dtSeconds <= 0.0) return false;
        bool changed = false;

        double miningMult = Math.Max(0.0, settings.MiningSpeedMultiplier);
        double smeltingMult = Math.Max(0.0, settings.SmeltingSpeedMultiplier);
        double farmingSpeedMult = Math.Max(0.0, settings.FarmingSpeedMultiplier);
        double cropYieldMult = Math.Max(0.0, settings.CropYieldMultiplier);

        // 1. Mining (VillageProduction rows 0..4) and Smelting (rows 5..9).
        var prods = VillageData.Productions;
        for (int i = 0; i < prods.Count; i++)
        {
            var prod = prods[i];
            if (prod.Row >= VillageData.ProductionCount) continue;
            if (prod.JobRow is not byte jobRow) continue; // Farming handled in step 2

            byte jobType = jobRow < VillageData.Jobs.Count ? VillageData.Jobs[jobRow].JobType : VillageData.JobTypeIdling;
            double speedMult = jobType == VillageData.JobTypeMining ? miningMult
                : jobType == VillageData.JobTypeSmelting ? smeltingMult
                : 1.0;
            if (speedMult <= 0.0) continue;

            if (prod.InputResource is int inRes && state.Resources[inRes] == 0)
                continue;

            long baseSpeed = JobSpeedLocked(state, jobRow, jobType);
            if (baseSpeed <= 0) continue;

            long effectiveSpeed = (long)Math.Round(baseSpeed * speedMult);
            if (effectiveSpeed <= 0) continue;

            long costPerItem = (long)prod.BaseProductionTime * 100L;
            if (costPerItem <= 0) continue;

            decimal totalWorkDec = (decimal)effectiveSpeed * (decimal)dtSeconds + state.ProductionProgress[prod.Row];
            long totalWork = totalWorkDec >= long.MaxValue ? long.MaxValue : (long)Math.Floor(totalWorkDec);
            long produced = totalWork / costPerItem;
            uint newProgress = (uint)(totalWork % costPerItem);

            if (prod.InputResource is int inputRes)
            {
                uint stock = state.Resources[inputRes];
                if (produced >= stock)
                {
                    produced = stock;
                    newProgress = 0; // Input depleted: reset remainder cleanly.
                }
                if (produced > 0)
                {
                    state.Resources[inputRes] -= (uint)produced;
                    state.Resources[prod.OutputResource] = SaturatingAdd(state.Resources[prod.OutputResource], produced);
                    changed = true;
                }
            }
            else if (produced > 0)
            {
                state.Resources[prod.OutputResource] = SaturatingAdd(state.Resources[prod.OutputResource], produced);
                changed = true;
            }

            if (state.ProductionProgress[prod.Row] != newProgress)
            {
                state.ProductionProgress[prod.Row] = newProgress;
                changed = true;
            }
        }

        // 2. Farming (VillageProduction rows 10..14 -> Crops Wheat..Blue Zanthimum, driven by 6 plots with VillageFarmAdjacency).
        if (farmingSpeedMult > 0.0 && cropYieldMult > 0.0)
        {
            Span<long> plotBaseSpeed = stackalloc long[VillageData.FarmPlotCount];
            Span<int> adjActive = stackalloc int[VillageData.FarmPlotCount];
            int distinctMask = 0;
            int distinctCrops = 0;

            for (int plot = 0; plot < VillageData.FarmPlotCount; plot++)
            {
                byte crop = state.FarmPlotCropResource[plot];
                if (crop is < VillageData.ResWheat or > VillageData.ResOrbisfera) continue;
                byte farmJob = (byte)(VillageData.JobFarmingFirst + plot);
                long speed = JobSpeedLocked(state, farmJob, VillageData.JobTypeFarming);
                if (speed <= 0) continue;

                plotBaseSpeed[plot] = speed;
                int bit = 1 << (crop - VillageData.ResWheat);
                if ((distinctMask & bit) == 0)
                {
                    distinctMask |= bit;
                    distinctCrops++;
                }
                if (plot < VillageData.FarmAdjacency.Count)
                {
                    foreach (int neighbor in VillageData.FarmAdjacency[plot])
                        if (neighbor is >= 0 and < VillageData.FarmPlotCount)
                            adjActive[neighbor]++;
                }
            }

            int adjBonusPct = VillageData.FarmAdjPlotBonusPct;
            int diffCropBonusPct = VillageData.FarmDiffCropBonusPct;

            // Aggregate effective farming speed per crop (VillageResources 10..14 -> VillageProduction 10..14).
            Span<long> cropSpeed = stackalloc long[5];
            for (int plot = 0; plot < VillageData.FarmPlotCount; plot++)
            {
                if (plotBaseSpeed[plot] <= 0) continue;
                int cropIdx = state.FarmPlotCropResource[plot] - VillageData.ResWheat;
                long bonusFactor = 100 + adjBonusPct * adjActive[plot] + diffCropBonusPct * distinctCrops;
                long adjusted = (plotBaseSpeed[plot] * Math.Max(0, bonusFactor)) / 100;
                cropSpeed[cropIdx] += adjusted;
            }

            for (int cropIdx = 0; cropIdx < 5; cropIdx++)
            {
                if (cropSpeed[cropIdx] <= 0) continue;
                int prodRow = 10 + cropIdx;
                if (prodRow >= prods.Count) continue;
                var prod = prods[prodRow];

                long effectiveSpeed = (long)Math.Round(cropSpeed[cropIdx] * farmingSpeedMult * cropYieldMult);
                if (effectiveSpeed <= 0) continue;

                long costPerItem = (long)prod.BaseProductionTime * 100L;
                if (costPerItem <= 0) continue;

                decimal totalWorkDec = (decimal)effectiveSpeed * (decimal)dtSeconds + state.ProductionProgress[prodRow];
                long totalWork = totalWorkDec >= long.MaxValue ? long.MaxValue : (long)Math.Floor(totalWorkDec);
                long produced = totalWork / costPerItem;
                uint newProgress = (uint)(totalWork % costPerItem);

                if (produced > 0)
                {
                    state.Resources[prod.OutputResource] = SaturatingAdd(state.Resources[prod.OutputResource], produced);
                    changed = true;
                }
                if (state.ProductionProgress[prodRow] != newProgress)
                {
                    state.ProductionProgress[prodRow] = newProgress;
                    changed = true;
                }
            }
        }

        return changed;
    }

    /// <summary>Base job speed (<c>0x1424c32e0</c>): <c>sum(workerPower * 100) * (100 + stat(JobSpeedStat)) / 100</c>.</summary>
    public static long JobSpeedLocked(VillageTownState state, byte jobRow, byte jobType)
    {
        long speed = 0;
        for (int i = 0; i < state.Workers.Count; i++)
        {
            var w = state.Workers[i];
            if (w.AssignedJobRow != jobRow) continue;
            byte rank = w.RankFor(jobType);
            speed += VillageData.WorkerPower(rank) * 100L;
        }
        if (speed <= 0) return 0;
        if (jobType < VillageData.JobTypes.Count && VillageData.JobTypes[jobType].JobSpeedStat is int statRow)
        {
            int pct = VillageData.GetBaseStat(statRow, 0);
            speed = (speed * (100 + pct)) / 100;
        }
        return Math.Max(0, speed);
    }

    static uint SaturatingAdd(uint current, long delta)
    {
        if (delta <= 0) return current;
        long sum = (long)current + delta;
        return sum >= uint.MaxValue ? uint.MaxValue : (uint)sum;
    }
}
