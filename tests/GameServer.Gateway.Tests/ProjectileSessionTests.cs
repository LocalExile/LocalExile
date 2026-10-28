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

    [Fact]
    public async Task Catchup_hits_commit_Rage_on_simulation_time_before_later_releases_with_the_send_gate_blocked()
    {
        using var wire = new Wire(); var conn = Connection();
        Set(conn, "gain_x_rage_on_hit", 5); Set(conn, "no_inherent_rage_loss", 1);
        var target = new SpawnedMonster("target", new(50, 10, 10, width: 300, height: 300), 0, 2, 100);
        conn.Monsters.Add(42, target);
        var session = new Program.ProjectileSession("rage-catchup", wire.Writer.GetStream(), wire.Ciphers, conn);
        conn.ProjectileRuntime = session;
        await wire.Gate.WaitAsync();
        try
        {
            Assert.True(SpinWait.SpinUntil(() => conn.Clock.Elapsed.TotalSeconds > .65, TimeSpan.FromSeconds(2)));
            _ = Program.RagePresentation(conn); // A panel refresh cannot settle past unprocessed hit history.
            var gainsSeenAtRelease = new List<int>();
            foreach (double at in new[] { 0d, .6 })
                session.Schedule(ProjectileRuntime.Time(at), () => true, () =>
                {
                    gainsSeenAtRelease.Add(Program.CharacterPlayerStats(conn)[Program.Stats.Key("current_rage")!.Value]);
                    return Shot(conn);
                });
            session.AdvanceTo(ProjectileRuntime.Time(1));
            Assert.Equal(new[] { 0, 5 }, gainsSeenAtRelease);
            Assert.Equal(10, Program.CharacterPlayerStats(conn)[Program.Stats.Key("current_rage")!.Value]);
            Assert.Equal(80u, target.Life);
            Assert.Equal(0, wire.Reader.Available);
        }
        finally
        {
            conn.Closed = true; session.Invalidate(); wire.Gate.Release();
            await session.OutputCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Skill_own_luck_and_prohibition_join_live_owner_conditions_at_projectile_contact(bool prohibited)
    {
        var random = new CriticalDraws();
        using var wire = new Wire(); var conn = Connection(random);
        conn.Pools = conn.Pools with { Life = 100 }; conn.PlayerLife = 100;
        var own = new Dictionary<uint, int>
        {
            [Program.Stats.Key("extra_critical_rolls_while_on_low_life")!.Value] = 1,
            [Program.Stats.Key("global_cannot_crit")!.Value] = prohibited ? 1 : 0,
            [Program.Stats.Key("gain_x_rage_on_hit")!.Value] = 3
        };
        Set(conn, "no_inherent_rage_loss", 1);
        var target = new SpawnedMonster("target", new(50, 10, 10, width: 300, height: 300), 0, 2, 100);
        conn.Monsters.Add(42, target);
        var session = new Program.ProjectileSession("skill-policy", wire.Writer.GetStream(), wire.Ciphers, conn);
        conn.ProjectileRuntime = session;
        try
        {
            session.Schedule(0, () => true, () => Shot(conn) with
            { Payload = new ProjectileOffense(DamagePacket.Of(DamageType.Fire, 10, 10), true, 0, 5000, 150, "own")
                { SkillStats = own } });
            session.AdvanceTo(0);
            conn.PlayerLife = 50;
            session.AdvanceTo(4000);
            Assert.Equal(prohibited ? 0 : 2, random.Calls);
            Assert.Equal(prohibited ? 90u : 85u, target.Life);
            Assert.Equal(3, Program.CharacterPlayerStats(conn)[Program.Stats.Key("current_rage")!.Value]);
        }
        finally
        {
            conn.Closed = true; session.Invalidate();
            await session.OutputCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    sealed class CriticalDraws : Random
    {
        public int Calls;
        public override int Next(int maxValue) => Calls++ == 0 ? 9000 : 0;
    }

    [Theory]
    [InlineData("bow", false)]
    [InlineData("bow", true)]
    [InlineData("cooldown", false)]
    [InlineData("cooldown", true)]
    public void Old_owners_cannot_reserve_ground_attack_or_cooldown_side_effects(string route, bool retired)
    {
        var conn = Connection(); conn.RageRetired = retired; conn.LeavingArea = !retired;
        conn.ChannelSkill = 123;
        bool accepted = route == "bow"
            ? Program.TryBeginBowAttack(conn, 0, 1, 0x102, 7, out _)
            : Program.TryBeginCooldownUse(conn, 10619, new(2500, 100), false,
                () => { conn.ActionCount++; conn.ChannelSkill = 456; }, out _);
        Assert.False(accepted);
        Assert.Equal(7, conn.ActionCount); Assert.Equal((ushort)123, conn.ChannelSkill);
        Assert.Equal(0, conn.NextAttackAt); Assert.Empty(conn.SingleUseCooldowns);
    }

    [Theory]
    [InlineData("death", 90u)]
    [InlineData("retire", 100u)]
    [InlineData("transfer", 100u)]
    [InlineData("disconnect", 100u)]
    public async Task Released_flights_survive_owner_death_but_cannot_grant_Rage_or_cross_owner_retirement(
        string invalidation, uint expectedLife)
    {
        using var wire = new Wire(); var conn = Connection(); Set(conn, "gain_x_rage_on_hit", 5);
        var target = new SpawnedMonster("target", new(50, 10, 10, width: 300, height: 300), 0, 2, 100);
        conn.Monsters.Add(42, target);
        var session = new Program.ProjectileSession("owner-lifecycle", wire.Writer.GetStream(), wire.Ciphers, conn);
        conn.ProjectileRuntime = session;
        try
        {
            session.Schedule(0, () => true, () => Shot(conn)); session.AdvanceTo(0);
            if (invalidation == "death") { conn.PlayerLife = 0; Program.ClearRageOnDeath(conn); }
            if (invalidation == "retire") Program.CarryRage(conn, (uint)Random.Shared.Next(1, int.MaxValue));
            if (invalidation == "transfer") conn.TryBeginAreaChange(new(2, 0, false), out _);
            if (invalidation == "disconnect") conn.Closed = true;
            session.AdvanceTo(4000);
            Assert.Equal(expectedLife, target.Life);
            Assert.Equal(0, Program.CharacterPlayerStats(conn)[Program.Stats.Key("current_rage")!.Value]);
        }
        finally
        {
            conn.Closed = true; session.Invalidate();
            await session.OutputCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task A_queued_projectile_skill_refresh_cannot_write_the_saved_bar_after_owner_retirement()
    {
        using var wire = new Wire(); var conn = Connection();
        var session = new Program.ProjectileSession("retired-skills", wire.Writer.GetStream(), wire.Ciphers, conn);
        conn.ProjectileRuntime = session;
        await wire.Gate.WaitAsync();
        var refreshing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? refresh = null;
        session.Enqueue(() =>
        {
            refresh = (Task)typeof(Program).GetMethod("SendSkills",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, ["retired-skills", wire.Writer.GetStream(), wire.Ciphers, conn])!;
            refreshing.SetResult();
            return refresh;
        });
        try
        {
            await refreshing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(refresh!.IsCompleted);
            Assert.Empty(CharacterSaves.Load(conn.Character!)?.SkillBar ?? []);
            Program.CarryRage(conn, (uint)Random.Shared.Next(1, int.MaxValue));
            session.Invalidate(); wire.Gate.Release();
            await session.OutputCompleted.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(CharacterSaves.Load(conn.Character!)?.SkillBar ?? []);
        }
        finally
        {
            conn.Closed = true; session.Invalidate();
            if (wire.Gate.CurrentCount == 0) wire.Gate.Release();
            await session.OutputCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Damage_commits_while_the_actual_socket_send_gate_is_held()
    {
        using var wire = new Wire(); var conn = Connection();
        var target = new SpawnedMonster("target", new(50, 10, 10, width: 300, height: 300), 0, 2, 100);
        conn.Monsters.Add(42, target);
        await wire.Gate.WaitAsync();
        var session = new Program.ProjectileSession("test", wire.Writer.GetStream(), wire.Ciphers, conn);
        session.Schedule(0, () => true, () => Shot(conn, 3));
        session.AdvanceTo(4000);
        Assert.Equal(90u, target.Life);
        Assert.Equal(0, wire.Reader.Available);
        conn.Closed = true; session.Invalidate(); wire.Gate.Release();
        await session.OutputCompleted.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Flight_that_ends_entirely_behind_the_send_gate_is_not_replayed_later()
    {
        using var wire = new Wire(); var conn = Connection();
        await wire.Gate.WaitAsync();
        var session = new Program.ProjectileSession("late-test", wire.Writer.GetStream(), wire.Ciphers, conn);
        session.Schedule(0, () => true, () => Shot(conn));
        session.AdvanceTo(8000);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Enqueue(() => { drained.SetResult(); return Task.CompletedTask; });
        wire.Gate.Release();
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, wire.Reader.Available);
        conn.Closed = true; session.Invalidate();
        await session.OutputCompleted.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_cancelled_windup_never_releases_but_a_released_volley_survives_new_actions()
    {
        using var wire = new Wire(); var conn = Connection();
        var target = new SpawnedMonster("target", new(50, 10, 10, width: 300, height: 300), 0, 2, 100);
        conn.Monsters.Add(42, target);
        var session = new Program.ProjectileSession("test", wire.Writer.GetStream(), wire.Ciphers, conn);
        session.Schedule(1000, () => conn.ActionCount == 7, () => Shot(conn));
        conn.ActionCount++;
        session.AdvanceTo(1000);
        Assert.Empty(session.Runtime.Projectiles);
        session.Schedule(1000, () => true, () => Shot(conn));
        session.AdvanceTo(1000);
        conn.ActionCount++;
        session.AdvanceTo(4000);
        Assert.Equal(90u, target.Life);
        conn.Closed = true; session.Invalidate();
        await session.OutputCompleted.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Transfer_discards_old_epoch_work_and_pending_damage()
    {
        using var wire = new Wire(); var conn = Connection();
        var target = new SpawnedMonster("target", new(50, 10, 10, width: 300, height: 300), 0, 2, 100);
        conn.Monsters.Add(42, target);
        var session = new Program.ProjectileSession("test", wire.Writer.GetStream(), wire.Ciphers, conn);
        conn.ProjectileRuntime = session;
        session.Schedule(0, () => true, () => Shot(conn));
        Assert.True(SpinWait.SpinUntil(() => conn.Clock.Elapsed.TotalSeconds > .05, TimeSpan.FromSeconds(1)));
        conn.TryBeginAreaChange(new PendingTransfer(2, 0, false), out _);
        session.AdvanceTo(4000);
        Assert.Equal(100u, target.Life);
        Assert.Empty(session.Runtime.Projectiles);
        await session.OutputCompleted.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Actual_instance_chooser_ingress_invalidates_overdue_flights_without_catching_up_damage()
    {
        using var wire = new Wire(); var conn = Connection();
        var target = new SpawnedMonster("target", new(50, 10, 10, width: 300, height: 300), 0, 2, 100);
        conn.Monsters.Add(42, target); conn.Chooser = new(2, [1UL]);
        var session = new Program.ProjectileSession("chooser", wire.Writer.GetStream(), wire.Ciphers, conn);
        conn.ProjectileRuntime = session;
        session.Schedule(0, () => true, () => Shot(conn));
        Assert.True(SpinWait.SpinUntil(() => conn.Clock.Elapsed.TotalSeconds > .05, TimeSpan.FromSeconds(1)));
        await wire.Gate.WaitAsync();
        var dispatch = Program.OnClientMessages("chooser", wire.Writer.GetStream(), wire.Ciphers,
            [(InstanceTransfer.ChooseInstanceId, new byte[] { 0, 0xaf, 0 })], [], conn);
        try
        {
            Assert.True(conn.LeavingArea);
            Assert.Equal(100u, target.Life);
            Assert.Empty(session.Runtime.Projectiles);
        }
        finally
        {
            conn.Closed = true; conn.PendingTransfer = null; wire.Gate.Release();
            await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
            await session.OutputCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Recovery_cancels_windup_at_the_hit_boundary_while_output_is_blocked()
    {
        using var wire = new Wire(); var conn = Connection();
        var target = new SpawnedMonster("target", new(50, 10, 10, width: 300, height: 300), 0, 2, 100);
        conn.Monsters.Add(42, target);
        var session = new Program.ProjectileSession("recovery", wire.Writer.GetStream(), wire.Ciphers, conn);
        conn.ProjectileRuntime = session;
        await wire.Gate.WaitAsync();
        session.Schedule(120000, () => conn.ActionCount == 7, () => Shot(conn));
        using (Program.ProjectileMutation(conn))
            Assert.NotNull(Program.CommitPlayerHitRecoveryLocked(conn, 1000, Program.ProjectileWorldTime(conn)));
        Assert.True(conn.HitRecovery.Active(Program.ProjectileWorldTime(conn)));
        Assert.Equal(8, conn.ActionCount);
        session.AdvanceTo(Math.Max(124000, session.Runtime.Now));
        Assert.Equal(100u, target.Life);
        Assert.Empty(session.Runtime.Projectiles);
        conn.Closed = true; session.Invalidate(); wire.Gate.Release();
        await session.OutputCompleted.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transfer_cleans_active_and_ended_native_objects_even_when_removal_is_queued(bool ended)
    {
        using var wire = new Wire(); var conn = Connection();
        await wire.Gate.WaitAsync();
        var session = new Program.ProjectileSession("transfer-visual", wire.Writer.GetStream(), wire.Ciphers, conn);
        conn.ProjectileRuntime = session;
        session.Schedule(0, () => true, () => Shot(conn)); session.AdvanceTo(0);
        uint id = Assert.Single(conn.ProjectileObjects);
        if (ended) session.AdvanceTo(8000);
        Assert.True(conn.TryBeginAreaChange(new PendingTransfer(2, 0, false), out var cleanup));
        Assert.Contains(id, cleanup);
        wire.Gate.Release();
        await session.OutputCompleted.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Boss_stage_state_and_refill_do_not_wait_for_hit_notifications()
    {
        using var wire = new Wire(); var conn = Connection();
        var target = new SpawnedMonster("boss", new(50, 10, 10, width: 300, height: 300), 0, 2, 100)
        { Stage = new(95, 2612, .1, 2), BusyUntil = 0 };
        conn.Monsters.Add(42, target);
        await wire.Gate.WaitAsync();
        var session = new Program.ProjectileSession("test", wire.Writer.GetStream(), wire.Ciphers, conn);
        session.Schedule(0, () => true, () => Shot(conn));
        session.AdvanceTo(4000);
        Assert.True(target.StageRunning);
        Assert.True(target.BusyUntil > .1);
        session.AdvanceTo(20000);
        Assert.False(target.StageRunning);
        Assert.Equal(100u, target.Life);
        conn.Closed = true; session.Invalidate(); wire.Gate.Release();
        await session.OutputCompleted.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Real_pause_barrier_freezes_the_committed_horizon_and_late_input_cannot_rewind_it()
    {
        using var wire = new Wire(); var conn = Connection();
        var session = new Program.ProjectileSession("pause-test", wire.Writer.GetStream(), wire.Ciphers, conn);
        conn.ProjectileRuntime = session;
        session.Schedule(0, () => true, () => Shot(conn) with { Profile = Shot(conn).Profile with { Speed = 1 } });
        await wire.Gate.WaitAsync();
        var paused = Program.OnClientMessages("pause-test", wire.Writer.GetStream(), wire.Ciphers,
            [(InstancePause.RequestId, new byte[] { 0x01, 0xd4, 1 })], [], conn);
        Assert.True(conn.Paused);
        long horizon = session.Runtime.Now;
        session.Advance();
        Assert.Equal(horizon, session.Runtime.Now);
        Assert.True(conn.ClockAt(1) >= horizon / (double)ProjectileRuntime.QuantaPerSecond);
        conn.Closed = true; session.Invalidate(); wire.Gate.Release();
        await paused.WaitAsync(TimeSpan.FromSeconds(5));
        await session.OutputCompleted.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Actor_relocation_drains_prior_contacts_atomically_before_replacing_the_walk()
    {
        using var wire = new Wire(); var conn = Connection();
        var target = new SpawnedMonster("target", new(30, 10, 10, width: 300, height: 300), 0, 2, 100);
        conn.Monsters.Add(42, target);
        var session = new Program.ProjectileSession("mutation", wire.Writer.GetStream(), wire.Ciphers, conn);
        conn.ProjectileRuntime = session;
        conn.Clock.Stop(); conn.Clock.Reset(); conn.Clock.Start();
        session.Schedule(0, () => true, () => Shot(conn) with { Profile = Shot(conn).Profile with { Speed = 30000000 } });
        // The first contact is due after one quantum. Readiness is a clock condition, not a timing assertion.
        Assert.True(SpinWait.SpinUntil(() => conn.Clock.Elapsed.TotalSeconds > .001, TimeSpan.FromSeconds(1)));
        using (Program.ProjectileMutation(conn))
            target.Walk.Teleport(30, 100, conn.Clock.Elapsed.TotalSeconds);
        Assert.Equal(90u, target.Life);
        session.AdvanceTo(session.Runtime.Now + 4000);
        Assert.Equal(90u, target.Life);
        conn.Closed = true; session.Invalidate();
        await session.OutputCompleted.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Lobbed_attack_landings_roll_accuracy_rather_than_bypassing_evasion()
    {
        using var wire = new Wire(); var conn = Connection();
        var target = new SpawnedMonster("target", new(50, 10, 10, width: 300, height: 300), 0, 2, 1000) { Level = 83 };
        conn.Monsters.Add(42, target);
        var session = new Program.ProjectileSession("landing", wire.Writer.GetStream(), wire.Ciphers, conn);
        for (int i = 0; i < 20; i++)
            session.Schedule(0, () => true, () => Shot(conn) with
            {
                Profile = Shot(conn).Profile with { Lobbed = true, EndArea = new("landing", 5) }, Aim = new(50, 10),
                Payload = new ProjectileOffense(DamagePacket.Of(DamageType.Physical, 10, 10), false, 0, 0, 150, "landing")
            });
        session.AdvanceTo(4000);
        Assert.True(target.Life > 800); // Twenty unevaded hits would deal exactly 200.
        conn.Closed = true; session.Invalidate();
        await session.OutputCompleted.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_lethal_hit_commits_quest_flags_experience_and_level_pools_while_output_is_blocked()
    {
        using var wire = new Wire();
        var conn = new ClientState(new AreaLayout(1, TutorialObjects.Strand, 300, 300, 10, 10, null, []), 1, 1)
            { Character = $"ProjectileKill{Guid.NewGuid():N}", Instance = new InstanceState(), PlayerLife = 100 };
        var target = new SpawnedMonster("Hillock", new(50, 10, 10, width: 300, height: 300), 0, 2, 1)
            { Variety = Program.Monsters.Find("Metadata/Monsters/ZombieBoss/ZombieBossHillockNormal")! };
        conn.Monsters.Add(42, target);
        await wire.Gate.WaitAsync();
        var session = new Program.ProjectileSession("kill-test", wire.Writer.GetStream(), wire.Ciphers, conn);
        try
        {
            session.Schedule(0, () => true, () => Shot(conn));
            session.AdvanceTo(4000);
            Assert.True(target.Dead);
            Assert.Contains("a1q1-KilledHillock", conn.QuestFlagsSnapshot());
            Assert.True(conn.Experience > 0);
            Assert.Equal(2, conn.Pools.Level);
            Assert.Equal(conn.PlayerMaxLife, conn.PlayerLife);
            Assert.Equal(0, wire.Reader.Available);
        }
        finally
        {
            conn.Closed = true; session.Invalidate(); wire.Gate.Release();
            await session.OutputCompleted.WaitAsync(TimeSpan.FromSeconds(5));
            PlayerInventories.Forget(conn.Character!);
        }
    }}
