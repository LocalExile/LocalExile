using System.Net;
using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Modding;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public sealed class LeapSlamTests
{
    static uint Key(string name) => Program.Stats.Key(name)
        ?? throw new InvalidOperationException($"Missing stat {name}");

    sealed class TestGame : IDisposable
    {
        readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
        readonly IModuleLifetime? previousLifetime = Game.CombatLifetime;
        readonly ModuleLifetime lifetime = new("Game");
        public ClientState Connection { get; }

        public TestGame()
        {
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
            // TestEnvironment already opened the shared scratch save store; this fixture needs no database of its own.
            Connection = new(new(1, "leap-slam-test", 256, 256, 100, 100, (_, _) => true, []), 1, 1)
            {
                Character = $"LeapSlam-{Guid.NewGuid():N}", PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
                ClassRow = SkillCatalog.WitchRow,
                Experience = CharacterStats.ExperienceFor(100),
                Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
            };
            Connection.Clock.Start();
            foreach (string name in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements",
                "virtual_intelligence_for_requirements" }) Connection.PlayerStats[Key(name)] = 1000;
        }

        public void Equip(bool multistrike = false)
        {
            var swordBase = Program.Items.Find("Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword1");
            var gemBase = Program.Items.Find("Metadata/Items/Gems/SkillGemLeapSlam");
            Assert.NotNull(swordBase);
            Assert.NotNull(gemBase);
            var sword = ItemCatalog.Create(swordBase);
            var gem = ItemCatalog.Create(gemBase);
            ItemSocket[] sockets = [new(SocketColour.White, gem), new(SocketColour.White, gem with { GemVariant = 1 })];
            if (multistrike)
            {
                var cleave = Program.Items.Find("Metadata/Items/Gems/SkillGemCleave");
                var support = Program.Items.Find("Metadata/Items/Gems/SupportGemMultistrike");
                Assert.NotNull(cleave);
                Assert.NotNull(support);
                sockets = [new(SocketColour.White, gem), new(SocketColour.White, ItemCatalog.Create(cleave)),
                    new(SocketColour.White, ItemCatalog.Create(support))];
            }
            Game.ItemsOf(Connection).Add(PlayerInventories.Weapon1, 0, 0, sword with
            {
                Sockets = sockets,
                Links = multistrike ? [1, 2] : [1, 1],
            });
        }

        public void Dispose()
        {
            Connection.ProjectileRuntime?.Invalidate();
            Connection.Closed = true;
            Connection.RetireOwnedCombat();
            PlayerInventories.Forget(Connection.Character!);
            lifetime.Stop(_ => { });
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
        }
    }

    [Fact]
    public async Task Leap_slam_uses_native_landing_and_animation_phases_and_one_paid_attack_hits_every_landing_target()
    {
        var skills = new SkillCatalog();
        int effect = skills.EffectRow("LeapSlam"), groundbreaking = skills.EffectRow("LeapSlamAltX");
        Assert.True(effect >= 0 && groundbreaking >= 0);
        foreach (int row in new[] { effect, groundbreaking })
        {
            Assert.Equal(LeapSlam.Action, skills.ActionHash(row));
            Assert.True(skills.IsAttack(row) && skills.IsMelee(row) && skills.IsMovement(row) && skills.IsTravel(row));
            Assert.False(skills.IsProjectile(row));
        }
        var own = skills.EffectStats(effect, 1).ToDictionary(s => s.Item1, s => s.Item2);
        var altOwn = skills.EffectStats(groundbreaking, 1).ToDictionary(s => s.Item1, s => s.Item2);
        Assert.Equal(550, own[Key(SkillAttackTiming.AdditionalBaseAttackTime)]);
        Assert.Equal(0, altOwn.GetValueOrDefault(Key(SkillAttackTiming.AdditionalBaseAttackTime)));
        Assert.Equal(15, own[Key("active_skill_base_area_of_effect_radius")]);
        Assert.Equal(25, altOwn[Key("active_skill_base_area_of_effect_radius")]);
        Assert.Equal(0, skills.AttackSpeedMultiplier(effect, 1));
        Assert.Equal(-30, skills.AttackSpeedMultiplier(groundbreaking, 1));
        var speed = new Dictionary<uint, int> { [StatKeys.MainHandAttackSpeedPct] = 0 };
        var weapon = new SkillCatalog.Unarmed(1000, 1, 1, 8);
        Assert.Equal(1.55, SkillAttackTiming.ForSkill(skills, effect, 1, weapon, speed, own), 6);
        Assert.Equal(1 / .7, SkillAttackTiming.ForSkill(skills, groundbreaking, 1, weapon, speed, altOwn), 6);
        speed[StatKeys.MainHandAttackSpeedPct] = 100;
        Assert.Equal(.775, SkillAttackTiming.ForSkill(skills, effect, 1, weapon, speed, own), 6);

        // Native distance subtracts one before clamping, then rounds each component; a horizontal 60-cell cap lands at 61.
        Assert.Equal((161, 100), LeapSlam.Target((100, 100), (200, 100), 0));
        Assert.Equal((130, 100), LeapSlam.Target((100, 100), (200, 100), 2));
        Assert.Equal((39, 100), LeapSlam.Target((100, 100), (0, 100), 0));
        Assert.Equal((70, 100), LeapSlam.Target((100, 100), (0, 100), 2));
        Assert.Equal((36, 48), LeapSlam.Target((0, 0), (90, 120), 0));
        Assert.Equal((18, 24), LeapSlam.Target((0, 0), (90, 120), 2));
        Assert.Equal((161, 100), LeapSlam.Target((100, 100), (161, 100), 0));
        Assert.Equal((100, 100), LeapSlam.Target((100, 100), (100, 100), 0));

        var barriers = new HashSet<(int X, int Y)>();
        var terrain = new AreaLayout(1, "leap-terrain", 128, 128, 32, 32, (x, _) => x < 40 || x >= 55, [])
        {
            Clearance = (x, _, size) => size == 1 && (x < 40 || x >= 55), Sight = (_, _) => 2,
        };
        Assert.Equal((80, 32), LeapSlam.Landing(terrain, barriers, (32, 32), (80, 32), 0));
        Assert.Equal((32, 32), LeapSlam.Landing(terrain, barriers, (80, 32), (32, 32), 0));
        barriers.Add((80, 32));
        Assert.Equal((79, 32), LeapSlam.Landing(terrain, barriers, (32, 32), (80, 32), 0));
        barriers.Clear();
        var wall = terrain with { Clearance = (_, _, size) => size == 1, Sight = (x, _) => x == 54 ? 1 : 2 };
        Assert.Equal((53, 32), LeapSlam.Landing(wall, barriers, (32, 32), (80, 32), 0));
        Assert.Equal((32, 32), LeapSlam.Landing(terrain with { Clearance = (_, _, _) => false }, barriers, (32, 32), (80, 32), 0));

        using var game = new TestGame();
        game.Equip();
        var conn = game.Connection;
        conn.PlayerStats[Key("global_always_hit")] = 1;
        conn.PlayerStats[Key("global_cannot_crit")] = 1;
        var gem = Game.GemSkillEffects(conn).Single(g => skills.EffectId(g.Effect) == "LeapSlam");
        var alt = Game.GemSkillEffects(conn).Single(g => skills.EffectId(g.Effect) == "LeapSlamAltX");
        Assert.True(Game.IsLeapSlam(conn, gem.Effect) && Game.IsLeapSlam(conn, alt.Effect));
        var granted = Game.PlayerSkills(conn).Single(g => g.Id == gem.Id);
        Assert.NotNull(granted.Stats);
        var panel = granted.Stats.ToDictionary(s => s.Item1, s => s.Item2);
        var altGranted = Game.PlayerSkills(conn).Single(g => g.Id == alt.Id);
        Assert.NotNull(altGranted.Stats);
        var altPanel = altGranted.Stats.ToDictionary(s => s.Item1, s => s.Item2);
        Assert.Equal(0, panel[StatKeys.SkillBehaviourVariation]);
        Assert.Equal(2, altPanel[StatKeys.SkillBehaviourVariation]);
        Assert.Equal(Game.WeaponFor(conn).AttackMs + 550, panel[StatKeys.MainHandTotalAttackDurationMs]);
        double seconds = Game.SupportedAttackSeconds(conn, gem.Id);
        Assert.Equal((int)Math.Round(100 / seconds), panel[StatKeys.HundredTimesAttacksPerSecond]);

        string ao = Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? skills.ClassFor(conn.ClassRow).AnimatedObject;
        var profile = Game.WeaponProfileFor(conn, activeSet: true);
        var leave = Game.Animations.SkillAnimationEventBounds(ao, profile, "leapslam", "leave_ground");
        var land = Game.Animations.SkillAnimationEventBounds(ao, profile, "leapslam", "hit_ground");
        Assert.NotNull(leave);
        Assert.NotNull(land);
        Assert.Equal(leave.Value.Name, land.Value.Name);
        Assert.True(leave.Value.EarliestMs > 10 && land.Value.EarliestMs > leave.Value.EarliestMs);
        var timing = Game.LeapSlamTiming(conn, gem.Id, gem.Effect, seconds);
        var altTiming = Game.LeapSlamTiming(conn, alt.Id, alt.Effect, seconds);
        double seek = leave.Value.EarliestMs - 10;
        Assert.Equal(leave.Value.Name, timing.Animation);
        Assert.Equal(10d / leave.Value.LengthMs * seconds, timing.Leave, 9);
        Assert.Equal((land.Value.EarliestMs - seek) / leave.Value.LengthMs * seconds, timing.Land, 9);
        Assert.Equal((leave.Value.LengthMs - seek) / leave.Value.LengthMs * seconds, timing.End, 9);
        Assert.Equal((double)leave.Value.EarliestMs / leave.Value.LengthMs * seconds, altTiming.Leave, 9);
        Assert.Equal((double)land.Value.EarliestMs / leave.Value.LengthMs * seconds, altTiming.Land, 9);
        Assert.Equal(seconds, altTiming.End, 9);
        var shortStartup = LeapSlam.Timing("short-startup", 100, 5, 80, 2, skipStartup: true);
        Assert.Equal(.1, shortStartup.Leave, 9);
        Assert.Equal(1.6, shortStartup.Land, 9);
        Assert.Equal(2, shortStartup.End, 9);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var writer = new TcpClient();
        writer.Connect((IPEndPoint)listener.LocalEndpoint);
        using var reader = listener.AcceptTcpClient();
        var stream = writer.GetStream();
        var ciphers = new ConnectionCiphers(new byte[64], false);
        var session = new Game.ProjectileSession("leap-test", stream, ciphers, conn);
        conn.ProjectileRuntime = session;
        session.AdvanceTo(ProjectileRuntime.Time(1000));
        double at = Game.ProjectileWorldTime(conn);
        conn.Walk.Teleport(100, 100, at);
        var from = conn.Walk.Position(at);
        var aim = (X: 150, Y: 100);
        var stationary = Game.PlanLeapSlam(conn, gem.Id, gem.Effect, from, at);
        Assert.NotNull(stationary);
        Assert.Equal(from, stationary.Landing);
        uint cost = (uint)Game.SupportedStats(conn, gem.Id)[Key("mana_cost")];
        Assert.True(cost > 0);

        conn.PlayerMana = 0;
        Assert.False(Game.TryBeginAttackUse(conn, gem.Id, gem.Effect, at,
            _ => throw new InvalidOperationException("An underfunded use must not move or start an action"), out var unpaid));
        Assert.Null(unpaid);
        Assert.Equal(0, conn.ActionCount);
        Assert.Equal(0, conn.NextAttackAt);
        Assert.Equal(from, conn.Walk.Position(at));
        conn.PlayerMana = 1000;
        Assert.False(Game.TryBeginAttackUse(conn, gem.Id, gem.Effect, at,
            _ => throw new InvalidOperationException("A rejected admission must have no side effects"), out var refused,
            admit: _ => false));
        Assert.Null(refused);
        Assert.Equal(1000u, conn.PlayerMana);
        Assert.Equal(0, conn.ActionCount);
        Assert.Equal(0, conn.NextAttackAt);
        Assert.Equal(from, conn.Walk.Position(at));

        LeapSlam.Use? leap = null;
        int handoffs = 0;
        Assert.True(Game.TryBeginAttackUse(conn, gem.Id, gem.Effect, at, acceptedAt =>
        {
            Assert.NotNull(leap);
            Assert.Equal(acceptedAt, leap.At);
            conn.ActionCount++;
            handoffs++;
            Game.CommitLeapSlam(conn, leap);
        }, out var ticket, acceptedAt => (leap = Game.PlanLeapSlam(conn, gem.Id, gem.Effect, aim, acceptedAt)) is not null));
        Assert.NotNull(leap);
        Assert.NotNull(ticket);
        Assert.False(ticket.Instant);
        Assert.True(ticket.Reserved);
        Assert.NotNull(ticket.Attack);
        Assert.Equal(gem.Id, ticket.Attack.Value.Skill);
        Assert.Equal(at, ticket.Attack.Value.At);
        Assert.Equal(seconds, ticket.Attack.Value.Seconds, 9);
        Assert.Equal((cost, 0u), ticket.Paid);
        Assert.Equal(1000u - cost, conn.PlayerMana);
        Assert.Equal(1, handoffs);
        Assert.Equal(1, conn.ActionCount);
        Assert.Equal(leap.ActionToken, ticket.ActionToken);
        Assert.Equal(aim, conn.Walk.Position(at));
        double next = conn.NextAttackAt;
        Assert.Equal(at + seconds, next, 9);
        Assert.Equal(at + timing.End, conn.LandingUntil, 9);

        double tooEarly = next - seconds * AttackTiming.EarlyAllowance - .001;
        Assert.False(Game.TryBeginAttackUse(conn, gem.Id, gem.Effect, tooEarly,
            _ => throw new InvalidOperationException("An early input must not move or start another action"), out var early));
        Assert.Null(early);
        Assert.Equal(1000u - cost, conn.PlayerMana);
        Assert.Equal(next, conn.NextAttackAt);
        Assert.Equal(1, conn.ActionCount);
        Assert.Equal(1, handoffs);
        Assert.Equal(aim, conn.Walk.Position(tooEarly));

        var first = new SpawnedMonster("landing-one", new(156, 100, 0), 0, 0, 100000) { HitAnimations = [new(0, 1)] };
        var second = new SpawnedMonster("landing-two", new(150, 107, 0), 0, 0, 100000) { HitAnimations = [new(0, 1)] };
        var origin = new SpawnedMonster("origin", new(102, 100, 0), 0, 0, 100000);
        conn.Monsters[100] = first;
        conn.Monsters[101] = second;
        conn.Monsters[102] = origin;
        var gate = Program.SendGates.GetValue(ciphers, _ => new(1, 1));
        Assert.True(gate.Wait(0));
        try
        {
            var cast = Game.CastGemSkill("leap-test", stream, ciphers, conn, gem.Id, from, aim,
                sequence: 1, actionToken: ticket.ActionToken, cooldownUse: ticket, swingAt: at, leap: leap);
            Assert.True(cast.IsCompletedSuccessfully);
            Assert.True(await cast);
            Assert.Equal(1000u - cost, conn.PlayerMana);
            Assert.Equal(next, conn.NextAttackAt);
            var duplicate = Game.CastGemSkill("leap-test", stream, ciphers, conn, gem.Id, from, aim,
                sequence: 1, actionToken: ticket.ActionToken, cooldownUse: ticket, swingAt: at, leap: leap);
            Assert.True(duplicate.IsCompletedSuccessfully);
            Assert.False(await duplicate);
            Assert.Equal(1000u - cost, conn.PlayerMana);
            long landsAt = ProjectileRuntime.Time(at + leap.Timing.Land);
            Assert.True(landsAt > session.Runtime.Now);
            session.AdvanceTo(landsAt - 1);
            Assert.Equal(100000u, first.Life);
            Assert.Equal(100000u, second.Life);
            Assert.Equal(100000u, origin.Life);
            session.AdvanceTo(landsAt);
            Assert.InRange(first.Life, 1u, 99999u);
            Assert.InRange(second.Life, 1u, 99999u);
            Assert.Equal(100000u, origin.Life);
            Assert.True(first.FlinchUntil > at && second.FlinchUntil > at);
            uint firstLife = first.Life, secondLife = second.Life;
            session.AdvanceTo(ProjectileRuntime.Time(at + leap.Timing.End + seconds));
            Assert.Equal(firstLife, first.Life);
            Assert.Equal(secondLife, second.Life);
            Assert.Equal(100000u, origin.Life);
            Assert.Equal(1000u - cost, conn.PlayerMana);
        }
        finally
        {
            session.Invalidate();
            gate.Release();
        }

        // A rejected replacement keeps the old Multistrike reservation. An accepted Leap cancels only its unstarted
        // swings before reserving its own attack, rather than waiting for or overwriting the whole repeat series.
        using var replacementGame = new TestGame();
        replacementGame.Equip(multistrike: true);
        var replacement = replacementGame.Connection;
        var replacementSession = new Game.ProjectileSession("leap-replacement-test", stream, ciphers, replacement);
        replacement.ProjectileRuntime = replacementSession;
        replacementSession.AdvanceTo(ProjectileRuntime.Time(2000));
        var repeatGem = Game.GemSkillEffects(replacement).Single(g => skills.EffectId(g.Effect) == "Cleave");
        var leapGem = Game.GemSkillEffects(replacement).Single(g => skills.EffectId(g.Effect) == "LeapSlam");
        double repeatSeconds = Game.SupportedAttackSeconds(replacement, repeatGem.Id);
        double leapSeconds = Game.SupportedAttackSeconds(replacement, leapGem.Id);
        double start = Game.ProjectileWorldTime(replacement);
        replacement.NextAttackAt = start + repeatSeconds;
        Assert.Equal(2, Game.MeleeAttackRepeats(replacement, repeatGem.Id));
        Game.ReserveMeleeRepeats(replacement, repeatGem.Id, repeatSeconds);
        double repeatEnd = replacement.NextAttackAt;
        double requested = start + repeatSeconds * .8;
        double cancelDeadline = start + repeatSeconds;
        Assert.Equal(cancelDeadline, Game.MeleeRepeatAttackDeadline(replacement, requested), 8);
        Assert.Equal(repeatEnd, replacement.NextAttackAt);
        Assert.False(Game.TryBeginAttackUse(replacement, leapGem.Id, leapGem.Effect, requested,
            _ => throw new InvalidOperationException("Rejected replacement must retain the repeat series"),
            out var rejectedReplacement, _ => false));
        Assert.Null(rejectedReplacement);
        Assert.Equal(repeatEnd, replacement.NextAttackAt);
        Assert.Equal(cancelDeadline, Game.MeleeRepeatAttackDeadline(replacement, requested), 8);
        uint beforeReplacement = replacement.PlayerMana;
        uint replacementCost = (uint)Game.SupportedStats(replacement, leapGem.Id)[Key("mana_cost")];
        Assert.True(Game.TryBeginAttackUse(replacement, leapGem.Id, leapGem.Effect, requested,
            _ => replacement.ActionCount++, out var replacementTicket));
        Assert.NotNull(replacementTicket);
        Assert.Equal(beforeReplacement - replacementCost, replacement.PlayerMana);
        Assert.Equal(Math.Max(requested, cancelDeadline) + leapSeconds, replacement.NextAttackAt, 8);
        Assert.True(replacement.NextAttackAt < repeatEnd + leapSeconds);
        Assert.Equal(replacement.NextAttackAt, Game.MeleeRepeatAttackDeadline(replacement, requested));
    }

    [Fact]
    public void Full_life_stuns_keep_shared_duration_and_immunity_rules_while_landing_displacement_needs_no_hit_chance()
    {
        using var game = new TestGame();
        var conn = game.Connection;
        var skills = new SkillCatalog();
        int effect = skills.EffectRow("LeapSlam");
        var q0 = skills.EffectStats(effect, 1).ToDictionary(s => s.Item1, s => s.Item2);
        var own = skills.EffectStats(effect, 1, 20).ToDictionary(s => s.Item1, s => s.Item2);
        uint conditional = Key("stun_duration_+%_vs_enemies_that_are_on_full_life");
        Assert.Equal(1, own[Key("always_stun_enemies_that_are_on_full_life")]);
        Assert.Equal(20, q0[conditional]);
        Assert.Equal(60, own[conditional]);
        var player = new Dictionary<uint, int> { [Key("base_stun_duration_+%")] = 30 };
        var physical = DamagePacket.Of(DamageType.Physical, 1, 1);
        var target = new SpawnedMonster("full-life", new(105, 100, 30), 0, 0, 1000) { HitAnimations = [new(0, 1)] };
        StunHit Hit(SpawnedMonster monster, int damage = 1) =>
            Game.OwnerStun(conn, monster, damage, physical, melee: true, spell: false, critical: false, player, own);
        var fullLife = Hit(target);
        Assert.True(fullLife.Guaranteed);
        Assert.Equal(90, fullLife.DurationPct);
        Assert.Equal(100, HitReaction.Chance(fullLife, target.MaxLife));
        Assert.Equal(0, HitReaction.Chance(fullLife with { Guaranteed = false }, target.MaxLife));
        Assert.Equal(0, HitReaction.Chance(fullLife with { Damage = 0 }, target.MaxLife));
        Assert.Equal(0, HitReaction.Chance(fullLife with { Damage = -1 }, target.MaxLife));
        Assert.False(Hit(target, 0).Guaranteed);
        player[Key("cannot_stun")] = 1;
        var prevented = Hit(target);
        Assert.False(prevented.Guaranteed);
        Assert.Equal(0, HitReaction.Chance(prevented, target.MaxLife));
        player.Remove(Key("cannot_stun"));
        var coldCritical = Game.OwnerStun(conn, target, 1, DamagePacket.Of(DamageType.Cold, 1, 1),
            melee: true, spell: false, critical: true, player, own);
        Assert.False(coldCritical.Guaranteed);
        Assert.Equal(0, HitReaction.Chance(coldCritical, target.MaxLife));

        conn.PlayerStats[Key(FortificationSession.ChanceOnStun)] = 100;
        var charges = Game.ChargesOf(conn);
        int ChargeStats(string name) => name == "chance_to_gain_endurance_charge_when_you_stun_enemy_%" ? 100 : 0;
        charges.Observe(ChargeStats);
        var unwavering = new SpawnedMonster("unwavering", new(110, 100, 0), 0, 0, 1000)
            { CannotBeStunned = true, HitAnimations = [new(0, 1)] };
        var immune = new SpawnedMonster("immune", new(110, 101, 0), 0, 0, 1000)
            { StunImmuneUntil = 100, HitAnimations = [new(0, 1)] };
        var rewarding = fullLife with { FortificationDamage = 200m };
        Assert.False(HitReaction.TryFlinch(unwavering, rewarding, 10, new Random(1)));
        Assert.False(HitReaction.TryFlinch(immune, rewarding, 10, new Random(1)));
        charges.Step(10, ChargeStats, () => default, alive: true, moving: false, inTown: false, new Random(1));
        Assert.Equal(0, charges.Effective(ChargeType.Endurance));
        Assert.Equal(0, Game.FortificationOf(conn).Value.Current);

        Assert.NotNull(Game.ApplyMonsterHitLocked(conn, target, 1));
        Assert.Equal(999u, target.Life);
        var injured = Hit(target);
        Assert.False(injured.Guaranteed);
        Assert.Equal(30, injured.DurationPct);
        Assert.Equal(0, HitReaction.Chance(injured, target.MaxLife));
        // The full-life witness was captured before damage, so the damaging hit still stuns and feeds the shared gains.
        Assert.True(HitReaction.TryFlinch(target, rewarding, 10, new Random(1)));
        Assert.Equal(10 + .35 * 1.9 * 1.5, target.FlinchUntil, 9);
        charges.Step(10, ChargeStats, () => default, alive: true, moving: false, inTown: false, new Random(1));
        Assert.Equal(1, charges.Effective(ChargeType.Endurance));
        Assert.True(Game.FortificationOf(conn).Value.Current > 0);
        var injuredTarget = new SpawnedMonster("injured", new(112, 100, 0), 0, 0, 1000)
            { Life = 999, HitAnimations = [new(0, 1)] };
        Assert.True(HitReaction.TryFlinch(injuredTarget, Hit(injuredTarget, 600) with { Owner = null }, 10, new Random(1)));
        Assert.Equal(10 + .35 * 1.3 * 1.5, injuredTarget.FlinchUntil, 9);

        var bossVariety = Game.Monsters.Find("Metadata/Monsters/Malachai/MalachaiBoss");
        Assert.NotNull(bossVariety);
        var boss = new SpawnedMonster("boss", new(115, 100, 0), 0, 0, 1000)
            { Variety = bossVariety, Rarity = MonsterRarity.Unique, HitAnimations = [new(0, 1)] };
        var bossHit = Hit(boss) with { Owner = null };
        Assert.True(HitReaction.TryFlinch(boss, bossHit, 20, new Random(1)));
        Assert.NotNull(BossMods.StunImmunityMilliseconds(bossVariety));
        Assert.Equal(boss.FlinchUntil + BossMods.StunImmunityMilliseconds(bossVariety)!.Value / 1000d, boss.StunImmuneUntil, 9);
        Assert.False(HitReaction.TryFlinch(boss, bossHit, boss.StunImmuneUntil - .001, new Random(1)));
        Assert.True(HitReaction.TryFlinch(boss, bossHit, boss.StunImmuneUntil, new Random(1)));

        conn.Monsters.Clear();
        var overlap = new SpawnedMonster("overlap", new(101, 100, 0), 0, 0, 1000) { SwingGeneration = 0 };
        var nearby = new SpawnedMonster("nearby", new(110, 100, 0), 0, 0, 1000);
        var knockbackImmune = new SpawnedMonster("knockback-immune", new(100, 101, 0), 0, 0, 1000) { Variety = bossVariety };
        var coincident = new SpawnedMonster("coincident", new(100, 100, 0), 0, 0, 1000);
        var barrierBlocked = new SpawnedMonster("barrier-blocked", new(99, 100, 0), 0, 0, 1000);
        var staged = new SpawnedMonster("staged", new(100, 99, 0), 0, 0, 1000) { StageRunning = true };
        conn.Monsters[1] = overlap;
        conn.Monsters[2] = nearby;
        conn.Monsters[3] = knockbackImmune;
        conn.Monsters[4] = coincident;
        conn.Monsters[5] = barrierBlocked;
        conn.Monsters[6] = staged;
        conn.Use(conn.Layout with { Walkable = (x, y) => (x, y) != (95, 100) });
        var noKnockbackChance = new Dictionary<uint, int> { [Key("base_global_chance_to_knockback_%")] = 0 };
        Assert.True(BossMods.KnockbackImmune(bossVariety));
        Game.LeapSlamDisplaceOccupants(conn, (100, 100), 15, 40, noKnockbackChance, from: (100, 80));
        Assert.Equal((105, 100), overlap.Walk.Position(40));
        Assert.Equal((105, 100), overlap.KnockbackPending);
        Assert.Equal(1, overlap.ActionGeneration);
        Assert.Null(overlap.SwingGeneration);
        Assert.Equal((100, 105), coincident.Walk.Position(40));
        Assert.Equal((110, 100), nearby.Walk.Position(40));
        Assert.Equal((100, 101), knockbackImmune.Walk.Position(40));
        Assert.Equal((99, 100), barrierBlocked.Walk.Position(40));
        Assert.Equal((100, 99), staged.Walk.Position(40));
        Assert.Null(nearby.KnockbackPending);
        Assert.Null(knockbackImmune.KnockbackPending);
        Assert.Null(barrierBlocked.KnockbackPending);
        Assert.Null(staged.KnockbackPending);
        Assert.All(conn.Monsters.Values, monster => Assert.Equal(1000u, monster.Life));
        Assert.False(Game.KnockbackOnHitLocked(conn, nearby, noKnockbackChance, null, 40, new Random(1)));
        Assert.Null(nearby.KnockbackPending);
        var directionless = new SpawnedMonster("directionless", new(100, 100, 0), 0, 0, 1000);
        conn.Monsters[7] = directionless;
        Game.LeapSlamDisplaceOccupants(conn, (100, 100), 15, 40, noKnockbackChance);
        Assert.Null(directionless.KnockbackPending);
        Assert.Equal((100, 100), directionless.Walk.Position(40));
    }
}
