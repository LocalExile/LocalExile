using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public class WarcryAdmissionTests
{
    const string Flag = "your_warcry_skills_are_disabled";
    static uint Key => Program.Stats.Key(Flag)!.Value;
    static MethodInfo Method(string name) => typeof(Program).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    sealed class Fixture : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new();
        readonly TcpClient reader;
        readonly ConnectionCiphers receive = new(new byte[64], isClient: true);
        public readonly ConnectionCiphers Ciphers = new(new byte[64], isClient: false);
        public readonly ClientState Conn;
        public readonly int Effect;
        public ushort Skill => Program.GemSkillIdPool.Value[0];
        public uint Cost => (uint)Program.Skills.EffectStats(Effect, 1)
            .Single(s => s.Item1 == Program.Stats.Key("base_mana_cost")).Item2;
        public ChargeSession Charges => (ChargeSession)Method("ChargesOf").Invoke(null, [Conn])!;
        public SemaphoreSlim Gate => Program.SendGates.GetValue(Ciphers, _ => new(1, 1));
        public bool HasOutput => reader.Available > 0;

        public Fixture(string gemId = "EnduringCry")
        {
            TestSaves.Ensure();
            Conn = new(new(ushort.MaxValue, "WarcryAdmission", 128, 128, 32, 32, null, []), 0, 1)
            { Character = "Warcry" + Guid.NewGuid().ToString("N"), ClassRow = 1, PlayerSent = true,
                PlayerLife = 500, PlayerMana = 1000, ActionCount = 17, ChannelSkill = 0x104,
                Pools = new(100, 500, 1000, 100, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                SentEnergyShield = 0, SentRechargeActive = false, SentEsRecoveryRate = 0 };
            // Item activation reads the requirement attributes, not the pools above (socketed gems count).
            foreach (string attribute in new[] { "strength", "dexterity", "intelligence" })
                Conn.PlayerStats[Program.Stats.Key($"virtual_{attribute}_for_requirements")!.Value] = 1000;
            var gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGem" + gemId)!);
            var host = ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!) with
                { Sockets = [new(SocketColour.White, gem)] };
            PlayerInventories.For(Conn.Character, Program.Items).Add(PlayerInventories.Weapon1, 0, 0, host);
            Effect = Program.Items.GemEffects(gem).First();
            Assert.Null(Method("GemDisabled").Invoke(null, [Conn, Effect])); // Requirements must really pass.
            Conn.Monsters.Add(42, new("WarcryTarget", new(34, 32, 30), 0, 2, 1000)
                { Hostile = true, Rarity = MonsterRarity.Rare });
            Conn.Clock.Start();
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
        }

        public Task Use(string route, ushort? skill = null)
        {
            ushort id = skill ?? Skill;
            if (route is "self" or "monster")
            {
                byte[] bytes = new LoginWriter().U16(0x24).U32(route == "self" ? 0x1000u : 42u)
                    .U16(id).U16(3).U16(0x408).ToArray();
                var use = InstanceTransfer.ReadUseSkillOnObject(bytes);
                return ((Task)Method("OnUseObject").Invoke(null,
                    ["warcry-test", writer.GetStream(), Ciphers, Conn, use, Conn.Clock.Elapsed.TotalSeconds])!)
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
            ushort message = route switch { "instant-object" => 0x191, "instant-ground" => 0x190, _ => 0x25 };
            var w = new LoginWriter().U16(message);
            if (message == 0x191) w.U32(0x1000).U16(id);
            else
            {
                w.U32(38).U32(32).U16(id);
                if (message == 0x25) w.U16(3).U16(0x408);
            }
            return Program.OnClientMessages("warcry-test", writer.GetStream(), Ciphers,
                [(message, w.ToArray())], [], Conn).WaitAsync(TimeSpan.FromSeconds(5));
        }

        public Task<bool> Cast() => (Task<bool>)Method("CastGemSkill").Invoke(null,
            ["warcry-test", writer.GetStream(), Ciphers, Conn, Skill, (32, 32), (38, 32), 0u, (ushort)0,
                null, null, false])!;

        public async Task<byte[]> Drain()
        {
            byte[] bytes = new byte[reader.Available];
            if (bytes.Length > 0) await reader.GetStream().ReadExactlyAsync(bytes);
            receive.Receive.Process(bytes);
            return bytes;
        }

        public async Task ExpectPackets(params byte[][] packets)
        {
            byte[] expected = [.. packets.SelectMany(p => p)];
            byte[] received = new byte[expected.Length];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await reader.GetStream().ReadExactlyAsync(received, timeout.Token);
            receive.Receive.Process(received);
            Assert.Equal(expected, received);
        }

        public void AssertUnspent()
        {
            Assert.Equal(17, Conn.ActionCount);
            Assert.Equal((ushort)0x104, Conn.ChannelSkill);
            Assert.Equal((32, 32), Conn.Walk.Destination);
            Assert.Equal(1000u, Conn.PlayerMana);
            Assert.Equal(500u, Conn.PlayerLife);
            Assert.Empty(Conn.SkillReadyAt);
            Assert.Empty(Conn.SingleUseCooldowns);
            Assert.False(Charges.Pending);
            Assert.Equal(0, Charges.Effective(ChargeType.Endurance));
            Assert.Equal(1000u, Conn.Monsters[42].Life);
        }

        public void AssertChargeRelease()
        {
            Charges.Step(Conn.Clock.Elapsed.TotalSeconds, _ => 0, () => default, true, false, false,
                new ScriptedRandom());
            Assert.Equal(2, Charges.Effective(ChargeType.Endurance));
        }

        public void Dispose()
        {
            Conn.Closed = true;
            writer.Dispose(); reader.Dispose(); listener.Stop();
            PlayerInventories.Forget(Conn.Character!);
        }
    }

    static async Task Until(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public async Task Shared_cast_entry_refuses_before_legacy_cooldown_or_payment()
    {
        using var f = new Fixture();
        f.Conn.PlayerStats[Key] = -7;
        Assert.False(await f.Cast());
        f.AssertUnspent();
    }

}
