namespace GameServer.Core.World;

/// <summary>A spell's movement-cancel window, independent of when a held spell may repeat.</summary>
public readonly record struct LockstepCastWindow(double Start, double Duration, double? EarlyUntil, double LateFrom)
{
    public bool AllowsMove(double at, bool earlyCancellation = true)
    {
        double elapsed = at - Start;
        return elapsed >= 0 && (earlyCancellation && EarlyUntil is double early && elapsed <= early || elapsed >= LateFrom);
    }
}
