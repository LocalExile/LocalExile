using GameServer.Core.Protocol.Instance;
using GameServer.Modules.Atlas;

namespace GameServer.Gateway.Tests;

public sealed class Act11FoundationTests
{
    [Fact]
    public async Task Atlas_progression_books_voidstones_0x1a3_and_no_auto_bypass()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        var data = Program.WorldOf(player).CharacterData;

        // 1. Restore does NOT auto-grant UnlockLeagueFlags / UnlockFlags (no unconditional endgame bypass).
        Assert.Equal(0, player.Character.AtlasPassivePoints);
        var restored = AtlasFeature.Restore(player).ToList();
        Assert.DoesNotContain(225, player.Character.LeagueQuestFlags);
        Assert.DoesNotContain(351, player.Character.LeagueQuestFlags);
        Assert.DoesNotContain(352, player.Character.LeagueQuestFlags);
        Assert.DoesNotContain(1019, player.Character.LeagueQuestFlags);
        Assert.False(Game.ClientHasQuestFlag(player, "HaveMavenMapDeviceAlteration"));

        // 2. S→C 0x01a3 bytes 0–1 carry Tangle & Cleansing Fire counts (0..28), NOT socketed Voidstone count.
        byte fullMask = AtlasProgress.VoidstoneMask(new HashSet<int> { 0, 1, 2, 3 });
        Assert.Equal((byte)0x1f, fullMask);
        byte[] packet = AtlasProgress.FullMessage(new HashSet<string>(), new HashSet<string>(), fullMask, tangleCount: 14, cleansingCount: 28);
        Assert.Equal(new byte[] { 0x01, 0xa3, 14, 28, 0x1f, 0x03, 0 }, packet[..7]);
        Assert.True(AtlasProgress.IsRegionElevated(2, new HashSet<int> { 0 })); // Maven slot 0 -> region 2
        Assert.False(AtlasProgress.IsRegionElevated(1, new HashSet<int> { 0 }));
        Assert.True(AtlasProgress.IsRegionElevated(1, new HashSet<int> { 0, 1, 2, 3 }));

        // 3. QuestItems.dat rows 0..25 loaded into ItemCatalog (excluding Deepwater), and all 9 Atlas Books of Skill work.
        var beacon = Program.Items.Find("Metadata/Items/QuestItems/Act11/MavenMapDeviceAlteration")!;
        Assert.NotNull(beacon.Quest);
        Assert.Equal("HaveMavenMapDeviceAlteration", beacon.Quest.Grant);
        Assert.Equal(225, beacon.Quest.LeagueFlag);

        // Deepwater items must remain owned by DeepwaterProgression (not merged into BaseType.Quest).
        var deepwaterDucat = Program.Items.Find("Metadata/Items/QuestItems/Deepwater/QuestDucat1");
        if (deepwaterDucat is not null) Assert.Null(deepwaterDucat.Quest);

        // Auto-attach Map Device alteration quest item when opening the Map Device.
        var items = Game.ItemsOf(player);
        var placedBeacon = items.Put(PlayerInventories.MainInventory,
            new InventoryItem(items.NewItemId(), 0, 0, ItemCatalog.Create(beacon)));
        await AtlasFeature.AttachCarriedMapDeviceAlterations(player);
        Assert.Contains(225, player.Character.LeagueQuestFlags);
        Assert.DoesNotContain(items.Items(PlayerInventories.MainInventory), i => i.Id == placedBeacon.Id);

