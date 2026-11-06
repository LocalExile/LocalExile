using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Modding;
using MonsterRarity = GameServer.Core.Protocol.Instance.MonsterRarity;

namespace GameServer.Gateway.Tests;

public sealed class Act6EncounterTests
{
    [Fact]
    public async Task EscortAndBossProgressionCannotSkipPendingStages()
    {
        var first = new RailEscort([(0, 0), (13, 0), (13, 26)], 13);
        var second = new RailEscort([(100, 0), (100, 13)], 13);
        first.Advance(0.5, true);
        Assert.Equal((6.5, 0d), first.Position);
        first.Advance(100, false);
        Assert.Equal((6.5, 0d), first.Position);
        first.Advance(1, true);
        Assert.Equal((13d, 6.5), first.Position);
        Assert.Equal(2, first.Target);
        Assert.Equal(1.5, first.RemainingSeconds);
        Assert.False(second.Complete);
        first.Restore((14, 20));
        Assert.Equal((13d, 20d), first.Position);
        first.Advance(100, true);
        first.Advance(100, true);
        Assert.True(first.Complete);
        Assert.Equal((13d, 26d), first.Position);
        Assert.False(second.Complete);
        second.Advance(1, true);
        Assert.True(second.Complete);

        var gate = new LifePhaseGate([75, 50, 25]);
        uint life = 1000;
        Assert.False(gate.Begin(life, 1000));
        foreach (uint floor in new uint[] { 750, 500, 250 })
        {
            life = gate.Damage(life, 1000, int.MaxValue);
            Assert.Equal(floor, life);
            Assert.Equal(floor, gate.Damage(life, 1000, int.MaxValue));
            Assert.True(gate.Begin(life, 1000));
            Assert.False(gate.Begin(life, 1000));
            Assert.Equal(life, gate.Damage(life, 1000, int.MaxValue));
            gate.Finish();
            int completed = gate.Completed;
            gate.Finish();
            Assert.Equal(completed, gate.Completed);
        }
        gate.DamageTakenPercent = 20;
        Assert.Equal(230u, gate.Damage(life, 1000, 100));
        gate.DamageTakenPercent = 100;
        Assert.Equal(150u, gate.Damage(life, 1000, 100));
        Assert.Equal(0u, gate.Damage(life, 1000, int.MaxValue));
        Assert.False(gate.Begin(0, 1000));
        Assert.Equal(1u, new LifePhaseGate([75]).Damage(1, 1, int.MaxValue));

        await PartyEncounterContinuesWithoutItsRunnerAsTarget();
    }

