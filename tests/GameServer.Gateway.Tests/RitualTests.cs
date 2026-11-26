using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain.Generation;
using GameServer.Modding;
using GameServer.Modules.Ritual;
using MonsterRarity = GameServer.Core.Protocol.Instance.MonsterRarity;

namespace GameServer.Gateway.Tests;

/// <summary>The Ritual module against the official 3.29.3c/d binary &amp; captures: objects, wire messages, item component 157,
/// client tables, campaign integration, arena containment, recovery ceiling, Favour buy/defer/reroll, Blood-filled Vessels,
/// and Omens.</summary>
public class RitualTests
{
    static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    /// <summary>The bodies, messages, UI formulas, RitualEncounter component 157, arena ring containment, recovery
    /// ceiling, and client tables.</summary>
    [Fact]
    public void The_bodies_the_messages_and_the_tables_match_the_captures()
    {
        RitualModule.LoadData();
        var cell = (512, 446);
        float rot = MathF.PI / 4;
        string altar = Hex(RitualWire.Interactable(cell, rot, 62, 0, 0, 0));
        Assert.Equal(320, altar.Length);
        Assert.StartsWith("000246a37a020001083e00000034610e460001090000000000", altar);
        Assert.EndsWith("0317140000000200000000000000", altar);
        string light = Hex(RitualWire.Light(cell, rot, 0, null)), scaled = Hex(RitualWire.Light(cell, rot, 2, (float)(1 + 8.46 * 0.5)));
        Assert.Equal(64, light.Length);
        Assert.Equal(88, scaled.Length);
        Assert.StartsWith("000000020000be010000db0f493f0000", light);
        Assert.StartsWith("000000020000be010000db0f493f00002400", scaled);
        string pool = Hex(RitualWire.Pool(cell, rot, 0x1234)), blocker = Hex(RitualWire.Blocker(cell, 87));
        Assert.Equal(64, pool.Length);
        Assert.StartsWith("02011cd9231700010134120000", pool);
        Assert.Equal(68, blocker.Length);
        Assert.StartsWith("020000020000be0100000000000000002000", blocker);
        var states = new BlobWriter();
        new StateMachineData([3, 1], 0).Write(states);
        Assert.Equal("020003000100", Hex(states.ToArray()));
        Assert.Equal("01650000123400000200000001be02", Hex(RitualWire.AltarState(0x1234, cell, 2)));
        Assert.Equal("016700000128", Hex(RitualWire.Tribute(296)));

        // 0x0166 exact 3.29.3c/d layout: u16 id, u32 tribute, u8 rerollsUsed, u8 count, count x 9B, u8 altars, u8 done.
        var favours = RitualWire.Favours(1500, 1, RitualTables.FavoursRows(0), 3, 0);
        Assert.Equal(2 + 4 + 1 + 1 + 20 * 9 + 2, favours.Length);
        Assert.Equal(1, favours[6]);  // rerollsUsed
        Assert.Equal(20, favours[7]); // count
        Assert.Equal(3, favours[^2]); // totalAltars
        Assert.Equal(0, favours[^1]); // completedAltars
        Assert.Equal("007b3800000620000000000000000000000000", Hex(RitualWire.OpenFavoursWindow()));

        // C->S 0x0283 (buy) and 0x0284 (defer) parsers.
        var buyPkt = new LoginWriter().U16(RitualWire.BuyFavourId).U32(7).U32(0).U32(unchecked((uint)-1)).U32(unchecked((uint)-1)).ToArray();
        Assert.True(RitualWire.TryReadBuyFavour(buyPkt, out var buy));
        Assert.Equal((7u, 0u, -1, -1), (buy.ItemId, buy.DestInventoryRow, buy.DestX, buy.DestY));
        var deferPkt = new LoginWriter().U16(RitualWire.DeferFavourId).U32(11).ToArray();
        Assert.True(RitualWire.TryReadDeferFavour(deferPkt, out uint deferId));
        Assert.Equal(11u, deferId);

        // Exact UI formulas (reroll, defer cost, defer eligibility).
        Assert.Equal(600, RitualTables.RerollCost(21, 0, 0, 0));
        Assert.Equal(2000, RitualTables.RerollCost(70, 0, 0, 0));
        Assert.Equal(0, RitualTables.RerollCost(70, 0, 1, 0));
        Assert.Equal(1640, RitualTables.RerollCost(70, 0, 0, -18));
        Assert.Equal(150, RitualTables.DeferCost(1000, 0));
        Assert.Equal(128, RitualTables.DeferCost(1000, -15));
        Assert.False(RitualTables.CanDefer(46, 1000, 1000));
        Assert.True(RitualTables.CanDefer(47, 1000, 1000));
        Assert.False(RitualTables.CanDefer(70, 99, 1000));

        // Blood-filled Vessel RitualEncounter item component (157) serialization.
        var vesselType = Program.Items.Find(RitualTables.BloodFilledVesselId);
        Assert.NotNull(vesselType);
        Assert.True(vesselType!.Supported);
        var vessel = ItemCatalog.Create(vesselType) with
        {
            RitualEncounter = new RitualEncounterData(75, 0x1234, [new RitualEncounterMonster(0xabcd, 4)]),
        };
        byte[] payload = vessel.Payload();
        // Tail of payload is RitualEncounter: u8 count(1), u32 LE level(75=0x4b), u16 LE hash(0xabcd), u16 LE count(4), u16 LE area(0x1234).
        Assert.EndsWith("014b000000cdab04003412", Hex(payload));

        // Circular arena ring blocker: blocks perimeter at radius 87, leaves interior & exterior walkable.
        var ring = new BlockerSet([new ClientPathfinder.Blocker(200, 200, 0) { RingRadius = 87 }]);
        Assert.False(ClientPathfinder.ObjectBlocks(ring, 200, 200, 2, 5)); // interior center
        Assert.False(ClientPathfinder.ObjectBlocks(ring, 250, 200, 2, 5)); // interior (d=50)
        Assert.True(ClientPathfinder.ObjectBlocks(ring, 287, 200, 2, 5));  // perimeter (d=87)
        Assert.False(ClientPathfinder.ObjectBlocks(ring, 310, 200, 2, 5)); // exterior (d=110)

        // Recovery ceiling while IsInRitual is set (both Life ceiling pct and ES Gain ceiling).
        Assert.Equal(100, PetrifiedBlood.RecoveryCeilingPct(s => s == Stat.MapRitualCannotRecoverLifeOrEnergyShieldAboveXPct ? 50 : 0));
        Assert.Equal(50, PetrifiedBlood.RecoveryCeilingPct(s => s == Stat.MapRitualCannotRecoverLifeOrEnergyShieldAboveXPct ? 50 : s == Stat.IsInRitual ? 1 : 0));
        var es = new EnergyShield();
        es.SetMaximum(100, refill: false);
        es.Gain(20);
        es.Gain(60, ceiling: 50);
        Assert.Equal(50u, es.Current);
        es.SetMaximum(100, refill: true);
        es.Gain(20, ceiling: 50);
        Assert.Equal(100u, es.Current);

        // The environment on and off as the captures send them.
        Assert.Equal("00df0104ff0000000000", Hex(InstanceEnvironments.Data((1279, 0))));
        Assert.Equal("00df0104ff00b3d6bf95", Hex(InstanceEnvironments.Data((1279, RitualTables.EndEnvironmentSeconds))));

        // Client tables: campaign Act 2 forced areas, altar ranges, tribute curve, runes, daemons.
        Assert.Equal(3, RitualTables.CampaignForcedAltars("1_2_8"));
        Assert.Equal(3, RitualTables.CampaignForcedAltars("1_2_13"));
        Assert.Equal(0, RitualTables.CampaignForcedAltars("1_1_2"));
        Assert.Equal((3, 3), RitualTables.AltarRange(45));
        Assert.Equal((3, 4), RitualTables.AltarRange(46));
        Assert.Equal(87, RitualTables.RadiusCells);
        Assert.Equal(new[] { 10, 6, 2, 0, 26, 15, 5, 0, 135, 81, 27, 0, 400, 240, 80, 0 },
            new[] { MonsterRarity.Normal, MonsterRarity.Magic, MonsterRarity.Rare, MonsterRarity.Unique }
                .SelectMany(r => Enumerable.Range(1, 4).Select(n => RitualTables.TributeOf(r, n))));
        Assert.Equal(82, RitualTables.Runes.Count);
        Assert.All(RitualTables.Runes, r => Assert.True(r.BuffRow >= 0 || r.ModRows.Count > 0));
        var fire = RitualTables.Runes.Single(r => r.Id == "Fire1");
        Assert.Equal([(uint)614, (uint)450], fire.BuffStats.Select(s => s.Stat));
        Assert.Equal([25, 20], fire.BuffStats.Select(s => s.Value));
        Assert.Equal([37430], fire.ModRows);
        var charged = RitualTables.Runes.Single(r => r.Id == "GenericCharges1");
        Assert.Empty(charged.BuffStats);
        Assert.Equal(3, RitualTables.RuneModSet(charged, 77)!.Mods.Count);
        Assert.True(RitualTables.IsMonsterType(Game.Monsters.Find("PhysicalBleedTotem")!));
        Assert.False(RitualTables.IsMonsterType(Game.Monsters.Find("GoldenCoinDaemon")!));
        Assert.Equal(16, RitualTables.RitualOmens.Count);
    }

