using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class CombatBugFixesTests
{
    static Player CreateTestPlayer(string name)
    {
        var player = new Player(new(1, name, 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = { Name = name },
            PlayerLife = 1000,
            PlayerMana = 100,
            Instance = new(),
            Pools = new(100, 1000, 100, 0, 0),
        };
        player.Clock.Stop();
        return player;
    }

    [Fact]
    public async Task Player_curses_deduplicate_across_sources_and_Temporal_Chains_clamps_slow()
    {
        string path = Path.Combine(Path.GetTempPath(), $"combat-bugfixes-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Saves.Open(path);
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        try
        {
            using var wire = new PartyFollowerTests.Wire();
            var player = CreateTestPlayer("CurseDedup");
            player.Connection.Session = new("curse-dedup-test", wire.Stream, wire.Ciphers);

            const ushort temporalChainsRow = 69;
            const ushort vulnerabilityRow = 79;
            object heldTc = new(), heldVuln = new();

            // Two timed Temporal Chains from different casters + one held source must not stack.
            await Game.AddPlayerDebuff(player, temporalChainsRow, caster: 101,
                new Dictionary<uint, int> { [Stat.TemporalChainsActionSpeedPlusPctFinal] = -20 }, 10);
            await Game.AddPlayerDebuff(player, temporalChainsRow, caster: 102,
                new Dictionary<uint, int> { [Stat.TemporalChainsActionSpeedPlusPctFinal] = -35 }, 10);
            await Game.HoldPlayerDebuff(player, heldTc, temporalChainsRow, caster: 103,
                new Dictionary<uint, int> { [Stat.TemporalChainsActionSpeedPlusPctFinal] = -15 });

            var sources = Game.PlayerDebuffStats(player, []);
            Assert.Equal(-35, sources.GetValueOrDefault(Stat.TemporalChainsActionSpeedPlusPctFinal));
            Assert.Equal(0.65, Game.PlayerActionFactorFrom(player, sources.GetValueOrDefault), precision: 4);

            // Extreme Temporal Chains slow clamps at -75% (0.25 action factor), never slowing action speed to 0.
            await Game.AddPlayerDebuff(player, temporalChainsRow, caster: 104,
                new Dictionary<uint, int> { [Stat.TemporalChainsActionSpeedPlusPctFinal] = -95 }, 10);
            sources = Game.PlayerDebuffStats(player, []);
            Assert.Equal(-95, sources.GetValueOrDefault(Stat.TemporalChainsActionSpeedPlusPctFinal));
            Assert.Equal(0.25, Game.PlayerActionFactorFrom(player, sources.GetValueOrDefault), precision: 4);

            // Unaffected by Temporal Chains suppresses the action speed slow completely.
            sources[Stat.UnaffectedByTemporalChains] = 1;
            Assert.Equal(1.0, Game.PlayerActionFactorFrom(player, sources.GetValueOrDefault), precision: 4);

            // Non-TC curses also deduplicate across held and timed sources (strongest wins).
            await Game.HoldPlayerDebuff(player, heldVuln, vulnerabilityRow, caster: 201,
                new Dictionary<uint, int> { [Stat.PhysicalDamageTakenPlusPct] = 22 });
            await Game.AddPlayerDebuff(player, vulnerabilityRow, caster: 202,
                new Dictionary<uint, int> { [Stat.PhysicalDamageTakenPlusPct] = 35 }, 10);
            sources = Game.PlayerDebuffStats(player, []);
            Assert.Equal(35, sources.GetValueOrDefault(Stat.PhysicalDamageTakenPlusPct));

            // Releasing the held Vulnerability leaves the timed +35% intact; holding a stronger one replaces it.
            await Game.ReleasePlayerDebuff(player, heldVuln);
            sources = Game.PlayerDebuffStats(player, []);
            Assert.Equal(35, sources.GetValueOrDefault(Stat.PhysicalDamageTakenPlusPct));
            await Game.HoldPlayerDebuff(player, heldVuln, vulnerabilityRow, caster: 203,
                new Dictionary<uint, int> { [Stat.PhysicalDamageTakenPlusPct] = 45 });
            sources = Game.PlayerDebuffStats(player, []);
            Assert.Equal(45, sources.GetValueOrDefault(Stat.PhysicalDamageTakenPlusPct));
            await Game.ReleasePlayerDebuff(player, heldVuln);
            sources = Game.PlayerDebuffStats(player, []);
            Assert.Equal(35, sources.GetValueOrDefault(Stat.PhysicalDamageTakenPlusPct));

            // Strongbox (caster == 0) curses deduplicate by strongest-wins instead of clobbering, and Hexproof suppresses
            // curse effect penalties (and blocks Silence row 275) while keeping curse flag stats.
            await Game.AddPlayerDebuff(player, vulnerabilityRow, caster: 0,
                new Dictionary<uint, int> { [Stat.PhysicalDamageTakenPlusPct] = 50 }, 10);
            await Game.AddPlayerDebuff(player, vulnerabilityRow, caster: 0,
                new Dictionary<uint, int> { [Stat.PhysicalDamageTakenPlusPct] = 15 }, 10);
            sources = Game.PlayerDebuffStats(player, []);
            Assert.Equal(50, sources.GetValueOrDefault(Stat.PhysicalDamageTakenPlusPct));

            const ushort eleWeakRow = 70;
            const ushort silenceRow = 275;
            await Game.AddPlayerDebuff(player, eleWeakRow, caster: 0,
                new Dictionary<uint, int>
                {
                    [Stat.BaseResistAllElementsPct] = -30,
                    [Stat.YouAreCursedWithElementalWeakness] = 1,
                }, 10);
            var hexproofSources = Game.PlayerDebuffStats(player, new Dictionary<uint, int> { [Stat.Hexproof] = 1 });
            Assert.Equal(0, hexproofSources.GetValueOrDefault(Stat.BaseResistAllElementsPct));
            Assert.Equal(1, hexproofSources.GetValueOrDefault(Stat.YouAreCursedWithElementalWeakness));
            Assert.Equal(0, hexproofSources.GetValueOrDefault(Stat.PhysicalDamageTakenPlusPct));

            using (GameEvents.PlayerStatSources.Add("Game", (owner, s) =>
            {
                if (ReferenceEquals(owner, player)) s[Stat.Hexproof] = 1;
                return s;
            }))
            {
                await Game.AddPlayerDebuff(player, silenceRow, caster: 0, new Dictionary<uint, int>(), 10);
                Assert.False(Game.HasPlayerDebuff(player, silenceRow));
            }

            // Map Temporal Chains must not stack on top of an already-stronger debuff Temporal Chains.
            var area = new MapDevice.MapArea(player.Layout.Hash, "MapWorldsStrand", "Strand", 1);
            var map = new MapMods(new Dictionary<uint, int> { [Stat.MapPlayerHasLevelXTemporalChains] = 15 });
            player.Instance.OpenedWith(new MapDevice.Run(player.Character.Name!, area, map));
            var pools = Game.PoolsFor(player, 1);
            Assert.Equal(0, pools.MapTemporalChainsActionSpeedPct);
        }
        finally
        {
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }

    [Fact]
    public void Vengeance_grants_rage_when_hit_and_scales_armour_per_rage()
    {
        string path = Path.Combine(Path.GetTempPath(), $"combat-vengeance-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Saves.Open(path);
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        try
        {
            using var wire = new PartyFollowerTests.Wire();
            var player = CreateTestPlayer("VengeanceTest");
            player.Clock.Start();
            player.Connection.Session = new("vengeance-test", wire.Stream, wire.Ciphers);
            // Vengeance notable (rage_notable2108, hash 53840): Gain 3 Rage when Hit by an Enemy, 1% increased Armour per Rage.
            const ushort vengeanceHash = 53840;
            player.Character.Passives.Add(vengeanceHash);
            // Provide base armour via PlayerStatSources so percentage scaling is directly observable.
            using var statHook = GameEvents.PlayerStatSources.Add("Game", (p, s) =>
            {
                if (!ReferenceEquals(p, player)) return s;
                s[Stat.BasePhysicalDamageReductionRating] = s.GetValueOrDefault(Stat.BasePhysicalDamageReductionRating) + 1000;
                return s;
            });
            try
            {
                var basePools = Game.PoolsFor(player, 1);
                Assert.Equal(1000, basePools.Armour);
                Assert.Equal(0, basePools.ArmourPct);

                // Synthetic enemy hit must trigger Vengeance's GainXRageWhenHit (+3 Rage).
                Assert.True(Game.TrySyntheticEnemyHit(player, DamageType.Physical, 10, block: false, spell: false,
                    suppress: false, out _));
                var inputs = RageStats.Inputs(player, key => Game.CharacterPlayerStats(player).GetValueOrDefault(key));
                Assert.Equal(3, inputs.GainWhenHit);
                Assert.Equal(3, player.Rage.Read(Game.AreaTime(player), inputs).Current);

                // 3 Rage with 1% increased Armour per Rage scales 1000 base Armour to 1030.
                var ragedPools = Game.PoolsFor(player, 1);
                Assert.Equal(3, ragedPools.ArmourPct);
                Assert.Equal(1030, ragedPools.Armour);

                // Higher Rage scales Armour proportionally and refreshes Pools in RageSnapshot.
                player.Rage.Gain(17, Game.AreaTime(player), inputs);
                Assert.Equal(20, player.Rage.Read(Game.AreaTime(player), inputs).Current);
                var twentyRagePools = Game.PoolsFor(player, 1);
                Assert.Equal(20, twentyRagePools.ArmourPct);
                Assert.Equal(1200, twentyRagePools.Armour);
            }
            finally
            {
                PlayerInventories.Forget(player.Character.Name!);
            }
        }
        finally
        {
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }

    [Fact]
    public async Task Axe_mastery_destroys_corpses_on_hit_kills_and_respects_dot_and_cannot_be_destroyed()
    {
        string path = Path.Combine(Path.GetTempPath(), $"combat-axemastery-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Saves.Open(path);
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        try
        {
            using var wire = new PartyFollowerTests.Wire();
            var player = CreateTestPlayer("AxeMasteryTest");
            player.Clock.Start();
            player.Connection.Session = new("axe-mastery-test", wire.Stream, wire.Ciphers);

            // Allocate real Axe Mastery node with Axe1 ("Enemies Killed by your Hits are destroyed").
            ushort axeNode = Enumerable.Range(1, ushort.MaxValue - 1).Select(i => (ushort)i)
                .First(i => Program.Passives.Find(i) is { IsMastery: true }
                    && Program.Passives.EffectsFor(i).Any(e => e.Id == "Axe1"));
            player.Character.Passives.Add(axeNode);
            player.Character.MasteryEffects = new Dictionary<ushort, ushort>
            {
                [axeNode] = Program.Passives.EffectsFor(axeNode).Single(e => e.Id == "Axe1").Hash,
            };

            var stats = Game.CharacterPlayerStats(player);
            Assert.NotEqual(0, stats.GetValueOrDefault(Stat.EnemiesExplodeOnKill));

            var variety = Game.Monsters.All.First(v => v.Supported && v.Id.Contains("Zombie", StringComparison.OrdinalIgnoreCase));
            SpawnedMonster Spawn(uint id, Dictionary<uint, int>? extraMods = null)
            {
                var m = new SpawnedMonster(variety.Name, new(0, 0, 0), 0, 0, 100)
                {
                    ObjectId = id,
                    Variety = variety,
                    Hostile = true,
                    Level = 1,
                    NoRewards = true,
                };
                if (extraMods is not null)
                    m.Mods = MonsterModSet.None with { Effects = new(extraMods) };
                player.Area.Monsters[id] = m;
                return m;
            }

            try
            {
                // 1. Hit kill destroys the corpse and removes it from Area.Monsters.
                var hitVictim = Spawn(501);
                var hit = Game.ApplyMonsterHitLocked(player, hitVictim, damage: 200, rampageSource: null,
                    playerStats: stats, allyAt: null, cull: default, cullPlayer: stats);
                Assert.NotNull(hit);
                Assert.Equal(0u, hit.Value.Life);
                await Game.MonsterKilled(player, 501, hitVictim, byMinion: false, byHit: true);
                Assert.True(hitVictim.CorpseConsumed);
                Assert.False(player.Area.Monsters.ContainsKey(501));

                // 2. Non-hit (DoT) kill must NOT destroy the corpse.
                var dotVictim = Spawn(502);
                dotVictim.Life = 0;
                dotVictim.Dead = true;
                await Game.MonsterKilled(player, 502, dotVictim, byMinion: false, byHit: false);
                Assert.False(dotVictim.CorpseConsumed);
                Assert.True(player.Area.Monsters.ContainsKey(502));

                // 3. Monster with corpse_cannot_be_destroyed must NOT be destroyed on hit kill.
                var protectedVictim = Spawn(503, new Dictionary<uint, int> { [Stat.CorpseCannotBeDestroyed] = 1 });
                var protectedHit = Game.ApplyMonsterHitLocked(player, protectedVictim, damage: 200, rampageSource: null,
                    playerStats: stats, allyAt: null, cull: default, cullPlayer: stats);
                Assert.NotNull(protectedHit);
                Assert.Equal(0u, protectedHit.Value.Life);
                await Game.MonsterKilled(player, 503, protectedVictim, byMinion: false, byHit: true);
                Assert.False(protectedVictim.CorpseConsumed);
                Assert.True(player.Area.Monsters.ContainsKey(503));
            }
            finally
            {
                PlayerInventories.Forget(player.Character.Name!);
            }
        }
        finally
        {
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }

    [Fact]
    public async Task Tribal_Fury_splashes_melee_strikes_at_full_damage_scales_area_and_skips_non_strikes()
    {
        // Unit check: melee_splash activates with no less-damage penalty, melee_splash_area_of_effect_+% adds to
        // increased area (not to melee_splash_area_of_effect_+%_final), and support penalty still applies when linked.
        var tribalOnly = MeleeSplash.Settings(key => key == Stat.MeleeSplash ? 1 : 0);
        Assert.NotNull(tribalOnly);
        Assert.Equal(14, tribalOnly.Value.Radius);
        Assert.Equal(1.0, tribalOnly.Value.More, 9);

        var withSmallNodes = MeleeSplash.Settings(key => key switch
        {
            Stat.MeleeSplash => 1,
            Stat.MeleeSplashAreaOfEffectPlusPct => 30,
            _ => 0,
        });
        Assert.Equal(15, withSmallNodes!.Value.Radius);
        Assert.Equal(1.0, withSmallNodes.Value.More, 9);

        // (1 + 1.0 generic + 1.0 splash increased) * (1 + 0.5 splash more) = 4.5 -> floor(14 * sqrt(4.5)) = 29.
        var combinedArea = MeleeSplash.Settings(key => key switch
        {
            Stat.MeleeSplash => 1,
            Stat.SkillAreaOfEffectPlusPct => 100,
            Stat.MeleeSplashAreaOfEffectPlusPct => 100,
            Stat.MeleeSplashAreaOfEffectPlusPctFinal => 50,
            _ => 0,
        });
        Assert.Equal(29, combinedArea!.Value.Radius);

        var withSupport = MeleeSplash.Settings(key => key switch
        {
            Stat.MeleeSplash => 1,
            Stat.SupportMeleeSplashDamagePlusPctFinalForSplash => -40,
            _ => 0,
        });
        Assert.Equal(0.6, withSupport!.Value.More, 9);

        // End-to-end with the real Tribal Fury passive (blight_special_notable6, graphId 27119) and real small
        // Splash AoE nodes (strikes_speed2734 / strikes_speed2735, graphIds 25175 / 53791).
        var tribalFury = Program.Passives.FindById("blight_special_notable6");
        Assert.NotNull(tribalFury);
        var splashAoe1 = Program.Passives.FindById("strikes_speed2734");
        var splashAoe2 = Program.Passives.FindById("strikes_speed2735");
        Assert.NotNull(splashAoe1);
        Assert.NotNull(splashAoe2);

        var clock = new StepClock();
        using var f = new TriggeredSummonTests.Fixture(clock);
        var player = f.Player;
        var previous = Game.AreaUpdateContext.Value;
        player.ProjectileRuntimeState = f.Session;
        player.Walk.Teleport(100, 100, clock.At);
        Game.AreaUpdateContext.Value = new(f.Lifetime, clock);
        int extraStrikes = 0;
        using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (ReferenceEquals(owner, player))
            {
                values[Stat.GlobalAlwaysHit] = 1;
                values[Stat.GlobalCannotCrit] = 1;
                values[Stat.AttackMinimumAddedPhysicalDamage] = values[Stat.AttackMaximumAddedPhysicalDamage] = 500;
                if (extraStrikes > 0) values[Stat.MeleeAttackNumberOfSpiritStrikes] = extraStrikes;
            }
            return values;
        });

        try
        {
            SpawnedMonster Monster(uint id, int x, int y = 100)
            {
                var monster = new SpawnedMonster("tribal fury target", new(x, y, 0), 0, 0, 1_000_000)
                {
                    ObjectId = id,
                    Hostile = true,
                    NoRewards = true,
                };
                player.Area.Monsters[id] = monster;
                player.Area.Grid.Set(new(id), (x, y));
                return monster;
            }

            void AdvanceUntilHit(SpawnedMonster watched, uint lifeBefore)
            {
                double deadline = clock.At + 2;
                while (watched.Life == lifeBefore && clock.At < deadline)
                {
                    clock.At += .025;
                    f.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
                }
            }

            // 1. Baseline without Tribal Fury: Heavy Strike hits primary (109, 100) and cannot reach nearby (122, 100, dist 13 > 12).
            ushort heavyStrike = Assert.Single(f.Equip(("Heavy Strike", 1)));
            var primary = Monster(901, 109);
            var nearby = Monster(902, 122);   // distance 13 from primary (> 12 direct sweep, <= 14 base splash)
            var edge = Monster(903, 124);     // distance 15 from primary (> 14 base splash, <= 15 with +30% splash AoE)
            var far = Monster(904, 130);      // distance 21 from primary (> 15)

            Assert.True(await Game.CastGemSkill(player, heavyStrike, (100, 100), (109, 100), 901));
            AdvanceUntilHit(primary, primary.Life);
            Assert.True(primary.Life < primary.MaxLife, "Baseline Heavy Strike did not hit primary.");
            Assert.Equal(nearby.MaxLife, nearby.Life);
            Assert.Equal(edge.MaxLife, edge.Life);
            Assert.Equal(far.MaxLife, far.Life);

            // 2. With Tribal Fury (no Melee Splash Support): splashes nearby (dist 13 <= 14) at full damage without double-hitting primary.
            player.Character.Passives.Add(tribalFury!.GraphId);
            Assert.Equal(1, Game.CharacterPlayerStats(player).GetValueOrDefault(Stat.MeleeSplash));
            clock.At = Math.Max(clock.At + 1, player.NextAttackAt + .1);
            f.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            uint primaryBeforeSplash = primary.Life;
            Assert.True(await Game.CastGemSkill(player, heavyStrike, (100, 100), (109, 100), 901));
            AdvanceUntilHit(primary, primaryBeforeSplash);

            uint primaryDamage = primaryBeforeSplash - primary.Life;
            uint nearbyDamage = nearby.MaxLife - nearby.Life;
            Assert.True(primaryDamage > 0, "Primary Heavy Strike target was not hit.");
            // Unarmed base damage rolls min..max independently per victim, so compare within 5% to prove full splash
            // damage (no -40% support penalty) and no double-hit on the primary target.
            Assert.InRange(nearbyDamage, primaryDamage * 95 / 100, primaryDamage * 105 / 100);
            Assert.Equal(edge.MaxLife, edge.Life);
            Assert.Equal(far.MaxLife, far.Life);

            // 3. Allocating the two +15% melee_splash_area_of_effect_+% passives grows radius 14 -> 15 and hits edge.
            player.Character.Passives.Add(splashAoe1!.GraphId);
            player.Character.Passives.Add(splashAoe2!.GraphId);
            clock.At = Math.Max(clock.At + 1, player.NextAttackAt + .1);
            f.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            uint primaryBefore2 = primary.Life;
            Assert.True(await Game.CastGemSkill(player, heavyStrike, (100, 100), (109, 100), 901));
            AdvanceUntilHit(primary, primaryBefore2);
            Assert.True(edge.Life < edge.MaxLife, "Splash AoE passives should extend Tribal Fury splash to distance 15.");
            Assert.Equal(far.MaxLife, far.Life);

            // 4. Non-Strike melee skill (Ground Slam) must NOT splash with Tribal Fury.
            player.Area.Monsters.Clear();
            ushort groundSlam = Assert.Single(f.Equip(("Ground Slam", 1)));
            var inFront = Monster(911, 108);
            var behindPlayer = Monster(912, 98); // distance 10 from inFront (<= 15 splash radius) but behind Ground Slam's cone
            clock.At = Math.Max(clock.At + 1, player.NextAttackAt + .1);
            f.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            Assert.True(await Game.CastGemSkill(player, groundSlam, (100, 100), (108, 100), 911));
            AdvanceUntilHit(inFront, inFront.Life);
            Assert.True(inFront.Life < inFront.MaxLife, "Ground Slam did not hit its front target.");
            Assert.Equal(behindPlayer.MaxLife, behindPlayer.Life);

            // 5. Molten Strike + additional strike target with Tribal Fury splashes around the second struck target too.
            player.Area.Monsters.Clear();
            extraStrikes = 1;
            ushort molten = Assert.Single(f.Equip(("Molten Strike", 1)));
            var first = Monster(921, 109);
            var second = Monster(922, 125);
            var secondSplash = Monster(923, 131); // distance 6 from second, 22 from first
            clock.At = Math.Max(clock.At + 1, player.NextAttackAt + .1);
            f.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            Assert.True(await Game.CastGemSkill(player, molten, (100, 100), (109, 100), 921));
            AdvanceUntilHit(first, first.Life);
            Assert.True(first.Life < first.MaxLife, "Molten Strike primary target did not take melee damage.");
            Assert.True(second.Life < second.MaxLife, "Molten Strike additional strike target was not hit.");
            Assert.True(secondSplash.Life < secondSplash.MaxLife, "Molten Strike additional strike did not splash with Tribal Fury.");

            // 6. Default Attack (melee unarmed) is also a Strike skill and splashes with Tribal Fury.
            player.Area.Monsters.Clear();
            extraStrikes = 0;
            var defaultPrimary = Monster(931, 104);
            var defaultSplash = Monster(932, 114); // distance 10 from primary (<= 15)
            var defaultFar = Monster(933, 130);    // distance 26 from primary (> 15)
            clock.At = Math.Max(clock.At + 2, player.NextAttackAt + .5);
            f.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            await Game.Attack(player, new InstanceTransfer.UseSkillOnObject(931, SkillCatalog.FirstSkillId, 1, 0),
                defaultPrimary, clock.At);
            Assert.True(defaultPrimary.Life < defaultPrimary.MaxLife, "Default Attack did not hit primary target.");
            Assert.True(defaultSplash.Life < defaultSplash.MaxLife, "Default Attack did not splash with Tribal Fury.");
            Assert.Equal(defaultFar.MaxLife, defaultFar.Life);
        }
        finally
        {
            Game.AreaUpdateContext.Value = previous;
        }
    }

    [Fact]
    public void Crave_the_Slaughter_scales_attack_speed_and_per_rage_effects_by_effective_rage()
    {
        var clock = new StepClock();
        using var f = new TriggeredSummonTests.Fixture(clock);
        var player = f.Player;
        var previous = Game.AreaUpdateContext.Value;
        player.ProjectileRuntimeState = f.Session;
        f.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
        Game.AreaUpdateContext.Value = new(f.Lifetime, clock);
        try
        {
            // Real PassiveSkills graph IDs from client.db:
            // - Crave the Slaughter (AscendancyBerserker11, 24528): +2000ms rage loss delay, 1% increased attack speed per Rage.
            // - Rite of Ruin (AscendancyBerserker8, 57560): 50% increased Rage Effect.
            // - Vengeance (rage_notable2108, 53840): 1% increased Armour per Rage.
            const ushort craveTheSlaughterHash = 24528;
            const ushort riteOfRuinHash = 57560;
            const ushort vengeanceHash = 53840;

            ushort heavyStrike = Assert.Single(f.Equip(("Heavy Strike", 1)));
            player.Character.Passives.Add(craveTheSlaughterHash);
            player.Pools = Game.PoolsFor(player, 1);

            int baseStunPerRage = 0;
            int baseMovePer5Rage = 0;
            int baseArmour = 0;
            int castSpeedInstead = 0;
            using var statHook = GameEvents.PlayerStatSources.Add("Game", (p, s) =>
            {
                if (!ReferenceEquals(p, player)) return s;
                if (baseArmour != 0) s[Stat.BasePhysicalDamageReductionRating] = s.GetValueOrDefault(Stat.BasePhysicalDamageReductionRating) + baseArmour;
                if (baseStunPerRage != 0) s[Stat.StunThresholdPlusPctPerRage] = s.GetValueOrDefault(Stat.StunThresholdPlusPctPerRage) + baseStunPerRage;
                if (baseMovePer5Rage != 0) s[Stat.MovementSpeedPlusPctPer5Rage] = s.GetValueOrDefault(Stat.MovementSpeedPlusPctPer5Rage) + baseMovePer5Rage;
                if (castSpeedInstead != 0) s[Stat.RageGrantsCastSpeedInstead] = 1;
                return s;
            });

            // 1. At 0 Rage, Crave the Slaughter delays inherent loss by +2000ms (4000ms total) and grants 0% attack speed.
            var stats0 = Game.CharacterPlayerStats(player);
            Assert.Equal(1, stats0.GetValueOrDefault(Stat.AttackSpeedPlusPctPerRage));
            Assert.Equal(4000, stats0.GetValueOrDefault(Stat.VirtualRageLossDelayMs));
            Assert.Equal(0, stats0.GetValueOrDefault(Stat.AttackSpeedPlusPctFromRage));
            int baseSpeedPct = stats0.GetValueOrDefault(Stat.MainHandAttackSpeedPlusPct);
            Assert.Equal(0, baseSpeedPct);
            double baseDefaultSeconds = Game.DefaultAttackSeconds(player, AttackHand.Main, stats0);
            double baseHeavySeconds = Game.SupportedAttackSeconds(player, heavyStrike);
            var heavyPanel0 = Game.PlayerSkills(player).Single(s => s.Id == heavyStrike).Stats!.ToDictionary(kv => kv.Key, kv => kv.Value);

            // 2. At 20 Rage without Rage Effect, Crave the Slaughter grants +20% increased attack speed.
            var inputs0 = RageStats.Inputs(player, key => stats0.GetValueOrDefault(key));
            Assert.True(player.Rage.Gain(20, clock.At, inputs0));
            var stats20 = Game.CharacterPlayerStats(player);
            Assert.Equal(20, stats20.GetValueOrDefault(Stat.CurrentRage));
            Assert.Equal(20, stats20.GetValueOrDefault(Stat.EffectiveRageForPerRageEffects));
            Assert.Equal(20, stats20.GetValueOrDefault(Stat.AttackSpeedPlusPctFromRage));
            Assert.Equal(20, stats20.GetValueOrDefault(Stat.MainHandAttackSpeedPlusPct));
            double rage20DefaultSeconds = Game.DefaultAttackSeconds(player, AttackHand.Main, stats20);
            double rage20HeavySeconds = Game.SupportedAttackSeconds(player, heavyStrike);
            Assert.True(rage20DefaultSeconds < baseDefaultSeconds);
            Assert.True(rage20HeavySeconds < baseHeavySeconds);
            var heavyPanel20 = Game.PlayerSkills(player).Single(s => s.Id == heavyStrike).Stats!.ToDictionary(kv => kv.Key, kv => kv.Value);
            Assert.True(heavyPanel20[Stat.MainHandAttackSpeedPlusPct] > heavyPanel0[Stat.MainHandAttackSpeedPlusPct]);
            Assert.True(heavyPanel20[Stat.HundredTimesAttacksPerSecond] > heavyPanel0[Stat.HundredTimesAttacksPerSecond]);

            // 3. Adding Rite of Ruin (+50% Rage Effect) turns 20 Rage into 30 effective Rage, scaling all per-Rage effects.
            player.Character.Passives.Add(riteOfRuinHash);
            player.Character.Passives.Add(vengeanceHash);
            baseArmour = 1000;
            baseStunPerRage = 1;
            baseMovePer5Rage = 1;
            player.Pools = Game.PoolsFor(player, 1);

            var statsRite = Game.CharacterPlayerStats(player);
            Assert.Equal(20, statsRite.GetValueOrDefault(Stat.CurrentRage));
            Assert.Equal(30, statsRite.GetValueOrDefault(Stat.EffectiveRageForPerRageEffects));
            Assert.Equal(30, statsRite.GetValueOrDefault(Stat.AttackSpeedPlusPctFromRage));
            Assert.Equal(30, statsRite.GetValueOrDefault(Stat.MainHandAttackSpeedPlusPct));
            Assert.Equal(30, statsRite.GetValueOrDefault(Stat.StunThresholdPlusPct));
            Assert.Equal(6, statsRite.GetValueOrDefault(Stat.MovementSpeedPlusPctFromRage)); // 30 / 5 * 1 = 6%
            Assert.Equal(30, player.Pools.ArmourPct);
            Assert.Equal(1300, player.Pools.Armour);
            Assert.Equal(6, player.Pools.MovementVelocityPct);
            Assert.True(Game.SupportedAttackSeconds(player, heavyStrike) < rage20HeavySeconds);

            // 4. rage_grants_cast_speed_instead routes attack_speed_+%_from_rage into cast speed instead of attack speed.
            int baseCastSpeed = statsRite.GetValueOrDefault(Stat.CastSpeedPlusPct);
            castSpeedInstead = 1;
            var statsCast = Game.CharacterPlayerStats(player);
            Assert.Equal(30, statsCast.GetValueOrDefault(Stat.AttackSpeedPlusPctFromRage));
            Assert.Equal(baseSpeedPct, statsCast.GetValueOrDefault(Stat.MainHandAttackSpeedPlusPct));
            Assert.Equal(baseCastSpeed + 30, statsCast.GetValueOrDefault(Stat.CastSpeedPlusPct));

            // 5. After inherent loss delay (4s) + full decay (2s for 20 Rage), Rage returns to 0 and speed resets cleanly.
            castSpeedInstead = 0;
            clock.At += 7.0;
            f.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            var statsDecayed = Game.CharacterPlayerStats(player);
            Assert.Equal(0, statsDecayed.GetValueOrDefault(Stat.CurrentRage));
            Assert.Equal(0, statsDecayed.GetValueOrDefault(Stat.EffectiveRageForPerRageEffects));
            Assert.Equal(0, statsDecayed.GetValueOrDefault(Stat.AttackSpeedPlusPctFromRage));
            Assert.Equal(baseSpeedPct, statsDecayed.GetValueOrDefault(Stat.MainHandAttackSpeedPlusPct));
        }
        finally
        {
            Game.AreaUpdateContext.Value = previous;
        }
    }

    sealed class StepClock : IPlayerUpdateSchedule
    {
        public double At = 1;
        public double Now(Player player) => At;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            stopping.ThrowIfCancellationRequested();
            At += milliseconds / 1000d;
            return Task.CompletedTask;
        }
    }
}
