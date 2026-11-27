using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modules.Atlas;

namespace GameServer.Gateway.Tests;

public sealed class EldritchAltarsTests
{
    [Fact]
    public async Task Eldritch_altar_wire_layout_BaseData_0x028e_0x02c9_HUD_and_atlas_passives()
    {
        // 1. C→S 0x028e packet framing & reader (7 bytes: u16 BE 0x028e, u32 BE altarEntityId, u8 choice 1|2).
        Assert.Equal(7, ClientPackets.Find(InstanceMapDevice.SelectEldritchAltarId)?.FixedLength);
        byte[] selectWire = new LoginWriter().U16(InstanceMapDevice.SelectEldritchAltarId).U32(0x12345678).U8(2).ToArray();
        Assert.Equal(7, selectWire.Length);
        var parsed = InstanceMapDevice.ReadSelectEldritchAltar(selectWire);
        Assert.Equal(0x12345678u, parsed.AltarEntityId);
        Assert.Equal((byte)2, parsed.Choice);

        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;

        // 2. Verify client.db loaded Eldritch packs, all 267 AltarChoices, and every currency drop ID resolves in ItemCatalog.
        Assert.NotEmpty(EldritchInfluence.Packs[PrimordialBoss.Tangle]);
        Assert.NotEmpty(EldritchInfluence.Packs[PrimordialBoss.CleansingFire]);
        Assert.Equal(267, EldritchAltars.AllChoices.Count);
        Assert.NotEmpty(EldritchAltars.CurrencyDropStatToItem);
        foreach (string itemId in EldritchAltars.CurrencyDropStatToItem.Values.Distinct())
            Assert.NotNull(Program.Items.ById(itemId));

        // 3. Roll choices with Eldritch Gaze (extra downside + 50% upside effect) and Wrath of the Cosmos (100% Exarch downside effect + extra upside).
        var gazeStats = new Dictionary<uint, int>
        {
            [Stat.MapPrimordialAltarExtraDownside] = 1,
            [Stat.MapPrimordialAltarUpsideEffectPlusPct] = 50,
            [Stat.AtlasKeystoneEldritchGaze] = 1,
        };
        var rolledTangle = EldritchAltars.RollAltarChoices(PrimordialBoss.Tangle, hardMode: false, allowBossTarget: true, gazeStats, new Random(42));
        Assert.NotNull(rolledTangle);
        Assert.Equal(2, rolledTangle.Value.Choice1.Downsides.Count); // Primary + Extra Downside 1 from Eldritch Gaze
        Assert.Equal(2, rolledTangle.Value.Choice2.Downsides.Count);
        Assert.All(rolledTangle.Value.Choice1.Downsides, m => Assert.Equal(PrimordialBoss.Tangle, m.Def.Boss));
        Assert.All(rolledTangle.Value.Choice1.Upsides, m => Assert.Equal(PrimordialBoss.Tangle, m.Def.Boss));

        var cosmosStats = new Dictionary<uint, int>
        {
            [Stat.MapSearingExarchAltarDownsideEffectPlusPct] = 100,
            [Stat.Of("map_searing_exarch_alter_extra_upside")] = 100,
            [Stat.AtlasKeystoneWrathOfTheCosmos] = 1,
        };
        var rolledExarch = EldritchAltars.RollAltarChoices(PrimordialBoss.CleansingFire, hardMode: false, allowBossTarget: false, cosmosStats, new Random(7));
        Assert.NotNull(rolledExarch);
        Assert.Equal(2, rolledExarch.Value.Choice1.Upsides.Count); // Primary + Extra Upside 1 from Wrath of the Cosmos
        Assert.NotEqual(EldritchAltars.TargetMapBoss, rolledExarch.Value.Choice1.TargetType);
        Assert.NotEqual(EldritchAltars.TargetMapBoss, rolledExarch.Value.Choice2.TargetType);

        // 4. Verify 0x02bb BaseData map wire layout: elemType = 8 (i32[]), exact u32 LE hashes, values[0] = Mods.HASH32.
        var entries = EldritchAltars.BuildBaseDataEntries(rolledTangle.Value.Choice1, rolledTangle.Value.Choice2);
        Assert.Contains(entries, e => e.KeyHash == PrimordialAltarObject.Choice1DownsidePrimary);
        Assert.Contains(entries, e => e.KeyHash == PrimordialAltarObject.Choice1DownsideExtra1);
        Assert.Contains(entries, e => e.KeyHash == PrimordialAltarObject.Choice1UpsidePrimary);
        Assert.Contains(entries, e => e.KeyHash == PrimordialAltarObject.Choice2DownsidePrimary);
        Assert.Contains(entries, e => e.KeyHash == PrimordialAltarObject.Choice2UpsidePrimary);

        byte[] blob = new PrimordialAltarObject(50, 60, entries).Blob();
        Assert.Equal((byte)0x02, blob[0]); // Awake flag
        Assert.Equal((byte)entries.Count, blob[1]);
        int pos = 2;
        foreach (var expected in entries)
        {
            uint keyHash = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(pos));
            pos += 4;
            byte flag = blob[pos++];
            byte valCount = blob[pos++];
            byte elemType = blob[pos++];
            Assert.Equal(expected.KeyHash, keyHash);
            Assert.Equal((byte)0, flag);
            Assert.Equal((byte)(1 + expected.StatValues.Count), valCount);
            Assert.Equal(PrimordialAltarObject.Int32ArrayElemType, elemType); // elemType MUST be 8
            uint modHash32 = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(pos));
            pos += 4;
            Assert.Equal(expected.ModHash32, modHash32);
            foreach (int sv in expected.StatValues)
            {
                Assert.Equal(sv, BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(pos)));
                pos += 4;
            }
        }

        // 5. Spawn an altar in an Eldritch instance, handle C→S 0x028e, verify activation, 0x02c9 HUD, and double-click guard.
        var ctx = EldritchInfluence.GetOrCreateContext(player, PrimordialBoss.Tangle, mapTier: 16);
        var altar = await EldritchAltars.TrySpawnAltarAt(player, ctx, (player.Layout.SpawnX, player.Layout.SpawnY),
            guaranteed: true, rng: new Random(13));
        Assert.NotNull(altar);
        Assert.False(altar.Activated);

        Assert.True(await EldritchAltars.HandleSelectAltar(player, new InstanceMapDevice.SelectEldritchAltar(altar.EntityId, 1)));
        Assert.True(altar.Activated);
        Assert.Equal(1, ctx.AltarsActivated);
        int activeCount = ctx.PlayerUpsides.Count + ctx.PlayerDownsides.Count
            + ctx.MinionUpsides.Count + ctx.MinionDownsides.Count
            + ctx.BossUpsides.Count + ctx.BossDownsides.Count;
        Assert.True(activeCount >= 2);

        // HUD packet encodes S→C 0x02c9 Ruleset 99 sub-id 0 with non-empty mod lists.
        byte[] hud = EldritchAltars.CurrentHudMessage(player);
        Assert.Equal(InstanceRulesets.DataId, BinaryPrimitives.ReadUInt16BigEndian(hud));
        Assert.Equal(InstanceRulesets.PrimordialInfluenceRulesetRow, hud[2]);

        // Duplicate selection on already-activated altar is a safe no-op.
        Assert.True(await EldritchAltars.HandleSelectAltar(player, new InstanceMapDevice.SelectEldritchAltar(altar.EntityId, 2)));
        Assert.Equal(1, ctx.AltarsActivated);
    }

    [Fact]
    public async Task Eldritch_story_progression_28_map_tracker_voidstone_and_Kirac_sync()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        var data = Program.WorldOf(player).CharacterData;

        // 1. Tangle story progression T9..T16 (AtlasPrimordialBossInfluence rows 0..8).
        player.Character.LeagueQuestFlags.Add(EldritchInfluence.LqfTangleUnlocked); // 351
        Assert.Equal(0, EldritchInfluence.CurrentStoryStep(player, PrimordialBoss.Tangle)?.Progress);

        // Simulate advancing T9 -> T10 -> T11 (Infinite Hunger) -> T12..T15 -> T16 (Eater of Worlds).
        player.Character.LeagueQuestFlags.Add(205); // T9
        Assert.Equal(1, EldritchInfluence.CurrentStoryStep(player, PrimordialBoss.Tangle)?.Progress);
        player.Character.LeagueQuestFlags.Add(206); // T10
        var step2 = EldritchInfluence.CurrentStoryStep(player, PrimordialBoss.Tangle)!;
        Assert.Equal(2, step2.Progress);
        Assert.Equal(11, step2.MinMapTier);
        Assert.Equal(4673, step2.QuestItemRow); // QuestTangleKey (Writhing Invitation)

        // Defeating Infinite Hunger sets LQF 207 and advances to Progress 3 (T12).
        await EldritchInfluence.OnPrimordialBossKilled(player, EldritchInfluence.InfiniteHungerVariety, [player]);
        Assert.Contains(EldritchInfluence.LqfTangleMiniBossDefeated, player.Character.LeagueQuestFlags);
        Assert.Equal(3, EldritchInfluence.CurrentStoryStep(player, PrimordialBoss.Tangle)?.Progress);

        // Advance through T12..T15 to Progress 7 (T16 Screaming Invitation).
        player.Character.LeagueQuestFlags.UnionWith([208, 209, 210, 211]);
        var step7 = EldritchInfluence.CurrentStoryStep(player, PrimordialBoss.Tangle)!;
        Assert.Equal(7, step7.Progress);
        Assert.Equal(16, step7.MinMapTier);
        Assert.Equal(4675, step7.QuestItemRow); // QuestTangleBossKey (Screaming Invitation)

        // Defeating Eater of Worlds sets LQF 212 + 221, NOT yet Voidstone Slot 2 (708) until Exarch (222) also falls.
        await EldritchInfluence.OnPrimordialBossKilled(player, EldritchInfluence.EaterOfWorldsVariety, [player]);
        Assert.Contains(EldritchInfluence.LqfTangleBossDefeated, player.Character.LeagueQuestFlags);
        Assert.Contains(EldritchInfluence.LqfTangleWatchstone, player.Character.LeagueQuestFlags);
        Assert.DoesNotContain(EldritchInfluence.LqfEldritchVoidstoneSlot2, player.Character.LeagueQuestFlags);
        Assert.Equal(8, EldritchInfluence.CurrentStoryStep(player, PrimordialBoss.Tangle)?.Progress);

        // Defeating Black Star sets LQF 323; defeating Searing Exarch sets LQF 328 + 222 AND unlocks Voidstone Slot 2 (LQF 708)!
        await EldritchInfluence.OnPrimordialBossKilled(player, EldritchInfluence.BlackStarVariety, [player]);
        Assert.Contains(EldritchInfluence.LqfCleansingFireMiniBossDefeated, player.Character.LeagueQuestFlags);
        await EldritchInfluence.OnPrimordialBossKilled(player, EldritchInfluence.SearingExarchVariety, [player]);
        Assert.Contains(EldritchInfluence.LqfCleansingFireBossDefeated, player.Character.LeagueQuestFlags);
        Assert.Contains(EldritchInfluence.LqfCleansingFireWatchstone, player.Character.LeagueQuestFlags);
        Assert.Contains(EldritchInfluence.LqfEldritchVoidstoneSlot2, player.Character.LeagueQuestFlags);

        // 2. Post-story repeatable 28-map tracker (Progress 8, T14+) + S→C 0x01a3 bytes 0–1 encoding.
        var bossInfo = new MonsterInfo("MapBoss", "Map Boss", 83, MonsterRarity.Unique, player.Layout.SpawnX, player.Layout.SpawnY);
        data.Change(EldritchInfluence.TangleTrackerKey, _ => "13");
        var repeatCtx14 = new EldritchInfluence.InstanceContext { Boss = PrimordialBoss.Tangle, MapTier = 14 };
        await EldritchInfluence.TryAdvanceRepeatableTrack(player, repeatCtx14, PrimordialBoss.Tangle, 14, bossInfo, [player], new Random(1));
        Assert.Equal((byte)14, AtlasFeature.PrimordialTrackerCounts(data).Tangle);

        // Reaching step 28 drops Screaming Invitation and resets tracker to 0.
        data.Change(EldritchInfluence.TangleTrackerKey, _ => "27");
        var repeatCtx28 = new EldritchInfluence.InstanceContext { Boss = PrimordialBoss.Tangle, MapTier = 16 };
        await EldritchInfluence.TryAdvanceRepeatableTrack(player, repeatCtx28, PrimordialBoss.Tangle, 16, bossInfo, [player], new Random(1));
        Assert.Equal((byte)0, AtlasFeature.PrimordialTrackerCounts(data).Tangle);

        // 3. Kirac Shop progression sync with AtlasProgress.Completed (KiracLevels tier & reroll gold cost).
        var stash = Game.PersonalStashOf(player);
        var threeMaps = AtlasProgress.Maps.Where(n => n.IsNormalMap).Take(3).ToList();
        AtlasProgress.Complete(data, threeMaps);
        int synced = EldritchInfluence.SyncKiracProgress(player, data, completedTier: 3);
        Assert.Equal(3, synced);
        Assert.Equal(3, KiracProgress.For(stash).Tier);
        Assert.Equal(2000, KiracProgress.For(stash).ShopRerollCost);
    }
}
