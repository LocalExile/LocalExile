using System.Buffers.Binary;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// Guild lifecycle, membership and metadata messages on the instance connection
/// (3.29.3c dispatcher <c>0x142150ae0</c> cases <c>0x5c</c>–<c>0x62</c>).
/// </summary>
public static class InstanceGuilds
{
    public const ushort
        CreateId = 0x0140,
        InviteId = 0x0141,
        AcceptInviteId = 0x0142,
        DeclineInviteId = 0x0143,
        SetMemberRankId = 0x0144,
        RemoveMemberId = 0x0145,
        InviteNotifyId = 0x0146,
        InviteExpiredId = 0x0147,
        GuildInfoId = 0x0148,
        GuildLeftId = 0x0149,
        MemberUpsertId = 0x014a,
        MemberRemoveId = 0x014b,
        DetailsChangedId = 0x014c,
        SetStatusId = 0x014d,
        RenameId = 0x014e,
        SetTagId = 0x014f;

    public const int AcceptInviteLength = 4, DeclineInviteLength = 4;

    /// <summary>Guild member rank byte values (3.29.3c <c>C→S 0x0144</c> callers <c>0x140ab95b9</c>, <c>0x140ab9609</c>,
    /// <c>0x140ab98b9</c>, <c>0x140ac0490</c> and <c>ClientStrings</c> <c>GuildMemberType*</c>).</summary>
    public const byte
        RankInvited = 0,
        RankMember = 1,
        RankOfficer = 2,
        RankLeader = 3,
        RankInitiate = 4;

    public sealed record Member(string Account, ulong JoinedAt, byte Rank);

    /// <summary>Reads a single-WStr guild request (<c>0x0140</c>, <c>0x0141</c>, <c>0x0145</c>, <c>0x014d</c>, <c>0x014e</c>, <c>0x014f</c>; writer <c>0x141f98140</c>).</summary>
    public static string ReadText(ReadOnlySpan<byte> m) => new LoginReader(m[2..]).WStr();

    /// <summary>Reads a <c>u16 BE</c> guild invite index (<c>0x0142</c> accept, <c>0x0143</c> decline; writer <c>0x141f99160</c>).</summary>
    public static ushort ReadInviteIndex(ReadOnlySpan<byte> m) => BinaryPrimitives.ReadUInt16BigEndian(m[2..]);

    /// <summary>Reads <c>C→S 0x0144</c> (writer <c>0x141f9baf0</c>, sender <c>0x142170d30</c>): <c>WStr account, u8 rank</c>.</summary>
    public static (string Account, byte Rank) ReadSetMemberRank(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m[2..]);
        return (r.WStr(), r.U8());
    }

    /// <summary>S→C 0x0146 (reader <c>0x1421adbe0</c>, case <c>0x5c</c>): incoming guild invite.
    /// Wire: <c>u16 BE index</c>, <c>WStr name</c>, <c>WStr tag</c>, <c>WStr status</c>, <c>raw u64 createdAt</c>, <c>WStr inviter</c>.</summary>
    public static byte[] InviteNotify(ushort index, string name, string tag, string status, ulong createdAt, string inviter) =>
        new LoginWriter().U16(InviteNotifyId).U16(index).WStr(name).WStr(tag).WStr(status).RawU64(createdAt).WStr(inviter).ToArray();

    /// <summary>S→C 0x0147 (reader <c>0x141f991c0</c>, case <c>0x5d</c>): remove/expire a pending guild invite by index.</summary>
    public static byte[] InviteExpired(ushort index) =>
        new LoginWriter().U16(InviteExpiredId).U16(index).ToArray();

    /// <summary>S→C 0x0148 (reader <c>0x1421adf30</c>, member list <c>0x1421c1400</c>, case <c>0x5e</c>): full guild snapshot.
    /// Wire: <c>WStr name</c>, <c>WStr tag</c>, <c>WStr status</c>, <c>raw u64 createdAt</c>, <c>u16 BE count</c>,
    /// <c>count × (WStr account, raw u64 joinedAt, u8 rank)</c>.</summary>
    public static byte[] GuildInfo(string name, string tag, string status, ulong createdAt, IReadOnlyList<Member> members)
    {
        if (members.Count > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(members));
        var w = new LoginWriter().U16(GuildInfoId).WStr(name).WStr(tag).WStr(status).RawU64(createdAt).U16((ushort)members.Count);
        foreach (var member in members)
            w.WStr(member.Account).RawU64(member.JoinedAt).U8(member.Rank);
        return w.ToArray();
    }

    /// <summary>S→C 0x0149 (reader <c>0x1400f1180</c>, case <c>0x5f</c>): cleared current guild membership (id only).</summary>
    public static byte[] GuildLeft() => [GuildLeftId >> 8, GuildLeftId & 0xff];

    /// <summary>S→C 0x014a (reader <c>0x1421ae1c0</c>, case <c>0x60</c>): add or update a guild member.
    /// Wire: <c>WStr account</c>, <c>raw u64 joinedAt</c>, <c>u8 rank</c>.</summary>
    public static byte[] MemberUpsert(string account, ulong joinedAt, byte rank) =>
        new LoginWriter().U16(MemberUpsertId).WStr(account).RawU64(joinedAt).U8(rank).ToArray();

    /// <summary>S→C 0x014b (reader <c>0x141f981a0</c>, case <c>0x61</c>): remove a member from the guild roster.</summary>
    public static byte[] MemberRemove(string account) =>
        new LoginWriter().U16(MemberRemoveId).WStr(account).ToArray();

    /// <summary>S→C 0x014c (reader <c>0x1421ae400</c>, case <c>0x62</c>): updated guild details (name, tag, status, createdAt).</summary>
    public static byte[] DetailsChanged(string name, string tag, string status, ulong createdAt) =>
        new LoginWriter().U16(DetailsChangedId).WStr(name).WStr(tag).WStr(status).RawU64(createdAt).ToArray();
}
