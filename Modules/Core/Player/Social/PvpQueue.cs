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
    static readonly Dictionary<Player, (string QueueName, byte OtherPlayers, bool IsChallenge, bool ShowWithdraw,
        ulong NextMatchTimeUnix, Player? Opponent)> Entries =
        Keep("Entries", () => new Dictionary<Player, (string, byte, bool, bool, ulong, Player?)>());

    /// <summary>Whether <paramref name="player"/> currently has an active PvP queue or challenge entry.</summary>
    public static bool IsQueued(Player player)
    {
        lock (Gate) return Entries.ContainsKey(player);
    }

    /// <summary>Current queue/challenge state for <paramref name="player"/>, or null when not queued.</summary>
    public static (string QueueName, byte OtherPlayers, bool IsChallenge, bool ShowWithdraw, ulong NextMatchTimeUnix,
        Player? Opponent)? StatusOf(Player player)
    {
        lock (Gate) return Entries.TryGetValue(player, out var entry) ? entry : null;
    }

    /// <summary>Records a PvP queue or challenge entry for <paramref name="player"/> and sends S→C 0x118.</summary>
    public static async Task Enqueue(Player player, string queueName, byte otherPlayers = 0, bool isChallenge = false,
        bool showWithdraw = true, ulong nextMatchTimeUnix = 0, Player? opponent = null)
    {
        if (player.Closed) return;
        queueName ??= "";
        if (ReferenceEquals(opponent, player)) opponent = null;

        Player? replacedOpponent = null;
        bool replacedWasChallenge = false;
        lock (Gate)
        {
            if (Entries.TryGetValue(player, out var previous)
                && previous.Opponent is { } oldOpponent && !ReferenceEquals(oldOpponent, opponent))
            {
                replacedOpponent = oldOpponent;
                replacedWasChallenge = previous.IsChallenge;
                if (Entries.TryGetValue(oldOpponent, out var oldReverse) && ReferenceEquals(oldReverse.Opponent, player))
                    Entries.Remove(oldOpponent);
            }
            Entries[player] = (queueName, otherPlayers, isChallenge, showWithdraw, nextMatchTimeUnix, opponent);
        }

        if (replacedOpponent is not null)
        {
            string name = CharacterName(player);
            string notice = replacedWasChallenge
                ? $"{name} cancelled the PvP challenge."
                : $"{name} left the PvP queue.";
            await NotifyCleared(replacedOpponent, notice);
        }

        await SendTo(player, InstancePvp.QueueStatus(otherPlayers, queueName, isChallenge, showWithdraw, nextMatchTimeUnix),
            $"0x0118 pvp queue status \"{queueName}\" (challenge={isChallenge}, withdraw={showWithdraw})");
    }

    static bool CompatibleForPvp(Player a, Player b) =>
        !ReferenceEquals(a, b) && !a.Closed && !b.Closed && !a.LeavingArea && !b.LeavingArea
        && !a.Character.Ssf && !b.Character.Ssf
        && a.Character.Hardcore == b.Character.Hardcore && a.Character.LeagueForm == b.Character.LeagueForm
        && string.Equals(a.Character.League, b.Character.League, StringComparison.OrdinalIgnoreCase);

    /// <summary>Joins a named PvP matchmaking queue, matching immediately if another compatible player is already
    /// queued in the same queue and league; otherwise shows S→C 0x118 with waiting count.</summary>
    public static async Task<bool> JoinQueue(Player player, string queueName)
    {
        if (player.Closed || player.LeavingArea || player.Character.Ssf) return false;
        queueName = string.IsNullOrWhiteSpace(queueName) ? "1v1 Arena" : queueName.Trim();

        Player? matched = null;
        lock (Gate)
        {
            foreach (var (other, entry) in Entries)
            {
                if (!entry.IsChallenge && entry.Opponent is null
                    && string.Equals(entry.QueueName, queueName, StringComparison.OrdinalIgnoreCase)
                    && CompatibleForPvp(player, other))
                {
                    matched = other;
                    break;
                }
            }
            if (matched is not null)
            {
                Entries.Remove(matched);
                Entries.Remove(player);
            }
        }

        if (matched is not null)
        {
            string a = CharacterName(player), b = CharacterName(matched);
            await SendTo(player, InstancePvp.QueueHide(), $"0x0119 pvp match found ({queueName}: {a} vs {b})");
            await SendTo(matched, InstancePvp.QueueHide(), $"0x0119 pvp match found ({queueName}: {a} vs {b})");
            await SendTo(player, InstanceChat.ChatLine($"PvP match found for {queueName} against {b}."),
                $"0x0017 pvp match found for {a}");
            await SendTo(matched, InstanceChat.ChatLine($"PvP match found for {queueName} against {a}."),
                $"0x0017 pvp match found for {b}");
            return true;
        }

        await Enqueue(player, queueName, otherPlayers: 0, isChallenge: false, showWithdraw: true);
        return false;
    }

    /// <summary>Handles C→S 0x156 (accept or decline a pending duel challenge).</summary>
    public static async Task RespondToChallenge(Player player, bool accepted)
    {
        if (!accepted)
        {
            await Leave(player, withdraw: false);
            return;
        }
        Player? challenger = null;
        lock (Gate)
        {
            foreach (var (other, entry) in Entries)
            {
                if (entry.IsChallenge && ReferenceEquals(entry.Opponent, player))
                {
                    challenger = other;
                    Entries.Remove(other);
                    Entries.Remove(player);
                    break;
                }
            }
        }
        if (challenger is null) return;
        string cName = CharacterName(challenger), pName = CharacterName(player);
        await SendTo(challenger, InstancePvp.QueueHide(), $"0x0119 duel accepted by {pName}");
        await SendTo(player, InstancePvp.QueueHide(), $"0x0119 duel accepted against {cName}");
        await SendTo(challenger, InstanceChat.ChatLine($"{pName} accepted your duel challenge."),
            $"0x0017 duel accepted by {pName}");
        await SendTo(player, InstanceChat.ChatLine($"Accepted duel challenge from {cName}."),
            $"0x0017 duel accepted from {cName}");
    }

    /// <summary>Handles C→S 0x11a (Leave Queue / decline) and C→S 0x18d (Withdraw): clears any tracked entry,
    /// always sends S→C 0x119 so stale HUD bars close cleanly, and notifies any linked opponent.</summary>
    public static async Task Leave(Player player, bool withdraw)
    {
        string name = CharacterName(player);
        List<(Player Peer, string Notice)> notify = [];

        lock (Gate)
        {
            if (Entries.Remove(player, out var own) && own.Opponent is { } opponent && !ReferenceEquals(opponent, player))
            {
                if (Entries.TryGetValue(opponent, out var reverse) && ReferenceEquals(reverse.Opponent, player))
                    Entries.Remove(opponent);
                string notice = own.IsChallenge
                    ? withdraw ? $"{name} withdrew the PvP challenge." : $"{name} cancelled the PvP challenge."
                    : $"{name} left the PvP queue.";
                notify.Add((opponent, notice));
            }

            foreach (var (other, entry) in Entries.Where(e => ReferenceEquals(e.Value.Opponent, player)).ToArray())
            {
                Entries.Remove(other);
                if (notify.Any(n => ReferenceEquals(n.Peer, other))) continue;
                string notice = entry.IsChallenge
                    ? $"{name} declined the PvP challenge."
                    : $"{name} left the PvP queue.";
                notify.Add((other, notice));
            }
        }

        await SendTo(player, InstancePvp.QueueHide(), withdraw ? "0x0119 pvp queue withdrawn" : "0x0119 pvp queue left");
        foreach (var (peer, notice) in notify)
            await NotifyCleared(peer, notice);
    }

    /// <summary>Cleans up PvP queue/challenge state when <paramref name="player"/> leaves the area or disconnects,
    /// notifying any linked opponent.</summary>
    public static async Task Leaving(Player player)
    {
        string name = CharacterName(player);
        List<(Player Peer, string Notice)> notify = [];

        lock (Gate)
        {
            if (Entries.Remove(player, out var own) && own.Opponent is { } opponent && !ReferenceEquals(opponent, player))
            {
                if (Entries.TryGetValue(opponent, out var reverse) && ReferenceEquals(reverse.Opponent, player))
                    Entries.Remove(opponent);
                string notice = own.IsChallenge
                    ? $"{name} cancelled the PvP challenge."
                    : $"{name} left the PvP queue.";
                notify.Add((opponent, notice));
            }

            foreach (var (other, entry) in Entries.Where(e => ReferenceEquals(e.Value.Opponent, player)).ToArray())
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

    static async Task NotifyCleared(Player peer, string notice)
    {
        if (peer.Closed) return;
        await SendTo(peer, InstancePvp.QueueHide(), $"0x0119 pvp queue hidden ({notice})");
        await SendTo(peer, InstanceChat.ChatLine(notice), $"0x0017 chat line \"{notice}\"");
    }

    static async Task SendTo(Player player, byte[] bytes, string? log)
    {
        if (player.Closed) return;
        try
        {
            if (player.Connection.Outbox is { } outbox) { await outbox(bytes, log); return; }
            if (player.Connection.Connected) await Send(player, bytes, log);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }
}