        // All 9 Atlas Books of Skill: 6 pinnacle (+1 passive, +2 respec) + 3 memory (+2 passive, +2 respec) = 12 passives, 18 respecs.
        string[] expectedBooks =
        [
            "Metadata/Items/QuestItems/SkillBooks/AtlasSkillBookElder",
            "Metadata/Items/QuestItems/SkillBooks/AtlasSkillBookMaven",
            "Metadata/Items/QuestItems/SkillBooks/AtlasSkillBookTangle",
            "Metadata/Items/QuestItems/SkillBooks/AtlasSkillBookCleansingFire",
            "Metadata/Items/QuestItems/SkillBooks/AtlasSkillBookInfiniteHunger",
            "Metadata/Items/QuestItems/SkillBooks/AtlasSkillBookBlackStar",
            "Metadata/Items/QuestItems/SkillBooks/AtlasSkillBookFirstMemoryBoss",
            "Metadata/Items/QuestItems/SkillBooks/AtlasSkillBookSecondMemoryBoss",
            "Metadata/Items/QuestItems/SkillBooks/AtlasSkillBookThirdMemoryBoss",
        ];
        Assert.Equal(9, AtlasFeature.AtlasSkillBooks.Count);
        foreach (string bookId in expectedBooks)
        {
            var type = Program.Items.ById(bookId)!;
            Assert.NotNull(type.Quest);
            Assert.NotNull(type.Quest.LeagueFlag);
            if (type.Quest.Grant is { } grant) player.Character.QuestFlags.Add(grant);
            var placed = items.Put(PlayerInventories.MainInventory,
                new InventoryItem(items.NewItemId(), 0, 0, ItemCatalog.Create(type)));
            Assert.True(await AtlasFeature.UseAtlasSkillBook(player, PlayerInventories.MainInventory, placed, type));
            Assert.Contains(type.Quest.LeagueFlag.Value, player.Character.LeagueQuestFlags);
            if (type.Quest.Grant is { } g) Assert.DoesNotContain(g, player.Character.QuestFlags);
            // Duplicate consumption is refused once the league flag is earned.
            var dup = items.Put(PlayerInventories.MainInventory,
                new InventoryItem(items.NewItemId(), 0, 0, ItemCatalog.Create(type)));
            Assert.False(await AtlasFeature.UseAtlasSkillBook(player, PlayerInventories.MainInventory, dup, type));
            items.Remove(PlayerInventories.MainInventory, dup.Id);
        }
        Assert.Equal(12, player.Character.AtlasPassivePoints);
        Assert.Equal(18, Game.AtlasRespecPoints(player));

        // 4. Full 3.29 retail Atlas Passive Point budget = 138 (100 normal + 10 unique cap + 5 nightmare + 12 books + 11 Maven).
        AtlasProgress.CompleteBonus(data, AtlasProgress.Maps);
        int[] mavenFlags = [974, 975, 976, 977, 978, 1162, 979, 980, 981, 982, 983];
        player.Character.LeagueQuestFlags.UnionWith(mavenFlags);
        Assert.Equal(138, AtlasModule.ExpectedAtlasPassivePoints(player, data));
        AtlasModule.ApplyBonusPoints(player, data);
        Assert.Equal(138, player.Character.AtlasPassivePoints);

        // Nightmare T17 bonus objective passes without requiring corruption.
        var sanctuary = AtlasProgress.Find("MapWorldsSanctuary")!;
        Assert.True(sanctuary.IsNightmareMap);
        var sanctuaryRun = new MapDevice.Run("Tester", new MapDevice.MapArea(sanctuary.Hash16, sanctuary.Id, sanctuary.Name, 16) { Level = 84 },
            new MapMods(new Dictionary<uint, int>()) { MapRarity = ItemRarity.Rare, Corrupted = false });
        Assert.True(AtlasProgress.MeetsBonusObjective(sanctuaryRun, sanctuary));

        // 5. 3.29 Voidstone drop scaling: 4 Voidstones guarantee +1 tier, convert <T16 to Gold, and can roll Nightmare Map on T16.
        var drops = Game.DropsFor(player);
        var goldFromLowTier = drops.Map(79, MonsterRarity.Rare, 12, new FixedRoll(0.0, 0.5, 0.99), voidstones: 4);
        Assert.NotNull(goldFromLowTier);
        Assert.Equal(ItemDrops.GoldClass, goldFromLowTier.Type.Class);

