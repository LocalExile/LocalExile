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

    /// <summary>S→C 0xaa (class 0x1435c9f88, read 0x141fa2ae0; dispatcher case 0x2b, 0x142153495): u32 BE a party id,
    /// u8 a reason. For the client's current party (+0xc680) it resets the party (0x14215ddc0 with an empty one) and fires
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

    /// <summary>C→S 0x94: wstr the selected party member's account (0x140ab8ed0). 0x97 promotes a leader.</summary>
    public const ushort PartyKickId = 0x94;
    public static string ReadPartyKick(ReadOnlySpan<byte> m) => new LoginReader(m[2..]).WStr();

    /// <summary>C→S 0x80: wstr name, two independent bytes (0x141f9b7e0).
    /// First byte selects friends (0) or ignore (1); second is add (0), remove/decline (1), accept (2).
    /// Returned U16 packs the native low/high bytes: friend add 0, remove/decline 0x100, accept 0x200.</summary>
    public static (string Name, ushort U16) ReadFriendAdd(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m[2..]);
        string name = r.WStr();
        byte operation = r.U8(), kind = r.U8();
        return (name, (ushort)(operation | kind << 8));
    }

    /// <summary>S→C 0xc9: account relation and note. Dispatcher 0x142154d2a: bit 0 accepted,
    /// bit 2 remove. The Friends panel (0x140a3c300) separates incoming invitations from accepted friends using bit 0;
    /// online character metadata comes from 0xa7, not 0xa3 (hideout invitation).</summary>
    public static byte[] FriendRelation(string account, bool accepted, bool remove = false) =>
        new LoginWriter().U16(0xc9).U8((byte)(remove ? 4 : accepted ? 1 : 0)).WStr(account).WStr("").ToArray();

    /// <summary>A known account without an active character: flags zero followed by its offline timestamp.</summary>
    public static byte[] OfflinePlayer(string account) =>
        new LoginWriter().U16(KnownPlayerId).WStr(account).WStr("").U8(0).RawU64(0).ToArray();

    /// <summary>Bytes of a 0x93 at the start of <paramref name="m"/>, or null if it is not complete yet.</summary>
    public static int? PartyInviteLength(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m);
        r.U16();
        r.WStr();
        return r.Short ? null : r.Position;
    }

    /// <summary>C→S 0x98: the Social party description bar (sender 0x14216f9e0, writer 0x141f9baf0): wstr the
    /// description ("" makes the party private or cancels it) and a u8 (client +0xcf20, meaning unknown). Not handled
    /// here; read so the stream goes on.</summary>
    public const ushort PartyDescribeId = 0x98;

    /// <summary>Bytes of a 0x98 at the start of <paramref name="m"/>, or null if it is not complete yet.</summary>
    public static int? PartyDescribeLength(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m);
        r.U16();
        r.WStr();
        r.U8();
        return r.Short ? null : r.Position;
    }

    /// <summary>C→S 0x187 (3 bytes; sender 0x1421718c0, writer 0x141fa20c0): the party options' Map Portal Access, one
    /// bit field byte: bits 0-2 client +0xcd37..+0xcd39, bit 3 access 0, bit 4 access 2, bit 5 access 1 or 2.</summary>
    public const ushort MapPortalAccessId = 0x187;
    public const int MapPortalAccessLength = 3;

    /// <summary>C→S 0xca: the Social panel's status message (sender 0x14216d6f0, writer 0x141f98140): wstr, the text the
    /// client also keeps as its current status (instance +0xcd10). Not stored here; read so the stream goes on.</summary>
    public const ushort StatusMessageId = 0xca;

    /// <summary>Bytes of a 0xca at the start of <paramref name="m"/>, or null if it is not complete yet.</summary>
    public static int? StatusMessageLength(ReadOnlySpan<byte> m)
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

    /// <summary>0xb8 flags bit 1 with bit 0 clear: a player's trade request. The in-game UI (0x140dca680) looks the wstr
    /// name up among the players it knows (instance +0xc748, 0x14216d4f0) and shows "{name} sent you a trade request"
    /// (ClientStrings 0x441, 0x140afc570). Bit 0 alone shows the requester "Waiting for player to accept trade
    /// request..." (ClientStrings 0x2bd) for the u32 other party; both bits open the trade window.</summary>
    public const byte TradeRequestFlag = 0x02;

    public const ushort KnownPlayerId = 0xa7, ForgetPlayerId = 0xa8, PartyInfoId = 0xa9;

    /// <summary>S→C 0x231 (class 0x1435ca0c8, read 0x141f9d510): u16 monster life percent, u8 players in the area, u8
    /// player scaling. The session handler (0x142159988) sets them at +0xd110 (players), +0xd10c (life) and +0xd108
    /// (scaling) and fires the event the party panel's bonus icons listen to (0x14023d460 → 0x14023d610 → 0x14023bbc0: the
    /// loot icon with 2 players or more, the monster life icon above 100). Being checked live (2026-11-04).</summary>
    public const ushort PartyModifiersId = 0x231;
    public static byte[] PartyModifiers(ushort monsterLife, byte players, byte scaling) =>
        new LoginWriter().U16(PartyModifiersId).U16(monsterLife).U8(players).U8(scaling).ToArray();

    /// <summary>S→C 0xa9 (class 0x1435e7740, write 0x1421a7f90, read 0x1421a8220; dispatcher case 0x2a): u32 +0x68, wstr
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
    /// S→C 0xa7 (class 0x1435e6cc8, 0xd0 bytes, write 0x1421a7b10 → 0x141f95e70; dispatcher case 0x28): adds or updates a
    /// player in the client's known-player table (instance +0xc738, an FNV-hashed map keyed by the first wstr, inserted
    /// by 0x14217a4d0 → 0x14217f2b0). The table is what the trade request (0xb8 flag 2, 0x14216d4f0 by character name)
    /// and the party list (0xa9) look players up in; without an entry the request is dropped silently (live 2026-10-28).
    /// Wire: wstr account (key, entry +0x30), wstr (+0x50), u8 flags (bit 0: u16 pair +0xaa/+0xae follows, bit 1: u16
    /// pair +0xb0/+0xb2 follows, bit 2 +0xba: character data, bits 3..6 +0xbb/+0xbc/+0xbd/+0xbe); with bit 2: wstr character
    /// (+0x70), u8 (+0xb9, the level), u8 (+0xb8, the packed class), u16 BE (+0xa8, the WorldAreas HASH16 of the character's
    /// area), u16 BE (+0xac), wstr (+0x90),
    /// u8 (+0xb4), then the pairs; without bit 2 a u64 (+0xa0). Read back by 0x141f96140. The bit 1 pair is the portrait
    /// frame (+0xb0) and the portrait (+0xb2), which the party portraits (0x14020b5a0) and social rows (0x140af7970) draw;
    /// the client's own writer leaves the pair out when both are 0. The Social panel's friend rows (`0x140af7970`) read the
    /// level from +0xb9 ("Level {1}, {0}") and look +0xa8 up in WorldAreas (`0x1422e02b0`) for the area name and its
    /// difficulty icon.
    /// </summary>
    public static byte[] KnownPlayer(string account, string character, int level, byte packedClass = 0, ushort frame = 0, ushort portrait = 0,
        ushort areaHash = 0)
    {
        bool look = frame != 0 || portrait != 0;
        var w = new LoginWriter().U16(KnownPlayerId).WStr(account).WStr("").U8((byte)(look ? 0x06 : 0x04))
            .WStr(character).U8((byte)Math.Clamp(level, 0, byte.MaxValue)).U8(packedClass).U16(areaHash).U16(0).WStr("").U8(0);
        if (look) w.U16(frame).U16(portrait);
        return w.ToArray();
    }
}
