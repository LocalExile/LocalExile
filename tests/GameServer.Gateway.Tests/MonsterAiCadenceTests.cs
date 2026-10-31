using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class MonsterAiCadenceTests
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

    static (ClientState Connection, SpawnedMonster Monster) State(int playerX = 50, double? attackSeconds = null)
    {
        var conn = new ClientState(new AreaLayout(1, "test", 400, 400, playerX, 10, null, []), 1, 1) { PlayerLife = 50 };
        var monster = new SpawnedMonster("AI test", new WalkEstimator(10, 10, 1, null, 400, 400), 60, 3, 100)
        {
            HitAnimations = [new HitAnimation(7, 0.4)],
            Attack = attackSeconds is double seconds
                ? new MonsterAttack(0x102, seconds, 8, DamagePacket.Of(DamageType.Physical, 10, 10)) : null,
        };
        conn.Monsters.Add(MonsterId, monster);
        return (conn, monster);
    }

    static Program.MonsterAiPlan Plan(ClientState conn, MonsterAiCadence cadence, double now) =>
        Program.PlanMonsterAi(conn, cadence, now);

    [Fact]
    public async Task A_running_enemy_worker_sends_its_first_chase_promptly_after_the_player_enters_aggro_range()
    {
        using var wire = new Wire();
        var (conn, monster) = State(playerX: 150);
        var worker = Program.RunMonsterAi("ai-test", wire.Writer.GetStream(), wire.Ciphers, conn);
        try
        {
            await Task.Delay(20);
            Assert.Equal(0, wire.Reader.Available);
            long entered = Stopwatch.GetTimestamp();
            lock (conn.Monsters) conn.Walk.Teleport(50, 10, conn.Clock.Elapsed.TotalSeconds);
            byte[] header = await wire.Read(2).WaitAsync(TimeSpan.FromMilliseconds(300));
            Assert.True(Stopwatch.GetElapsedTime(entered).TotalMilliseconds < 300);
            Assert.True(monster.Aggro);
            new ConnectionCiphers(new byte[64], isClient: true).Receive.Process(header);
            Assert.Equal(new byte[] { 0x01, 0xab }, header);
        }
        finally
        {
            conn.Closed = true;
            await worker.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void Stable_chase_paths_are_not_restarted_by_twenty_decisions_per_second()
    {
        var (conn, monster) = State();
        var cadence = new MonsterAiCadence();
        int moves = 0;
        for (int step = 0; step <= 40; step++) moves += Plan(conn, cadence, step * 0.05).Moves.Count;

        Assert.Equal(1, moves);
        Assert.Equal(1, monster.ActionGeneration);
        // A size-2 body's surround cell (MonsterSurround): the innermost ring, contact radius 5, on its own bearing.
        Assert.Equal((45, 10), monster.LastTarget);
    }

    [Fact]
    public void Moving_target_repaths_are_bounded_to_ten_per_second_even_if_collision_clears_the_chase_intent()
    {
        var (conn, monster) = State();
        var cadence = new MonsterAiCadence();
        var issued = new List<double>();
        for (int step = 0; step <= 20; step++)
        {
            double now = step * 0.05;
            conn.Walk.Teleport(50 + step * 2, 10, now);
            monster.LastTarget = null;   // collision repair must not bypass the per-monster repath budget
            if (Plan(conn, cadence, now).Moves.Count > 0) issued.Add(now);
        }

        Assert.InRange(issued.Count, 8, 11);
        Assert.All(issued.Zip(issued.Skip(1)), pair => Assert.True(pair.Second - pair.First >= 0.1 - 1e-9));
    }

    [Fact]
    public void Small_target_jitter_does_not_replace_a_valid_chase_path()
    {
        var (conn, monster) = State();
        var cadence = new MonsterAiCadence();
        Assert.Single(Plan(conn, cadence, 0).Moves);
        int generation = monster.ActionGeneration;
        conn.Walk.Teleport(51, 10, 0.1);

        Assert.Empty(Plan(conn, cadence, 0.1).Moves);
        Assert.Equal(generation, monster.ActionGeneration);
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.714)]
    [InlineData(1.24)]
    public void Enemy_attack_reservations_keep_their_full_duration_with_at_most_one_decision_step_of_delay(double seconds)
    {
        var (conn, monster) = State(playerX: 15, attackSeconds: seconds);
        var cadence = new MonsterAiCadence();
        var attacks = new List<double>();
        for (int step = 0; step <= 100; step++)
        {
            double now = step * 0.05;
            var plan = Plan(conn, cadence, now);
            if (plan.Swings.Count == 0) continue;
            attacks.Add(now);
            cadence.CompleteSwing(monster, plan.Swings[0].Action);   // completed dispatch/contact; BusyUntil still owns recovery
        }

        Assert.True(attacks.Count >= 4);
        Assert.All(attacks.Zip(attacks.Skip(1)), pair =>
            Assert.InRange(pair.Second - pair.First, seconds - 1e-9, seconds + MonsterAiCadence.StepSeconds + 1e-9));
    }

    [Fact]
    public void A_swing_pending_at_the_send_gate_is_not_reserved_again_and_old_completion_cannot_clear_its_replacement()
    {
        var (conn, monster) = State(playerX: 15, attackSeconds: 0.2);
        var cadence = new MonsterAiCadence();
        int original = Assert.Single(Plan(conn, cadence, 0).Swings).Action;
        Assert.Empty(Plan(conn, cadence, 1).Swings);
        Assert.Equal(original, monster.ActionGeneration);
        Assert.NotNull(HitReaction.TryFlinch(monster, 10, 1, new Random(1)));
        Assert.Empty(Plan(conn, cadence, 1.2).Swings);
        int replacement = Assert.Single(Plan(conn, cadence, 1.4).Swings).Action;
        cadence.CompleteSwing(monster, original);
        Assert.True(cadence.SwingPending(monster));
        cadence.CompleteSwing(monster, replacement);
        Assert.False(cadence.SwingPending(monster));
    }

    [Theory]
    [InlineData(15)]
    [InlineData(50)]
    public void A_running_boss_stage_blocks_swing_and_chase_planning_even_after_its_busy_deadline(int playerX)
    {
        var (conn, monster) = State(playerX, attackSeconds: 0.2);
        var cadence = new MonsterAiCadence();
        monster.StageRunning = true;
        monster.BusyUntil = 0.2;
        monster.ActionGeneration = 7;
        monster.Walk.Order(100, 10, 0);
        var before = monster.Walk.Position(1);

        var blocked = Plan(conn, cadence, 1);

        Assert.Empty(blocked.Swings);
        Assert.Empty(blocked.Moves);
        Assert.Empty(blocked.BossBars);
        Assert.Equal(7, monster.ActionGeneration);
        Assert.Equal(before, monster.Walk.Position(1));
        Assert.Null(monster.LastTarget);
        Assert.False(monster.Aggro);

        monster.StageRunning = false;
        var resumed = Plan(conn, cadence, 1.05);
        if (playerX == 15) Assert.Single(resumed.Swings);
        else Assert.Single(resumed.Moves);
    }

    [Fact]
    public void Slow_frames_and_resume_make_one_decision_without_replaying_missed_steps_or_attacks()
    {
        var (conn, monster) = State(playerX: 15, attackSeconds: 0.2);
        var cadence = new MonsterAiCadence();
        int first = Assert.Single(Plan(conn, cadence, 0).Swings).Action;
        cadence.CompleteSwing(monster, first);
        int afterStall = Assert.Single(Plan(conn, cadence, 20).Swings).Action;
        cadence.CompleteSwing(monster, afterStall);
        Assert.Empty(Plan(conn, cadence, 20).Swings);
        Assert.Empty(Plan(conn, cadence, 20.01).Swings);

        conn.Clock.Stop();
        Assert.Empty(Plan(conn, cadence, 20.2).Swings);
        Assert.Empty(Plan(conn, cadence, 20.2).Swings);
        conn.Clock.Start();
        int resumed = Assert.Single(Plan(conn, cadence, 20.2).Swings).Action;
        cadence.CompleteSwing(monster, resumed);
        Assert.Empty(Plan(conn, cadence, 20.2).Swings);
    }

    [Fact]
    public void Ai_off_does_not_replace_a_manual_monster_walk()
    {
        var (conn, monster) = State();
        var cadence = new MonsterAiCadence();
        conn.AiEnabled = false;
        monster.Walk.Order(100, 10, 0);
        var before = monster.Walk.Position(1);
        Assert.Empty(Plan(conn, cadence, 1).Moves);
        Assert.Equal(before, monster.Walk.Position(1));
        Assert.False(monster.Aggro);
        Assert.Equal(0, monster.ActionGeneration);
        conn.AiEnabled = true;
        Assert.Single(Plan(conn, cadence, 1.05).Moves);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("transfer")]
    [InlineData("disconnect")]
    [InlineData("flinch")]
    public async Task A_queued_chase_is_held_or_invalidated_at_the_actual_send_gate(string change)
    {
        using var wire = new Wire();
        var (conn, monster) = State();
        var cadence = new MonsterAiCadence();
        await wire.Gate.WaitAsync();
        var step = Program.StepMonsterAi("ai-test", wire.Writer.GetStream(), wire.Ciphers, conn, cadence);
        Assert.False(step.IsCompleted);
        lock (conn.Monsters)
        {
            if (change == "pause") conn.Clock.Stop();
            if (change == "transfer") conn.TryBeginAreaChange(new PendingTransfer(2, 0, false), out _);
            if (change == "disconnect") conn.Closed = true;
            if (change == "flinch") Assert.NotNull(HitReaction.TryFlinch(monster, 10, conn.Clock.Elapsed.TotalSeconds, new Random(1)));
        }
        wire.Gate.Release();
        if (change == "pause")
        {
            await Task.Delay(60);
            Assert.False(step.IsCompleted);
            Assert.Equal(0, wire.Reader.Available);
            conn.Clock.Start();
        }

        await step.WaitAsync(TimeSpan.FromSeconds(5));
        if (change == "pause")
        {
            byte[] header = await wire.Read(2);
            new ConnectionCiphers(new byte[64], isClient: true).Receive.Process(header);
            Assert.Equal(new byte[] { 0x01, 0xab }, header);
        }
        else Assert.Equal(0, wire.Reader.Available);
    }
}
