namespace GameServer.Core.World;

public sealed partial class PlayerWalk
{
    sealed class PositionSample(double time)
    {
        public double Time { get; } = time;
        public (int X, int Y)? Cell { get; set; }
    }

    // Scheduled contacts can be serviced after a new walk order has replaced the route they occurred on. Keep only the
    // requested cells. The caller owns the watch until the last contact has been consumed or the walker leaves.
    Dictionary<object, Dictionary<double, PositionSample>>? positionWatches;

    public void WatchPositions(object key, IEnumerable<double> times)
    {
        var watches = LazyInitializer.EnsureInitialized(ref positionWatches);
        lock (watches)
            if (!watches.ContainsKey(key)) watches.Add(key, times.ToDictionary(t => t, t => new PositionSample(t)));
    }

    public (int X, int Y) WatchedPosition(object key, double time)
    {
        var watches = Volatile.Read(ref positionWatches)!;
        lock (watches) return watches[key][time].Cell ?? Position(time);
    }

    public void EndPositionWatch(object key)
    {
        if (Volatile.Read(ref positionWatches) is { } watches)
            lock (watches) watches.Remove(key);
    }

    void CapturePositionsBefore(double time)
    {
        if (Volatile.Read(ref positionWatches) is not { } watches) return;
        lock (watches)
        {
            foreach (var watch in watches.Values)
                foreach (var sample in watch.Values)
                    if (sample.Cell is null && sample.Time < time) sample.Cell = Position(sample.Time);
        }
    }
}
