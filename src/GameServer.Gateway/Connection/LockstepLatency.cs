using System.Diagnostics;

/// <summary>
/// A lockstep client's real round trip, measured with S→C 0x194 (Game.ProbeLockstepLatency) to aim at the client's cell
/// for a target. It decides nothing: the client chooses its own Network Mode and every connection serves both (07 §6).
/// The client reads its socket once per update, so a slow one (a window in the background: about 100 ms on loopback) adds
/// up to a whole update to each round trip. Only the lowest of the last few is the network's, which is how the client's
/// own meter reads it too (07 §1).
/// </summary>
public sealed class LockstepLatency
{
    public const double Lost = 1000;
    public const int Window = 16, Enough = 8;

    readonly Queue<double> _samples = new();

    /// <summary>Enough round trips are in for <see cref="Lowest"/> to mean something.</summary>
    public bool Settled
    {
        get { lock (_samples) return _samples.Count >= Enough; }
    }

    /// <summary>One round trip in ms, <see cref="Lost"/> for a probe never answered.</summary>
    public void Add(double ms)
    {
        lock (_samples)
        {
            _samples.Enqueue(ms);
            if (_samples.Count > Window) _samples.Dequeue();
        }
    }

    /// <summary>The lowest round trip of the window.</summary>
    public double Lowest
    {
        get { lock (_samples) return _samples.Count == 0 ? double.NaN : _samples.Min(); }
    }
}
