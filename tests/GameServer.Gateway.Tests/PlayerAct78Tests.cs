using GameServer.Modding;

namespace GameServer.Gateway.Tests;

public sealed class PlayerAct78Tests
{
    // Every monster shot has one drawer (issue 1004). One the client draws carries its count on the actor record and the
    // server flies the same count hidden; one the server draws carries no count and flies visible. The families resolve
    // to the rows the client's actions fire, which the hit timing on GGG's 3.30.0b servers matches (MonsterShots).
    [Fact]
    public void Every_monster_shot_has_one_drawer_and_its_record_count_is_its_flight_count()
    {
        InGame(conn =>
        {
            var catalog = Game.SkillsFor(conn);
            int clientDrawn = 0, serverDrawn = 0;
            foreach (var variety in Game.Monsters.All)
            {
                int effect = Game.MavenNucleus.CombatEffect(catalog, variety);
                if (effect < 0 || !Game.MonsterAttackIsRanged(catalog, variety, effect)
                    || Game.MavenNucleus.ShotFor(catalog, variety, effect, 58) is not { } shot) continue;
                var attack = new MonsterAttack(0x4000, 1, 30, GameServer.Core.World.DamagePacket.Empty)
                    { Ranged = true, Spell = catalog.IsSpell(effect), Projectile = shot };
                var stats = Game.MonsterActionSkillStats(conn, effect, 58, attack).ToDictionary(s => s.Item1, s => s.Item2);
                string label = $"{variety.Id} {catalog.EffectId(effect)}";
                // A resolved shot keeps the effect's SkillID as the client's own builder adds it, but a spell mortar's.
                if (catalog.BehaviourVariation(effect) is > 0 and var skillId && shot is { Variation: 0, SkillStats: not null })
                    Assert.Equal(catalog.MonsterProjectile(effect) is null ? skillId : 0, stats.GetValueOrDefault(StatKeys.SkillBehaviourVariation));
                if (shot.ClientDraws)
                {
                    clientDrawn++;
                    Assert.True(shot.Count >= 1, label);
                    Assert.Equal(shot.Count, stats[StatKeys.VirtualTotalProjectiles]);
                    Assert.Equal(shot.Count, stats[StatKeys.TotalProjectilesToFire]);
                    Assert.Equal(shot.Count, stats[StatKeys.VirtualTotalArrows]);
                    var flight = shot.Target == SkillCatalog.MonsterShotTarget.MortarScatter
                        ? Game.MonsterProjectileProfile(shot, 50, 20, 60)
                        : MonsterShots.ClientDrawnProfile(conn, shot, !attack.Spell, new(20, 0), 0x8001, 0x40);
                    Assert.True(flight.HideVisual, label);
                    Assert.Equal(shot.Count, flight.Count);
                }
                else
                {
                    serverDrawn++;
                    Assert.False(stats.ContainsKey(StatKeys.VirtualTotalProjectiles), label);
                    Assert.False(Game.MonsterProjectileProfile(shot, 50, 20, 60).HideVisual, label);
                }
            }
            Assert.True(clientDrawn > 800 && serverDrawn > 0, $"{clientDrawn} client-drawn, {serverDrawn} server-drawn");

            SkillCatalog.MonsterProjectileInfo? Shot(string variety, string effect) =>
                MonsterShots.Resolve(catalog, variety.Length > 0 ? Game.Monsters.Find(variety) : null, catalog.EffectRow(effect), 10);
            // The families seen on GGG's servers, each the row whose speed their hits fit.
            Assert.Equal(("Metadata/Projectiles/SandspitterMortarProjectile", 183, true),
                (Shot("Metadata/Monsters/SandSpitters/SandSpitter", "MMASandSpitterProjectile")!.Metadata,
                    Shot("Metadata/Monsters/SandSpitters/SandSpitter", "MMASandSpitterProjectile")!.Variation,
                    Shot("Metadata/Monsters/SandSpitters/SandSpitter", "MMASandSpitterProjectile")!.ClientDraws));
            Assert.Equal("Metadata/Projectiles/Fireball", Shot("Metadata/Monsters/Goatman/GoatmanShamanFireball", "MonsterFireballContactPos")!.Metadata);
            Assert.Equal("Metadata/Projectiles/Snake", Shot("Metadata/Monsters/Snake/SnakeMeleeSpitForest", "SnakeProjectileSlow")!.Metadata);
            Assert.Equal("Metadata/Projectiles/SkeletonProjectileCold", Shot("Metadata/Monsters/Skeletons/SkeletonCasterCold", "SkeletonProjectileCold")!.Metadata);
            var arrow = Shot("Metadata/Monsters/Skeletons/SkeletonBow", "MeleeAtAnimationSpeed")!;
            Assert.Equal(("Metadata/Projectiles/DefaultArrow", true), (arrow.Metadata, arrow.Arrows));
            // The issue's bosses: the Lieutenant's six arrows fly; the Elder's volley is the server's (its contact
            // direction is not reproduced); the Shaper's speeds up as the client's mover does, through every target.
            var split = Shot("", "LieutenantSplitArrow")!;
            Assert.Equal(("Metadata/Projectiles/SplitArrowDefault", 6, true), (split.Metadata, split.Count, split.ClientDraws));
            var elder = Shot("", "ElderProjectileSpellcast1")!;
            Assert.Equal(("Metadata/Projectiles/ElderProjectileLarge", 3, false), (elder.Metadata, elder.Count, elder.ClientDraws));
            var shaper = Shot("", "AtlasBossAcceleratingProjectiles")!;
            var shaperFlight = MonsterShots.ClientDrawnProfile(conn, shaper, false, new(30, 0), 0x8001, 0x40);
            Assert.Equal(("Metadata/Projectiles/ShaperProjectile", true, -1), (shaper.Metadata, shaper.ClientDraws, shaperFlight.Pierce));
            Assert.Equal(0.46 * .05 + 25 * .05 * .05, shaperFlight.Legs!(0).First(new(0, 0), new(30, 0)).Target.X, 3);
            Assert.Equal("Metadata/Projectiles/IncaBossMinionProjectile", Shot("", "IncursionIncaMinionProjectile")!.Metadata);
            // Actions that name their row in code: an archer known by its bow class, a siphon by its art, a throw that comes
            // back by the player's recipe, a bouncing magma orb, a fixed lightning mortar ring.
            Assert.Equal(("Metadata/Projectiles/DefaultArrow", true),
                (Shot("Metadata/Monsters/Bandits/BanditBow", "MeleeAtAnimationSpeed") is { } sniper ? (sniper.Metadata, sniper.ClientDraws) : default));
            Assert.Equal("Metadata/Projectiles/PaleKingSiphon", Shot("", "ProphecyBossEssenceDrain")!.Metadata);
            Assert.Equal(("Metadata/Projectiles/ThrownWeapon", "SpectralThrow", true),
                (Shot("", "ExileThrownWeapon")!.Metadata, Shot("", "ExileThrownWeapon")!.Recipe, Shot("", "ExileThrownWeapon")!.ClientDraws));
            Assert.Equal(("Metadata/Projectiles/MatriachMortar", true, "RollingMagma"),
                (Shot("", "MotherOfFlamesMagmaOrb")!.Metadata, Shot("", "MotherOfFlamesMagmaOrb")!.Lobbed, Shot("", "MotherOfFlamesMagmaOrb")!.Recipe));
            Assert.Equal("Metadata/Projectiles/MortarLightning", Shot("", "EssenceWrathMortarProjectile")!.Metadata);
            // A spell mortar is the client's as the attack form is (its SkillID's MonsterMortar row on the record), and
            // lands a fixed time after its release at any distance: its flight clip's contact (the Tide Strider's 496 ms),
            // else the clip's length (Arakaali's 400 ms clip has none).
            var spellMortar = Shot("", "ArakaaliMortarProjectile")!;
            Assert.Equal((true, 10, true), (spellMortar.ClientDraws, spellMortar.Variation, spellMortar.FixedFlight));
            Assert.Equal(0.4, MonsterShots.MortarFlightSeconds(spellMortar, 1), 3);
            Assert.Equal(0.496, MonsterShots.MortarFlightSeconds(Shot("", "WaterElementalMortar")!, 1), 3);
            // ... and so is a nova of them, each landing turned about the launch as the client's emitter turns it.
            Assert.True(Shot("", "DoedreBloodProjectileNova1")!.ClientDraws);
            // A spark is the client's own wander (SparkLegs, seeded by the monster), its life on the record; a totem's
            // spark is the server's, which places no totem.
            var spark = Shot("", "MonsterSpark")!;
            Assert.Equal(("Metadata/Projectiles/Spark", true, "Spark"), (spark.Metadata, spark.ClientDraws, spark.Recipe));
            Assert.Contains(MonsterShots.CountStats(spark), s => Program.Stats.Name(s.Item1) == "skill_effect_duration" && s.Item2 > 0);
            Assert.Equal((false, "totem, trap or mine form"), (Shot("", "BossSparkTotem")!.ClientDraws, Shot("", "BossSparkTotem")!.ServerReason));
            // An ice spear changes form 20.199 cells out, its second form stronger.
            var spear = Shot("", "MountainGoatmanIceSpear")!;
            var spearFlight = MonsterShots.ClientDrawnProfile(conn, spear, false, new(30, 0), 0x8001, 0x40);
            Assert.Equal((true, -1, 20.199, 1.25), (spear.ClientDraws, spearFlight.Pierce, spearFlight.FormChange!.Distance, spearFlight.FormChange.DamageMultiplier));
            // A spiral's steps are the shared release's: the record carries their total, the server flies each step.
            var spiralSpear = Shot("", "BeyondDemonIceSpearSpiral")!;
            Assert.Equal((true, spiralSpear.Count), (spiralSpear.ClientDraws, MonsterShots.Spiral(spiralSpear)!.Value.Steps));
            // Creeping frost meets the player and bursts there, at the radius its record gives the client (Hailrake's +100%).
            var frost = Shot("", "HailrakeArcticBreath")!;
            var frostFlight = MonsterShots.ClientDrawnProfile(conn, frost, false, new(30, 0), 0x8001, 0x40);
            Assert.Equal((true, true, 30.0), (frost.ClientDraws, frostFlight.DirectDamage, frostFlight.EndArea!.Radius));
            Assert.Contains(MonsterShots.CountStats(frost), s => Program.Stats.Name(s.Item1) == "total_skill_area_of_effect_radius_+%_including_final" && s.Item2 == 100);
            // A simulated mortar's record keeps its own publication and gains the effect's native art.
            var mortar = new MonsterAttack(0x4000, 1, 30, GameServer.Core.World.DamagePacket.Empty)
                { Ranged = true, Projectile = new("Metadata/Projectiles/SandspitterMortarProjectile", 100, false, 1)
                    { Count = 3, Variation = 183 } };
            var final = Game.MonsterActionSkillStats(conn, 35, 58, mortar);
            uint artKey = Program.Stats.Key("skill_art_variation")!.Value;
            Assert.Equal(Game.MonsterProjectileSkillStats(conn, 35, 58, mortar), final.Where(s => s.Item1 != artKey));
            Assert.Equal(21, Assert.Single(final, s => s.Item1 == artKey).Item2);
        });
    }

    static void InGame(Action<ClientState> body)
    {
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        try
        {
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
            body(new ClientState(new(1, "native shots", 100, 100, 10, 10, null, []), 1, 1));
        }
        finally
        {
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            if (previousLogic is not null) Program.SetGameLogic(previousLogic);
        }
    }
}
