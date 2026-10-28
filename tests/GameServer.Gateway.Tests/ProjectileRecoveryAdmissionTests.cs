using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public partial class OwnAuraGatewayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paid_KB_windup_stays_cancelled_after_recovery_ends_even_when_catching_up(bool catchUp)
    {
        using var f = new Fixture("KineticBlast");
        f.Conn.PlayerStats[Program.Stats.Key("attack_speed_+%")!.Value] = -50;
        f.Conn.PlayerStats[Program.Stats.Key("stun_recovery_+%")!.Value] = 10000;
        f.Conn.Pools = Program.PoolsFor(f.Conn, 100) with { Accuracy = int.MaxValue };
        var target = new SpawnedMonster("cancelled attack target", new(60, 32, 0), 0, 2, 10000);
        f.Conn.Monsters.Add(42, target);
        var session = new Program.ProjectileSession("paid-recovery", f.Stream, f.Ciphers, f.Conn);
        f.Conn.ProjectileRuntime = session;
        await f.Gate.WaitAsync();
        try
        {
            uint mana = f.Conn.PlayerMana;
            double duration = (double)Call("SupportedAttackSeconds", f.Conn, f.Skill())!;
            double delay = (double)Call("AttackContactDelay", f.Conn, duration, false)!;
            double admittedAt = f.Conn.Clock.Elapsed.TotalSeconds;
            using (Program.ProjectileMutation(f.Conn, advance: false))
            {
                Assert.True(f.Use("ground").IsCompletedSuccessfully);
                Assert.True(f.Conn.PlayerMana < mana);
                Assert.Empty(session.Runtime.Projectiles);
            }
            Program.PlayerRecoveryIntent recovery;
            using (Program.ProjectileMutation(f.Conn, advance: false))
                recovery = Assert.IsType<Program.PlayerRecoveryIntent>(Program.CommitPlayerHitRecoveryLocked(
                    f.Conn, 100000, f.Conn.Clock.Elapsed.TotalSeconds));
            Assert.True(recovery.Until < admittedAt + delay, "Recovery must finish before the scheduled slow release.");
            await Until(() => f.Conn.Clock.Elapsed.TotalSeconds >= recovery.Until);
            Assert.False(f.Conn.HitRecovery.Active(f.Conn.Clock.Elapsed.TotalSeconds));
            if (!catchUp) session.AdvanceTo(ProjectileRuntime.Time(recovery.Until));
            session.AdvanceTo(Math.Max(session.Runtime.Now, ProjectileRuntime.Time(admittedAt + delay + 2)));
            Assert.Equal(10000u, target.Life);
            Assert.Empty(session.Runtime.Projectiles);
        }
        finally
        {
            f.Conn.Closed = true; session.Invalidate(); f.Gate.Release();
            await session.OutputCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Recovery_keeps_an_already_released_KB_projectile_alive()
    {
        using var f = new Fixture("KineticBlast");
        f.Conn.Pools = Program.PoolsFor(f.Conn, 100) with { Accuracy = int.MaxValue };
        var target = new SpawnedMonster("released flight target", new(100, 32, 0), 0, 2, 10000);
        f.Conn.Monsters.Add(42, target);
        var session = new Program.ProjectileSession("released-recovery", f.Stream, f.Ciphers, f.Conn);
        f.Conn.ProjectileRuntime = session;
        await f.Gate.WaitAsync();
        try
        {
            using (Program.ProjectileMutation(f.Conn, advance: false))
            {
                Assert.True(f.Use("ground").IsCompletedSuccessfully);
                for (int i = 0; i < 120 && session.Runtime.Projectiles.Count == 0; i++)
                    session.AdvanceTo(session.Runtime.Now + ProjectileRuntime.TickQuanta);
                var flight = Assert.Single(session.Runtime.Projectiles);
                Assert.Equal(10000u, target.Life);
                Assert.NotNull(Program.CommitPlayerHitRecoveryLocked(f.Conn, 100000, Program.ProjectileWorldTime(f.Conn)));
                Assert.Contains(flight, session.Runtime.Projectiles);
                session.AdvanceTo(session.Runtime.Now + ProjectileRuntime.Time(2));
                Assert.True(target.Life < 10000);
            }
        }
        finally
        {
            f.Conn.Closed = true; session.Invalidate(); f.Gate.Release();
            await session.OutputCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
