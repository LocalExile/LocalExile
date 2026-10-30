using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

[CollectionDefinition("Actor scale", DisableParallelization = true)]
public class ActorScaleCollection;

[Collection("Actor scale")]
public sealed class ActorScaleGatewayTests : IDisposable
{
    readonly List<string> names = [];
    static MethodInfo Method(string name) => typeof(Program).GetMethod(name,
        BindingFlags.Static | BindingFlags.NonPublic)!;
    static T Call<T>(string name, params object?[] args) => (T)Method(name).Invoke(null, args)!;
    static void Publish(ClientState conn) => Method("PublishPlayerPose").Invoke(null, [conn]);
    static int Scale(ClientState conn) => Program.CharacterPlayerStats(conn)[1973];
    static List<(uint, int)> Refresh(ClientState conn) => Call<List<(uint, int)>>("PlayerStatsWithCleared", conn);

    ClientState Character(InstanceState? instance = null, bool solo = false, Guid? characterId = null)
    {
        TestSaves.Ensure();
        string name = "Scale" + Guid.NewGuid().ToString("N"); names.Add(name);
        var conn = new ClientState(new(ushort.MaxValue, "ScaleTest", 128, 128, 32, 32, null, []), 0, 1)
        { Character = name, CharacterId = characterId, Ssf = solo, League = solo ? "SSF Standard" : null,
            ClassRow = 1, Instance = instance ?? new(), PlayerLife = 100, PlayerSent = true,
            WornCosmetics = [] };
        conn.Pools = Program.PoolsFor(conn, 100);
        return conn;
    }

    static void Wear(ClientState conn, int value, byte slot = 0)
    {
        var record = Program.Mtx.Owned.First(r =>
            Program.MtxEffects.Of(r.MtxTypeHash)?.Stats.Contains((720u, value)) == true);
        conn.WornCosmetics!.Add(new(slot, record.Id, record.MtxTypeHash));
    }

    static Item SizeItem(string type, int value) => ItemCatalog.Create(Program.Items.Find(type)!) with
    { Mods = [Program.Items.Mods.ById("ActorSizeUniqueAmulet2")!.With(ModSection.Explicit, [value], Random.Shared)] };

    [Fact]
    public void Real_passive_item_and_cosmetics_add_then_remove_without_feedback_or_precedence_changes()
    {
        var conn = Character();
        conn.Passives.Add(49937); // Domain fixture only; does not admit a Phrecia class.
        var items = PlayerInventories.For(conn.Character!, Program.Items);
        var amulet = items.Add(5, 0, 0, SizeItem("Amber Amulet", 20));
        Wear(conn, 5); Wear(conn, 3, 1);
        Assert.Equal(38, Scale(conn)); Assert.Equal(38, Scale(conn));
        Assert.False(conn.PlayerStats.ContainsKey(720)); Assert.False(conn.PlayerStats.ContainsKey(1973));
        conn.PlayerStats[720] = 0;
        Assert.Equal(0, Scale(conn));
        conn.PlayerStats.Remove(720);
        conn.PlayerStats[1973] = 777;
        Assert.Equal(777, Scale(conn));
        conn.PlayerStats[1973] = 0;
        Assert.Equal(0, Scale(conn));
        conn.PlayerStats.Remove(1973);
        conn.WornCosmetics!.RemoveAt(1); Assert.Equal(35, Scale(conn));
        conn.WornCosmetics.Clear(); Assert.Equal(30, Scale(conn));
        items.Remove(5, amulet.Id); Assert.Equal(10, Scale(conn));
        conn.Passives.Clear(); Assert.Equal(0, Scale(conn));
        Assert.Equal((1973u, 0), Assert.Single(Refresh(conn), s => s.Item1 == 1973));
        Assert.Equal(0, Scale(Character()));
    }

    // One loopback transport with persistent cipher state; no server/client executable or native session.
    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new();
        TcpClient reader = null!;
        readonly ConnectionCiphers decrypt = new(new byte[64], true);
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        public NetworkStream Stream => writer.GetStream();
        public SemaphoreSlim Gate => Program.SendGates.GetValue(Ciphers, _ => new(1, 1));
        public static async Task<Wire> Open()
        {
            var wire = new Wire(); wire.listener.Start();
            var accept = wire.listener.AcceptTcpClientAsync();
            await wire.writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)wire.listener.LocalEndpoint).Port);
            wire.reader = await accept;
            return wire;
        }
        public Task Invoke(string method, ClientState conn, params object?[] tail) =>
            Call<Task>(method, ["actor-scale-test", Stream, Ciphers, conn, .. tail]);
        public async Task<byte[]> Read()
        {
            // A sentinel sent through the same gate makes collection independent of TCP packet boundaries/timing.
            byte[] sentinel = new LoginWriter().U16(0x7ffe).U32(0x12345678).ToArray();
            await Program.Send("actor-scale-test", Stream, Ciphers, sentinel.ToArray(), null);
            var result = new List<byte>(); var buffer = new byte[4096];
            while (true)
            {
                int n = await reader.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(n > 0);
                byte[] chunk = buffer[..n]; decrypt.Receive.Process(chunk); result.AddRange(chunk);
                if (result.Count >= sentinel.Length && result.TakeLast(sentinel.Length).SequenceEqual(sentinel))
                    return result.Take(result.Count - sentinel.Length).ToArray();
            }
        }
        public void Dispose() { reader.Dispose(); writer.Dispose(); listener.Dispose(); }
    }

    static uint Compact(byte[] data, ref int at, bool signed = false)
    {
        byte first = data[at++];
        int extra = first < 0x80 ? 0 : first < 0xc0 ? 1 : first < 0xe0 ? 2 : first < 0xf0 ? 3 : 4;
        int bits = extra == 4 ? 32 : 7 * (extra + 1);
        uint value = extra == 4 ? 0u : (uint)(first & (0x7f >> extra));
        for (int i = 0; i < extra; i++) value = (value << 8) | data[at++];
        if (signed && bits < 32 && (value & (1u << (bits - 1))) != 0) value |= uint.MaxValue << bits;
        return value;
    }
    static List<(uint, int)> StatsAt(byte[] bytes, int at)
    {
        int count = (int)Compact(bytes, ref at); var stats = new List<(uint, int)>();
        for (int i = 0; i < count; i++) stats.Add((Compact(bytes, ref at), unchecked((int)Compact(bytes, ref at, true))));
        return stats;
    }
    static int Index(byte[] bytes, byte[] prefix)
    {
        int at = bytes.AsSpan().IndexOf(prefix);
        Assert.True(at >= 0, $"Missing packet prefix {Convert.ToHexString(prefix)}");
        return at;
    }
    static void AssertUpdate(byte[] bytes, uint id, int scale)
    {
        var prefix = new LoginWriter().U16(0x1b3).U32(id).U32(0).U16(0).ToArray();
        var stats = StatsAt(bytes, Index(bytes, prefix) + prefix.Length);
        Assert.Equal((1973u, scale), Assert.Single(stats, s => s.Item1 == 1973));
    }
    static void AssertCreate(byte[] bytes, uint id, int scale)
    {
        int start = Index(bytes, new LoginWriter().U16(0x2b9).U32(id).ToArray());
        int at = start + 18; // Object header, type and blob16 length, then the little-endian component blob.
        Assert.Equal(0, bytes[at + 16]); // Player Positioned flags0 has no custom scale extension.
        Assert.Equal((1973u, scale), Assert.Single(StatsAt(bytes, at + 20), s => s.Item1 == 1973));
    }

    public void Dispose() { foreach (string name in names) PlayerInventories.Forget(name); }
}
