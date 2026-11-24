using GameServer.Modding;

namespace GameServer.Gateway.Tests;

/// <summary>
/// A boss with no encounter controller of its own fights with the whole list of granted effects the tables give it
/// (BossKits), instead of walking up and swinging one default attack forever.
/// </summary>
public sealed class BossKitsTests
{
    [Theory]
    [InlineData("OriathanCitizenZombieMale1", 5196)]
    [InlineData("OriathanCitizenZombieMale4Boss", 5197)]
    public void The_living_Strand_zombies_cannot_select_their_death_explosion(string id, int explosion)
    {
        InGame(player =>
        {
            var variety = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/OriathanCitizen/" + id));
            var catalog = Game.SkillsFor(player);
            Assert.Equal(1995, variety.AttackMs);
            Assert.Equal(406, Game.MavenNucleus.CombatEffect(catalog, variety));
            Assert.DoesNotContain(explosion, BossKits.Effects(catalog, variety));
            Assert.Equal(-1, Game.MavenNucleus.CombatEffect(catalog, variety with { GrantedEffects = [explosion] }));
        });
    }

    [Fact]
    public void A_campaign_boss_has_its_whole_kit_and_a_plain_monster_has_none()
    {
        InGame(player =>
        {
            // Brutus, Lord Incarcerator (The Upper Prison): a bar of his own, six effects, three the client plays as a
            // hit of his own, then his Spiked Ground, a ground the server lays (shaped by its skill types). The other two
            // are shapes and triggers that answer to objects the server never creates, and they are left out: a swing
            // that hits for nothing only makes him stand still.
            var brutus = Game.Monsters.Find("Metadata/Monsters/Brute/BossBrute");
            Assert.NotNull(brutus);
            Assert.True(brutus.BossHealthBar);
            Assert.True(BossKits.IsCampaignBoss(brutus));
            int defaultEffect = brutus.GrantedEffects![0];
            var kit = BossKits.Kit(player, brutus, 9, defaultEffect);
            Assert.Equal([2395, 4003, 491, 9282, 3971], kit.Select(k => k.Effect));
            // The effect his default attack already used stays first: a kit adds the rest of the list, it does not
            // replace the attack he had (Dominus' LightningSlam is live-verified).
            Assert.Equal(defaultEffect, kit[0].Effect);
            ushort[] ids = [.. kit.Select(k => k.Attack.SkillId)];
            ushort[] slots = [0x4000, 0x4001, 0x4002, 0x4003, 0x4004];
            Assert.Equal<ushort>(slots, ids);
            Assert.Equal(kit.Count, kit.Select(k => k.Effect).Distinct().Count());
            // Each of them reaches the actor under the id the order will name: what 0x1ab can point at.
            ushort[] granted = [.. BossKits.SkillsOf(player, brutus, 9, kit).Select(s => s.Id)];
            Assert.Equal<ushort>(ids, granted);
            // The specials the tables give a cooldown keep it: the hook and the ground slam before the swings.
            Assert.Equal([6d, 6d], kit.Take(2).Select(k => k.CooldownSeconds));
            Assert.All(kit.Skip(2).SkipLast(1), k => Assert.Equal(0, k.CooldownSeconds));

            // Fire Fury (The Coast) has no life bar and is still one of the campaign's bosses: the small ones are the
            // ones that walked up and swung one attack for the whole game.
            var fury = Game.Monsters.Find("Metadata/Monsters/Cannibal/CannibalFemaleBossWitch");
            Assert.NotNull(fury);
            Assert.False(fury.BossHealthBar);
            Assert.True(BossKits.IsCampaignBoss(fury));
            Assert.True(BossKits.Kit(player, fury, 2).Count > 1);

            // A plain monster: no list of its own, and not a boss of any area.
            var corpse = Game.Monsters.Find("Metadata/Monsters/Zombies/ZombieBite");
            Assert.NotNull(corpse);
            Assert.False(BossKits.IsCampaignBoss(corpse));
            Assert.Empty(BossKits.Kit(player, corpse, 1, 406));

            // Arakaali's arena-laser activators need the encounter's turret objects. Generic AI has none; calling
            // the activator with an empty turret list crashes the native client, even while the boss is at full life.
            var arakaali = Assert.IsType<MonsterCatalog.Variety>(
                Game.Monsters.Find("Metadata/Monsters/VaalSpiderGod/Arakaali"));
            var catalog = Game.SkillsFor(player);
            int[] laserDrivers = [6879, 7802, 7805];
            Assert.All(laserDrivers, effect => Assert.Contains(effect, arakaali.GrantedEffects!));
            var arakaaliKit = BossKits.Kit(player, arakaali, 55, Game.MavenNucleus.CombatEffect(catalog, arakaali));
            Assert.DoesNotContain(arakaaliKit, entry => laserDrivers.Contains(entry.Effect));
            // Standalone mortar and melee attacks remain playable; this does not disable the encounter or its actor.
            Assert.Contains(arakaaliKit, entry => entry.Effect == 4438);
            Assert.Contains(arakaaliKit, entry => entry.Effect == 208);
            var arakaaliSkills = BossKits.SkillsOf(player, arakaali, 55, arakaaliKit);
            uint artKey = Stat.SkillArtVariation;
            foreach (var (effect, art) in new[] { (4259, 1), (4260, 2), (4261, 3) })
            {
                int slot = arakaaliKit.ToList().FindIndex(entry => entry.Effect == effect);
                Assert.Contains((artKey, art), arakaaliSkills[slot].Stats!);
            }

            CheckPietyBlood(player);

            // Yugul starts natively untargetable. He must finish his entrance before generic AI can attack; a rebind
            // already recreates a risen monster in state 2, so first entry must reach the same targetable state.
            var yugul = Assert.IsType<MonsterCatalog.Variety>(
                Game.Monsters.Find("Metadata/Monsters/Frog/FrogGod/SilverFrog"));
            var emerge = new MonsterEmerge();
            var kind = emerge.Kind(yugul, GameServer.Core.Protocol.Instance.MonsterRarity.Unique);
            Assert.Equal(EmergeKind.Buried, kind);
            Assert.Equal(EmergeKind.Buried, emerge.Kind(
                Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/Goatman/GoatmanLeapSlamNavaliBoss")),
                GameServer.Core.Protocol.Instance.MonsterRarity.Unique));
            Assert.Equal(EmergeKind.None, emerge.Kind(
                Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find(IzaroGoddess.Variety)),
                GameServer.Core.Protocol.Instance.MonsterRarity.Unique));
            Assert.Equal(EmergeKind.None, emerge.Kind(brutus,
                GameServer.Core.Protocol.Instance.MonsterRarity.Unique));
            var waiting = new SpawnedMonster("Yugul", new(20, 20, 1), 60, 2, maxLife: 100) { Variety = yugul };
            Assert.True(MonsterEmerge.AddLocked(player, 0x3050, waiting, kind, unrisen: true));
            Assert.DoesNotContain(0x3050u, player.Area.Monsters.Keys);
            Assert.Equal(1, MonsterEmerge.CreateState(yugul, kind, unrisen: true));
            var risen = Assert.Single(emerge.RiseLocked(player, (20, 20), 10, out bool watching));
            Assert.Same(waiting, risen.Monster);
            Assert.False(watching);
            Assert.Same(waiting, player.Area.Monsters[0x3050]);
            Assert.True(waiting.BusyUntil > 10);
            Assert.Equal(2, MonsterEmerge.CreateState(yugul, kind, unrisen: false));
        });
    }

    static void CheckPietyBlood(Player conn)
    {
        var piety = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/Axis/PietyBeastBoss"));
        var beam = Assert.Single(BossKits.Kit(conn, piety, 39), entry => entry.Effect == BossKits.PietyBeamEffect).Attack;
        Assert.Equal(new CorruptedBloodOnHit(3, 5, 2800, 0), beam.CorruptedBlood);
        conn.PlayerLife = 5000;
        conn.EnergyShield.SetMaximum(100, refill: true);
        var stats = new Dictionary<uint, int> { [Stat.AdditionalPhysicalDamageReductionPctAgainstHits] = 90 };
        void Hit(double at, bool blocked = false) => Game.ApplyCorruptedBloodOnHit(conn, beam, beam.Damage, blocked, at, stats);
        Hit(1, blocked: true);
        Assert.Equal(0, Game.CorruptedBloodStacks(conn, 1));
        Hit(1);
        // BuffDefinitions 238 is not a recovery buff: the empty list follows duration and stacks directly.
        Assert.Equal(33, Assert.Single(conn.Minions.OutputQueue, p => p.Log == "player corrupted blood (3)").Packet.Length);
        Hit(2);
        Game.AdvancePlayerAilmentsLocked(conn, 0, 3, stats);
        // The old rate applies until the second hit: 140 + 280 physical damage, 100 absorbed by ES.
        Assert.Equal(4680u, conn.PlayerLife);
        Assert.Equal(0u, conn.EnergyShield.Current);
        Hit(3);
        Assert.Equal(9, Game.CorruptedBloodStacks(conn, 3));
        Hit(3);
        Assert.Equal(10, Game.CorruptedBloodStacks(conn, 3));
        var flask = new Dictionary<uint, int> { [Stat.LocalFlaskBleedingImmunityIfBleedingAndRemoveCorruptedBloodS] = 6 };
        Game.ApplyConditionalFlaskAilmentProtectionLocked(conn, flask, 4);
        Hit(9.99);
        Assert.Equal(0, Game.CorruptedBloodStacks(conn, 9.99));
        Hit(10);
        Assert.Equal(3, Game.CorruptedBloodStacks(conn, 10));
        Game.AdvancePlayerAilmentsLocked(conn, 3, 16, stats);
        Assert.Equal(0, Game.CorruptedBloodStacks(conn, 16));
        Assert.Equal(3514u, conn.PlayerLife); // One second capped, five at three stacks, including the final partial tick.
        stats[Stat.CannotBeInflictedByCorruptedBlood] = 1;
        Hit(17);
        Assert.Equal(0, Game.CorruptedBloodStacks(conn, 17));
        stats.Clear();
        stats[Stat.DebuffTimePassedPlusPct] = 100;
        Hit(18);
        stats[Stat.UnaffectedByCorruptedBlood] = 1;
        Game.AdvancePlayerAilmentsLocked(conn, 18, 20.49, stats);
        Assert.Equal(3514u, conn.PlayerLife);
        Assert.Equal(3, Game.CorruptedBloodStacks(conn, 20.49));
        Assert.Equal(0, Game.CorruptedBloodStacks(conn, 20.5));
        Assert.Equal(UtilityFlasks.Support.Modelled, UtilityFlasks.SupportOf(Stat.ImmuneToCorruptedBlood));
    }

    [Fact]
    public void A_kit_plays_a_ready_special_first_and_rotates_the_rest()
    {
        // A swing with no cooldown, a second one without a cooldown, and a special that reaches further and is ready
        // again 30 seconds after each use.
        MonsterKitSkill[] kit =
        [
            new(100, new(0x4000, 1.5, 8, GameServer.Core.World.DamagePacket.Empty), 0),
            new(200, new(0x4001, 1.5, 8, GameServer.Core.World.DamagePacket.Empty), 0),
            new(300, new(0x4002, 2, 60, GameServer.Core.World.DamagePacket.Empty), 30),
        ];
        var boss = new SpawnedMonster("boss", new(0, 0, 1), 60, 2, maxLife: 100) { Kit = kit, Attack = kit[0].Attack };
        // Out of the swings' reach but inside the special's: the special is the only one that can act, so it does.
        Assert.Equal(0x4002, BossKits.Choose(boss, 0, 40)!.SkillId);
        // Ready, it also takes the turn in melee reach, ahead of the two swings.
        Assert.Equal(0x4002, BossKits.Choose(boss, 0, 2)!.SkillId);
        BossKits.Consume(boss, boss.Kit[2].Attack, 0);
        // Its cooldown is not out yet: with nothing else to prove, the default swing is the tie-break.
        Assert.Equal(0x4000, BossKits.Choose(boss, 1, 2)!.SkillId);
        BossKits.Consume(boss, boss.Kit[0].Attack, 1);
        // Used at 1, the default swing steps aside for the second one, which has not been used yet.
        Assert.Equal(0x4001, BossKits.Choose(boss, 2, 2)!.SkillId);
        BossKits.Consume(boss, boss.Kit[1].Attack, 2);
        // Both used, neither special ready: the swing used longest ago takes it again.
        Assert.Equal(0x4000, BossKits.Choose(boss, 2, 2)!.SkillId);
        // The special is spent: out of the swings' reach nothing can act, and the boss keeps the attack it had (the
        // walk-up brings it within reach).
        Assert.Equal(0x4000, BossKits.Choose(boss, 3, 40)!.SkillId);
        // Ready again at 32, it takes the turn ahead of the two without a cooldown, even in melee reach.
        Assert.Equal(0x4002, BossKits.Choose(boss, 32, 2)!.SkillId);
        // Nothing reaches a player that far away: it keeps the attack it had and walks up.
        Assert.Equal(0x4000, BossKits.Choose(boss, 40, 500)!.SkillId);
    }

    [Fact]
    public void A_boss_teleports_and_walks_up_for_its_swing()
    {
        InGame(conn =>
        {
            var catalog = Game.SkillsFor(conn);
            // Merveil, the Siren: her teleport is a move of the kit (BossMoves), with a cooldown the tables do not give.
            var siren = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/seawitchillusion/BossMerveil"));
            var kit = BossKits.Kit(conn, siren, 13, Game.MavenNucleus.CombatEffect(catalog, siren), unique: true);
            var teleport = Assert.Single(kit, k => catalog.EffectId(k.Effect) == "MerveilTeleport");
            Assert.Equal(MonsterSkillShape.Travel, teleport.Attack.Use?.Shape);
            Assert.True(teleport.CooldownSeconds >= BossMoves.TeleportCooldown);
            // Ice Spear reaches her whole aggro range: once her swing has waited its turn she walks up for it.
            var boss = new SpawnedMonster("Merveil", new(0, 0, 1), 60, 2, maxLife: 100) { Kit = kit, Attack = kit[0].Attack, Variety = siren };
            var melee = Assert.Single(kit, k => catalog.EffectId(k.Effect) == "Melee");
            // She opens with her spells: the swing's wait starts with the fight.
            Assert.NotEqual(melee.Attack.SkillId, BossKits.Choose(boss, 1, 30)!.SkillId);
            foreach (var entry in kit) boss.KitReadyAt[entry.Effect] = 1;
            Assert.Equal(melee.Attack.SkillId, BossKits.Choose(boss, 12, 30)!.SkillId);
        });
    }

    [Fact]
    public void Boss_moves_unshaped_filters_summons_and_form_cleanup_hold()
    {
        InGame(conn =>
        {
            var catalog = Game.SkillsFor(conn);
            // Cyclones, projectile-moves, mines and triggered effects are never misclassified as moves or teleports.
            Assert.True(BossMoves.IsTeleport(catalog, 4007)); // MerveilTeleport
            foreach (int excluded in new[] { 2530, 6052, 6566, 2803, 71 }) // Cyclone, HydraTeleportShot, AvariusCharge, SmokeMine, IceNovaTriggered
            {
                Assert.False(BossMoves.IsMove(catalog, excluded));
                Assert.False(BossMoves.IsTeleport(catalog, excluded));
            }
            // Kit bosses still pass unshaped safety filters (DeathNova / Triggered excluded) while keeping Merveil's own skills.
            var twisted = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/Seawitch/BossMerveil2"));
            Assert.False(BossKits.KeepsUnshaped(catalog, twisted, 1618)); // SpikerBossIceSpearDeathNova
            Assert.False(BossKits.KeepsUnshaped(catalog, twisted, 71));   // IceNovaTriggered
            Assert.True(BossKits.KeepsUnshaped(catalog, twisted, 3250));  // MerveilSeaWitchScreech
            Assert.True(BossKits.KeepsUnshaped(catalog, twisted, 3777));  // MerveilGeyser
            Assert.True(BossKits.KeepsUnshaped(catalog, twisted, 6995));  // SeawitchWaveGeometrySpellMerveil
            var twistedKit = BossKits.Kit(conn, twisted, 13, Game.MavenNucleus.CombatEffect(catalog, twisted), unique: true);
            Assert.Contains(twistedKit, k => k.Effect == 3777 && k.Attack.Strike is not null);
            Assert.Contains(twistedKit, k => k.Effect == 3250);
            Assert.Contains(twistedKit, k => k.IsTeleport && k.Effect == 4007);
            // Geyser impact art ends with the final pulse instead of hanging past stay.
            var geyser = Assert.NotNull(BossMoves.StrikeOf(catalog, 3777, 13));
            Assert.InRange(geyser.Strike.ImpactSeconds, 0.1, 9.01);

            // Teleport opener respects MinRange and Range before being consumed.
            var form = new SpawnedMonster("Merveil", new(0, 0, 1), 60, 2, maxLife: 100) { Kit = twistedKit, Attack = twistedKit[0].Attack, Variety = twisted };
            BossKits.OpenWithTeleport(form);
            var teleport = Assert.Single(twistedKit, k => k.IsTeleport);
            Assert.NotEqual(teleport.Attack.SkillId, BossKits.Choose(form, 1, 5)!.SkillId);
            Assert.NotEqual(teleport.Attack.SkillId, BossKits.Choose(form, 1, 100)!.SkillId);
            Assert.Equal(teleport.Attack.SkillId, BossKits.Choose(form, 1, 30)!.SkillId);

            // Guessed boss summons resolve to safe adds with world-unit offset and DiesWithParent, excluding chieftains.
            var piety = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/Axis/PietyBoss"));
            var blood = BossSummons.For(catalog, piety, 2507);
            Assert.NotNull(blood);
            Assert.Equal("Metadata/Monsters/BloodElemental/BloodElemental", Game.Monsters.Find(blood.Variety.ToString())?.Id);
            Assert.Equal(65, blood.Offset);
            Assert.True(blood.DiesWithParent);
            var daresso = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/Daresso/DaressoBoss"));
            var clone = BossSummons.For(catalog, daresso, 4614);
            Assert.NotNull(clone);
            Assert.Equal("Metadata/Monsters/Daresso/DaressoClone", Game.Monsters.Find(clone.Variety.ToString())?.Id);
            Assert.True(clone.DiesWithParent);
            var chief = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/BloodChieftain/MonkeyChiefBloodBossPale"));
            var monkey = BossSummons.For(catalog, chief, 883);
            Assert.NotNull(monkey);
            Assert.DoesNotContain("Chief", Game.Monsters.Find(monkey.Variety.ToString())?.Id ?? "");

            // Second-form cleanup always clears EntranceGrace and removes Form 1 even if the killer leaves mid-transform.
            var before = new SpawnedMonster("Merveil", new(10, 10, 1), 60, 2, maxLife: 100) { Dead = true, Life = 0 };
            form.EntranceGrace = true;
            conn.Area.Monsters[0x9001] = before;
            conn.Area.Monsters[0x9002] = form;
            conn.LeavingArea = true;
            BossForms.Finish(conn, 0x9001, before, 0x9002, form).GetAwaiter().GetResult();
            Assert.False(form.EntranceGrace);
            Assert.True(before.CorpseConsumed);
            Assert.DoesNotContain(0x9001u, conn.Area.Monsters.Keys);
        });
    }

    [Fact]
    public void Boss_summon_overrides_name_the_monster_the_guess_cannot_find()
    {
        InGame(player =>
        {
            var catalog = Game.SkillsFor(player);
            // Atziri's demons are skeletons from another folder and her mirrors are a skill with no summon word;
            // Avarius's statues are excluded by name, Kaom's spawners and Piety's eye spawners carry no summon word,
            // and Shavronne's books name no monster. Each override names the row's monster and how many it brings.
            var atziri = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/Atziri/Atziri"));
            var demons = BossSummons.For(catalog, atziri, 3386); // AtziriSummonDemons
            Assert.NotNull(demons);
            Assert.Equal("Metadata/Monsters/Skeletons/SkeletonMeleeKnightElementalSwordAtziri", Game.Monsters.Find(demons.Variety.ToString())?.Id);
            Assert.Equal(3, demons.Count);
            Assert.True(demons.DiesWithParent);
            var mirror = BossSummons.For(catalog, atziri, 3401); // AtziriMirrorImage
            Assert.NotNull(mirror);
            Assert.Equal("Metadata/Monsters/Atziri/AtziriMirror", Game.Monsters.Find(mirror.Variety.ToString())?.Id);
            Assert.Equal(2, mirror.Count);
            Assert.True(mirror.DiesWithParent);
            var atziri2 = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/Atziri/Atziri2"));
            var mirror2 = BossSummons.For(catalog, atziri2, 3402); // AtziriMirrorImageUber
            Assert.NotNull(mirror2);
            Assert.Equal("Metadata/Monsters/Atziri/AtziriMirror", Game.Monsters.Find(mirror2.Variety.ToString())?.Id);
            Assert.Equal(2, mirror2.Count);
            var avarius = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/AvariusCasticus/AvariusCasticus"));
            var statues = BossSummons.For(catalog, avarius, 6326); // AvariusRaiseStatues
            Assert.NotNull(statues);
            Assert.Equal("Metadata/Monsters/BloodElemental/BloodElemental", Game.Monsters.Find(statues.Variety.ToString())?.Id);
            Assert.Equal(2, statues.Count);
            var kaom = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/Kaom/KaomBoss"));
            var archers = BossSummons.For(catalog, kaom, 5142); // TriggerKaomArcherSpawners
            Assert.NotNull(archers);
            Assert.Equal("Metadata/Monsters/Daemon/KaomVolcanoMortar", Game.Monsters.Find(archers.Variety.ToString())?.Id);
            Assert.Equal(3, archers.Count);
            var beast = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/Axis/PietyBeastBoss"));
            var eye = BossSummons.For(catalog, beast, 5243); // PietyActivateEyeSpawner
            Assert.NotNull(eye);
            Assert.Equal("Metadata/Monsters/SummonedSkull/SummonedArcaneEye", Game.Monsters.Find(eye.Variety.ToString())?.Id);
            Assert.Equal(1, eye.Count);
            var tower = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/Shavronne/ShavronneTower"));
            var books = BossSummons.For(catalog, tower, 6674); // ShavronneSummonLightningBooks
            Assert.NotNull(books);
            Assert.Equal("Metadata/Monsters/Skeletons/SkeletonCasterLightning", Game.Monsters.Find(books.Variety.ToString())?.Id);
            Assert.Equal(2, books.Count);
        });
    }

    [Fact]
    public void Boss_kits_play_the_summon_rows_the_tables_give_no_type()
    {
        InGame(player =>
        {
            // The summon rows are in the kit, not only resolvable: Atziri's demons and mirrors, Kaom's archer
            // spawners and Piety's eye spawners. The last two carry no attack, spell or projectile type, so without
            // the override the kit drops them before it ever asks what they raise.
            var atziri = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/Atziri/Atziri"));
            var atziriKit = BossKits.Kit(player, atziri, 70);
            Assert.Contains(atziriKit, k => k.Effect == 3386 && k.Attack.Summon is not null);
            Assert.Contains(atziriKit, k => k.Effect == 3401 && k.Attack.Summon is not null);
            var kaom = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/Kaom/KaomBoss"));
            var kaomKit = BossKits.Kit(player, kaom, 40);
            Assert.Contains(kaomKit, k => k.Effect == 5142 && k.Attack.Summon is not null);
            var beast = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/Axis/PietyBeastBoss"));
            var beastKit = BossKits.Kit(player, beast, 45);
            Assert.Contains(beastKit, k => k.Effect == 5243 && k.Attack.Summon is not null);
            var tower = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/Shavronne/ShavronneTower"));
            var towerKit = BossKits.Kit(player, tower, 50);
            Assert.Contains(towerKit, k => k.Effect == 6674 && k.Attack.Summon is not null);
        });
    }

    /// <summary>Runs the body with the game module mounted on a connection of its own, as BossStageTests does.</summary>
    static void InGame(Action<Player> body)
    {
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        try
        {
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
            body(new Player(new(1, "kit", 100, 100, 10, 10, null, []), 1, 1));
        }
        finally
        {
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            if (previousLogic is not null) Program.SetGameLogic(previousLogic);
        }
    }
}
