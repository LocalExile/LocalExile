using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    static void LifecycleSet(ClientState state, string name, int value) =>
        state.PlayerStats[Program.Stats.Key(name)!.Value] = value;

    static ClientState OlderStatObserver(GatewayCharacter game)
    {
        var bow = ItemCatalog.Create(Program.Items.Find("Short Bow")!) with
        {
            Sockets = [new(SocketColour.White,
                ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemFireball")!))],
        };
        uint host = game.Items.Add(PlayerInventories.Weapon1, 0, 0, bow).Id;
        var older = game.Reload();
        LifecycleSet(older, "base_intelligence", 100);
        older.Pools = Program.PoolsFor(older, 60);
        Assert.Contains(host, Program.InactiveItems[game.Character]);
        LifecycleSet(game.State, "base_intelligence", 100);
        LifecycleSet(game.State, "base_dexterity", 100);
        game.State.Pools = Program.PoolsFor(game.State, 60);
        Assert.DoesNotContain(host, Program.InactiveItems[game.Character]);
        Assert.Contains(ProgramValue<IReadOnlyList<GrantedSkill>>("PlayerSkills", game.State),
            s => s.Id == Program.GemSkillIdPool.Value[0]);
        return older;
    }

    static void EndStatLifecycle(ClientState state, string lifecycle)
    {
        lock (state.Monsters)
            switch (lifecycle)
            {
                case "closed": state.Closed = true; break;
                case "leaving": state.LeavingArea = true; break;
                case "retired": state.RageRetired = true; break;
                default: throw new ArgumentOutOfRangeException(nameof(lifecycle));
            }
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("leaving")]
    [InlineData("retired")]
    public void Character_stale_stat_calculations_cannot_publish_over_current_equipment_qualification(string lifecycle)
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(level: 60);
        var older = OlderStatObserver(game);
        var published = Program.InactiveItems[game.Character];
        EndStatLifecycle(older, lifecycle);
        for (int i = 0; i < 5; i++)
        {
            Program.CharacterPlayerStats(older);
            Assert.Same(published, Program.InactiveItems[game.Character]);
        }
        Assert.Contains(ProgramValue<IReadOnlyList<GrantedSkill>>("PlayerSkills", game.State),
            s => s.Id == Program.GemSkillIdPool.Value[0]);
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("leaving")]
    [InlineData("retired")]
    public async Task Character_queued_ES_refresh_checks_lifecycle_before_reading_sources(string lifecycle)
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(level: 60);
        var older = OlderStatObserver(game);
        var cache = older.StatSourcesCache;
        var published = Program.InactiveItems[game.Character];
        LifecycleSet(older, "base_dexterity", 1); // A source read would replace the connection-local cache.
        var result = await game.StatLifecycleExchange(async (stream, cipher) =>
        {
            var gate = Program.SendGates.GetValue(cipher, _ => new(1, 1));
            await gate.WaitAsync();
            var queued = ProgramValue<Task>("SendPlayerEnergyShield", "queued-ES-lifecycle", stream, cipher, older);
            try
            {
                Assert.False(queued.IsCompleted);
                EndStatLifecycle(older, lifecycle);
            }
            finally { gate.Release(); }
            await queued;
        });
        Assert.Empty(result.Bytes);
        Assert.Same(cache, older.StatSourcesCache);
        Assert.Same(published, Program.InactiveItems[game.Character]);
    }

    [Fact]
    public async Task Character_current_connection_still_publishes_ES_and_equipment_removal_and_restoration()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(level: 60);
        OlderStatObserver(game);
        var host = Assert.Single(game.Items.Items(PlayerInventories.Weapon1));
        LifecycleSet(game.State, "base_dexterity", 0);
        var result = await game.StatLifecycleExchange((stream, cipher) =>
            ProgramValue<Task>("SendPlayerEnergyShield", "current-ES-lifecycle", stream, cipher, game.State));
        Assert.NotEmpty(result.Bytes);
        Assert.Contains(host.Id, Program.InactiveItems[game.Character]);
        Assert.DoesNotContain(ProgramValue<IReadOnlyList<GrantedSkill>>("PlayerSkills", game.State),
            s => s.Id == Program.GemSkillIdPool.Value[0]);
        LifecycleSet(game.State, "base_dexterity", 100);
        Program.CharacterPlayerStats(game.State);
        Assert.DoesNotContain(host.Id, Program.InactiveItems[game.Character]);
        Assert.Contains(ProgramValue<IReadOnlyList<GrantedSkill>>("PlayerSkills", game.State),
            s => s.Id == Program.GemSkillIdPool.Value[0]);
    }

    [Fact]
    public async Task Character_retired_open_connection_resource_writer_finishes_without_changing_current_qualification_or_pools()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(level: 60);
        var older = OlderStatObserver(game);
        var published = Program.InactiveItems[game.Character];
        older.PlayerLife = 1;
        older.PlayerMana = 0;
        uint currentLife = game.State.PlayerLife, currentMana = game.State.PlayerMana;
        await game.StatLifecycleSession("RunResourceRecovery", older, async recovery =>
        {
            Assert.False(recovery.IsCompleted);
            // Use the actual successful-transfer retirement path, while the old socket remains open.
            older.LeavingArea = true;
            Program.CarryRage(older, (uint)Random.Shared.Next(1, int.MaxValue));
            Assert.True(older.RageRetired);
            await recovery.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(older.Closed);
            Assert.Equal(1u, older.PlayerLife);
            Assert.Equal(0u, older.PlayerMana);
            Assert.Equal(currentLife, game.State.PlayerLife);
            Assert.Equal(currentMana, game.State.PlayerMana);
            Assert.Same(published, Program.InactiveItems[game.Character]);
        });
    }

    [Fact]
    public async Task Character_temporarily_leaving_resource_writer_skips_loading_time_and_resumes_after_rollback()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(level: 60);
        LifecycleSet(game.State, "base_life_regeneration_rate_per_minute", 6000);
        game.State.PlayerLife = 1;
        game.State.LeavingArea = true;
        await game.StatLifecycleSession("RunResourceRecovery", game.State, async recovery =>
        {
            await Task.Delay(100);
            Assert.Equal(1u, game.State.PlayerLife);
            Assert.False(recovery.IsCompleted);
            var resumed = System.Diagnostics.Stopwatch.StartNew();
            game.State.ResumeAfterFailedTransfer();
            while (game.State.PlayerLife == 1 && resumed.Elapsed.TotalSeconds < 1) await Task.Delay(5);
            Assert.True(game.State.PlayerLife > 1);
            Assert.InRange(game.State.PlayerLife - 1, 1u, (uint)Math.Ceiling(resumed.Elapsed.TotalSeconds * 100) + 2);
            Assert.False(recovery.IsCompleted);
        });
    }

    [Fact]
    public void Character_loading_discards_accrued_recovery_but_retains_unexpired_sources()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(level: 60);
        var conn = game.State;
        var leech = Program.LeechOf(conn);
        double at = Program.ProjectileWorldTime(conn) + 1;
        foreach (var pool in new[] { leech.Life, leech.Mana, leech.EnergyShield })
        {
            pool.Configure(at, new(1, 1000, 1));
            pool.Grant(at, 100, 1000, 100);
        }
        var fort = Program.FortificationOf(conn);
        fort.State.GrantExplicitStacks(at, 10);
        fort.Observe(at, name => name == FortificationState.LifeRegenPerStack ? 600 : 0);
        fort.ConfigureRecovery(at, 1000, false);
        conn.Antiquarian.Stunned(at, 1000, 8);
        conn.EnduringCryRecovery.Begin(at, 2, 100);
        lock (conn.Monsters) Program.DiscardLoadingRecovery(conn, at + .5);
        Assert.Equal((0d, 0d, 0d), leech.TakeRecovery(at + .5));
        Assert.Equal(0, fort.TakeRecovery(at + .5));
        Assert.Equal(0, conn.Antiquarian.TakeRecovery(at + .5));
        Assert.Equal(0, conn.EnduringCryRecovery.Take(at + .5));
        // Discarding the loading interval must not delete sources or pay them twice after rollback.
        Assert.Equal((5d, 5d, 5d), leech.TakeRecovery(at + .75));
        Assert.Equal(250, fort.TakeRecovery(at + .75));
        Assert.Equal(20, conn.Antiquarian.TakeRecovery(at + .75));
        Assert.Equal(25, conn.EnduringCryRecovery.Take(at + .75));
    }

    [Fact]
    public async Task Character_active_leech_does_not_pay_loading_time_after_an_actual_refused_transfer()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(level: 60);
        var conn = game.State;
        conn.PlayerLife = 1;
        LifecycleSet(conn, "no_life_regeneration", 1);
        Program.CharacterPlayerStats(conn);
        double now = Program.ProjectileWorldTime(conn);
        Program.LeechOf(conn).Life.Grant(now, 100, conn.PlayerMaxLife, 100);
        var transfer = new PendingTransfer(0, 0, false);
        Assert.True(conn.TryBeginAreaChange(transfer, out _));
        await game.StatLifecycleExchange(async (stream, cipher) =>
        {
            var recovery = ProgramValue<Task>("RunResourceRecovery", "refused-transfer-leech", stream, cipher, conn);
            var resumed = new System.Diagnostics.Stopwatch();
            try
            {
                await Program.Transfer("refused-transfer-leech", stream, cipher, conn, transfer, async request =>
                {
                    Assert.Equal("transfer", request.Op);
                    await Task.Delay(350);
                    Assert.Equal(1u, conn.PlayerLife);
                    resumed.Start();
                    return new ControlResponse { Ok = false, Error = "fixture refusal" };
                });
                Assert.False(conn.LeavingArea);
                Assert.False(conn.RageRetired);
                while (conn.PlayerLife == 1 && resumed.Elapsed.TotalSeconds < 1) await Task.Delay(5);
                Assert.True(conn.PlayerLife > 1);
                Assert.InRange(conn.PlayerLife - 1, 1u,
                    (uint)Math.Ceiling(conn.PlayerMaxLife * .02 * resumed.Elapsed.TotalSeconds) + 2);
                Assert.True(Program.LeechOf(conn).Life.Active(Program.ProjectileWorldTime(conn)));
            }
            finally
            {
                conn.Closed = true;
                await recovery.WaitAsync(TimeSpan.FromSeconds(2));
            }
        });
    }

    [Fact]
    public void Character_critical_context_during_loading_retains_its_resume_baseline()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(level: 60);
        LifecycleSet(game.State, "has_focus", 1);
        var before = ProgramValue<(bool, bool, int)>("CriticalContext", game.State);
        Assert.True(before.Item2);
        game.State.LeavingArea = true;
        Assert.Equal(before, ProgramValue<(bool, bool, int)>("CriticalContext", game.State));
        game.State.ResumeAfterFailedTransfer();
        Assert.Equal(before, ProgramValue<(bool, bool, int)>("CriticalContext", game.State));
    }

    [Theory]
    [InlineData("RunPlayerTick")]
    [InlineData("RunCharges")]
    [InlineData("RunFortification")]
    [InlineData("RunFlaskChargeRegen")]
    [InlineData("RunUtilityFlasks")]
    [InlineData("RunFlaskGrounds")]
    [InlineData("SendRage")]
    public async Task Character_retired_continuous_stat_publishers_finish_before_the_old_socket_closes(string method)
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(level: 60);
        var older = OlderStatObserver(game);
        var published = Program.InactiveItems[game.Character];
        older.RageRetired = true;
        await game.StatLifecycleSession(method, older, async task =>
        {
            await task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(older.Closed);
            Assert.Same(published, Program.InactiveItems[game.Character]);
        });
    }

    [Fact]
    public async Task Character_player_tick_skips_temporarily_leaving_sources_until_retirement()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(level: 60);
        var older = OlderStatObserver(game);
        var published = Program.InactiveItems[game.Character];
        older.LeavingArea = true;
        await game.StatLifecycleSession("RunPlayerTick", older, async task =>
        {
            await Task.Delay(300);
            Assert.False(task.IsCompleted);
            Assert.Same(published, Program.InactiveItems[game.Character]);
            EndStatLifecycle(older, "retired");
            await task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(older.Closed);
        });
    }

    sealed partial class GatewayCharacter
    {
        public Task<(string? Error, byte[] Bytes)> StatLifecycleExchange(
            Func<System.Net.Sockets.NetworkStream, ConnectionCiphers, Task> inspect) => Exchange(async (stream, cipher) =>
            {
                await inspect(stream, cipher);
                return null;
            });

        public Task<(string? Error, byte[] Bytes)> StatLifecycleSession(string method, ClientState state,
            Func<Task, Task> inspect) => StatLifecycleExchange(async (stream, cipher) =>
            {
                object?[] args = ["continuous-stat-lifecycle", stream, cipher, state];
                if (method == "RunUtilityFlasks") args = [.. args, 0];
                var task = ProgramValue<Task>(method, args);
                try { await inspect(task); }
                finally
                {
                    state.Closed = true;
                    await task.WaitAsync(TimeSpan.FromSeconds(2));
                }
            });
    }
}
