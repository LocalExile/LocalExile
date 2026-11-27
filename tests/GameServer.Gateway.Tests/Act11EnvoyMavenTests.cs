using System.Buffers.Binary;
using System.Text;
using GameServer.Core.Protocol.Instance;
using GameServer.Modules.Atlas;

namespace GameServer.Gateway.Tests;

public sealed class Act11EnvoyMavenTests
{
    [Fact]
    public async Task Maven_witness_persistence_0x1a3_section4_LE_0x1a4_flag4_and_ruleset112()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        var data = Program.WorldOf(player).CharacterData;

        // 1. Pick three distinct witnessable Atlas maps and persist them as Maven-witnessed.
        var witnessable = AtlasProgress.Maps.Where(n => n.IsWitnessable && n.AreaHashes.Count > 0).Take(3).ToList();
        Assert.Equal(3, witnessable.Count);
        ushort[] hashes = [.. witnessable.Select(n => n.AreaHashes.First())];
        foreach (ushort h in hashes)
            Assert.True(MavenWitnessing.AddWitnessedArea(data, h));
        // Duplicate witness is a no-op.
        Assert.False(MavenWitnessing.AddWitnessedArea(data, hashes[0]));

        var stored = MavenWitnessing.WitnessedAreaHashes(data);
        Assert.Equal(hashes.Order().ToArray(), stored.ToArray());

        // 2. S→C 0x01a3 section 0x04 encodes WorldAreas.HASH16 as LITTLE-ENDIAN u16s after the two 22-byte masks.
        byte[] full = AtlasProgress.FullMessage(new HashSet<string>(), new HashSet<string>(),
            voidstones: 0x04, tangleCount: 7, cleansingCount: 11, witnessedAreas: stored);
        Assert.Equal(7 + 44 + 1 + stored.Count * 2, full.Length);
        Assert.Equal(new byte[] { 0x01, 0xa3, 7, 11, 0x04, 0x07, 0 }, full[..7]);
        int sec4 = 7 + 44;
        Assert.Equal((byte)stored.Count, full[sec4]);
        for (int i = 0; i < stored.Count; i++)
        {
            ushort decodedLe = BinaryPrimitives.ReadUInt16LittleEndian(full.AsSpan(sec4 + 1 + i * 2, 2));
            Assert.Equal(stored[i], decodedLe);
        }

        // 3. S→C 0x01a4 flag 0x04 marks incremental Maven witness while preserving tangle/cleansing counts and bonus bit.
        var node = witnessable[0];
        byte[] inc = AtlasProgress.NodeMessage(node, bonus: true, witnessed: true, tangleCount: 5, cleansingCount: 9);
        Assert.Equal(new byte[]
        {
            0x01, 0xa4,
            (byte)(node.Hash16 >> 8), (byte)node.Hash16,
            5, 9, 0x06,
        }, inc);

        // 4. Witnessing 3 Atlas maps with LQF 225 unlocks Atlas1 Ready LQF 1019; Void1 unlocks LQF 1100 when all 4 WitnessAreas are witnessed.
        player.Character.LeagueQuestFlags.Add(EnvoyEncounters.LqfMavenBeacon);
        var readyAtlas = await MavenWitnessing.EvaluateWitnessThresholds(player, data);
        Assert.Contains(readyAtlas, f => f.Id == "Atlas1");
        Assert.Contains(MavenWitnessing.LqfAtlasReady, player.Character.LeagueQuestFlags);

        var void1 = MapBossFights.Current.InvitationById("Void1")!;
        Assert.Equal(4, void1.WitnessAreaHashes.Count);
        foreach (ushort vh in void1.WitnessAreaHashes)
            Assert.True(MavenWitnessing.AddWitnessedArea(data, vh));
        var readyVoid = await MavenWitnessing.EvaluateWitnessThresholds(player, data);
        Assert.Contains(readyVoid, f => f.Id == "Void1");
        Assert.Contains(void1.ReadyLeagueFlag, player.Character.LeagueQuestFlags);

        // 5. Kirac open_maven_invitation places invitation in MavenInvitations slot 6 (Atlas1) and sends 0x7b 0x52.
        Assert.True(await MavenWitnessing.OpenMavenInvitationUi(player, "open_maven_invitation Atlas1"));
        var inviteInSlot = Game.ItemsOf(player).Items(PlayerInventories.MavenInvitations)
            .FirstOrDefault(i => i.X == 6);
        Assert.NotNull(inviteInSlot);
        Assert.Equal(Program.Items.Row(4131)!.Hash, inviteInSlot.Item.TypeHash);

