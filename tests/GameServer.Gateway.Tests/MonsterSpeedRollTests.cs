using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

/// <summary>
/// The client's per-monster movement speed roll (MonsterSpeedRoll.cs; 07-latency-visibility-movement.md §5 "Monsters
/// on the client"): the keyed draw, the roll range read from MonsterVarieties, and the spawned monster's walk speed.
/// </summary>
public class MonsterSpeedRollTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static readonly Lazy<MonsterCatalog> Catalog = new(() => new MonsterCatalog(Data()));
    // Row 230: a plain Goatman, range (−5, 5), base speed 25. Row 1159: Hungry Corpse (ZombieBite), no range, base 8.
    static MonsterCatalog.Variety Goatman => Catalog.Value.Find("Metadata/Monsters/Goatman/GoatmanLeapSlam")!;
    static MonsterCatalog.Variety HungryCorpse => Catalog.Value.Find("1159")!;

    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        public readonly TcpClient Writer = new(), Reader;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], isClient: false);

        public Wire()
        {
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            Writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            Reader = accepting.GetAwaiter().GetResult();
        }

        public void Dispose() { Writer.Dispose(); Reader.Dispose(); listener.Stop(); }
    }

    static ClientState Connection() => new(new AreaLayout(1, "speed-roll", 400, 400, 50, 10, null, []), 1, 1);

    static async Task<SpawnedMonster> Spawn(Wire wire, ClientState conn, MonsterCatalog.Variety variety, int x)
    {
        uint id = conn.NextObjectId;
        await Program.SpawnMonster("speed-roll-test", wire.Writer.GetStream(), wire.Ciphers, conn, variety, 5,
            MonsterRarity.Normal, x, 100, log: false).WaitAsync(TimeSpan.FromSeconds(10));
        lock (conn.Monsters) return conn.Monsters[id];
    }

    [Theory]
    // Reference values from the published init_by_array with the client's constants and transition, replayed in Python.
    [InlineData(0x1234u, 0x9f1a5e3cu, 2)]
    [InlineData(0x2000u, 0xdeadbeefu, 0)]
    [InlineData(0x2001u, 0xdeadbeefu, 1)]
    [InlineData(1u, 0u, -5)]
    public void The_roll_is_fixed_by_the_object_id_and_the_type_word(uint objectId, uint typeWord, int expected)
    {
        Assert.Equal(expected, MonsterSpeedRoll.Percent(objectId, typeWord, -5, 5));
        Assert.Equal(expected, MonsterSpeedRoll.Percent(objectId, typeWord, -5, 5));
    }

    [Fact]
    public void An_empty_range_rolls_zero()
    {
        Assert.Equal(0, MonsterSpeedRoll.Percent(0x1234, 0x9f1a5e3c, 0, 0));
        Assert.Equal(0, MonsterSpeedRoll.Percent(0x1234, 0x9f1a5e3c, 7, 7));
        Assert.Equal(1.0, MonsterSpeedRoll.Factor(0));
        Assert.Equal(0.95, MonsterSpeedRoll.Factor(-5));
        Assert.Equal(1.1, MonsterSpeedRoll.Factor(10));
    }

    [Theory]
    [InlineData(-5, 5)]
    [InlineData(-10, 10)]
    [InlineData(-30, 15)]
    [InlineData(0, 10)]
    [InlineData(-20, -10)]
    public void Rolls_stay_inside_the_range_and_reach_both_ends(int min, int max)
    {
        var rolls = Enumerable.Range(0, 2000).Select(n => MonsterSpeedRoll.Percent(0x3000u + (uint)n, 0x9f1a5e3cu, min, max)).ToList();
        Assert.All(rolls, roll => Assert.InRange(roll, min, max));
        Assert.Contains(min, rolls);
        Assert.Contains(max, rolls);
    }

    [Fact]
    public void The_catalog_reads_each_variety_s_roll_range()
    {
        Assert.Equal((-5, 5), (Goatman.SpeedRollMin, Goatman.SpeedRollMax));
        Assert.Equal(25, Goatman.MovementSpeed);
        Assert.Equal((0, 0), (HungryCorpse.SpeedRollMin, HungryCorpse.SpeedRollMax));
        // A variety made without the columns has no range.
        var bare = new MonsterCatalog.Variety(0, "Metadata/Monsters/Test", "Test", "base", null, null);
        Assert.Equal((0, 0), (bare.SpeedRollMin, bare.SpeedRollMax));
    }

    [Fact]
    public async Task A_goatman_spawns_with_a_walk_speed_inside_its_five_percent_band()
    {
        using var wire = new Wire();
        var conn = Connection();
        var goatman = Goatman;
        uint type = InstanceEntities.ObjectTypeHash(MonsterObject.TypePath(goatman.Id, 5));
        var rolls = new List<int>();
        for (int n = 0; n < 8; n++)
        {
            uint id = conn.NextObjectId;
            var monster = await Spawn(wire, conn, goatman, 100 + 10 * n);
            Assert.Equal(MonsterSpeedRoll.Percent(id, type, goatman), monster.SpeedRoll);
            Assert.InRange(monster.SpeedRoll, -5, 5);
            Assert.InRange(monster.Walk.Speed, 25 * 0.95, 25 * 1.05);
            Assert.Equal(25 * (1 + monster.SpeedRoll / 100.0), monster.Walk.Speed, 9);
            rolls.Add(monster.SpeedRoll);
        }
        // Ids 0x3000.. with the GoatmanLeapSlam@5 type hash, by the Python reference.
        Assert.Equal(0xa4ebc1deu, type);
        Assert.Equal(new[] { 4, 2, -1, -3, 1, 0, -2, -3 }, rolls);
    }

    [Fact]
    public async Task A_variety_without_a_range_spawns_at_its_base_speed()
    {
        using var wire = new Wire();
        var monster = await Spawn(wire, Connection(), HungryCorpse, 100);
        Assert.Equal(0, monster.SpeedRoll);
        Assert.Equal(8, monster.Walk.Speed);
    }
}
