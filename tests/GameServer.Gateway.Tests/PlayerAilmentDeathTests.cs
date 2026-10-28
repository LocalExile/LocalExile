using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class OwnAuraGatewayTests
{
    static void BeginLethalIgnite(ClientState state)
    {
        state.PlayerLife = 1;
        double now = state.Clock.Elapsed.TotalSeconds;
        var fire = DamagePacket.Of(DamageType.Fire, 100, 100);
        state.ElementalAilments.Apply(fire, fire, true, state.PlayerMaxLife, now, _ => 0, new Random(1));
    }

    [Fact]
    public async Task Lethal_ignite_resource_writer_runs_normal_death_cleanup_and_publishes_once()
    {
        using var f = new Fixture();
        Program.FortificationOf(f.Conn).State.GrantExplicitStacks(f.Conn.Clock.Elapsed.TotalSeconds, 1);
        BeginLethalIgnite(f.Conn);
        var recovery = (Task)Call("RunResourceRecovery", "ailment-death", f.Stream, f.Ciphers, f.Conn)!;
        try
        {
            await Until(() => f.Conn.DeathNotificationEpoch >= 0);
            await Task.Delay(100);
            byte[] bytes = await f.Drain();
            Assert.Equal(0u, f.Conn.PlayerLife);
            Assert.Equal(1, Count(bytes, InstanceProtocol.DeathScreen(Program.DeathScreenFlags(f.Conn))));
            Assert.True(f.Conn.AuraDeathHandled);
            Assert.False(f.Conn.ElementalAilments.HasEffects);
            Assert.Equal(0, Program.FortificationOf(f.Conn).Read(f.Conn.Clock.Elapsed.TotalSeconds).Current);
            await (Task)Call("PlayerDied", "ailment-death", f.Stream, f.Ciphers, f.Conn, "duplicate death")!;
            await Task.Delay(100);
            Assert.Equal(0, Count(await f.Drain(), InstanceProtocol.DeathScreen(Program.DeathScreenFlags(f.Conn))));
        }
        finally { f.Conn.Closed = true; await recovery.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task Righteous_Fire_remains_nonlethal_and_does_not_publish_an_ailment_death()
    {
        using var f = new Fixture("RighteousFire");
        await f.Use("ground");
        await Until(() => f.Conn.RighteousFire is not null);
        await f.Drain();
        f.Conn.PlayerLife = 1;
        var recovery = (Task)Call("RunResourceRecovery", "rf-nonlethal", f.Stream, f.Ciphers, f.Conn)!;
        try
        {
            await Until(() => f.Conn.RighteousFire is null);
            Assert.Equal(1u, f.Conn.PlayerLife);
            Assert.Equal(-1, f.Conn.DeathNotificationEpoch);
            Assert.Equal(0, Count(await f.Drain(), InstanceProtocol.DeathScreen(Program.DeathScreenFlags(f.Conn))));
        }
        finally { f.Conn.Closed = true; await recovery.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Held_ailment_death_notification_is_discarded_after_resurrection_or_transfer(bool transfer)
    {
        using var f = new Fixture();
        await f.Gate.WaitAsync();
        BeginLethalIgnite(f.Conn);
        var recovery = (Task)Call("RunResourceRecovery", "held-ailment-death", f.Stream, f.Ciphers, f.Conn)!;
        Task resurrect = Task.CompletedTask;
        bool held = true;
        try
        {
            await Until(() => f.Conn.DeathNotificationEpoch >= 0);
            if (transfer) Assert.True(f.Conn.TryBeginAreaChange(new(2, 0, false), out _));
            else resurrect = (Task)Call("Resurrect", "held-ailment-death", f.Stream, f.Ciphers, f.Conn, false)!;
            f.Gate.Release(); held = false;
            await resurrect;
            await Task.Delay(100);
            Assert.Equal(0, Count(await f.Drain(), InstanceProtocol.DeathScreen(Program.DeathScreenFlags(f.Conn))));
        }
        finally
        {
            f.Conn.Closed = true;
            if (held) f.Gate.Release();
            await recovery.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
