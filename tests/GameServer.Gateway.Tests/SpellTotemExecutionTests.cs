using GameServer.Core.Protocol.Instance;
using GameServer.Core.Accounts;
using GameServer.Core.World;
using GameServer.Core.Projectiles;
using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;
public sealed class SpellTotemExecutionTests
{
    static ClientState Connection(string name)
    {
        var conn = new ClientState(new(1, name, 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = name, PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        foreach (string attr in new[] { "strength", "dexterity", "intelligence" })
            conn.PlayerStats[Program.Stats.Key($"virtual_{attr}_for_requirements")!.Value] = 1000;
        return conn;
    }

    static Item Gem(string name) => ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + name)!);
    static ushort Equip(ClientState conn, string active, string support)
    {
        var items = Game.ItemsOf(conn);
        foreach (var previous in items.EquipmentSnapshot().Where(e => e.Row == PlayerInventories.Weapon1))
            items.Remove(PlayerInventories.Weapon1, previous.Item.Id);
        var sword = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword1")!);
        items.Add(PlayerInventories.Weapon1, 0, 0, sword with
        {
            Sockets = [new(SocketColour.White, Gem(active)), new(SocketColour.White, Gem(support))], Links = [2],
        });
        return Game.GemSkillEffects(conn).Single().Id;
    }

