using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class DeferredSystemsTests
{
    static Item Gem(string name)
    {
        var found = Program.Items.FindGem(name)!.Value;
        return ItemCatalog.Create(found.Type) with { GemVariant = found.Variant.Index };
    }

    static void EquipLinked(ClientState conn, string weaponName, params string[] gems)
    {
        var items = Game.ItemsOf(conn);
        foreach (var existing in items.EquipmentSnapshot().Where(e => e.Row == PlayerInventories.Weapon1))
            items.Remove(PlayerInventories.Weapon1, existing.Item.Id);
        var weapon = ItemCatalog.Create(Program.Items.Find(weaponName)!);
        var sockets = gems.Select(n => new ItemSocket(SocketColour.White, Gem(n))).ToArray();
        items.Add(PlayerInventories.Weapon1, 0, 0, weapon with { Sockets = sockets, Links = [(byte)sockets.Length] });
    }

    [Fact]
    public async Task Brands_snipe_and_generals_cry_rules_and_execution()
    {
        string path = Path.Combine(Path.GetTempPath(), $"deferred-combat-{Guid.NewGuid():N}.db");
        var previous = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Saves.Open(path);
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = SpellTotemExecutionTests.Connection("DeferredCombat");
        conn.Clock.Start();
        conn.Walk.Teleport(100, 100, 0);
        conn.PlayerStats[Program.Stats.Key("global_always_hit")!.Value] = 1;
        foreach (string attr in new[] { "strength", "dexterity", "intelligence" })
            conn.PlayerStats[Program.Stats.Key($"additional_{attr}")!.Value] = 300;
        using var wire = new SpellTotemExecutionTests.Wire();
        var session = Game.ProjectilesFor("deferred", wire.Stream, wire.Ciphers, conn);
        try
        {
            // 1. Brands: limits, presentation, Penance Brand of Dissipation pulses & Brand Recall
            var emptyStats = new Dictionary<uint, int>();
            var equityStats = new Dictionary<uint, int>
            {
                [Program.Stats.Key("base_total_number_of_sigils_allowed")!.Value] = 5,
                [Program.Stats.Key("keystone_runebinder")!.Value] = 1,
            };
            Assert.Equal(3, Game.BrandMaxTotal(emptyStats, emptyStats));
            Assert.Equal(5, Game.BrandMaxTotal(equityStats, emptyStats));
            Assert.Equal(1, Game.BrandMaxPerTarget(emptyStats, emptyStats));
            Assert.Equal(2, Game.BrandMaxPerTarget(equityStats, emptyStats));
            var magmaPres = Game.PresentationOf(Game.BrandFamily.Magma);
            Assert.Equal((ushort)1547, magmaPres.AttachedBuffRow);
            Assert.Equal(4481u, magmaPres.AttachedMiscAnimatedRow);

            EquipLinked(conn, "Gnarled Branch", "Penance Brand of Dissipation", "Brand Recall");
            var brandEffects = Game.GemSkillEffects(conn).ToDictionary(g => g.Name);
            var penance = brandEffects["Penance Brand of Dissipation"];
            var recall = brandEffects["Brand Recall"];
            Assert.True(Game.IsBrandSkill(conn, penance.Effect));
            Assert.True(Game.IsBrandRecall(conn, recall.Effect));

            var enemy = new SpawnedMonster("brand target", new(110, 100, 0), 0, 0, 100000) { Hostile = true };
            conn.Monsters[701] = enemy;

            Assert.True(await Game.ResolveCastBrand("brand", wire.Stream, wire.Ciphers, conn, penance.Id, penance,
                (110, 100), conn.ActionInterruptionGeneration, () => true));
            Assert.Equal(1, Game.BrandCountFor(conn));
            Assert.Equal(1, Game.AttachedBrandCountFor(conn));
            var activeBrand = Assert.Single(Game.ActiveBrandsFor(conn));
            Assert.Equal(701u, activeBrand.AttachedMonsterId);

            // Deterministic activation tick ramps Penance Brand of Dissipation energy and deals damage
            int energyAfterAttach = activeBrand.Energy;
            Assert.Equal(1, energyAfterAttach);
            uint lifeBefore = enemy.Life;
            double t = activeBrand.NextActivationAt + 0.05;
            await Game.TickBrands("brand", wire.Stream, wire.Ciphers, conn, t);
            Assert.Equal(energyAfterAttach + 1, activeBrand.Energy);
            Assert.True(enemy.Life < lifeBefore);

            // Brand Recall detaches (resetting Dissipation energy), reattaches, and fires an immediate pulse
            uint lifeBeforeRecall = enemy.Life;
            Assert.True(await Game.CastBrandRecall("brand", wire.Stream, wire.Ciphers, conn, recall.Id, recall, () => true));
            Assert.Equal(1, activeBrand.Energy);
            Assert.True(enemy.Life < lifeBeforeRecall);
            Game.ClearBrands(conn);
            Assert.Equal(0, Game.BrandCountFor(conn));

            // 2. Snipe: stage math, manual-cast disable on linked bow skill, and staged bow release
            EquipLinked(conn, "Crude Bow", "Snipe", "Burning Arrow");
            var snipeEffects = Game.GemSkillEffects(conn).ToDictionary(g => g.Name);
            var snipe = snipeEffects["Snipe"];
            var burningArrow = snipeEffects["Burning Arrow"];
            Assert.NotNull(Game.GemSkillInstanceDisabled(conn, burningArrow.Effect, burningArrow.Id, triggered: false));
            Assert.Null(Game.GemSkillInstanceDisabled(conn, burningArrow.Effect, burningArrow.Id, triggered: true));
            Assert.Equal(6, Game.SnipeMaxStages(conn, snipe.Id));

            var snipeSupported = Game.SupportedSkill(conn, snipe.Id);
            var baSupported = Game.SupportedSkill(conn, burningArrow.Id);
            int hitPerStage = snipeSupported.Stats[Program.Stats.Key("snipe_triggered_skill_hit_damage_+%_final_per_stage")!.Value];
            int ailmentPerStage = snipeSupported.Stats[Program.Stats.Key("snipe_triggered_skill_ailment_damage_+%_final_per_stage")!.Value];
            var (hitMult, ailmentMult) = Game.SnipeStageMultipliers(snipeSupported.Stats, 4, baSupported.Stats);
            Assert.Equal(0.6 * (1 + 4 * hitPerStage / 100.0), hitMult, 6);
            Assert.Equal(0.6 * (1 + 4 * ailmentPerStage / 100.0), ailmentMult, 6);

            int released = await Game.ReleaseSnipeStages("snipe", wire.Stream, wire.Ciphers, conn, snipe.Id, snipe, 3, (120, 100));
            Assert.Equal(1, released);

            // 3. General's Cry & Mirage Warriors: non-blocking wire flags, corpse consumption, rush & strike
            var nonBlockingBlob = new MonsterObject(100, 100, 100, MonsterObject.BaseComponents) { Blocking = false }.Blob();
            // Object header (2) + Positioned: x(4) y(4) rot(4) team(2) flags0(1) flags1(1 @17) hidden(1) blocking(1) noWalkerBlock(1 @20)
            Assert.Equal((byte)0x0a, nonBlockingBlob[17]);
            Assert.Equal((byte)0, nonBlockingBlob[19]);
            Assert.Equal((byte)1, nonBlockingBlob[20]);

            EquipLinked(conn, "OneHandSword1", "General's Cry", "Heavy Strike");
            var cryEffects = Game.GemSkillEffects(conn).ToDictionary(g => g.Name);
            var cry = cryEffects["General's Cry"];
            var crySupported = Game.SupportedSkill(conn, cry.Id);
            var linkedMelee = Game.GeneralsCryLinkedMeleeSkills(conn, crySupported);
            Assert.Contains(linkedMelee, s => s.Effect.Name == "Heavy Strike");
            Assert.InRange(Game.MirageWarriorDamageFactor(conn, cry.Level, cry.Quality), 0.35, 0.55);

            var zombie = Game.Monsters.Find("Metadata/Monsters/Zombies/Zombie")!;
            var corpse = new SpawnedMonster("corpse", new(106, 100, 0), 0, 0, 1000) { Hostile = true, Dead = true, Life = 0, Variety = zombie };
            var target = new SpawnedMonster("gc target", new(108, 100, 0), 0, 0, 100000) { Hostile = true, Variety = zombie };
            conn.Monsters.Clear();
            conn.Monsters[710] = corpse;
            conn.Monsters[711] = target;

            int summoned = await Game.GeneralsCryReleased("gc", wire.Stream, wire.Ciphers, conn, cry.Id, (100, 100), 60, conn.ProjectileEpoch);
            Assert.True(summoned >= 1);
            Assert.False(conn.Monsters.ContainsKey(710));
            var warrior = conn.Monsters.Values.Single(m => m.NonBlocking && !m.Hostile);
            Assert.False(LivingCollision.Blocks(warrior));

            uint gcTargetBefore = target.Life;
            double gcNow = Game.AreaTime(conn);
            await Game.MirageWarriorsTick("gc", wire.Stream, wire.Ciphers, conn, gcNow + 0.05);
            await Game.MirageWarriorsTick("gc", wire.Stream, wire.Ciphers, conn, gcNow + 1.5);
            Assert.True(target.Life < gcTargetBefore);
        }
        finally
        {
            Game.ClearBrands(conn);
            session.Invalidate();
            conn.Closed = true;
            lifetime.Stop(_ => { });
            Game.CombatLifetime = previousLifetime;
            PlayerInventories.Forget(conn.Character!);
            Program.SetGameLogic(previous!);
            TestEnvironment.ReleaseSaves(path);
        }
    }

    [Fact]
    public void Lore_interactables_trick_chests_and_character_voice_lines()
    {
        // 1. Lore interactables: Transitionable state 2 + single/cycling/multi-page selection
        var npcs = Game.Npcs.Value;
        var diary = npcs.Find("Metadata/NPC/Info/MerveilDiary");
        Assert.NotNull(diary);
        var preparedDiary = QuestobjectsRules.PrepareLoreNpc(diary!, "Metadata/NPC/Info/MerveilDiary.ao", 12.5f);
        Assert.Equal((byte)2, preparedDiary.TransitionState);
        Assert.Equal("Metadata/NPC/Info/MerveilDiary.ao", preparedDiary.Model);
        Assert.Equal(12.5f, preparedDiary.Height);

        var stone = npcs.Find("Metadata/NPC/Info/Karui/Stone1");
        Assert.NotNull(stone);
        var stoneTalks = npcs.TalksOf(stone!).ToList();
        var pickedStone = QuestobjectsRules.SelectLoreTalk(stoneTalks, new HashSet<string>(), 0);
        Assert.NotNull(pickedStone);
        Assert.Equal(0, pickedStone!.Index);

        var letters = npcs.Find("Metadata/NPC/Info/BeachEncampmentNotes");
        Assert.NotNull(letters);
        var letterTalks = npcs.TalksOf(letters!).ToList();
        var firstUnread = QuestobjectsRules.SelectLoreTalk(letterTalks, new HashSet<string>(), 0);
        Assert.NotNull(firstUnread);
        var allReadFlags = new HashSet<string>(letterTalks.SelectMany(t => t.HeardFlags));
        var cycled = QuestobjectsRules.SelectLoreTalk(letterTalks, allReadFlags, 2);
        Assert.NotNull(cycled);
        Assert.Equal(letterTalks[2].Row, cycled!.Row);

        var shaperBook = npcs.Find("Metadata/NPC/Info/ShaperMemoryFragmentBook");
        if (shaperBook is not null)
        {
            var pages = npcs.TalksOf(shaperBook).ToList();
            if (pages.Select(p => p.Option).Distinct(StringComparer.Ordinal).Count() > 1)
                Assert.Null(QuestobjectsRules.SelectLoreTalk(pages, new HashSet<string>(), 0));
        }

        // 2. Amarissa & Ambrosia Trick Chests + unique buried emerge classification
        var emerge = new MonsterEmerge();
        foreach (string id in new[] { "Metadata/Monsters/Seawitch/SeaWitchBossSpark", "Metadata/Monsters/Seawitch/SeaWitchBossFirestorm" })
        {
            var variety = Game.Monsters.Find(id);
            Assert.NotNull(variety);
            Assert.Equal(EmergeKind.Buried, emerge.Kind(variety!, MonsterRarity.Unique));
            Assert.Equal((byte)2, MonsterEmerge.CreateState(variety!, EmergeKind.Buried, unrisen: false));
        }
        foreach (var rule in QuestobjectsRules.TrickChestRules)
        {
            var info = QuestObjectTypes.Find(rule.ChestType);
            Assert.NotNull(info);
            Assert.True(QuestObject.CanSerialize(info!.Components));
            var placed = new QuestobjectsRules.PlacedTrickChest(rule, rule.DefaultModel, 100, 100, 0, null, (112, 100));
            byte[] closedBlob = QuestobjectsRules.TrickChestBlob(placed, info, opened: false);
            byte[] openedBlob = QuestobjectsRules.TrickChestBlob(placed, info, opened: true);
            Assert.NotEmpty(closedBlob);
            Assert.NotEqual(closedBlob, openedBlob);
        }

        // 3. Player character voice lines (CharacterAudioEvents) catalog, triggers & 0x2bc wire bytes
        var audio = CharacterAudioCatalog.Default.Value;
        Assert.Equal(291, audio.All.Count);
        Assert.Equal(261, audio.ServerDriven.Count);

        var enterCoast = audio.ById("EnterCannibalBay");
        Assert.NotNull(enterCoast);
        Assert.Equal("Visited-1_2", enterCoast!.QuestFlagId);
        Assert.Equal(125, enterCoast.QuestFlagRow);
        Assert.Equal(4000, enterCoast.DelayMs);
        Assert.True(enterCoast.HasCharacterLines);

        var killBrutus = audio.ById("KillBrutus");
        Assert.NotNull(killBrutus);
        Assert.Equal("AudioEventKilledBrutus", killBrutus!.QuestFlagId);
        Assert.Equal(2000, killBrutus.DelayMs);
        Assert.True(killBrutus.HasCharacterLines && killBrutus.HasGoddessLines);

        Assert.Equal(["AudioEventKilledBrutus"], CharacterVoiceLines.FlagsForKill("1_1_7_2", "Metadata/Monsters/Brute/BossBrute"));
        Assert.Equal("a4-BeastShake1", CharacterVoiceLines.FlagForAreaEntry("1_4_3_1", isMapArea: false));
        Assert.Equal("EnteredMaps", CharacterVoiceLines.FlagForAreaEntry("MapAtlasStrand", isMapArea: true));
        Assert.Equal("Met-Maramoa", CharacterVoiceLines.FlagForNpcMeet("Metadata/NPC/Act3/Maramoa"));

        byte[] questUpdate = InstanceEntities.PlayerQuestFlagsUpdate(0x1000, [125]);
        Assert.Equal((byte)0x02, questUpdate[0]);
        Assert.Equal((byte)0xbc, questUpdate[1]);
        // PlayerQuestFlagsUpdate carries flag 0x02 with bit 125 set (block 1, bit 61 = 1UL << 61)
        int blockOffset = questUpdate.Length - 8;
        ulong mask = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(questUpdate.AsSpan(blockOffset, 8));
        Assert.Equal(1UL << (125 - 64), mask);
    }
}