    [Fact]
    public void CartAndTidePacketsPreserveFollowingComponentAndPacketBoundaries()
    {
        string[] components = ["Positioned", "Animated", "BaseEvents", "Stats", "Movement", "Transitionable", "MinimapIcon"];
        var blob = new QuestObject(471, 448, components, Team: 4).Blob();
        Assert.Equal(4, BitConverter.ToUInt16(blob, 14));
        // Positioned 18, Animated 1, Stats 10; Movement: presence byte, XY, four u32s, two flags.
        int movement = 2 + 18 + 1 + 10;
        Assert.Equal(0, blob[movement]);
        Assert.Equal(471, BitConverter.ToInt32(blob, movement + 1));
        Assert.Equal(448, BitConverter.ToInt32(blob, movement + 5));
        Assert.All(blob[(movement + 9)..(movement + 27)], b => Assert.Equal(0, b));
        Assert.Equal(new byte[] { 1, 0, 0 }, blob[(movement + 27)..]);
        Assert.Equal(5, QuestObject.TransitionableIndex(components));

        using var metadata = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes("data/tables/BuffDefinitions.json"));
        var row = metadata.RootElement[856];
        Assert.Equal("brine_king_tide", row.GetProperty("Id").GetString());
        Assert.False(row.GetProperty("IsRecovery").GetBoolean());
        Assert.False(row.GetProperty("__col44").GetBoolean());
        byte[] buff = BrineKing.TideBuff(0x1000, 7);
        Assert.Equal(27, buff.Length);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, buff[23..]);
        byte[] next = GameServer.Core.Protocol.Instance.InstanceProtocol.LifeUpdate(0x1000, 1000);
        byte[] stream = [.. buff, .. next];
        Assert.Equal(next, stream[27..]);

        MonsterSpellsSuppressCalculatedDamage();
    }

    static async Task PartyEncounterContinuesWithoutItsRunnerAsTarget()
    {
        using var runtime = new Runtime();
        using var hostWire = new Wire();
        using var peerWire = new Wire();
        using var lateWire = new Wire();
        var instance = new InstanceState();
        var host = runtime.Connection("BrineHost", instance, 800, 800, hostWire);
        var peer = runtime.Connection("BrinePeer", instance, 100, 100, peerWire);
        instance.World.Runner = host;
        const uint id = 0x08000000;
        var variety = Game.Monsters.Find(BrineKing.Boss)!;
        var boss = new SpawnedMonster("Brine King", new PlayerWalk(100, 100, 30), 60, 8, 1000)
        {
            Variety = variety, Level = 49, Rarity = MonsterRarity.Unique,
            States = BrineKing.StartStates(variety), LifePhases = new([75, 50, 25]),
            Attack = new(0x4005, 1, 8, DamagePacket.Empty) { Scripted = true },
        };
        instance.World.Monsters[id] = boss;
        host.MonsterViews[id] = peer.MonsterViews[id] = new((100, 100), (100, 100), boss.Life);
        host.AsleepMonsters.Add(id);
        var fight = new BrineKing.Fight(id, boss, host);
        await fight.Bind(host);
        await fight.Bind(peer);
        double now = 1;
        for (int stage = 0; stage < 3; stage++, now += 14)
        {
            boss.Life = boss.LifePhases.Damage(boss.Life, boss.MaxLife, int.MaxValue);
            await fight.Step(host, now);
            Assert.True(boss.LifePhases.Active);
            Assert.Same(peer, boss.Target);
            Assert.True(boss.BossBarShown);
            Assert.Equal(1, boss.States!.SharedStates[boss.States.IndexOf("boss_life_bar")]);
            Assert.Contains(id, peer.BossBarsSeen);
            Assert.DoesNotContain(id, host.BossBarsSeen);
            // Host death must not stop this shared phase while the other party member remains alive.
            host.PlayerLife = 0;
            await fight.Step(host, now + 13.1);
            Assert.False(boss.LifePhases.Active);
            Assert.Equal(stage + 1, boss.LifePhases.Completed);
            if (stage == 0)
            {
                uint wave = peer.QuestObjects.First(p => p.Value.Rule.ObjectType.EndsWith("tidal_wave/tidal_wave", StringComparison.Ordinal)).Key;
                string expected = Convert.ToHexString(InstanceEntities.TransitionableUpdate(wave, 3, 4));
                foreach (var wire in new[] { hostWire, peerWire })
                    Assert.Contains(Program.HistoryFor(wire.Ciphers).Lines(), line => line.EndsWith(expected, StringComparison.Ordinal));
            }
        }
        Assert.Equal(0u, boss.LifePhases.Damage(boss.Life, boss.MaxLife, int.MaxValue));

        instance.World.MonsterOrigins[id] = new(InstanceEntities.ObjectTypeHash(variety.Id),
            new MonsterObject(100, 100, boss.Life, variety.ComponentsOrBase, variety.AnimatedObject,
                MonsterRarity.Unique, StateMachine: boss.States), (ushort)variety.Row, boss.Level, MonsterRarity.Unique);
        var late = runtime.Connection("BrineLate", instance, 100, 100, lateWire);
        int generation = boss.ActionGeneration;
        await fight.Bind(late);
        Assert.Equal(generation, boss.ActionGeneration); // No Actor update before its create reaches this client.
        await Game.SyncInstanceMonsters("late", lateWire.Stream, lateWire.Ciphers, late);
        late.AsleepMonsters.Add(id);
        await fight.Bind(late);
        Assert.Equal(generation, boss.ActionGeneration); // Sleeping clients cannot consume Actor updates either.
        await Game.SyncInstanceMonsters("late", lateWire.Stream, lateWire.Ciphers, late);
        Assert.DoesNotContain(id, late.BossBarsSeen);
        late.AsleepMonsters.Remove(id);
        await fight.Bind(late);
        Assert.True(boss.ActionGeneration > generation);
        await Game.SyncInstanceMonsters("late", lateWire.Stream, lateWire.Ciphers, late);
        Assert.Contains(id, late.BossBarsSeen);
    }

    static void MonsterSpellsSuppressCalculatedDamage()
    {
        using var runtime = new Runtime();
        foreach (var (chance, flat, expected) in new[] { (0, 0, 330), (100, 0, 198), (100, 100, 258) })
        {
            var conn = new ClientState(new(1, "spell-mitigation", 1000, 1000, 100, 100, null, []), 1, 1)
            {
                Character = $"BrineSpell{chance}_{flat}", PlayerLife = 1000000,
                Pools = new(49, 1000000, 100, 2, 0, Armour: 10000, Evasion: 1000000,
                    SpellSuppressionChance: chance, SpellSuppressionEffect: 40),
            };
            conn.PlayerStats[Program.Stats.Key("physical_damage_taken_+")!.Value] = flat;
            var attacker = new SpawnedMonster("spell source", new PlayerWalk(100, 100, 1), 60, 1, 1000);
            var spell = new MonsterAttack(0x4000, 1, 8, DamagePacket.Of(DamageType.Physical, 1000, 1000)) { Spell = true };
            Game.MonsterHitOutcome? hit;
            lock (conn.Monsters)
                hit = Game.CommitMonsterHitLocked("Act6 spell", conn, 1, attacker, spell, 1, (100, 100), (100, 100), false);
            Assert.NotNull(hit);
            Assert.Equal(1000, hit.RawDamage);
            Assert.Equal(expected, hit.Damage);
            Assert.Equal(1000000u - (uint)expected, conn.PlayerLife);
        }
    }

    sealed class Runtime : IDisposable
    {
        readonly string saves = Path.Combine(Path.GetTempPath(), $"localexile-act6-{Guid.NewGuid():N}.db");
        readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
        readonly IModuleLifetime? previousLifetime = Game.CombatLifetime;
        readonly ModuleLifetime lifetime = new("Game");
        readonly List<ClientState> players = [];
        public Runtime()
        {
            Saves.Open(saves);
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        }
        public ClientState Connection(string character, InstanceState instance, int x, int y, Wire wire)
        {
            var conn = new ClientState(new(1, "brine-party-test", 1000, 1000, x, y, null, []), 1, 1)
            {
                Character = character, Instance = instance, PlayerLife = 1000000, PlayerSent = true,
                Pools = new(49, 1000000, 100, 2, 0), Wire = (character, wire.Stream, wire.Ciphers),
            };
            players.Add(conn);
            Program.InGame.TryAdd(conn, 0);
            return conn;
        }
        public void Dispose()
        {
            foreach (var player in players) { player.Closed = true; Program.InGame.TryRemove(player, out _); }
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(saves);
        }
    }

    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new();
        readonly TcpClient reader;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        public NetworkStream Stream => writer.GetStream();
        public Wire()
        {
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
        }
        public void Dispose() { reader.Dispose(); writer.Dispose(); listener.Stop(); }
    }
}
