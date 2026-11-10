using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using League = GameServer.Core.Accounts.League;
namespace GameServer.Gateway.Tests;
public class DamageTakenTriggerTests
{
    [Fact]
    public void Cwdt_is_trigger_only_and_native_level_limit_preserves_manual_high_level_spell()
    {
        WithCharacter(conn =>
        {
            Equip(conn, "SupportGemCastOnDamageTaken");
            var gem = Game.GemSkillEffects(conn).Single(g => g.Name == "Fireball");
            Assert.Contains("trigger", Game.GemSkillInstanceDisabled(conn, gem.Effect, gem.Id) ?? "");
            conn.Clock.Start();
            int action = conn.ActionCount; bool sideEffects = false;
            Assert.False(Game.TryBeginSkillCooldownUse(conn, gem.Id, gem.Effect, null, false,
                () => sideEffects = true, out _));
            Assert.False(sideEffects); Assert.Equal(action, conn.ActionCount);
            var support = Game.SupportedSkill(conn, gem.Id).Supports.Single();
            Assert.Empty(Game.Supports.Resolve(gem.Effect, [support.Gem], Game.SkillsFor(conn), activeLevel: 20));
            Assert.NotEmpty(Game.Supports.Resolve(gem.Effect, [support.Gem with { Level = 20 }], Game.SkillsFor(conn), activeLevel: 20));
            foreach (string triggerId in new[] { "SupportCastOnCritTriggered", "SupportCastWhileChannellingTriggered" })
            {
                var effect = Game.Supports.Effects.Single(e => e.Id == triggerId);
                var applied = Game.Supports.Resolve(gem.Effect, [new(effect.Row, 1, 0, 1)], Game.SkillsFor(conn));
                Assert.Contains("trigger", Game.TriggerOnlyReason(Game.SupportedSkill(conn, gem.Id) with { Supports = applied }) ?? "");
            }
        });
    }
    [Fact]
    public void Accepted_hits_trigger_one_paid_supported_release_without_replacing_the_action()
    {
        WithCharacter(conn =>
        {
            Equip(conn, "SupportGemCastOnDamageTaken");
            conn.Clock.Start();
            var skill = Game.GemSkillEffects(conn).Single(g => g.Name == "Fireball");
            var supported = Game.SupportedSkill(conn, skill.Id);
            int threshold = supported.Stats[Program.Stats.Key("cast_on_damage_taken_threshold")!.Value];
            Assert.Equal(528, threshold);
            Assert.Equal(250, supported.Supports.Single().CostMultiplier);
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            using var writer = new System.Net.Sockets.TcpClient();
            writer.Connect((System.Net.IPEndPoint)listener.LocalEndpoint);
            using var reader = listener.AcceptTcpClient();
            var session = new Game.ProjectileSession("cwdt-test", writer.GetStream(), new ConnectionCiphers(new byte[64], false), conn);
            try
            {
                uint mana = conn.PlayerMana;
                uint expectedCost = (uint)Game.SupportedStats(conn, skill.Id)[Program.Stats.Key("mana_cost")!.Value];
                int action = conn.ActionCount;
                // Ward absorbs every point of the first hit, yet the accepted damage must count.
                conn.Immune = true;
                Assert.Equal(0, Game.DamagePlayerHitTyped(conn, threshold, 0, 0, 0, new Dictionary<uint, int>(), GameServer.Core.World.DamagePacket.Empty));
                conn.Immune = false;
                conn.Ward.SetMaximum(1000);
                Assert.Equal(0, Game.DamagePlayerHitTyped(conn, threshold - 1, 0, 0, 0, new Dictionary<uint, int>(), GameServer.Core.World.DamagePacket.Empty));
                Game.ReleaseDamageTakenTriggers(conn, session);
                session.AdvanceTo(0);
                Assert.Empty(session.Runtime.Projectiles);
                Assert.Equal(mana, conn.PlayerMana);
                Game.DamagePlayerHitTyped(conn, 1, 0, 0, 0, new Dictionary<uint, int>(), GameServer.Core.World.DamagePacket.Empty);
                Game.ReleaseDamageTakenTriggers(conn, session);
                session.AdvanceTo(0);
                Assert.Single(session.Runtime.Projectiles);
                Assert.Equal(mana - expectedCost, conn.PlayerMana);
                Assert.Equal(action, conn.ActionCount);
                uint paid = conn.PlayerMana;
                var shot = Assert.IsType<ProjectileOffense>(session.Runtime.Projectiles.Single().Use.Payload);
                Assert.Equal(skill.Id, shot.SourceSkill);
                Assert.False(shot.ClientPredicted);
                Assert.Single(conn.ProjectileObjects);
                var inputs = Game.SupportedHitInputsFor(conn, skill.Id, area: false)!;
                var withoutTriggerPenalty = Game.SupportedHitFrom(inputs with
                { Modifiers = inputs.Modifiers.Where(m => m.Name != "cast_on_damage_taken_damage_+%_final").ToList() }, suppress: false);
                double penalty = 1 + supported.Stats[Program.Stats.Key("cast_on_damage_taken_damage_+%_final")!.Value] / 100d;
                Assert.Equal(withoutTriggerPenalty[GameServer.Core.World.DamageType.Fire].Min * penalty,
                    shot.Hit[GameServer.Core.World.DamageType.Fire].Min, 5);
                Assert.Equal(supported.Stats[Program.Stats.Key("cast_on_damage_taken_damage_+%_final")!.Value],
                    shot.SkillStats[Program.Stats.Key("cast_on_damage_taken_damage_+%_final")!.Value]);
                Game.RecordDamageTakenTriggers(conn, threshold * 10, 0);
                Game.ReleaseDamageTakenTriggers(conn, session);
                session.AdvanceTo(0);
                Assert.Single(session.Runtime.Projectiles);
                Assert.Equal(paid, conn.PlayerMana);
                // Failed payment cannot create a projectile or consume the player's action.
                conn.PlayerMana = 0;
                Game.RecordDamageTakenTriggers(conn, threshold, 1000);
                Game.ReleaseDamageTakenTriggers(conn, session); session.AdvanceTo(0);
                Assert.Single(session.Runtime.Projectiles);
                conn.PlayerMana = paid;
                // ES and Mana absorb hits after the trigger's damage-taken observation.
                conn.Ward.SetMaximum(0);
                conn.EnergyShield.SetMaximum(1000, refill: true);
                Assert.Equal(0, Game.DamagePlayerHitTyped(conn, threshold, 0, 0, 1001,
                    new Dictionary<uint, int>(), GameServer.Core.World.DamagePacket.Empty));
                Assert.True(conn.EnergyShield.Current < 1000);
                Game.ReleaseDamageTakenTriggers(conn, session); session.AdvanceTo(0);
                Assert.Equal(2, session.Runtime.Projectiles.Count);
                conn.EnergyShield.SetMaximum(0);
                var manaDefence = new Dictionary<uint, int> { [Program.Stats.Key("base_damage_removed_from_mana_before_life_%")!.Value] = 100 };
                conn.PlayerMana = 1000;
                Assert.Equal(0, Game.DamagePlayerHitTyped(conn, threshold, 0, 0, 1002,
                    manaDefence, GameServer.Core.World.DamagePacket.Empty));
                Game.ReleaseDamageTakenTriggers(conn, session); session.AdvanceTo(0);
                Assert.Equal(3, session.Runtime.Projectiles.Count);
                paid = conn.PlayerMana;
                Game.RecordDamageTakenTriggers(conn, threshold, 1003);
                var host = Game.ItemsOf(conn).EquipmentSnapshot().Single(e => e.Row == PlayerInventories.Weapon1);
                var beforeSwap = Game.SupportedSkill(conn, skill.Id).SourceGem;
                Game.ItemsOf(conn).Change(PlayerInventories.Weapon1, host.Item.Id, item => item with
                {
                    Sockets = [new(SocketColour.White, item.Sockets[0].Gem! with { }), item.Sockets[1]],
                });
                Assert.Equal(skill.Id, Game.GemSkillEffects(conn).Single(g => g.Name == "Fireball").Id);
                Assert.NotSame(beforeSwap, Game.SupportedSkill(conn, skill.Id).SourceGem);
                Game.ReleaseDamageTakenTriggers(conn, session); session.AdvanceTo(0);
                Assert.Equal(3, session.Runtime.Projectiles.Count);
                Assert.Equal(paid, conn.PlayerMana);
                Game.RecordDamageTakenTriggers(conn, threshold, 1004);
                Game.ItemsOf(conn).Remove(PlayerInventories.Weapon1, host.Item.Id);
                Game.ReleaseDamageTakenTriggers(conn, session); session.AdvanceTo(0);
                Assert.Equal(3, session.Runtime.Projectiles.Count);
                Assert.Equal(paid, conn.PlayerMana);
                // Triggered Spark still needs the native wander/duration profile, even though the server draws it.
                Equip(conn, "SupportGemCastOnDamageTaken", "SkillGemSpark");
                var spark = Game.GemSkillEffects(conn).Single(g => g.Name == "Spark");
                Game.RecordDamageTakenTriggers(conn, threshold, 1005);
                Game.ReleaseDamageTakenTriggers(conn, session); session.AdvanceTo(0);
                var sparkShot = session.Runtime.Projectiles.First(p => ((ProjectileOffense)p.Use.Payload!).SourceSkill == spark.Id);
                Assert.False(((ProjectileOffense)sparkShot.Use.Payload!).ClientPredicted);
                Assert.NotNull(sparkShot.Profile.Legs);
                Assert.Equal(150, sparkShot.Profile.LegLeash);
                VerifyNonProjectile(conn, session);
                VerifyGuards(conn, session);
                paid = conn.PlayerMana;
                conn.Clock.Stop();
                Game.RecordDamageTakenTriggers(conn, threshold, 1);
                Game.ReleaseDamageTakenTriggers(conn, session);
                Assert.Equal(paid, conn.PlayerMana);
            }
            finally { session.Invalidate(); }
        });
        VerifyCriticalTrigger();
    }
    static void VerifyNonProjectile(ClientState conn, Game.ProjectileSession session)
    {
        foreach (var item in Game.ItemsOf(conn).Items(PlayerInventories.Weapon1).ToArray())
            Game.ItemsOf(conn).Remove(PlayerInventories.Weapon1, item.Id);
        Equip(conn, "SupportGemCastOnDamageTaken", "SkillGemIceNova");
        var nova = Game.GemSkillEffects(conn).Single(g => g.Name == "Ice Nova");
        Assert.True(Game.TriggerPayloadAvailable(conn, Game.SupportedSkill(conn, nova.Id)));
        Assert.Null(Game.GemSkillInstanceDisabled(conn, nova.Effect, nova.Id, triggered: true));
        var origin = conn.Walk.Position(0);
        var victim = new SpawnedMonster("trigger-target", new(origin.X + 1, origin.Y, 0), 0, 0, 100000)
            { Hostile = true };
        var friendly = new SpawnedMonster("friendly", new(origin.X, origin.Y, 0), 0, 0, 100000) { Hostile = false };
        conn.Monsters[0x800abcd] = victim; conn.Monsters[0x800abce] = friendly;
        int action = conn.ActionCount;
        Game.ReleaseTriggeredNonProjectile(conn, session, nova.Id, (1000, 1000), () => false).GetAwaiter().GetResult();
        Assert.Equal(100000u, victim.Life);
        Game.ReleaseTriggeredNonProjectile(conn, session, nova.Id, (1000, 1000), () => true).GetAwaiter().GetResult();
        Assert.True(victim.Life < 100000); // Self-centred nova ignores the remote aim.
        Assert.Equal(100000u, friendly.Life);
        Assert.Equal(action, conn.ActionCount);
        foreach (var item in Game.ItemsOf(conn).Items(PlayerInventories.Weapon1).ToArray())
            Game.ItemsOf(conn).Remove(PlayerInventories.Weapon1, item.Id);
        Equip(conn, "SupportGemCastOnDamageTaken", "SkillGemFlammability");
        var curse = Game.GemSkillEffects(conn).Single(g => g.Name == "Flammability");
        Assert.True(Game.TriggerPayloadAvailable(conn, Game.SupportedSkill(conn, curse.Id)));
        Game.ReleaseTriggeredNonProjectile(conn, session, curse.Id, origin, () => true).GetAwaiter().GetResult();
        Assert.Single(victim.Curses.All);
        Assert.Empty(friendly.Curses.All);
        Assert.True(victim.Effects["base_fire_damage_resistance_%"] < 0);
        Assert.Equal(action, conn.ActionCount);
    }
    static void VerifyGuards(ClientState conn, Game.ProjectileSession session)
    {
        void Change(string active)
        {
            foreach (var item in Game.ItemsOf(conn).Items(PlayerInventories.Weapon1).ToArray())
                Game.ItemsOf(conn).Remove(PlayerInventories.Weapon1, item.Id);
            Equip(conn, "SupportGemCastOnDamageTaken", active);
        }
        Change("SkillGemSteelskin");
        var steel = Game.GemSkillEffects(conn).Single(g => g.Name == "Steelskin");
        Assert.True(Game.TriggerPayloadAvailable(conn, Game.SupportedSkill(conn, steel.Id)));
        double now = conn.Clock.Elapsed.TotalSeconds;
        conn.PlayerMana = 1000;
        int action = conn.ActionCount;
        Game.RecordDamageTakenTriggers(conn, 528, now);
        Game.ReleaseDamageTakenTriggers(conn, session);
        Assert.True(conn.PlayerMana < 1000);
        Assert.False(Game.TriggerGuardReady(conn, now));
        Assert.Equal(1, Game.CharacterPlayerStats(conn)[Program.Stats.Key("affected_by_guard_skill")!.Value]);
        var guardPacket = Game.TriggeredGuardPresentation(conn, now);
        Assert.Equal(1u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(guardPacket.AsSpan(guardPacket.Length - 8)));
        Assert.Equal(54u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(guardPacket.AsSpan(guardPacket.Length - 4)));
        Assert.Equal((46, 0, 0), Game.TriggeredGuardHit(conn, 100, 0, 0, now));
        // The level-one shield absorbs only 54, and starts its 3s cooldown when depleted.
        Assert.False(Game.TriggerGuardReady(conn, now + 2.99));
        Assert.True(Game.TriggerGuardReady(conn, now + 3.01));
        Assert.Equal(action, conn.ActionCount);
        Change("SkillGemImmortalCall");
        var call = Game.GemSkillEffects(conn).Single(g => g.Name == "Immortal Call");
        var charges = Game.ChargesOf(conn);
        charges.State.Set(ChargeType.Endurance, 3, now + 4, charges.Limits(ChargeType.Endurance));
        Game.StartTriggeredGuard(conn, session, call.Id, now + 4);
        Assert.Equal(0, charges.Effective(ChargeType.Endurance));
        Assert.Equal((412, 750, 1000), Game.TriggeredGuardHit(conn, 1000, 1000, 1000, now + 4.1));
        Assert.False(Game.TriggerGuardReady(conn, now + 8.59));
        Assert.True(Game.TriggerGuardReady(conn, now + 8.61));
        Assert.Equal(action, conn.ActionCount);
    }
    static void VerifyCriticalTrigger()
    {
        WithCharacter(conn =>
        {
            conn.Clock.Start();
            EquipCriticalLinks(conn);
            var attack = Game.GemSkillEffects(conn).Single(s => s.Name == "Kinetic Blast");
            var fireball = Game.GemSkillEffects(conn).Single(s => s.Name == "Fireball");
            var pulse = Game.GemSkillEffects(conn).Single(s => s.Name == "Freezing Pulse");
            var source = Assert.IsType<Game.LinkedTriggerSource>(Game.CaptureCriticalTriggerSource(conn, attack.Id));
            Assert.Equal(new[] { fireball.Id, pulse.Id }, source.Spells.Select(s => s.Skill));
            Assert.Contains("critical strike", Game.TriggerOnlyReason(Game.SupportedSkill(conn, fireball.Id))!);
            Assert.Null(Game.TriggerOnlyReason(Game.SupportedSkill(conn, attack.Id)));
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
            using var writer = new System.Net.Sockets.TcpClient(); writer.Connect((System.Net.IPEndPoint)listener.LocalEndpoint);
            using var reader = listener.AcceptTcpClient();
            var session = new Game.ProjectileSession("coc-test", writer.GetStream(), new ConnectionCiphers(new byte[64], false), conn);
            try
            {
                int action = conn.ActionCount; uint mana = conn.PlayerMana;
                Assert.False(Game.ReleaseCriticalTrigger(conn, session, source, false, true, (20, 0), 10));
                Assert.False(Game.ReleaseCriticalTrigger(conn, session, source, true, false, (20, 0), 10));
                Assert.Equal(mana, conn.PlayerMana);
                Assert.True(Game.ReleaseCriticalTrigger(conn, session, source, true, true, (20, 0), 10));
                session.AdvanceTo(0);
                var first = Assert.IsType<ProjectileOffense>(Assert.Single(session.Runtime.Projectiles).Use.Payload);
                Assert.Equal(fireball.Id, first.SourceSkill); Assert.False(first.ClientPredicted);
                var inputs = Game.SupportedHitInputsFor(conn, fireball.Id, area: false)!;
                var full = Game.SupportedHitFrom(inputs with { Modifiers = inputs.Modifiers
                    .Where(m => m.Name != "support_cast_on_crit_spell_damage_+%_final").ToList() }, suppress: false);
                Assert.Equal(full[GameServer.Core.World.DamageType.Fire].Min * .81,
                    first.Hit[GameServer.Core.World.DamageType.Fire].Min, 5);
                Assert.True(Game.ReleaseCriticalTrigger(conn, session, source, true, true, (20, 0), 10));
                session.AdvanceTo(0);
                Assert.Equal(2, session.Runtime.Projectiles.Count);
                Assert.Contains(session.Runtime.Projectiles, p => ((ProjectileOffense)p.Use.Payload!).SourceSkill == pulse.Id);
                Assert.False(Game.ReleaseCriticalTrigger(conn, session, source, true, true, (20, 0), 10));
                conn.PlayerMana = 0;
                Assert.False(Game.ReleaseCriticalTrigger(conn, session, source, true, true, (20, 0), 11));
                Assert.Equal(2, session.Runtime.Projectiles.Count);
                conn.PlayerMana = mana;
                var host = Game.ItemsOf(conn).Items(PlayerInventories.BodyArmour).Single();
                Game.ItemsOf(conn).Change(PlayerInventories.BodyArmour, host.Id, item => item with
                { Sockets = [.. item.Sockets.Select(s => s with { Gem = PlayerInventories.PreserveGemIdentity(s.Gem!, s.Gem! with { GemExperience = 1 }) })] });
                Assert.True(Game.ReleaseCriticalTrigger(conn, session, source, true, true, (20, 0), 12));
                session.AdvanceTo(0); Assert.Equal(3, session.Runtime.Projectiles.Count);
                Game.ItemsOf(conn).Change(PlayerInventories.BodyArmour, host.Id, item => item with
                { Sockets = [item.Sockets[0] with { Gem = item.Sockets[0].Gem! with { } }, .. item.Sockets.Skip(1)] });
                uint before = conn.PlayerMana;
                Assert.False(Game.ReleaseCriticalTrigger(conn, session, source, true, true, (20, 0), 13));
                Assert.Equal(before, conn.PlayerMana);
                Assert.Equal(action, conn.ActionCount);
            }
            finally { session.Invalidate(); }
        });
        WithCharacter(conn =>
        {
            conn.Clock.Start(); EquipCriticalLinks(conn);
            conn.PlayerStats[Program.Stats.Key("global_always_hit")!.Value] = 1;
            var attack = Game.GemSkillEffects(conn).Single(s => s.Name == "Kinetic Blast");
            Assert.True(Game.SupportedCriticalChance(conn, attack.Id) > 0);
            var victim = new SpawnedMonster("critical-contact", new(1, 0, 0), 0, 0, 100000) { Hostile = true };
            conn.Monsters[0x800abc] = victim;
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
            using var writer = new System.Net.Sockets.TcpClient(); writer.Connect((System.Net.IPEndPoint)listener.LocalEndpoint);
            using var reader = listener.AcceptTcpClient();
            var session = Game.ProjectilesFor("coc-contact", writer.GetStream(), new ConnectionCiphers(new byte[64], false), conn);
            try
            {
                uint mana = conn.PlayerMana; int action = conn.ActionCount;
                Game.AreaSkillHit("coc-contact", writer.GetStream(), session.ciphers, conn, attack.Id, attack,
                    (0, 0), (1, 0), immediate: true, criticalRoll: new SkillCriticalRoll(0)).GetAwaiter().GetResult();
                Assert.True(victim.Life < 100000);
                Assert.True(conn.PlayerMana < mana); // The accepted critical contact paid for its linked spell.
                Assert.Equal(action, conn.ActionCount);
            }
            finally { session.Invalidate(); }
        });
    }
    static void EquipCriticalLinks(ClientState conn)
    {
        var names = new[] { "SkillGemKineticBlast", "SkillGemFireball", "SkillGemFreezingPulse", "SupportGemCastOnCrit" };
        var robe = ItemCatalog.Create(Program.Items.Find("Simple Robe")!) with
        { Sockets = [.. names.Select(n => new ItemSocket(SocketColour.White,
            ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + n)!)))], Links = [4] };
        Game.ItemsOf(conn).Add(PlayerInventories.BodyArmour, 0, 0, robe);
        Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!));
    }
    static void Equip(ClientState conn, string support, string active = "SkillGemFireball")
    {
        var fireball = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + active)!);
        var trigger = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + support)!);
        var wand = ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!) with
        { Sockets = [new(SocketColour.White, fireball), new(SocketColour.White, trigger)], Links = [2] };
        Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, wand);
    }
    sealed class ZeroCritical : Random { public override int Next(int maxValue) => 0; }
    static void WithCharacter(Action<ClientState> run)
    {
        string path = Path.Combine(Path.GetTempPath(), $"cwdt-{Guid.NewGuid():N}.db");
        var old = Program.GameLogicOrNull; var oldLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game"); Game.CombatLifetime = lifetime; Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "cwdt", 1000, 1000, 100, 100, null, []), 1, 1)
        { CriticalRandom = new ZeroCritical(), Character = "Cwdt" + Guid.NewGuid().ToString("N"), PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
          Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
          LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])) };
        conn.Clock.Stop();
        foreach (string name in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" }) conn.PlayerStats[Program.Stats.Key(name)!.Value] = 1000;
        try { run(conn); }
        finally { PlayerInventories.Forget(conn.Character!); lifetime.Stop(_ => { }); Game.CombatLifetime = oldLifetime; Program.SetGameLogic(old!); TestEnvironment.ReleaseSaves(path); }
    }
}
