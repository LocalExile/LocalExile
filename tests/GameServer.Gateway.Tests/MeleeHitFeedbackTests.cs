using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class MeleeHitFeedbackTests
{
    [Fact]
    public void An_authoritative_heavy_strike_keeps_typed_critical_feedback_without_replaying_a_root_action()
    {
        var chaos = DamagePacket.Of(DamageType.Chaos, 100, 100);
        foreach (string effect in new[] { "HeavyStrike", "HeavyStrikeAltX", "HeavyStrikeAltY" })
        {
            string art = Assert.IsType<string>(Game.MeleeImpactArt(effect));
            var packet = Game.MeleeHitFeedback(0x1000, 0x1234, 0x8000012, Game.HitCode(chaos), 0x40,
                critical: true, impactArt: art, orderKey: 0x1000, checkpoint: 0x7000);
            Assert.Equal(InstanceProtocol.ActionHitId, BinaryPrimitives.ReadUInt16BigEndian(packet));
            Assert.Equal(0x1000u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(6)));
            Assert.Equal(0x7000, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(10)));
            Assert.Equal(0xffff, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(12)));
            Assert.Equal(0x8000012u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(16)));
            Assert.Equal(0x4230, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(20)));
            Assert.Equal(3, packet[22]); // Immediate hit data, never an unconsumable queued result.
            Assert.Equal(4, packet[23]); // Actual Chaos hit, not the old physical default.
            Assert.Equal(0x40, packet[24]);
        }
        // A different skill keeps its real sequence and its existing native hit presentation.
        Assert.Null(Game.MeleeImpactArt("Sweep"));
        Assert.Equal(InstanceProtocol.ActionHit(0x1000, 0x1234, 0x8000012, flags: 0x4204, kind: 3, code: 2),
            Game.MeleeHitFeedback(0x1000, 0x1234, 0x8000012, 2, 0, false, null));
    }

    [Fact]
    public void Heavy_strike_uses_its_actor_stance_clip_instead_of_the_default_attack()
    {
        using var f = new AnimatedAdmissionTests.Fixture();
        var conn = new Player(f.Player.Layout, 1, 1)
        {
            Character =
            {
                Name = f.Player.Character.Name,
                ClassRow = Program.Skills.RowOfClass(1),
                Experience = f.Player.Character.Experience,
            },
            Pools = f.Player.Pools,
            PlayerLife = f.Player.PlayerLife,
            LeagueModule = f.Player.LeagueModule,
        };
        var items = Game.ItemsOf(conn);
        items.Remove(PlayerInventories.Weapon1, Assert.Single(items.Items(PlayerInventories.Weapon1)).Id);
        var weapon = ItemCatalog.Create(Program.Items.Find("Driftwood Maul")!);
        items.Put(PlayerInventories.Weapon1, new(1, 0, 0, weapon));

        int effect = Program.Skills.EffectRow("HeavyStrike");
        double delay = Game.GemAttackContactDelay(conn, effect, .6, out var clip, out double fraction);
        Assert.Equal("bash1_maul", clip);
        Assert.Equal(799d / 1166, fraction, 6);
        Assert.Equal(AttackTiming.ContactDelay(.6, fraction, 0), delay, 6);
        var contacts = new AnimationContacts();
        var maul = new WeaponProfile(Shield: false, DualWield: false, Staff: false, Bow: false,
            DualClaws: false, TwoHand: true, Melee: true, TwoHandMelee: true, Mace: true);
        Assert.NotEqual(contacts.AttackContact("Metadata/Characters/Str/Str.ao", maul), fraction);
        Assert.Null(contacts.ActAttackContact("Metadata/Characters/Str/Str.ao", maul, "GlacialHammer"));
    }

    [Fact]
    public async Task Golem_minion_and_reservation_hits_emit_immediate_typed_action_hit_feedback()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-hit-feedback-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        using var wire = new Wire();
        Saves.Open(path);
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
        var conn = new Player(new(1, "hit-feedback", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character =
            {
                Name = "HitFeedback" + Guid.NewGuid().ToString("N"),
                Experience = CharacterStats.ExperienceFor(100),
            },
            Connection =
            {
                PlayerSent = true,
                Session = new("test", wire.Stream, wire.Ciphers),
            },
            Instance = new(),
            PlayerLife = 10000,
            PlayerMana = 10000,
            Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("hit-test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        conn.Golems.Restored = true;
        // 1000 more of each attribute so PoolsFor inside ToggleAura keeps Herald of Ice's gem requirements met.
        conn.God = true;
        using var requirements = TestRequirements.Waive(conn);
        try
        {
            // 1. Golem taking a Chaos spell hit from (100, 100) while standing at (110, 100).
            var golemGem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemSummonFlameGolem")!);
            var weapon = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!) with
            { Sockets = [new(SocketColour.White, golemGem)] };
            Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, weapon);
            var golemSource = Game.GemSkillEffects(conn).Single(g => g.Name == "Summon Flame Golem");
            var definition = Game.GolemData.Find("SummonFlameGolem")!;
            uint golemId = await Game.SummonGolem(conn, golemSource.Id, definition, (110, 100));
            var golem = conn.Golems.All[golemId];
            conn.Minions.OutputQueue.Clear();

            const uint attackerId = 0x08000010;
            var attacker = new SpawnedMonster("chaos-attacker", new(100, 100, 1), 100, 8, 1000);
            var chaosSpell = new MonsterAttack(0x4000, 1, 100, DamagePacket.Of(DamageType.Chaos, 200, 200)) { Spell = true };
            Assert.True(Game.GolemTakeHitLocked(conn, golem, attackerId, attacker, chaosSpell, 2));

            int golemHitIndex = conn.Minions.OutputQueue.FindIndex(o =>
                o.Packet.Length >= 25 && BinaryPrimitives.ReadUInt16BigEndian(o.Packet) == InstanceProtocol.ActionHitId);
            int golemLifeIndex = conn.Minions.OutputQueue.FindIndex(o =>
                o.Kind == PlayerMinions.OutputKind.Life && o.TargetId == golemId);
            Assert.True(golemHitIndex >= 0 && golemLifeIndex > golemHitIndex);
            var golemHit = conn.Minions.OutputQueue[golemHitIndex];
            Assert.Equal(attackerId, golemHit.WorldId);
            Assert.Equal(attackerId, BinaryPrimitives.ReadUInt32BigEndian(golemHit.Packet.AsSpan(2)));
            Assert.Equal(0xffff, BinaryPrimitives.ReadUInt16BigEndian(golemHit.Packet.AsSpan(12)));
            Assert.Equal(golemId, BinaryPrimitives.ReadUInt32BigEndian(golemHit.Packet.AsSpan(16)));
            ushort golemFlags = BinaryPrimitives.ReadUInt16BigEndian(golemHit.Packet.AsSpan(20));
            Assert.Equal(0x4201, golemFlags & ~0x0020);
            Assert.Equal(3, golemHit.Packet[22]);
            Assert.Equal(4, golemHit.Packet[23]); // Chaos damage code.
            Assert.Equal(Game.HitAngle(10, 0), golemHit.Packet[24]);

            // 2. Minion attack hitting a hostile target with Fire damage.
            const uint minionId = 0x04000020, targetId = 0x08000021;
            var fireAttack = new MonsterAttack(0x4000, 1, 20, DamagePacket.Of(DamageType.Fire, 250, 250));
            var minion = new SpawnedMonster("minion", new(100, 100, 1), 100, 2, 1000)
            { Hostile = false, AlliedFighter = true, Attack = fireAttack };
            var target = new SpawnedMonster("target", new(110, 100, 1), 100, 2, 10000);
            conn.Area.Monsters[minionId] = minion;
            conn.Area.Monsters[targetId] = target;
            var landed = Game.MinionHitTargetLocked(conn, minionId, minion, targetId, target, 3, new Random(1),
                checkReach: true, accuracy: 1_000_000);
            Assert.NotNull(landed);
            Assert.Equal(2, landed.Value.HitCode); // Fire damage code.
            byte[] minionPacket = Game.MinionActionHit(minionId, targetId, landed.Value.HitCode, Game.HitAngle(10, 0));
            Assert.Equal(InstanceProtocol.ActionHitId, BinaryPrimitives.ReadUInt16BigEndian(minionPacket));
            Assert.Equal(minionId, BinaryPrimitives.ReadUInt32BigEndian(minionPacket.AsSpan(2)));
            Assert.Equal(0xffff, BinaryPrimitives.ReadUInt16BigEndian(minionPacket.AsSpan(12)));
            Assert.Equal(targetId, BinaryPrimitives.ReadUInt32BigEndian(minionPacket.AsSpan(16)));
            Assert.Equal(0x4201, BinaryPrimitives.ReadUInt16BigEndian(minionPacket.AsSpan(20)));
            Assert.Equal(3, minionPacket[22]);
            Assert.Equal(2, minionPacket[23]);
            Assert.Equal(Game.HitAngle(10, 0), minionPacket[24]);

            // 3. Herald of Ice secondary burst emits immediate Cold 0x1af before Life update.
            conn.Area.Monsters.Remove(targetId);
            Game.ItemsOf(conn).Remove(PlayerInventories.Weapon1, Game.ItemsOf(conn).Items(PlayerInventories.Weapon1).Single().Id);
            var hoiGem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemHeraldOfIce")!);
            Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, weapon with { Sockets = [new(SocketColour.White, hoiGem)] });
            var hoiSkill = Game.GemSkillEffects(conn).Single(g => Program.Skills.EffectId(g.Effect) == "HeraldOfIce");
            var reservation = Game.SupportedReservationCost(conn, hoiSkill.Id);
            Assert.NotNull(reservation);
            Assert.True(await Game.ToggleAura(conn, hoiSkill.Id, hoiSkill.Effect,
                hoiSkill.Name, reservation!.Value, hoiSkill.Level, hoiSkill.Quality));
            var hoiAura = conn.Auras[hoiSkill.Id];
            const uint coldTargetId = 0x08000030;
            conn.Area.Monsters[coldTargetId] = new("cold-target", new(100, 90, 1), 100, 2, 10000);
            conn.Minions.OutputQueue.Clear();

            Game.ReservationDamageLocked(conn, hoiAura, (100, 100), 4);
            int hoiHitIndex = conn.Minions.OutputQueue.FindIndex(o =>
                o.Packet.Length >= 25 && BinaryPrimitives.ReadUInt16BigEndian(o.Packet) == InstanceProtocol.ActionHitId);
            int hoiLifeIndex = conn.Minions.OutputQueue.FindIndex(o =>
                o.Kind == PlayerMinions.OutputKind.Life && o.TargetId == coldTargetId);
            Assert.True(hoiHitIndex >= 0 && hoiLifeIndex > hoiHitIndex);
            var hoiHit = conn.Minions.OutputQueue[hoiHitIndex];
            Assert.Equal(conn.Id, hoiHit.WorldId);
            Assert.Equal(conn.Id, BinaryPrimitives.ReadUInt32BigEndian(hoiHit.Packet.AsSpan(2)));
            Assert.Equal(0xffff, BinaryPrimitives.ReadUInt16BigEndian(hoiHit.Packet.AsSpan(12)));
            Assert.Equal(coldTargetId, BinaryPrimitives.ReadUInt32BigEndian(hoiHit.Packet.AsSpan(16)));
            ushort hoiFlags = BinaryPrimitives.ReadUInt16BigEndian(hoiHit.Packet.AsSpan(20));
            Assert.Equal(0x4201, hoiFlags & ~0x0020);
            Assert.Equal(3, hoiHit.Packet[22]);
            Assert.Equal(1, hoiHit.Packet[23]); // Cold damage code.
            Assert.Equal(Game.HitAngle(0, -10), hoiHit.Packet[24]);
        }
        finally
        {
            conn.Closed = true;
            PlayerInventories.Forget(conn.Character.Name!);
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }

    [Fact]
    public async Task Scheduled_melee_hits_keep_typed_hit_code_and_lockstep_hit_order()
    {
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        using var wire = new Wire();
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
        var conn = new Player(new(1, "scheduled-melee", 300, 300, 100, 100, null, []), 1, 1)
        {
            Instance = new(),
            PlayerLife = 1000,
            PlayerMana = 1000,
            Connection =
            {
                PlayerSent = true,
                LockstepClient = true,
                Session = new("scheduled-test", wire.Stream, wire.Ciphers),
            },
        };
        conn.Walk.Teleport(100, 100, 0);
        const uint targetId = 0x08000042;
        var monster = new SpawnedMonster("target", new(108, 100, 0), 100, 2, 1000);
        conn.Area.Monsters[targetId] = monster;
        var session = new Game.ProjectileSession(conn);
        conn.ProjectileRuntime = session;
        try
        {
            Assert.True(session.Active);
            Game.CommitScheduledMeleeHit(session, targetId, monster, new(50, false), 50, critical: true,
                new(0), 0x1234, "Lightning Strike", settleKill: false, hitCode: 3);
            var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Enqueue(() => { flushed.TrySetResult(); return Task.CompletedTask; });
            await flushed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            byte[] expectedHit = InstanceProtocol.ActionHit(conn.Id, 0x1234, targetId,
                flags: 0x4221, kind: 3, code: 3, angle: Game.HitAngle(8, 0),
                orderKey: conn.Id, checkpoint: Game.HitCheckpoint);
            byte[] expectedLife = InstanceProtocol.LifeUpdate(targetId, 50,
                orderKey: conn.Id, checkpoint: Game.HitLifeCheckpoint);
            Assert.Equal(expectedHit, wire.Read(expectedHit.Length));
            Assert.Equal(expectedLife, wire.Read(expectedLife.Length));
        }
        finally
        {
            conn.Closed = true;
            session.Invalidate();
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
        }
    }

    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new();
        readonly TcpClient reader;
        readonly ConnectionCiphers decrypt = new(new byte[64], true);
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        public NetworkStream Stream => writer.GetStream();

        public Wire()
        {
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
            reader.ReceiveTimeout = 5000;
        }

        public byte[] Read(int count)
        {
            var bytes = new byte[count];
            reader.GetStream().ReadExactly(bytes);
            decrypt.Receive.Process(bytes);
            return bytes;
        }

        public void Dispose()
        {
            reader.Dispose();
            writer.Dispose();
            listener.Stop();
        }
    }
}
