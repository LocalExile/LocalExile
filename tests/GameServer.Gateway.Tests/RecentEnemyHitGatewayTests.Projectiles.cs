using GameServer.Core.Projectiles;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class RecentEnemyHitGatewayTests
{
    [Fact]
    public async Task Earlier_projectile_kill_invalidates_monster_contact_before_incoming_rng_or_recent_history()
    {
        using var f = new Fixture();
        var incoming = new TraceRandom(); f.Conn.IncomingHitRandom = incoming;
        var monster = f.Monster(seconds: .01);
        var session = new Program.ProjectileSession("contact-order", f.Stream, f.Ciphers, f.Conn);
        f.Conn.ProjectileRuntime = session;
        try
        {
            using (Program.ProjectileMutation(f.Conn, advance: false))
                session.Runtime.Release(new(new("Metadata/Projectiles/DefaultArrow")
                    { Speed = 10000, Range = 10, Radius = .1 },
                    new(Program.PlayerId, f.Conn.ProjectileEpoch), new(Program.PlayerId, f.Conn.ProjectileEpoch),
                    1, new(32, 32), new(40, 32))
                {
                    Payload = new ProjectileOffense(DamagePacket.Of(DamageType.Physical, 2000, 2000),
                        true, 0, 0, 150, "earlier projectile")
                });
            await f.Swing(monster).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(monster.Dead);
            Assert.Equal(10000u, f.Conn.PlayerLife);
            Assert.Equal(0, f.Count);
            Assert.Empty(incoming.Draws);
        }
        finally
        {
            f.Conn.Closed = true; session.Invalidate();
            await session.OutputCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