    /// <summary>Full end-to-end Ritual lifecycle: placement, guards, containment ring, waves, tribute, Favour window
    /// (open/buy/defer/reroll + AccountData persistence), Ritual Vessel bottling, MapOpening Blood-filled Vessel
    /// injection, and Omen crafting.</summary>
    [GameFilesFact]
    public async Task An_altar_is_ready_when_its_guards_die_and_its_start_revives_them_for_tribute()
    {
        RitualModule.LoadData();
        var mods = new MapMods(new Dictionary<uint, int> { [Stat.MapAreaRitualAdditionalChancePct] = 100 });
        var area = AreaInfo.Load(GamePack.Default.TryRead, "MapWorldsStrand", 70);
        var instance = new InstanceState();
        var stats = instance.AreaStats(() => Game.ComposeMapAreaStats(mods, 3, new Dictionary<uint, int>(), new HashSet<string>(), -1));
        uint seed = ServerLevels.PlayableSeed(area.Id, 0x7269, 70, stats);
        Assert.IsType<ServerLevels.Level>(ServerLevels.For(area.Id, seed, 70));
        var layout = AreaLayouts.For(area.Hash16, seed);
        instance.OpenedWith(new("RitualMap", new(layout.Hash, area.Id, "Strand", 3) { Level = 70 }, mods));

        TestEnvironment.OpenOwnSaves("ritual");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
        await using var owner = await ScarabEncounterTests.Wire.Open(instance, layout, seed);
        instance.World.Runner = owner.Player;
        try
        {
            var player = owner.Player;
            var encounter = new RitualEncounter();
            Assert.Null(encounter.Populate(new AreaPopulated(player, 70, 1234, [])));
            await encounter.Placed(player);
            var state = encounter.Of(player)!;
            var placedModSet = RitualTables.RuneModSet(state.Altars[0].Rune, state.Level);
            Assert.Equal(placedModSet is null, state.ModSet is null);
            Assert.Equal(placedModSet?.Mods.Count, state.ModSet?.Mods.Count);
            state.Owner = Program.CharacterName(player);
            Assert.Contains("IsMapOwner", encounter.QuestFlags(player));

            var second = new Player(layout, 2, seed) { Instance = player.Instance };
            Assert.Same(state, encounter.Of(second));
            Assert.Equal(2, encounter.EnterMessages(second).Count());
            Assert.NotNull(encounter.Populate(new AreaPopulated(player, 70, 1234, [])));
            var (lo, hi) = RitualTables.AltarRange(70);
            Assert.InRange(state.Altars.Count, lo, hi);
            Assert.All(state.Altars, a => Assert.True(a.Cell.X >= 0 && a.Cell.Y >= 0 && a.Cell.X < layout.GridW && a.Cell.Y < layout.GridH));
            Assert.Single(state.Altars.Select(a => a.Rune.Row).Distinct());
            Assert.NotEmpty(state.Offers);

            player.Clock.Start();
            var altar = state.Altars[0];
            altar.Cell = layout.Near((layout.SpawnX + 10, layout.SpawnY));
            await encounter.Advance(player, 0.25);
            Assert.True(altar.Shown);
            Assert.NotEqual(0u, altar.Interactable);
            int guards = altar.Tied.Count;
            Assert.True(guards > 0);
            Assert.All(altar.Tied, id => Assert.True(player.Area.Monsters.TryGetValue(id, out var m) && m.Life > 0));
            Assert.Equal(RitualEncounter.Inactive, altar.State);

            await using (var avenger = await ScarabEncounterTests.Wire.Open(instance, layout, seed))
            {
                uint id = altar.Tied.First();
                var monster = player.Area.Monsters[id];
                var at = monster.Walk.Position(Game.AreaTime(player));
                avenger.Player.Walk.Teleport(at.X, at.Y, Game.AreaTime(avenger.Player));
                uint before = monster.Life;
                Game.ItemsOf(avenger.Player).Add(PlayerInventories.MainInventory, 0, 0, ItemCatalog.Create(Program.Items.Find(RitualOmens.Revenge)!));
                using var moreTaken = MonsterModEffects.HandOverMore([], [], [Stat.DamageTakenPlusPctFinalFromAncestralOmen]);
                double oldFactor = monster.Effects.DamageTaken(false);
                await RitualOmens.OnPlayerDied(avenger.Player, 100);
                Assert.Equal(before, monster.Life);
                Assert.Equal(oldFactor * 1.25, monster.Effects.DamageTaken(false), 6);
            }

            player.Clock.Start(); // Opening the second test connection pauses the shared instance clock.
            foreach (uint id in altar.Tied) player.Area.Monsters[id].Life = 0;
            await encounter.Advance(player, 0.25);
            Assert.Equal(RitualEncounter.Ready, altar.State);
            Assert.Equal(guards, altar.Credit.Count);
            Assert.NotEmpty(altar.Pools);

            // Inject 2 bottled Blood-filled Vessel monsters: they must enter Altar 1 once (N=1), clear VesselMonsters,
            // and revive once (N=2) on Altar 2 without duplicating.
            string vesselPath = altar.Credit[0].Path;
            state.VesselMonsters.Add((vesselPath, MonsterRarity.Normal));
            state.VesselMonsters.Add((vesselPath, MonsterRarity.Normal));

            var use = new InstanceTransfer.UseSkillOnObject(altar.Interactable, 0x0266, 0, 0x0408) { Started = true };
            player.Walk.Teleport(altar.Cell.X, altar.Cell.Y, Game.AreaTime(player));
            Assert.True(await encounter.OnUse(player, use, Game.AreaTime(player)));
            Assert.Equal(RitualEncounter.Running, altar.State);
            Assert.Contains(player.StationaryBlockers, b => b.RingRadius == RitualTables.RadiusCells);
            var run = state.Run!;
            Assert.Equal(guards + 2, run.Monsters.Count);
            Assert.Empty(state.VesselMonsters);
            Assert.True(await encounter.OnUse(player, use, Game.AreaTime(player)));
            Assert.Same(run, state.Run);
            await encounter.Entered(second);
            await encounter.Entered(player);

            for (int f = 0; f < 59; f++) await encounter.Advance(player, 1 / 30.0);
            Assert.Equal(0, run.Next);
            for (int f = 0; f < 60 + 3 * (guards + 2); f++) await encounter.Advance(player, 1 / 30.0);
            Assert.Equal(guards + 2, run.Next);
            Assert.NotEmpty(run.Alive);

            int revived = run.Alive.Count;
            foreach (uint id in run.Alive.Keys) player.Area.Monsters[id].Life = 0;
            await encounter.Advance(player, 1 / 30.0);
            Assert.True(state.Tribute >= 10 * revived);
            Assert.Equal(RitualEncounter.Done, altar.State);
            Assert.Null(state.Run);
            Assert.Equal(1, state.Completed);
            Assert.DoesNotContain(player.StationaryBlockers, b => b.RingRadius == RitualTables.RadiusCells);

            // Second altar: bottled monsters must revive once at N=2 via state.Revived, never re-injected at N=1.
            int carriedFromAltar0 = state.Revived.Count;
            var altar1 = state.Altars[1];
            altar1.Cell = layout.Near((layout.SpawnX + 10, layout.SpawnY));
            await encounter.Advance(player, 0.30);
            Assert.True(altar1.Shown);
            foreach (uint id in altar1.Tied) player.Area.Monsters[id].Life = 0;
            await encounter.Advance(player, 0.30);
            Assert.Equal(RitualEncounter.Ready, altar1.State);
            state.SpawnWisp = true;
            var use1 = new InstanceTransfer.UseSkillOnObject(altar1.Interactable, 0x0266, 0, 0x0408) { Started = true };
            player.Walk.Teleport(altar1.Cell.X, altar1.Cell.Y, Game.AreaTime(player));
            Assert.True(await encounter.OnUse(player, use1, Game.AreaTime(player)));
            var run2 = state.Run!;
            Assert.NotEqual(0u, run2.Wisp);
            Assert.DoesNotContain(run2.Wisp, run2.Daemons);
            Assert.Equal(altar1.Credit.Count + carriedFromAltar0, run2.Monsters.Count);
            Assert.Equal(carriedFromAltar0, run2.Monsters.Count(m => m.N == 2));
            Assert.Equal(altar1.Credit.Count, run2.Monsters.Count(m => m.N == 1));
            for (int f = 0; f < 120 + 3 * run2.Monsters.Count && state.Run is not null; f++)
            {
                if (run2.Alive.Count > 0)
                    foreach (uint id in run2.Alive.Keys) player.Area.Monsters[id].Life = 0;
                await encounter.Advance(player, 1 / 30.0);
            }
            Assert.Equal(RitualEncounter.Done, altar1.State);
            Assert.Null(state.Run);
            Assert.Equal(2, state.Completed);

            // Favour window: open (0x0169), defer (0x0284), buy (0x0283), reroll (0x0168).
            state.Tribute = 20000;
            Assert.True(await RitualFavours.HandleOpen(player, encounter));
            var revealed = state.Offers.Where(o => o.Revealed && !o.Purchased).ToList();
            Assert.True(revealed.Count >= 2);
            var toDefer = revealed[0];
            int tribBeforeDefer = state.Tribute;
            Assert.True(await RitualFavours.HandleDefer(player,
                new LoginWriter().U16(RitualWire.DeferFavourId).U32(toDefer.ItemId).ToArray(), encounter));
            Assert.True(toDefer.DeferredThisPage);
            Assert.True(state.Tribute < tribBeforeDefer);
            Assert.Single(RitualFavours.LoadDeferred(player));
            // League isolation: another league on the same account does not see Standard's deferred rewards.
            var otherLeague = new Player(layout, 3, seed) { Instance = player.Instance };
            otherLeague.Character.Account = player.Character.Account;
            otherLeague.Character.League = "Hardcore";
            Assert.Empty(RitualFavours.LoadDeferred(otherLeague));

            var toBuy = revealed[1];
            int tribBeforeBuy = state.Tribute;
            // Requesting locked ExpandedInventory falls back to MainInventory (never deposits into ExpandedInventory).
            Assert.True(await RitualFavours.HandleBuy(player,
                new LoginWriter().U16(RitualWire.BuyFavourId).U32(toBuy.ItemId)
                    .U32(PlayerInventories.ExpandedInventory).U32(0).U32(0).ToArray(), encounter));
            Assert.True(toBuy.Purchased);
            Assert.Equal(tribBeforeBuy - toBuy.BuyCost, state.Tribute);
            Assert.Empty(Game.ItemsOf(player).Items(PlayerInventories.ExpandedInventory));

            Assert.True(await RitualFavours.HandleReroll(player, encounter));
            Assert.Equal(1, state.RerollsUsed);

            // Ritual Vessel bottling on completed altar -> Blood-filled Vessel with captured monsters.
            var vesselCurType = Program.Items.Find(RitualTables.RitualVesselId)!;
            var inv = Game.ItemsOf(player);
            var vesselStack = inv.Add(PlayerInventories.MainInventory, 0, 0, ItemCatalog.Create(vesselCurType));
            var useObjPkt = new LoginWriter().U16(InstanceItems.UseItemOnObjectId)
                .U32(PlayerInventories.InventoryId(PlayerInventories.MainInventory))
                .U32(vesselStack.Id).U32(altar.Interactable).ToArray();
            Assert.True(await encounter.OnUseItemOnObject(player, useObjPkt));
            Assert.True(altar.Bottled);
            var filled = inv.Items(PlayerInventories.MainInventory)
                .FirstOrDefault(i => Program.Items.ByHash(i.Item.TypeHash)?.Id == RitualTables.BloodFilledVesselId);
            Assert.NotNull(filled);
            Assert.NotNull(filled!.Item.RitualEncounter);
            Assert.NotEmpty(filled.Item.RitualEncounter!.Monsters);

            // MapOpening claims "Ritual" and injects Blood-filled Vessel bonuses & serialized monsters.
            var mapSlot = new MapDevice.Slot(0, MapDevice.MapClass, "Strand", 0, [], 0);
            var vesselType = Program.Items.Find(RitualTables.BloodFilledVesselId)!;
            var vesselSlot = new MapDevice.Slot(vesselType.Hash, vesselType.Class, vesselType.Name, vesselType.Row, [], 0)
            {
                BaseId = vesselType.Id,
                SourceItem = filled.Item,
            };
            var opening = new MapDevice.Opening(new(layout.Hash, area.Id, "Strand", 3) { Level = 70 }, MapMods.None);
            var mapReq = new MapOpening(player, [mapSlot, vesselSlot], campaign: false, opening, "");
            encounter.OnMapOpening(mapReq);
            Assert.Contains("Ritual", mapReq.HandledContent);
            Assert.NotNull(mapReq.Opening?.ContentData);
            Assert.True(mapReq.Opening!.ContentData!.ContainsKey(RitualEncounter.VesselsContentKey));
            Assert.Equal(20, mapReq.Opening.Mods.Get(Stat.MapRitualMonsterSpawningSpeedPlusPct));
            Assert.Equal(0, mapReq.Opening.Mods.Get(Stat.MapItemDropQuantityPlusPct)); // MapFragmentMods row 247 already adds +5% IIQ.

            // Omen of Connections: Orb of Fusing links all linkable sockets (keeping Abyss sockets unlinked) and consumes the Omen.
            var omenType = Program.Items.Find(RitualOmens.Connections)!;
            inv.Add(PlayerInventories.MainInventory, 1, 0, ItemCatalog.Create(omenType));
            var bodyType = Program.Items.Find("Metadata/Items/Armours/BodyArmours/BodyStr1")!;
            var unlinked = ItemCatalog.Create(bodyType) with
            {
                ItemLevel = 50,
                Sockets = [new(SocketColour.Red), new(SocketColour.Red), new(SocketColour.Green), new(SocketColour.Abyss)],
                Links = [1, 1, 1, 1],
            };
            uint fusingHash = Program.Items.Find(CurrencyEffects.OrbOfFusing)!.Hash;
            var linked = await RitualOmens.OnCurrencyCrafting(player, unlinked, fusingHash, new Random(1));
            Assert.NotNull(linked);
            Assert.Equal([3, 1], linked!.Links);

            // Omen of Fortune: refuses bases whose only uniques are EncounterOnly/Replica/Corrupted/Mirrored without consuming the Omen.
            var fortuneOmenType = Program.Items.Find(RitualOmens.Fortune)!;
            var fortuneStack = inv.Add(PlayerInventories.MainInventory, 3, 0, ItemCatalog.Create(fortuneOmenType));
            uint chanceHash = Program.Items.Find(CurrencyEffects.OrbOfChance)!.Hash;
            var allUniques = Game.UniquesFor(player).All;
            var excludedOnlyBaseId = allUniques
                .GroupBy(u => u.BaseTypeId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => g.All(u => u.EncounterOnly || u.Corrupted || u.Mirrored || u.Name.StartsWith("Replica ", StringComparison.Ordinal))
                    && Program.Items.Find(g.Key) is { Supported: true })?.Key;
            if (excludedOnlyBaseId is not null)
            {
                var excludedTarget = ItemCatalog.Create(Program.Items.Find(excludedOnlyBaseId)!);
                Assert.Null(await RitualOmens.OnCurrencyCrafting(player, excludedTarget, chanceHash, new Random(1)));
                Assert.Contains(inv.Items(PlayerInventories.MainInventory), i => i.Id == fortuneStack.Id);
            }

            // Act 2 campaign Favour page (level 18): every rolled unique must be drop-eligible (no EncounterOnly, Replica, or T0).
            var lowState = new RitualEncounter.State { Level = 18, Seed = 999 };
            var drops = Game.DropsFor(player);
            for (int pageSeed = 1; pageSeed <= 5; pageSeed++)
            {
                var lowOffers = RitualFavours.GeneratePage(player, lowState, new Random(pageSeed), isReroll: false);
                foreach (var offer in lowOffers)
                {
                    if (offer.RealItem.Rarity != ItemRarity.Unique) continue;
                    Assert.False(offer.RealItem.Corrupted);
                    Assert.False(offer.RealItem.Mirrored);
                    var bt = Program.Items.ByHash(offer.RealItem.TypeHash);
                    Assert.NotNull(bt);
                    Assert.Contains(allUniques, u => u.BaseTypeId.Equals(bt!.Id, StringComparison.OrdinalIgnoreCase)
                        && drops.IsDropEligibleUnique(u, 18) && !u.Corrupted && !u.Mirrored
                        && !u.Name.StartsWith("Replica ", StringComparison.Ordinal));
                }
            }

            var chromatic = Program.Items.Find(CurrencyEffects.ChromaticOrb)!.Hash;
            var tri = Program.Items.Find(RitualOmens.Trichromatism)!;
            inv.Add(PlayerInventories.MainInventory, 5, 0, ItemCatalog.Create(tri));
            Assert.Null(await RitualOmens.OnCurrencyCrafting(player, unlinked, chromatic, new Random(1)));
            // Returning to the same instance still counts the previous Omen.
            await using var rejoined = await ScarabEncounterTests.Wire.Open(instance, layout, seed, player.Character.Name);
            Assert.Null(await RitualOmens.OnCurrencyCrafting(rejoined.Player, unlinked, chromatic, new Random(1)));
            await using var nextArea = await ScarabEncounterTests.Wire.Open(new InstanceState(), layout, seed, player.Character.Name);
            var coloured = await RitualOmens.OnCurrencyCrafting(nextArea.Player, unlinked, chromatic, new Random(1));
            Assert.NotNull(coloured);
            Assert.Equal([SocketColour.Red, SocketColour.Green, SocketColour.Blue], coloured.Sockets.Take(3).Select(s => s.Colour).Order());
            Assert.Equal(SocketColour.Abyss, coloured.Sockets[3].Colour);

            // Omen of Refreshment: refills flasks in-place via GainFlaskCharges (preserving item ID and per-flask MaxCharges).
            var flaskType = Program.Items.Find("Metadata/Items/Flasks/FlaskLife1")!;
            await using var fresh = await ScarabEncounterTests.Wire.Open(new InstanceState(), layout, seed);
            var lowLife = fresh.Player;
            var bag = Game.ItemsOf(lowLife);
            var addedFlask = bag.Add(PlayerInventories.Flask, 0, 0, ItemCatalog.Create(flaskType) with { Charges = 0 });
            var refreshOmen = Program.Items.Find(RitualOmens.Refreshment)!;
            bag.Add(PlayerInventories.MainInventory, 2, 0, ItemCatalog.Create(refreshOmen));
            lowLife.Pools = Game.PoolsFor(lowLife, 70);
            lowLife.PlayerLife = Math.Max(1u, lowLife.PlayerMaxLife / 5);
            await RitualOmens.Tick(lowLife);
            var refilledFlask = bag.Items(PlayerInventories.Flask).Single(i => i.Id == addedFlask.Id);
            Assert.Equal(addedFlask.Id, refilledFlask.Id);
            Assert.Equal(Program.Items.FlaskChargesOf(flaskType)!.MaxCharges, refilledFlask.Item.Charges);

            await using (var heir = await ScarabEncounterTests.Wire.Open(new InstanceState(), layout, seed))
            {
                var gear = Game.ItemsOf(heir.Player);
                var fireball = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemFireball")!) with { GemExperience = 0 };
                var wand = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1")!)
                    with { Sockets = [new(SocketColour.Blue, fireball)] };
                gear.Put(PlayerInventories.Weapon1, new InventoryItem(gear.NewItemId(), 0, 0, wand));
                gear.Add(PlayerInventories.MainInventory, 0, 0, ItemCatalog.Create(Program.Items.Find(RitualOmens.Bequeathal)!));
                await RitualOmens.OnPlayerDied(heir.Player, 10);
                Assert.Equal(30, gear.Items(PlayerInventories.Weapon1).Single().Item.Sockets[0].Gem!.GemExperience);
            }
            await using (var pilgrim = await ScarabEncounterTests.Wire.Open(new InstanceState(), layout, seed))
            {
                Game.ItemsOf(pilgrim.Player).Add(PlayerInventories.MainInventory, 0, 0, ItemCatalog.Create(Program.Items.Find(RitualOmens.Acceleration)!));
                await RitualOmens.OnLevelUp(pilgrim.Player, 70);
                Assert.Single(pilgrim.Player.Instance!.ContentState.Keys, k => k.StartsWith("Shrines/id/", StringComparison.Ordinal));
            }

            foreach (var (penalty, remaining) in new[] { (5, 1.25m), (10, 2.5m) })
            {
                await using var death = await ScarabEncounterTests.Wire.Open(new InstanceState(), layout, seed);
                Game.ItemsOf(death.Player).Add(PlayerInventories.MainInventory, 0, 0,
                    ItemCatalog.Create(Program.Items.Find(RitualOmens.Amelioration)!));
                Assert.Equal(remaining, await RitualOmens.OnDeathPenaltyPct(death.Player, penalty));
            }

            foreach (var (omenId, stat, expected) in new[]
            {
                (RitualOmens.DeathDancing, Stat.AvoidDamagePct, 75),
                (RitualOmens.Resurgence, Stat.DamageTakenGoesToLifeOver4SecondsPct, 100),
                (RitualOmens.Adrenaline, Stat.DamagePlusPct, 100),
                (RitualOmens.Brilliance, Stat.ExperienceGainPlusPct, 100),
                (RitualOmens.SoulDevourer, Stat.MapPlayerConsumeNearbySouls, 1)
            })
            {
                await using var recipient = await ScarabEncounterTests.Wire.Open(new InstanceState(), layout, seed);
                var subject = recipient.Player;
                Game.ItemsOf(subject).Add(PlayerInventories.MainInventory, 0, 0, ItemCatalog.Create(Program.Items.Find(omenId)!));
                subject.PlayerLife = 1;
                if (omenId == RitualOmens.Brilliance || omenId == RitualOmens.SoulDevourer)
                    await RitualOmens.OnLevelUp(subject, 70);
                else
                    await RitualOmens.Tick(subject);
                var sheet = new StatSheet(subject);
                RitualOmens.ApplyStats(sheet);
                Assert.Equal(expected, sheet.Added.GetValueOrDefault(stat));
                Assert.Equal(1u, subject.PlayerLife);
            }
        }
        finally
        {
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseOwnSaves();
        }
    }
}
