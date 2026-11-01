using System.Collections.Concurrent;
using GameServer.Core.Protocol.Instance;


/// <summary>
/// The global and trade chat channels of each character, and the echo of what a player types
/// (Agents/docs/protocol/10-chat-and-commands.md §1, §2). There is one player per server, so a channel only holds its own
/// lines: the server answers each join (C→S 0x134) with S→C 0x135 and sends every typed line back as an S→C 0x17 line
/// from the character, on the channel it was typed in.
/// </summary>
sealed class ChatChannels
{
    /// <summary>The channel given for a join without a number (0xffff) before the character chose one.</summary>
    public const ushort DefaultChannel = 1;

    readonly ConcurrentDictionary<(string Character, ChatChannelKind Kind), (ushort Channel, byte Language)> _chosen = new();

    /// <summary>
    /// The channel a join request gets: the number asked for (/global N, /trade N, or the one the client remembers), else
    /// the character's last channel of that kind, else <see cref="DefaultChannel"/>. The channel is kept for the next areas
    /// (<see cref="Current"/>) and logins while the Gateway runs. <c>Changed</c> is false when the character was already on it.
    /// </summary>
    public (ushort Channel, bool Changed) Join(string character, ChatChannelKind kind, ushort requested, byte language = 0)
    {
        var key = (character.ToUpperInvariant(), kind);
        bool had = _chosen.TryGetValue(key, out var previous);
        ushort channel = requested is 0 or InstanceChat.AnyChannel ? (had ? previous.Channel : DefaultChannel) : requested;
        _chosen[key] = (channel, language);
        return (channel, !had || previous.Channel != channel);
    }

    /// <summary>
    /// The channels the character is on, global first. The client joins only once per game session (after the first
    /// area), and every area change starts a new instance connection whose channels are empty again, so the server
    /// tells each new connection the channels with S→C 0x135 (live 2026-10-25: after /goto both were 0 until then).
    /// </summary>
    public List<(ChatChannelKind Kind, ushort Channel, byte Language)> Current(string character)
    {
        var current = new List<(ChatChannelKind, ushort, byte)>();
        foreach (var kind in (ChatChannelKind[])[ChatChannelKind.Global, ChatChannelKind.Trade])
            if (_chosen.TryGetValue((character.ToUpperInvariant(), kind), out var c)) current.Add((kind, c.Channel, c.Language));
        return current;
    }

    /// <summary>The line the chat panel prints after a join (BackendErrors JoinedGlobalChannel / JoinedTradeChannel,
    /// without the language name).</summary>
    public static string JoinedText(ChatChannelKind kind, ushort channel) =>
        $"You have joined {(kind == ChatChannelKind.Global ? "global" : "trade")} chat channel {channel}.";

    /// <summary>
    /// The S→C 0x17 line that shows the player's own typed line (C→S 0x15 without a slash), or null when there is nothing
    /// to show: an empty line, or a party, guild or whisper line (there are no other players to send it to).
    /// </summary>
    public static byte[]? Echo(string character, string typed)
    {
        var (channel, text) = InstanceChat.SplitChannel(typed);
        if (string.IsNullOrWhiteSpace(text) || channel is not (TypedChannel.Local or TypedChannel.Global or TypedChannel.Trade))
            return null;
        return InstanceChat.ChatLine(text, InstanceChat.Sender(channel, character));
    }
}