        // 6. Crucible boss resolution uses actual witnessed Atlas areas' bosses, and completing the Crucible
        //    clears witnessed Atlas areas, clears Ready LQF 1019, sets Complete LQF 979 (+ 374, 426), and grants +1 passive / +2 respec.
        var atlas1 = MapBossFights.Current.InvitationById("Atlas1")!;
        var (crucibleBosses, usedHashes) = MavenWitnessing.ResolveCrucibleBosses(player, atlas1);
        Assert.Equal(3, crucibleBosses.Count);
        Assert.Equal(3, usedHashes.Count);
        Assert.All(usedHashes, h => Assert.Contains(h, hashes));

        int pointsBefore = player.Character.AtlasPassivePoints;
        ushort respecBefore = Game.AtlasRespecPoints(player);
        var crucibleState = new MavenWitnessing.CrucibleState
        {
            Fight = atlas1,
            Started = true,
            UsedWitnessHashes = usedHashes,
        };
        await MavenWitnessing.CompleteCrucibleFight(player, crucibleState, (10, 10));
        Assert.Contains(atlas1.CompleteLeagueFlag, player.Character.LeagueQuestFlags);
        Assert.Contains(MavenWitnessing.LqfCompletedAnyMavenFight, player.Character.LeagueQuestFlags);
        Assert.Contains(MavenWitnessing.LqfVoidQuestUnlocked, player.Character.LeagueQuestFlags);
        Assert.DoesNotContain(MavenWitnessing.LqfAtlasReady, player.Character.LeagueQuestFlags);
        Assert.Equal(0, MavenWitnessing.CountWitnessedAtlasMaps(MavenWitnessing.WitnessedAreas(data)));
        Assert.Equal(pointsBefore + 1, player.Character.AtlasPassivePoints);
        Assert.Equal(respecBefore + 2, Game.AtlasRespecPoints(player));

