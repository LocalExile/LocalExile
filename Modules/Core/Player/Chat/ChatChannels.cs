using System.Collections.Concurrent;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Chat;

/// <summary>
/// The global and trade chat channels of each character (Agents/docs/protocol/10-chat-and-commands.md §2). The server answers
/// each join (C→S 0x134) with S→C 0x135; who else is on a channel is the chat server's.
/// </summary>
sealed class ChatChannels
{
    /// <summary>The channel given for a join without a number (0xffff) before the character chose one.</summary>
    public const ushort DefaultChannel = 1;

    readonly ConcurrentDictionary<(string Character, ChatChannelKind Kind), (ushort Channel, byte Language)> _chosen = new();

    /// <summary>
    /// The channel a join request gets: the number asked for (/global N, /trade N, or the one the client remembers), else
    /// the character's last channel of that kind, else <see cref="DefaultChannel"/>. The channel is kept for the next areas
    /// (<see cref="Current"/>) and logins while the Gateway runs. <c>Changed</c> is false when the character was already on it, in that language.
    /// </summary>
    public (ushort Channel, bool Changed) Join(string character, ChatChannelKind kind, ushort requested, byte language = 0)
    {
        var key = (character.ToUpperInvariant(), kind);
        bool had = _chosen.TryGetValue(key, out var previous);
        ushort channel = requested is 0 or InstanceChat.AnyChannel ? (had ? previous.Channel : DefaultChannel) : requested;
        _chosen[key] = (channel, language);
        return (channel, !had || previous.Channel != channel || previous.Language != language);
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

    /// <summary>The names of 0x134's language byte (the client's names to numbers: 0x1422ed4f0).</summary>
    static readonly string[] Languages = ["English", "French", "German", "Portuguese", "Russian", "Thai", "Traditional Chinese", "Simplified Chinese", "Spanish", "Korean", "Japanese"];

    /// <summary>The line the chat panel prints after a join (BackendErrors JoinedGlobalChannel / JoinedTradeChannel).</summary>
    public static string JoinedText(ChatChannelKind kind, ushort channel, byte language) =>
        $"You have joined {(kind == ChatChannelKind.Global ? "global" : "trade")} chat channel {channel}{(language < Languages.Length ? " " + Languages[language] : "")}.";
}
