using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// Chat on the instance connection (Agents/docs/protocol/10-chat-and-commands.md).
/// </summary>
public static class InstanceChat
{
    public const ushort ChatId = 0x15;

    /// <summary>
    /// C→S 0x15: what the player typed in the chat box, slash commands included. Compact wide string (the format of
    /// <see cref="LoginWriter.WStr"/>) then a u8, 0 in every capture. Plain text arrives with the channel prefix the
    /// client added ("#hello" for global), commands as typed ("/spawn_mob 42"). Captured live 2026-10-16.
    /// </summary>
    public sealed record ChatInput(string Text, byte U8)
    {
        public bool IsCommand => Text.StartsWith('/');
    }

    /// <summary>Bytes of the 0x15 at the start of <paramref name="m"/>, or null if it is not complete yet.</summary>
    public static int? Length(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m);
        r.U16();
        r.WStr();
        r.U8();
        return r.Short ? null : r.Position;
    }

    public const ushort MessageBoxId = 0x0d;

    /// <summary>S→C 0x0d (class 0x1435bf650, the one-wide-string class also used by login 0x24–0x29; dispatcher case 0 →
    /// listeners at +0xa1f0): a message box with the text and an OK button. Live 2026-10-16.</summary>
    public static byte[] MessageBox(string text) => new LoginWriter().U16(MessageBoxId).WStr(text).ToArray();

    public const ushort ChatLineId = 0x17;

    /// <summary>S→C 0x17 (class 0x1435dcec0, read 0x140e0c710; dispatcher case 2 → listeners at instance +0xb78 → chat
    /// panel 0x140179f20 / 0x14017a300): one line in the chat panel. wstr sender (its first character picks the channel),
    /// wstr text, wstr guild tag, u16 flags (bit 8 sends it to +0xd78 instead, bit 3 adds a field), u8, u8, 8 bytes, u8,
    /// u8 n + n linked items. An empty sender and zeros print the text alone. Live 2026-10-20.</summary>
    public static byte[] ChatLine(string text, string sender = "") =>
        new LoginWriter().U16(ChatLineId).WStr(sender).WStr(text).WStr("").U16(0).U8(0).U8(0).RawU64(0).U8(0).U8(0).ToArray();

    public static ChatInput ReadChat(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m[2..]);
        return new ChatInput(r.WStr(), r.U8());
    }

    /// <summary>
    /// The channel of a typed line and its text without the prefix: the client puts '#' before global, '$' before
    /// trade, '%' before party, '&amp;' before guild and '@' before a whisper; local has none (live 2026-10-25).
    /// </summary>
    public static (TypedChannel Channel, string Text) SplitChannel(string typed) => typed.Length == 0 ? (TypedChannel.Local, typed)
        : typed[0] switch
        {
            '#' => (TypedChannel.Global, typed[1..]),
            '$' => (TypedChannel.Trade, typed[1..]),
            '%' => (TypedChannel.Party, typed[1..]),
            '&' => (TypedChannel.Guild, typed[1..]),
            '@' => (TypedChannel.Whisper, typed[1..]),
            _ => (TypedChannel.Local, typed),
        };

    /// <summary>The 0x17 sender of a player's own line: the name, with '#' or '$' in front for the global and trade
    /// channels, which the chat panel shows as "#Name: text" in red and "$Name: text" in orange.</summary>
    public static string Sender(TypedChannel channel, string name) => channel switch
    {
        TypedChannel.Global => "#" + name,
        TypedChannel.Trade => "$" + name,
        _ => name,
    };

    public const ushort JoinChannelId = 0x134, ChannelJoinedId = 0x135;

    /// <summary>C→S 0x134 is 7 bytes.</summary>
    public const int JoinChannelLength = 7;

    /// <summary>The channel number of a join without one: "any channel" (the client's remembered one is also 0xffff
    /// until the player types /global N or /trade N).</summary>
    public const ushort AnyChannel = 0xffff;

    /// <summary>
    /// C→S 0x134 (class 0x1435bf730, write 0x141f9ae30; sender 0x142169250): join a global or trade chat channel.
    /// u16 channel, u8 kind (0 global, 1 trade), u8 automatic (1 when the client joins by itself after entering an area,
    /// 0 for /global N and /trade N), u8 language. The client waits 5 s for 0x135, then prints "Timeout while joining
    /// global chat channel N" (or "… global chat." for 0xffff). Live 2026-10-25: two after entering an area (global and
    /// trade, 0xffff, automatic, language 0), one per /global 821 and /trade 822.
    /// </summary>
    public sealed record JoinChannelRequest(ChatChannelKind Kind, ushort Channel, bool Automatic, byte Language);

    /// <summary>The request, or null for a kind other than global (0) or trade (1): the client has only those two.</summary>
    public static JoinChannelRequest? ReadJoinChannel(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m[2..]);
        ushort channel = r.U16();
        byte kind = r.U8();
        bool automatic = r.Bool();
        return kind > 1 ? null : new JoinChannelRequest(kind == 0 ? ChatChannelKind.Global : ChatChannelKind.Trade, channel, automatic, r.U8());
    }

    /// <summary>
    /// S→C 0x135 (class 0x1435dc240, read 0x1421a6cd0; dispatcher case 0x56): the channel joined. u16 channel, u8 kind
    /// (0 global, 1 trade), u8 language. The client clears the pending join's 5 s timeout, keeps the channel as the current
    /// one (instance +0xcef8 global, +0xcef0 trade) and enables the chat panel's Global or Trade tab. Live 2026-10-25.
    /// </summary>
    public static byte[] ChannelJoined(ChatChannelKind kind, ushort channel, byte language) =>
        new LoginWriter().U16(ChannelJoinedId).U16(channel).U8((byte)kind).U8(language).ToArray();
}

/// <summary>The two chat channels a player joins by number (C→S 0x134, S→C 0x135).</summary>
public enum ChatChannelKind : byte { Global = 0, Trade = 1 }

/// <summary>Where a typed chat line goes, from its prefix (<see cref="InstanceChat.SplitChannel"/>).</summary>
public enum TypedChannel { Local, Global, Trade, Party, Guild, Whisper }
