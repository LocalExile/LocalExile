using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class MonsterBossStageLifecycleTests
{
    const uint MonsterId = 0x3000;

    sealed class HeldStream(Socket socket) : NetworkStream(socket, ownsSocket: false)
    {
        public bool HoldNextWrite;
        public readonly TaskCompletionSource WriteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource AllowWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (HoldNextWrite)
            {
                HoldNextWrite = false;
                WriteEntered.TrySetResult();
                await AllowWrite.Task.WaitAsync(cancellationToken);
            }
            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly ConnectionCiphers incoming = new(new byte[64], isClient: true);
        public readonly TcpClient Writer = new(), Reader;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], isClient: false);
        public readonly HeldStream Stream;
        public SemaphoreSlim Gate => Program.SendGates.GetValue(Ciphers, _ => new(1, 1));

        public Wire()
        {
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            Writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            Reader = accepting.GetAwaiter().GetResult();
            Stream = new(Writer.Client);
        }

        public async Task<byte[]> Read(int length)
        {
            byte[] bytes = new byte[length];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Reader.GetStream().ReadExactlyAsync(bytes, timeout.Token);
            incoming.Receive.Process(bytes);
            return bytes;
        }

        public void Dispose()
        {
            Stream.AllowWrite.TrySetResult();
            Stream.Dispose();
            Writer.Dispose();
            Reader.Dispose();
            listener.Stop();
        }
    }

    static (ClientState Connection, SpawnedMonster Monster) State()
    {
        var conn = new ClientState(new AreaLayout(1, "test", 300, 300, 10, 10, null, []), 1, 1)
            { PlayerLife = 50 };
        var monster = new SpawnedMonster("test boss", new WalkEstimator(10, 10, 30, null, 300, 300), 60, 3, 100)
        {
            Attack = new MonsterAttack(0x102, 1, 10, DamagePacket.Of(DamageType.Physical, 10, 10)),
            Variety = new(1, "test", "test boss", "Monster", null, ["Positioned", "Transitionable"]),
            Stage = new(50, 2612, 3, 2),
        };
        conn.Monsters.Add(MonsterId, monster);
        return (conn, monster);
    }

    static byte[] Completion(SpawnedMonster monster) =>
    [
        .. InstanceEntities.TransitionableUpdate(MonsterId, 1, monster.Stage!.TransitionTo),
        .. InstanceProtocol.LifeUpdate(MonsterId, monster.MaxLife),
        .. InstanceProtocol.ActionEnded(MonsterId),
    ];

    [Theory]
    [InlineData("echo")]
    [InlineData("contact")]
    public async Task Claiming_a_boss_stage_permanently_invalidates_an_ordinary_swing_even_after_the_stage_ends(string work)
    {
        using var wire = new Wire();
        var (conn, monster) = State();
        int original = monster.ActionGeneration;
        monster.SwingGeneration = original;
        await wire.Gate.WaitAsync();
        Task pending = work == "echo"
            ? Program.MonsterSwing("stage-test", wire.Stream, wire.Ciphers, conn, MonsterId, monster, original)
            : Program.MonsterContact("stage-test", wire.Stream, wire.Ciphers, conn, MonsterId, monster, original);
        Assert.False(pending.IsCompleted);
        lock (conn.Monsters)
        {
            (monster.Life, bool starts) = Program.TakeDamageLocked(monster, 60);
            Assert.True(starts);
            Assert.True(monster.StageRunning);
            Assert.True(monster.StageTaken);
            Assert.Null(monster.SwingGeneration);
            Assert.Equal(original + 1, monster.ActionGeneration);
            // Even a continuation that only reaches its gate after the entire stage must remain stale.
            monster.StageRunning = false;
        }
        wire.Gate.Release();

        await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(50u, conn.PlayerLife);
        Assert.False(Program.MonsterActionCurrent(conn, MonsterId, monster, original));
        Assert.Equal(0, wire.Reader.Available);
    }

    [Fact]
    public async Task Stage_completion_is_written_before_the_next_planned_attack_even_if_its_socket_write_is_slow()
    {
        using var wire = new Wire();
        var (conn, monster) = State();
        monster.StageTaken = monster.StageRunning = true;
        monster.Life = 40;
        wire.Stream.HoldNextWrite = true;
        var completing = Program.CompleteMonsterStage("stage-test", wire.Stream, wire.Ciphers, conn, MonsterId, monster);
        await wire.Stream.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var plan = Program.PlanMonsterAi(conn, new MonsterAiCadence(), conn.Clock.Elapsed.TotalSeconds);
        int action = Assert.Single(plan.Swings).Action;
        var swing = Program.MonsterSwing("stage-test", wire.Stream, wire.Ciphers, conn, MonsterId, monster, action);
        Assert.False(swing.IsCompleted);
        Assert.Equal(0, wire.Reader.Available);
        wire.Stream.AllowWrite.TrySetResult();

        await completing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Completion(monster), await wire.Read(Completion(monster).Length));
        byte[] echo = InstanceProtocol.ActionStarted(MonsterId, 10, 10, 10, 10, monster.Attack!.SkillId, 0xffff,
            0x0408, targetObjectId: 0x1000);
        Assert.Equal(echo, await wire.Read(echo.Length));
        lock (conn.Monsters) monster.ActionGeneration++;
        await swing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(monster.StageRunning);
        Assert.Equal(monster.MaxLife, monster.Life);
        Assert.Equal(50u, conn.PlayerLife);
        Assert.Equal(0, wire.Reader.Available);
    }

    [Fact]
    public async Task A_stage_completion_queued_before_pause_cannot_refill_or_unblock_its_boss_until_resume()
    {
        using var wire = new Wire();
        var (conn, monster) = State();
        monster.StageTaken = monster.StageRunning = true;
        monster.Life = 40;
        await wire.Gate.WaitAsync();
        var completing = Program.CompleteMonsterStage("stage-test", wire.Stream, wire.Ciphers, conn, MonsterId, monster);
        conn.Clock.Stop();
        wire.Gate.Release();

        await Task.Delay(60);

        Assert.False(completing.IsCompleted);
        Assert.True(monster.StageRunning);
        Assert.Equal(40u, monster.Life);
        Assert.Equal(0, wire.Reader.Available);
        conn.Clock.Start();
        await completing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Completion(monster), await wire.Read(Completion(monster).Length));
        Assert.False(monster.StageRunning);
        Assert.Equal(monster.MaxLife, monster.Life);
    }

    [Theory]
    [InlineData("transfer")]
    [InlineData("disconnect")]
    [InlineData("removed")]
    [InlineData("replaced")]
    [InlineData("dead")]
    [InlineData("zero-life")]
    public async Task A_stage_completion_rechecks_its_area_and_monster_identity_at_the_actual_send_gate(string change)
    {
        using var wire = new Wire();
        var (conn, monster) = State();
        monster.StageTaken = monster.StageRunning = true;
        monster.Life = 40;
        await wire.Gate.WaitAsync();
        var completing = Program.CompleteMonsterStage("stage-test", wire.Stream, wire.Ciphers, conn, MonsterId, monster);
        lock (conn.Monsters)
        {
            if (change == "transfer") Assert.True(conn.TryBeginAreaChange(new PendingTransfer(2, 0, false), out _));
            if (change == "disconnect") conn.Closed = true;
            if (change == "removed") conn.Monsters.Remove(MonsterId);
            if (change == "replaced") conn.Monsters[MonsterId] = State().Monster;
            if (change == "dead") monster.Dead = true;
            if (change == "zero-life") monster.Life = 0;
        }
        wire.Gate.Release();

        await completing.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(monster.StageRunning);
        Assert.Equal(change == "zero-life" ? 0u : 40u, monster.Life);
        Assert.Equal(0, wire.Reader.Available);
    }
}
