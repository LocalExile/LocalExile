using GameServer.Modding;
using GameServer.Core.Diagnostics;

public sealed class PlayerUpdatesState(IModuleLifetime rules, IPlayerUpdateSchedule schedule) : ITransientCombatState
{
    public readonly IModuleLifetime Rules = rules;
    public readonly IPlayerUpdateSchedule Schedule = schedule;
    public readonly CancellationTokenSource Stop = new();
    public long Generation;
    public Task Completion = Task.CompletedTask;
    public (uint Id, SpawnedMonster Monster)[] PendingStages = [];
    public readonly HashSet<PlayerMinions.Attack> MinionAttacks = [];
    public void Clear() => Stop.Cancel();
}

public static partial class Game
{
    // The loops of a connection wait on its frames (AreaFrames): they wake at frame boundaries, one after another in
    // PlayerUpdateRole order, and read the boundary's time. The shared default (no connection) is the Clock.
    sealed class PlayerUpdateSchedule(Player? frames = null) : IPlayerUpdateSchedule
    {
        public double Now(Player conn) => frames is null ? conn.Clock.Elapsed.TotalSeconds : conn.Frames.Now(conn);
        public double ResourceNow(Player conn) => ProjectileWorldTime(conn);
        public Task Wait(int milliseconds, CancellationToken stopping) => Task.Delay(milliseconds, stopping);
        public Task Wait(PlayerUpdateRole role, int milliseconds, CancellationToken stopping) =>
            frames?.Frames.Wait(frames, role, milliseconds, stopping) ?? Task.Delay(milliseconds, stopping);
    }
    public static readonly AsyncLocal<PlayerUpdatesState?> AreaUpdateContext = new();
    public static bool CurrentGameWork => QuestKillCommitOutput is not null
        || CurrentGameRules && (Program.ProjectileOutputGuard.Value?.Invoke() ?? true);
    public static double AreaTime(Player conn) => AreaUpdateContext.Value?.Schedule.Now(conn) ?? conn.Clock.Elapsed.TotalSeconds;
    public static Task AreaWait(PlayerUpdateRole role, int milliseconds) => AreaUpdateContext.Value is { } state
        ? state.Schedule.Wait(role, milliseconds, state.Stop.Token) : Task.Delay(milliseconds);

    public static IPlayerUpdateSchedule PlayerUpdatesSchedule = new PlayerUpdateSchedule();

    public static bool PlayerUpdatesCurrent(Player conn, PlayerUpdatesState state) => CurrentGameRules
        && !conn.Closed && !conn.RageRetired && !state.Stop.IsCancellationRequested
        && ReferenceEquals(conn.PlayerUpdatesState, state) && ReferenceEquals(CombatLifetime, state.Rules)
        && conn.PlayerUpdatesWorker.Current(state.Rules, null, state, state.Generation);

    public static void EnsurePlayerUpdates(Player conn)
    {
        lock (conn.Monsters)
        {
            if (!CurrentGameRules || conn.Closed || conn.RageRetired || !conn.PlayerSent
                || !conn.Connected) return;
            if (conn.PlayerUpdatesState is not PlayerUpdatesState state || !PlayerUpdatesCurrent(conn, state))
            {
                conn.RetirePlayerUpdates();
                // Tests swap in a schedule of their own; the game's runs on the connection's frames.
                state = new(CombatLifetime!, PlayerUpdatesSchedule is PlayerUpdateSchedule ? new PlayerUpdateSchedule(conn) : PlayerUpdatesSchedule);
                conn.PlayerUpdatesState = state;
                state.PendingStages = [.. conn.Monsters.Where(p => p.Value.StageRunning).Select(p => (p.Key, p.Value))];
            }
            if (!conn.PlayerUpdatesWorker.TryStart(conn, state.Rules, null, state, out long generation)) return;
            state.Generation = generation;
            // Do not inherit an old projectile/worker's AsyncLocal output predicate into its replacement.
            if (ExecutionContext.IsFlowSuppressed()) state.Completion = Task.Run(() => RunPlayerUpdates(conn, state));
            else
                using (ExecutionContext.SuppressFlow()) state.Completion = Task.Run(() => RunPlayerUpdates(conn, state));
        }
    }

    static async Task RunPlayerUpdates(Player conn, PlayerUpdatesState state)
    {
        var previous = Program.ProjectileOutputGuard.Value;
        Program.ProjectileOutputGuard.Value = () => PlayerUpdatesCurrent(conn, state);
        AreaUpdateContext.Value = state;
        Program.ActingPlayer.Value = conn;
        try
        {
            StartResourceRecovery(conn, state);
            StartMonsterAi(conn, state);
            StartLivingCollisions(conn, state);
            StartContentTick(conn, state);
            StartFlaskChargeRegen(conn, state);
            RestoreFortification(conn);
            StartCharges(conn, state);
            StartPlayerMinions(conn, state);
            WakeFootsteps(conn);
            // The periodic work runs on the area's timers; the updates hold their claim until they stop, so that no second
            // start doubles the timers.
            await Task.WhenAll(Drive(PlayerUpdateRole.MonsterAction, () => ResumeBossStages(conn, state)),
                Drive(PlayerUpdateRole.Tour, () => ContinueTour(conn)),
                Task.Delay(Timeout.Infinite, state.Stop.Token));
        }
        catch (OperationCanceledException) when (state.Stop.IsCancellationRequested) { }
        catch (Exception ex) { Console.WriteLine($"[gateway] {conn.Remote} player updates failed: {ServerLog.Describe(ex)}"); throw; }
        finally
        {
            AreaUpdateContext.Value = null;
            Program.ProjectileOutputGuard.Value = previous;
            lock (conn.Monsters) conn.PlayerUpdatesWorker.Complete(state.Rules, null, state, state.Generation);
        }
        async Task Drive(PlayerUpdateRole role, Func<Task> loop)
        {
            try { await loop(); }
            catch { state.Stop.Cancel(); throw; }
            finally { conn.Frames.Ended(role); }
        }
    }

    static async Task ResumeBossStages(Player conn, PlayerUpdatesState state)
    {
        foreach (var (id, monster) in state.PendingStages)
            await RunBossStage(conn, id, monster);
    }
}
