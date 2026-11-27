using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modules.Atlas;

namespace GameServer.Gateway.Tests;

public sealed class AtlasAndMapEntryTests
{
    [Fact]
    public void AreaInstance_and_Atlas_protocol_packets_encode_exact_client_wire_layouts()
    {
        byte[] plain = new AreaInstance(1, 0x235c, 7, 2).Encode();
        byte[] hideout = new AreaInstance(1, 0x235c, 7, 2, GuildHideout: true).Encode();
        Assert.Equal(plain.Length, hideout.Length);
        // Header before flags: 2 id + 4 instance + 2 area + 2 empty WStr + 4 seed + 4 bytes = 18; flags at [18..20].
        Assert.Equal(0x80, hideout[19] ^ plain[19]);

        byte[] delve = new AreaInstance(1, InstanceDelve.MineArea, 7, 68, ReturnAreaHash: 0x8b9b).Encode();
        Assert.Equal(plain.Length + 2, delve.Length);
        Assert.Equal(0x08, delve[18] & 0x08);
        // Level [21], B1d [22], B28 [23], then u16 BE ReturnAreaHash at [24..25].
        Assert.Equal(0x8b, delve[24]);
        Assert.Equal(0x9b, delve[25]);

        byte[] names = InstanceEntities.AtlasTreeNames("Boss", "Delve", "");
        Assert.Equal("003C" + "0004" + "426F7373" + "0005" + "44656C7665" + "0000", Convert.ToHexString(names));

        byte[] renameWire = new LoginWriter().U16(InstanceEntities.AtlasTreeRenameId).U8(1).WStr("Harvest").ToArray();
        Assert.Equal((1, "Harvest"), InstanceEntities.ReadAtlasTreeRename(renameWire));
        // Short/truncated 0x2a7 payloads safely return an invalid preset index instead of throwing.
        Assert.Equal((byte.MaxValue, ""), InstanceEntities.ReadAtlasTreeRename([0x02, 0xa7]));
        Assert.Equal((byte.MaxValue, ""), InstanceEntities.ReadAtlasTreeRename([0x02, 0xa7, 0x00]));
        Assert.Equal([0x00, 0x91, 0x00], InstanceEntities.AtlasWindowReply());

        byte[] noReset = InstanceEntities.CharacterPassives(5, 3, [0x1234]);
        byte[] withReset = InstanceEntities.CharacterPassives(5, 3, [0x1234], freeReset: true);
        Assert.Equal(0x00, noReset[11]);
        Assert.Equal(0x01, withReset[11]);

        Assert.Equal(2, InstanceMapDevice.ReadActivateMemoryLine([0x02, 0x97, 0x02]));
        Assert.Equal(5, InstanceMapDevice.ReadActivateMavenInvitation([0x02, 0x98, 0x05]));
        Assert.Equal(2, ClientPackets.Find(InstanceEntities.ResetAllPassivesId)?.FixedLength);
        Assert.Equal(2, ClientPackets.Find(InstanceEntities.AtlasWindowRequestId)?.FixedLength);
        Assert.Equal(2, ClientPackets.Find(InstanceDelve.ReturnToWorldId)?.FixedLength);
        Assert.Equal(3, ClientPackets.Find(InstanceMapDevice.ActivateMemoryLineId)?.FixedLength);
        Assert.Equal(3, ClientPackets.Find(InstanceMapDevice.ActivateMavenInvitationId)?.FixedLength);
        Assert.Equal(renameWire.Length, ClientPackets.Length(InstanceEntities.AtlasTreeRenameId, renameWire));

        // LaunchCategory comes from each league's league.json rather than a hardcoded framework league name.
        var leagues = LeagueCatalog.FromEnvironment();
        Assert.Equal((byte)59, leagues.Find("Allflame")!.Entry().C1);
        Assert.Equal((byte)59, leagues.Find("Hardcore Allflame")!.Entry().C1);
        Assert.Equal((byte)59, leagues.Find("SSF Allflame")!.Entry().C1);
        Assert.Equal((byte)0, leagues.Find("Standard")!.Entry().C1);
    }

