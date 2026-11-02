using System.Net;

/// <summary>
/// Differs from GGG: it serves lockstep at any latency, and here only a connection confirmed as local gets it, off unless
/// Game_LOCKSTEP=1. GGG's server is the client's own simulation, so a late frame only delays it. Ours is a port that
/// corrects the client at each action start, which is only worth the input delay when the delay is one frame or two.
/// Every other connection keeps the predictive path and its message box, so a real network is never served this way.
/// </summary>
public static class LockstepLink
{
    /// <summary>Both socket ends are loopback, and the gateway address the client was given (the opening's u32, 0x14's)
    /// is too: a tunnel on the server's side delivers a remote client from 127.0.0.1, but that client was told another
    /// address. The client's own mode is not known here: it shows only in what the client sends, and a predictive
    /// client ignores 0x2b5.</summary>
    public static bool Confirmed(bool enabled, EndPoint? remote, EndPoint? local, uint openingAddress) =>
        enabled && Loopback(remote) && Loopback(local) && openingAddress >> 24 == 127;

    static bool Loopback(EndPoint? end) => end is IPEndPoint { Address: var address } && IPAddress.IsLoopback(address);
}
