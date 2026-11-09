namespace GameServer.Core.World;

/// <summary>A spell's cancellation windows, independent of when a held spell may repeat.</summary>
public readonly record struct LockstepCastWindow(double Start, double Duration, double? EarlyUntil, double LateFrom,
    ushort? ActionHash = null)
{
    // Different spell actions can replace a cast only in its early window (0x141e8d900).
    public bool AllowsSkill(double at, ushort? nextAction) => ActionHash is ushort current && nextAction is ushort next
        && current != next && at >= Start && EarlyUntil is double early && at - Start <= early;

    public bool AllowsMove(double at, bool earlyCancellation = true)
    {
        double elapsed = at - Start;
        return elapsed >= 0 && (earlyCancellation && EarlyUntil is double early && elapsed <= early || elapsed >= LateFrom);
    }
}
