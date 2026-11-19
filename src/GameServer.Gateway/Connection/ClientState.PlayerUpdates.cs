// Named roles share one generation claim, while retaining each loop's existing cadence.
public enum PlayerUpdateRole { Player, MonsterAction, Tour, Minions, Projectiles }
public interface IPlayerUpdateSchedule
{
    double Now(ClientState conn);
    double ResourceNow(ClientState conn) => Now(conn);
    Task Wait(int milliseconds, CancellationToken stopping);
    Task Wait(PlayerUpdateRole role, int milliseconds, CancellationToken stopping) => Wait(milliseconds, stopping);
}

public sealed partial class ClientState
{
    public ITransientCombatState? PlayerUpdatesState;
    public double FootstepRun;
    public readonly Dictionary<uint, double> TemporarySkillEffects = [];
    public ConnectionWorker PlayerUpdatesWorker { get; } = new("player-updates");
    /// <summary>The area's frames the player update loops wait on: the instance's, or this connection's own outside one.</summary>
    public AreaFrames Frames => Instance?.World.Frames ?? LazyInitializer.EnsureInitialized(ref ownFrames, () => new AreaFrames(() => Clock));
    AreaFrames? ownFrames;

    public void RetirePlayerUpdates()
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
public sealed class ConnectionProjectileRetirement(ClientState conn, IProjectileRuntime runtime) : IDisposable
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
