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
        public double Now(Player player) => frames is null ? player.Clock.Elapsed.TotalSeconds : player.Frames.Now(player);
        public double ResourceNow(Player player) => ProjectileWorldTime(player);
        public Task Wait(int milliseconds, CancellationToken stopping) => Task.Delay(milliseconds, stopping);
        public Task Wait(PlayerUpdateRole role, int milliseconds, CancellationToken stopping) =>
            frames?.Frames.Wait(frames, role, milliseconds, stopping) ?? Task.Delay(milliseconds, stopping);
    }
    public static readonly AsyncLocal<PlayerUpdatesState?> AreaUpdateContext = new();
    public static bool CurrentGameWork => QuestKillCommitOutput is not null
        || CurrentGameRules && (Program.ProjectileOutputGuard.Value?.Invoke() ?? true);
    /// <summary>The rules' time: the area's frame time (AreaFrames.Now), the same for everything in a frame.</summary>
    public static double AreaTime(Player player) => AreaUpdateContext.Value?.Schedule.Now(player) ?? player.Frames.Now(player);
    /// <summary>The rules' randomness: the area's seeded Random (AreaFrames.Rng).</summary>
    public static Random AreaRng(Player player) => player.Frames.Rng;
    public static Task AreaWait(PlayerUpdateRole role, int milliseconds) => AreaUpdateContext.Value is { } state
        ? state.Schedule.Wait(role, milliseconds, state.Stop.Token) : Task.Delay(milliseconds);

    public static IPlayerUpdateSchedule PlayerUpdatesSchedule = new PlayerUpdateSchedule();

    public static bool PlayerUpdatesCurrent(Player player, PlayerUpdatesState state) => CurrentGameRules
        && !player.Closed && !player.RageRetired && !state.Stop.IsCancellationRequested
        && ReferenceEquals(player.PlayerUpdatesState, state) && ReferenceEquals(CombatLifetime, state.Rules)
        && player.PlayerUpdatesWorker.Current(state.Rules, null, state, state.Generation);

    public static void EnsurePlayerUpdates(Player player)
    {
        if (!CurrentGameRules || player.Closed || player.RageRetired || !player.Connection.PlayerSent
            || !player.Connection.Connected) return;
        if (player.PlayerUpdatesState is not PlayerUpdatesState state || !PlayerUpdatesCurrent(player, state))
        {
            player.RetirePlayerUpdates();
            // Tests swap in a schedule of their own; the game's runs on the connection's frames.
            state = new(CombatLifetime!, PlayerUpdatesSchedule is PlayerUpdateSchedule ? new PlayerUpdateSchedule(player) : PlayerUpdatesSchedule);
            player.PlayerUpdatesState = state;
            state.PendingStages = [.. player.Area.Monsters.Where(p => p.Value.StageRunning).Select(p => (p.Key, p.Value))];
        }
        if (!player.PlayerUpdatesWorker.TryStart(player, state.Rules, null, state, out long generation)) return;
        state.Generation = generation;
        // Do not inherit an old projectile/worker's AsyncLocal output predicate into its replacement.
        if (ExecutionContext.IsFlowSuppressed()) state.Completion = AreaFrames.Start(() => RunPlayerUpdates(player, state));
        else
            using (ExecutionContext.SuppressFlow()) state.Completion = AreaFrames.Start(() => RunPlayerUpdates(player, state));
    }

    static async Task RunPlayerUpdates(Player player, PlayerUpdatesState state)
    {
        var previous = Program.ProjectileOutputGuard.Value;
        Program.ProjectileOutputGuard.Value = () => PlayerUpdatesCurrent(player, state);
        AreaUpdateContext.Value = state;
        Program.ActingPlayer.Value = player;
        try
        {
            StartResourceRecovery(player, state);
            StartMonsterAi(player, state);
            StartLivingCollisions(player, state);
            StartContentTick(player, state);
            StartFlaskChargeRegen(player, state);
            RestoreFortification(player);
            StartCharges(player, state);
            StartPlayerMinions(player, state);
            WakeFootsteps(player);
            // The periodic work runs on the area's timers; the updates hold their claim until they stop, so that no second
            // start doubles the timers.
            await Task.WhenAll(Drive(PlayerUpdateRole.MonsterAction, () => ResumeBossStages(player, state)),
                Drive(PlayerUpdateRole.Tour, () => ContinueTour(player)),
                Task.Delay(Timeout.Infinite, state.Stop.Token));
        }
        catch (OperationCanceledException) when (state.Stop.IsCancellationRequested) { }
        catch (Exception ex) { Console.WriteLine($"[gateway] {player.Connection.Remote} player updates failed: {ServerLog.Describe(ex)}"); throw; }
        finally
        {
            AreaUpdateContext.Value = null;
            Program.ProjectileOutputGuard.Value = previous;
            player.PlayerUpdatesWorker.Complete(state.Rules, null, state, state.Generation);
        }
        async Task Drive(PlayerUpdateRole role, Func<Task> loop)
        {
            try { await loop(); }
            catch { state.Stop.Cancel(); throw; }
            finally { player.Frames.Ended(role); }
        }
    }

    static async Task ResumeBossStages(Player player, PlayerUpdatesState state)
    {
        foreach (var (id, monster) in state.PendingStages)
            await RunBossStage(player, id, monster);
    }
}
