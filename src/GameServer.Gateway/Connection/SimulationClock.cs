using System.Diagnostics;

/// <summary>
/// Monotonic game-time clock for an area instance or player. At <see cref="Speed"/> == 1 it matches
/// <see cref="Stopwatch"/>; at other speeds (<c>/sim_speed</c> or <c>Game_SIM_SPEED</c>) game time advances at
/// <see cref="Speed"/>× wall-clock time while <see cref="Stop"/> / <see cref="Start"/> freeze and resume cleanly and
/// <see cref="SetSpeed"/> changes rate continuously without jumping elapsed time.
/// </summary>
public sealed class SimulationClock
{
    public const double MinSpeed = 0.1, MaxSpeed = 1000.0;

    static readonly Lock liveGate = new();
    static readonly List<WeakReference<SimulationClock>> live = [];

    readonly Lock gate = new();
    readonly Func<long> timestamp;
    readonly double ticksPerRawTick;
    double accumulatedSimTicks;
    long startedAt;
    double speed;
    bool running;

    public static double ClampSpeed(double value) =>
        double.IsFinite(value) && value > 0 ? Math.Clamp(value, MinSpeed, MaxSpeed) : 1.0;

    public double Speed
    {
        get { lock (gate) return speed; }
    }

    public SimulationClock(double speed = 0, Func<long>? timestamp = null, long? frequency = null, bool trackLive = true)
    {
        this.speed = speed > 0 && double.IsFinite(speed) ? ClampSpeed(speed) : Program.SimSpeed;
        this.timestamp = timestamp ?? Stopwatch.GetTimestamp;
        long freq = frequency is > 0 ? frequency.Value : Stopwatch.Frequency;
        ticksPerRawTick = (double)TimeSpan.TicksPerSecond / freq;
        if (trackLive)
        {
            lock (liveGate)
            {
                if (live.Count > 64) live.RemoveAll(w => !w.TryGetTarget(out _));
                live.Add(new WeakReference<SimulationClock>(this));
            }
        }
    }

    public static SimulationClock StartNew(double speed = 0)
    {
        var clock = new SimulationClock(speed);
        clock.Start();
        return clock;
    }

    /// <summary>Updates the rate of every live tracked clock continuously without jumping elapsed time.</summary>
    public static int SetAllLiveSpeed(double newSpeed)
    {
        double clamped = ClampSpeed(newSpeed);
        List<SimulationClock> targets;
        lock (liveGate)
        {
            live.RemoveAll(w => !w.TryGetTarget(out _));
            targets = new List<SimulationClock>(live.Count);
            foreach (var weak in live)
                if (weak.TryGetTarget(out var c)) targets.Add(c);
        }
        foreach (var c in targets) c.SetSpeed(clamped);
        return targets.Count;
    }

    public bool IsRunning
    {
        get { lock (gate) return running; }
    }

    public TimeSpan Elapsed
    {
        get
        {
            double total;
            lock (gate)
                total = running
                    ? accumulatedSimTicks + (timestamp() - startedAt) * ticksPerRawTick * speed
                    : accumulatedSimTicks;
            return total <= 0 ? TimeSpan.Zero : TimeSpan.FromTicks((long)total);
        }
    }

    public long ElapsedMilliseconds => (long)Elapsed.TotalMilliseconds;

    /// <summary>Changes the simulation rate continuously so <see cref="Elapsed"/> has no jump.</summary>
    public void SetSpeed(double newSpeed)
    {
        double clamped = ClampSpeed(newSpeed);
        lock (gate)
        {
            if (running)
            {
                long now = timestamp();
                accumulatedSimTicks += (now - startedAt) * ticksPerRawTick * speed;
                startedAt = now;
            }
            speed = clamped;
        }
    }

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
            accumulatedSimTicks += (timestamp() - startedAt) * ticksPerRawTick * speed;
            running = false;
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            accumulatedSimTicks = 0;
            startedAt = 0;
            running = false;
        }
    }

    public void Restart()
    {
        lock (gate)
        {
            accumulatedSimTicks = 0;
            startedAt = timestamp();
            running = true;
        }
    }
}
