using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// Chat on the instance connection (Agents/docs/protocol/10-chat-and-commands.md).
/// </summary>
public static class InstanceChat
{
    public const ushort ChatId = 0x15;

    /// <summary>
    /// C→S 0x15 (class 0x1435cb088, write 0x141f97a10, read 0x141f97bd0): what the player typed in the chat box, slash
    /// commands included. Compact wide string (the format of <see cref="LoginWriter.WStr"/>), u8 n, then n × u32: where
    /// each linked item stands in the text, on a '_', counting the channel prefix. Plain text arrives with the prefix the
    /// client added ("#hello" for global), commands as typed ("/spawn_mob 42"). Captured live 2026-10-16.
    /// </summary>
    public sealed record ChatInput(string Text, IReadOnlyList<uint> Links)
    {
        public bool IsCommand => Text.StartsWith('/');
    }

    /// <summary>Bytes of the 0x15 at the start of <paramref name="m"/>, or null if it is not complete yet.</summary>
    public static int? Length(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m);
        r.U16();
        r.WStr();
        for (int links = r.U8(); links > 0; links--) r.U32();
        return r.Short ? null : r.Position;
    }

    /// <summary>C→S 0x12, 0x13 and 0x14 (senders in 0x14018faa0): one per item linked in a line, just before its 0x15. Only
    /// 0x12, an item in an inventory, is mapped; 0x13 (u32, u8 index, u8) and 0x14 (u32, u8 index) are the client's other
    /// kinds of link.</summary>
    public const ushort LinkItemId = 0x12, LinkOtherId = 0x13, LinkAnotherId = 0x14;
    public const int LinkItemLength = 12, LinkOtherLength = 8, LinkAnotherLength = 7;

    /// <summary>C→S 0x12 (class 0x1435ca9f8, write 0x141f97830): u8 the link's index in its line, u32 inventory id (with
    /// the side-channel bits of <see cref="InstanceItems.ReadPickUpItem"/>), u32 item id, u8.</summary>
    public sealed record LinkItem(byte Index, uint InventoryId, uint ItemId);

    public static LinkItem ReadLinkItem(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m[2..]);
        return new(r.U8(), r.U32() & 0xc01fffff, r.U32());
    }

    public const ushort MessageBoxId = 0x0d;

    /// <summary>S→C 0x0d (class 0x1435ca068, the one-wide-string class also used by login 0x24–0x29; dispatcher case 0 →
    /// listeners at +0xa1f0): a message box with the text and an OK button. Live 2026-10-16.</summary>
    public static byte[] MessageBox(string text) => new LoginWriter().U16(MessageBoxId).WStr(text).ToArray();

    public const ushort ChatLineId = 0x17;

    /// <summary>S→C 0x17 (class 0x1435e6908, read 0x140e0afb0; dispatcher case 2 → listeners at instance +0xb78 → chat
    /// panel 0x14017a270 / 0x14017a670): one line in the chat panel. wstr sender (its first character picks the channel),
    /// wstr text, wstr guild tag, u16 flags (bit 8 sends it to +0xd78 instead, bit 3 adds a field), u8, u8, 8 bytes, u8,
    /// u8 n + n linked items, in the order of the text. An empty sender and zeros print the text alone. Live 2026-10-20.</summary>
    public static byte[] ChatLine(string text, string sender = "", ushort flags = 0, IReadOnlyList<ChatLineItem>? items = null)
    {
        var w = new LoginWriter().U16(ChatLineId).WStr(sender).WStr(text).WStr("").U16(flags).U8(0).U8(0).RawU64(0).U8(0).U8((byte)(items?.Count ?? 0));
        foreach (var item in items ?? []) w.U32(item.At).Bytes(item.Wire);
        return w.ToArray();
    }

    /// <summary>The 0x17 flag of the sender's own copy of a whisper: bit 2 of the flags' first byte (0x14017a670; 4 in the second
    /// byte reddens the text). An '@' line then reads "@To Name" for "@From Name",
    /// without the whisper sound and the window flash (0x14017a670).</summary>
    public const ushort WhisperSentFlag = 0x0400;

    public static ChatInput ReadChat(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m[2..]);
        string text = r.WStr();
        var links = new uint[r.U8()];
        for (int i = 0; i < links.Length; i++) links[i] = r.U32();
        return new ChatInput(text, links);
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

    /// <summary>The 0x17 sender of a player's own line: the name, with the channel's prefix in front for the global,
    /// trade and party channels, which the chat panel shows as "#Name: text" in red, "$Name: text" in orange and
    /// "%Name: text" in the party colour. The prefix is what tells a party line from a local one.</summary>
    public static string Sender(TypedChannel channel, string name) => channel switch
    {
        TypedChannel.Global => "#" + name,
        TypedChannel.Trade => "$" + name,
        TypedChannel.Party => "%" + name,
        _ => name,
    };

    public const ushort JoinChannelId = 0x134, ChannelJoinedId = 0x135;

    /// <summary>C→S 0x134 is 7 bytes.</summary>
    public const int JoinChannelLength = 7;

    /// <summary>The channel number of a join without one: "any channel" (the client's remembered one is also 0xffff
    /// until the player types /global N or /trade N).</summary>
    public const ushort AnyChannel = 0xffff;

    /// <summary>
    /// C→S 0x134 (class 0x1435ca148, write 0x141fa1860; sender 0x14216fc00): join a global or trade chat channel.
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
    /// S→C 0x135 (class 0x1435e7160, read 0x1421ad640; dispatcher case 0x56): the channel joined. u16 channel, u8 kind
    /// (0 global, 1 trade), u8 language. The client clears the pending join's 5 s timeout, keeps the channel as the current
    /// one (instance +0xcef8 global, +0xcef0 trade) and enables the chat panel's Global or Trade tab. Live 2026-10-25.
    /// </summary>
    public static byte[] ChannelJoined(ChatChannelKind kind, ushort channel, byte language) =>
        new LoginWriter().U16(ChannelJoinedId).U16(channel).U8((byte)kind).U8(language).ToArray();
}

/// <summary>An item linked in a 0x17 line: the '_' of the text it replaces (0x14017a670) and <see cref="Item.Wire"/>.</summary>
public sealed record ChatLineItem(uint At, byte[] Wire);

/// <summary>The two chat channels a player joins by number (C→S 0x134, S→C 0x135).</summary>
public enum ChatChannelKind : byte { Global = 0, Trade = 1 }

/// <summary>Where a typed chat line goes, from its prefix (<see cref="InstanceChat.SplitChannel"/>).</summary>
public enum TypedChannel { Local, Global, Trade, Party, Guild, Whisper }
