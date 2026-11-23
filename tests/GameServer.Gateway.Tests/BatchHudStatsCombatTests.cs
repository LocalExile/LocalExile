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

            // 3. HUD minion counter icons (PlayerMinionsChange) for all active player minion types, including Spectre row 0.
            SpawnedMonster LivingMinion(string label) => new(label, new PlayerWalk(100, 100, 30), 10, 1, 100) { Hostile = false };
            GolemCatalog.Definition Def(string id, string kind, bool isGolem = false) =>
                new(0, id, kind, "", 0, [], false, false, IsGolem: isGolem);

            player.Summons.Fighters[201] = new(201, LivingMinion("spectre"), Def("RaiseSpectre", "Spectre"), 0x1001);
            player.Summons.Fighters[202] = new(202, LivingMinion("zombie"), Def("RaiseZombie", "Zombie"), 0x1002);
            player.Summons.Fighters[203] = new(203, LivingMinion("skeleton"), Def("SummonSkeletons", "Skeleton"), 0x1003);
            player.Summons.Fighters[204] = new(204, LivingMinion("srs"), Def("SummonRagingSpirit", "RagingSpirit"), 0x1004);
            player.Golems.All[205] = new(205, LivingMinion("golem"), Def("SummonFlameGolem", "Fire", isGolem: true), 0x1005);
            player.Reservations.Fighters[206] = new(206, LivingMinion("agony"), Def("HeraldOfAgony", "Agony"), 0x1006);

            var rosterPacket = Game.SentinelOwnershipPresentation(player);
            Assert.NotNull(rosterPacket);
            Assert.Equal(0x02bc, BinaryPrimitives.ReadUInt16BigEndian(rosterPacket.AsSpan(0, 2)));
            Assert.Equal((ushort)0, player.Summons.PresentedOwnership[201].Type);
            Assert.Equal((ushort)7, player.Summons.PresentedOwnership[202].Type);
            Assert.Equal((ushort)9, player.Summons.PresentedOwnership[203].Type);
            Assert.Equal((ushort)20, player.Summons.PresentedOwnership[204].Type);
            Assert.Equal((ushort)24, player.Summons.PresentedOwnership[205].Type);
            Assert.Equal((ushort)39, player.Summons.PresentedOwnership[206].Type);
            Assert.Null(Game.SentinelOwnershipPresentation(player)); // No changes -> no duplicate packet.

            player.Summons.Fighters.Clear();
            player.Golems.All.Clear();
            player.Reservations.Fighters.Clear();
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

        // 9. Standard death XP penalty: 0% pre-Act 5 / HC / PvP, 5% Act 5 Kitava, 10% Act 10 Kitava, clamped to floor.
        uint floor = CharacterStats.LevelStarts[49], next = CharacterStats.LevelStarts[50], span = next - floor;
        var xpPlayer = new Player(new(1, "1_1_1", 300, 300, 100, 100, null, []), 1, 1)
        {
            Character =
            {
                Name = "DeathXp_" + Guid.NewGuid().ToString("N"),
                Experience = floor + span / 2,
            },
        };
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

        var hcPlayer = new Player(new(1, "1_1_1", 300, 300, 100, 100, null, []), 1, 1) { Character = { Hardcore = true } };
        hcPlayer.Character.QuestFlags.Add(Quests.EndGameAvailable);
        Assert.Equal(0, Game.DeathExperiencePenaltyPercent(hcPlayer));
        var pvpPlayer = new Player(new(1, "pvp1", 300, 300, 100, 100, null, []), 1, 1);
        pvpPlayer.Character.QuestFlags.Add(Quests.EndGameAvailable);
        Assert.Equal(0, Game.DeathExperiencePenaltyPercent(pvpPlayer));

        // 10. Chance-based instant cooldown reset sync (0x50cd / 0x52e2) keeps uses unspent and emits 0x02bc refresh.
        using var f = new MinionUtilityTests.Fixture();
        ushort skillId = f.Equip(("Flame Dash", 20)).Single();
        var gem = Game.GemSkillEffects(f.Player).Single(g => g.Id == skillId);
        f.Player.ClientSkillIds.Add(skillId);
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
        var refreshPacket = Game.NativeCooldownRefresh(f.Player, f.Player.ProjectileEpoch, () => [(skillId, gem.Effect)]);
        Assert.NotNull(refreshPacket);
        Assert.Equal(0x02bc, BinaryPrimitives.ReadUInt16BigEndian(refreshPacket.AsSpan(0, 2)));
        Assert.False(cdState.NeedsNativeRefresh);

        // 0x50cd applies only to Retaliation skills.
        refreshChance = 0;
        retaliationChance = 100;
        Assert.False(Game.CooldownResetTriggered(f.Player, gem.Effect, skillId, new Random(1)));
        const int crushingFistEffect = 12477;
        Assert.True(Program.Skills.IsRetaliation(crushingFistEffect));
        Assert.True(Game.CooldownResetTriggered(f.Player, crushingFistEffect, null, new Random(1)));
    }
}