    [Fact]
    public void Native_spell_totem_admits_fireball_and_rejects_unimplemented_payloads()
    {
        string path = Path.Combine(Path.GetTempPath(), $"spell-totem-{Guid.NewGuid():N}.db");
        var previous = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game"); Game.CombatLifetime = lifetime;
        Saves.Open(path); Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = Connection("SpellTotem");
        try
        {
            ushort skill = Equip(conn, "SkillGemFireball", "SupportGemSpellTotem");
            var gem = Game.SupportedSkill(conn, skill);
            Assert.NotNull(Game.TotemVariety(conn, gem.Effect));
            Assert.True(Game.ProjectilePathAvailable("test", conn, skill, gem.Effect));
            foreach (string active in new[] { "SkillGemFreezingPulse", "SkillGemSpark", "SkillGemIceSpear", "SkillGemFrostBolt" })
            {
                skill = Equip(conn, active, "SupportGemSpellTotem");
                gem = Game.SupportedSkill(conn, skill);
                Assert.NotNull(Game.TotemVariety(conn, gem.Effect));
                Assert.True(Game.ProjectilePathAvailable("test", conn, skill, gem.Effect));
            }
            skill = Equip(conn, "SkillGemArc", "SupportGemSpellTotem");
            gem = Game.SupportedSkill(conn, skill);
            Assert.False(Game.ProjectilePathAvailable("test", conn, skill, gem.Effect));
            skill = Equip(conn, "SkillGemShockwaveTotem", "SupportGemSpellTotem");
            Assert.False(Game.ProjectilePathAvailable("test", conn, skill, Game.SupportedSkill(conn, skill).Effect));
            skill = Equip(conn, "SkillGemRejuvenationTotem", "SupportGemSpellTotem");
            Assert.NotNull(Game.TotemVariety(conn, Game.SupportedSkill(conn, skill).Effect));
            Assert.True(Game.ProjectilePathAvailable("test", conn, skill, Game.SupportedSkill(conn, skill).Effect));
            Assert.Equal(1000u, conn.PlayerMana);
            skill = Equip(conn, "SkillGemSpark", "SupportGemSpellTotem");
            conn.Clock.Start();
            using var wire = new Wire();
            var session = Game.ProjectilesFor("spark-totem", wire.Stream, wire.Ciphers, conn);
            try
            {
                var spark = Game.GemSkillEffects(conn).Single();
                var source = Game.SupportedSkill(conn, skill);
                var totem = new TotemState.Totem(777, skill, (100, 20), 100, 100, null)
                { Effect = spark.Effect, SourceGem = spark.Gem, SourceHost = source.Host, SourceItem = source.SourceGem };
                conn.Totems.Place(totem, 1, 0);
                conn.Monsters[900] = new("spark-target", new(120, 20, 0), 0, 0, 10000);
                var actions = new List<byte>();
                Game.TickSpellTotemsLocked("spark-totem", wire.Stream, wire.Ciphers, conn, actions);
                session.AdvanceTo(ProjectileRuntime.Time(2));
                var emitted = session.Runtime.Projectiles.Where(p => p.Use.Emitter.Id == 777).ToArray();
                Assert.NotEmpty(emitted);
                Assert.All(emitted, p => { Assert.NotNull(p.Profile.Legs); Assert.Equal(150, p.Profile.LegLeash);
                    var shot = Assert.IsType<ProjectileOffense>(p.Use.Payload);
                    Assert.False(shot.ClientPredicted);
                    Assert.Equal(Game.SkillsFor(conn).Clips(p.Profile.Id)!.Flight!.Index, shot.Presentation!.FlightClip);
                    Assert.Equal(100, p.Use.Origin.X); Assert.Equal(20, p.Use.Origin.Y); });
            }
            finally { session.Invalidate(); }
        }
        finally { conn.Closed = true; lifetime.Stop(_ => { }); Game.CombatLifetime = previousLifetime;
            PlayerInventories.Forget(conn.Character!); Program.SetGameLogic(previous!); TestEnvironment.ReleaseSaves(path); }
    }
    [Fact]
    public async Task Totem_release_uses_its_entity_position_supported_damage_and_source_lifetime()
    {
        string path = Path.Combine(Path.GetTempPath(), $"spell-totem-release-{Guid.NewGuid():N}.db");
        var previous = Program.GameLogicOrNull;
        Saves.Open(path);
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game"); Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = Connection("SpellTotemRelease");
        conn.Clock.Stop();
        using var wire = new Wire();
        var session = new Game.ProjectileSession("test", wire.Stream, wire.Ciphers, conn);
        conn.ProjectileRuntimeState = session;
        try
        {
            ushort skill = Equip(conn, "SkillGemFireball", "SupportGemSpellTotem");
            var gem = Game.GemSkillEffects(conn).Single();
            var spawned = await Game.CreateTotem("test", wire.Stream, wire.Ciphers, conn, skill, gem.Effect, gem.Level,
                Game.TotemVariety(conn, gem.Effect)!, (150, 80));
            Assert.NotNull(spawned);
            var placed = Assert.Single(conn.Totems.All);
            Assert.Equal((150, 80), placed.At); Assert.True(placed.MaxLife > 1);
            Assert.Equal(8, placed.Until - conn.Clock.Elapsed.TotalSeconds, 3);
            Assert.True(Game.SpellTotemCurrent(conn, placed));
            var contact = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = Game.CreateTotem("test", wire.Stream, wire.Ciphers, conn, skill, gem.Effect, gem.Level,
                Game.TotemVariety(conn, gem.Effect)!, (151, 80), null, () => { contact.SetResult(); return finish.Task; });
            await contact.Task;
            Assert.Same(placed, Assert.Single(conn.Totems.Living(0)));
            finish.SetResult(false);
            Assert.Null(await pending);
            Assert.Same(placed, Assert.Single(conn.Totems.Living(0)));
            var sourceHost = Game.ItemsOf(conn).Items(PlayerInventories.Weapon1).Single();
            Game.ItemsOf(conn).Change(PlayerInventories.Weapon1, sourceHost.Id, item => item with
            { Sockets = [.. item.Sockets.Select(s => s with { Gem = PlayerInventories.PreserveGemIdentity(s.Gem!, s.Gem! with { GemExperience = 1 }) })] });
            Assert.True(Game.SpellTotemCurrent(conn, placed)); // Ordinary XP updates keep the bound physical source.

            var totem = new TotemState.Totem(700, skill, (100, 20), 100, 8, null)
                { Effect = gem.Effect, SourceGem = gem.Gem, SourceHost = Game.SupportedSkill(conn, skill).Host, SourceItem = Game.SupportedSkill(conn, skill).SourceGem };
            var ancestralBond = Program.Stats.Key("keystone_ancestral_bond")!.Value;
            conn.PlayerStats[ancestralBond] = 1;
            Assert.Equal(2, Game.TotemLimit(conn, skill));
            Assert.True(new SkillDamageContext(SkillHitKind.Spell, Game.CharacterPlayerStats(conn)).Suppressed);
            var use = Game.CaptureGemProjectile(conn, session, skill, gem, (130, 20), new(0), totem: totem);
            Assert.Equal(700u, use.Emitter.Id); Assert.Equal(700u, use.Owner.Id);
            Assert.Equal(100, use.Origin.X); Assert.Equal(20, use.Origin.Y);
            var shot = Assert.IsType<ProjectileOffense>(use.Payload);
            Assert.NotNull(shot.ActualCaster);
            Assert.True(new SkillDamageContext(SkillHitKind.Spell, shot.ActualCaster!).Hit(shot.Hit).MaxTotal > 0);
            conn.PlayerStats[StatKeys.SpellSkillsDealNoDamage] = 1;
            var disabledShot = (ProjectileOffense)Game.CaptureGemProjectile(conn, session, skill, gem, (130, 20), new(0), totem: totem).Payload!;
            Assert.True(new SkillDamageContext(SkillHitKind.Spell, disabledShot.ActualCaster!).Suppressed);
            conn.PlayerStats.Remove(StatKeys.SpellSkillsDealNoDamage);
            Assert.Equal(Game.SupportedProjectileHit(conn, skill, false).MaxTotal * .45, shot.Hit.MaxTotal, 6);
            var ownerShot = Assert.IsType<ProjectileOffense>(Game.CaptureGemProjectile(conn, session, skill, gem,
                (130, 20), new(0)).Payload);
            Assert.Equal(ownerShot.Ignite.Max * .45, shot.Ignite.Max, 6);
            Assert.Equal(ownerShot.Ignite.CriticalMax * .45, shot.Ignite.CriticalMax, 6);
            var totemDamage = Program.Stats.Key("totem_damage_+%")!.Value;
            conn.PlayerStats[totemDamage] = 100;
            var stronger = (ProjectileOffense)Game.CaptureGemProjectile(conn, session, skill, gem, (130, 20), new(0), totem: totem).Payload!;
            Assert.Equal(shot.Hit.MaxTotal * 2, stronger.Hit.MaxTotal, 5);
            Assert.Equal(shot.Ignite.Max * 2, stronger.Ignite.Max, 4);
            conn.PlayerStats.Remove(totemDamage);
            Assert.Equal(1.25, Game.TotemCastSeconds(conn, skill)!.Value, 3);
            var proxy = Game.TotemProxy(conn, skill);
            Assert.NotNull(proxy);
            Assert.Equal(-40, proxy!.Stats!.Single(s => s.Key == Program.Stats.Key("cast_speed_+%")).Value);
            Assert.True(Game.SpellTotemCurrent(conn, totem));
            Assert.Equal(1000u, conn.PlayerMana); Assert.Equal(1000u, conn.PlayerLife);
            conn.Totems.Place(totem, 1, 0);
            conn.Clock.Start();
            var actions = new List<byte>();
            Game.TickSpellTotemsLocked("test", wire.Stream, wire.Ciphers, conn, actions);
            Assert.Empty(actions); Assert.Equal(0, totem.NextCast); // Empty area keeps the player tick alive.
            var target = new SpawnedMonster("totem-target", new(125, 20, 0), 0, 0, 10000);
            conn.Monsters[900] = target;
            Game.TickSpellTotemsLocked("test", wire.Stream, wire.Ciphers, conn, actions);
            Assert.NotEmpty(actions); Assert.True(totem.NextCast > 1);
            conn.Clock.Stop(); actions.Clear();
            Game.TickSpellTotemsLocked("test", wire.Stream, wire.Ciphers, conn, actions);
            Assert.Empty(actions); conn.Clock.Start();
            session.AdvanceTo(ProjectileRuntime.Time(3));
            Assert.True(target.Life < 10000, $"life={target.Life} closed={conn.Closed}");
            Assert.Equal(1000u, conn.PlayerMana);
            totem.NextCast = 0; target.Dead = true; target.Life = 0;
            actions.Clear();
            Game.TickSpellTotemsLocked("test", wire.Stream, wire.Ciphers, conn, actions);
            Assert.Empty(actions); Assert.Equal(0, totem.NextCast); // Last enemy died; no cast or tick failure.
            conn.Monsters[901] = new("next-target", new(100, 45, 0), 0, 0, 10000);
            Game.TickSpellTotemsLocked("test", wire.Stream, wire.Ciphers, conn, actions);
            Assert.NotEmpty(actions);

            var prohibited = Program.Stats.Key("base_deal_no_fire_damage")!.Value;
            conn.PlayerStats[prohibited] = 1;
            use = Game.CaptureGemProjectile(conn, session, skill, gem, (130, 20), new(0), totem: totem);
            Assert.Equal(0, Assert.IsType<ProjectileOffense>(use.Payload).Hit.MaxTotal);
            conn.PlayerStats.Remove(prohibited);
            conn.LeavingArea = true; Assert.False(Game.SpellTotemCurrent(conn, totem)); conn.LeavingArea = false;
            conn.PlayerLife = 0; Assert.False(Game.SpellTotemCurrent(conn, totem)); conn.PlayerLife = 1000;

            // Replace the active gem in the same host/socket: wire id and TypeHash stay identical.
            var items = Game.ItemsOf(conn);
            var host = items.Items(PlayerInventories.Weapon1).Single();
            items.Change(PlayerInventories.Weapon1, host.Id, item => item with
            {
                Sockets = [new(SocketColour.White, Gem("SkillGemFireball")), item.Sockets[1]],
            });
            var swapped = Game.GemSkillEffects(conn).Single();
            Assert.Equal(skill, swapped.Id); Assert.Equal(gem.Gem, swapped.Gem);
            Assert.Equal(totem.SourceHost, Game.SupportedSkill(conn, skill).Host);
            Assert.NotSame(totem.SourceItem, Game.SupportedSkill(conn, skill).SourceGem);
            Assert.False(Game.SpellTotemCurrent(conn, totem));
            var next = conn.Monsters[901];
            uint before = next.Life;
            session.AdvanceTo(ProjectileRuntime.Time(6));
            Assert.Equal(before, next.Life); // The pending old-source release was cancelled.
            Equip(conn, "SkillGemFireball", "SupportGemSpellTotem");
            Assert.False(Game.SpellTotemCurrent(conn, totem));
            var state = new TotemState(); state.Place(totem, 1, 0);
            state.Place(new(701, skill, (90, 20), 50, 8, null), 1, 0);
            Assert.False(totem.Alive(0)); Assert.Single(state.Living(0));
            Assert.Equal((50, 0u), TotemState.Redirect(100, 100, 50));
            Assert.Single(state.Expire(8)); Assert.Empty(state.Living(8));
            var renewed = Game.SupportedSkill(conn, skill);
            var live = new TotemState.Totem(702, skill, (100, 20), 100, 100, null)
                { Effect = renewed.Effect, SourceGem = Game.GemSkillEffects(conn).Single().Gem,
                  SourceHost = renewed.Host, SourceItem = renewed.SourceGem };
            Assert.True(Game.SpellTotemCurrent(conn, live));
            Game.BindTotemSource(conn, live);
            var levelHost = Game.ItemsOf(conn).Items(PlayerInventories.Weapon1).Single();
            Game.ItemsOf(conn).Change(PlayerInventories.Weapon1, levelHost.Id, item => item with
            { Sockets = [.. item.Sockets.Select(s => s with { Gem = PlayerInventories.PreserveGemIdentity(s.Gem!, s.Gem! with
                { GemExperience = Program.Items.Gem(Program.Items.ByHash(s.Gem!.TypeHash)!)!.ExperienceFor(2) }) })] });
            Assert.Equal(2, Game.SupportedSkill(conn, skill).Level);
            Assert.True(Game.SpellTotemCurrent(conn, live));
            lifetime.Stop(ex => throw ex);
            Assert.False(Game.SpellTotemCurrent(conn, live)); Assert.False(session.Active);
        }
        finally
        {
            session.Invalidate(); conn.Closed = true;
            lifetime.Stop(ex => throw ex); Game.CombatLifetime = previousLifetime;
            PlayerInventories.Forget(conn.Character!); Program.SetGameLogic(previous!);
            TestEnvironment.ReleaseSaves(path);
        }
        await VerifyBallistas();
    }
    static async Task VerifyBallistas()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ballista-{Guid.NewGuid():N}.db");
        var previous = Program.GameLogicOrNull; var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game"); Game.CombatLifetime = lifetime;
        Saves.Open(path); Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = Connection("Ballista" + Guid.NewGuid().ToString("N")); conn.Clock.Stop();
        var items = Game.ItemsOf(conn);
        var body = ItemCatalog.Create(Program.Items.Find("Simple Robe")!) with
        { Sockets = [new(SocketColour.White, Gem("SkillGemBurningArrow")), new(SocketColour.White, Gem("SupportGemRangedAttackTotem")),
            new(SocketColour.White, Gem("SkillGemSiegeBallista")), new(SocketColour.White, Gem("SkillGemShrapnelBallista"))], Links = [2, 1, 1] };
        items.Add(PlayerInventories.BodyArmour, 0, 0, body);
        var bow = items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Crude Bow")!));
        using var wire = new Wire();
        var session = new Game.ProjectileSession("ballista-test", wire.Stream, wire.Ciphers, conn); conn.ProjectileRuntimeState = session;
        try
        {
            var burning = Game.GemSkillEffects(conn).Single(s => s.Name == "Burning Arrow");
            var siege = Game.GemSkillEffects(conn).Single(s => s.Name == "Siege Ballista");
            var shrapnel = Game.GemSkillEffects(conn).Single(s => s.Name == "Shrapnel Ballista");
            foreach (var skill in new[] { burning, siege, shrapnel })
            {
                Assert.True(Game.BallistaPathAvailable(conn, skill.Id), $"{skill.Name} effect={Game.BallistaEffect(conn, skill.Effect)} ranged={Game.SupportedSkill(conn, skill.Id).Stats.GetValueOrDefault(Program.Stats.Key("is_ranged_attack_totem")!.Value)} weapon={Game.WeaponRestricted(conn, skill.Effect)} recipe={Game.ResolveProjectileDefinition(skill.Effect)?.Metadata}");
                Assert.True(Game.ProjectilePathAvailable("test", conn, skill.Id, skill.Effect));
                Assert.NotNull(Game.TotemVariety(conn, skill.Effect)); Assert.Equal(3, Game.TotemLimit(conn, skill.Id));
            }
            Assert.Equal(60, Game.TotemTargetRange(conn, burning.Id));
            Assert.Equal(80, Game.TotemTargetRange(conn, siege.Id));
            Assert.Equal(40, Game.TotemTargetRange(conn, shrapnel.Id));
            Assert.Equal(Game.SupportedAttackSeconds(conn, burning.Id) * 2, Game.TotemAttackSeconds(conn, burning.Id), 5);
            for (int i = 0; i < 4; i++)
                Assert.NotNull(await Game.CreateTotem("ballista-test", wire.Stream, wire.Ciphers, conn, burning.Id,
                    burning.Effect, burning.Level, Game.TotemVariety(conn, burning.Effect)!, (100 + i, 20)));
            Assert.Equal(3, conn.Totems.Living(0).Count());
            var placed = conn.Totems.Living(0).Last(); Assert.True(placed.Ballista);
            conn.PlayerStats[StatKeys.SpellSkillsDealNoDamage] = 1;
            var use = Game.CaptureGemProjectile(conn, session, burning.Id, burning, (130, 20), new(0), totem: placed);
            var shot = Assert.IsType<ProjectileOffense>(use.Payload);
            Assert.False(shot.Spell); Assert.NotNull(shot.AccuracyProfile); Assert.NotNull(shot.ActualCaster);
            Assert.Equal(Game.SupportedProjectileHit(conn, burning.Id, false).MaxTotal * .64, shot.Hit.MaxTotal, 5);
            // Burning Arrow converts all physical damage; use a physical attack for the bleed regression.
            var bleedHost = items.Items(PlayerInventories.BodyArmour).Single();
            items.Change(PlayerInventories.BodyArmour, bleedHost.Id, _ => body with
            { Sockets = [body.Sockets[0] with { Gem = Gem("SkillGemSplitArrow") }, .. body.Sockets.Skip(1)] });
            var bleeding = Game.GemSkillEffects(conn).Single(s => s.Name == "Split Arrow");
            var ownerShot = (ProjectileOffense)Game.CaptureGemProjectile(conn, session, bleeding.Id, bleeding, (130, 20), new(0)).Payload!;
            var bleedShot = (ProjectileOffense)Game.CaptureGemProjectile(conn, session, bleeding.Id, bleeding, (130, 20), new(0), totem: placed).Payload!;
            Assert.True(ownerShot.Bleed.Max > 0);
            Assert.Equal(ownerShot.Bleed.Max * .64, bleedShot.Bleed.Max, 6);
            Assert.Equal(ownerShot.Bleed.CriticalMax * .64, bleedShot.Bleed.CriticalMax, 6);
            items.Change(PlayerInventories.BodyArmour, bleedHost.Id, _ => body);
            Assert.True(new SkillDamageContext(SkillHitKind.Attack, shot.ActualCaster!).Hit(shot.Hit).MaxTotal > 0);
            Assert.Equal(placed.Id, use.Emitter.Id); Assert.Equal(103, use.Origin.X);
            Assert.NotNull(Game.TotemProxy(conn, burning.Id));
            conn.Clock.Start();
            var target = new SpawnedMonster("ballista-target", new(125, 20, 0), 0, 0, 100000) { Hostile = true };
            var friendly = new SpawnedMonster("ballista-friendly", new(125, 20, 0), 0, 0, 100000) { Hostile = false };
            conn.Monsters[901] = target; conn.Monsters[902] = friendly;
            uint mana = conn.PlayerMana;
            session.Schedule(0, () => true, () => use); session.AdvanceTo(ProjectileRuntime.Time(1));
            Assert.True(target.Life < 100000); Assert.Equal(100000u, friendly.Life); Assert.Equal(mana, conn.PlayerMana);
            // Two bonus ballista slots coexist with the ordinary totem slot.
            var state = new TotemState();
            for (uint i = 1; i <= 3; i++) state.Place(new(i, burning.Id, (0, 0), 10, 10, null)
                { Ballista = true, BonusSlots = 2 }, 3, 0, 1);
            state.Place(new(10, burning.Id, (0, 0), 10, 10, null), 1, 0, 1);
            Assert.Equal(3, state.Living(0).Count()); Assert.Equal(2, state.Living(0).Count(t => t.Ballista));
            state.Place(new(11, burning.Id, (0, 0), 10, 10, null), 1, 0, 1);
            Assert.Equal(3, state.Living(0).Count()); Assert.Equal(2, state.Living(0).Count(t => t.Ballista));
            // Native Shrapnel has three individually admitted arrows, not the normal one-hit-per-emission policy.
            var source = Game.SupportedSkill(conn, shrapnel.Id);
            var shardTotem = new TotemState.Totem(800, shrapnel.Id, (100, 20), 100, 100, null)
            { Effect = shrapnel.Effect, SourceGem = shrapnel.Gem, SourceHost = source.Host, SourceItem = source.SourceGem, Ballista = true };
            var shards = Game.CaptureGemProjectile(conn, session, shrapnel.Id, shrapnel, (102, 20), new(0), totem: shardTotem);
            Assert.Equal(3, shards.Profile.Count); Assert.Equal(ProjectileHitScope.Projectile, shards.Profile.HitRule.Scope);
            var fixedShot = (ProjectileOffense)shards.Payload!;
            shards = shards with { Payload = fixedShot with { Hit = DamagePacket.Of(DamageType.Physical, 100, 100),
                CriticalChance = 0, AccuracyProfile = new(100000, CannotBeEvaded: true) } };
            var close = new SpawnedMonster("shotgun-target", new(102, 20, 0), 0, 0, 100000) { Hostile = true };
            conn.Monsters.Clear(); conn.Monsters[903] = close;
            session.Schedule(session.Runtime.Now, () => true, () => shards);
            session.AdvanceTo(ProjectileRuntime.Time(2)); Assert.Equal(99700u, close.Life);
            // Retiring the emitter cancels future casts, not a projectile that has already left it.
            for (int reason = 0; reason < 4; reason++)
            {
                var sourceNow = Game.SupportedSkill(conn, burning.Id);
                var emitter = new TotemState.Totem((uint)(810 + reason), burning.Id, (100, 60 + reason * 30), 100, 100, null)
                { Effect = burning.Effect, SourceGem = burning.Gem, SourceHost = sourceNow.Host, SourceItem = sourceNow.SourceGem };
                Game.BindTotemSource(conn, emitter);
                var flight = Game.CaptureGemProjectile(conn, session, burning.Id, burning, (120, emitter.At.Y), new(0), totem: emitter);
                var payload = (ProjectileOffense)flight.Payload!;
                flight = flight with { Profile = flight.Profile with { Speed = 10, Count = 1, Range = 40 },
                    Payload = payload with { Hit = DamagePacket.Of(DamageType.Physical, 100, 100), CriticalChance = 0,
                        AccuracyProfile = new(100000, CannotBeEvaded: true) } };
                var delayed = new SpawnedMonster("in-flight", new(120, emitter.At.Y, 0), 0, 0, 100000) { Hostile = true };
                conn.Monsters.Clear(); conn.Monsters[920] = delayed;
                double at = session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
                session.Schedule(session.Runtime.Now, () => true, () => flight);
                session.AdvanceTo(ProjectileRuntime.Time(at + .01));
                Assert.Equal(100000u, delayed.Life);
                if (reason == 0) emitter.Until = 0;
                else if (reason == 1) emitter.DiedAt = 0;
                else if (reason == 2) conn.PlayerLife = 0;
                else
                {
                    var bodyHost = items.Items(PlayerInventories.BodyArmour).Single();
                    items.Change(PlayerInventories.BodyArmour, bodyHost.Id, item => item with
                    { Sockets = [item.Sockets[0] with { Gem = Gem("SkillGemBurningArrow") }, .. item.Sockets.Skip(1)] });
                }
                Assert.False(Game.SpellTotemCurrent(conn, emitter));
                session.AdvanceTo(ProjectileRuntime.Time(at + 3));
                Assert.Equal(99900u, delayed.Life);
                conn.PlayerLife = 1000;
            }
            items.Change(PlayerInventories.Weapon1, bow.Id, _ => ItemCatalog.Create(Program.Items.Find("Rusted Sword")!));
            Assert.False(Game.BallistaPathAvailable(conn, burning.Id)); Assert.False(Game.SpellTotemCurrent(conn, placed));
            items.Change(PlayerInventories.Weapon1, bow.Id, _ => ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!));
            var host = items.Items(PlayerInventories.BodyArmour).Single();
            items.Change(PlayerInventories.BodyArmour, host.Id, _ => body with
            { Sockets = [new(SocketColour.White, Gem("SkillGemKineticBlast")),
                new(SocketColour.White, Gem("SupportGemRangedAttackTotem"))], Links = [2] });
            var wandSkill = Game.GemSkillEffects(conn).Single(s => s.Name == "Kinetic Blast");
            Assert.True(Game.BallistaPathAvailable(conn, wandSkill.Id));
            var bolt = Gem("SkillGemKineticBolt");
            int fragmenting = Program.Items.Gem(Program.Items.ByHash(bolt.TypeHash)!)!.Variants
                .Single(v => Game.SkillsFor(conn).EffectId(v.Effect) == "KineticBoltAltX").Index;
            items.Change(PlayerInventories.BodyArmour, host.Id, _ => body with
            { Sockets = [new(SocketColour.White, bolt with { GemVariant = fragmenting })], Links = [1] });
            var fragments = Game.GemSkillEffects(conn).Single();
            Assert.Equal("KineticBoltAltX", Game.SkillsFor(conn).EffectId(fragments.Effect));
            var fragmentStats = Game.SupportedSkill(conn, fragments.Id).Stats;
            int ReadFragment(string name) => Program.Stats.Key(name) is uint key ? fragmentStats.GetValueOrDefault(key) : 0;
            Assert.Equal(1, ReadFragment("projectiles_can_shotgun"));
            Assert.Equal(ProjectileHitScope.Projectile,
                GatewayProjectileModifiers.Resolve(new("fragment-check"), ReadFragment, attack: true).HitRule.Scope);
            Assert.True(Game.ProjectilePathAvailable("test", conn, fragments.Id, fragments.Effect));
            items.Change(PlayerInventories.BodyArmour, host.Id, _ => body with
            { Sockets = [new(SocketColour.White, Gem("SkillGemArtilleryBallista"))], Links = [1] });
            var artillery = Game.GemSkillEffects(conn).Single();
            Assert.False(Game.BallistaEffect(conn, artillery.Effect));
            items.Change(PlayerInventories.Weapon1, bow.Id, _ => ItemCatalog.Create(Program.Items.Find("Crude Bow")!));
            items.Change(PlayerInventories.BodyArmour, host.Id, _ => body with
            { Sockets = [new(SocketColour.White, Gem("SkillGemSiegeBallista")),
                new(SocketColour.White, Gem("SupportGemFocusedBallista"))], Links = [2] });
            var focused = Game.GemSkillEffects(conn).Single();
            Assert.False(Game.BallistaPathAvailable(conn, focused.Id));
        }
        finally
        {
            session.Invalidate(); conn.Closed = true; lifetime.Stop(_ => { }); Game.CombatLifetime = previousLifetime;
            PlayerInventories.Forget(conn.Character!); Program.SetGameLogic(previous!); TestEnvironment.ReleaseSaves(path);
        }
    }
    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new(); readonly TcpClient reader;
        readonly Task drain;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        public NetworkStream Stream => writer.GetStream();
        public Wire()
        {
            listener.Start(); var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
            drain = Task.Run(async () =>
            {
                try { var buffer = new byte[65536]; while (await reader.GetStream().ReadAsync(buffer) > 0) { } }
                catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            });
        }
        public void Dispose() { reader.Dispose(); writer.Dispose(); listener.Stop(); drain.GetAwaiter().GetResult(); }
    }
}



