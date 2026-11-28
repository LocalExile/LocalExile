using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;
using GameServer.Modules.Atlas;

namespace GameServer.Gateway.Tests;

public sealed class Act11ZanaEagonAtlasTests
{
    [Fact]
    public async Task Atlas_0x01a3_sections_0x08_0x10_astrolabe_0x0075_and_vault_milestones()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        var data = Program.WorldOf(player).CharacterData;

        // 1. S→C 0x01a3 section 0x08 (Memory Lines, all fields big-endian).
        var lineWire = new AtlasProgress.MemoryLineWire(0x01020304u, 0xABCD, 0x1234, CurrentStepIndex: 2, TotalSteps: 4);
        byte[] linePkt = AtlasProgress.MemoryLinesMessage([lineWire], voidstones: 0x05, tangleCount: 7, cleansingCount: 11);
        Assert.Equal(new byte[] { 0x01, 0xa3, 7, 11, 0x05, 0x08, 0 }, linePkt[..7]);
        Assert.Equal((byte)1, linePkt[7]);
        Assert.Equal(0x01020304u, BinaryPrimitives.ReadUInt32BigEndian(linePkt.AsSpan(8, 4)));
        Assert.Equal((ushort)0xABCD, BinaryPrimitives.ReadUInt16BigEndian(linePkt.AsSpan(12, 2)));
        Assert.Equal((ushort)0x1234, BinaryPrimitives.ReadUInt16BigEndian(linePkt.AsSpan(14, 2)));
        Assert.Equal((byte)2, linePkt[16]);
        Assert.Equal((byte)4, linePkt[17]);
        Assert.Equal(18, linePkt.Length);

        // 2. S→C 0x01a3 section 0x10 (Astrolabe Shaped Regions, big-endian + CompactInt stat values).
        var shapedWire = new AtlasProgress.ShapedRegionWire(
            QuadrantId: 2,
            AtlasMissionTypeRow: 1,
            Nodes: [new AtlasProgress.ShapedNodeWire(0x55AA, [new AtlasProgress.ShapedNodeModWire(0x1C99, [1, 5])])]);
        byte[] shapedPkt = AtlasProgress.ShapedRegionsMessage([shapedWire], voidstones: 0x1f, tangleCount: 0, cleansingCount: 0);
        Assert.Equal(new byte[] { 0x01, 0xa3, 0, 0, 0x1f, 0x10, 0 }, shapedPkt[..7]);
        Assert.Equal((byte)1, shapedPkt[7]);     // regionCount
        Assert.Equal((byte)2, shapedPkt[8]);     // quadrantId
        Assert.Equal((byte)1, shapedPkt[9]);     // atlasMissionTypeRow
        Assert.Equal((byte)1, shapedPkt[10]);    // nodeCount
        Assert.Equal((ushort)0x55AA, BinaryPrimitives.ReadUInt16BigEndian(shapedPkt.AsSpan(11, 2)));
        Assert.Equal((byte)1, shapedPkt[13]);    // modCount
        Assert.Equal((ushort)0x1C99, BinaryPrimitives.ReadUInt16BigEndian(shapedPkt.AsSpan(14, 2)));
        Assert.Equal((byte)1, shapedPkt[16]);    // CompactInt(1)
        Assert.Equal((byte)5, shapedPkt[17]);    // CompactInt(5)

        // 3. Applying an Astrolabe via C→S 0x0075 consumes the item, shapes the quadrant, and completing its nodes unlocks LQF 1717.
        Assert.Equal(11, AstrolabesAndVaults.Astrolabes.Count);
        var targetNode = AtlasProgress.Maps.First(n => n.IsNormalMap && !n.RequiresSpecificItem && n.Region == 0);
        var astrolabeType = Program.Items.Find("Metadata/Items/Currency/AstrolabeHarvest")!;
        var items = Game.ItemsOf(player);
        var placed = items.Put(PlayerInventories.MainInventory,
            new InventoryItem(items.NewItemId(), 0, 0, ItemCatalog.Create(astrolabeType)));

        await AtlasFeature.UseItemOnNode(player, new InstanceMapDevice.UseItemOnAtlasNode(
            PlayerInventories.InventoryId(PlayerInventories.MainInventory), placed.Id, targetNode.Hash16));

