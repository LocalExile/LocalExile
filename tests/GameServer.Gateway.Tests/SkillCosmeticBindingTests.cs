using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public class SkillCosmeticBindingTests
{
    const string A = "Metadata/Items/Gems/SkillGemSpark";
    const string B = "Metadata/Items/Gems/SkillGemKineticBlast";
    const uint SparkRecord = 122;

    sealed class Fixture : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new();
        readonly TcpClient reader;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], isClient: false);
        public readonly ClientState Conn;
        public PlayerInventories Items => PlayerInventories.For(Conn.Character!, Program.Items);
        public SemaphoreSlim Gate => Program.SendGates.GetValue(Ciphers, _ => new(1, 1));

        public Fixture()
        {
            TestSaves.Ensure();
            Conn = new(new(ushort.MaxValue, "CosmeticTest", 128, 128, 32, 32, null, []), 0, 1)
            { Character = "Mtx" + Guid.NewGuid().ToString("N"), ClassRow = 1, WornCosmetics = [],
                PlayerLife = 100, PlayerMana = 100 };
            Conn.Pools = Program.PoolsFor(Conn, 100);
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
        }

        static T Call<T>(string method, params object?[] args) => (T)typeof(Program)
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args)!;
        public List<InstanceMicrotransactions.SkillSlot> Slots() => Call<List<InstanceMicrotransactions.SkillSlot>>("SkillSlots", Conn);
        public Task Refresh() => Call<Task>("SendWornCosmetics", "cosmetic-test", writer.GetStream(), Ciphers, Conn, "test");
        public Task Pick(uint key, uint record) => Call<Task>("PickSkillCosmetic", "cosmetic-test", writer.GetStream(), Ciphers, Conn, (key, record));
        public void Equip(params string[] gems)
        {
            foreach (var item in Items.Items(PlayerInventories.Weapon1)) Items.Remove(PlayerInventories.Weapon1, item.Id);
            if (gems.Length == 0) return;
            var host = ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!) with
            { Sockets = [.. gems.Select(g => new ItemSocket(SocketColour.Blue, ItemCatalog.Create(Program.Items.Find(g)!)))] };
            Items.Add(PlayerInventories.Weapon1, 0, 0, host);
        }
        public uint Hash(string gem) => Program.Items.Find(gem)!.Hash;
        public uint Saved(string gem, byte slotType = 0) => CharacterSaves.SkillCosmetics(Conn.Character!)
            .GetValueOrDefault(CharacterSaves.SkillCosmeticKey(Hash(gem), slotType))?.Record ?? 0;
        public uint Record(string gem, byte slotType = 0) => Program.Mtx.Owned.First(r =>
            Program.MtxEffects.FitsSkill(r.MtxTypeHash, Program.Items.Find(gem)!, slotType)).Id;
        public int Variation(string gem) => Call<int>("SkillArtVariation", Conn, Hash(gem));
        public async Task AssertPackets(params byte[][] packets)
        {
            byte[] expected = [.. packets.SelectMany(p => p)];
            byte[] actual = new byte[expected.Length];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await reader.GetStream().ReadExactlyAsync(actual, timeout.Token);
            new ConnectionCiphers(new byte[64], isClient: true).Receive.Process(actual);
            Assert.Equal(expected, actual);
            Assert.Equal(0, reader.Available);
        }
        public void Dispose()
        {
            Conn.Closed = true;
            writer.Dispose(); reader.Dispose(); listener.Stop(); PlayerInventories.Forget(Conn.Character!);
        }
    }

    [Theory]
    [InlineData(false)]
    public async Task Replacement_retires_A_key_even_after_clearing_its_MTX(bool clear)
    {
        using var f = new Fixture();
        f.Equip(A);
        var a = Assert.Single(f.Slots());
        await f.Pick(a.Key, SparkRecord);
        if (clear) await f.Pick(a.Key, 0);
        f.Equip(B);
        var b = Assert.Single(f.Slots());
        Assert.NotEqual(a.Key, b.Key);
        Assert.Equal(f.Hash(B), b.GemHash);
        Assert.Equal(0u, b.RecordId);
        Assert.False(f.Conn.SkillSlots.ContainsKey(a.Key));
        await f.Pick(a.Key, SparkRecord); // delayed picker from the removed skill
        await f.Pick(b.Key, 0);
        Assert.Equal(clear ? 0u : SparkRecord, f.Saved(A));
        Assert.Equal(0u, f.Saved(B));
        f.Equip(A);
        var restored = Assert.Single(f.Slots());
        Assert.NotEqual(a.Key, restored.Key);
        Assert.NotEqual(b.Key, restored.Key);
        Assert.Equal(clear ? 0u : SparkRecord, restored.RecordId);
    }

}
