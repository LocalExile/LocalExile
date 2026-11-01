using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// Player-to-player requests: trade, party and friends (captured live 2026-10-28 with two clients in one town).
/// </summary>
public static class InstanceSocial
{
    public const ushort TradeRequestId = 0xba, TradeRequestAcceptId = 0xb0, PartyInviteId = 0x93, FriendAddId = 0x80;
    public const int TradeRequestLength = 6, TradeRequestAcceptLength = 6;

    /// <summary>C→S 0x95 (6 bytes): the party invite notification's Accept: u32 BE the party id from S→C 0xa9 (live
    /// 2026-10-29: 0x103, 0x104 for the invites sent).</summary>
    public const ushort PartyAcceptId = 0x95;
    public const int PartyAcceptLength = 6;
    public static uint ReadPartyAccept(ReadOnlySpan<byte> m) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(m[2..]);

    /// <summary>C→S 0x96 (6 bytes): "Leave Party": u32 BE the party id (live 2026-10-29: 0x101).</summary>
    public const ushort PartyLeaveId = 0x96;
    public const int PartyLeaveLength = 6;

    /// <summary>S→C 0xaa (class 0x1435bf570, read 0x141f9c0b0; dispatcher case 0x2b, 0x14214cb45): u32 BE a party id,
    /// u8 a reason. For the client's current party (+0xc680) it resets the party (0x142157450 with an empty one) and fires
    /// its event with the reason; another id is dropped from the known parties (+0xc668). (0xab, case 0x2c, only clears a
    /// pending invite, +0xc730.)</summary>
    public const ushort PartyRemoveId = 0xaa;
    public static byte[] PartyRemove(uint party, byte reason = 0) => new LoginWriter().U16(PartyRemoveId).U32(party).U8(reason).ToArray();

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

    public const ushort KnownPlayerId = 0xa7, ForgetPlayerId = 0xa8, PartyInfoId = 0xa9;

    /// <summary>S→C 0xa9 (class 0x1435dc860, write 0x1421a1620, read 0x1421a18b0; dispatcher case 0x2a): u32 +0x68, wstr
    /// +0x28, wstr +0x48, u32 +0x68 again, u8 +0x6e, u8 +0x6c, u8 +0x6d, u8 +0x6f, u8 member count, then per member a
    /// wstr name (an account name: looked up in the known players, 0xa7) and a u8 status. +0x68 is the party id; +0x6e the
    /// receiving player's own index in the list (0xff none); the second wstr the leader's account name.</summary>
    public static byte[] PartyInfo(uint u68, string s28, string s48, byte b6e, byte b6c, byte b6d, byte b6f,
        IReadOnlyList<(string Name, byte Status)> members)
    {
        var w = new LoginWriter().U16(PartyInfoId).U32(u68).WStr(s28).WStr(s48).U32(u68).U8(b6e).U8(b6c).U8(b6d).U8(b6f)
            .U8((byte)members.Count);
        foreach (var (name, status) in members) w.WStr(name).U8(status);
        return w.ToArray();
    }

    /// <summary>
    /// S→C 0xa7 (class 0x1435dbda8, 0xd0 bytes, write 0x1421a11a0 → 0x141f8f580; dispatcher case 0x28): adds or updates a
    /// player in the client's known-player table (instance +0xc738, an FNV-hashed map keyed by the first wstr, inserted
    /// by 0x142173b60 → 0x142178940). The table is what the trade request (0xb8 flag 2, 0x142166b40 by character name)
    /// and the party list (0xa9) look players up in; without an entry the request is dropped silently (live 2026-10-28).
    /// Wire: wstr account (key, entry +0x30), wstr (+0x50), u8 flags (bit 0: u16 pair +0xaa/+0xae follows, bit 1: u16
    /// pair +0xb0/+0xb2 follows, bit 2 +0xba: character data, bits 3..6 +0xbb/+0xbc/+0xbd/+0xbe); with bit 2: wstr character
    /// (+0x70), u8 (+0xb9), u8 (+0xb8, the packed class), u16 BE (+0xa8, taken as the level), u16 BE (+0xac), wstr (+0x90),
    /// u8 (+0xb4), then the pairs; without bit 2 a u64 (+0xa0). Read back by 0x141f8f850. The bit 1 pair is the portrait
    /// frame (+0xb0) and the portrait (+0xb2), which the party portraits (0x14020b230) and social rows (0x140af6c20) draw;
    /// the client's own writer leaves the pair out when both are 0.
    /// </summary>
    public static byte[] KnownPlayer(string account, string character, int level, byte packedClass = 0, ushort frame = 0, ushort portrait = 0)
    {
        bool look = frame != 0 || portrait != 0;
        var w = new LoginWriter().U16(KnownPlayerId).WStr(account).WStr("").U8((byte)(look ? 0x06 : 0x04))
            .WStr(character).U8(0).U8(packedClass).U16((ushort)Math.Clamp(level, 0, ushort.MaxValue)).U16(0).WStr("").U8(0);
        if (look) w.U16(frame).U16(portrait);
        return w.ToArray();
    }
}