        Assert.DoesNotContain(items.Items(PlayerInventories.MainInventory), i => i.Id == placed.Id);
        var shapedRegions = AstrolabesAndVaults.LoadShapedRegions(data);
        Assert.Single(shapedRegions);
        Assert.Equal((byte)0, shapedRegions[0].QuadrantId);
        Assert.Equal((byte)1, shapedRegions[0].MissionTypeRow); // Harvest
        Assert.Contains(targetNode.Id, shapedRegions[0].RemainingNodeIds);
        Assert.Contains(AstrolabesAndVaults.LqfShapedRegionStarted, player.Character.LeagueQuestFlags);

        // Complete every map in the Shaped Region -> unlocks Memory Vault (LQF 1717) & LQF 1686.
        foreach (string nodeId in shapedRegions[0].RemainingNodeIds.ToArray())
            await AstrolabesAndVaults.OnMapCompleted(player, AtlasProgress.Find(nodeId)!);

        Assert.Empty(AstrolabesAndVaults.LoadShapedRegions(data));
        Assert.Contains(AstrolabesAndVaults.LqfVaultAvailable, player.Character.LeagueQuestFlags);
        Assert.Contains(AstrolabesAndVaults.LqfFirstShapedRegionCompleted, player.Character.LeagueQuestFlags);

        // 4. Witnessing all 4 Memory Vaults sets progressive milestones 1755 -> 1756 -> 1757 -> 1692.
        player.Character.LeagueQuestFlags.Add(710);
        await AstrolabesAndVaults.UpdateVaultWitnessMilestones(player);
        Assert.Contains(AstrolabesAndVaults.LqfAnyVaultWitnessed, player.Character.LeagueQuestFlags);
        Assert.DoesNotContain(AstrolabesAndVaults.LqfAllVaultsWitnessed, player.Character.LeagueQuestFlags);

