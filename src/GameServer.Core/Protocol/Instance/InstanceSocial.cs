using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// Player-to-player requests: trade, party and friends (captured live 2026-10-28 with two clients in one town).
/// </summary>
public static class InstanceSocial
{
    public const ushort TradeRequestId = 0xba, TradeRequestAcceptId = 0xb0, PartyInviteId = 0x93, FriendAddId = 0x80;
    public const int TradeRequestLength = 6, TradeRequestAcceptLength = 6;

    /// <summary>C→S 0xba (6 bytes): "Trade" on another player: u32 BE that player's object id as the sender sees it.</summary>
    public static uint ReadTradeRequest(ReadOnlySpan<byte> m) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(m[2..]);

    /// <summary>C→S 0xb0 (6 bytes): the trade request notification's accept: u32 BE the requester's object id.</summary>
    public static uint ReadTradeRequestAccept(ReadOnlySpan<byte> m) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(m[2..]);

    /// <summary>C→S 0x93: "Invite to Party": wstr the character's name.</summary>
    public static string ReadPartyInvite(ReadOnlySpan<byte> m) => new LoginReader(m[2..]).WStr();

    /// <summary>C→S 0x80: "Add Friend": wstr the character's name, u16 (0 from the context menu).</summary>
    public static (string Name, ushort U16) ReadFriendAdd(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m[2..]);
        return (r.WStr(), r.U16());
    }

    /// <summary>Bytes of a 0x93 at the start of <paramref name="m"/>, or null if it is not complete yet.</summary>
    public static int? PartyInviteLength(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m);
        r.U16();
        r.WStr();
        return r.Short ? null : r.Position;
    }

    /// <summary>Bytes of a 0x80 at the start of <paramref name="m"/>, or null if it is not complete yet.</summary>
    public static int? FriendAddLength(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m);
        r.U16();
        r.WStr();
        r.U16();
        return r.Short ? null : r.Position;
    }

    /// <summary>0xb8 flags bit 1 with bit 0 clear: a player's trade request. The in-game UI (0x140dccdf0) looks the wstr
    /// name up among the players it knows (instance +0xc748, 0x142166b40) and shows "{name} sent you a trade request"
    /// (ClientStrings 0x441, 0x140afb820). Bit 0 alone shows the requester "Waiting for player to accept trade
    /// request..." (ClientStrings 0x2bd) for the u32 other party; both bits open the trade window.</summary>
    public const byte TradeRequestFlag = 0x02;

    public const ushort KnownPlayerId = 0xa7, ForgetPlayerId = 0xa8;

    /// <summary>
    /// S→C 0xa7 (class 0x1435dbda8, 0xd0 bytes, write 0x1421a11a0 → 0x141f8f580; dispatcher case 0x28): adds or updates a
    /// player in the client's known-player table (instance +0xc738, an FNV-hashed map keyed by the first wstr, inserted
    /// by 0x142173b60 → 0x142178940). The table is what the trade request (0xb8 flag 2, 0x142166b40 by character name)
    /// and the party list (0xa9) look players up in; without an entry the request is dropped silently (live 2026-10-28).
    /// Wire: wstr account (key, entry +0x30), wstr (+0x50), u8 flags (bit 0: u16 pair +0xaa/+0xae follows, bit 1: u16
    /// pair +0xb0/+0xb2 follows, bit 2 +0xba: character data, bits 3..6 +0xbb/+0xbc/+0xbd/+0xbe); with bit 2: wstr character
    /// (+0x70), u8 (+0xb9), u8 (+0xb8), u16 BE (+0xa8, taken as the level), u16 BE (+0xac), wstr (+0x90), u8 (+0xb4), then
    /// the pairs; without bit 2 a u64 (+0xa0).
    /// </summary>
    public static byte[] KnownPlayer(string account, string character, int level) =>
        new LoginWriter().U16(KnownPlayerId).WStr(account).WStr("").U8(0x04)
            .WStr(character).U8(0).U8(0).U16((ushort)Math.Clamp(level, 0, ushort.MaxValue)).U16(0).WStr("").U8(0).ToArray();
}
