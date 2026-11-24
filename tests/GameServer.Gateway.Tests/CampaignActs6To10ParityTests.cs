using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class CampaignActs6To10ParityTests
{
    [Fact]
    public void Acts6To10LootVendorAndPantheonCatalogParity()
    {
        var store = GameData.Store!;
        var drops = DropDataProvider.Default.Current;
        var items = Program.Items;
        var uniques = Game.Uniques;
        var settings = SystemSettings.Default;

        // 1. Unique drop pool hygiene: 0 drop_boss_items uniques or Replica * uniques with encounter_only = 0.
        var bossUniqueNames = drops.BossItems.Values
            .Where(b => string.Equals(b.ItemKind, "unique", StringComparison.OrdinalIgnoreCase))
            .Select(b => b.ItemId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.NotEmpty(bossUniqueNames);

        var leakedBossUniques = uniques.All
            .Where(u => !u.EncounterOnly && bossUniqueNames.Contains(u.Name))
            .Select(u => u.Name)
            .ToArray();
        Assert.Empty(leakedBossUniques);

        var leakedReplicas = uniques.All
            .Where(u => !u.EncounterOnly && u.Name.StartsWith("Replica ", StringComparison.OrdinalIgnoreCase))
            .Select(u => u.Name)
            .ToArray();
        Assert.Empty(leakedReplicas);

        // 2. All 8 restored canonical ASCII core uniques resolve with positive weights.
        var weightsByName = store.LoadUniqueWeights().ToDictionary(w => w.Name, StringComparer.Ordinal);
        string[] restoredCoreUniques =
        [
            "Emperor's Vigilance",
            "Rigwald's Charge",
            "Tasalio's Sign",
            "Ngamahu's Sign",
            "Veruso's Battering Rams",
            "Mercenary's Lot",
            "The Broken Elegy",
            "Witchhunter's Judgment",
        ];
        foreach (string name in restoredCoreUniques)
        {
            Assert.NotNull(uniques.Find(name));
            Assert.True(weightsByName.TryGetValue(name, out var row), $"Missing unique_weights row for {name}");
            Assert.True(row.Weight > 0, $"Non-positive weight for {name}");
            Assert.True(row.RuthlessWeight > 0, $"Non-positive ruthless weight for {name}");
        }

        // 3. Area-restricted cards match the campaign zone and meet the level and weight gates.
        var act6To10Zones = ClientTables.Table("WorldAreas")
            .Where(r => (r.Text("Id") ?? "").StartsWith("2_", StringComparison.Ordinal)
                && r.IntOrNull("Act") is >= 6 and <= 10
                && !r.Bool("IsTown"))
            .Select(r => (Id: r.Text("Id")!, AreaLevel: r.IntOrNull("AreaLevel") ?? 0))
            .ToList();
        Assert.Equal(69, act6To10Zones.Count);

        var cardsByArea = drops.CardAreas.Values
            .Where(c => c.MinMapTier == 0)
            .GroupBy(c => c.AreaId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        Assert.Contains(cardsByArea["2_6_5"], c => c.CardId == "The Metalsmith's Gift");
        Assert.DoesNotContain(cardsByArea["2_6_5"], c => c.CardId == "The Battle Born");
        Assert.DoesNotContain(cardsByArea["2_6_13"], c => c.CardId == "The Siren");
        foreach (var (areaId, areaLevel) in act6To10Zones)
        {
            if (!cardsByArea.TryGetValue(areaId, out var areaCards)) continue;
            foreach (var card in areaCards)
            {
                var baseType = items.Find(card.CardId);
                Assert.NotNull(baseType);
                Assert.Equal("DivinationCard", baseType.Class);
                Assert.True(baseType.DropLevel <= areaLevel,
                    $"Card {card.CardId} DropLevel {baseType.DropLevel} exceeds {areaId} AreaLevel {areaLevel}");
                Assert.True(drops.ItemWeights.TryGetValue(("card", baseType.Id), out var cardWeight)
                    && cardWeight.Enabled && cardWeight.Weight > 0,
                    $"Card {card.CardId} ({baseType.Id}) in {areaId} lacks positive enabled drop_item_weights");
            }
        }

        // 4. Retunable Part 2 campaign map & Divine Vessel drop settings loaded from world.db and validated.
        Assert.Equal(0.18, settings.DropCampaignMapChanceMultiplier, precision: 6);
        Assert.Equal(0.08, settings.DropDivineVesselChancePct, precision: 6);
        Assert.True(SystemSettings.Validate("drop_campaign_map_chance_multiplier", 0.25, settings.Values, out _));
        Assert.False(SystemSettings.Validate("drop_campaign_map_chance_multiplier", -0.1, settings.Values, out _));
        Assert.True(SystemSettings.Validate("drop_divine_vessel_chance_pct", 0.5, settings.Values, out _));
        Assert.False(SystemSettings.Validate("drop_divine_vessel_chance_pct", 101.0, settings.Values, out _));

        // 5. Fusing linear quality scaling & Jeweller's SocketCapacity guard.
        var bodyBase = Assert.IsType<ItemCatalog.BaseType>(items.Find("Metadata/Items/Armours/BodyArmours/BodyStr1"));
        ItemSocket[] sixSockets = Enumerable.Range(0, 6).Select(_ => new ItemSocket(SocketColour.Red)).ToArray();
        var unlinkedQ0 = ItemCatalog.Create(bodyBase, sockets: sixSockets, links: [1, 1, 1, 1, 1, 1]) with { ItemLevel = 68, Quality = 0 };
        var unlinkedQ20 = unlinkedQ0 with { Quality = 20 };

        int highLinksQ0 = 0, highLinksQ20 = 0;
        var rngQ0 = new Random(32906);
        var rngQ20 = new Random(32906);
        for (int i = 0; i < 4000; i++)
        {
            var r0 = Assert.IsType<Item>(CurrencyEffects.Fusing(unlinkedQ0, bodyBase, rngQ0));
            var r20 = Assert.IsType<Item>(CurrencyEffects.Fusing(unlinkedQ20, bodyBase, rngQ20));
            if (r0.Links!.Max() >= 5) highLinksQ0++;
            if (r20.Links!.Max() >= 5) highLinksQ20++;
        }
        Assert.True(highLinksQ20 > highLinksQ0,
            $"Expected 20% quality to increase 5L/6L rate (q0={highLinksQ0}, q20={highLinksQ20})");
        Assert.Null(CurrencyEffects.Fusing(unlinkedQ0 with { Links = [6] }, bodyBase, new Random(1)));

        int capacity = CurrencyEffects.SocketCapacity(unlinkedQ0, bodyBase);
        Assert.Equal(6, capacity);
        Assert.Null(CurrencyEffects.Jewellers(unlinkedQ0, bodyBase, new Random(1)));

        // 6. ShopGems excludes Royale gems across all colours and active/support tabs.
        foreach (int colour in new[] { 1, 2, 3 })
        {
            foreach (bool support in new[] { false, true })
            {
                var gems = items.ShopGems(colour, support);
                Assert.NotEmpty(gems);
                Assert.DoesNotContain(gems, g => g.Id.Contains("Royale", StringComparison.OrdinalIgnoreCase));
            }
        }

        // 7. All 20 PantheonSouls rows resolve cleanly in PantheonCatalog and ItemCatalog.
        var pantheon = PantheonCatalog.Default;
        Assert.Equal(20, pantheon.Souls.Count);
        foreach (var soul in pantheon.Souls)
        {
            Assert.False(string.IsNullOrWhiteSpace(soul.WorldAreaId));
            Assert.False(string.IsNullOrWhiteSpace(soul.VesselItemId));
            Assert.False(string.IsNullOrWhiteSpace(soul.UpgradeFlag));
            Assert.InRange(soul.PanelRow, 0, pantheon.Gods.Count - 1);
            Assert.Equal(soul, pantheon.SoulForArea(soul.WorldAreaId));
            Assert.Equal(soul, pantheon.SoulByVessel(soul.VesselItemId));
            Assert.Equal(soul, pantheon.SoulByUpgradeFlag(soul.UpgradeFlag));
            var vesselType = items.Find(soul.VesselItemId);
            Assert.NotNull(vesselType);
            Assert.True(vesselType.Supported);
        }
    }

    [Fact]
    public void Acts6To10BossQuestAndWorldCatalogParity()
    {
        // 1. Part 2 ServerLevels ordinary door types & HeistCache league chances across Acts 6-10.
        Assert.True(ServerLevels.IsOrdinaryDoorType("Metadata/Terrain/Act8/Area3/Objects/GoldenDoor"));
        Assert.True(ServerLevels.IsOrdinaryDoorType("Metadata/QuestObjects/BanditDoor"));
        Assert.True(ServerLevels.IsOrdinaryDoorType("Metadata/MiscellaneousObjects/Door"));

        foreach (string areaId in new[] { "2_6_2", "2_7_1", "2_8_1", "2_9_1", "2_10_1" })
        {
            var (chance, _) = LeagueChances.Current.ChanceOf(areaId, LeagueChances.Heist);
            Assert.True(chance > 0, $"Expected nonzero HeistCache chance in {areaId}");
        }
        Assert.Equal(0, LeagueChances.Current.ChanceOf("1_1_2", LeagueChances.Heist).Chance);

        // 2. BossMods heal-on-player-death marker mods (10% major / 5% minor).
        var monsters = Game.Monsters;
        var majorHealBoss = Assert.IsType<MonsterCatalog.Variety>(monsters.Find("Metadata/Monsters/Brute/BossBrute"));
        var minorHealBoss = Assert.IsType<MonsterCatalog.Variety>(monsters.Find(ShavronneBrutusEncounter.Shavronne));
        Assert.True(BossMods.Has(majorHealBoss, BossMods.HealOnPlayerDeath));
        Assert.Equal(0.10, BossMods.PlayerDeathHealFraction(majorHealBoss), precision: 6);
        Assert.True(BossMods.Has(minorHealBoss, BossMods.HealOnPlayerDeathMinor));
        Assert.Equal(0.05, BossMods.PlayerDeathHealFraction(minorHealBoss), precision: 6);

        // 3. All Act 6-10 dedicated boss varieties exist in client.db and are owned by the Act 6-10 facades.
        string[] act6To8Bosses =
        [
            TukohamaEncounter.Boss,
            ShavronneBrutusEncounter.Shavronne,
            ShavronneBrutusEncounter.Brutus,
            AbberathEncounter.Phase1,
            AbberathEncounter.Phase2,
            RyslathaEncounter.Boss,
            MaligaroSanctumEncounter.Maligaro,
            MaligaroSanctumEncounter.BlackDeath,
            MaligaroSanctumEncounter.Fidelitas,
            RalakeshEncounter.Greust,
            RalakeshEncounter.Oak,
            RalakeshEncounter.Alira,
            RalakeshEncounter.Kraityn,
            GruthkulEncounter.Boss,
            TolmanEncounter.Boss,
            GemlingLegionEncounter.Fireball,
            GemlingLegionEncounter.Earthquake,
            GemlingLegionEncounter.GlacialCascade,
            GemlingLegionEncounter.Captain,
            DawnDuskEncounters.Dawn,
            DawnDuskEncounters.Dusk,
        ];
        foreach (string id in act6To8Bosses)
        {
            var v = monsters.Find(id);
            Assert.NotNull(v);
            Assert.Equal(id, v.Id);
            Assert.True(Act6To8Encounters.IsBossControllerVariety(id), $"Not registered in Act6To8Encounters: {id}");
        }

        string[] act9To10Bosses =
        [
            ShakariEncounter.ShakariP1,
            ShakariEncounter.ShakariP2,
            GarukhanKiraEncounter.Garukhan,
            GeneralAdusBasiliskEncounters.GeneralAdus,
            GeneralAdusBasiliskEncounters.Basilisk,
            RottingCoreSoulsEncounter.DoedreSoul,
            RottingCoreSoulsEncounter.MaligaroSoul,
            RottingCoreSoulsEncounter.ShavronneSoul,
            PlaguewingBannonEncounter.Plaguewing,
            AvariusReassembledEncounter.AvariusWickerman,
            VilentaEncounter.Vilenta,
        ];
        foreach (string id in act9To10Bosses)
        {
            var v = monsters.Find(id);
            Assert.NotNull(v);
            Assert.Equal(id, v.Id);
            Assert.True(Act9To10Encounters.IsBossControllerVariety(id), $"Not registered in Act9To10Encounters: {id}");
        }

        // 4. Key Acts 6-10 + Epilogue quest varieties, flags, and NPCs verified in client.db.
        var kira = monsters.Find(GarukhanKiraEncounter.Kira);
        Assert.NotNull(kira);
        Assert.Equal(GarukhanKiraEncounter.Kira, kira.Id);

        var questFlags = ClientTables.Table("QuestFlags")
            .Select(r => r.Text("Id") ?? "")
            .ToHashSet(StringComparer.Ordinal);
        string[] requiredFlags =
        [
            "a6q2-KilledBoss",
            "a7q1-KilledBoss",
            "a8q6-DeliveredItem",
            "a8q6-KilledBoss",
            "a9q2-KilledKira",
            "a9q3-BeginEncounter",
            "a9q3-FinishEncounter",
            "a10q1-RescuedBannon",
            "EndGameAvailable",
            "MapDeviceActive",
            "InMapArea",
        ];
        foreach (string flag in requiredFlags)
            Assert.Contains(flag, questFlags);

        var worldAreas = ClientTables.Table("WorldAreas")
            .Select(r => r.Text("Id") ?? "")
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("2_11_endgame_town", worldAreas);

        var npcs = NpcCatalog.Load();
        Assert.NotNull(npcs.Find("Metadata/NPC/Epilogue/Kirac"));
        Assert.NotNull(npcs.Find("Metadata/NPC/Epilogue/HelenaIntro"));
        Assert.NotNull(npcs.Find("Metadata/NPC/Act10/BannonRooftop"));
        Assert.NotNull(npcs.Find("Metadata/NPC/Act10/BannonTown"));
        Assert.NotNull(npcs.Find("Metadata/NPC/Act8/ClarissaQuay"));
    }
}