        player.Character.LeagueQuestFlags.UnionWith([711, 712, 713]);
        await AstrolabesAndVaults.UpdateVaultWitnessMilestones(player);
        Assert.Contains(AstrolabesAndVaults.LqfTwoVaultsWitnessed, player.Character.LeagueQuestFlags);
        Assert.Contains(AstrolabesAndVaults.LqfThreeVaultsWitnessed, player.Character.LeagueQuestFlags);
        Assert.Contains(AstrolabesAndVaults.LqfAllVaultsWitnessed, player.Character.LeagueQuestFlags);
    }

    [Fact]
    public async Task Memory_tears_colourful_roses_and_atlas_anomalies_lifecycle()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        var data = Program.WorldOf(player).CharacterData;

        // 1. Memory Tear qualification stages across T14+ -> T15+ -> T16+ -> repeatable.
        player.Character.QuestFlags.Add(Quests.EndGameAvailable);
        Assert.Equal(0, MemoryTears.EligibleTearStage(player, 13));
        Assert.Equal(1, MemoryTears.EligibleTearStage(player, 14));
        player.Character.LeagueQuestFlags.Add(MemoryTears.LqfEagonHideoutUnlocked);
        player.Character.LeagueQuestFlags.Add(MemoryTears.LqfNeglectDefeated);
        Assert.Equal(2, MemoryTears.EligibleTearStage(player, 15));
        player.Character.LeagueQuestFlags.Add(MemoryTears.LqfFearDefeated);
        Assert.Equal(3, MemoryTears.EligibleTearStage(player, 16));
        player.Character.LeagueQuestFlags.Add(MemoryTears.LqfDreadDefeated);
        player.Character.LeagueQuestFlags.Add(MemoryTears.LqfAtlasDestructionHasBegun);
        Assert.Equal(4, MemoryTears.EligibleTearStage(player, 16));

        // MemoryLine items are no longer blocked by "not yet implemented" in MapDevice.
        var kiracMemory = Program.Items.Find("Metadata/Items/MemoryLines/MemoryLineKirac")!;
        var memorySlot = new MapDevice.Slot(kiracMemory.Hash, kiracMemory.Class, kiracMemory.Name, kiracMemory.Row, [], 0)
        {
            BaseId = kiracMemory.Id,
        };
        Game.MapDevices.Check([memorySlot], player.Character.Name, Program.Items.Mods, out string why, player: player);
        Assert.DoesNotContain("not yet implemented", why, StringComparison.OrdinalIgnoreCase);

        // 2. Colourful Roses (Quest 205) flag progression: 3319 -> 3320 -> 3318 -> 3321 -> 3322 -> clears 3319.
        var roses = await MemoryTears.InitColourfulRoses(player);
        Assert.Contains(MemoryTears.QfMemoryBossCollectBothRoses, player.Character.QuestFlags);
        Assert.True(await MemoryTears.TryCollectRose(player, roses, isBlue: true));
        Assert.Contains(MemoryTears.QfBlueRoseHeld, player.Character.QuestFlags);
        // Cannot carry both roses simultaneously
        Assert.False(await MemoryTears.TryCollectRose(player, roses, isBlue: false));
        Assert.True(await MemoryTears.TryReturnHeldRose(player, roses));
        Assert.DoesNotContain(MemoryTears.QfBlueRoseHeld, player.Character.QuestFlags);
        Assert.Contains(MemoryTears.QfBlueRoseReturned, player.Character.QuestFlags);
        Assert.Contains(MemoryTears.QfMemoryBossCollectBothRoses, player.Character.QuestFlags);

        Assert.True(await MemoryTears.TryCollectRose(player, roses, isBlue: false));
        Assert.Contains(MemoryTears.QfRedRoseHeld, player.Character.QuestFlags);
        Assert.True(await MemoryTears.TryReturnHeldRose(player, roses));
        Assert.DoesNotContain(MemoryTears.QfRedRoseHeld, player.Character.QuestFlags);
        Assert.Contains(MemoryTears.QfRedRoseReturned, player.Character.QuestFlags);
        Assert.DoesNotContain(MemoryTears.QfMemoryBossCollectBothRoses, player.Character.QuestFlags);

        // 3. Atlas Anomalies (AtlasNode 166..181): discover -> unlock LQF + 1884 -> open via MapOpening with 0 items -> consume LQF.
        Assert.Equal(16, AtlasAnomalies.All.Count);
        var discovered = await AtlasAnomalies.TryDiscoverOnMapCompletion(player, new Random(7), guaranteed: true);
        Assert.NotNull(discovered);
        Assert.Contains(discovered.UnlockLqf, player.Character.LeagueQuestFlags);
        Assert.Contains(AtlasAnomalies.LqfAnomalyEncountered, player.Character.LeagueQuestFlags);

        // Opening an unlocked Anomaly via MapOpening with Target = NodeHash16 and 0 items succeeds with ConsumeSlots = [].
        var prevLeague = player.LeagueModule;
        const string testOwner = "test-anomalies";
        player.LeagueModule = new(new GameServer.Core.Accounts.League("atlas-anomaly-test", "Standard", false, false),
            new HashSet<string>(["Game", "Atlas", testOwner]));
        var openReq = new MapOpening(player, [], false, null, "Put a map into the Map Device first.")
        {
            Target = discovered.NodeHash16,
        };
        AtlasAnomalies.Register(testOwner);
        try
        {
            GameEvents.ResolveMapOpening(openReq);
            Assert.NotNull(openReq.Opening);
            Assert.Equal(discovered.AreaId, openReq.Opening.Area.Id);
            Assert.NotNull(openReq.ConsumeSlots);
            Assert.Empty(openReq.ConsumeSlots);
        }
        finally
        {
            GameEventOwners.Release(testOwner, GameEventOwners.ModuleRank);
            player.LeagueModule = prevLeague;
        }

        // Consuming the Anomaly clears its per-Anomaly LQF while preserving LQF 1884 (Kirac 'Anomalies').
        Assert.True(await AtlasAnomalies.ConsumeAnomaly(player, discovered.UnlockLqf));
        Assert.DoesNotContain(discovered.UnlockLqf, player.Character.LeagueQuestFlags);
        Assert.Contains(AtlasAnomalies.LqfAnomalyEncountered, player.Character.LeagueQuestFlags);
    }
}
