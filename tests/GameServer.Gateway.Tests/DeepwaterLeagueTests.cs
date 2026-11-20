using System.Reflection;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Modding;
using GameServer.Modules.Deepwater;

namespace GameServer.Gateway.Tests;

public sealed class DeepwaterLeagueTests
{
    // The Allflame leagues list the Deepwater module, which answers the league category the client's topology pick and
    // Deepwater pin read (LeagueCategoryToDisplayName's id for LeagueNames "Deepwater") and map_deepwater_league on every
    // area; every other league keeps category 60 and no league stat, whatever its name.
    [Fact]
    public void Allflame_leagues_play_in_the_Deepwater_category_with_the_league_stat()
    {
        var leagues = LeagueCatalog.FromFolder("Leagues");
        Assert.Empty(leagues.Errors);
        var allflame = leagues.All.Where(l => l.Base == "Allflame").ToList();
        Assert.Equal(8, allflame.Count);
        Assert.All(allflame, l => Assert.Contains("Core/Content/Deepwater", l.Modules));
        Assert.All(leagues.All.Where(l => l.Base != "Allflame"), l => Assert.DoesNotContain("Core/Content/Deepwater", l.Modules));

        var lifetime = new ModuleLifetime("Deepwater");
        try
        {
            new DeepwaterModule().Load(Context(lifetime));
            uint flag = Program.Stats.Key("map_deepwater_league")!.Value;
            var request = new AreaStatsRequest("Exile", null, 0x7ad2 /* 1_1_2 The Coast */, null);
            foreach (var league in allflame)
            {
                var runtime = new LeagueRuntime(league, ServerLeagues.ModulesOf(league));
                Assert.Equal(59, InstanceEvents.ComposeLeagueCategory(runtime, request with { League = league.Name }));
                Assert.Equal(1, InstanceEvents.ComposeAreaStats(runtime, request with { League = league.Name }).GetValueOrDefault(flag));
            }
            var standard = new LeagueRuntime(leagues.Find("Standard")!, ServerLeagues.ModulesOf(leagues.Find("Standard")!));
            var named = request with { League = "Deepwater Standard" };
            Assert.Equal(LeagueCategory.None, InstanceEvents.ComposeLeagueCategory(standard, named));
            Assert.False(InstanceEvents.ComposeAreaStats(standard, named).ContainsKey(flag));
        }
        finally
        {
            lifetime.Stop(ex => throw ex);
            GameEventOwners.Release("Deepwater", GameEventOwners.ModuleRank);
        }
    }

