using System.Runtime.CompilerServices;


/// <summary>
/// AI decisions use elapsed game time; a slow frame or pause never creates a backlog of decisions to replay.
/// Chase repaths have a separate budget so responsive attack/aggro checks do not keep restarting the same walk.
/// Access under the connection's Monsters lock.
/// </summary>
sealed class MonsterAiCadence
{
    public const int StepMilliseconds = 50;
    public const double StepSeconds = StepMilliseconds / 1000.0;
    public const double RepathSeconds = 0.1;
    public const int TargetChangeCells = 3;
    public const double RetrySeconds = 0.5;

    sealed class Schedule
    {
        public double NextChase = double.NegativeInfinity;
        public int? PendingSwing;
    }

    readonly ConditionalWeakTable<SpawnedMonster, Schedule> monsters = new();
    double nextStep = double.NegativeInfinity;
    bool running;

    // Avoid losing an entire decision step to the binary rounding of a deadline such as 0.1 + 0.2.
    public static bool Due(double now, double deadline) => now + 1e-9 >= deadline;

    public bool TryBeginStep(double now, bool active)
    {
        running = active;
        if (!active) { nextStep = now; return false; }
        if (!Due(now, nextStep)) return false;
        nextStep = now + StepSeconds;
        return true;
    }

    public int DelayMilliseconds(double now) => !running ? StepMilliseconds
        : (int)Math.Clamp(Math.Ceiling((nextStep - now) * 1000), 1, StepMilliseconds);

    public bool TryChase(SpawnedMonster monster, (int X, int Y) target, double now, bool retryAfterArrival = false)
    {
        var schedule = monsters.GetValue(monster, _ => new());
        if (!Due(now, schedule.NextChase)) return false;
        if (monster.LastTarget is { } previous
            && Math.Abs(previous.X - target.X) + Math.Abs(previous.Y - target.Y) < TargetChangeCells)
        {
            // The native path budget can end a walk short of its cell: a monster that has arrived may retry the same
            // target, at most every RetrySeconds; a target that only jittered keeps the current walk.
            if (!retryAfterArrival || !Due(now, monster.NextChaseRetryAt)) return false;
            monster.NextChaseRetryAt = now + RetrySeconds;
        }
        schedule.NextChase = now + RepathSeconds;
        return true;
    }

    public bool SwingPending(SpawnedMonster monster) =>
        monsters.TryGetValue(monster, out var schedule) && schedule.PendingSwing == monster.ActionGeneration;

    public void BeginSwing(SpawnedMonster monster, int action) =>
        monsters.GetValue(monster, _ => new()).PendingSwing = action;

    public void CompleteSwing(SpawnedMonster monster, int action)
    {
        if (monsters.TryGetValue(monster, out var schedule) && schedule.PendingSwing == action) schedule.PendingSwing = null;
    }
}