        // 7. MavenBossFightRules (Ruleset 112): verify 0x001e string event and 0x02c9 sub-ids 0, 1, 2 wire bytes.
        byte[] ev = MavenWitnessing.RulesetEventPacket(0x12345678, "maven_boss_stop_rings");
        Assert.Equal((ushort)0x001e, BinaryPrimitives.ReadUInt16BigEndian(ev));
        Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32BigEndian(ev.AsSpan(2, 4)));
        Assert.Equal((ushort)"maven_boss_stop_rings".Length, BinaryPrimitives.ReadUInt16BigEndian(ev.AsSpan(6, 2)));
        Assert.Equal("maven_boss_stop_rings", Encoding.ASCII.GetString(ev, 8, ev.Length - 8));

        byte[] ringState = MavenWitnessing.MavenBossRingStatePacket(2, 10f, 20f, 30f, 40f);
        Assert.Equal((ushort)0x02c9, BinaryPrimitives.ReadUInt16BigEndian(ringState));
        Assert.Equal((byte)112, ringState[2]);
        Assert.Equal((byte)18, ringState[3]); // compact len: u8 sub-id(0) + u8 state(2) + 4*f32(16) = 18
        Assert.Equal((byte)0, ringState[4]);
        Assert.Equal((byte)2, ringState[5]);
        Assert.Equal(10f, BinaryPrimitives.ReadSingleLittleEndian(ringState.AsSpan(6, 4)));
        Assert.Equal(40f, BinaryPrimitives.ReadSingleLittleEndian(ringState.AsSpan(18, 4)));

        Assert.Equal(new byte[] { 0x02, 0xc9, 112, 1, 1 }, MavenWitnessing.MavenBossStopRingsPacket());
        Assert.Equal(new byte[] { 0x02, 0xc9, 112, 1, 2 }, MavenWitnessing.MavenBossStartRingsPacket());
    }

    [Fact]
    public void Envoy_progression_stat_15661_and_post_boss_arena_envoys()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        var lqfs = player.Character.LeagueQuestFlags;
        var normalMods = new MapMods(new Dictionary<uint, int>()) { MapRarity = ItemRarity.Rare };
        MapDevice.Run RunAt(int tier) => new(
            Program.CharacterName(player),
            new MapDevice.MapArea(0x1234, "MapWorldsStrand", "Strand", tier),
            normalMods);

        // 1. T3+ map after LQF 1038 (Explore Atlas) and before LQF 1115 spawns Envoy (484).
        Assert.Null(EnvoyEncounters.DetermineMapEnvoyNpc(player, RunAt(3), null));
        lqfs.Add(EnvoyEncounters.LqfExploreAtlas);
        Assert.Null(EnvoyEncounters.DetermineMapEnvoyNpc(player, RunAt(2), null));
        Assert.Equal(EnvoyEncounters.EnvoyId, EnvoyEncounters.DetermineMapEnvoyNpc(player, RunAt(3), null));

        // 2. Once LQF 1115 is set and before Kirac LQF 984, Envoy waits; after LQF 984 he returns for "A Warning" (LQF 425).
        lqfs.Add(EnvoyEncounters.LqfMetEnvoy);
        Assert.Null(EnvoyEncounters.DetermineMapEnvoyNpc(player, RunAt(3), null));
        lqfs.Add(EnvoyEncounters.LqfKiracOnEnvoy);
        Assert.Equal(EnvoyEncounters.EnvoyId, EnvoyEncounters.DetermineMapEnvoyNpc(player, RunAt(3), null));

        // 3. After Maven Beacon (LQF 225): Stat 15661 (map_spawn_envoy) resolves 2 (TangleQuest) on T5+ before LQF 351,
        //    and 3 (CleansingFireQuest) on T6+ before LQF 352.
        lqfs.Add(EnvoyEncounters.LqfEnvoyMavenWarning);
        lqfs.Add(EnvoyEncounters.LqfMavenBeacon);

        Assert.Equal(0, Game.ResolveEnvoySpawnStat(4, ItemRarity.Rare, lqfs));
        Assert.Equal(2, Game.ResolveEnvoySpawnStat(5, ItemRarity.Rare, lqfs));
        Assert.Equal(0, Game.ResolveEnvoySpawnStat(6, ItemRarity.Unique, lqfs));

        var composedTangle = Game.ComposeMapAreaStats(normalMods, 5, new Dictionary<uint, int>(), new HashSet<string>(), -1, spawnEnvoy: 2);
        Assert.Equal(2, composedTangle[Stat.MapSpawnEnvoy]);
        Assert.Equal(EnvoyEncounters.EnvoyTangleId, EnvoyEncounters.DetermineMapEnvoyNpc(player, RunAt(5), composedTangle));

        lqfs.Add(EnvoyEncounters.LqfTangleBeacon);
        Assert.Equal(0, Game.ResolveEnvoySpawnStat(5, ItemRarity.Rare, lqfs));
        Assert.Equal(3, Game.ResolveEnvoySpawnStat(6, ItemRarity.Rare, lqfs));

        var composedExarch = Game.ComposeMapAreaStats(normalMods, 6, new Dictionary<uint, int>(), new HashSet<string>(), -1, spawnEnvoy: 3);
        Assert.Equal(3, composedExarch[Stat.MapSpawnEnvoy]);
        Assert.Equal(EnvoyEncounters.EnvoyCleansingFireId, EnvoyEncounters.DetermineMapEnvoyNpc(player, RunAt(6), composedExarch));

        lqfs.Add(EnvoyEncounters.LqfCleansingFireBeacon);
        Assert.Equal(0, Game.ResolveEnvoySpawnStat(16, ItemRarity.Rare, lqfs));

        // 4. All 5 post-boss arena Envoys map cleanly and have supported NPC definitions with invisible/npc_mode states.
        Assert.Equal(EnvoyEncounters.EnvoyTangleBoss1Id,
            EnvoyEncounters.PostBossEnvoyForKill("MapWorldsPrimordialBoss1", "Metadata/Monsters/AtlasInvaders/DoomMonsters/DoomBoss_"));
        Assert.Equal(EnvoyEncounters.EnvoyTangleBoss2Id,
            EnvoyEncounters.PostBossEnvoyForKill("MapWorldsPrimordialBoss4", "Metadata/Monsters/AtlasInvaders/ConsumeMonsters/ConsumeBoss"));
        Assert.Equal(EnvoyEncounters.EnvoyCleansingFireBoss1Id,
            EnvoyEncounters.PostBossEnvoyForKill("MapWorldsPrimordialBoss2", "Metadata/Monsters/AtlasInvaders/BlackStarMonsters/BlackStarBoss"));
        Assert.Equal(EnvoyEncounters.EnvoyCleansingFireBoss2Id,
            EnvoyEncounters.PostBossEnvoyForKill("MapWorldsPrimordialBoss3", "Metadata/Monsters/AtlasInvaders/CleansingMonsters/CleansingBoss"));
        Assert.Equal(EnvoyEncounters.EnvoyMavenId,
            EnvoyEncounters.PostBossEnvoyForKill("MavenBoss", "Metadata/Monsters/MavenBoss/MavenBrainBoss"));

        foreach (string id in new[]
        {
            EnvoyEncounters.EnvoyId, EnvoyEncounters.EnvoyMavenId, EnvoyEncounters.EnvoyTangleId,
            EnvoyEncounters.EnvoyCleansingFireId, EnvoyEncounters.EnvoyTangleBoss1Id, EnvoyEncounters.EnvoyTangleBoss2Id,
            EnvoyEncounters.EnvoyCleansingFireBoss1Id, EnvoyEncounters.EnvoyCleansingFireBoss2Id,
            MavenWitnessing.MavenObserverNpcId,
        })
        {
            var npc = Game.Npcs.Find(id);
            Assert.NotNull(npc);
            Assert.True(npc.Supported, $"{id} must be supported by NpcObject");
        }
    }
}