        var nightmareFromT16 = drops.Map(83, MonsterRarity.Rare, 16, new FixedRoll(0.0, 0.001, 0.5), voidstones: 4);
        Assert.NotNull(nightmareFromT16);
        Assert.Equal("Metadata/Items/Maps/MapKeyNightmareBoss", nightmareFromT16.Type.Id);
    }

    [Fact]
    public async Task Act11_a11q1_scripts_npcs_and_merciless_eternal_labyrinth()
    {
        // 1. Karui Shores HelenaIntro state=1 (visible), MapDeviceActive not in EveryCharacter, EagonHideout gated on LQF 1492.
        Assert.DoesNotContain("MapDeviceActive", Quests.EveryCharacter);
        var karuiPlacements = TownNpcs.Of(0xb4e7)!.Placements(1);
        var helenaIntro = karuiPlacements.First(p => p.Id == "Metadata/NPC/Epilogue/HelenaIntro");
        Assert.Equal((byte)1, helenaIntro.TransitionState);
        var eagonHideout = NpcCatalog.HideoutNpcs.First(p => p.Id == "Metadata/NPC/Epilogue/EagonHideout");
        Assert.Equal(1492, eagonHideout.RequiredLeagueFlag);

        // 2. NPCTalk script primitive parsing across Envoy, Eagon, and Kirac rows.
        var npcs = Game.Npcs;
        var envoy = npcs.Find("Metadata/NPC/Epilogue/Envoy")!;
        var envoyWarning = npcs.TalksOf(envoy).First(t => t.Row == 2554);
        Assert.True(envoyWarning.SetsMavenRegionFlag);

        var envoyTangle = npcs.Find("Metadata/NPC/Epilogue/EnvoyTangle")!;
        var tangleDrop = npcs.TalksOf(envoyTangle).First(t => t.Row == 2802);
        Assert.Contains("Metadata/Items/QuestItems/Act11/TangleMapDeviceAlteration", tangleDrop.DropQuestItems);
        Assert.Contains("MavenTangleLure", tangleDrop.MavenDialogs);

        var eagonWild = npcs.Find("Metadata/NPC/Epilogue/EagonWild")!;
        var eagonCoords = npcs.TalksOf(eagonWild).First(t => t.Row == 3672);
        Assert.Contains(eagonCoords.ScriptTextAudios, a => a.AudioId == "EagonWildDoNotDally" && a.NpcId == "Metadata/NPC/Epilogue/EagonWild");
        Assert.Contains("portal_out_01", eagonCoords.PlayAnimations);

        var kirac = npcs.Find("Metadata/NPC/Epilogue/Kirac")!;
        var kiracRespec = npcs.TalksOf(kirac).First(t => t.Row == 2324);
        Assert.Equal(("AtlasPassiveTreeScreen", 1u), kiracRespec.OpenScreenRequest);
        var kiracInvite = npcs.TalksOf(kirac).First(t => t.Script.Contains("open_maven_invitation", StringComparison.Ordinal));
        Assert.Contains(kiracInvite.RulesetEvents, e => e.StartsWith("open_maven_invitation ", StringComparison.Ordinal));

        var eagon = npcs.Find("Metadata/NPC/Epilogue/Eagon")!;
        var memoryLineTalk = npcs.TalksOf(eagon).First(t => t.Row == 3677);
        Assert.Contains("open_memory_line", memoryLineTalk.RulesetEvents);

        // 3. GameEvents.RulesetEvent dispatches cleanly.
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        string? seenEvent = null;
        using var sub = GameEvents.RulesetEvent.Add("Game", (_, _, _, _, ev) =>
        {
            seenEvent = ev;
            return Task.FromResult(true);
        });
        Assert.True(await GameEvents.DispatchRulesetEvent(player, 1, new SpawnedNpc(eagon, 10, 10), memoryLineTalk, "open_memory_line"));
        Assert.Equal("open_memory_line", seenEvent);

        // 4. Eternal Labyrinth (tier 4): requires level 68+, Merciless complete, and an Offering to the Goddess (no map trials required),
        //    and ConsumeLabyrinthOffering atomically removes one Offering from MapDevice / MainInventory.
        var plaza = new AreaLayout(0, LabyrinthDay.Plaza, 100, 100, 10, 10, null, []);
        var labPlayer = new Player(plaza, 0, 0)
        {
            Character = { Name = player.Character.Name },
            Pools = CharacterStats.For(Program.Skills.ClassFor(0), 67),
        };
        labPlayer.Character.QuestFlags.Add("LabyrinthComplete3");
        Assert.Equal("requires level 68", Game.LabyrinthAdmissionFailure(labPlayer, 4));

        labPlayer.Pools = CharacterStats.For(Program.Skills.ClassFor(0), 68);
        Assert.Equal("requires an Offering to the Goddess", Game.LabyrinthAdmissionFailure(labPlayer, 4));

        var offeringType = Program.Items.Find("Metadata/Items/MapFragments/CurrencyOfferingToTheGoddess")!;
        var items = Game.ItemsOf(labPlayer);
        var offering = items.Put(PlayerInventories.MapDevice,
            new InventoryItem(items.NewItemId(), 0, 0, ItemCatalog.Create(offeringType)));
        Assert.Null(Game.LabyrinthAdmissionFailure(labPlayer, 4));

        var spent = Game.ConsumeLabyrinthOffering(labPlayer);
        Assert.NotNull(spent);
        Assert.Equal(PlayerInventories.MapDevice, spent.Value.Row);
        Assert.Equal(offering.Id, spent.Value.ItemId);
        Assert.Equal("requires an Offering to the Goddess", Game.LabyrinthAdmissionFailure(labPlayer, 4));
    }

    sealed class FixedRoll(params double[] rolls) : Random(11)
    {
        int _index;
        public override double NextDouble() => rolls[Math.Min(_index++, rolls.Length - 1)];
    }
}
