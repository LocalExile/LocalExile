using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Gateway.Tests;

public sealed class CampaignRetailParityTests : IDisposable
{
    public void Dispose() => TestEnvironment.ReleaseOwnSaves();

    [Fact]
    public void Campaign_retail_settings_drops_and_vendors_follow_3_29_parity()
    {
        TestEnvironment.OpenOwnSaves("campaign-retail-parity");
        var settings = SystemSettings.Default;
        var originalValues = new Dictionary<string, double>(settings.Values, StringComparer.OrdinalIgnoreCase);

        // 1. All 24 retail 3.29 system_settings defaults loaded from world.db.
        Assert.Equal(16.0, settings.DropNormalChancePct);
        Assert.Equal(40.0, settings.DropWeightCurrency);
        Assert.Equal(4.0, settings.DropWeightGems);
        Assert.Equal(8.0, settings.DropWeightFlasks);
        Assert.Equal(48.0, settings.DropWeightEquipment);
        Assert.Equal(10.0, settings.DropNaturalQualityChancePct);
        Assert.Equal(1.0, settings.DropSocketLinkScale);
        Assert.Equal(1.0, settings.DropUniqueChanceMultiplier);
        Assert.True(settings.DropPenaltyEnabled);
        Assert.Equal(2, settings.DropPenaltyLevelOffset);
        Assert.Equal(2.5, settings.DropPenaltyPerLevelPct);
        Assert.Equal(68, settings.DropPenaltyCharacterLevelCap);
        Assert.Equal(0.5, settings.DropCampaignCardChancePct);
        Assert.True(settings.DropFirstKillBossBonus);
        Assert.Equal(1, settings.VendorRefreshLevels);
        Assert.True(settings.MonsterSpeciesModsEnabled);
        Assert.True(settings.MonsterEsEnabled);
        Assert.True(settings.MonsterArmourEnabled);
        Assert.Equal(500, settings.MonsterBaseCritChancePermyriad);
        Assert.Equal(1.0, settings.MonsterAilmentThresholdScaling);
        Assert.Equal(1.0, settings.CampaignChestDensityMultiplier);
        Assert.True(settings.CampaignDoorsEnabled);
        Assert.Equal(12.0, settings.CampaignPackMagicChancePct);
        Assert.Equal(6.0, settings.CampaignPackRareChancePct);

        // Validation rules for retail settings.
        Assert.True(SystemSettings.Validate("drop_normal_chance_pct", 35.0, originalValues, out _));
        Assert.False(SystemSettings.Validate("drop_normal_chance_pct", 120.0, originalValues, out _));
        Assert.True(SystemSettings.Validate("drop_penalty_enabled", 0, originalValues, out _));
        Assert.False(SystemSettings.Validate("drop_penalty_enabled", 2, originalValues, out _));
        Assert.True(SystemSettings.Validate("vendor_refresh_levels", 3, originalValues, out _));
        Assert.False(SystemSettings.Validate("vendor_refresh_levels", 0, originalValues, out _));
        Assert.True(SystemSettings.Validate("monster_base_crit_chance_permyriad", 750, originalValues, out _));
        Assert.False(SystemSettings.Validate("monster_base_crit_chance_permyriad", 10_001, originalValues, out _));
        Assert.True(SystemSettings.Validate("monster_ailment_threshold_scaling", 0.5, originalValues, out _));
        Assert.False(SystemSettings.Validate("monster_ailment_threshold_scaling", -1.0, originalValues, out _));

        // 2. ItemDrops: campaign divination card area filtering vs Atlas maps, overlevel penalty, starter/boss drops, gold scaling.
        var catalog = Program.Items;
        var rolls = Program.Rolls;
        var drops = Game.Drops;
        var rng = new Random(32901);

        // Campaign cards filter strictly by WorldAreas.Id (min_map_tier = 0) without cross-Part name collision.
        for (int i = 0; i < 12; i++)
        {
            var aqCard = drops.Card(rng, level: 33, areaId: "1_4_1", mapTier: 0);
            Assert.NotNull(aqCard);
            Assert.Contains(aqCard.Type.Name, new[] { "Humility", "The Carrion Crow" });
        }
        Assert.Null(drops.Card(rng, level: 2, areaId: "1_1_2", mapTier: 0));
        var act6CoastCard = drops.Card(rng, level: 45, areaId: "2_6_2", mapTier: 0);
        Assert.NotNull(act6CoastCard);
        Assert.Contains(act6CoastCard.Type.Name, new[] { "Lantador's Lost Love", "The Metalsmith's Gift" });
        var atlasCard = drops.Card(rng, level: 68, areaId: "MapWorldsBeach", mapTier: 1);
        Assert.NotNull(atlasCard);
        Assert.Equal("DivinationCard", atlasCard.Type.Class);

        // Overlevel currency/card penalty respects offset (2), rate (2.5%/lvl), and character cap (68).
        Assert.Equal(1.0, ItemDrops.DropPenaltyMultiplier(12, 10), precision: 6);
        Assert.Equal(0.75, ItemDrops.DropPenaltyMultiplier(22, 10), precision: 6);
        Assert.Equal(1.0, ItemDrops.DropPenaltyMultiplier(95, 66), precision: 6);
        Assert.Equal(0.85, ItemDrops.DropPenaltyMultiplier(95, 60), precision: 6);

        // Hillock starter drop (Witch = classRow 1): life/mana flasks, 2 Wisdoms, linked magic Wand, linked armour.
        var hillock = drops.RollHillockStarterDrop(classRow: 1, new Random(77));
        Assert.Equal(5, hillock.Count);
        Assert.Contains(hillock, d => d.Type.Id == "Metadata/Items/Flasks/FlaskLife1");
        Assert.Contains(hillock, d => d.Type.Id == "Metadata/Items/Flasks/FlaskMana1");
        Assert.Contains(hillock, d => d.Type.Id == "Metadata/Items/Currency/CurrencyIdentification" && d.Item.StackSize == 2);
        var starterWand = Assert.Single(hillock, d => d.Type.Class == "Wand");
        Assert.Equal(ItemRarity.Magic, starterWand.Item.Rarity);
        Assert.True(starterWand.Item.Links is { Count: > 0 } wandLinks && wandLinks.Max() >= 2);
        Assert.Contains(hillock, d => d.Type.Class is "Body Armour" or "Helmet" or "Gloves" or "Boots"
            && d.Item.Links is { Count: > 0 } armourLinks && armourLinks.Max() >= 2);

        // Boss first-kill bonus: Act boss = 3 equipment + 2 currency; Quest boss = 2 equipment + 1 currency.
        var actBossBonus = drops.RollBossFirstKillBonus(30, new Random(123), actBoss: true);
        Assert.Equal(5, actBossBonus.Count);
        Assert.Equal(3, actBossBonus.Count(d => ItemDrops.EquipmentClasses.Contains(d.Type.Class)));
        Assert.Equal(2, actBossBonus.Count(d => d.Type.Class == "StackableCurrency"));
        var questBossBonus = drops.RollBossFirstKillBonus(20, new Random(123), actBoss: false);
        Assert.Equal(3, questBossBonus.Count);
        Assert.Equal(2, questBossBonus.Count(d => ItemDrops.EquipmentClasses.Contains(d.Type.Class)));
        Assert.Equal(1, questBossBonus.Count(d => d.Type.Class == "StackableCurrency"));

        // Gold scales with level, rarity, IIR, and IIQ.
        var gLow = drops.Gold(1, MonsterRarity.Unique, new Random(99), multiplier: 1, increasedRarity: 0, quantityFactor: 1.0);
        var gHigh = drops.Gold(68, MonsterRarity.Unique, new Random(99), multiplier: 1, increasedRarity: 0, quantityFactor: 1.0);
        var gBoosted = drops.Gold(68, MonsterRarity.Unique, new Random(99), multiplier: 1, increasedRarity: 100, quantityFactor: 1.5);
        Assert.NotNull(gLow);
        Assert.NotNull(gHigh);
        Assert.NotNull(gBoosted);
        Assert.True(gHigh.Item.StackSize > gLow.Item.StackSize);
        Assert.True(gBoosted.Item.StackSize > gHigh.Item.StackSize);

        // 3. VendorStock: fixed ItemClasses and retail 1-level vs retuned 3-level refresh.
        var skills = Program.Skills;
        string character = "ParityVendor" + Guid.NewGuid().ToString("N");
        uint nextId = 0;
        var notes = new List<string>();
        var tarkleigh10 = VendorStock.Open(character, "Standard", "Metadata/NPC/Act1/Tarkleigh", 10, catalog, rolls, skills,
            [], () => ++nextId, LeagueRules.Standard, false, notes, random: new Random(505));
        Assert.Empty(notes);
        var weaponClasses = new HashSet<string>
        {
            "One Hand Sword", "Two Hand Sword", "Thrusting One Hand Sword", "One Hand Axe", "Two Hand Axe",
            "One Hand Mace", "Two Hand Mace", "Dagger", "Rune Dagger", "Claw", "Bow", "Staff", "Warstaff",
            "Sceptre", "Shield", "Body Armour", "Helmet", "Gloves", "Boots", "Quiver",
        };
        var tarkleighFinite = tarkleigh10.Tabs.SelectMany(t => t.Items).Where(i => tarkleigh10.Finite.ContainsKey(i.Id)).ToArray();
        Assert.Equal(60, tarkleighFinite.Length);
        Assert.All(tarkleighFinite, i => Assert.Contains(catalog.ByHash(i.Item.TypeHash)!.Class, weaponClasses));
        var nessa10 = VendorStock.Open(character, "Standard", "Metadata/NPC/Act1/Nessa", 10, catalog, rolls, skills,
            [], () => ++nextId, LeagueRules.Standard, false, notes, random: new Random(606));
        Assert.Empty(notes);
        var nessaSupplies = nessa10.Tabs.SelectMany(t => t.Items)
            .Where(i => !nessa10.Finite.ContainsKey(i.Id))
            .Select(i => catalog.ByHash(i.Item.TypeHash)!.Name).ToHashSet();
        Assert.Contains("Portal Scroll", nessaSupplies);
        Assert.Contains("Orb of Transmutation", nessaSupplies);
        Assert.DoesNotContain("Orb of Alchemy", nessaSupplies);

        // Retail default vendor_refresh_levels = 1 refreshes 100% on level-up.
        var tarkleigh11 = VendorStock.Open(character, "Standard", "Metadata/NPC/Act1/Tarkleigh", 11, catalog, rolls, skills,
            [], () => ++nextId, LeagueRules.Standard, false, notes, random: new Random(506));
        Assert.Empty(tarkleigh10.Finite.Values.Intersect(tarkleigh11.Finite.Values));

        // Retuning settings live changes drop penalty and vendor refresh cadence cleanly.
        try
        {
            var retuned = new Dictionary<string, double>(originalValues, StringComparer.OrdinalIgnoreCase)
            {
                ["drop_penalty_enabled"] = 0,
                ["vendor_refresh_levels"] = 3,
            };
            settings.Replace(retuned);
            Assert.Equal(1.0, ItemDrops.DropPenaltyMultiplier(50, 10), precision: 6);
            string retunedChar = "ParityVendorRetuned" + Guid.NewGuid().ToString("N");
            var open10 = VendorStock.Open(retunedChar, "Standard", "Metadata/NPC/Act1/Tarkleigh", 10, catalog, rolls, skills,
                [], () => ++nextId, LeagueRules.Standard, false, notes, random: new Random(707));
            var open11 = VendorStock.Open(retunedChar, "Standard", "Metadata/NPC/Act1/Tarkleigh", 11, catalog, rolls, skills,
                [], () => ++nextId, LeagueRules.Standard, false, notes, random: new Random(708));
            Assert.InRange(open10.Finite.Values.Intersect(open11.Finite.Values).Count(), 39, 40);
        }
        finally
        {
            settings.Replace(originalValues);
        }

        // 4. GemVendorStock: Lani never sells gems; Siosa enforces Act 1-3 quest unlocks across all classes.
        var npcs = NpcCatalog.Load();
        var rewards = QuestRewardCatalog.Load();
        var gemStock = GemVendorStock.For(catalog, npcs, skills);
        var allTakenFlags = rewards.Offers.Where(o => o.TakenFlag is not null).Select(o => o.TakenFlag!).ToHashSet();
        allTakenFlags.Add(GemVendorStock.SiosaUnlock);
        allTakenFlags.Add(GemVendorStock.LillyUnlock);
        Assert.False(GemVendorStock.Sells("Metadata/NPC/Act5/Lani"));
        Assert.False(GemVendorStock.Sells("Metadata/NPC/Act10/Lani"));
        Assert.Empty(gemStock.Stock("Metadata/NPC/Act5/Lani", 1, allTakenFlags));
        Assert.Empty(gemStock.Stock("Metadata/NPC/Act3/Siosa", 1, new HashSet<string>()));
        var siosaQuestOnly = gemStock.Stock("Metadata/NPC/Act3/Siosa", 1, new HashSet<string> { GemVendorStock.SiosaUnlock });
        var siosaAllAct1To3 = gemStock.Stock("Metadata/NPC/Act3/Siosa", 1, allTakenFlags);
        var clarissaWitch = gemStock.Stock("Metadata/NPC/Act3/ClarissaTown", 1, allTakenFlags);
        Assert.NotEmpty(siosaQuestOnly);
        Assert.True(siosaAllAct1To3.Count > siosaQuestOnly.Count);
        Assert.True(siosaAllAct1To3.Count > clarissaWitch.Count);
    }

