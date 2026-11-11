namespace GameServer.Modules.Survivors;

/// <summary>One run's authority over a connection. Captured before asynchronous work, never recovered afterward.</summary>
sealed class SurvivorsOwnership(ClientState conn, SurvivorsRun run, CancellationTokenSource cancellation)
{
    internal SurvivorsRun Run => run;
    internal CancellationTokenSource Cancellation => cancellation;
    internal CancellationToken Token { get; } = cancellation.Token;
    internal bool Current => !Token.IsCancellationRequested && !conn.Closed && !conn.LeavingArea
        && ReferenceEquals(SurvivorsSkills.StateOf(conn).Ownership, this)
        && ReferenceEquals(SurvivorsSkills.StateOf(conn).Survivors, run);

    internal bool Commit(Action action)
    {
        lock (conn.Monsters)
        {
            if (!Current) return false;
            action();
            return true;
        }
    }
}
