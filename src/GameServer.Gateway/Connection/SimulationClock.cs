using System.Diagnostics;

/// <summary>
/// Monotonic game-time clock for an area instance or player. At <see cref="Speed"/> == 1 it matches
/// <see cref="Stopwatch"/>; at higher speeds (<c>Game_SIM_SPEED</c>) game time advances at
/// <see cref="Speed"/>× wall-clock time while <see cref="Stop"/> / <see cref="Start"/> freeze and resume it cleanly.
/// </summary>
public sealed class SimulationClock
{
    readonly Lock gate = new();
    readonly Func<long> timestamp;
    readonly double ticksPerRawTick;
    long accumulatedRaw;
    long startedAt;
    bool running;

    public double Speed { get; }

    public SimulationClock(double speed = 0, Func<long>? timestamp = null, long? frequency = null)
    {
        Speed = speed > 0 && double.IsFinite(speed) ? speed : Program.SimSpeed;
        this.timestamp = timestamp ?? Stopwatch.GetTimestamp;
        long freq = frequency is > 0 ? frequency.Value : Stopwatch.Frequency;
        ticksPerRawTick = (double)TimeSpan.TicksPerSecond / freq;
    }

    public static SimulationClock StartNew(double speed = 0)
    {
        var clock = new SimulationClock(speed);
        clock.Start();
        return clock;
    }

    public bool IsRunning
    {
        get { lock (gate) return running; }
    }

    public TimeSpan Elapsed
    {
        get
        {
            long raw;
            lock (gate)
                raw = running ? accumulatedRaw + (timestamp() - startedAt) : accumulatedRaw;
            if (raw <= 0) return TimeSpan.Zero;
            double scaledTicks = raw * ticksPerRawTick * (Speed == 1.0 ? 1.0 : Speed);
            return TimeSpan.FromTicks((long)scaledTicks);
        }
    }

    public long ElapsedMilliseconds => (long)Elapsed.TotalMilliseconds;

    public void Start()
    {
        lock (gate)
        {
            if (running) return;
            startedAt = timestamp();
            running = true;
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            if (!running) return;
            accumulatedRaw += timestamp() - startedAt;
            running = false;
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            accumulatedRaw = 0;
            startedAt = 0;
            running = false;
        }
    }

    public void Restart()
    {
        lock (gate)
        {
            accumulatedRaw = 0;
            startedAt = timestamp();
            running = true;
        }
    }
}