    [Fact]
    public void Campaign_monster_stats_packs_and_quest_audio_load_cleanly()
    {
        // 1. MonsterCatalog: ES 40%, Evasion interpolation, Accuracy, Base Crit 500, AilmentThreshold, AreaTier, MinimumAttackDistance.
        var monsters = Game.Monsters;
        var alira = monsters.Find("Metadata/Monsters/BanditLeaderInt/BanditLeaderAlira");
        var kraityn = monsters.Find("Metadata/Monsters/BanditLeaderDexInt/BanditLeaderKraityn");
        var heavyBandit = monsters.Find("Metadata/Monsters/Bandits/BanditBossHeavyStrike_");
        var wolfBoss = monsters.Find("Metadata/Monsters/Lion/LionMapWolfColdBoss");
        Assert.NotNull(alira);
        Assert.NotNull(kraityn);
        Assert.NotNull(heavyBandit);
        Assert.NotNull(wolfBoss);

        // Energy Shield = 40% (GameConstants.EnergyShieldRatioOfLife) * MonsterTypes.EnergyShield / 100.
        Assert.Equal(400, monsters.BaseEnergyShield(alira, 20, 1000));

        // Evasion interpolation (0 -> BaseEvasion, 20 -> 40% of delta, 50 -> BaseEvasiveEvasion).
        int evBase = monsters.BaseEvasion(30)!.Value;
        int evHigh = monsters.BaseEvasiveEvasion(30)!.Value;
        Assert.Equal(evHigh, monsters.BaseEvasion(kraityn, 30));
        Assert.Equal(evBase + (int)((long)(evHigh - evBase) * 20 / 50), monsters.BaseEvasion(heavyBandit, 30));

        // Accuracy scales by MonsterTypes.Accuracy / 100 (Kraityn = 120%).
        int accBase = monsters.BaseAccuracy(30)!.Value;
        Assert.Equal((int)((long)accBase * 120 / 100), monsters.BaseAccuracy(kraityn, 30));

        // Base crit 500 permyriad (5.00%) and AilmentThreshold scaling + MinimumAttackDistance.
        Assert.Equal(500, MonsterModEffects.DefaultBaseCriticalChance);
        Assert.Equal(6, wolfBoss.MinimumAttackDistance);
        long wolfThreshold = monsters.AilmentThreshold(wolfBoss, 70, 10_000);
        Assert.InRange(wolfThreshold, 1, 9_999);

        // AreaTier: Part 1 (Acts 1-5, Normal Lab), Part 2 (Acts 6-10, Cruel Lab), Endgame (Maps, Merciless/Eternal Lab, Delve, Heist).
        Assert.Equal(1, monsters.AreaTier("1_1_2", 0, isMap: false));
        Assert.Equal(1, monsters.AreaTier("1_5_8", 0, isMap: false));
        Assert.Equal(1, monsters.AreaTier("1_Labyrinth_1_1", 0, isMap: false));
        Assert.Equal(2, monsters.AreaTier("2_6_2", 0, isMap: false));
        Assert.Equal(2, monsters.AreaTier("2_10_6", 0, isMap: false));
        Assert.Equal(2, monsters.AreaTier("2_Labyrinth_1_1", 0, isMap: false));
        Assert.Equal(3, monsters.AreaTier("MapWorldsBeach", 0, isMap: false));
        Assert.Equal(3, monsters.AreaTier("3_Labyrinth_1_1", 0, isMap: false));
        Assert.Equal(3, monsters.AreaTier("Delve_Main", 0, isMap: false));
        Assert.Equal(3, monsters.AreaTier("HeistBunker", 0, isMap: false));
        Assert.Equal(3, monsters.AreaTier("1_1_2", 0, isMap: true));

        // 2. MonsterModCatalog: TemporalAura non-self-slow and SpeciesApplied idempotence.
        var monsterMods = new MonsterModCatalog(Program.Items.Mods);
        var temporalMod = monsterMods.Mods.ById("MonsterModTemporalAura");
        Assert.NotNull(temporalMod);
        var temporalSet = monsterMods.Create([temporalMod.Roll(ModSection.Explicit, new Random(1))], [], MonsterRarity.Rare, 30);
        Assert.True(temporalSet.Effects[Stat.CannotBeDamagedByThingsOutsideRadius] > 0);
        Assert.Equal(0, temporalSet.Effects[Stat.ActionSpeedMinusPct]);
        Assert.Equal(0, temporalSet.Effects[Stat.DebuffTimePassedPlusPct]);
        Assert.Equal(0, temporalSet.Effects[Stat.BaseCooldownSpeedPlusPct]);
        Assert.False(temporalSet.SpeciesApplied);

        var speciesRows = MonsterCatalog.SpeciesModsForArea(alira, tier: 1);
        var speciesEffects = monsterMods.SpeciesEffects(speciesRows, new Random(2));
        var applied = temporalSet with { Effects = temporalSet.Effects.Plus(speciesEffects), SpeciesApplied = true };
        Assert.True(applied.SpeciesApplied);
        var idempotent = applied.SpeciesApplied ? applied : applied with { Effects = applied.Effects.Plus(speciesEffects), SpeciesApplied = true };
        Assert.Equal(applied.Effects.Ids, idempotent.Effects.Ids);

        // 3. MonsterPacks: Flag=0 fixed entries, Grounds biome filter, and AreaPacks.Act.
        var packs = Game.Packs;
        var statues = packs.Find("1_1_11_2-merveilstatues");
        Assert.NotNull(statues);
        Assert.Equal(2, statues.FixedEntries.Count);
        var drowned = packs.Find("Act1-ZombieDrowned");
        Assert.NotNull(drowned);
        Assert.Contains("BeachOceanShallowWet", drowned.RequiredGrounds);
        Assert.Equal(1, packs.For("1_1_2")!.Act);
        Assert.Equal(5, packs.For("1_5_8")!.Act);
        Assert.Equal(10, packs.For("2_10_6")!.Act);

        // 4. NpcCatalog: BanditRespec excluded from HasAvailableReward / AvailableRewards, and Script2 timer/counter flags.
        var npcs = NpcCatalog.Load();
        var eramir = npcs.Find("Metadata/NPC/Act2/Eramir");
        Assert.NotNull(eramir);
        var noFlags = new HashSet<string>();
        Assert.False(npcs.HasAvailableReward(eramir, 33, noFlags));
        Assert.DoesNotContain(33, npcs.AvailableRewards(noFlags));
        var fairgraves = npcs.Find("Metadata/NPC/Act3/Fairgraves");
        Assert.NotNull(fairgraves);
        var fairgravesTalks = npcs.TalksOf(fairgraves);
        var timerTalk = Assert.Single(fairgravesTalks, t => t.Row == 502);
        var counterTalk = Assert.Single(fairgravesTalks, t => t.Row == 503);
        Assert.True(timerTalk.StartsTimer);
        Assert.False(timerTalk.IncrementsCounter);
        Assert.True(counterTalk.IncrementsCounter);
        Assert.False(counterTalk.StartsTimer);

        // 5. CharacterAudioCatalog: FirstEntry_NPCTextAudioKey lookup.
        var audio = CharacterAudioCatalog.Default;
        var crematoriumEntry = audio.FirstEntryFor("1_3_3_1");
        var ruinedSquareEntry = audio.FirstEntryFor("1_5_3b");
        Assert.NotNull(crematoriumEntry);
        Assert.Equal((ushort)300, crematoriumEntry.TextAudioRow);
        Assert.NotNull(ruinedSquareEntry);
        Assert.Equal((ushort)1435, ruinedSquareEntry.TextAudioRow);
    }
}
