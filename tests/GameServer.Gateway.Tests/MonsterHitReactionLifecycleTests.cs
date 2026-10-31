using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class MonsterHitReactionLifecycleTests
{
    const uint MonsterId = 0x3000;

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

        public async Task<byte[]> Read(int length)
        {
            byte[] bytes = new byte[length];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Reader.GetStream().ReadExactlyAsync(bytes, timeout.Token);
            return bytes;
        }

        public void Dispose() { Writer.Dispose(); Reader.Dispose(); listener.Stop(); }
    }

    static (ClientState Connection, SpawnedMonster Monster) State(double attackSeconds = 0.4)
    {
        var conn = new ClientState(new AreaLayout(1, "test", 300, 300, 10, 10, null, []), 1, 1)
            { PlayerLife = 50 };
        var monster = new SpawnedMonster("test monster", new WalkEstimator(10, 10, 30, null, 300, 300), 60, 3, 100)
        {
            Attack = new MonsterAttack(0x102, attackSeconds, 10, DamagePacket.Of(DamageType.Physical, 10, 10)),
            HitAnimations = [new HitAnimation(7, 0.4)],
        };
        conn.Monsters.Add(MonsterId, monster);
        return (conn, monster);
    }

    static int EchoLength(SpawnedMonster m) =>
        InstanceProtocol.ActionStarted(MonsterId, 10, 10, 10, 10, m.Attack!.SkillId, 0xffff, 0x0408,
            targetObjectId: 0x1000).Length;

    [Theory]
    [InlineData("flinch")]
    [InlineData("death")]
    [InlineData("removed")]
    [InlineData("replaced")]
    [InlineData("transfer")]
    [InlineData("disconnect")]
    public async Task A_queued_swing_echo_cannot_start_after_its_monster_action_was_invalidated(string change)
    {
        using var wire = new Wire();
        var (conn, m) = State();
        await wire.Gate.WaitAsync();
        var swing = Program.MonsterSwing("reaction-test", wire.Writer.GetStream(), wire.Ciphers, conn, MonsterId, m, m.ActionGeneration);
        Assert.False(swing.IsCompleted);
        lock (conn.Monsters)
        {
            if (change == "flinch") Assert.NotNull(HitReaction.TryFlinch(m, 10, conn.Clock.Elapsed.TotalSeconds, new Random(1)));
            if (change == "death") m.Life = 0;
            if (change == "removed") conn.Monsters.Remove(MonsterId);
            if (change == "replaced") conn.Monsters[MonsterId] = State().Monster;
            if (change == "transfer") conn.TryBeginAreaChange(new PendingTransfer(2, 0, false), out _);
            if (change == "disconnect") conn.Closed = true;
        }
        wire.Gate.Release();

        await swing.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(50u, conn.PlayerLife);
        Assert.Equal(0, wire.Reader.Available);
    }

    [Fact]
    public async Task A_sent_swing_interrupted_by_a_flinch_cannot_deal_its_pending_damage()
    {
        using var wire = new Wire();
        var (conn, m) = State(attackSeconds: 2);
        int original = m.ActionGeneration;
        var swing = Program.MonsterSwing("reaction-test", wire.Writer.GetStream(), wire.Ciphers, conn, MonsterId, m, original);
        await wire.Read(EchoLength(m));

        await Program.SendMonsterFlinch("reaction-test", wire.Writer.GetStream(), wire.Ciphers, conn, MonsterId, m, 10, log: false);
        await wire.Read(InstanceProtocol.ActorAnimation(MonsterId, 0.4f, PlayerHitRecovery.TakeHitAnimation).Length);
        await swing.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(50u, conn.PlayerLife);
        Assert.False(Program.MonsterActionCurrent(conn, MonsterId, m, original));
        Assert.Equal(0, wire.Reader.Available);
    }

    [Fact]
    public async Task A_contact_waiting_at_the_send_gate_rechecks_flinch_before_mutating_life()
    {
        using var wire = new Wire();
        var (conn, m) = State();
        await wire.Gate.WaitAsync();
        var contact = Program.MonsterContact("reaction-test", wire.Writer.GetStream(), wire.Ciphers, conn, MonsterId, m, m.ActionGeneration);
        Assert.False(contact.IsCompleted);   // the production damage commit is waiting at the actual send gate
        lock (conn.Monsters)
            Assert.NotNull(HitReaction.TryFlinch(m, 10, conn.Clock.Elapsed.TotalSeconds, new Random(1)));
        wire.Gate.Release();

        await contact.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(50u, conn.PlayerLife);
        Assert.Equal(0, wire.Reader.Available);
    }

    [Fact]
    public async Task A_flinch_queued_behind_the_send_gate_starts_its_recovery_when_the_animation_is_sent()
    {
        using var wire = new Wire();
        var (conn, m) = State();
        m.Walk.Order(100, 10, 0);
        await wire.Gate.WaitAsync();
        var flinch = Program.SendMonsterFlinch("reaction-test", wire.Writer.GetStream(), wire.Ciphers, conn, MonsterId, m, 10, log: false);
        await Task.Delay(30);
        Assert.Equal(0, m.ActionGeneration);
        Assert.Equal(double.NegativeInfinity, m.FlinchUntil);
        double beforeSend = conn.Clock.Elapsed.TotalSeconds;
        wire.Gate.Release();

        await flinch.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(m.FlinchUntil >= beforeSend + 0.4);
        Assert.Equal(m.Walk.Position(conn.Clock.Elapsed.TotalSeconds), m.Walk.Position(100));
        byte[] message = await wire.Read(InstanceProtocol.ActorAnimation(MonsterId, 0.4f, PlayerHitRecovery.TakeHitAnimation).Length);
        new ConnectionCiphers(new byte[64], isClient: true).Receive.Process(message);
        Assert.Equal(InstanceProtocol.ActorAnimation(MonsterId, 0.4f, PlayerHitRecovery.TakeHitAnimation), message);
        Assert.Equal(0, wire.Reader.Available);   // no ActionEnded or rig-only clip can override native recovery
    }

    [Theory]
    [InlineData("death")]
    [InlineData("removed")]
    [InlineData("replaced")]
    [InlineData("transfer")]
    [InlineData("disconnect")]
    [InlineData("zero damage")]
    public async Task An_invalidated_queued_flinch_cannot_change_state_or_send_a_stale_animation(string change)
    {
        using var wire = new Wire();
        var (conn, m) = State();
        await wire.Gate.WaitAsync();
        var flinch = Program.SendMonsterFlinch("reaction-test", wire.Writer.GetStream(), wire.Ciphers, conn, MonsterId, m,
            change == "zero damage" ? 0 : 10, log: false);
        lock (conn.Monsters)
        {
            if (change == "death") m.Life = 0;
            if (change == "removed") conn.Monsters.Remove(MonsterId);
            if (change == "replaced") conn.Monsters[MonsterId] = State().Monster;
            if (change == "transfer") conn.TryBeginAreaChange(new PendingTransfer(2, 0, false), out _);
            if (change == "disconnect") conn.Closed = true;
        }
        int generationBeforeGate = m.ActionGeneration;
        wire.Gate.Release();

        await flinch.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(generationBeforeGate, m.ActionGeneration);
        Assert.Equal(double.NegativeInfinity, m.FlinchUntil);
        Assert.Equal(0, wire.Reader.Available);
    }

    [Fact]
    public async Task A_paused_flinch_waits_without_starting_recovery_or_sending_an_animation()
    {
        using var wire = new Wire();
        var (conn, m) = State();
        conn.Clock.Stop();
        var flinch = Program.SendMonsterFlinch("reaction-test", wire.Writer.GetStream(), wire.Ciphers, conn, MonsterId, m, 10, log: false);
        await Task.Delay(50);

        Assert.False(flinch.IsCompleted);
        Assert.Equal(0, m.ActionGeneration);
        Assert.Equal(double.NegativeInfinity, m.FlinchUntil);
        Assert.Equal(0, wire.Reader.Available);
        conn.Clock.Start();

        await flinch.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, m.ActionGeneration);
        Assert.True(m.FlinchUntil > conn.Clock.Elapsed.TotalSeconds);
        await wire.Read(InstanceProtocol.ActorAnimation(MonsterId, 0.4f, PlayerHitRecovery.TakeHitAnimation).Length);
    }

    [Fact]
    public async Task Paused_contact_and_recovery_use_game_time_and_the_old_swing_never_revives()
    {
        var (conn, m) = State();
        conn.Clock.Stop();
        double until = conn.Clock.Elapsed.TotalSeconds + 0.03;
        int original = m.ActionGeneration;
        var contact = Program.WaitMonsterActionTime(conn, MonsterId, m, original, until);
        await Task.Delay(70);
        Assert.False(contact.IsCompleted);
        conn.Clock.Start();
        Assert.True(await contact.WaitAsync(TimeSpan.FromSeconds(5)));

        lock (conn.Monsters)
            Assert.NotNull(HitReaction.TryFlinch(m, 10, conn.Clock.Elapsed.TotalSeconds, new Random(1)));
        conn.Clock.Stop();
        double frozen = conn.Clock.Elapsed.TotalSeconds;
        await Task.Delay(450);
        Assert.Equal(frozen, conn.Clock.Elapsed.TotalSeconds);
        Assert.False(Program.MonsterActionCurrent(conn, MonsterId, m, m.ActionGeneration));
        Assert.False(Program.MonsterActionCurrent(conn, MonsterId, m, original));
        conn.Clock.Start();
        await Task.Delay(450);
        Assert.True(Program.MonsterActionCurrent(conn, MonsterId, m, m.ActionGeneration));
        Assert.False(Program.MonsterActionCurrent(conn, MonsterId, m, original));
    }
}
