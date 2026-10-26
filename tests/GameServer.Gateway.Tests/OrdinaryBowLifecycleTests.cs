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
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            Writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            Reader = accepting.GetAwaiter().GetResult();
        }
        public void Dispose() { Writer.Dispose(); Reader.Dispose(); listener.Stop(); }
    }

    static ClientState Connection()
    {
        TestSaves.Ensure();
        return new(new AreaLayout(1, "test", 300, 300, 10, 10, null, []), 1, 1)
        { Character = $"BowLife{Guid.NewGuid():N}", PlayerLife = 100, ActionCount = 7 };
    }

    static Task Flight(Wire wire, ClientState conn, OrdinaryBowShot? shot = null) => Program.FlyProjectile("bow-test", wire.Writer.GetStream(), wire.Ciphers,
            conn, ("Metadata/Projectiles/DefaultArrow", 100), (10, 10), (110, 10), null,
            conn.ProjectileEpoch, log: false, impact: "none", actionToken: 7, ordinaryShot: shot);

    static async Task ReadLaunch(Wire wire, CancellationToken cancellation)
    {
        // These fixed-size packets prove that the glide factory has initialized the flight, not just that creation
        // registered its id. Holding the gate after this witness really queues an in-flight contact sample.
        int create = InstanceEntities.CreateObject(0x3000, 0, new ProjectileObject("arrow", 0, 0, Height: 0).Blob()).Length;
        int wake = InstanceProtocol.EnterWakeZone(0x3000).Length;
        int move = InstanceProtocol.MoveTo(0x3000, 0, 0, 1).Length;
        byte[] bytes = new byte[create + wake + move];
        await wire.Reader.GetStream().ReadExactlyAsync(bytes, cancellation);
        new ConnectionCiphers(new byte[64], isClient: true).Receive.Process(bytes);
        Assert.Equal(new byte[] { 0x01, 0xba }, bytes.AsSpan(create + wake, 2).ToArray());
    }

    [Theory]
    [InlineData("action")]
    [InlineData("death")]
    [InlineData("transfer")]
    [InlineData("disconnect")]
    public async Task Creation_waiting_at_the_actual_send_gate_cannot_release_a_cancelled_shot(string cancellation)
    {
        using var wire = new Wire();
        var conn = Connection();
        await wire.Gate.WaitAsync();
        var flight = Flight(wire, conn);
        Assert.False(flight.IsCompleted);
        lock (conn.Monsters)
        {
            if (cancellation == "action") conn.ActionCount++;
            if (cancellation == "death") conn.PlayerLife = 0;
            if (cancellation == "transfer") conn.TryBeginAreaChange(new PendingTransfer(2, 0, false), out _);
            if (cancellation == "disconnect") conn.Closed = true;
        }
        wire.Gate.Release();
        await flight.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(conn.ProjectileObjects);
        Assert.Equal(0, wire.Reader.Available);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_paused_queued_action_echo_retries_after_resume_or_cancels_with_its_original_token(bool cancel)
    {
        using var wire = new Wire();
        var conn = Connection();
        await wire.Gate.WaitAsync();
        byte[] plain = [0x01, 0xab, 0x12, 0x34];
        var sending = Program.SendOrdinaryBowAction("bow-test", wire.Writer.GetStream(), wire.Ciphers,
            conn, plain.ToArray(), null, 7, conn.ProjectileEpoch);
        conn.Clock.Stop();
        wire.Gate.Release();
        await Task.Delay(50);
        Assert.False(sending.IsCompleted);
        Assert.Equal(0, wire.Reader.Available);
        if (cancel) conn.ActionCount++;
        conn.Clock.Start();
        Assert.Equal(!cancel, await sending.WaitAsync(TimeSpan.FromSeconds(5)));
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
    public async Task Paused_creation_waits_and_a_released_arrow_survives_a_later_player_action_until_one_impact()
    {
        using var wire = new Wire();
        var conn = Connection();
        var target = new SpawnedMonster("target", new(50, 10, 10, width: 300, height: 300), 0, 2, 100);
        conn.Monsters.Add(42, target);
        var shot = new OrdinaryBowShot(DamagePacket.Of(DamageType.Physical, 10, 10), int.MaxValue, 0, 150, 100);
        await wire.Gate.WaitAsync();
        var flight = Flight(wire, conn, shot: shot);
        conn.Clock.Stop();
        wire.Gate.Release();
        await Task.Delay(50);
        Assert.Empty(conn.ProjectileObjects);
        Assert.Equal(0, wire.Reader.Available);
        conn.Clock.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            lock (conn.Monsters) if (conn.ProjectileObjects.Count != 0) { conn.ActionCount++; break; }
            await Task.Delay(1, timeout.Token);
        }
        await flight.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, target.Impale.Stacks);
        Assert.Equal(90u, target.Life);
        Assert.True(conn.LastEnemyHitAt > 0);
        Assert.Empty(conn.ProjectileObjects);
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

    [Theory]
    [InlineData("pause")]
    [InlineData("transfer")]
    [InlineData("target-death")]
    [InlineData("player-death")]
    public async Task A_queued_ordinary_contact_rechecks_lifetime_before_mutating_target_state(string change)
    {
        using var wire = new Wire();
        var conn = Connection();
        var target = new SpawnedMonster("target", new(100, 10, 10, width: 300, height: 300), 0, 2, 100);
        target.Impale.Apply(100);
        conn.Monsters.Add(42, target);
        var shot = new OrdinaryBowShot(DamagePacket.Of(DamageType.Physical, 10, 10), int.MaxValue, 0, 150, 100);
        var flight = Flight(wire, conn, shot);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await ReadLaunch(wire, timeout.Token);
        await wire.Gate.WaitAsync(timeout.Token);
        await Task.Delay(1400, timeout.Token); // The sweep/accuracy has been waiting behind this gate beyond contact.
        Assert.Equal(100u, target.Life);
        Assert.Equal(1, target.Impale.Stacks);
        Assert.Equal(double.NegativeInfinity, conn.LastEnemyHitAt);
        lock (conn.Monsters)
        {
            if (change == "pause") conn.Clock.Stop();
            if (change == "transfer") conn.TryBeginAreaChange(new PendingTransfer(2, 0, false), out _);
            if (change == "target-death") { target.Dead = true; target.Life = 0; }
            if (change == "player-death") conn.PlayerLife = 0;
        }
        wire.Gate.Release();
        if (change == "pause")
        {
            await Task.Delay(50, timeout.Token);
            Assert.Equal(100u, target.Life);
            Assert.Equal(1, target.Impale.Stacks);
            conn.Clock.Start();
        }
        await flight.WaitAsync(TimeSpan.FromSeconds(5));
        if (change is "pause" or "player-death")
        {
            Assert.Equal(80u, target.Life);
            Assert.Equal(2, target.Impale.Stacks);
        }
        else
        {
            Assert.Equal(change == "target-death" ? 0u : 100u, target.Life);
            Assert.Equal(1, target.Impale.Stacks);
            Assert.Equal(double.NegativeInfinity, conn.LastEnemyHitAt);
        }
    }
}
