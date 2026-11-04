// Named roles share one generation claim, while retaining each loop's existing cadence.
enum PlayerUpdateRole { Recovery, Player, MonsterAi, MonsterRegeneration, LivingCollisions, Charges, Fortification, Rage, FlaskCharges, FlaskGrounds, MonsterAction, Tour, Minions, Footsteps }
interface IPlayerUpdateSchedule
{
    double Now(ClientState conn);
    double ResourceNow(ClientState conn) => Now(conn);
    Task Wait(int milliseconds, CancellationToken stopping);
    Task Wait(PlayerUpdateRole role, int milliseconds, CancellationToken stopping) => Wait(milliseconds, stopping);
}

sealed partial class ClientState
{
    internal ITransientCombatState? PlayerUpdatesState;
    internal double FootstepRun;
    internal readonly Dictionary<uint, double> TemporarySkillEffects = [];
    internal ConnectionWorker PlayerUpdatesWorker { get; } = new("player-updates");

    internal void RetirePlayerUpdates()
    {
        lock (Monsters)
        {
            PlayerUpdatesWorker.Retire();
            PlayerUpdatesState?.Clear();
            PlayerUpdatesState = null;
        }
    }
}

// Host-owned identities, not a retained Game closure; removal cannot touch a replacement runtime.
sealed class ConnectionProjectileRetirement(ClientState conn, IProjectileRuntime runtime) : IDisposable
{
    public void Dispose()
    {
        lock (conn.Monsters)
        {
            runtime.Invalidate();
            if (ReferenceEquals(conn.ProjectileRuntimeState, runtime)) conn.ProjectileRuntimeState = null;
        }
    }
}
