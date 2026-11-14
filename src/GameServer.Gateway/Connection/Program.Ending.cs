using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// Ending a connection from another one. The framework holds the connections and the game module the rules, so a rule
/// that lets one connection hold a character ends the one it replaces here: the client is told why, its stream is closed
/// so its own loop finishes, and the caller waits until everything it saves is written.
/// </summary>
public partial class Program
{
    /// <summary>
    /// Ends <paramref name="conn"/> and completes after its cleanup, its save included (<see cref="ClientState.Ended"/>).
    /// <paramref name="message"/> reaches the client first, when it still can. A connection that is already ending is
    /// waited for, not ended twice.
    /// </summary>
    public static async Task EndAsync(ClientState conn, string reason, string? message = null)
    {
        if (!conn.Closed)
        {
            conn.CloseReason ??= reason;
            if (message is { Length: > 0 } && conn.Outbox is { } outbox)
            {
                try { await outbox(InstanceChat.ChatLine(message), $"0x0017 chat line \"{message}\""); }
                catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException) { }
            }
            // Closed stops the frame loops (WorldOutput.Close); closing the stream is what ends the pending read.
            conn.Closed = true;
            if (conn.Wire.Stream is { } stream)
            {
                try { stream.Close(); }
                catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            }
        }
        await conn.Ended.Task;
    }
}
