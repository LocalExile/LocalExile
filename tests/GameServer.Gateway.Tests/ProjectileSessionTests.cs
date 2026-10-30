using System.Net;
using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ProjectileSessionTests
{
    public ProjectileSessionTests() => TestSaves.Ensure();
    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        public readonly TcpClient Writer = new(), Reader;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        public SemaphoreSlim Gate => Program.SendGates.GetValue(Ciphers, _ => new(1, 1));
        public Wire()
        {
            listener.Start(); var accepting = listener.AcceptTcpClientAsync();
            Writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            Reader = accepting.GetAwaiter().GetResult();
        }
        public void Dispose() { Writer.Dispose(); Reader.Dispose(); listener.Stop(); }
    }

    static ClientState Connection(Random? criticalRandom = null) => new(new AreaLayout(1, "projectile-test", 300, 300, 10, 10, null, []), 1, 1)
        { PlayerLife = 100, ActionCount = 7, Character = $"Projectile{Guid.NewGuid():N}",
            CriticalRandom = criticalRandom ?? Random.Shared };
    static ProjectileUse Shot(ClientState conn, int count = 1) => new(
        new("Metadata/Projectiles/DefaultArrow") { Speed = 3000, Range = 100, Radius = 1,
            Count = count, SpreadRadians = 0 }, new(1), new(1), 1, new(10, 10), new(110, 10))
        { Payload = new ProjectileOffense(DamagePacket.Of(DamageType.Physical, 10, 10), false,
            int.MaxValue, 0, 150, "test") };

    static void Set(ClientState conn, string name, int value) => conn.PlayerStats[Program.Stats.Key(name)!.Value] = value;

    sealed class CriticalDraws : Random
    {
        public int Calls;
        public override int Next(int maxValue) => Calls++ == 0 ? 9000 : 0;
    }

    [Theory]
    [InlineData("bow", false)]
    public void Old_owners_cannot_reserve_ground_attack_or_cooldown_side_effects(string route, bool retired)
    {
        var conn = Connection(); conn.RageRetired = retired; conn.LeavingArea = !retired;
        conn.ChannelSkill = 123;
        bool accepted = route == "bow"
            ? Program.TryBeginRangedAttack(conn, 0, 1, 0x102, 7, out _)
            : Program.TryBeginCooldownUse(conn, 10619, new(2500, 100), false,
                () => { conn.ActionCount++; conn.ChannelSkill = 456; }, out _);
        Assert.False(accepted);
        Assert.Equal(7, conn.ActionCount); Assert.Equal((ushort)123, conn.ChannelSkill);
        Assert.Equal(0, conn.NextAttackAt); Assert.Empty(conn.SingleUseCooldowns);
    }

}
