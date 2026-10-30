using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class OrdinaryBowLifecycleTests
{
    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        public readonly TcpClient Writer = new(), Reader;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], isClient: false);
        public SemaphoreSlim Gate => Program.SendGates.GetValue(Ciphers, _ => new(1, 1));
        public Wire()
        {
            listener.Start(); var accepting = listener.AcceptTcpClientAsync();
            Writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            Reader = accepting.GetAwaiter().GetResult();
        }
        public void Dispose() { Writer.Dispose(); Reader.Dispose(); listener.Stop(); }
    }

    static ClientState Connection() => new(new AreaLayout(1, "test", 300, 300, 10, 10, null, []), 1, 1)
        { PlayerLife = 100, ActionCount = 7 };
    static ProjectileOffense Offense => new(DamagePacket.Of(DamageType.Physical, 10, 10), false,
        int.MaxValue, 0, 150, "ordinary") { ImpaleChance = 100 };

    [Theory]
    [InlineData("action")]
    public async Task Cancelled_windup_cannot_release_even_with_native_output_held(string cancellation)
    {
        await using var f = new ProjectileRuntimeFixture(); await f.HoldOutput();
        int token = f.Conn.ActionCount, epoch = f.Conn.ProjectileEpoch; bool released = false;
        f.Session.Schedule(120000, () => f.Conn.ActionCount == token && f.Conn.PlayerLife > 0
            && f.Conn.ProjectileActive(epoch), () => { released = true; return f.Shot(offense: Offense); });
        if (cancellation == "action") f.Conn.ActionCount++;
        if (cancellation == "death") f.Conn.PlayerLife = 0;
        if (cancellation == "transfer") f.Conn.TryBeginAreaChange(new(2, 0, false), out _);
        if (cancellation == "disconnect") f.Conn.Closed = true;
        f.Advance(2);
        Assert.False(released); Assert.Empty(f.Session.Runtime.Projectiles);
        Assert.Empty(f.Conn.ProjectileObjects); Assert.Equal(0, f.Available);
    }

}
