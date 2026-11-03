using GameServer.Modding;
using GameServer.Core.Diagnostics;

sealed class PlayerUpdatesState(IModuleLifetime rules, IPlayerUpdateSchedule schedule) : ITransientCombatState
{
    internal readonly IModuleLifetime Rules = rules;
    internal readonly IPlayerUpdateSchedule Schedule = schedule;
    internal readonly CancellationTokenSource Stop = new();
    internal long Generation;
    internal Task Completion = Task.CompletedTask;
    internal (uint Id, SpawnedMonster Monster)[] PendingStages = [];
    internal readonly HashSet<PlayerMinions.Attack> MinionAttacks = [];
    public void Clear() => Stop.Cancel();
}

static partial class Game
{
    sealed class PlayerUpdateSchedule : IPlayerUpdateSchedule
    {
        public double Now(ClientState conn) => conn.Clock.Elapsed.TotalSeconds;
        public double ResourceNow(ClientState conn) => ProjectileWorldTime(conn);
        public Task Wait(int milliseconds, CancellationToken stopping) => Task.Delay(milliseconds, stopping);
    }
    internal static readonly AsyncLocal<PlayerUpdatesState?> AreaUpdateContext = new();
    internal static bool CurrentGameWork => CurrentGameRules && (Program.ProjectileOutputGuard.Value?.Invoke() ?? true);
    internal static double AreaTime(ClientState conn) => AreaUpdateContext.Value?.Schedule.Now(conn) ?? conn.Clock.Elapsed.TotalSeconds;
    internal static Task AreaWait(PlayerUpdateRole role, int milliseconds) => AreaUpdateContext.Value is { } state
        ? state.Schedule.Wait(role, milliseconds, state.Stop.Token) : Task.Delay(milliseconds);

    internal static IPlayerUpdateSchedule PlayerUpdatesSchedule = new PlayerUpdateSchedule();

    internal static bool PlayerUpdatesCurrent(ClientState conn, PlayerUpdatesState state) => CurrentGameRules
        && !conn.Closed && !conn.RageRetired && !state.Stop.IsCancellationRequested
        && ReferenceEquals(conn.PlayerUpdatesState, state) && ReferenceEquals(CombatLifetime, state.Rules)
        && conn.PlayerUpdatesWorker.Current(state.Rules, null, state, state.Generation);

    internal static void EnsurePlayerUpdates(ClientState conn)
    {
        lock (conn.Monsters)
        {
            if (!CurrentGameRules || conn.Closed || conn.RageRetired || !conn.PlayerSent
                || conn.Wire.Stream is null || conn.Wire.Ciphers is null) return;
            if (conn.PlayerUpdatesState is not PlayerUpdatesState state || !PlayerUpdatesCurrent(conn, state))
            {
                conn.RetirePlayerUpdates();
                state = new(CombatLifetime!, PlayerUpdatesSchedule); conn.PlayerUpdatesState = state;
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

    static async Task RunPlayerUpdates(ClientState conn, PlayerUpdatesState state)
    {
        var previous = Program.ProjectileOutputGuard.Value;
        Program.ProjectileOutputGuard.Value = () => PlayerUpdatesCurrent(conn, state);
        AreaUpdateContext.Value = state;
        try
        {
            var (remote, stream, ciphers) = conn.Wire;
            await Task.WhenAll(Drive(() => RunPlayerTick(remote, stream, ciphers, conn, state)),
                Drive(() => RunResourceRecovery(remote, stream, ciphers, conn, state)),
                Drive(() => RunMonsterAi(remote, stream, ciphers, conn, state)),
                Drive(() => RunMonsterRegeneration(remote, stream, ciphers, conn, state)),
                Drive(() => RunLivingCollisions(remote, stream, ciphers, conn, state)),
                Drive(() => RunCharges(remote, stream, ciphers, conn, state)),
                Drive(() => RunFortification(remote, stream, ciphers, conn, state)),
                Drive(() => RunRage(remote, stream, ciphers, conn, state)),
                Drive(() => RunFlaskChargeRegen(remote, stream, ciphers, conn, state)),
                Drive(() => RunFlaskGrounds(remote, stream, ciphers, conn, state)),
                Drive(() => RunPlayerMinions(remote, stream, ciphers, conn, state)),
                Drive(() => RunFootstepSkills(remote, stream, ciphers, conn, state)),
                Drive(() => ResumeBossStages(remote, stream, ciphers, conn, state)),
                Drive(() => ContinueTour(remote, stream, ciphers, conn)));
        }
        catch (OperationCanceledException) when (state.Stop.IsCancellationRequested) { }
        catch (Exception ex) { Console.WriteLine($"[gateway] {conn.Wire.Remote} player updates failed: {ServerLog.Describe(ex)}"); throw; }
        finally
        {
            AreaUpdateContext.Value = null;
            Program.ProjectileOutputGuard.Value = previous;
            lock (conn.Monsters) conn.PlayerUpdatesWorker.Complete(state.Rules, null, state, state.Generation);
        }
        async Task Drive(Func<Task> loop)
        {
            try { await loop(); }
            catch { state.Stop.Cancel(); throw; }
        }
    }

    static async Task ResumeBossStages(string remote, System.Net.Sockets.NetworkStream stream,
        GameServer.Core.Protocol.Login.ConnectionCiphers ciphers, ClientState conn, PlayerUpdatesState state)
    {
        foreach (var (id, monster) in state.PendingStages)
        {
            while (true)
            {
                int wait;
                lock (conn.Monsters)
                {
                    if (!PlayerUpdatesCurrent(conn, state)) return;
                    if (!MonsterPresent(conn, id, monster) || !monster.StageRunning) break;
                    double left = monster.BusyUntil - state.Schedule.Now(conn);
                    wait = !conn.Paused && left <= 0 ? 0 : (int)Math.Clamp(Math.Ceiling(left * 1000), 16, 1000);
                }
                if (wait == 0) { await CompleteMonsterStage(remote, stream, ciphers, conn, id, monster); break; }
                await state.Schedule.Wait(PlayerUpdateRole.MonsterAction, wait, state.Stop.Token);
            }
        }
    }
}
