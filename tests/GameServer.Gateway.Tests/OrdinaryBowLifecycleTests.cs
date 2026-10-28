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
    [InlineData("death")]
    [InlineData("transfer")]
    [InlineData("disconnect")]
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_paused_queued_action_echo_retries_after_resume_or_cancels_with_its_original_token(bool cancel)
    {
        using var wire = new Wire(); var conn = Connection(); await wire.Gate.WaitAsync();
        byte[] plain = [0x01, 0xab, 0x12, 0x34];
        var sending = Program.SendOrdinaryBowAction("bow-test", wire.Writer.GetStream(), wire.Ciphers,
            conn, plain.ToArray(), null, 7, conn.ProjectileEpoch);
        conn.Clock.Stop(); wire.Gate.Release(); await Task.Delay(50);
        Assert.False(sending.IsCompleted); Assert.Equal(0, wire.Reader.Available);
        if (cancel) conn.ActionCount++;
        conn.Clock.Start(); Assert.Equal(!cancel, await sending.WaitAsync(TimeSpan.FromSeconds(5)));
        if (cancel) Assert.Equal(0, wire.Reader.Available);
        else
        {
            byte[] received = new byte[plain.Length];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await wire.Reader.GetStream().ReadExactlyAsync(received, timeout.Token);
            new ConnectionCiphers(new byte[64], isClient: true).Receive.Process(received);
            Assert.Equal(plain, received);
        }
    }

    [Fact]
    public async Task Released_ordinary_arrow_survives_a_new_action_and_commits_impale_behind_the_send_gate()
    {
        await using var f = new ProjectileRuntimeFixture();
        var target = ProjectileRuntimeFixture.Monster(50, 10); f.Conn.Monsters.Add(42, target);
        await f.HoldOutput(); f.Release(f.Shot(offense: Offense)); f.Conn.ActionCount++;
        f.Advance(5); f.Advance(10);
        Assert.Equal(1, target.Impale.Stacks); Assert.Equal(90u, target.Life);
        Assert.True(f.Conn.LastEnemyHitAt > 0); Assert.Equal(0, f.Available);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("transfer")]
    [InlineData("target-death")]
    [InlineData("player-death")]
    public async Task Ordinary_contacts_follow_simulation_lifetime_without_waiting_for_publication(string change)
    {
        await using var f = new ProjectileRuntimeFixture();
        var target = ProjectileRuntimeFixture.Monster(100, 10); target.Impale.Apply(100);
        f.Conn.Monsters.Add(42, target); await f.HoldOutput();
        f.Release(f.Shot(offense: Offense)); f.Advance(1);
        if (change == "pause") f.Conn.Clock.Stop();
        if (change == "transfer") f.Conn.TryBeginAreaChange(new(2, 0, false), out _);
        if (change == "target-death") { target.Dead = true; target.Life = 0; }
        if (change == "player-death") f.Conn.PlayerLife = 0;
        if (change == "pause")
        {
            f.Session.Advance(); Assert.Equal(100u, target.Life); Assert.Equal(1, target.Impale.Stacks);
            f.Conn.Clock.Start();
        }
        f.Advance(10);
        if (change is "pause" or "player-death")
        {
            Assert.Equal(80u, target.Life); Assert.Equal(2, target.Impale.Stacks);
            Assert.True(f.Conn.LastEnemyHitAt > 0);
        }
        else
        {
            Assert.Equal(change == "target-death" ? 0u : 100u, target.Life);
            Assert.Equal(1, target.Impale.Stacks); Assert.Equal(double.NegativeInfinity, f.Conn.LastEnemyHitAt);
        }
        Assert.Equal(0, f.Available);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ground_dispatch_can_process_resume_after_a_paused_or_queued_bow_use(bool queued)
    {
        TestSaves.Ensure();
        string character = $"Bow{Guid.NewGuid():N}";
        var conn = new ClientState(new AreaLayout(1, "test", 300, 300, 10, 10, null, []), 1, 1)
            { Character = character, PlayerLife = 100, ActionCount = 7 };
        var items = PlayerInventories.For(character, Program.Items);
        var bow = Assert.IsType<ItemCatalog.BaseType>(Program.Items.Find("Crude Bow"));
        items.Add(PlayerInventories.Weapon1, 0, 0, new(bow.Hash, bow.Components, bow.VisualIdentity));
        using var wire = new Wire();
        var ground = (InstanceProtocol.UseSkillAtLocationId, Convert.FromHexString("0025000000640000000a010200000001"));
        var resume = (InstancePause.RequestId, new byte[] { 0x01, 0xd4, 0 });
        try
        {
            if (queued)
            {
                await wire.Gate.WaitAsync();
                var dispatch = Program.OnClientMessages("bow-test", wire.Writer.GetStream(), wire.Ciphers, [ground], [], conn);
                await dispatch.WaitAsync(TimeSpan.FromSeconds(2)); // The confirmation is queued; input dispatch is free.
                conn.Clock.Stop();
                var resuming = Program.OnClientMessages("bow-test", wire.Writer.GetStream(), wire.Ciphers, [resume], [], conn);
                Assert.False(conn.Paused); // Resume reached the clock before its own acknowledgement acquired the gate.
                conn.ActionCount++;
                wire.Gate.Release();
                await resuming.WaitAsync(TimeSpan.FromSeconds(2));
            }
            else
            {
                conn.Clock.Stop();
                var monster = new SpawnedMonster("target", new(30, 10, 10, width: 300, height: 300), 0, 2, 100);
                await Program.Attack("bow-test", wire.Writer.GetStream(), wire.Ciphers, conn,
                    new InstanceTransfer.UseSkillOnObject(42, SkillCatalog.FirstSkillId, 0, 0), monster, 0)
                    .WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(7, conn.ActionCount);
                Assert.Equal(0, conn.NextAttackAt);
                Assert.Equal((10, 10), conn.Walk.Destination);
                await Program.OnClientMessages("bow-test", wire.Writer.GetStream(), wire.Ciphers, [ground, resume], [], conn)
                    .WaitAsync(TimeSpan.FromSeconds(2));
                Assert.False(conn.Paused);
                Assert.Equal(7, conn.ActionCount);
                Assert.Equal(0, conn.NextAttackAt);
                Assert.Equal((10, 10), conn.Walk.Destination);
                Assert.Equal(0, conn.ChannelSkill);
            }
            Assert.Empty(conn.ProjectileObjects);
        }
        finally { conn.Closed = true; PlayerInventories.Forget(character); }
    }

}
