using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public sealed class AttributeResourceTests : IDisposable
{
    readonly string name = "AttributeResources" + Guid.NewGuid().ToString("N");
    static MethodInfo Method(string name) => typeof(Program).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
    static uint Key(string name) => Program.Stats.Key(name)!.Value;

    ClientState Character()
    {
        TestSaves.Ensure();
        var conn = new ClientState(new(ushort.MaxValue, "AttributeResourceTest", 128, 128, 32, 32, null, []), 0, 1)
            { Character = name, ClassRow = SkillCatalog.WitchRow };
        conn.PlayerStats[Key("mana_regeneration_rate_+%")] = -100;
        conn.Auras[0x103] = new(0x103, Program.Skills.EffectRow("Haste"), "Haste", "Haste", 1,
            AuraCatalog.BuffRow("Haste"), 0, 5000);
        conn.Pools = Program.PoolsFor(conn, 100);
        return conn;
    }

    sealed class Wire : IDisposable
    {
        readonly TcpClient writer, reader;
        readonly ConnectionCiphers receive = new(new byte[64], true);
        public readonly ConnectionCiphers Send = new(new byte[64], false);
        public NetworkStream Stream => writer.GetStream();
        public ChatCommands Commands(ClientState conn) => (ChatCommands)Method("Commands")
            .Invoke(null, ["attribute-resource-test", Stream, Send, conn])!;
        Wire(TcpClient writer, TcpClient reader) => (this.writer, this.reader) = (writer, reader);

        public static async Task<Wire> Open()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var writer = new TcpClient();
            var accepting = listener.AcceptTcpClientAsync();
            await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            return new(writer, await accepting);
        }

        public async Task<byte[]> Read()
        {
            // A protocol marker after the completed operation avoids TCP chunk/Available timing assumptions.
            byte[] marker = InstanceChat.ChatLine("attribute-wire-end-" + Guid.NewGuid().ToString("N"));
            await Program.Send("attribute-resource-test", Stream, Send, marker.ToArray(), null);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var plain = new List<byte>();
            while (true)
            {
                var chunk = new byte[16384];
                int count = await reader.GetStream().ReadAsync(chunk, timeout.Token);
                Assert.True(count > 0);
                var decoded = chunk[..count];
                receive.Receive.Process(decoded);
                plain.AddRange(decoded);
                byte[] bytes = plain.ToArray();
                if (bytes.AsSpan().IndexOf(marker) >= 0) return bytes;
            }
        }

        public void Dispose() { writer.Dispose(); reader.Dispose(); }
    }

    static List<(uint Current, uint Reserved, byte Pool)> Resources(byte[] bytes)
    {
        var updates = new List<(uint, uint, byte)>();
        byte[] prefix = InstanceProtocol.LifeUpdate(Program.PlayerId, 0)[..12];
        for (int offset = 0; offset <= bytes.Length - 24; offset++)
            if (bytes.AsSpan(offset, 12).SequenceEqual(prefix))
            {
                updates.Add((BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 12)),
                    BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 16)), bytes[offset + 20]));
                offset += 23;
            }
        return updates;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Idle_raw_attributes_publish_reservation_changes_and_clamp_resources_without_refilling(bool dead)
    {
        var conn = Character();
        Assert.Equal(1245u, conn.Pools.Life);
        Assert.Equal(650u, conn.Pools.Mana);
        conn.PlayerLife = dead ? 0 : 1245u;
        conn.PlayerMana = dead ? 0 : 325u;
        using var wire = await Wire.Open();
        var commands = wire.Commands(conn);

        await commands.RunAsync("/set_stat additional_intelligence 100");
        var increased = Resources(await wire.Read());
        Assert.Contains((dead ? 0u : 325u, 350u, (byte)1), increased);
        Assert.Equal(dead ? 0u : 1245u, conn.PlayerLife);
        Assert.Equal(dead ? 0u : 325u, conn.PlayerMana);

        await commands.RunAsync("/set_stat all_attributes_+% -100");
        var decreased = Resources(await wire.Read());
        Assert.Contains((dead ? 0u : 317u, 317u, (byte)1), decreased);
        Assert.Contains((dead ? 0u : 1238u, 0u, (byte)0), decreased);
        Assert.Equal(dead ? 0u : 1238u, conn.PlayerLife);
        Assert.Equal(dead ? 0u : 317u, conn.PlayerMana);

        await commands.RunAsync("/set_stat all_attributes_+% 0");
        var restored = Resources(await wire.Read());
        Assert.Contains((dead ? 0u : 317u, 350u, (byte)1), restored);
        Assert.Equal(dead ? 0u : 1238u, conn.PlayerLife); // A larger maximum does not heal or resurrect.
        Assert.Equal(dead ? 0u : 317u, conn.PlayerMana);

        await commands.RunAsync("/set_stat additional_intelligence 0");
        Assert.Contains((dead ? 0u : 317u, 325u, (byte)1), Resources(await wire.Read()));
    }

    [Fact]
    public async Task Queued_mana_update_samples_current_mana_and_reservation_inside_the_send_gate()
    {
        var conn = Character();
        conn.PlayerLife = 100;
        conn.PlayerMana = 100;
        using var wire = await Wire.Open();
        var gate = Program.SendGates.GetValue(wire.Send, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        Task queued;
        try
        {
            queued = (Task)Method("SendPlayerMana").Invoke(null,
                ["attribute-resource-test", wire.Stream, wire.Send, conn, null])!;
            Assert.False(queued.IsCompleted);
            lock (conn.Monsters)
            {
                conn.PlayerStats[Key("additional_intelligence")] = 100;
                conn.Pools = Program.PoolsFor(conn, 100);
                conn.PlayerMana = 77;
            }
        }
        finally { gate.Release(); }
        await queued.WaitAsync(TimeSpan.FromSeconds(5));
        var updates = Resources(await wire.Read());
        Assert.Contains((77u, 350u, (byte)1), updates);
        Assert.DoesNotContain((100u, 325u, (byte)1), updates);
    }

    [Fact]
    public async Task Rounded_Strength_group_changes_publish_combined_mana_and_idle_reservation_without_refilling()
    {
        var conn = Character();
        conn.MainAscendancy = new(Nodes: [38080]);
        conn.PlayerStats[Key(PowerOfPurpose.RawFlag)] = 0; // This control isolates the Strength-mana source.
        conn.Pools = Program.PoolsFor(conn, 100);
        Assert.Equal(674u, conn.Pools.Mana);
        conn.PlayerLife = 100;
        conn.PlayerMana = 337;
        using var wire = await Wire.Open();
        var commands = wire.Commands(conn);
        async Task Check(string command, int strength, uint mana, uint current, uint reserved)
        {
            Assert.True(await commands.RunAsync(command));
            byte[] plain = await wire.Read();
            Assert.Equal(strength, conn.Pools.Strength);
            Assert.Equal(mana, conn.Pools.Mana);
            Assert.Contains((current, reserved, (byte)1), Resources(plain));
            byte[] maximum = InstanceEntities.StatsUpdate(Program.PlayerId, [(Key("maximum_mana"), (int)mana)])[13..];
            Assert.True(plain.AsSpan().IndexOf(maximum) >= 0);
            Assert.Equal(100u, conn.PlayerLife);
            Assert.Equal(current, conn.PlayerMana);
        }
        await Check("/set_stat strength_+% 11", 16, 682, 337, 341); // 15.54 rounds into the fourth group.
        await Check("/set_stat strength_+% -22", 11, 666, 333, 333);
        await Check("/set_stat strength_+% off", 14, 674, 333, 337);
        await Check("/set_stat X_mana_per_4_strength 0", 14, 650, 325, 325);
        await Check("/set_stat X_mana_per_4_strength off", 14, 674, 325, 337);
    }

    [Fact]
    public async Task Positive_pool_increases_publish_signed_maxima_and_a_valid_reservation_on_the_wire()
    {
        var conn = Character();
        var ring = ItemCatalog.Create(Program.Items.Find("Iron Ring")!) with
        {
            Mods = new[] { "maximum_life_+%", "maximum_mana_+%" }.Select(stat =>
            {
                var mod = Program.Items.Mods.All.Where(m => m.Stats.Any(s => s.Id == stat && !s.Local))
                    .OrderBy(m => m.Level).First();
                return new ItemMod(ModSection.Explicit, mod.Hash, mod.Stats.Select(s => s.Id == stat ? 100 : 0).ToArray());
            }).ToArray()
        };
        PlayerInventories.For(name, Program.Items).Add(6, 0, 0, ring);
        conn.Pools = Program.PoolsFor(conn, 100);
        conn.PlayerLife = conn.PlayerMana = 100;
        using var wire = await Wire.Open();
        await wire.Commands(conn).RunAsync("/set_stat additional_all_attributes 2147483647");
        byte[] plain = await wire.Read();
        foreach (string stat in new[] { "base_maximum_life", "maximum_life", "base_maximum_mana", "maximum_mana" })
        {
            // Match the signed compact pair inside the actual full 0x1b3 publication, excluding its header/count.
            byte[] pair = InstanceEntities.StatsUpdate(Program.PlayerId, [(Key(stat), int.MaxValue)])[13..];
            Assert.True(plain.AsSpan().IndexOf(pair) >= 0, $"Expected positive {stat} on the wire");
        }
        Assert.Contains((100u, 1073741824u, (byte)1), Resources(plain));
        Assert.Equal(1073741823, AuraCatalog.UnreservedMana(conn.Pools, conn.Auras.Values));
        Assert.Equal(100u, conn.PlayerLife);
        Assert.Equal(100u, conn.PlayerMana);
    }

    public void Dispose()
    {
        PlayerInventories.Forget(name);
        Program.InactiveItems.TryRemove(name, out _);
    }
}
