using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public sealed class MoltenStrikeTests
{
    [Fact]
    public void Molten_strike_resolves_profile_scales_radius_9_publishes_panel_stats_and_isolates_melee_vs_projectile_damage()
    {
        var skills = new SkillCatalog();
        int effect = skills.EffectRow("MoltenStrike");
        Assert.True(effect >= 0 && skills.IsProjectile(effect) && skills.IsStrike(effect));
        var proj = skills.ProjectileFor(effect);
        Assert.NotNull(proj);
        Assert.Equal("Metadata/Projectiles/MoltenStrikeChunk", proj.Value.Id);
        Assert.Equal(750, proj.Value.Speed);

        // Replicate real MoltenStrike StatSet shape without injecting active_skill_base_area_of_effect_radius.
        var level1Stats = skills.EffectStats(effect, 1, 0).ToDictionary(s => s.Item1, s => s.Item2);
        Assert.Null(Program.Stats.Key("active_skill_base_area_of_effect_radius") is uint baseAreaKey && level1Stats.ContainsKey(baseAreaKey) ? baseAreaKey : null);
        int GetLevel1(string name) => Program.Stats.Key(name) is uint k ? level1Stats.GetValueOrDefault(k) : 0;
        var profile = GatewayProjectileSkillProfiles.Resolve("MoltenStrike", proj.Value.Id, GetLevel1, 750, attack: true);
        Assert.True(profile.Lobbed);
        Assert.False(profile.DirectDamage);
        Assert.True(profile.CannotPierce && profile.CannotFork && profile.CannotSplit && profile.CannotChain);
        Assert.NotNull(profile.EndArea);
        Assert.Equal(9, profile.EndArea.Radius);
        Assert.Contains(ProjectileEndReason.Landing, profile.EndAreaReasons!);
        Assert.Contains(ProjectileEndReason.Terrain, profile.EndAreaReasons!);
        Assert.Equal(4, profile.Count);

        string path = Path.Combine(Path.GetTempPath(), $"localexile-ms-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "ms", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "MoltenStrikeChar", PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        try
        {
            var sword = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword1")!);
            var moltenGem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemMoltenStrike")!);
            var meleePhys = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SupportGemMeleePhysicalDamage")!);
            var concEffect = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SupportGemConcentratedEffect")!);

            // Unlinked Q0 Molten Strike in a White socket (no +10 colour quality bonus -> exact -60% projectile final).
            Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, sword with
            {
                Sockets = [new ItemSocket(SocketColour.White, moltenGem)], Links = [1],
            });
            foreach (string name in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements",
                "virtual_intelligence_for_requirements" }) conn.PlayerStats[Program.Stats.Key(name)!.Value] = 1000;

            var granted = Game.PlayerSkills(conn).Single(s => Game.SkillEffectId(conn, s.Id) == "MoltenStrike");
            Assert.NotNull(granted.Stats);
            var skillMap = granted.Stats!.ToDictionary(kv => kv.Item1, kv => kv.Item2);
            Assert.Equal(4, skillMap[Program.Stats.Key("virtual_total_number_of_projectiles")!.Value]);
            Assert.Equal(0, skillMap[Program.Stats.Key("total_skill_area_of_effect_radius_+%_including_final")!.Value]);
            Assert.Equal(9, skillMap[Program.Stats.Key("active_skill_area_of_effect_radius")!.Value]);
            Assert.True(Game.ProjectilePathAvailable("test", conn, granted.Id, effect));

            var baseMeleeHit = Game.SupportedHit(conn, granted.Id);
            var baseProjHit = Game.SupportedProjectileHit(conn, granted.Id, area: true);
            Assert.Equal(baseMeleeHit.MinTotal * 0.4, baseProjHit.MinTotal, 4);

            var meleeIgniteBase = Game.SupportedAilmentRows(conn, granted.Id, "main_hand", projectile: false)
                .ToDictionary(r => r.Stat, r => r.Value)["minimum_ignite_damage_to_deal_per_minute_from_non_critical_main_hand_damage"];
            var projIgniteBase = Game.SupportedAilmentRows(conn, granted.Id, "main_hand", projectile: true)
                .ToDictionary(r => r.Stat, r => r.Value)["minimum_ignite_damage_to_deal_per_minute_from_non_critical_main_hand_damage"];
            Assert.InRange(projIgniteBase, (int)(meleeIgniteBase * 0.4) - 2, (int)(meleeIgniteBase * 0.4) + 2);

            // Q20 + Melee Physical + Concentrated Effect: melee only gets Melee Physical, balls only get Conc Effect + -50% proj final.
            Game.ItemsOf(conn).Remove(PlayerInventories.Weapon1, Game.ItemsOf(conn).Items(PlayerInventories.Weapon1).Single().Id);
            Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, sword with
            {
                Sockets =
                [
                    new ItemSocket(SocketColour.White, moltenGem with { Quality = 20 }),
                    new ItemSocket(SocketColour.White, meleePhys),
                    new ItemSocket(SocketColour.White, concEffect)
                ],
                Links = [3],
            });
            var linked = Game.PlayerSkills(conn).Single(s => Game.SkillEffectId(conn, s.Id) == "MoltenStrike");
            var linkedMap = linked.Stats!.ToDictionary(kv => kv.Item1, kv => kv.Item2);
            Assert.True(linkedMap[Program.Stats.Key("total_skill_area_of_effect_radius_+%_including_final")!.Value] < 0);

            var linkedMeleeInputs = Game.SupportedHitInputsFor(conn, linked.Id, projectile: false);
            var linkedProjInputs = Game.SupportedHitInputsFor(conn, linked.Id, area: true, projectile: true);
            Assert.NotNull(linkedMeleeInputs);
            Assert.NotNull(linkedProjInputs);
            Assert.True(linkedMeleeInputs.Melee && !linkedMeleeInputs.Projectile && !linkedMeleeInputs.Area);
            Assert.True(!linkedProjInputs.Melee && linkedProjInputs.Projectile && linkedProjInputs.Area);

            var linkedMelee = Game.SupportedHit(conn, linked.Id);
            var linkedProj = Game.SupportedProjectileHit(conn, linked.Id, area: true);
            Assert.True(linkedMelee.MinTotal > baseMeleeHit.MinTotal); // Melee Physical boosted melee
            Assert.True(linkedProj.MinTotal > baseProjHit.MinTotal);   // Q20 (-50% vs -60%) + Conc Effect boosted balls, without Melee Physical
            // Confirm Melee Physical did not leak into balls: compare against Q20 + Conc Effect ratio.
            Assert.True(linkedProj.MinTotal < linkedMelee.MinTotal * 0.65);

            // Phase 2: Vaal Molten Strike profile (9 balls, 8 ground chains, GroundBounceKeepsRange, base radius 9+4=13) + Wildfire stats.
            int vaalEffect = skills.EffectRow("VaalMoltenStrike");
            Assert.True(vaalEffect >= 0 && skills.BehaviourVariation(vaalEffect) == 2);
            var vaalProj = skills.ProjectileFor(vaalEffect);
            Assert.NotNull(vaalProj);
            Assert.Equal("Metadata/Projectiles/MoltenStrikeChunk", vaalProj.Value.Id);
            var vaalStats = skills.EffectStats(vaalEffect, 1, 0).ToDictionary(s => s.Item1, s => s.Item2);
            int GetVaal(string name) => Program.Stats.Key(name) is uint k ? vaalStats.GetValueOrDefault(k) : 0;
            var vaalProfile = GatewayProjectileSkillProfiles.Resolve("VaalMoltenStrike", vaalProj.Value.Id, GetVaal, 750, attack: true);
            Assert.Equal(9, vaalProfile.Count);
            Assert.False(vaalProfile.CannotChain);
            Assert.Equal(0, vaalProfile.Chain);
            Assert.Equal(8, vaalProfile.GroundChains);
            Assert.True(vaalProfile.GroundBounceKeepsRange);
            Assert.NotNull(vaalProfile.EndArea);
            Assert.Equal(13, vaalProfile.EndArea.Radius);

            // Wildfire threshold stats on base MoltenStrike: -50% final projectile count (4 -> 2) + 1 ground chain keeping range.
            int GetWildfire(string name) => name switch
            {
                "molten_strike_projectiles_chain_when_impacting_ground" => 1,
                "molten_strike_chain_count_+" => 1,
                "threshold_jewel_molten_strike_damage_projectile_count_+%_final" => -50,
                _ => GetLevel1(name)
            };
            var wildfireProfile = GatewayProjectileSkillProfiles.Resolve("MoltenStrike", proj.Value.Id, GetWildfire, 750, attack: true);
            Assert.Equal(2, wildfireProfile.Count);
            Assert.False(wildfireProfile.CannotChain);
            Assert.Equal(1, wildfireProfile.GroundChains);
            Assert.True(wildfireProfile.GroundBounceKeepsRange);

            // Vaal Molten Strike gem equipped: VaalExecutor + panel stats (9 balls, 8 chains, radius 13).
            var twoHandSword = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/TwoHandWeapons/TwoHandSwords/TwoHandSword1")!);
            var vaalGem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemVaalMoltenStrike")!);
            Game.ItemsOf(conn).Remove(PlayerInventories.Weapon1, Game.ItemsOf(conn).Items(PlayerInventories.Weapon1).Single().Id);
            Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, twoHandSword with
            {
                Sockets =
                [
                    new ItemSocket(SocketColour.White, vaalGem),
                    new ItemSocket(SocketColour.White, moltenGem with { GemVariant = 1 })
                ],
                Links = [1, 1],
            });
            var grantedVaal = Game.PlayerSkills(conn).Single(s => Game.SkillEffectId(conn, s.Id) == "VaalMoltenStrike");
            Assert.True(Game.VaalExecutor(conn, vaalEffect));
            Assert.True(Game.ProjectilePathAvailable("test", conn, grantedVaal.Id, vaalEffect));
            var vaalMap = grantedVaal.Stats!.ToDictionary(kv => kv.Item1, kv => kv.Item2);
            Assert.Equal(9, vaalMap[Program.Stats.Key("virtual_total_number_of_projectiles")!.Value]);
            Assert.Equal(8, vaalMap[Program.Stats.Key("virtual_number_of_chains_for_projectiles")!.Value]);
            Assert.Equal(0, vaalMap[Program.Stats.Key("virtual_projectiles_cannot_chain")!.Value]);
            Assert.Equal(13, vaalMap[Program.Stats.Key("active_skill_area_of_effect_radius")!.Value]);

            // Molten Strike of the Zenith (AltX) equipped on 2H sword: 3 base balls, path available.
            var zenithSkill = Game.PlayerSkills(conn).Single(s => Game.SkillEffectId(conn, s.Id) == "MoltenStrikeAltX");
            Assert.True(Game.ProjectilePathAvailable("test", conn, zenithSkill.Id, skills.EffectRow("MoltenStrikeAltX")));
            var zenithMap = zenithSkill.Stats!.ToDictionary(kv => kv.Item1, kv => kv.Item2);
            Assert.Equal(3, zenithMap[Program.Stats.Key("virtual_total_number_of_projectiles")!.Value]);
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            lifetime.Stop(_ => { });
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }

    [Fact]
    public async Task Molten_strike_gates_on_melee_hit_distributes_targets_samples_270_sector_and_allows_overlapping_landing_hits()
    {
        // 1. Target selection, whiff gate, distribution, 270° sector and 0.633 s flight timing.
        var candidates = new (uint Id, (int X, int Y) Pos, int Size)[]
        {
            (1, (20, 0), 0),
            (2, (8, 1), 0),
        };
        Assert.Null(MoltenStrikeGeometry.SelectGroundStrikeTarget(candidates, (100, 100), (110, 100), 11));
        Assert.Equal((uint)2, MoltenStrikeGeometry.SelectGroundStrikeTarget(candidates, (0, 0), (30, 0), 11));

        Assert.Equal([2, 2, 1], MoltenStrikeGeometry.DistributeCount(5, 3));
        Assert.Empty(MoltenStrikeGeometry.DistributeCount(4, 0));

        var (minDist, maxDist) = MoltenStrikeGeometry.DistanceBounds(radiusPercent: 0, projectileSpeedPct: 0, chainingRangePct: 0);
        Assert.Equal(2.0, minDist);
        Assert.Equal(25.0, maxDist);

        var (clampedMin, clampedMax) = MoltenStrikeGeometry.DistanceBounds(radiusPercent: 0, projectileSpeedPct: -90, chainingRangePct: 0);
        Assert.Equal(2.0, clampedMin);
        Assert.Equal(2.0, clampedMax);

        Assert.Equal(40, MoltenStrikeGeometry.EffectiveAnimationSpeedPct(0, 4));
        Assert.Equal(60, MoltenStrikeGeometry.EffectiveAnimationSpeedPct(20, 4));

        var rng = new Random(42);
        var forward = new ProjectileVector(1, 0);
        double minCos135 = Math.Cos(MoltenStrikeGeometry.SectorHalfAngleRadians); // -0.7071...
        int baseAnimPct = MoltenStrikeGeometry.EffectiveAnimationSpeedPct(0, 4);
        double expectedMeanFlight = 0.633 / (1 + baseAnimPct / 100.0);
        for (int i = 0; i < 200; i++)
        {
            var (target, speed) = MoltenStrikeGeometry.SampleBall(new(10, 0), forward, minDist, maxDist, 0.633, baseAnimPct, rng);
            var delta = target - new ProjectileVector(10, 0);
            Assert.InRange(delta.Length, 2.0 - 1e-6, 25.0 + 1e-6);
            double dot = delta.Unit.Dot(forward);
            Assert.True(dot >= minCos135 - 1e-9, $"Sample fell inside 90° backward blind sector: dot={dot}");
            double flightSeconds = delta.Length / speed;
            Assert.InRange(flightSeconds, expectedMeanFlight * 0.9 - 1e-6, expectedMeanFlight * 1.1 + 1e-6);
        }

        // 2. End-to-end ProjectileSession release + overlapping area hits on landing without duplicate server 0x2bb objects.
        string path = Path.Combine(Path.GetTempPath(), $"localexile-ms-sim-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "ms-sim", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "MoltenSimChar", PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        try
        {
            var sword = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword1")!);
            var moltenGem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemMoltenStrike")!);
            Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, sword with
            {
                Sockets = [new ItemSocket(SocketColour.White, moltenGem)], Links = [1],
            });
            foreach (string name in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements",
                "virtual_intelligence_for_requirements", "global_always_hit" })
                conn.PlayerStats[Program.Stats.Key(name)!.Value] = 1000;
            conn.PlayerStats[Program.Stats.Key("global_cannot_crit")!.Value] = 1;

            var granted = Game.PlayerSkills(conn).Single(s => Game.SkillEffectId(conn, s.Id) == "MoltenStrike");
            var gemInfo = Game.GemSkillEffects(conn).Single(g => g.Id == granted.Id);

            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            using var writer = new System.Net.Sockets.TcpClient();
            writer.Connect((System.Net.IPEndPoint)listener.LocalEndpoint);
            using var reader = listener.AcceptTcpClient();
            var session = new Game.ProjectileSession("test", writer.GetStream(),
                new GameServer.Core.Protocol.Login.ConnectionCiphers(new byte[64], false), conn);
            conn.ProjectileRuntime = session;

            // Zero struck targets -> zero projectiles emitted.
            int skipped = Game.EmitMoltenStrikeBallsLocked(conn, session, granted.Id, gemInfo, (0, 0), (10, 0),
                new SkillCriticalRoll(0), 1, 0, 0, 1, [], new Random(1));
            Assert.Equal(0, skipped);
            Assert.Empty(session.Runtime.Projectiles);

            // Multi-target strike splits total balls evenly across struck targets.
            int splitLaunched = Game.EmitMoltenStrikeBallsLocked(conn, session, granted.Id, gemInfo, (0, 0), (12, 0),
                new SkillCriticalRoll(0), 2, 0, 0, 1, [new(12, 0), new(12, 20)], new Random(3));
            Assert.Equal(4, splitLaunched);
            Assert.Equal(2, session.Runtime.Projectiles.Count(p => Math.Abs(p.Use.Origin.Y - 0) < 0.01));
            Assert.Equal(2, session.Runtime.Projectiles.Count(p => Math.Abs(p.Use.Origin.Y - 20) < 0.01));
            session.Runtime.Clear();

            // Negative projectile speed forces all 4 balls to clamp at 2 cells from origin (12, 0), overlapping monster at (14, 0) within radius 9.
            conn.PlayerStats[Program.Stats.Key("projectile_speed_+%")!.Value] = -95;
            var mob = new SpawnedMonster("target", new(14, 0, 0), 0, 0, 100000) { Hostile = true, Life = 100000 };
            conn.Monsters[100] = mob;

            int launched = Game.EmitMoltenStrikeBallsLocked(conn, session, granted.Id, gemInfo, (0, 0), (12, 0),
                new SkillCriticalRoll(0), 3, 0, 0, 1, [new(12, 0)], new Random(7));
            Assert.Equal(4, launched);
            Assert.Equal(4, session.Runtime.Projectiles.Count);
            Assert.Empty(conn.ProjectileObjects); // ClientPredicted: no duplicate server 0x2bb object created

            // 4 projectiles -> +40% animation speed -> max flight ~0.497 s, so +0.55 s finishes all 4 balls.
            session.AdvanceTo(session.Runtime.Now + ProjectileRuntime.Time(0.55));
            Assert.Empty(session.Runtime.Projectiles);
            Assert.True(mob.Life < 100000);
            // Without crits, a single ball deals at most singleBallMax; life loss exceeding singleBallMax proves overlapping hits from the same volley.
            var singleBallMax = Game.SupportedProjectileHit(conn, granted.Id, area: true).MaxTotal;
            Assert.True(100000 - mob.Life > singleBallMax + 1, $"Expected multiple overlapping molten ball hits (loss={100000 - mob.Life}, singleMax={singleBallMax})");

            var twoHandSword = new WeaponProfile(Shield: false, DualWield: false, Staff: false, Bow: false, DualClaws: false,
                TwoHand: true, Melee: true, TwoHandMelee: true, Sword: true);
            var dexBounds = Game.Animations.DefaultAttackContactBounds("Metadata/Characters/Dex/Dex.ao", twoHandSword, 0, 0.6389);
            Assert.NotNull(dexBounds);
            Assert.Equal("attack_2hsword_01a", dexBounds.Value.Animation);
            Assert.InRange(dexBounds.Value.EarliestSeconds, 0.304, 0.306);
            Assert.InRange(dexBounds.Value.LatestSeconds, 0.481, 0.483);

            double contactDelay;
            double earliestContact;
            string? contactClip;
            double contactFraction;
            lock (conn.Monsters)
                contactDelay = Game.MoltenStrikeContactDelay(conn, gemInfo.Effect, 0.645, 0,
                    out contactClip, out contactFraction, out earliestContact);
            Assert.Equal("attack_sword_01a", contactClip);
            // Not clamped down by EarlyAllowance (0.645 * 0.75 - 0.05 = 0.434): keeps full 01a contact beyond 0.45 s.
            Assert.True(contactDelay > 0.45, $"Expected full 01a defaultAttack contact delay (>0.45 s), got {contactDelay:0.000}");
            Assert.True(earliestContact > 0 && earliestContact < contactDelay);

            // Action replacement before earliestContact cancels ExecuteMoltenStrikeSwing cleanly (no phantom melee hit or balls).
            uint lifeBeforeReplaced = mob.Life;
            conn.ActionCount = 10;
            await Game.ExecuteMoltenStrikeSwing("test", writer.GetStream(),
                new GameServer.Core.Protocol.Login.ConnectionCiphers(new byte[64], false),
                conn, granted.Id, gemInfo, (0, 0), (14, 0), conn.ActionInterruptionGeneration,
                100, 4, actionToken: 9, cooldownUse: null, new SkillCriticalRoll(0), swingAt: null,
                repeat: 0, totalRepeats: 0, attackSeconds: 0.64, repeatDelay: 0, ruthless: 1);
            Assert.Empty(session.Runtime.Projectiles);
            Assert.Equal(lifeBeforeReplaced, mob.Life);

            // A Move order after earliestContact cancels backswing rather than windup: melee hit + 4 balls still execute.
            session.Runtime.Clear();
            double walkAt = conn.Clock.Elapsed.TotalSeconds;
            lock (conn.Monsters)
            {
                conn.Walk.Teleport(4, 0, walkAt);
                conn.Walk.Order(20, 0, walkAt);
            }
            conn.ActionCount = 11;
            await Game.ExecuteMoltenStrikeSwing("test", writer.GetStream(),
                new GameServer.Core.Protocol.Login.ConnectionCiphers(new byte[64], false),
                conn, granted.Id, gemInfo, (4, 0), (14, 0), conn.ActionInterruptionGeneration,
                100, 5, actionToken: 10, cooldownUse: null, new SkillCriticalRoll(0), swingAt: walkAt - 0.60,
                repeat: 0, totalRepeats: 0, attackSeconds: 0.645, repeatDelay: 0, ruthless: 1);
            Assert.Equal(4, session.Runtime.Projectiles.Count);
            Assert.True(mob.Life < lifeBeforeReplaced);

            // Phase 2a: Molten Strike of the Zenith (AltX) 5th-attack counter (0..4) + +5 balls + +600% more damage.
            session.Runtime.Clear();
            var twoHandSwordItem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/TwoHandWeapons/TwoHandSwords/TwoHandSword1")!);
            var vaalGemItem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemVaalMoltenStrike")!);
            Game.ItemsOf(conn).Remove(PlayerInventories.Weapon1, Game.ItemsOf(conn).Items(PlayerInventories.Weapon1).Single().Id);
            Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, twoHandSwordItem with
            {
                Sockets =
                [
                    new ItemSocket(SocketColour.White, moltenGem with { GemVariant = 1 }),
                    new ItemSocket(SocketColour.White, vaalGemItem)
                ],
                Links = [1, 1],
            });
            var zenithGranted = Game.PlayerSkills(conn).Single(s => Game.SkillEffectId(conn, s.Id) == "MoltenStrikeAltX");
            var zenithGem = Game.GemSkillEffects(conn).Single(g => g.Id == zenithGranted.Id);
            var ciphers = new GameServer.Core.Protocol.Login.ConnectionCiphers(new byte[64], false);
            double pastSwing = conn.Clock.Elapsed.TotalSeconds - 1.0;
            for (int attack = 1; attack <= 4; attack++)
            {
                await Game.ScheduleMoltenStrike("test", writer.GetStream(), ciphers, conn, zenithGranted.Id, zenithGem,
                    (4, 0), (14, 0), conn.ActionInterruptionGeneration, targetId: 100, sequence: (ushort)(10 + attack),
                    actionToken: conn.ActionCount, swingAt: pastSwing);
                Assert.Equal(attack, conn.MoltenStrikeAttackCounts[zenithGranted.Id]);
                Assert.Equal(3, session.Runtime.Projectiles.Count);
                session.Runtime.Clear();
            }
            Assert.True(conn.MoltenStrikeFifthBuffs.ContainsKey(zenithGranted.Id));
            uint lifeBeforeNormalZenith = mob.Life;
            // Measure a single normal Zenith ball hit vs a 5th-attack Zenith ball hit (same non-crit, -95% speed overlap).
            await Game.ScheduleMoltenStrike("test", writer.GetStream(), ciphers, conn, zenithGranted.Id, zenithGem,
                (4, 0), (14, 0), conn.ActionInterruptionGeneration, targetId: 100, sequence: 15,
                actionToken: conn.ActionCount, swingAt: pastSwing);
            Assert.Equal(0, conn.MoltenStrikeAttackCounts[zenithGranted.Id]);
            Assert.True(conn.MoltenStrikeFifthBuffs.ContainsKey(zenithGranted.Id)); // Persistent counter buff stays active
            Assert.Equal(8, session.Runtime.Projectiles.Count); // 3 base + 5 on 5th attack
            var zenithProjMax = Game.SupportedProjectileHit(conn, zenithGranted.Id, area: true).MaxTotal;
            uint lifeBeforeFifthLand = mob.Life;
            session.AdvanceTo(session.Runtime.Now + ProjectileRuntime.Time(0.60));
            Assert.Empty(session.Runtime.Projectiles);
            uint fifthBallsDamage = lifeBeforeFifthLand - mob.Life;
            // 8 balls * 7x (+600% more) easily exceeds 8 * singleNormalMax.
            Assert.True(fifthBallsDamage > zenithProjMax * 8 * 4,
                $"Expected 5th-attack +600% more projectile damage (dealt={fifthBallsDamage}, normalSingleMax={zenithProjMax})");

            // Phase 2b: Vaal Molten Strike emits 9 balls even on melee whiff, keeps ground chains (8), and resists cancellation.
            session.Runtime.Clear();
            var vaalGranted = Game.PlayerSkills(conn).Single(s => Game.SkillEffectId(conn, s.Id) == "VaalMoltenStrike");
            var vaalGemInfo = Game.GemSkillEffects(conn).Single(g => g.Id == vaalGranted.Id);
            lock (conn.Monsters)
            {
                Game.RefreshVaalSouls(conn);
                for (int s = 0; s < 15; s++) conn.VaalSouls.Gain();
            }
            Assert.True(Game.TryBeginSkillCooldownUse(conn, vaalGranted.Id, vaalGemInfo.Effect, null, false,
                () => conn.ActionCount++, out var vaalTicket));
            Assert.NotNull(conn.VaalUse);
            int vaalAction = conn.ActionCount;
            conn.ActionCount = vaalAction + 5; // Simulate later action: cannot_cancel_skill_before_contact_point keeps Vaal swing alive.
            await Game.ExecuteMoltenStrikeSwing("test", writer.GetStream(), ciphers, conn, vaalGranted.Id, vaalGemInfo,
                (4, 0), (40, 0), conn.ActionInterruptionGeneration, 0, 20, actionToken: vaalAction, cooldownUse: vaalTicket,
                new SkillCriticalRoll(0), swingAt: conn.Clock.Elapsed.TotalSeconds - 1.0,
                repeat: 0, totalRepeats: 0, attackSeconds: 0.70, repeatDelay: 0, ruthless: 1);
            Assert.Equal(9, session.Runtime.Projectiles.Count);
            Assert.All(session.Runtime.Projectiles, p =>
            {
                Assert.Equal(8, p.Profile.GroundChains);
                Assert.True(p.Profile.GroundBounceKeepsRange);
            });
        }
        finally
        {
            conn.ProjectileRuntime?.Invalidate();
            PlayerInventories.Forget(conn.Character!);
            lifetime.Stop(_ => { });
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
