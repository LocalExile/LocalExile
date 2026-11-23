/// <summary>
/// Something that ends at a frame, and may tick every <see cref="Interval"/> frames until then: a buff's duration, a
/// cooldown, a "recently" window, a burn's damage ticks. Frames, not seconds: an area's frames stop while it is paused or
/// empty, and so do its timers. <see cref="EndFrame"/> long.MaxValue is a timer that only ticks until cancelled.
/// </summary>
public sealed class AreaTimer
{
    public long StartFrame { get; internal set; }
    public long EndFrame { get; set; } = long.MaxValue;
    /// <summary>0: no ticks, only the end. Otherwise OnTick runs every Interval frames after the start, before the end.</summary>
    public int Interval { get; init; }
    public Action<AreaTimer>? OnTick { get; init; }
    public Action<AreaTimer>? OnEnd { get; init; }
    /// <summary>The entity it belongs to: when that entity is gone the timer lapses without running.</summary>
    public EntityId? Owner { get; init; }
    /// <summary>What made it (a module): dropped with it (AreaTimers.Drop).</summary>
    public object? Source { get; init; }
    public bool Cancelled { get; private set; }
    /// <summary>The ticks run so far.</summary>
    public long Ticks { get; private set; }

    internal long Due;
    public void Cancel() => Cancelled = true;
    public long FramesLeft(long frame) => Math.Max(0, EndFrame - frame);

    internal long NextDue()
    {
        if (Interval <= 0) return EndFrame;
        long tick = StartFrame + (Ticks + 1) * Interval;
        return Math.Min(tick, EndFrame);
    }

    internal void Ticked() => Ticks++;
}

/// <summary>
/// An area's timers: one collection, ordered by when each is next due, and one update a frame that runs only the ones due
/// (a timer that is not due costs nothing). A callback may add, cancel or change timers; one added for this frame or before
/// runs in the next frame's update.
/// </summary>
public sealed class AreaTimers(Entities entities)
{
    readonly PriorityQueue<AreaTimer, (long Due, long Order)> queue = new();
    long order;

    public int Count => queue.Count;

    /// <summary>Starts <paramref name="timer"/> at <paramref name="frame"/>.</summary>
    public AreaTimer Add(AreaTimer timer, long frame)
    {
        timer.StartFrame = frame;
        Enqueue(timer);
        return timer;
    }

    /// <summary>A timer that ends in <paramref name="frames"/> frames.</summary>
    public AreaTimer After(long frame, long frames, Action<AreaTimer> onEnd, EntityId? owner = null, object? source = null) =>
        Add(new AreaTimer { EndFrame = frame + frames, OnEnd = onEnd, Owner = owner, Source = source }, frame);

    /// <summary>A timer that ticks every <paramref name="interval"/> frames, for <paramref name="frames"/> frames (or until
    /// cancelled).</summary>
    public AreaTimer Every(long frame, int interval, Action<AreaTimer> onTick, long frames = long.MaxValue, Action<AreaTimer>? onEnd = null,
        EntityId? owner = null, object? source = null) =>
        Add(new AreaTimer { Interval = Math.Max(1, interval), EndFrame = frames == long.MaxValue ? long.MaxValue : frame + frames,
            OnTick = onTick, OnEnd = onEnd, Owner = owner, Source = source }, frame);

    void Enqueue(AreaTimer timer)
    {
        timer.Due = timer.NextDue();
        queue.Enqueue(timer, (timer.Due, order++));
    }

    /// <summary>Runs every timer due at or before <paramref name="frame"/>, in due order (then in the order they were added).</summary>
    public void Update(long frame)
    {
        while (queue.TryPeek(out var timer, out var at) && at.Due <= frame)
        {
            queue.Dequeue();
            if (timer.Cancelled || timer.Owner is { } owner && !entities.Exists(owner)) continue;
            // Changed since it was queued (EndFrame moved): it goes back at its new place.
            if (timer.NextDue() != timer.Due) { Enqueue(timer); continue; }
            if (timer.Due >= timer.EndFrame)
            {
                timer.OnEnd?.Invoke(timer);
                continue;
            }
            timer.Ticked();
            timer.OnTick?.Invoke(timer);
            if (!timer.Cancelled) Enqueue(timer);
        }
    }

    /// <summary>Cancels every timer <paramref name="source"/> made (a module unloading).</summary>
    public void Drop(object source)
    {
        foreach (var (timer, _) in queue.UnorderedItems)
            if (ReferenceEquals(timer.Source, source)) timer.Cancel();
    }
}
