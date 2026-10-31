using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// S→C 0x1af for a monster's hit on the player carries the outcome's presentation and is processed at once (kind 3):
// the 3.29.3b decode shows kind 2 only queues the package behind an action with the same sequence, which the fabricated
// 0xffff never guarantees, and a 0x0400 stun bit would ask the client for a second stun beside the authoritative 0x1d1.
public class MonsterHitPacketTests
{
    const uint MonsterId = 0x3000;

    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        public readonly TcpClient Writer = new(), Reader;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], isClient: false);
        readonly ConnectionCiphers decrypt = new(new byte[64], isClient: true);

        public Wire()
        {
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            Writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            Reader = accepting.GetAwaiter().GetResult();
        }

        public async Task<byte[]> Read(int length)
        {
            byte[] bytes = new byte[length];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Reader.GetStream().ReadExactlyAsync(bytes, timeout.Token);
            decrypt.Receive.Process(bytes);
            return bytes;
        }

        public void Dispose() { Writer.Dispose(); Reader.Dispose(); listener.Stop(); }
    }

    static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }

    static byte[] ObjectHeader(ushort id, uint objectId) => new LoginWriter().U16(id).U32(objectId).U32(0).U16(0).ToArray();

    // Review 2026-10-30: with a projectile session active, the melee contact read the runtime's time at the send gate
    // without advancing it, behind a clock read the life tick or a mortar landing had already made; the fortification
    // tracker refused the earlier time and the swing's task faulted unobserved. The contact commits at the later time.
    [Fact]
    public async Task A_melee_contact_behind_a_later_clock_read_still_commits_its_hit()
    {
        await using var f = new ProjectileRuntimeFixture();
        var m = new SpawnedMonster("test monster", new WalkEstimator(12, 10, 30, null, 300, 300), 60, 3, 100)
        {
            Attack = new MonsterAttack(0x102, 0.4, 10, DamagePacket.Of(DamageType.Physical, 10, 10)),
        };
        f.Conn.Monsters.Add(MonsterId, m);
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Program.FortificationOf(f.Conn).Read(f.Conn.Clock.Elapsed.TotalSeconds);   // the runtime's time is still zero
        await Program.MonsterContact("hit-packet-test", f.Stream, f.Ciphers, f.Conn, MonsterId, m, m.ActionGeneration)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(f.Conn.PlayerLife < 100, "the hit landed");
        byte[] wire = await f.ReadWire();
        Assert.True(IndexOf(wire, ObjectHeader(InstanceProtocol.ActionHitId, MonsterId)) >= 0, "its 0x1af went out");
        Assert.False(f.Conn.Closed);
    }

    static (ClientState Connection, SpawnedMonster Monster) State(DamageType type)
    {
        TestSaves.Ensure();   // the hit reads the character's final stats, which load its save
        var conn = new ClientState(new AreaLayout(1, "test", 300, 300, 10, 10, null, []), 1, 1) { PlayerLife = 50 };
        var monster = new SpawnedMonster("test monster", new WalkEstimator(10, 10, 30, null, 300, 300), 60, 3, 100)
        {
            Attack = new MonsterAttack(0x102, 0.4, 10, DamagePacket.Of(type, 10, 10)),
        };
        conn.Monsters.Add(MonsterId, monster);
        return (conn, monster);
    }

    [Theory]
    [InlineData(DamageType.Physical, 0)]
    [InlineData(DamageType.Fire, 2)]
    [InlineData(DamageType.Cold, 1)]
    [InlineData(DamageType.Lightning, 3)]
    [InlineData(DamageType.Chaos, 4)]
    public async Task A_monster_hit_is_an_immediate_hit_packet_with_its_damage_type(DamageType type, int code)
    {
        using var wire = new Wire();
        var (conn, m) = State(type);
        var contact = Program.MonsterContact("hit-packet-test", wire.Writer.GetStream(), wire.Ciphers, conn, MonsterId, m, m.ActionGeneration);
        // The whole contact (packet, life, mana, flask charges) is written before anything is read: a fault surfaces here.
        await contact.WaitAsync(TimeSpan.FromSeconds(5));

        byte[] expected = InstanceProtocol.ActionHit(MonsterId, 0xffff, Program.PlayerId, flags: 0x4201, kind: 0x03, code: (byte)code);
        byte[] hit = await wire.Read(expected.Length);
        // Everything but the trailing angle byte, which depends on the two positions.
        Assert.Equal(expected[..^1], hit[..^1]);
        Assert.True(conn.PlayerLife < 50);
    }

    // The outcome bits ride on the fixed presentation flags; a blocked hit in the contact pipeline depends on the player's
    // pools, which the hit path may recompute, so the bits are checked on their own.
    [Theory]
    [InlineData(false, false, 0x4201)]
    [InlineData(true, false, 0x4241)]
    [InlineData(false, true, 0x4221)]
    [InlineData(true, true, 0x4261)]
    public void The_flags_carry_the_blocked_and_critical_bits(bool blocked, bool critical, int flags) =>
        Assert.Equal((ushort)flags, Program.MonsterHitFlags(blocked, critical));

    [Theory]
    [InlineData(DamageType.Physical, DamageType.Fire, 3, 0)]
    [InlineData(DamageType.Physical, DamageType.Fire, 20, 2)]
    public void The_type_code_is_the_largest_damage_component(DamageType first, DamageType second, int secondAmount, int code)
    {
        var packet = DamagePacket.Of(first, 10, 10).Add(second, new(secondAmount, secondAmount));
        Assert.Equal((byte)code, Program.HitCode(packet));
    }
}
