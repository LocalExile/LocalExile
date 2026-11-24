using System.Buffers.Binary;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class BatchHudStatsCombatTests
{
    [Fact]
    public async Task Player_hud_stats_minion_counters_and_removable_buff_cancel_behave_per_client_contract()
    {
        using var fixture = new MinionUtilityTests.Fixture();
        // 1 & 2. Requirement stats (main_hand_weapon_type, virtual_*_for_requirements, is_dual_wielding)
        //        and light radius virtual stats (virtual_light_radius_+%, virtual_blind_does_not_affect_light_radius).
        var player = new Player(new(1, "1_1_1", 300, 300, 100, 100, null, []), 1, 1)
        {
            Character = { Name = "BatchHudStats_" + Guid.NewGuid().ToString("N"), ClassRow = 1 },
            Connection = { PlayerSent = true },
            Instance = new(),
            PlayerLife = 100,
            LeagueModule = new(new League("batch-hud-test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        player.Clock.Stop();
        try
        {
            using var statHook = GameEvents.PlayerStatSources.Add("Game", (p, s) =>
            {
                if (!ReferenceEquals(p, player)) return s;
                s[Stat.AdditionalStrength] = s.GetValueOrDefault(Stat.AdditionalStrength) + 100;
                s[Stat.AdditionalDexterity] = s.GetValueOrDefault(Stat.AdditionalDexterity) + 200;
                s[Stat.AdditionalIntelligence] = s.GetValueOrDefault(Stat.AdditionalIntelligence) + 80;
                s[Stat.LightRadiusPlusPct] = s.GetValueOrDefault(Stat.LightRadiusPlusPct) + 15;
                s[Stat.LightRadiusPlusPctWhilePhased] = s.GetValueOrDefault(Stat.LightRadiusPlusPctWhilePhased) + 25;
                s[Stat.KeystoneBlindMonk] = 1;
                return s;
            });

            var items = Game.ItemsFor(player);
            var inv = Game.ItemsOf(player);
            var bow = ItemCatalog.Create(items.Find("Crude Bow")!, random: new Random(1));
            inv.Add(PlayerInventories.Weapon1, 0, 0, bow);
            player.Pools = Game.PoolsFor(player, 1);

            IReadOnlyDictionary<uint, int> stats;
            using (new Game.PlayerStatScope(player, fresh: true))
                stats = Game.CharacterPlayerStats(player);

            Assert.Equal(9, stats[Stat.MainHandWeaponType]);
            Assert.Equal(stats[Stat.Strength], stats[Stat.VirtualStrengthForRequirements]);
            Assert.Equal(stats[Stat.Dexterity], stats[Stat.VirtualDexterityForRequirements]);
            Assert.Equal(stats[Stat.Intelligence], stats[Stat.VirtualIntelligenceForRequirements]);
            Assert.Equal(15, stats[Stat.VirtualLightRadiusPlusPct]);
            Assert.Equal(1, stats[Stat.VirtualBlindDoesNotAffectLightRadius]);

            // Phasing updates virtual_light_radius_+% with light_radius_+%_while_phased.
            player.Phasing = true;
            using (new Game.PlayerStatScope(player, fresh: true))
                stats = Game.CharacterPlayerStats(player);
            Assert.Equal(40, stats[Stat.VirtualLightRadiusPlusPct]);
            player.Phasing = false;

            // Dual wielding emits main_hand_weapon_type, off_hand_weapon_type and is_dual_wielding on snapshots.
            var bowEntry = inv.Items(PlayerInventories.Weapon1).Single();
            inv.Remove(PlayerInventories.Weapon1, bowEntry.Id);
            var sword1 = ItemCatalog.Create(items.Find("Rusted Sword")!, random: new Random(2));
            var sword2 = ItemCatalog.Create(items.Find("Rusted Sword")!, random: new Random(3));
            inv.Add(PlayerInventories.Weapon1, 0, 0, sword1);
            inv.Add(PlayerInventories.Offhand1, 0, 0, sword2);
            using (new Game.PlayerStatScope(player, fresh: true))
                stats = Game.CharacterPlayerStats(player);
            Assert.Equal(1, stats[Stat.IsDualWielding]);
            Assert.True(stats.ContainsKey(Stat.OffHandWeaponType));

            // Pre-existing virtual_*_for_requirements overrides are preserved (not clobbered by StatSnapshotCache).
            using (GameEvents.PlayerStatSources.Add("Game", (p, s) =>
            {
                if (ReferenceEquals(p, player)) s[Stat.VirtualStrengthForRequirements] = 999;
                return s;
            }))
            {
                using (new Game.PlayerStatScope(player, fresh: true))
                    stats = Game.CharacterPlayerStats(player);
                Assert.Equal(999, stats[Stat.VirtualStrengthForRequirements]);
            }

            // 3. HUD minion counter icons (PlayerMinionsChange) for all active player minion types, including Spectre
            //    row 0, VaalAnimatedWeapon row 56, Elemental Relics from PassiveGrantedSkills, and Area.Monsters check.
            SpawnedMonster LivingMinion(uint id, string label, bool register = true)
            {
                var m = new SpawnedMonster(label, new PlayerWalk(100, 100, 30), 10, 1, 100) { Hostile = false };
                if (register) player.Area.Monsters[id] = m;
                return m;
            }
            GolemCatalog.Definition Def(string id, string kind, bool isGolem = false) =>
                new(0, id, kind, "", 0, [], false, false, IsGolem: isGolem);

            player.Summons.Fighters[201] = new(201, LivingMinion(201, "spectre"), Def("RaiseSpectre", "Spectre"), 0x1001);
            player.Summons.Fighters[202] = new(202, LivingMinion(202, "zombie"), Def("RaiseZombie", "Zombie"), 0x1002);
            player.Summons.Fighters[203] = new(203, LivingMinion(203, "skeleton"), Def("SummonSkeletons", "Skeleton"), 0x1003);
            player.Summons.Fighters[204] = new(204, LivingMinion(204, "srs"), Def("SummonRagingSpirit", "RagingSpirit"), 0x1004);
            player.Summons.Fighters[207] = new(207, LivingMinion(207, "vaal_aw"), Def("VaalAnimateWeapon", "VaalAnimatedWeapon"), 0x1007);
            player.Summons.Fighters[208] = new(208, LivingMinion(208, "unregistered", register: false), Def("RaiseZombie", "Zombie"), 0x1008);
            player.Golems.All[205] = new(205, LivingMinion(205, "golem"), Def("SummonFlameGolem", "Fire", isGolem: true), 0x1005);
            player.Reservations.Fighters[206] = new(206, LivingMinion(206, "agony"), Def("HeraldOfAgony", "Agony"), 0x1006);
            player.Character.Passives.Add(19641); // Unwavering Crusade -> passive-granted SummonElementalRelic
            var relicMonster = LivingMinion(209, "fire_relic");
            player.Minions.Add(new(209, relicMonster, "SummonElementalRelic", "Fire") { Until = 100 });

            var rosterPacket = Game.SentinelOwnershipPresentation(player);
            Assert.NotNull(rosterPacket);
            Assert.Equal(0x02bc, BinaryPrimitives.ReadUInt16BigEndian(rosterPacket.AsSpan(0, 2)));
            Assert.Equal((ushort)0, player.Summons.PresentedOwnership[201].Type);
            Assert.Equal((ushort)7, player.Summons.PresentedOwnership[202].Type);
            Assert.Equal((ushort)9, player.Summons.PresentedOwnership[203].Type);
            Assert.Equal((ushort)20, player.Summons.PresentedOwnership[204].Type);
            Assert.Equal((ushort)24, player.Summons.PresentedOwnership[205].Type);
            Assert.Equal((ushort)39, player.Summons.PresentedOwnership[206].Type);
            Assert.Equal((ushort)56, player.Summons.PresentedOwnership[207].Type);
            Assert.False(player.Summons.PresentedOwnership.ContainsKey(208)); // missing from Area.Monsters
            Assert.Equal((ushort)61, player.Summons.PresentedOwnership[209].Type);
            Assert.NotEqual((ushort)0, player.Summons.PresentedOwnership[209].Source);
            Assert.Null(Game.SentinelOwnershipPresentation(player)); // No changes -> no duplicate packet.

            player.Summons.Fighters.Clear();
            player.Golems.All.Clear();
            player.Reservations.Fighters.Clear();
            player.Minions.Remove(209);
            player.Area.Monsters.Clear();
            Assert.NotNull(Game.SentinelOwnershipPresentation(player));
            Assert.Empty(player.Summons.PresentedOwnership);

            // 4. Generic removable buff cancel (C->S 0x4b) via AreaBuffs: accepts Removable=1 buffs, rejects unremovable debuffs.
            Assert.True(Game.IsRemovableBuff(GracePeriod.BuffRow));
            Assert.False(Game.IsRemovableBuff(1)); // chilled is non-removable

            ushort PublishAndTakeInstance(Buff buff)
            {
                ushort expected = player.Area.Buffs.Next;
                player.Area.Buffs.Set(player.Id, buff);
                player.Area.Buffs.Publish();
                Assert.NotNull(player.Area.Buffs.Find(player.Id, expected));
                return expected;
            }

            // Grace Period cancel clears EntryProtection and queues AreaBuffs removal.
            await Game.StartGracePeriod(player);
            ushort graceWire = player.Area.Buffs.Next;
            player.Area.Buffs.Publish();
            Assert.Equal(GracePeriod.BuffRow, player.Area.Buffs.Find(player.Id, graceWire)?.Row);
            Assert.True(await Game.CancelRemovableBuff(player, graceWire));
            Assert.Null(player.EntryProtection.CurrentBuff);
            Assert.False(player.Area.Buffs.Has(player.Id, GracePeriod.BuffRow, player.Id));

            // Dead player cannot cancel removable buffs.
            var deadPlayer = new Player(new(1, "1_1_1", 300, 300, 100, 100, null, []), 1, 1)
            {
                Character = { Name = "DeadCancel_" + Guid.NewGuid().ToString("N"), ClassRow = 1 },
                Connection = { PlayerSent = true },
                Instance = new(),
                PlayerLife = 0,
            };
            await Game.StartGracePeriod(deadPlayer);
            ushort deadGraceWire = deadPlayer.Area.Buffs.Next;
            deadPlayer.Area.Buffs.Publish();
            Assert.False(await Game.CancelRemovableBuff(deadPlayer, deadGraceWire));
            Assert.NotNull(deadPlayer.EntryProtection.CurrentBuff);

            // Vaal Immortal Call uses AreaBuffs (no wire instance collision) and cancels cleanly via 0x4b.
            fixture.Player.God = true;
            fixture.Equip(("Vaal Immortal Call", 1));
            var vicGem = Game.GemSkillEffects(fixture.Player).Single(g => Game.SkillsFor(fixture.Player).EffectId(g.Effect) == "VaalImmortalCall");
            Game.RefreshVaalSouls(fixture.Player);
            for (int i = 0; i < 100; i++) fixture.Player.VaalSouls.Gain();
            var vicPlan = Game.CooldownFor(fixture.Player, vicGem.Effect, vicGem.Level, vicGem.Quality);
            Assert.True(Game.TryBeginSkillCooldownUse(fixture.Player, vicGem.Id, vicGem.Effect, vicPlan, false,
                () => fixture.Player.ActionCount++, out var vicTicket));
            ushort vicExpectedWire = fixture.Player.Area.Buffs.Next;
            Assert.True(await Game.CastGemSkill(fixture.Player, vicGem.Id, (100, 100), (120, 100), cooldownUse: vicTicket));
            fixture.Player.Area.Buffs.Publish();
            Assert.Equal((ushort)109, fixture.Player.Area.Buffs.Find(fixture.Player.Id, vicExpectedWire)?.Row);
            Assert.True(Game.VaalImmortalCallActive(fixture.Player, Game.AreaTime(fixture.Player)));
            Assert.True(await Game.CancelRemovableBuff(fixture.Player, vicExpectedWire));
            Assert.False(Game.VaalImmortalCallActive(fixture.Player, Game.AreaTime(fixture.Player)));
            Assert.False(fixture.Player.Area.Buffs.HasRow(fixture.Player.Id, 109));

            // Arcane Surge cancel clears SupportGems state and AreaBuffs entry.
            Game.GainArcaneSurgeLocked(player, 0);
            ushort surgeWire = PublishAndTakeInstance(new(895, player.Id, U68: Buff.Seconds(4)));
            Assert.True(await Game.CancelRemovableBuff(player, surgeWire));
            Assert.False(Game.ArcaneSurgeActive(player, 0));
            Assert.False(player.Area.Buffs.Has(player.Id, 895, player.Id));

            // Timed Phasing cancel clears PhasingUntil, AreaBuffs entry, and updates light radius / phasing stats.
            Game.GainPhasing(player, 4);
            ushort phasingWire = player.Area.Buffs.Next;
            player.Area.Buffs.Publish();
            Assert.True(player.IsPhasing);
            Assert.True(await Game.CancelRemovableBuff(player, phasingWire));
            Assert.False(player.IsPhasing);
            Assert.False(player.Area.Buffs.HasRow(player.Id, 336));

            // Non-removable debuff (chilled, row 1) is rejected even when live in AreaBuffs.
            ushort chillWire = PublishAndTakeInstance(new(1, player.Id, U68: Buff.Seconds(4)));
            Assert.False(await Game.CancelRemovableBuff(player, chillWire));
            Assert.True(player.Area.Buffs.Has(player.Id, 1, player.Id));
            Assert.False(await Game.CancelRemovableBuff(player, 999));
        }
        finally
        {
            PlayerInventories.Forget(player.Character.Name!);
        }
    }

    [Fact]
    public void Combat_visuals_knockback_monster_armours_death_xp_penalty_and_cooldown_reset_work()
    {
        // 5. Actor animation seed (+0x21c) defaults to 0 and serializes non-zero seeds into MonsterObject.Blob().
        var zeroBlob = new MonsterObject(100, 100, 10, MonsterObject.BaseComponents).Blob();
        var seededBlob = new MonsterObject(100, 100, 10, MonsterObject.BaseComponents) { AnimationSeed = 0x1234 }.Blob();
        Assert.Equal(zeroBlob.Length, seededBlob.Length);
        var diffs = Enumerable.Range(0, zeroBlob.Length).Where(i => zeroBlob[i] != seededBlob[i]).ToArray();
        Assert.Equal(2, diffs.Length);
        Assert.Equal(diffs[0] + 1, diffs[1]);
        Assert.Equal((ushort)0x1234, BinaryPrimitives.ReadUInt16LittleEndian(seededBlob.AsSpan(diffs[0], 2)));

        // 6. S->C 0x1b0 ActionResultFlag encoder matches verified big-endian layout (reader 0x141fe4440).
        var flagPacket = InstanceProtocol.ActionResultFlag(0x10203040u, 0x1234, 0x05060708u, true,
            actionByte: 0x02, skillIndex: 0x03, orderKey: 0x11223344u, checkpoint: 0x5566);
        Assert.Equal(21, flagPacket.Length);
        Assert.Equal(InstanceProtocol.ActionResultFlagId, BinaryPrimitives.ReadUInt16BigEndian(flagPacket.AsSpan(0, 2)));
        Assert.Equal(0x10203040u, BinaryPrimitives.ReadUInt32BigEndian(flagPacket.AsSpan(2, 4)));
        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32BigEndian(flagPacket.AsSpan(6, 4)));
        Assert.Equal((ushort)0x5566, BinaryPrimitives.ReadUInt16BigEndian(flagPacket.AsSpan(10, 2)));
        Assert.Equal((ushort)0x1234, BinaryPrimitives.ReadUInt16BigEndian(flagPacket.AsSpan(12, 2)));
        Assert.Equal((byte)0x02, flagPacket[14]);
        Assert.Equal((byte)0x03, flagPacket[15]);
        Assert.Equal(0x05060708u, BinaryPrimitives.ReadUInt32BigEndian(flagPacket.AsSpan(16, 4)));
        Assert.Equal((byte)1, flagPacket[20]);

        // 7. Native 0x1af knockback sets flags bit 11 (0x0800), kind bits 0x0c (u3c/u3e), and clears Teleport fallback.
        var kbPlayer = new Player(new(1, "1_1_1", 1000, 1000, 100, 100, null, []), 1, 1);
        var kbTarget = kbPlayer.Area.Monsters[1] = new SpawnedMonster("kb", kbPlayer.NewMonsterWalk(1, 105, 100, 30, 1, false), 0, 0, 100);
        var kbStats = new Dictionary<uint, int> { [Stat.BaseGlobalChanceToKnockbackPct] = 100 };
        Assert.True(Game.KnockbackOnHitLocked(kbPlayer, kbTarget, kbStats, null, 0, new Random(1)));
        Assert.NotNull(kbTarget.NativeKnockbackPending);
        var kb = kbTarget.NativeKnockbackPending!.Value;
        double expectedDist = (kb.DistanceMod * 0.01 + 1.0) * 4.0 * (LivingCollision.PlayerSize + 5.0)
            / (Math.Max(0, LivingCollision.Size(kbTarget)) + 5.0);
        Assert.InRange(expectedDist, 3.9, 4.1);

        ushort hitFlags = 0x4201;
        byte hitKind = 0x03, hitAngle = 0;
        Game.ConsumeNativeKnockbackLocked(kbTarget, ref hitFlags, ref hitKind, ref hitAngle, out ushort u3c, out ushort u3e);
        Assert.True((hitFlags & Game.ActionHitKnockbackFlag) != 0);
        Assert.Equal(0x0f, hitKind);
        Assert.Null(kbTarget.NativeKnockbackPending);
        Assert.Null(kbTarget.KnockbackPending);
        var plainHit = InstanceProtocol.ActionHit(1, 1, 2, flags: 0x4201, kind: 3, code: 0, angle: hitAngle);
        var kbHit = InstanceProtocol.ActionHit(1, 1, 2, flags: hitFlags, kind: hitKind, code: 0, angle: hitAngle, u3c: u3c, u3e: u3e);
        Assert.Equal(plainHit.Length + 4, kbHit.Length);

        // Wall collision stops before unwalkable cells (no wall tunneling), and reversed knockback pulls without overlap.
        Assert.Equal((0, 6), Game.KnockbackDestination((0, 0), (0, 5), 4, (_, y) => y != 7));
        Assert.Equal((0, 2), Game.KnockbackDestination((0, 0), (0, 5), 10, (_, _) => true, reversed: true, minSeparation: 2));
        Assert.Null(Game.KnockbackDestination((0, 0), (0, 2), 4, (_, _) => true, reversed: true, minSeparation: 2));

        // Reversed knockback pulls monster toward origin; player attacker negates wire angle, totem attacker keeps direct angle.
        var revStats = new Dictionary<uint, int>
        {
            [Stat.BaseGlobalChanceToKnockbackPct] = 100,
            [Stat.EnemyKnockbackDirectionIsReversed] = 1,
        };
        var revTarget = kbPlayer.Area.Monsters[2] = new SpawnedMonster("rev", kbPlayer.NewMonsterWalk(2, 110, 100, 30, 1, false), 0, 0, 100);
        Assert.True(Game.KnockbackOnHitLocked(kbPlayer, revTarget, revStats, null, 0, new Random(1)));
        Assert.Equal((106, 100), revTarget.KnockbackPending);
        Assert.Equal(Game.HitAngle(4, 0), revTarget.NativeKnockbackPending!.Value.Angle);

        var totemRevTarget = kbPlayer.Area.Monsters[3] = new SpawnedMonster("totem_rev", kbPlayer.NewMonsterWalk(3, 110, 100, 30, 1, false), 0, 0, 100);
        Assert.True(Game.KnockbackOnHitLocked(kbPlayer, totemRevTarget, revStats, null, 0, new Random(1),
            origin: (100, 100), attackerSize: 3, attackerHasReversedStat: false));
        Assert.Equal((106, 100), totemRevTarget.KnockbackPending);
        Assert.Equal(Game.HitAngle(-4, 0), totemRevTarget.NativeKnockbackPending!.Value.Angle);

        // #1249 CannotBeKnockedBack and immobile varieties are excluded from knockback.
        var immuneTarget = new SpawnedMonster("immune", kbPlayer.NewMonsterWalk(4, 105, 100, 30, 1, false), 0, 0, 100)
        {
            Mods = MonsterModSet.None with { Effects = new(new Dictionary<uint, int> { [Stat.CannotBeKnockedBack] = 1 }) },
        };
        Assert.False(Game.KnockbackAllowed(immuneTarget));
        var immobileVariety = Game.Monsters.Find("Metadata/Monsters/Clone/MarauderCloneImmobile")!;
        var immobileTarget = new SpawnedMonster("immobile", kbPlayer.NewMonsterWalk(5, 105, 100, 30, 1, false), 0, 0, 100)
        {
            Variety = immobileVariety,
        };
        Assert.False(Game.KnockbackAllowed(immobileTarget));

        // 8. Monster helmets (slot 4), non-bow back visuals (slot 7), and MonsterArmours slots (2/5/6).
        var witch = Game.Monsters.Find("Metadata/Monsters/Exiles/ExileWitch1")!;
        Assert.Contains(witch.Weapons, w => w.Slot == 4 && w.VisualIdentity != 0);
        var skeleton = Game.Monsters.Find("Metadata/Monsters/Skeletons/SkeletonMelee14")!;
        Assert.Contains(skeleton.Weapons, w => w.Slot == 7 && w.VisualIdentity != 0);
        var banditBowBoss = Game.Monsters.Find("Metadata/Monsters/Bandits/BanditBossBow")!;
        Assert.Contains(banditBowBoss.Weapons, w => w.Slot == 2 && w.VisualIdentity != 0);
        var armouredBoss = Game.Monsters.Find("Metadata/Monsters/Bandits/MapBanditBowBossFire")!;
        Assert.Contains(armouredBoss.Weapons, w => w.Slot == 5 && w.VisualIdentity != 0);
        Assert.Contains(armouredBoss.Weapons, w => w.Slot == 6 && w.VisualIdentity != 0);

        // 9. Standard death XP penalty: 0% pre-Act 5 / HC / town / hideout / PvP, 5% Act 5 Kitava, 10% Act 10 Kitava, clamped to floor.
        uint floor = CharacterStats.LevelStarts[49], next = CharacterStats.LevelStarts[50], span = next - floor;
        var xpPlayer = new Player(new(1, "1_1_1", 300, 300, 100, 100, null, []), 1, 1)
        {
            Character =
            {
                Name = "DeathXp_" + Guid.NewGuid().ToString("N"),
                Experience = floor + span / 2,
            },
        };
        try
        {
            Assert.Equal(0, Game.DeathExperiencePenaltyPercent(xpPlayer));
            Assert.Equal((0, xpPlayer.Character.Experience, xpPlayer.Character.Experience), Game.ApplyDeathExperiencePenalty(xpPlayer));

            xpPlayer.Character.QuestFlags.Add("a5q6-CompletedBoss");
            Assert.Equal(5, Game.DeathExperiencePenaltyPercent(xpPlayer));
            uint expected5 = xpPlayer.Character.Experience - (uint)((ulong)span * 5UL / 100UL);
            var (pct5, _, after5) = Game.ApplyDeathExperiencePenalty(xpPlayer);
            Assert.Equal(5, pct5);
            Assert.Equal(expected5, after5);
            Assert.Equal(expected5, xpPlayer.Character.Experience);

            // Unearned EndGameAvailable stays 5%; earned EndGameAvailable becomes 10% and clamps to current level floor.
            xpPlayer.Character.QuestFlags.Add(Quests.EndGameAvailable);
            xpPlayer.Character.UnearnedQuestFlags.Add(Quests.EndGameAvailable);
            Assert.Equal(5, Game.DeathExperiencePenaltyPercent(xpPlayer));
            xpPlayer.Character.UnearnedQuestFlags.Remove(Quests.EndGameAvailable);
            Assert.Equal(10, Game.DeathExperiencePenaltyPercent(xpPlayer));
            xpPlayer.Character.Experience = floor + 1;
            var (pct10, _, afterFloor) = Game.ApplyDeathExperiencePenalty(xpPlayer);
            Assert.Equal(10, pct10);
            Assert.Equal(floor, afterFloor);
        }
        finally
        {
            CharacterSaves.ForgetExperience(xpPlayer.Character.Name!);
        }

        var hcPlayer = new Player(new(1, "1_1_1", 300, 300, 100, 100, null, []), 1, 1) { Character = { Hardcore = true } };
        hcPlayer.Character.QuestFlags.Add(Quests.EndGameAvailable);
        Assert.Equal(0, Game.DeathExperiencePenaltyPercent(hcPlayer));
        var pvpPlayer = new Player(new(1, "pvp1", 300, 300, 100, 100, null, []), 1, 1);
        pvpPlayer.Character.QuestFlags.Add(Quests.EndGameAvailable);
        Assert.Equal(0, Game.DeathExperiencePenaltyPercent(pvpPlayer));
        var townArea = WaypointCatalog.Areas.FindById("1_1_town")!;
        var townPlayer = new Player(new(townArea.Hash16, townArea.Id, 300, 300, 100, 100, null, []), 1, 1);
        townPlayer.Character.QuestFlags.Add(Quests.EndGameAvailable);
        Assert.Equal(0, Game.DeathExperiencePenaltyPercent(townPlayer));
        if (HideoutCatalog.Default is { } hideout)
        {
            var hideoutPlayer = new Player(new(hideout.AreaHash16, hideout.AreaId, 300, 300, 100, 100, null, []), 1, 1);
            hideoutPlayer.Character.QuestFlags.Add(Quests.EndGameAvailable);
            Assert.Equal(0, Game.DeathExperiencePenaltyPercent(hideoutPlayer));
        }

        // 10. Chance-based instant cooldown reset sync (0x50cd / 0x52e2) keeps uses unspent and emits 0x02bc refresh;
        //     Guard skills are excluded while held, and Dash supports instant reset via TryCommitDashUse.
        using var f = new MinionUtilityTests.Fixture();
        f.Player.Use(new(AreaLayouts.TheCoast, "1_1_2", 1000, 1000, 100, 100, (_, _) => true, []) { Sight = (_, _) => 2 });
        f.Player.Walk.Teleport(100, 100, 0);
        f.Player.God = true;
        var equipped = f.Equip(("Flame Dash", 20), ("Steelskin", 20), ("Dash", 20));
        ushort skillId = equipped[0], guardSkillId = equipped[1], dashSkillId = equipped[2];
        var gem = Game.GemSkillEffects(f.Player).Single(g => g.Id == skillId);
        var guardGem = Game.GemSkillEffects(f.Player).Single(g => g.Id == guardSkillId);
        var dashGem = Game.GemSkillEffects(f.Player).Single(g => g.Id == dashSkillId);
        f.Player.ClientSkillIds.Add(skillId);
        f.Player.ClientSkillIds.Add(guardSkillId);
        f.Player.ClientSkillIds.Add(dashSkillId);
        int refreshChance = 100, retaliationChance = 0;
        using var cdStatHook = GameEvents.PlayerStatSources.Add("Game", (p, s) =>
        {
            if (!ReferenceEquals(p, f.Player)) return s;
            if (refreshChance != 0) s[Game.BaseCooldownRefreshChanceStat] = s.GetValueOrDefault(Game.BaseCooldownRefreshChanceStat) + refreshChance;
            if (retaliationChance != 0) s[Game.RetaliationPreventCooldownStat] = s.GetValueOrDefault(Game.RetaliationPreventCooldownStat) + retaliationChance;
            return s;
        });

        Assert.True(Game.TryBeginSkillCooldownUse(f.Player, skillId, gem.Effect, Game.CooldownForSkill(f.Player, skillId),
            false, () => { }, out var ticket));
        Assert.NotNull(ticket);
        Assert.True(ticket.CooldownReset);
        var cdState = Game.SingleUseState(f.Player, gem.Effect, skillId);
        Assert.NotNull(cdState);
        Assert.Equal(0, cdState.SpentUses);
        Assert.True(ticket.CommitNativeUse(f.Player, skillId));
        Assert.True(cdState.NeedsNativeRefresh);
        // SynchronizeNativeReset updates TotalBaseMilliseconds even when already non-zero.
        cdState.SynchronizeNativeReset(skillId, 4500);
        Assert.Equal(4500, cdState.TotalBaseMilliseconds);
        var refreshPacket = Game.NativeCooldownRefresh(f.Player, f.Player.ProjectileEpoch, () => [(skillId, gem.Effect)]);
        Assert.NotNull(refreshPacket);
        Assert.Equal(0x02bc, BinaryPrimitives.ReadUInt16BigEndian(refreshPacket.AsSpan(0, 2)));
        Assert.False(cdState.NeedsNativeRefresh);

        // Guard skill is excluded from instant cooldown reset to keep Hold/Resume synchronized.
        Assert.False(Game.CooldownResetTriggered(f.Player, guardGem.Effect, guardSkillId, new Random(1)));
        Assert.True(Game.TryBeginSkillCooldownUse(f.Player, guardSkillId, guardGem.Effect,
            Game.CooldownForSkill(f.Player, guardSkillId), true, () => { }, out var guardTicket));
        Assert.NotNull(guardTicket);
        Assert.False(guardTicket.CooldownReset);

        // Dash supports cooldown reset via TryCommitDashUse + ConfirmNativeUse.
        Assert.True(Game.TryCommitDashUse(f.Player, dashSkillId, dashGem.Effect, (115, 100), out var dashReceipt));
        Assert.NotNull(dashReceipt);
        Assert.True(dashReceipt.CooldownReset);
        var dashState = Game.SingleUseState(f.Player, dashGem.Effect, dashSkillId);
        Assert.NotNull(dashState);
        Assert.Equal(0, dashState.SpentUses);
        Assert.True(dashReceipt.ConfirmNativeUse(f.Player));
        Assert.True(dashState.NeedsNativeRefresh);
        var dashRefresh = Game.NativeCooldownRefresh(f.Player, f.Player.ProjectileEpoch, () => [(dashSkillId, dashGem.Effect)]);
        Assert.NotNull(dashRefresh);
        Assert.Equal(0x02bc, BinaryPrimitives.ReadUInt16BigEndian(dashRefresh.AsSpan(0, 2)));

        // 0x50cd applies only to Retaliation skills.
        refreshChance = 0;
        retaliationChance = 100;
        Assert.False(Game.CooldownResetTriggered(f.Player, gem.Effect, skillId, new Random(1)));
        const int crushingFistEffect = 12477;
        Assert.True(Program.Skills.IsRetaliation(crushingFistEffect));
        Assert.True(Game.CooldownResetTriggered(f.Player, crushingFistEffect, null, new Random(1)));
    }
}
