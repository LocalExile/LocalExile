using GameServer.Core.Protocol.Instance;
using GameServer.Modules.Settlers;
using GameServer.Modules.Settlers.Operations;
using GameServer.Modules.Settlers.Town;
using GameServer.Storage;
using Xunit;

namespace GameServer.Gateway.Tests;

[Collection("Gateway")]
public sealed class SettlersTests
{
    [Fact]
    public void Settlers_full_town_economy_waypoint_multipliers_and_sqlite_persistence()
    {
        OperationsData.EnsureLoaded();

        string tempDir = Path.Combine(Path.GetTempPath(), $"localexile-settlers-town-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var prevClock = VillageTownService.ClockUtcSeconds;
        try
        {
            long now = 1_750_000_000L;
            VillageTownService.ClockUtcSeconds = () => now;
            VillageTownService.Reset();

            using var db = new SaveDatabase(Path.Combine(tempDir, "characters.db"));
            var state = VillageTownService.GetState("settler_alice", "Standard", db);

            lock (state.Gate)
            {
                state.TreasuryGold = 10_000;
                state.Resources[VillageData.ResPendingIron] = 50_000;
                state.Resources[VillageData.ResIronOre] = 50_000;
                state.FarmPlotCropResource[0] = VillageData.ResWheat;
                state.FarmPlotCropResource[1] = VillageData.ResCorn;
                state.LastSimulatedUtc = now;

                // Assign one Rank-5 miner (job 1), one Rank-5 smelter (job 6), and one Rank-5 farmer on plot 0 (job 11).
                state.Workers.Add(new TownWorker
                {
                    WorkerId = state.NextWorkerId++,
                    FirstNameWordKey = VillageData.FirstNames[0].Hash32,
                    LastNameWordKey = VillageData.LastNames[0].Hash32,
                    Name = "Miner One",
                    WagePerHour = 1_000,
                    AssignedJobRow = 1,
                    Skills = [new(VillageData.JobTypeMining, 5)],
                });
                state.Workers.Add(new TownWorker
                {
                    WorkerId = state.NextWorkerId++,
                    FirstNameWordKey = VillageData.FirstNames[0].Hash32,
                    LastNameWordKey = VillageData.LastNames[0].Hash32,
                    Name = "Smelter One",
                    WagePerHour = 1_000,
                    AssignedJobRow = 6,
                    Skills = [new(VillageData.JobTypeSmelting, 5)],
                });
                state.Workers.Add(new TownWorker
                {
                    WorkerId = state.NextWorkerId++,
                    FirstNameWordKey = VillageData.FirstNames[0].Hash32,
                    LastNameWordKey = VillageData.LastNames[0].Hash32,
                    Name = "Farmer One",
                    WagePerHour = 1_000,
                    AssignedJobRow = 11,
                    Skills = [new(VillageData.JobTypeFarming, 5)],
                });
                VillageTownService.SaveLocked(state, db);
            }

            // 1. Retail 1.0x simulation for 600s (10 minutes): active wages = 3,000g/hr -> 500g burned.
            uint mined1x, smelted1x, wheat1x, goldAfter1x;
            lock (state.Gate)
            {
                Assert.True(VillageTownService.SimulateLocked(state, now + 600, SettlersSettings.Default));
                mined1x = 50_000 - state.Resources[VillageData.ResPendingIron];
                smelted1x = state.Resources[VillageData.ResRefinedIron];
                wheat1x = state.Resources[VillageData.ResWheat];
                goldAfter1x = state.TreasuryGold;
                Assert.True(mined1x > 0);
                Assert.True(smelted1x > 0);
                Assert.True(wheat1x > 0);
                Assert.Equal(9_500u, goldAfter1x);
            }

            // 2. Waypoint accelerated settings (4x mining/smelting/farming, 2x crop yield, 0x gold burn) for another 600s.
            var boosted = SettlersSettings.Default with
            {
                GoldBurnRateMultiplier = 0.0,
                MiningSpeedMultiplier = 4.0,
                SmeltingSpeedMultiplier = 4.0,
                FarmingSpeedMultiplier = 4.0,
                CropYieldMultiplier = 2.0,
            };
            lock (state.Gate)
            {
                uint pendingBefore = state.Resources[VillageData.ResPendingIron];
                uint refinedBefore = state.Resources[VillageData.ResRefinedIron];
                uint wheatBefore = state.Resources[VillageData.ResWheat];
                Assert.True(VillageTownService.SimulateLocked(state, now + 1_200, boosted));
                Assert.Equal(goldAfter1x, state.TreasuryGold); // 0x gold burn consumes 0 gold
                Assert.True(pendingBefore - state.Resources[VillageData.ResPendingIron] >= mined1x * 3);
                Assert.True(state.Resources[VillageData.ResRefinedIron] - refinedBefore >= smelted1x * 3);
                Assert.True(state.Resources[VillageData.ResWheat] - wheatBefore >= wheat1x * 6);
                VillageTownService.SaveLocked(state, db);
            }

            // 3. Exact zero-treasury cutoff: with 100g left and 3,000g/hr active wages (2x burn = 6,000g/hr -> 60s of gold),
            // a 3600s offline jump must stop at 0 gold without wraparound.
            lock (state.Gate)
            {
                state.TreasuryGold = 100;
                state.GoldBurnRemainder = 0;
                var fastBurn = SettlersSettings.Default with { GoldBurnRateMultiplier = 2.0 };
                Assert.True(VillageTownService.SimulateLocked(state, now + 4_800, fastBurn));
                Assert.Equal(0u, state.TreasuryGold);
                Assert.Equal(0u, state.GoldBurnRemainder);
                VillageTownService.SaveLocked(state, db);
            }

            // 4. Reload from SQLite after cache reset and verify full league+account persistence.
            VillageTownService.Reset();
            var reloaded = VillageTownService.GetState("settler_alice", "Standard", db);
            lock (reloaded.Gate)
            {
                Assert.Equal(3, reloaded.Workers.Count);
                Assert.Equal(0u, reloaded.TreasuryGold);
                Assert.True(reloaded.Resources[VillageData.ResRefinedIron] > 0);
                Assert.True(reloaded.Resources[VillageData.ResWheat] > 0);
                Assert.Equal(VillageData.ResWheat, reloaded.FarmPlotCropResource[0]);
            }
        }
        finally
        {
            VillageTownService.ClockUtcSeconds = prevClock;
            VillageTownService.Reset();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Settlers_bridge_unifies_town_shipping_mapping_disenchanting_and_recombinator()
    {
        OperationsData.EnsureLoaded();

        string tempDir = Path.Combine(Path.GetTempPath(), $"localexile-settlers-ops-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var prevClock = VillageTownService.ClockUtcSeconds;
        try
        {
            long now = 1_750_010_000L;
            VillageTownService.ClockUtcSeconds = () => now;
            VillageTownService.Reset();

            using var db = new SaveDatabase(Path.Combine(tempDir, "characters.db"));
            var bridge = new TownOperationsBridge(db);

            // Seed authoritative town state via VillageTownService and confirm TownOperationsBridge reads/mutates the SAME object.
            var state = VillageTownService.GetState("settler_bob", "Standard", db);
            lock (state.Gate)
            {
                state.TreasuryGold = 250_000;
                state.Resources[VillageData.ResRefinedIron] = 5_000;
                state.Resources[VillageData.ResWheat] = 5_000;
                state.Resources[VillageData.ResMagicDust] = 10_000;
                state.Workers.Add(new TownWorker
                {
                    WorkerId = state.NextWorkerId++,
                    FirstNameWordKey = VillageData.FirstNames[0].Hash32,
                    LastNameWordKey = VillageData.LastNames[0].Hash32,
                    Name = "Sailor One",
                    WagePerHour = 200,
                    AssignedJobRow = OperationsData.JobShippingFirst,
                    Skills = [new(OperationsData.JobTypeShipping, 5)],
                });
                state.Workers.Add(new TownWorker
                {
                    WorkerId = state.NextWorkerId++,
                    FirstNameWordKey = VillageData.FirstNames[0].Hash32,
                    LastNameWordKey = VillageData.LastNames[0].Hash32,
                    Name = "Mapper One",
                    WagePerHour = 200,
                    AssignedJobRow = OperationsData.JobMappingFirst,
                    Skills = [new(OperationsData.JobTypeMapping, 5)],
                });
                state.Workers.Add(new TownWorker
                {
                    WorkerId = state.NextWorkerId++,
                    FirstNameWordKey = VillageData.FirstNames[0].Hash32,
                    LastNameWordKey = VillageData.LastNames[0].Hash32,
                    Name = "Disenchanter One",
                    WagePerHour = 200,
                    AssignedJobRow = OperationsData.JobDisenchanting,
                    Skills = [new(OperationsData.JobTypeDisenchanting, 5)],
                });
                VillageTownService.SaveLocked(state, db);
            }

            // 1. Bridge worker views & zero-split-brain resource mutation.
            Assert.Single(bridge.GetWorkersForJob("settler_bob", "Standard", OperationsData.JobShippingFirst));
            Assert.Single(bridge.GetWorkersForJob("settler_bob", "Standard", OperationsData.JobMappingFirst));
            Assert.Single(bridge.GetWorkersForJob("settler_bob", "Standard", OperationsData.JobDisenchanting));
            Assert.False(bridge.HasActiveWork("settler_bob", "Standard"));

            Assert.True(bridge.TrySpendResources("settler_bob", "Standard", [(VillageData.ResRefinedIron, 1_000)]));
            Assert.Equal(4_000u, bridge.GetResource("settler_bob", "Standard", VillageData.ResRefinedIron));
            lock (state.Gate) Assert.Equal(4_000u, state.Resources[VillageData.ResRefinedIron]);

            // 2. Deferred in-memory snapshot mutation (persistNow: false -> HasActiveWork true, then FlushIfDirty persists).
            bridge.MutateSnapshot("settler_bob", "Standard", snap =>
            {
                var ships = snap.Ships.ToList();
                var exports = new uint[OperationsData.ExportCount];
                exports[5] = 1_000;
                ships[0] = ships[0] with
                {
                    State = VillageShippingService.ShipStateSailing,
                    PortId = 1,
                    DepartedUtc = now,
                    ArrivalUtc = now + 1800,
                    ShipmentValue = 100_000,
                    CargoJson = VillageShippingService.SerializeCargoBlob(exports, 0, 1800),
                };
                return (snap with { Ships = ships }, true);
            }, persistNow: false);

            Assert.True(bridge.HasActiveWork("settler_bob", "Standard"));
            lock (state.Gate)
            {
                Assert.Equal(VillageShippingService.ShipStateSailing, state.Ships[0].State);
                Assert.Equal(1, state.Ships[0].PortRow);
                // OnSimulateTickLocked advances sailing timer in 0x1424c7c10 progress units without forcing 1s SQLite writes.
                uint expectedTarget = VillageShippingService.ComputeTravelTime(1, 0, 1.0).ProgressTarget;
                Assert.False(bridge.OnSimulateTickLocked(state, 300, now + 300));
                Assert.Equal(expectedTarget / 6, state.Ships[0].TimerElapsed);
                Assert.Equal(expectedTarget, state.Ships[0].TimerTotal);
            }
            bridge.FlushIfDirty("settler_bob", "Standard");

            // 3. Shipping value + dust multiplier curve (0x1424c60e0).
            var shipExports = new uint[OperationsData.ExportCount];
            shipExports[5] = 1_000; // Crimson Iron Bar
            shipExports[OperationsData.MagicDustExportRow] = 10_000;
            var (baseVal, dustMult, shipVal) = VillageShippingService.ComputeShipmentValue(shipExports);
            Assert.True(baseVal > 0 && dustMult > 1.0 && shipVal > baseVal);

            // 4. Recombinator dual-mode wire codec + cost formula verification (0=Predictable, 1=Unpredictable).
            byte[] predPacket = new GameServer.Core.Protocol.Login.LoginWriter()
                .U16(InstanceVillage.RecombinateId).U8(0).U8(1).U8(0).U8(1).U8(0).U8(0).U8(0).ToArray();
            var predReq = InstanceVillage.ReadRecombinate(predPacket);
            Assert.True(predReq.PredictableMode);
            Assert.False(predReq.Execute);

            byte[] unpredPacket = new GameServer.Core.Protocol.Login.LoginWriter()
                .U16(InstanceVillage.RecombinateId).U8(0).U8(0).U8(0).U8(1).U8(1).ToArray();
            var unpredReq = InstanceVillage.ReadRecombinate(unpredPacket);
            Assert.False(unpredReq.PredictableMode);
            Assert.True(unpredReq.Execute);

            var catalog = Program.Items;
            var ringBase = catalog.All.First(bt => bt.Class == "Ring");
            var explicitModDef = catalog.Mods.All.First(m => m.GenerationType == 1 && m.Level >= 60);
            var item0 = new Item(ringBase.Hash, ["Base", "Mods"], 0)
            {
                ItemLevel = 84,
                Rarity = ItemRarity.Rare,
                Identified = true,
                Mods = [new(ModSection.Explicit, explicitModDef.Hash, [10]), new(ModSection.Explicit, explicitModDef.Hash, [10])],
            };
            var item1 = new Item(ringBase.Hash, ["Base", "Mods"], 0)
            {
                ItemLevel = 84,
                Rarity = ItemRarity.Rare,
                Identified = true,
                Mods = [new(ModSection.Explicit, explicitModDef.Hash, [10]), new(ModSection.Explicit, explicitModDef.Hash, [10])],
            };
            var (predGold, predDust) = RecombinatorService.ComputeCosts(item0, item1, [explicitModDef], predictableMode: true, catalog);
            var (unpredGold, unpredDust) = RecombinatorService.ComputeCosts(item0, item1, [], predictableMode: false, catalog);
            Assert.True(predGold > 0 && predDust > 0);
            Assert.True(unpredGold > predGold && unpredDust > 0);

            // 5. Disenchanting dust curve verification (0x1424c6af0: ilvl 45 < ilvl 68 < ilvl 84 + yield multiplier).
            long dust45 = VillageDisenchantingService.ComputeDustValue(item0 with { ItemLevel = 45 }, catalog);
            long dust68 = VillageDisenchantingService.ComputeDustValue(item0 with { ItemLevel = 68 }, catalog);
            long dust84 = VillageDisenchantingService.ComputeDustValue(item0 with { ItemLevel = 84 }, catalog);
            long dust84x2 = VillageDisenchantingService.ComputeDustValue(item0 with { ItemLevel = 84 }, catalog, dustYieldMultiplier: 2.0);
            Assert.True(dust45 > 0 && dust68 > dust45 && dust84 > dust68);
            Assert.Equal(dust84 * 2, dust84x2);

            // 6. Verify Returned ship state keeps (target, target) timer, idle map device has isStopped=1, and 3.29 Map (Tier 1) resolves to tier 1.
            lock (state.Gate)
            {
                state.Ships[0].State = VillageShippingService.ShipStateReturned;
                state.Ships[0].PortRow = 1;
                var wire = state.ToWireSnapshot((ulong)now);
                uint target = VillageShippingService.ComputeTravelTime(1, 0, 1.0).ProgressTarget;
                Assert.Equal(target, wire.ShipTimers[0].Elapsed);
                Assert.Equal(target, wire.ShipTimers[0].Total);
                Assert.Equal(VillageMappingService.FlagStopped, (byte)(wire.MapDevices[0].StateFlags1 & VillageMappingService.FlagStopped));
            }
            if (catalog.Find("Map (Tier 1)") is { } t1Map)
            {
                Assert.True(OperationsData.TryGetMapTier(t1Map.Hash, out int resolvedTier));
                Assert.Equal(1, resolvedTier);
            }

            // 7. Regression: ApplyFromStoreSnapshot preserves sailing TimerTotal in progress units (never raw seconds)
            //    and keeps IsAtSea true, while idle PortId=0/EventId=0 normalize to NoneRow (0xFF).
            uint shipTarget = VillageShippingService.ComputeTravelTime(1, 0, 1.0).ProgressTarget;
            bridge.MutateSnapshot(account, league, snap =>
            {
                var ships = snap.Ships.Where(s => s.ShipIndex != 0 && s.ShipIndex != 1).ToList();
                ships.Add(new VillageStore.ShipRow(0, VillageShippingService.ShipStateSailing, 1,
                    now - 100, now + 1800, 0, 1000,
                    $"{{\"Exports\":[0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0],\"Elapsed\":{shipTarget / 3},\"Total\":{shipTarget}}}",
                    "[]", 0, 0, false));
                ships.Add(new VillageStore.ShipRow(1, VillageShippingService.ShipStateIdle, 0,
                    0, 0, 0, 0, "{}", "[]", 0, 0, false));
                return (snap with { Ships = ships }, true);
            }, persistNow: true);
            lock (state.Gate)
            {
                Assert.Equal(shipTarget, state.Ships[0].TimerTotal);
                Assert.True(state.Ships[0].IsAtSea);
                Assert.Equal(InstanceVillage.NoneRow, state.Ships[1].PortRow);
                Assert.Equal(InstanceVillage.NoneRow, state.Ships[1].FailureRow);
            }

            // 8. Regression: ProspectingAstrolabe rejects >1 direct astrolabe and rejects stacking on an already-shaped map.
            var astrolabe = new GameServer.Modules.Settlers.MapOres.ProspectingAstrolabe();
            var dummyArea = new MapDevice.MapArea(0xF4DD, "MapWorldsBeach", "Beach", 0) { Level = 83 };
            var dummyOpening = new MapDevice.Opening(dummyArea, MapMods.None);
            var astrolabeSlot = new MapDeviceSlot(0, 0, 1, GameServer.Modules.Settlers.MapOres.MapOreTables.ProspectingAstrolabeId, "Currency", false);
            var twoAstrolabes = new MapOpening(null!, [astrolabeSlot, astrolabeSlot], false, dummyOpening);
            astrolabe.OnMapOpening(twoAstrolabes);
            Assert.Null(twoAstrolabes.Opening);

            var alreadyShaped = new MapOpening(null!, [astrolabeSlot], false,
                dummyOpening with { Mods = MapMods.None.WithFragmentStat(Stat.ProspectingAstrolabeEnabledMap, 1) });
            astrolabe.OnMapOpening(alreadyShaped);
            Assert.Null(alreadyShaped.Opening);

            var oneAstrolabe = new MapOpening(null!, [astrolabeSlot], false, dummyOpening);
            astrolabe.OnMapOpening(oneAstrolabe);
            Assert.NotNull(oneAstrolabe.Opening);
            Assert.Equal(1, oneAstrolabe.Opening!.Mods.Get(Stat.MonsterInAstrolabeMap));
        }
        finally
        {
            VillageTownService.ClockUtcSeconds = prevClock;
            VillageTownService.Reset();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}
