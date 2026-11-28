using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Modules.Game;

namespace GameServer.Gateway.Tests;

public sealed class LuminaryAscendancyTests
{
    static async Task WithLuminaryTestPlayer(Func<Player, Task> body, bool hardMode = false)
    {
        string path = Path.Combine(Path.GetTempPath(), $"luminary-test-{Guid.NewGuid():N}.db");
        var oldLogic = Program.GameLogicOrNull;
        var oldLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Saves.Open(path);
        Program.SetGameLogic(new GameLogic(lifetime));
        var league = new League(hardMode ? "Ruthless" : "Standard", hardMode ? "Ruthless" : "Standard", false, false)
        {
            DataSet = hardMode ? "HardMode" : null,
        };
        var layout = new AreaLayout(1, "1_1_2", 500, 500, 100, 100, null, []);
        var instance = new InstanceState();
        await using var wire = await ScarabEncounterTests.Wire.Open(instance, layout, 1);
        var player = wire.Player;
        player.Character.Name = "Lum" + Guid.NewGuid().ToString("N")[..8];
        player.Character.ClassRow = 2; // Scion
        player.Character.Experience = CharacterStats.LevelStarts[85];
        player.Character.League = league.Name;
        player.PlayerLife = 2000;
        player.PlayerMana = 1000;
        player.Pools = new(85, 2000, 1000, 500, 0, Strength: 300, Dexterity: 300, Intelligence: 300);
        player.LeagueModule = new(league, new HashSet<string>(["Game"]));
        player.Clock.Stop();
        using var waived = TestRequirements.Waive(player);
        try
        {
            await body(player);
        }
        finally
        {
            PlayerInventories.Forget(player.Character.Name!);
            lifetime.Stop(_ => { });
            GameEventOwners.Release("luminary-test", GameEventOwners.ModuleRank);
            Game.CombatLifetime = oldLifetime;
            Program.SetGameLogic(oldLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }

    static SpawnedMonster MakeCompanion(Player player, uint entityId, uint life, uint maxLife, bool isPermanent,
        out ActiveCompanion companion)
    {
        var monster = new SpawnedMonster("Mercenary", new PlayerWalk(105, 100, 35), aggroRange: 100, stopRange: 10, maxLife: maxLife)
        {
            ObjectId = entityId,
            Level = 80,
            Life = life,
            MaxLife = maxLife,
            Hostile = false,
            AlliedFighter = true,
            IsAlliedCompanion = true,
            IsPermanentCompanion = isPermanent,
        };
        player.Area.Monsters[entityId] = monster;
        companion = new ActiveCompanion(entityId, monster, isPermanent);
        player.Companion = companion;
        return monster;
    }

    static void AllocateLuminary(Player player, params string[] nodeIds)
    {
        var tree = Game.PassivesFor(player);
        player.Character.Passives.Clear();
        player.Character.Passives.Add(tree.FindById("AscendancyLuminaryStart")!.GraphId);
        foreach (string id in nodeIds)
            player.Character.Passives.Add(tree.FindById(id)!.GraphId);
        player.Character.StatsChanged?.Invoke(StatInput.Passives);
    }

    [Fact]
    public async Task All_21_Luminary_nodes_Normal_and_HardMode_values_and_stat_event_parity_match_spec()
    {
        // Verify all 21 real AscendancyLuminary* rows exist in Program.Passives (excluding [DNT] AscendancyLuminary8).
        string[] allReal =
        [
            "AscendancyLuminaryStart",
            "AscendancyLuminary1", "AscendancyLuminary2", "AscendancyLuminary3", "AscendancyLuminary4",
            "AscendancyLuminary5", "AscendancyLuminary6", "AscendancyLuminary7",
            "AscendancyLuminary9_", "AscendancyLuminary10", "AscendancyLuminary11", "AscendancyLuminary12",
            "AscendancyLuminary13", "AscendancyLuminary14", "AscendancyLuminary15", "AscendancyLuminary16",
            "AscendancyLuminary17", "AscendancyLuminary18", "AscendancyLuminary19__", "AscendancyLuminary20",
            "AscendancyLuminary21", "AscendancyLuminary22",
        ];
        foreach (string id in allReal)
            Assert.NotNull(Program.Passives.FindById(id));

        // Normal mode: allocate every active graph node and verify exact bonus projection.
        await WithLuminaryTestPlayer(player =>
        {
            AllocateLuminary(player,
                "AscendancyLuminary5", "AscendancyLuminary9_",
                "AscendancyLuminary4", "AscendancyLuminary10", "AscendancyLuminary2", "AscendancyLuminary11",
                "AscendancyLuminary3", "AscendancyLuminary12", "AscendancyLuminary1", "AscendancyLuminary13",
                "AscendancyLuminary17", "AscendancyLuminary18", "AscendancyLuminary19__",
                "AscendancyLuminary20", "AscendancyLuminary21", "AscendancyLuminary22", "AscendancyLuminary14");

            var b = Game.LuminaryBonusesOf(player);
            Assert.True(b.CanHavePermanentFollower);
            Assert.Equal(-30, b.DamageMoreFromLuminaryPct);
            Assert.Equal(8, b.DamageMorePerUniquePct);
            Assert.Equal(15, b.DamageIncreasedPct);
            Assert.Equal(30, b.MaxLifeIncreasedPct); // Luminary1 + Luminary3 = 15 + 15
            Assert.Equal(50, b.NonCurseAuraEffectPct);
            Assert.Equal(100, b.TauntOnHitPct);
            Assert.Equal(40, b.RecoupIfLifeLowerPct);
            Assert.Equal(20, b.DivertIfLifeHigherPct);
            Assert.True(b.NoDeathOnLinkedMercenaryDeath);
            Assert.True(b.InfiniteLinkDuration);
            Assert.Equal(-50, b.LinkCostMorePct);
            Assert.True(b.GoldenGlory);
            Assert.True(b.CanUseUniqueInSlot(PlayerInventories.Ring1));
            Assert.True(b.CanUseUniqueInSlot(PlayerInventories.Ring2));
            Assert.True(b.CanUseUniqueInSlot(PlayerInventories.Amulet));
            Assert.True(b.CanUseUniqueInSlot(PlayerInventories.Belt));
            Assert.True(b.CanUseUniqueInSlot(PlayerInventories.Helmet));
            Assert.True(b.CanUseUniqueInSlot(PlayerInventories.Boots));
            Assert.True(b.CanUseUniqueInSlot(PlayerInventories.Gloves));
            Assert.True(b.CanUseUniqueInSlot(PlayerInventories.Weapon1));
            Assert.True(b.CanUseUniqueInSlot(PlayerInventories.Offhand1));
            // Hardcoded retail 3.29 block: permanent mercenaries can NEVER equip Unique Body Armour.
            Assert.False(b.CanUseUniqueInSlot(PlayerInventories.BodyArmour));

            // Multiplicative Noble Blood (-30% less) * Legendary Arms (+8% more per unique).
            Assert.Equal(0.7, b.MercenaryDamageMoreFactor(0), 6);
            Assert.Equal(0.7 * 1.40, b.MercenaryDamageMoreFactor(5), 6);

            // Mirror stats emitted for permanent mercenary, empty for temporary mercenary.
            var permStats = Game.LuminaryCompanionStats(player, equippedUniqueCount: 3, isPermanent: true)
                .ToDictionary(x => x.Key, x => x.Value);
            Assert.Equal(1, permStats[Stat.IsPermanentMercenary]);
            Assert.Equal(-30, permStats[Stat.DamagePlusPctFinalFromLuminary]);
            Assert.Equal(-30, permStats[Stat.MinionDamagePlusPctFinalFromLuminary]);
            Assert.Equal(24, permStats[Stat.DamagePlusPctFinalPerEquippedUnique]);
            Assert.Equal(24, permStats[Stat.MinionDamagePlusPctFinalPerEquippedUnique]);
            Assert.Equal(15, permStats[Stat.DamagePlusPctAndMinionDamagePlusPct]);
            Assert.Equal(30, permStats[Stat.MaximumLifePlusPctAndMinionMaximumLifePlusPct]);
            Assert.Equal(40, permStats[Stat.DamageTakenRecoupedAsLifePctIfLifeLowerThanParentsLife]);
            Assert.Equal(1, permStats[Stat.ObjectsLinkedToYouDoNotDieWhenYouDie]);
            Assert.Equal(1, permStats[Stat.MercenaryCanUseUniqueRing]);
            Assert.Equal(1, permStats[Stat.MercenaryCanUseUniqueWeaponsShieldsAndQuivers]);
            Assert.Empty(Game.LuminaryCompanionStats(player, equippedUniqueCount: 3, isPermanent: false));
            return Task.CompletedTask;
        });

        // HardMode (Ruthless): reduced values on Loyal Bodyguard (15/30), Bestowed Knighthood (30/100), Legendary Arms (4),
        // and Oath of Fealty omits link_skill_cost_+%_final.
        await WithLuminaryTestPlayer(player =>
        {
            AllocateLuminary(player, "AscendancyLuminary5", "AscendancyLuminary9_",
                "AscendancyLuminary10", "AscendancyLuminary12", "AscendancyLuminary13", "AscendancyLuminary14");
            var b = Game.LuminaryBonusesOf(player);
            Assert.Equal(0, b.LinkCostMorePct);
            Assert.True(b.InfiniteLinkDuration);
            Assert.True(b.NoDeathOnLinkedMercenaryDeath);
            Assert.Equal(15, b.DivertIfLifeHigherPct);
            Assert.Equal(30, b.RecoupIfLifeLowerPct);
            Assert.Equal(30, b.NonCurseAuraEffectPct);
            Assert.Equal(100, b.TauntOnHitPct);
            Assert.Equal(4, b.DamageMorePerUniquePct);
            return Task.CompletedTask;
        }, hardMode: true);

        // Parity check: stats injected via GameEvents.Stats vs GameEvents.PlayerStatSources both resolve identically.
        await WithLuminaryTestPlayer(player =>
        {
            using (GameEvents.Stats.Add("Game", sheet =>
            {
                if (!ReferenceEquals(sheet.Player, player)) return;
                sheet.Add(Stat.CanHaveActiveMercenaryFollower, 1);
                sheet.Add(Stat.PermanenetMercenaryNonCurseAuraEffectPlusPct, 50);
            }))
            {
                var viaStats = Game.LuminaryBonusesOf(player);
                Assert.True(viaStats.CanHavePermanentFollower);
                Assert.Equal(50, viaStats.NonCurseAuraEffectPct);
            }
            using (GameEvents.PlayerStatSources.Add("Game", (p, sources) =>
            {
                if (!ReferenceEquals(p, player)) return sources;
                var next = new Dictionary<uint, int>(sources);
                next[Stat.CanHaveActiveMercenaryFollower] = next.GetValueOrDefault(Stat.CanHaveActiveMercenaryFollower) + 1;
                next[Stat.PermanenetMercenaryNonCurseAuraEffectPlusPct] = next.GetValueOrDefault(Stat.PermanenetMercenaryNonCurseAuraEffectPlusPct) + 50;
                return next;
            }))
            {
                var viaSources = Game.LuminaryBonusesOf(player);
                Assert.True(viaSources.CanHavePermanentFollower);
                Assert.Equal(50, viaSources.NonCurseAuraEffectPct);
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Oath_of_Fealty_Golden_Glory_Loyal_Bodyguard_and_Bestowed_Knighthood_mechanics_work()
    {
        await WithLuminaryTestPlayer(async player =>
        {
            // ---- 1. Oath of Fealty: -50% link cost, infinite duration, companion targetable without link_skills_can_target_minions,
            //         permanent merc death does NOT kill player while temporary merc death DOES kill player ----
            int reduced = Game.EffectiveLinkCostPerMinute(600,
                k => k == Stat.LinkSkillCostPlusPctFinal ? -50 : 0, _ => 0);
            Assert.Equal(300, reduced);

            AllocateLuminary(player, "AscendancyLuminary5", "AscendancyLuminary9_",
                "AscendancyLuminary4", "AscendancyLuminary10", "AscendancyLuminary2", "AscendancyLuminary11",
                "AscendancyLuminary3", "AscendancyLuminary12", "AscendancyLuminary1", "AscendancyLuminary13");

            // Give player Flame Link gem in body armour so StartLink finds it.
            var inv = PlayerInventories.For(player.Character.Name!, Program.Items);
            var flameLink = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemFlameLink")!)
                with { Identified = true };
            var body = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Armours/BodyArmours/BodyStr1")!)
                with { Identified = true, Sockets = [new ItemSocket(SocketColour.Red, flameLink)] };
            inv.Add(PlayerInventories.BodyArmour, 0, 0, body);
            var linkGrant = Assert.Single(Game.GemSkillEffects(player), g => Game.SkillsFor(player).EffectId(g.Effect) == "FlameLink");

            // Spawn a permanent companion and link it (no link_skills_can_target_minions!).
            var permMonster = MakeCompanion(player, 0x80000101u, life: 3000, maxLife: 3000, isPermanent: true, out var permComp);
            Assert.True(await Game.StartLink(player, linkGrant.Id, permComp.EntityId));
            var activeLink = Game.ActiveCompanionLink(player, permComp.EntityId, now: 1.0);
            Assert.NotNull(activeLink);
            Assert.True(double.IsPositiveInfinity(activeLink!.Until));

            // ---- 2. Golden Glory: positive and negative Light Radius modify Link buff effect on permanent mercenary only ----
            var baseStats = Game.PlayerStatValues(player);
            // 5 small nodes (Luminary 1, 2, 3, 4, 5 = 5 * +5% = +25% light radius)
            long baseEffect = Game.LinkBuffEffectPercentForCompanion(player, permComp, baseStats);
            Assert.Equal(125, baseEffect);
            var boostedStats = new Dictionary<uint, int>(baseStats) { [Stat.LightRadiusPlusPct] = 60 };
            Assert.Equal(160, Game.LinkBuffEffectPercentForCompanion(player, permComp, boostedStats));
            var reducedStats = new Dictionary<uint, int>(baseStats) { [Stat.LightRadiusPlusPct] = -40 };
            Assert.Equal(60, Game.LinkBuffEffectPercentForCompanion(player, permComp, reducedStats));
            var baseOffence = Game.CompanionLinkOffenceAt(player, permComp, baseStats, now: 1.0);
            var boostedOffence = Game.CompanionLinkOffenceAt(player, permComp, boostedStats, now: 1.0);
            Assert.True(boostedOffence.FireMax > baseOffence.FireMax);

            // ---- 3. Loyal Bodyguard: diversion when merc.Life > player.PlayerLife; recoup when merc.Life < player.PlayerLife ----
            int divertedTaken = 0;
            var bodyguardComp = new ActiveCompanion(permComp.EntityId, permMonster, isPermanent: true)
            {
                ApplyDamageLocked = (_, dmg, _) =>
                {
                    divertedTaken += dmg;
                    permMonster.Life = (uint)Math.Max(0, (long)permMonster.Life - dmg);
                    return dmg;
                },
            };
            player.Companion = bodyguardComp;
            player.PlayerLife = 2000;
            permMonster.Life = 3000; // > 2000 -> 20% diverted
            int diverted = Game.CompanionTakeOwnerHitLocked(player, 500, now: 1.0, baseStats);
            Assert.Equal(100, diverted);
            Assert.Equal(100, divertedTaken);

            permMonster.Life = 2000; // == 2000 -> NO diversion
            Assert.Equal(0, Game.CompanionTakeOwnerHitLocked(player, 500, now: 1.0, baseStats));

            var recoup = new RecoupRecovery();
            uint curPlayerLife = player.PlayerLife;
            permMonster.Life = curPlayerLife; // == player life -> NO recoup
            Assert.Equal(0, Game.CompanionRecoupHitLocked(player, permMonster, isPermanent: true, 400, recoup, now: 1.0));
            permMonster.Life = curPlayerLife / 2; // strictly < player life -> 40% recouped over 4s = 160
            double recouped = Game.CompanionRecoupHitLocked(player, permMonster, isPermanent: true, 400, recoup, now: 1.0);
            Assert.Equal(160.0, recouped, 4);
            Assert.Equal(0, Game.CompanionRecoupHitLocked(player, permMonster, isPermanent: false, 400, recoup, now: 1.0)); // temp merc gets none

            // ---- 4. Bestowed Knighthood: +50% non-curse aura scaling & 100% taunt on hit ----
            var grace = new PlayerMinions.Aura("Grace", 10, 40, [(Stat.BaseEvasionRating, 1000)]);
            var scaledAura = Game.ScaleCompanionNonCurseAura(grace, 50);
            Assert.Equal(1500, scaledAura.Stats[0].Value);

            var hostile = new SpawnedMonster("Goblin", new PlayerWalk(106, 100, 20), aggroRange: 60, stopRange: 8, maxLife: 5000)
            {
                ObjectId = 0x80000201u, Level = 70, Life = 5000, MaxLife = 5000, Hostile = true,
            };
            player.Area.Monsters[hostile.ObjectId] = hostile;
            Assert.True(Game.TryApplyCompanionTauntLocked(player, bodyguardComp, hostile, now: 1.0));
            Assert.Equal(0.9, Game.CompanionTauntedHitAgainstOtherFactor(player, hostile, now: 1.5), 6);
            Game.ClearCompanionTauntsLocked(player, bodyguardComp.EntityId);
            Assert.Equal(1.0, Game.CompanionTauntedHitAgainstOtherFactor(player, hostile, now: 1.5), 6);

            // ---- 5. Oath of Fealty death check: permanent merc death ends link without killing player ----
            permMonster.Life = 0;
            permMonster.Dead = true;
            await Game.LinkSkillsTick(player, now: 2.0);
            Assert.True(player.PlayerLife > 0);
            Assert.Equal(0, player.Links.Count);

            // Temporary mercenary: Golden Glory does NOT apply, and its death DOES kill the linked player!
            var tempMonster = MakeCompanion(player, 0x80000102u, life: 3000, maxLife: 3000, isPermanent: false, out var tempComp);
            Assert.Equal(100, Game.LinkBuffEffectPercentForCompanion(player, tempComp, boostedStats));
            Assert.True(await Game.StartLink(player, linkGrant.Id, tempComp.EntityId));
            tempMonster.Life = 0;
            tempMonster.Dead = true;
            await Game.LinkSkillsTick(player, now: 3.0);
            Assert.Equal(0u, player.PlayerLife);
        });
    }

    [Fact]
    public async Task Bestowed_Knighthood_adds_linearly_with_companion_own_aura_effect_and_Generosity()
    {
        await WithLuminaryTestPlayer(player =>
        {
            player.Walk.Teleport(100, 100, 0);
            player.Frames.World(player);

            // Allocate Noble Blood + Bestowed Knighthood (+50% permanent mercenary non-curse aura effect).
            AllocateLuminary(player, "AscendancyLuminary5", "AscendancyLuminary9_",
                "AscendancyLuminary1", "AscendancyLuminary13");
            Assert.Equal(50, Game.LuminaryBonusesOf(player).NonCurseAuraEffectPct);

            // Place an allied recipient entity (0x20000001 = player-range ID) and companion in the area spatial grid.
            const uint allyPlayerId = 0x20000001u;
            player.Area.Zones.Grid.Set(new(allyPlayerId), (100, 100));

            // Permanent companion emitting Grace (1000 base evasion) with +40% own aura effect (e.g. Generosity):
            // Must add linearly: +40% + +50% = +90% -> 1900 (NOT multiplicative 1.4 * 1.5 = 2.1 -> 2100!).
            MakeCompanion(player, 0x80000301u, life: 3000, maxLife: 3000, isPermanent: true, out var permComp);
            player.Area.Zones.Grid.Set(new(permComp.EntityId), (105, 100));
            ushort graceRow = AuraCatalog.BuffRow("Grace");
            var generosityGrace = new PlayerMinions.Aura("Grace", graceRow, 60, [(Stat.BaseEvasionRating, 1000)])
            {
                SharesWithAllies = true,
                AffectsSource = false,
                OwnAuraEffectPct = 40,
            };
            Game.SyncCompanionSharedAuras(player, permComp, [generosityGrace], now: 1.0, scaleByLuminary: true);
            player.Area.Zones.Update(moved: true);

            var permBuff = player.Area.Buffs.Get(allyPlayerId, graceRow, permComp.EntityId);
            Assert.NotNull(permBuff);
            Assert.NotNull(permBuff!.Values);
            Assert.Equal(1900u, permBuff.Values![0]);

            // Temporary companion with +40% own aura effect gets ONLY its own +40% (1400), NOT Bestowed Knighthood.
            Game.ClearCompanionSharedAuras(player, permComp.EntityId);
            MakeCompanion(player, 0x80000302u, life: 3000, maxLife: 3000, isPermanent: false, out var tempComp);
            player.Area.Zones.Grid.Set(new(tempComp.EntityId), (105, 100));
            Game.SyncCompanionSharedAuras(player, tempComp, [generosityGrace], now: 2.0, scaleByLuminary: true);
            player.Area.Zones.Update(moved: true);

            var tempBuff = player.Area.Buffs.Get(allyPlayerId, graceRow, tempComp.EntityId);
            Assert.NotNull(tempBuff);
            Assert.NotNull(tempBuff!.Values);
            Assert.Equal(1400u, tempBuff.Values![0]);
            return Task.CompletedTask;
        });
    }
}
