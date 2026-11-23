// Named roles share one generation claim, while retaining each loop's existing cadence.
public enum PlayerUpdateRole { Player, MonsterAction, Tour, Minions, Projectiles }
public interface IPlayerUpdateSchedule
{
    double Now(Player player);
    double ResourceNow(Player player) => Now(player);
    Task Wait(int milliseconds, CancellationToken stopping);
    Task Wait(PlayerUpdateRole role, int milliseconds, CancellationToken stopping) => Wait(milliseconds, stopping);
}

public sealed partial class Player
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
        PlayerUpdatesWorker.Retire();
        PlayerUpdatesState?.Clear();
        PlayerUpdatesState = null;
    }
}

// Host-owned identities, not a retained Game closure; removal cannot touch a replacement runtime.
public sealed class ConnectionProjectileRetirement(Player player, IProjectileRuntime runtime) : IDisposable
{
    public void Dispose()
    {
        runtime.Invalidate();
        if (ReferenceEquals(player.ProjectileRuntimeState, runtime)) player.ProjectileRuntimeState = null;
    }
}