    [Fact]
    public async Task Atlas_boss_option_tree_names_free_resets_delve_return_and_receptacles_enforced()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        string owner = Program.CharacterName(player);
        var beach = Game.MapDevices.Atlas!.Find("MapWorldsBeach")!;
        var area = new MapDevice.MapArea(beach.Hash16, beach.Id, beach.Name, 16) { Level = 83 };
        var nonAtlasArea = new MapDevice.MapArea(beach.Hash16, beach.Id, beach.Name, 16) { Level = 75, UsesAtlas = false };
        try
        {
            // MapDevice.Run.Of preserves both UsesAtlas and BossOption.
            var open = MapDevice.Activate(owner, area, MapMods.None, bossOption: 2);
            var run = MapDevice.Run.Of(owner, open);
            Assert.True(run.Atlas);
            Assert.Equal((byte)2, open.BossOption);
            Assert.Equal((byte)2, run.BossOption);

            var simOpen = MapDevice.Activate(owner, nonAtlasArea, MapMods.None, bossOption: 0);
            var simRun = MapDevice.Run.Of(owner, simOpen);
            Assert.False(simRun.Atlas);
            Assert.Equal((byte)0, simRun.BossOption);

            // BossOption eligibility gating: requires UsesAtlas, non-Unique, tier >= minTier (T1 Maven, T9+ Eater/Exarch), and unlock flag.
            var rareMods = new MapMods(new Dictionary<uint, int>()) { MapRarity = ItemRarity.Rare };
            var uniqueMods = new MapMods(new Dictionary<uint, int>()) { MapRarity = ItemRarity.Unique };
            var withEater = Game.ComposeMapAreaStats(rareMods, 16, new Dictionary<uint, int>(), new HashSet<string>(), 0, deepwater: true, bossOption: 2);
            var withExarch = Game.ComposeMapAreaStats(rareMods, 16, new Dictionary<uint, int>(), new HashSet<string>(), 0, bossOption: 3);
            var withoutBoss = Game.ComposeMapAreaStats(rareMods, 16, new Dictionary<uint, int>(), new HashSet<string>(), 0, bossOption: 0);
            var uniqueBoss = Game.ComposeMapAreaStats(uniqueMods, 16, new Dictionary<uint, int>(), new HashSet<string>(), 0, bossOption: 2);
            var lowTierEater = Game.ComposeMapAreaStats(rareMods, 8, new Dictionary<uint, int>(), new HashSet<string>(), 0, bossOption: 2);
            var lowTierMaven = Game.ComposeMapAreaStats(rareMods, 1, new Dictionary<uint, int>(), new HashSet<string>(), 0, bossOption: 1);
            var zeroTierBoss = Game.ComposeMapAreaStats(rareMods, 0, new Dictionary<uint, int>(), new HashSet<string>(), 0, bossOption: 1);
            Assert.Equal(1, withEater[Stat.MapDeepwaterLeague]);
            // Stat 0x2a03 (10755): 1 = Tangle (Eater, bossOption 2), 2 = Cleansing Fire (Exarch, bossOption 3), unset for Maven.
            Assert.Equal(1, withEater[Stat.MapAtlasPrimordialBossInfluence]);
            Assert.Equal(2, withExarch[Stat.MapAtlasPrimordialBossInfluence]);
            Assert.False(withoutBoss.ContainsKey(Stat.MapAtlasPrimordialBossInfluence));
            Assert.False(uniqueBoss.ContainsKey(Stat.MapAtlasPrimordialBossInfluence));
            Assert.False(lowTierEater.ContainsKey(Stat.MapAtlasPrimordialBossInfluence));
            Assert.False(lowTierMaven.ContainsKey(Stat.MapAtlasPrimordialBossInfluence));
            Assert.False(zeroTierBoss.ContainsKey(Stat.MapAtlasPrimordialBossInfluence));

            // A family's league block: carried once, for the tree's families and the ones the map names itself.
            Assert.Contains("harvest", Game.MapLeagueBlockFamilies);
            var named = new MapMods(new Dictionary<uint, int>()) { AtlasFamilies = new HashSet<string> { "harvest" } };
            Assert.Equal(named.AtlasFamilies, named.WithFragmentStat(Stat.MapPackSizePlusPct, 5).AtlasFamilies);
            foreach (var (mods, tree, block) in new[] { (rareMods, new HashSet<string>(), 0), (rareMods, new HashSet<string> { "harvest" }, 1),
                (named, new HashSet<string>(), 1), (named, new HashSet<string> { "harvest" }, 1) })
                Assert.Equal(block, Game.ComposeMapAreaStats(mods, 3, new Dictionary<uint, int>(), tree, 0)
                    .GetValueOrDefault(Stat.MapHarvestSeedsAreTier1));

            player.Character.LeagueQuestFlags.Add(351); // MapDeviceTangleUnlocked
            Assert.Equal((byte)2, Game.EligibleBossOption(player, new MapDevice.Opening(area, rareMods), 2));
            Assert.Equal((byte)0, Game.EligibleBossOption(player, new MapDevice.Opening(nonAtlasArea, rareMods), 2));
            Assert.Equal((byte)0, Game.EligibleBossOption(player, new MapDevice.Opening(area, uniqueMods), 2));
            Assert.Equal((byte)0, Game.EligibleBossOption(player, new MapDevice.Opening(area with { Tier = 8 }, rareMods), 2));
            player.Character.LeagueQuestFlags.Remove(351);
            Assert.Equal((byte)0, Game.EligibleBossOption(player, new MapDevice.Opening(area, rareMods), 2));

            // Atlas tree names, locked preset rejections, control-char sanitization, and empty free resets.
            Game.SetAtlasTreeName(player, 0, "Mapping");
            Game.SetAtlasTreeName(player, 2, "Bosses");
            Assert.Equal(["Mapping", "", "Bosses"], Game.AtlasTreeNames(player));
            Assert.Equal(InstanceEntities.AtlasTreeNames("Mapping", "", "Bosses"), Game.AtlasTreeNamesMessage(player));

            Assert.False(AtlasFeature.TreeUnlocked(player, 1));
            await AtlasFeature.RenameAtlasPreset(player, (1, "LockedName"));
            Assert.Equal("", Game.AtlasTreeNames(player)[1]);
            await AtlasFeature.RenameAtlasPreset(player, (0, "  Clean\u0007Name\r\n  "));
            Assert.Equal("CleanName", Game.AtlasTreeNames(player)[0]);

            // Locked preset 1 refuses allocate, refund-all, and free-reset without consuming free reset.
            player.Character.AtlasPassivePoints = 5;
            ushort firstAtlasNode = Game.AtlasPassiveNodes.Links[AtlasPassives.StartGraphId].First();
            await AtlasFeature.ChangeAtlasPassive(player, 1, firstAtlasNode, allocate: true);
            Assert.Empty(player.Character.AtlasPassives[1]);

            Assert.Equal(0, Game.AtlasSlots(player, 0)[6] & InstanceEntities.PassiveFreeResetFlag);
            Game.SetAtlasFreeReset(player, true);
            Assert.Equal(InstanceEntities.PassiveFreeResetFlag, Game.AtlasSlots(player, 0)[6] & InstanceEntities.PassiveFreeResetFlag);
            await AtlasFeature.ResetAtlasPassivesFree(player, 1); // locked preset
            Assert.True(Game.HasAtlasFreeReset(player));
            await AtlasFeature.ResetAtlasPassivesFree(player, 0); // empty preset does not consume flag
            Assert.True(Game.HasAtlasFreeReset(player));
            Game.SetAtlasFreeReset(player, false);
            Assert.Equal(0, Game.AtlasSlots(player, 0)[6] & InstanceEntities.PassiveFreeResetFlag);

            // Empty ordinary passive tree does not consume passives.free_reset.
            Assert.Equal(0, Game.PassivesMessage(player).Bytes[11] & InstanceEntities.PassiveFreeResetFlag);
            Game.SetPassiveFreeReset(player, true);
            Assert.Equal(InstanceEntities.PassiveFreeResetFlag, Game.PassivesMessage(player).Bytes[11] & InstanceEntities.PassiveFreeResetFlag);
            await Game.ResetPassivesFree(player);
            Assert.True(Game.HasPassiveFreeReset(player));
            Game.SetPassiveFreeReset(player, false);
            Assert.Equal(0, Game.PassivesMessage(player).Bytes[11] & InstanceEntities.PassiveFreeResetFlag);

            // Delve return-area filtering: towns & own hideout allowed; maps, pinnacle bosses, Labyrinth, Delve Hub/Mine rejected.
            var homeHideout = Game.HomeHideout(player)!;
            Assert.True(Game.IsReturnableWorldArea(player, AreaLayouts.LioneyesWatch));
            Assert.True(Game.IsReturnableWorldArea(player, homeHideout.AreaHash16));
            Assert.False(Game.IsReturnableWorldArea(player, 0));
            Assert.False(Game.IsReturnableWorldArea(player, beach.Hash16));
            Assert.False(Game.IsReturnableWorldArea(player, InstanceDelve.MineArea));
            Assert.False(Game.IsReturnableWorldArea(player, 0x8b9b)); // Delve_Hub
            var shaper = Game.MapDevices.Atlas.Find("ShaperBoss");
            if (shaper is not null) Assert.False(Game.IsReturnableWorldArea(player, shaper.Hash16));
            var plaza = WaypointCatalog.Areas.Entries.FirstOrDefault(e => e.Id == WaypointCatalog.LabyrinthPlaza);
            if (plaza is not null) Assert.False(Game.IsReturnableWorldArea(player, plaza.Hash16));

            // ResolveReturnWorldArea rejects maps/Delve Hub and falls back to HomeHideout while preserving live town instances.
            var fallback = Game.ResolveReturnWorldArea(player, (beach.Hash16, 42UL), 0x8b9b);
            Assert.NotEqual(beach.Hash16, fallback.Area);
            Assert.NotEqual((ushort)0x8b9b, fallback.Area);
            Assert.Equal(homeHideout.AreaHash16, fallback.Area);
            Assert.Equal(0UL, fallback.Instance);

            var savedTown = Game.ResolveReturnWorldArea(player, (AreaLayouts.LioneyesWatch, 77UL), null);
            Assert.Equal((AreaLayouts.LioneyesWatch, 77UL), savedTown);

            // MavenInvitations (row 86) & QuestItem Maven Invitation (MavenMapAtlas1, row 4131) check.
            var items = Program.Items;
            var atlasInvite1 = items.Find("Metadata/Items/MapFragments/Maven/MavenMapAtlas1")!;
            var voidInvite1 = items.Find("Metadata/Items/MapFragments/Maven/MavenMapVoid1New")!;
            var beachMapItem = items.Find("Metadata/Items/Maps/MapKeyTier1")!;
            var skillBook = items.Find("Metadata/Items/QuestItems/SkillBooks/Book-a1q6")!;
            Assert.Equal("QuestItem", atlasInvite1.Class);
            Assert.True(PlayerInventories.AcceptsItem(PlayerInventories.MavenInvitations, atlasInvite1.Hash, atlasInvite1.Class));
            Assert.True(PlayerInventories.AcceptsItem(PlayerInventories.MavenInvitations, voidInvite1.Hash, voidInvite1.Class));
            Assert.False(PlayerInventories.AcceptsItem(PlayerInventories.MavenInvitations, beachMapItem.Hash, beachMapItem.Class));
            Assert.False(PlayerInventories.AcceptsItem(PlayerInventories.MavenInvitations, skillBook.Hash, skillBook.Class));
            Assert.False(PlayerInventories.Accepts(PlayerInventories.MavenInvitations, "Map"));
            Assert.False(PlayerInventories.Accepts(PlayerInventories.MavenInvitations, "MapKey"));

            var inviteSlot = new MapDevice.Slot(atlasInvite1.Hash, atlasInvite1.Class, atlasInvite1.Name, atlasInvite1.Row, [], 0)
            {
                Components = atlasInvite1.Components, BaseId = atlasInvite1.Id,
            };
            var inviteOpen = Game.MapDevices.Check([inviteSlot], owner, items.Mods, out string inviteWhy);
            Assert.NotNull(inviteOpen);
            Assert.Equal("MavenHub", inviteOpen.Area.Id);
            Assert.Equal(atlasInvite1.Row, inviteOpen.ItemRow);
            Assert.Equal("", inviteWhy);
        }
        finally
        {
            MapDevice.Close(owner);
        }
    }
}
