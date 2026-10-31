using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class ChaosArmourTests
{
    static uint Key(string name) => Program.Stats.Key(name)!.Value;

    [Theory]
    [InlineData(1000, 50, 75, 1000, 355)]
    public void Chaos_hit_armour_reads_post_resistance_damage_and_keeps_the_existing_cap(
        int amount, int resistance, int maximum, int armour, int expected)
    {
        var pools = new CharacterStats.Pools(1, 100, 50, 0, 0, Armour: armour,
            ArmourAppliesToChaosDamage: true);
        int resisted = DamageModel.Mitigate(amount, resistance, maximum);
        Assert.Equal(expected, PlayerHitMitigation.AfterResistance(pools, DamageType.Chaos, resisted, 90, 90));
        // The denominator must read 500 at 50% resistance, not the raw 1000 (which would leave 415).
        Assert.Equal(armour, pools.Armour);
    }

    sealed class IncomingFixture : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new() { NoDelay = true };
        readonly TcpClient reader;
        readonly ConnectionCiphers ciphers = new(new byte[64], false);
        readonly ConnectionCiphers receive = new(new byte[64], true);
        public ClientState Conn { get; }

        public IncomingFixture()
        {
            TestSaves.Ensure();
            Conn = new(new(ushort.MaxValue, "IncomingChaos", 128, 128, 32, 32, null, []), 0, 1)
            { Character = "ChaosArmour" + Guid.NewGuid().ToString("N"), ClassRow = 1,
                PlayerLife = 10_000, PlayerMana = 1000, PlayerSent = true };
            Conn.Pools = Program.PoolsFor(Conn, 100);
            Conn.Clock.Start();
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
            Conn.Commands = (ChatCommands)typeof(Program).GetMethod("Commands",
                BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
                ["chaos-armour-test", writer.GetStream(), ciphers, Conn])!;
        }

        public void Set(string stat, int value) => Conn.PlayerStats[Key(stat)] = value;

        public void Defences()
        {
            // Fixed final defences isolate mitigation from equipment formulas; production never rewrites Armour.
            Conn.Pools = Program.PoolsFor(Conn, 100) with { Life = 10_000, Mana = 1000, Armour = 1000,
                FireResistance = 50, ChaosResistance = 50, EnergyShield = 300, Evasion = 0,
                BlockChance = 0, SpellBlockChance = 0 };
            Conn.EnergyShield.SetMaximum(300, refill: true);
        }

        public async Task Command(string line)
        {
            var input = new LoginWriter().U16(InstanceChat.ChatId).WStr(line).U8(0).ToArray();
            var messages = InstanceProtocol.SplitClientMessages(input, out var partial, out var unknown);
            Assert.Empty(partial); Assert.Empty(unknown); Assert.Single(messages);
            await Program.OnClientMessages("chaos-armour-test", writer.GetStream(), ciphers, messages, [], Conn)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }

        public async Task Swing(DamagePacket damage)
        {
            var monster = new SpawnedMonster("Chaos fixture", new(34, 32, 30), 0, 2, 100)
            { Attack = new(SkillCatalog.FirstSkillId, .002, 10, damage) };
            Conn.Monsters[42] = monster;
            Conn.EvasionEntropy = 100; // Force this contact to hit without changing the production RNG.
            Conn.LastEvasionAt = Conn.Clock.Elapsed.TotalSeconds;
            await Program.MonsterSwing("chaos-armour-test", writer.GetStream(), ciphers, Conn, 42u, monster,
                monster.ActionGeneration).WaitAsync(TimeSpan.FromSeconds(5));
        }

        public async Task<byte[]> Received(byte[] expected)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var received = new MemoryStream();
            byte[] buffer = new byte[4096];
            while (true)
            {
                int count = await reader.GetStream().ReadAsync(buffer, timeout.Token);
                if (count == 0) throw new EndOfStreamException("Expected gateway message was not received");
                byte[] chunk = buffer[..count];
                receive.Receive.Process(chunk);
                received.Write(chunk);
                if (received.GetBuffer().AsSpan(0, (int)received.Length).IndexOf(expected) >= 0)
                    return received.ToArray();
            }
        }

        public async Task<string> ReceivedText() => System.Text.Encoding.ASCII.GetString(
            await Received(System.Text.Encoding.ASCII.GetBytes("29% armour")));

        public void Dispose()
        {
            Conn.Closed = true;
            writer.Dispose(); reader.Dispose(); listener.Stop();
            PlayerInventories.Forget(Conn.Character!);
        }
    }
}
