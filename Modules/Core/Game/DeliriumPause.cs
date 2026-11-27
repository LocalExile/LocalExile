static partial class Game
{
    static readonly Lock DeliriumPauseGate = new();
    static readonly List<Func<Player, bool>> DeliriumPauseList = [];
    static volatile Func<Player, bool>[] _deliriumPauseSources = [];

    /// <summary>Registers a lock-free predicate checked each Delirium mirror tick to pause the fog timer while another
    /// league encounter is active in the player's instance.</summary>
    public static IDisposable RegisterDeliriumPauseSource(Func<Player, bool> isPaused)
    {
        lock (DeliriumPauseGate)
        {
            DeliriumPauseList.Add(isPaused);
            _deliriumPauseSources = [.. DeliriumPauseList];
        }
        return new DeliriumPauseSubscription(isPaused);
    }

    /// <summary>True when any active league encounter in <paramref name="player"/>'s area pauses the Delirium mirror.</summary>
    public static bool IsDeliriumEncounterPaused(Player player)
    {
        foreach (var check in _deliriumPauseSources)
            if (check(player)) return true;
        return false;
    }

    sealed class DeliriumPauseSubscription(Func<Player, bool> check) : IDisposable
    {
        public void Dispose()
        {
            lock (DeliriumPauseGate)
            {
                if (DeliriumPauseList.Remove(check))
                    _deliriumPauseSources = [.. DeliriumPauseList];
            }
        }
    }
}
