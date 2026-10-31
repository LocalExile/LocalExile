using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class RecentEnemyHitGatewayTests
{
    static MethodInfo Method(string name) => typeof(Program).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
    static uint Key(string name) => Program.Stats.Key(name)!.Value;

    sealed class TraceRandom(int seed = 123, int? armourRoll = null, double? blockRoll = null) : Random(seed)
    {
        public readonly List<string> Draws = [];
        public override int Next(int maxValue)
        {
            Draws.Add($"next:{maxValue}");
            return maxValue == 100 && armourRoll is int roll ? roll : base.Next(maxValue);
        }
        public override int Next(int minValue, int maxValue)
        {
            Draws.Add($"range:{minValue}:{maxValue}");
            return base.Next(minValue, maxValue);
        }
        public override double NextDouble()
        {
            Draws.Add("double"); return blockRoll ?? base.NextDouble();
        }
    }

    sealed class Fixture : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new();
        readonly TcpClient reader;
        readonly ConnectionCiphers receiver = new(new byte[64], true);
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        public readonly ClientState Conn;
        public NetworkStream Stream => writer.GetStream();
        public SemaphoreSlim Gate => Program.SendGates.GetValue(Ciphers, _ => new(1, 1));
        public Fixture(int armour = 500, string? character = null, InstanceState? instance = null)
        {
            TestSaves.Ensure();
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
            Conn = new(new(ushort.MaxValue, "IncomingArmourTest", 128, 128, 32, 32, null, []), 0, 1)
            {
                Character = character ?? "IncomingArmour" + Guid.NewGuid().ToString("N"), ClassRow = 1,
                Instance = instance ?? new(),
                PlayerLife = 10000, PlayerMana = 50, PlayerSent = true,
                Pools = new(1, 10000, 1000, 0, 0, Armour: armour,
                    Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            };
            Conn.Commands = (ChatCommands)Method("Commands").Invoke(null, ["incoming-test", Stream, Ciphers, Conn])!;
            Set(RecentEnemyHits.PerHit, 10);
        }
        public void Set(string name, int value) => Conn.PlayerStats[Key(name)] = value;
        public int Count => Conn.RecentEnemyHits.Count(Conn.Clock.Elapsed.TotalSeconds);
        public int Chance => Program.CharacterPlayerStats(Conn)[Key(RecentEnemyHits.VirtualChance)];
        public Task Command(string text)
        {
            byte[] bytes = new LoginWriter().U16(InstanceChat.ChatId).WStr(text).U8(0).ToArray();
            var messages = InstanceProtocol.SplitClientMessages(bytes, out var carry, out var unknown);
            Assert.Empty(carry); Assert.Empty(unknown); Assert.Single(messages);
            return Program.OnClientMessages("incoming-test", Stream, Ciphers, messages, unknown, Conn)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        public SpawnedMonster Monster(DamagePacket? hit = null, double seconds = .002, bool hostile = true,
            MonsterModSet? mods = null)
        {
            var monster = new SpawnedMonster("incoming target", new(34, 32, 30), 0, 2, 1000)
            { Hostile = hostile, Mods = mods ?? MonsterModSet.None, Attack = new(SkillCatalog.FirstSkillId, seconds, 20,
                hit ?? DamagePacket.Of(DamageType.Physical, 100, 100)) };
            Conn.Monsters[42] = monster;
            return monster;
        }
        public Task Swing(SpawnedMonster monster)
        {
            lock (Conn.Monsters) return (Task)Method("MonsterSwing").Invoke(null,
                ["incoming-test", Stream, Ciphers, Conn, 42u, monster, monster.ActionGeneration])!;
        }
        public Task Present() => Program.SendRecentEnemyHits("incoming-test", Stream, Ciphers, Conn);
        public async Task<byte[]> Read()
        {
            var bytes = new byte[reader.Available];
            if (bytes.Length > 0) await reader.GetStream().ReadExactlyAsync(bytes);
            receiver.Receive.Process(bytes);
            return bytes;
        }
        public void Dispose()
        {
            Conn.Closed = true;
            writer.Dispose(); reader.Dispose(); listener.Stop();
            PlayerInventories.Forget(Conn.Character!);
        }
    }

    [Fact]
    public async Task Parsed_enemy_contacts_use_prior_history_and_leave_base_armour_and_attack_bonus_unchanged()
    {
        using var f = new Fixture();
        var random = new TraceRandom(armourRoll: 0); f.Conn.IncomingHitRandom = random;
        f.Set(ArmourAttackDamage.Per450Armour, 2);
        foreach (var (chance, damage) in new[] { (0, 50), (10, 33), (20, 33), (30, 33), (30, 33) })
        {
            Assert.Equal(chance, f.Chance);
            uint before = f.Conn.PlayerLife;
            await f.Command("/hit physical 100");
            Assert.Equal((uint)damage, before - f.Conn.PlayerLife);
            Assert.Equal(500, f.Conn.Pools.Armour);
            Assert.Equal(2, Program.CharacterPlayerStats(f.Conn)[Key("attack_damage_+%")]);
        }
        Assert.Equal(5, f.Count);
        Assert.Equal(4, random.Draws.Count);
        Assert.All(random.Draws, d => Assert.Equal("next:100", d));
        Assert.Equal(double.NegativeInfinity, f.Conn.LastEnemyHitAt);
    }

}
