using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class PowerOfPurposeGatewayTests
{
    static uint Key(string name) => Program.Stats.Key(name)!.Value;
    static MethodInfo Method(string name) =>
        typeof(Program).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    sealed class Character : IDisposable
    {
        public ClientState State { get; }
        public Character()
        {
            TestSaves.Ensure();
            State = new(new(ushort.MaxValue, "PurposeTest", 128, 128, 32, 32, null, []), 0, 1)
            {
                Character = "Purpose" + Guid.NewGuid().ToString("N"), ClassRow = SkillCatalog.WitchRow,
                PlayerLife = 25, PlayerSent = true,
            };
            State.PlayerStats[Key("mana_regeneration_rate_+%")] = -100;
            State.Pools = Program.PoolsFor(State, 1);
        }
        public void Dispose() => PlayerInventories.Forget(State.Character!);
    }

    [Fact]
    public void Real_Mental_Conditioning_raw_flag_enables_conversion_and_display_alone_does_not()
    {
        Assert.Equal(9548u, Key(PowerOfPurpose.RawFlag));
        Assert.Equal("display_mode_is_behemoth_ascendancy", Program.Stats.Name(21159));
        var node = new PassiveTree().Find(38080)!;
        Assert.Equal("Mental Conditioning", node.Name);
        Assert.Equal("AscendancyCustomBerserker11", node.Id);
        Assert.Contains((9548u, 1), node.Stats);
        using var character = new Character();
        var conn = character.State;
        var ordinary = conn.Pools;
        conn.PlayerStats[21159] = 1;
        Assert.Equal(ordinary, Program.PoolsFor(conn, 1));
        conn.Passives.Add(38080);
        var active = Program.PoolsFor(conn, 1);
        Assert.Equal(16u, active.Mana);
        Assert.Equal(128, active.Armour); // B = 34 + 6 + 32/2 + floor(14/4)*8 = 80.
        Assert.Equal(ordinary.Strength, active.Strength);
        Assert.Equal(ordinary.Intelligence, active.Intelligence);
        // Explicit conversion zero retains the node's independent mana-per-Strength source.
        conn.PlayerStats[9548] = 0;
        Assert.Equal(ordinary with { Mana = 80 }, Program.PoolsFor(conn, 1));
        Assert.Equal(0, Program.PoolsFor(conn, 1).Armour);
        conn.PlayerStats.Remove(9548);
        Assert.Equal(active, Program.PoolsFor(conn, 1));
        using var rebuilt = new Character();
        rebuilt.State.Passives.AddRange(conn.Passives);
        Assert.Equal(active, Program.PoolsFor(rebuilt.State, 1)); // Fresh connection derives the same pools.
        conn.Passives.Remove(38080);
        Assert.Equal(ordinary, Program.PoolsFor(conn, 1));
    }

    static Task<byte[]> Command(ClientState conn, string command) => Exchange(async (stream, ciphers) =>
    {
        conn.Commands = (ChatCommands)Method("Commands").Invoke(null, ["purpose-test", stream, ciphers, conn])!;
        Assert.True(await conn.Commands.RunAsync(command));
    });

    static ItemMod Mod(string name, int value)
    {
        var mod = Program.Items.Mods.All.Where(m => m.Domain == 1 && m.Stats.Count == 1
            && m.Stats[0].Id == name && Program.Items.Mods.ByHash(m.Hash)?.Id == m.Id)
            .OrderBy(m => m.Level).First();
        return new(ModSection.Explicit, mod.Hash, [value]);
    }

    static async Task<byte[]> Exchange(Func<NetworkStream, ConnectionCiphers, Task> work)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        var accepting = listener.AcceptTcpClientAsync();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var server = await accepting;
        using var capture = new MemoryStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reading = client.GetStream().CopyToAsync(capture, timeout.Token);
        byte[] key = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        try { await work(server.GetStream(), new(key, isClient: false)).WaitAsync(timeout.Token); }
        finally
        {
            server.Client.Shutdown(SocketShutdown.Send);
            await reading;
        }
        var bytes = capture.ToArray();
        new ConnectionCiphers(key, isClient: true).Receive.Process(bytes);
        return bytes;
    }

    static void AssertWire(byte[] bytes, int maximum, int armour, uint current, uint reserved)
    {
        var stats = StatUpdates(bytes);
        Assert.Equal(maximum, stats[Key("maximum_mana")]);
        Assert.Equal(maximum, stats[Key("base_maximum_mana")]);
        Assert.Equal(armour, stats[Key("physical_damage_reduction_rating")]);
        Assert.All(ManaUpdates(bytes), pair => Assert.Equal((current, reserved), pair));
        Assert.NotEmpty(ManaUpdates(bytes));
    }

    // Decode captured server output after transport decryption, rather than matching the production writer's bytes.
    static Dictionary<uint, int> StatUpdates(byte[] bytes)
    {
        var result = new Dictionary<uint, int>();
        foreach (int start in Headers(bytes, 0x1b3))
        {
            int pos = start + 12;
            int count = Compact(bytes, ref pos, signed: false);
            Assert.InRange(count, 1, 2000);
            for (int i = 0; i < count; i++)
            {
                uint key = (uint)Compact(bytes, ref pos, signed: false);
                result[key] = Compact(bytes, ref pos, signed: true);
            }
        }
        return result;
    }

    static List<(uint Current, uint Reserved)> ManaUpdates(byte[] bytes) =>
        Headers(bytes, 0x1b1).Where(start => bytes[start + 20] == 1)
            .Select(start => (BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(start + 12)),
                BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(start + 16)))).ToList();

    static List<int> Headers(byte[] bytes, ushort id)
    {
        var result = new List<int>();
        for (int pos = 0; pos <= bytes.Length - 12; pos++)
            if (BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(pos)) == id
                && BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(pos + 2)) == Program.PlayerId
                && bytes.AsSpan(pos + 6, 6).IndexOfAnyExcept((byte)0) < 0)
                result.Add(pos);
        return result;
    }

    static int Compact(byte[] bytes, ref int pos, bool signed)
    {
        byte first = bytes[pos++];
        int extra = first < 0x80 ? 0 : first < 0xc0 ? 1 : first < 0xe0 ? 2 : first < 0xf0 ? 3 : 4;
        int bits = extra == 4 ? 32 : 7 * (extra + 1);
        uint value = extra == 4 ? 0 : (uint)(first & (0x7f >> extra));
        for (int i = 0; i < extra; i++) value = value << 8 | bytes[pos++];
        if (signed && bits < 32 && (value & (1u << (bits - 1))) != 0) value |= uint.MaxValue << bits;
        return unchecked((int)value);
    }
}
