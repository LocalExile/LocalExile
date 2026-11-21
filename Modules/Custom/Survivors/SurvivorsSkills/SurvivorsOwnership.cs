namespace GameServer.Modules.Survivors;

/// <summary>One run's authority over a connection. Captured before asynchronous work, never recovered afterward.</summary>
sealed class SurvivorsOwnership(Player player, SurvivorsRun run, CancellationTokenSource cancellation)
{
    internal SurvivorsRun Run => run;
    internal CancellationTokenSource Cancellation => cancellation;
    internal CancellationToken Token { get; } = cancellation.Token;
    internal bool Current => !Token.IsCancellationRequested && !player.Closed && !player.LeavingArea
        && ReferenceEquals(SurvivorsSkills.StateOf(player).Ownership, this)
        && ReferenceEquals(SurvivorsSkills.StateOf(player).Survivors, run);

    internal bool Commit(Action action)
    {
        if (!Current) return false;
        action();
        return true;
    }
}