    // ValerieTutorial (Stats, Functions, StateMachine after the base six) is written: Stats as a monster's with no pairs
    // (compact count, u32, u32, u8), nothing for Functions, then her StateMachine (2 states, 2 timers).
    // A lantern placement (C->S 0x2ca) is framed, and a lantern ends its blob with its light task.
    [Fact]
    public void Allflame_objects_and_ruleset_actions_are_written()
    {
        var valerie = NpcCatalog.Load().Find("Metadata/NPC/League/Deepwater/ValerieTutorial");
        Assert.NotNull(valerie);
        Assert.True(valerie.Supported);
        byte[] blob = new NpcObject(236, 190, valerie.Components!, 0, valerie.StateMachine, valerie.TransitionState).Blob();
        Assert.Equal("0000" + "EC000000" + "BE000000" + "00000000" + "0000" + "00020001" + "00" + "00" + "01FF" + "00"
            + "00" + "00000000" + "00000000" + "00"
            + "0202" + "0000" + "0000" + "0000000000" + "0000000000" + "00", Convert.ToHexString(blob));

        // A lantern placement (C->S 0x2ca for ruleset 171, opcode 0 and the cell) is framed, so the tick after it survives.
        byte[] place = Convert.FromHexString("02CAAB09" + "00" + "EC000000" + "BE000000");
        var split = InstanceProtocol.SplitClientMessages([.. place, .. Tick()], out byte[] partial, out byte[] unknown);
        Assert.Empty(partial);
        Assert.Empty(unknown);
        Assert.Equal([(ushort)0x2ca, (ushort)0x1b], split.Select(m => m.Id));
        var (row, payload) = InstanceRulesets.ReadAction(split[0].Bytes);
        Assert.Equal(0xab, row);
        Assert.Equal(place[4..], payload);

        // Right-clicking a removable buff (C->S 0x004b, 6 bytes: u32 BE buff instance id) is framed too.
        byte[] cancelBuff = Convert.FromHexString("004B0000002A");
        var cancelSplit = InstanceProtocol.SplitClientMessages([.. cancelBuff, .. Tick()], out byte[] cancelPartial, out byte[] cancelUnknown);
        Assert.Empty(cancelPartial);
        Assert.Empty(cancelUnknown);
        Assert.Equal([(ushort)InstanceEntities.CancelBuffId, (ushort)0x1b], cancelSplit.Select(m => m.Id));
        Assert.Equal(0x2au, InstanceEntities.ReadCancelBuff(cancelSplit[0].Bytes));

        // The Lantern's components and its DeepwaterLantern task (GameObjectTasks HASH16 0xc4df, LE) close its blob.
        byte[] light = new byte[16];
        byte[] lantern = new QuestObject(236, 190, ["Positioned", "BaseEvents", "Animated", "Buffs", "StateMachine", "MinimapIcon", "Functions"])
        {
            StateMachine = new StateMachineData([0], 0),
            Tasks = [(0xc4df, light)],
        }.Blob();
        Assert.Equal("01DFC4" + Convert.ToHexString(light), Convert.ToHexString(lantern[^19..]));

        // An int script variable (type 9): this.RandomNumber = 3 in the base data.
        Assert.Equal(0xe70679ebu, QuestObject.VariableHash("RandomNumber"));
        byte[] numbered = new QuestObject(0, 0, ["Positioned"]) { IntVariables = new Dictionary<uint, int> { [0xe70679eb] = 3 } }.Blob();
        Assert.Equal("0001" + "EB7906E7" + "000109" + "03000000", Convert.ToHexString(numbered[..13]));

        // A chart base writes its DeepwaterChart part (u8 Area Level, u8 rotation, DeepwaterRooms row; a row past the end
        // crashes the client) last, and the header
        // flags (0x0006: a Dredged item, accepted by the capsule grid) follow the type hash.
        var chart = new ItemCatalog("data").Find("Metadata/Items/Deepwater/ChartSandy");
        Assert.NotNull(chart);
        Assert.True(chart.Supported);
        byte[] item = (ItemCatalog.Create(chart) with { HeaderFlags = 6, Chart = new(68, 3, 3) }).Payload();
        Assert.Equal("0600", Convert.ToHexString(item[4..6]));
        Assert.Equal("440303", Convert.ToHexString(item[^3..]));

        // Ctrl + click into the capsule (C->S 0x0112, live: item 7 from the main inventory) is framed and read.
        split = InstanceProtocol.SplitClientMessages([.. Convert.FromHexString("0112000000070001"), .. Tick()], out partial, out unknown);
        Assert.Empty(unknown);
        Assert.Equal([(ushort)0x112, (ushort)0x1b], split.Select(m => m.Id));
        Assert.Equal(new InstanceItems.LeagueWindowMove(7, false, true), InstanceItems.ReadLeagueWindowMove(split[0].Bytes));

        // The voyage planner's start (C->S 0x2ca for ruleset 172: 11 bytes, op 0, 9 cells) is framed and read.
        byte[] start = Convert.FromHexString("02CAAC0B" + "00" + "09" + "000102030405060708");
        split = InstanceProtocol.SplitClientMessages([.. start, .. Tick()], out partial, out unknown);
        Assert.Empty(partial);
        Assert.Empty(unknown);
        Assert.Equal([(ushort)0x2ca, (ushort)0x1b], split.Select(m => m.Id));
        (row, payload) = InstanceRulesets.ReadAction(split[0].Bytes);
        Assert.Equal(0xac, row);
        Assert.Equal(start[4..], payload);

        // Phase 6: HeistRewardDisplay component serialization (GhostflameCraftingOutput vat: empty and populated with mod diff).
        var catalog = new ItemCatalog("data");
        var rolls = new ItemRolls(catalog);
        var rng = new Random(42);
        string[] vatComponents = ["Positioned", "BaseEvents", "Animated", "InteractionAction", "HeistRewardDisplay", "StateMachine", "Functions"];
        Assert.True(QuestObject.CanSerialize(vatComponents, haveStateMachine: true));
        byte[] emptyVat = new QuestObject(315, 164, vatComponents) { StateMachine = new StateMachineData([0, 0], 0) }.Blob();
        // Tail: HeistRewardDisplay (00 00 00000000) + StateMachine (02 00 0000 0000) + Tasks (00).
        Assert.Equal("000000000000" + "020000000000" + "00", Convert.ToHexString(emptyVat[^13..]));

        var beltType = catalog.Find("Metadata/Items/Belts/Belt1")!;
        var primaryItem = ItemCatalog.Create(beltType) with { ItemLevel = 84, Intangibility = 37 };
        var secondaryItem = ItemCatalog.Create(beltType) with { ItemLevel = 84, Intangibility = 12 };
        byte[] primaryBytes = primaryItem.Payload();
        byte[] secondaryBytes = secondaryItem.Payload();
        Assert.Equal(37, primaryBytes[^1]);
        byte[] fullVat = new QuestObject(315, 164, vatComponents)
        {
            RewardDisplay = new RewardDisplayData(primaryItem, secondaryItem, OpenAttempts: 1),
            StateMachine = new StateMachineData([1, 0], 0),
        }.Blob();
        string expectedDisplayHex = "0101" + Convert.ToHexString(primaryBytes) + Convert.ToHexString(secondaryBytes) + "01000000";
        Assert.Contains(expectedDisplayHex + "020001000000" + "00", Convert.ToHexString(fullVat));

        // Phase 6: 3-byte C->S 0x008b ("TAKE ALL" on multi-item quest reward offers like #111 / #139).
        split = InstanceProtocol.SplitClientMessages([.. Convert.FromHexString("008B00"), .. Tick()], out partial, out unknown);
        Assert.Empty(partial);
        Assert.Empty(unknown);
        Assert.Equal([(ushort)InstanceNpcs.TakeAllRewardsId, (ushort)0x1b], split.Select(m => m.Id));
        Assert.Equal((byte)0, InstanceNpcs.ReadTakeAllRewards(split[0].Bytes));

        // Phase 6: 8-byte quick-move framing for 0x010e (Chart), 0x010f (Crafting), 0x0115 (Cursed Ducats).
        foreach (var (opcode, hex, expected) in new (ushort, string, InstanceItems.LeagueWindowMove)[]
        {
            (InstanceItems.MissionSlotMoveId, "010E000000090001", new(9, false, true)),
            (InstanceItems.CraftingSlotMoveId, "010F0000000B0101", new(11, true, true)),
            (InstanceItems.SocketStorageMoveId, "01150000000D0101", new(13, true, true)),
        })
        {
            split = InstanceProtocol.SplitClientMessages([.. Convert.FromHexString(hex), .. Tick()], out partial, out unknown);
            Assert.Empty(partial);
            Assert.Empty(unknown);
            Assert.Equal([opcode, (ushort)0x1b], split.Select(m => m.Id));
            Assert.Equal(expected, InstanceItems.ReadLeagueWindowMove(split[0].Bytes));
        }

        // Phase 6: QuestRewardCatalog per-choice RequiredLeagueFlag & Offer.Reclaimable (#139 Cursed Ducats, #111 Voidstones).
        var rewardCatalog = QuestRewardCatalog.Load();
        var cursedOffer = rewardCatalog.Find(139);
        Assert.NotNull(cursedOffer);
        Assert.True(cursedOffer.Reclaimable);
        Assert.Equal([1905, 1906, 1886, 1907, 1908, 1909], cursedOffer.Choices.Select(c => c.RequiredLeagueFlag).ToArray());
        var voidstoneOffer = rewardCatalog.Find(111);
        Assert.NotNull(voidstoneOffer);
        Assert.True(voidstoneOffer.Reclaimable);
        Assert.Equal([219, 220, 708, 709], voidstoneOffer.Choices.Select(c => c.RequiredLeagueFlag).ToArray());
        var campaignOffer = rewardCatalog.Find(1);
        Assert.NotNull(campaignOffer);
        Assert.False(campaignOffer.Reclaimable);

        // Phase 6: Vesper's Allflame Crafting currency resolution & exact two-division Sulphur cost formula.
        var exaltType = catalog.Find("Metadata/Items/Currency/CurrencyAddModToRare")!;
        var bindingType = catalog.Find("Metadata/Items/Currency/CurrencyUpgradeToRareAndSetSockets")!;
        var changelingType = catalog.Find(DeepwaterDucats.Changeling)!;
        var exaltT0 = DeepwaterCrafting.ResolveCurrency(exaltType.Hash, _ => false);
        var exaltT2 = DeepwaterCrafting.ResolveCurrency(exaltType.Hash, f => f is 1876 or 1877);
        Assert.NotNull(exaltT0);
        Assert.NotNull(exaltT2);
        Assert.Equal(2, exaltT0.MaxCopies);
        Assert.Equal(4, exaltT2.MaxCopies);
        Assert.Equal(5310, DeepwaterCrafting.SulphurCost(exaltT2, primaryItem, beltType, hardMode: false));
        var jewelType = catalog.Find("Metadata/Items/Jewels/JewelDex")!;
        var jewelItem = ItemCatalog.Create(jewelType) with { ItemLevel = 84 };
        Assert.Equal(3375, DeepwaterCrafting.SulphurCost(exaltT2, jewelItem, jewelType, hardMode: false));
        var chart67 = ItemCatalog.Create(chart) with { ItemLevel = 67, Chart = new(67, 0, 3) };
        Assert.Equal(742, DeepwaterCrafting.SulphurCost(exaltT2, chart67, chart, hardMode: false));
        Assert.Equal(5625, DeepwaterCrafting.SulphurCost(exaltT2, ItemCatalog.Create(beltType) with { ItemLevel = 67 }, beltType, hardMode: true));
        var changelingRow = DeepwaterCrafting.ResolveCurrency(changelingType.Hash, _ => false);
        Assert.NotNull(changelingRow);
        Assert.Equal(1000, DeepwaterCrafting.SulphurCost(changelingRow, null, null, hardMode: false));
        Assert.Equal(2500, DeepwaterCrafting.SulphurCost(changelingRow, null, null, hardMode: true));
        var bindingRow = DeepwaterCrafting.ResolveCurrency(bindingType.Hash, _ => false);
        Assert.NotNull(bindingRow);
        Assert.True(bindingRow.DisabledInRuthless);

        // Phase 6: All 11 Ducats smoke check.
        var changelingOut = DeepwaterDucats.Apply(catalog, rolls, DeepwaterDucats.ChangelingHash, null, null, rng);
        Assert.NotNull(changelingOut);
        Assert.Contains(catalog.ByHash(changelingOut.TypeHash)!.Id, DeepwaterDucats.FunctionalDucats);

        var rareBelt = rolls.Roll(ItemCatalog.Create(beltType) with { ItemLevel = 84 }, ItemRarity.Rare, rng);
        var kisharaOut = DeepwaterDucats.Apply(catalog, rolls, DeepwaterDucats.KisharaHash, rareBelt, beltType, rng);
        Assert.NotNull(kisharaOut);
        Assert.Equal(ItemRarity.Rare, kisharaOut.Rarity);
        Assert.False(kisharaOut.Split);
        Assert.Equal(1, kisharaOut.Mods.Count(m => m.Section == ModSection.Explicit));

        var abyssType = catalog.Find("Metadata/Items/Jewels/JewelAbyssMelee")!;
        var rareAbyss = rolls.Roll(ItemCatalog.Create(abyssType) with { ItemLevel = 84 }, ItemRarity.Rare, rng);
        while (rareAbyss.Mods.Count(m => m.Section == ModSection.Explicit) < 4)
            rareAbyss = rolls.AddAffix(rareAbyss, (2, 2), rng) ?? throw new InvalidOperationException("failed to reach 4 mods on abyss jewel");
        var merrickOut = DeepwaterDucats.Apply(catalog, rolls, DeepwaterDucats.MerrickHash, rareAbyss, abyssType, rng);
        Assert.NotNull(merrickOut);
        Assert.True(merrickOut.Corrupted);
        Assert.Equal(5, merrickOut.Mods.Count(m => m.Section == ModSection.Explicit));

        var glovesType = catalog.Find("Metadata/Items/Armours/Gloves/GlovesStr1")!;
        var fourSockGloves = ItemCatalog.Create(glovesType) with
        {
            ItemLevel = 84,
            Rarity = ItemRarity.Rare,
            Sockets = [new(SocketColour.Red), new(SocketColour.Red), new(SocketColour.Red), new(SocketColour.Red)],
            Links = [4],
        };
        var katakohiOut = DeepwaterDucats.Apply(catalog, rolls, DeepwaterDucats.KatakohiHash, fourSockGloves, glovesType, rng);
        Assert.NotNull(katakohiOut);
        int enchantCount = katakohiOut.Mods.Count(m => m.Section == ModSection.Enchant);
        Assert.InRange(enchantCount, 1, 3);
        Assert.Equal(4 - enchantCount, katakohiOut.Sockets.Count);

        var amuletType = catalog.Find("Metadata/Items/Amulets/Amulet1")!;
        var amuletItem = ItemCatalog.Create(amuletType) with { ItemLevel = 84, Rarity = ItemRarity.Rare };
        var ukatoaOut = DeepwaterDucats.Apply(catalog, rolls, DeepwaterDucats.UkatoaHash, amuletItem, amuletType, rng);
        Assert.NotNull(ukatoaOut);
        Assert.Equal(1, ukatoaOut.Mods.Count(m => m.Section == ModSection.Implicit));
        Assert.True(rolls.HasEldritchImplicit(ukatoaOut));
        var simplexType = catalog.Find("Metadata/Items/Amulets/AmuletE2")!;
        Assert.Null(DeepwaterDucats.Apply(catalog, rolls, DeepwaterDucats.UkatoaHash, ItemCatalog.Create(simplexType) with { ItemLevel = 84, Rarity = ItemRarity.Rare }, simplexType, rng));

        var strMod = catalog.Mods.Row(0)!;
        var strBelt = ItemCatalog.Create(beltType) with { ItemLevel = 84, Rarity = ItemRarity.Rare, Mods = [new ItemMod(ModSection.Explicit, strMod.Hash, [10])] };
        var genteelOut = DeepwaterDucats.Apply(catalog, rolls, DeepwaterDucats.GenteelHash, strBelt, beltType, rng);
        Assert.NotNull(genteelOut);
        Assert.NotEqual(strMod.Hash, genteelOut.Mods.Single(m => m.Section == ModSection.Explicit).Hash);

        var openRareBelt = ItemCatalog.Create(beltType) with { ItemLevel = 84, Rarity = ItemRarity.Rare, Mods = [new ItemMod(ModSection.Explicit, strMod.Hash, [10])] };
        var brinehookOut = DeepwaterDucats.Apply(catalog, rolls, DeepwaterDucats.BrinehookHash, openRareBelt, beltType, rng);
        Assert.NotNull(brinehookOut);
        Assert.Contains(brinehookOut.Mods, m => catalog.Mods.ByHash(m.Hash)?.Families.Contains(1047) == true);

        var rotmotherOut = DeepwaterDucats.Apply(catalog, rolls, DeepwaterDucats.RotmotherHash, openRareBelt, beltType, rng);
        Assert.NotNull(rotmotherOut);
        Assert.Contains(rotmotherOut.Mods, m => catalog.Mods.ByHash(m.Hash)?.Families.Contains(7814) == true);

        var bodyType = catalog.Find("Metadata/Items/Armours/BodyArmours/BodyStr1")!;
        var rareBody = rolls.Roll(ItemCatalog.Create(bodyType) with { ItemLevel = 84 }, ItemRarity.Rare, rng);
        var cyaxanOut = DeepwaterDucats.Apply(catalog, rolls, DeepwaterDucats.CyaxanHash, rareBody, bodyType, rng);
        Assert.NotNull(cyaxanOut);
        Assert.Contains(cyaxanOut.Mods, m => catalog.Mods.ByHash(m.Hash)?.Domain == 41);

        var telesiaOut = DeepwaterDucats.Apply(catalog, rolls, DeepwaterDucats.TelesiaHash, chart67, chart, rng);
        Assert.NotNull(telesiaOut?.Chart);
        Assert.Equal(67, telesiaOut.Chart.Value.AreaLevel);

        Assert.Null(DeepwaterDucats.ApplyTzamoto(ItemCatalog.Create(beltType), rng, out bool noGhostDestroyed));
        Assert.False(noGhostDestroyed);
        bool sawSurvive = false, sawDestroy = false;
        for (int i = 0; i < 40 && !(sawSurvive && sawDestroy); i++)
        {
            var tz = DeepwaterDucats.ApplyTzamoto(primaryItem, rng, out bool destroyed);
            if (destroyed) { Assert.Null(tz); sawDestroy = true; }
            else { Assert.NotNull(tz); Assert.Equal(0, tz.Intangibility); sawSurvive = true; }
        }
        Assert.True(sawSurvive && sawDestroy);

        // Phase 6: Cursed Ducats scaling, deduplication, and RunEffects.
        Assert.Equal(6, DeepwaterCursedDucats.All.Count);
        Assert.NotNull(DeepwaterCursedDucats.StatsForCount(1));
        Assert.NotNull(DeepwaterCursedDucats.StatsForCount(2));
        Assert.NotNull(DeepwaterCursedDucats.StatsForCount(3));
        var d0 = ItemCatalog.Create(catalog.Row(DeepwaterCursedDucats.All[0].BaseRow)!);
        var d1 = ItemCatalog.Create(catalog.Row(DeepwaterCursedDucats.All[1].BaseRow)!);
        var cursedGlobal = new Dictionary<uint, int>();
        Assert.Equal(2, DeepwaterCursedDucats.InjectStats([d0, d0, d1], catalog.Mods, cursedGlobal));
        var cursedRun = DeepwaterCursedDucats.EffectsFor(cursedGlobal, rng);
        Assert.Equal(1.4, cursedRun.QuantityFactor, 6);
        Assert.Equal(1.8, cursedRun.RarityFactor, 6);
        Assert.Equal(100, cursedRun.Tankiness);

        // Phase 6: Messages in a Bottle catalog, unique messages, and reward building.
        Assert.Equal(14, DeepwaterBottles.OutcomesFor(hardMode: false).Count);
        Assert.Equal(12, DeepwaterBottles.OutcomesFor(hardMode: true).Count);
        Assert.Equal(11, DeepwaterBottles.UniqueMessages.Count);
        Assert.NotNull(DeepwaterBottles.UniqueMessageRow("Kalandra's Touch"));
        var uniques = new UniqueCatalog(catalog);
        var chainHookOutcome = DeepwaterBottles.OutcomesFor(false).Single(o => o.BaseId == "BottledChainHookOfAngling");
        var chainHookReward = DeepwaterBottles.BuildReward(chainHookOutcome, catalog, uniques, _ => 1.0, 68, rng);
        Assert.NotNull(chainHookReward);
        Assert.Equal(1, chainHookReward.Item.GemVariant);
        var divineOutcome = DeepwaterBottles.OutcomesFor(false).Single(o => o.BaseId == "BottledDivineOrb");
        var divineReward = DeepwaterBottles.BuildReward(divineOutcome, catalog, uniques, _ => 1.0, 68, rng);
        Assert.NotNull(divineReward);
        Assert.Equal("Metadata/Items/Currency/CurrencyModValues", catalog.ByHash(divineReward.Item.TypeHash)!.Id);

        // Phase 7: QuestStates league-flag completion (Q209–211) & Quests.League.EveryCharacter drops LQF 1834.
        Assert.False(QuestStates.Default.Value.Complete("DeepwaterTutorial", _ => false));
        Assert.True(QuestStates.Default.Value.Complete("DeepwaterTutorial", _ => false, f => f == 1823));
        Assert.False(QuestStates.Default.Value.Complete("DeepwaterFairgravesAllflameQuest", _ => false));
        Assert.True(QuestStates.Default.Value.Complete("DeepwaterFairgravesAllflameQuest", _ => false, f => f == 1859));
        Assert.False(QuestStates.Default.Value.Complete("DeepwaterVelkaQuest", _ => false));
        Assert.True(QuestStates.Default.Value.Complete("DeepwaterVelkaQuest", _ => false, f => f == 1888));
        Assert.DoesNotContain(1834, Quests.League.EveryCharacter);

        // Phase 7: AreaTransitionObject optional trailing StateMachine (Boat_AreaTransition_Animate).
        string[] boatTransitionComponents = [.. AreaTransitionObject.ComponentOrder, "StateMachine"];
        Assert.True(AreaTransitionObject.CanSerialize(boatTransitionComponents));
        byte[] boatTransitionBlob = new AreaTransitionObject(259, 213, 0x1234) { StateMachine = new StateMachineData([], 0) }.Blob();
        Assert.Equal("000000", Convert.ToHexString(boatTransitionBlob[^3..]));

        // Phase 7: NPCTalk league-flag opt-in (Valerie #3932 sets LQF 1834; #3955 uses __col17 = 1 manual sentinel).
        var npcCatalog = NpcCatalog.Load();
        var valerieTalks = npcCatalog.TalksOf(valerie).ToDictionary(t => t.Row);
        Assert.Contains(1834, valerieTalks[3932].LeagueFlagsSet);
        Assert.Null(valerieTalks[3955].StartLeagueFlag);

        // Phase 7: Ship upgrades (DeepwaterUpgrades#0–4), turn-in copy-cap progression, and bench stage props.
        Assert.Equal(5, DeepwaterUpgrades.ShipUpgradeRows.Count);
        Assert.All(DeepwaterUpgrades.ShipUpgradeRows, u => Assert.NotEmpty(u.Stats));
        var upgradeStats = new Dictionary<uint, int>();
        Assert.Equal(5, DeepwaterUpgrades.InjectStats(DeepwaterUpgrades.TotalTurnedInLeagueFlags.ToHashSet(), upgradeStats));
        Assert.Equal(1, DeepwaterUpgrades.ExtraLanternsFromStats(upgradeStats));
        var (count1, flags1) = DeepwaterUpgrades.ComputeTurnInFlags(new HashSet<int>(), 0);
        Assert.Equal(1, count1);
        Assert.Contains(1824, flags1);
        Assert.Contains(1829, flags1);
        Assert.Contains(DeepwaterUpgrades.ThreeOutputCraftLeagueFlag, flags1);
        var (count3, flags3) = DeepwaterUpgrades.ComputeTurnInFlags(new HashSet<int> { 1824, 1825, 1829, 1830, 1876 }, 2);
        Assert.Equal(3, count3);
        Assert.Contains(1826, flags3);
        Assert.Contains(1831, flags3);
        Assert.Contains(DeepwaterUpgrades.FourOutputCraftLeagueFlag, flags3);
        Assert.Contains(DeepwaterUpgrades.EndgameCraftLeagueFlag, flags3);
        Assert.Contains(DeepwaterUpgrades.SpecialCraftsUnlockedLeagueFlag, flags3);
        var (count5, flags5) = DeepwaterUpgrades.ComputeTurnInFlags(new HashSet<int> { 1824, 1825, 1826, 1827, 1829, 1830, 1831, 1832, 1876, 1877, 1878, 1818 }, 4);
        Assert.Equal(5, count5);
        Assert.Contains(1828, flags5);
        Assert.Contains(1833, flags5);
        for (int stage = 1; stage <= DeepwaterUpgrades.BenchStageTypes.Length; stage++)
        {
            var comps = DeepwaterUpgrades.BenchStageComponents(stage);
            var sm = DeepwaterUpgrades.BenchStageStateMachine(stage);
            Assert.True(QuestObject.CanSerialize(comps, haveStateMachine: true));
            Assert.NotEmpty(new QuestObject(331, 146, comps) { StateMachine = sm }.Blob());
        }

        // Phase 7: Act 1 Deepwater Fairgraves (1_1_9) & Kishara's Rest bosses (#551, #10582, #10583) + Velka drops & Bloodline.
        Assert.True(QuestObject.CanSerialize(DeepwaterFairgraves.BarrierComponents(), haveStateMachine: false));
        Assert.NotEmpty(DeepwaterFairgraves.BuildBarrierObject(100, 100, 0f, null, null, open: false).Blob());
        Assert.NotEmpty(DeepwaterFairgraves.BuildBarrierObject(100, 100, 0f, null, null, open: true).Blob());
        var monsters = new MonsterCatalog("data");
        foreach (int varietyRow in new[] { 551, DeepwaterVelka.CressieRow, DeepwaterVelka.VelkaRow })
        {
            var monster = monsters.Find(varietyRow.ToString());
            Assert.NotNull(monster);
            Assert.True(monster.Supported);
        }
        Assert.Equal(100, DeepwaterVelka.UniqueDropWeights.Sum(w => w.Weight));
        var stormSapphire = DeepwaterVelka.CreateVelkaUnique(uniques, catalog, "Stormblood Sapphire Flask", rng);
        var stormTopaz = DeepwaterVelka.CreateVelkaUnique(uniques, catalog, "Stormblood Topaz Flask", rng);
        Assert.NotNull(stormSapphire);
        Assert.NotNull(stormTopaz);
        Assert.NotEqual(stormSapphire.TypeHash, stormTopaz.TypeHash);
        Assert.NotNull(DeepwaterVelka.CreateVelkaUnique(uniques, catalog, "Seablister", rng));
        Assert.NotNull(DeepwaterVelka.CreateVelkaUnique(uniques, catalog, "The Crustacean's Call", rng));
        var pearlOne = DeepwaterVelka.CreateVelkaUnique(uniques, catalog, "Pearl of Tsoatha", rng, twoSupportPearl: false);
        var pearlTwo = DeepwaterVelka.CreateVelkaUnique(uniques, catalog, "Pearl of Tsoatha", rng, twoSupportPearl: true);
        Assert.NotNull(pearlOne);
        Assert.NotNull(pearlTwo);
        ushort secondSupportHash = catalog.Mods.ById(DeepwaterVelka.PearlSupportTwoModId)!.Hash;
        Assert.DoesNotContain(pearlOne.Mods, m => m.Hash == secondSupportHash);
        Assert.Contains(pearlTwo.Mods, m => m.Hash == secondSupportHash);
        Assert.True(catalog.Find(DeepwaterVelka.CoursingCurrentSupportId)?.Supported);
        Assert.True(DeepwaterVelka.IsQuestDrop(catalog, ItemCatalog.Create(catalog.Find(DeepwaterVelka.BossAllflameId)!)));
        Assert.False(DeepwaterVelka.IsQuestDrop(catalog, primaryItem));
        Assert.Equal(DeepwaterVelka.BloodlineRow, new DescendancyCatalog().Find(DeepwaterVelka.BloodlineId)?.Index);
        foreach (string objType in new[]
        {
            DeepwaterVelka.DescensionObjectType,
            DeepwaterVelka.BarrierType,
            DeepwaterVelka.FightBarrierType,
            DeepwaterVelka.ArenaTeleporterType,
            DeepwaterVelka.InvisibleLanternType,
            "Metadata/Terrain/Leagues/Deepwater/Objects/CorpseBattery",
        })
        {
            var info = QuestObjectTypes.Find(objType);
            Assert.NotNull(info);
            Assert.True(QuestObject.CanSerialize(info.Components, haveStateMachine: info.Components.Contains("StateMachine")));
        }
    }

    static byte[] Tick()
    {
        byte[] tick = new byte[InstanceProtocol.KnownClientLength(0x1b) ?? throw new InvalidOperationException("0x1b has no fixed length")];
        tick[1] = 0x1b;
        return tick;
    }

    static IModuleContext Context(IModuleLifetime lifetime)
    {
        var context = DispatchProxy.Create<IModuleContext, SurvivorsLeagueTests.HookContext>();
        ((SurvivorsLeagueTests.HookContext)(object)context).Lifetime = lifetime;
        return context;
    }
}
