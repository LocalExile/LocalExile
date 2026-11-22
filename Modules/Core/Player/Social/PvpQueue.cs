using GameServer.Core.Protocol.Instance;
using static Program;

namespace GameServer.Modules.Social;

/// <summary>
/// Server-side PvP queue and challenge bar state (S→C 0x118 / 0x119, C→S 0x11a / 0x18d).
/// Backing storage uses BCL and Gateway framework types only so collectible Social reloads never retain old types.
/// </summary>
public static class PvpQueue
{
    // BCL/Gateway-only values survive a collectible Social reload without retaining this assembly's types.
    static T Keep<T>(string name, Func<T> create) where T : class
    {
        string key = "LocalExile.Social.PvpQueue." + name;
        if (AppContext.GetData(key) is T old) return old;
        var value = create();
        AppContext.SetData(key, value);
        return value;
    }

    static readonly object Gate = Keep("Gate", () => new object());
    static readonly Dictionary<ClientState, (string QueueName, byte OtherPlayers, bool IsChallenge, bool ShowWithdraw,
        ulong NextMatchTimeUnix, ClientState? Opponent)> Entries =
        Keep("Entries", () => new Dictionary<ClientState, (string, byte, bool, bool, ulong, ClientState?)>());

    /// <summary>Whether <paramref name="conn"/> currently has an active PvP queue or challenge entry.</summary>
    public static bool IsQueued(ClientState conn)
    {
        lock (Gate) return Entries.ContainsKey(conn);
    }

    /// <summary>Current queue/challenge state for <paramref name="conn"/>, or null when not queued.</summary>
    public static (string QueueName, byte OtherPlayers, bool IsChallenge, bool ShowWithdraw, ulong NextMatchTimeUnix,
        ClientState? Opponent)? StatusOf(ClientState conn)
    {
        lock (Gate) return Entries.TryGetValue(conn, out var entry) ? entry : null;
    }

    /// <summary>Records a PvP queue or challenge entry for <paramref name="conn"/> and sends S→C 0x118.</summary>
    public static async Task Enqueue(ClientState conn, string queueName, byte otherPlayers = 0, bool isChallenge = false,
        bool showWithdraw = true, ulong nextMatchTimeUnix = 0, ClientState? opponent = null)
    {
        if (conn.Closed) return;
        queueName ??= "";
        if (ReferenceEquals(opponent, conn)) opponent = null;

        ClientState? replacedOpponent = null;
        bool replacedWasChallenge = false;
        lock (Gate)
        {
            if (Entries.TryGetValue(conn, out var previous)
                && previous.Opponent is { } oldOpponent && !ReferenceEquals(oldOpponent, opponent))
            {
                replacedOpponent = oldOpponent;
                replacedWasChallenge = previous.IsChallenge;
                if (Entries.TryGetValue(oldOpponent, out var oldReverse) && ReferenceEquals(oldReverse.Opponent, conn))
                    Entries.Remove(oldOpponent);
            }
            Entries[conn] = (queueName, otherPlayers, isChallenge, showWithdraw, nextMatchTimeUnix, opponent);
        }

        if (replacedOpponent is not null)
        {
            string name = CharacterName(conn);
            string notice = replacedWasChallenge
                ? $"{name} cancelled the PvP challenge."
                : $"{name} left the PvP queue.";
            await NotifyCleared(replacedOpponent, notice);
        }

        await SendTo(conn, InstancePvp.QueueStatus(otherPlayers, queueName, isChallenge, showWithdraw, nextMatchTimeUnix),
            $"0x0118 pvp queue status \"{queueName}\" (challenge={isChallenge}, withdraw={showWithdraw})");
    }

    /// <summary>Handles C→S 0x11a (Leave Queue / decline) and C→S 0x18d (Withdraw): clears any tracked entry,
    /// always sends S→C 0x119 so stale HUD bars close cleanly, and notifies any linked opponent.</summary>
    public static async Task Leave(ClientState conn, bool withdraw)
    {
        string name = CharacterName(conn);
        List<(ClientState Peer, string Notice)> notify = [];

        lock (Gate)
        {
            if (Entries.Remove(conn, out var own) && own.Opponent is { } opponent && !ReferenceEquals(opponent, conn))
            {
                if (Entries.TryGetValue(opponent, out var reverse) && ReferenceEquals(reverse.Opponent, conn))
                    Entries.Remove(opponent);
                string notice = own.IsChallenge
                    ? withdraw ? $"{name} withdrew the PvP challenge." : $"{name} cancelled the PvP challenge."
                    : $"{name} left the PvP queue.";
                notify.Add((opponent, notice));
            }

            foreach (var (other, entry) in Entries.Where(e => ReferenceEquals(e.Value.Opponent, conn)).ToArray())
            {
                Entries.Remove(other);
                if (notify.Any(n => ReferenceEquals(n.Peer, other))) continue;
                string notice = entry.IsChallenge
                    ? $"{name} declined the PvP challenge."
                    : $"{name} left the PvP queue.";
                notify.Add((other, notice));
            }
        }

        await SendTo(conn, InstancePvp.QueueHide(), withdraw ? "0x0119 pvp queue withdrawn" : "0x0119 pvp queue left");
        foreach (var (peer, notice) in notify)
            await NotifyCleared(peer, notice);
    }

    /// <summary>Cleans up PvP queue/challenge state when <paramref name="conn"/> leaves the area or disconnects,
    /// notifying any linked opponent.</summary>
    public static async Task Leaving(ClientState conn)
    {
        string name = CharacterName(conn);
        List<(ClientState Peer, string Notice)> notify = [];

        lock (Gate)
        {
            if (Entries.Remove(conn, out var own) && own.Opponent is { } opponent && !ReferenceEquals(opponent, conn))
            {
                if (Entries.TryGetValue(opponent, out var reverse) && ReferenceEquals(reverse.Opponent, conn))
                    Entries.Remove(opponent);
                string notice = own.IsChallenge
                    ? $"{name} cancelled the PvP challenge."
                    : $"{name} left the PvP queue.";
                notify.Add((opponent, notice));
            }

            foreach (var (other, entry) in Entries.Where(e => ReferenceEquals(e.Value.Opponent, conn)).ToArray())
            {
                Entries.Remove(other);
                if (notify.Any(n => ReferenceEquals(n.Peer, other))) continue;
                string notice = entry.IsChallenge
                    ? $"{name} is no longer available for the PvP challenge."
                    : $"{name} left the PvP queue.";
                notify.Add((other, notice));
            }
        }

        foreach (var (peer, notice) in notify)
            await NotifyCleared(peer, notice);
    }

    static async Task NotifyCleared(ClientState peer, string notice)
    {
        if (peer.Closed) return;
        await SendTo(peer, InstancePvp.QueueHide(), $"0x0119 pvp queue hidden ({notice})");
        await SendTo(peer, InstanceChat.ChatLine(notice), $"0x0017 chat line \"{notice}\"");
    }

    static async Task SendTo(ClientState conn, byte[] bytes, string? log)
    {
        if (conn.Closed) return;
        try
        {
            if (conn.Outbox is { } outbox) { await outbox(bytes, log); return; }
            var (remote, stream, ciphers) = conn.Wire;
            if (stream is not null) await Send(remote, stream, ciphers, bytes, log);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }
}
