using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;

/// <summary>Which connections a lockstep client is served on (Game_LOCKSTEP).</summary>
public enum LockstepMode
{
    /// <summary>None: every lockstep client gets the message box.</summary>
    Off,
    /// <summary><c>1</c>: connections confirmed as local, and timed.</summary>
    Local,
    /// <summary><c>network</c>: any connection, as GGG does. A predictive client still plays predictive.</summary>
    Network,
}

/// <summary>
/// Differs from GGG: it serves lockstep at any latency, and here it is off unless Game_LOCKSTEP asks for it, local only
/// with 1. GGG's server is the client's own simulation; ours is a port that corrects the client at each action start, so
/// serving any connection is a choice for whoever runs the server. Either way the client chooses its own mode, so a
/// predictive client keeps the predictive path.
/// </summary>
public static class LockstepLink
{
    public static LockstepMode ModeOf(string? setting) => setting switch
    {
        "1" => LockstepMode.Local,
        "network" => LockstepMode.Network,
        _ => LockstepMode.Off,
    };

    /// <summary>Both socket ends are loopback, and the gateway address the client was given (the opening's u32, 0x14's)
    /// is too: a tunnel on the server's side delivers a remote client from 127.0.0.1, but that client was told another
    /// address. The client's own mode is not known here: it shows only in what the client sends, and a predictive
    /// client ignores 0x2b7. A tunnel on both ends passes this: <see cref="LockstepLatency"/> measures that one.</summary>
    public static bool Confirmed(LockstepMode mode, EndPoint? remote, EndPoint? local, uint openingAddress) =>
        mode == LockstepMode.Network
        || mode == LockstepMode.Local && Loopback(remote) && Loopback(local) && openingAddress >> 24 == 127;

    static bool Loopback(EndPoint? end) => end is IPEndPoint { Address: var address } && IPAddress.IsLoopback(address);
}

/// <summary>
/// Whether a connection's real round trip is low enough for lockstep, whatever its addresses say. The bar is the client's
/// own: Auto picks lockstep under 70 ms and leaves it over 90 ms (0x142148880). The client reads its socket once per
/// update, so a slow one (a window in the background: about 100 ms on loopback) adds up to a whole update to each round
/// trip. Only the lowest of the last few is the network's, which is how the client's own meter reads it too (07 §1).
/// A character keeps its verdict across area changes, which are new connections, while the samples are recent.
/// </summary>
public sealed class LockstepLatency
{
    public const double ConfirmMs = 70, RefuseMs = 90, Lost = 1000;
    bool _always;
    public const int Window = 16, Enough = 8;
    static readonly TimeSpan Stale = TimeSpan.FromSeconds(30);
    static readonly ConcurrentDictionary<string, LockstepLatency> OfCharacter = new();

    readonly Queue<double> _samples = new();
    long _lastAt;

    /// <summary>Confirmed low: this connection may be served.</summary>
    public bool Confirmed { get; private set; }

    /// <summary>Measured and not low enough: this connection is treated as a real network's.</summary>
    public bool Refused { get; private set; }

    /// <summary>For a connection served whatever its round trip (<see cref="LockstepMode.Network"/>): timed for
    /// <see cref="Lowest"/> alone, with no verdict.</summary>
    public static LockstepLatency Unconditional() => new() { Confirmed = true, _always = true };

    /// <summary>Enough round trips are in for <see cref="Lowest"/> to mean something.</summary>
    public bool Settled
    {
        get { lock (_samples) return _samples.Count >= Enough; }
    }

    /// <summary>The character's measure. One gone stale starts over, so a later session proves itself again.</summary>
    public static LockstepLatency For(string character)
    {
        var latency = OfCharacter.GetOrAdd(character, _ => new());
        lock (latency._samples)
            if (Stopwatch.GetElapsedTime(latency._lastAt) > Stale)
            {
                latency._samples.Clear();
                latency.Confirmed = latency.Refused = false;
            }
        return latency;
    }

    /// <summary>One round trip in ms, <see cref="Lost"/> for a probe never answered. True when the verdict changes.</summary>
    public bool Add(double ms)
    {
        lock (_samples)
        {
            (bool wasConfirmed, bool wasRefused) = (Confirmed, Refused);
            _lastAt = Stopwatch.GetTimestamp();
            _samples.Enqueue(ms);
            if (_samples.Count > Window) _samples.Dequeue();
            if (!_always && _samples.Count >= Enough)
            {
                double lowest = _samples.Min();
                if (lowest < ConfirmMs) (Confirmed, Refused) = (true, false);
                else if (lowest >= RefuseMs || !Confirmed && _samples.Count == Window) (Confirmed, Refused) = (false, true);
            }
            return (wasConfirmed, wasRefused) != (Confirmed, Refused);
        }
    }

    /// <summary>The lowest round trip of the window, for the log.</summary>
    public double Lowest
    {
        get { lock (_samples) return _samples.Count == 0 ? double.NaN : _samples.Min(); }
    }
}
