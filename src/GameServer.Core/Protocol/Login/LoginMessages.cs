namespace GameServer.Core.Protocol.Login;

// Login-connection messages after the key exchange. Every message is `u16 id` followed by its fields, with no length
// prefix, so the reader must know each id's layout. Layouts come from the client's serializers (vtable slot 1 writes
// the id and fields, slot 2 reads the fields); see Agents/docs/re/009-login-messages.md for addresses. Field names are ours.
// Where the client's use of a field is unknown, the name says what it is on the wire.

public interface ILoginMessage
{
    ushort MessageId { get; }

    /// <summary>Writes the fields after the id.</summary>
    void WriteFields(LoginWriter w);
}

public static class LoginMessage
{
    public static byte[] Encode(ILoginMessage m)
    {
        var w = new LoginWriter().U16(m.MessageId);
        m.WriteFields(w);
        return w.ToArray();
    }
}

/// <summary>Messages with no fields: S→C 0x01; C→S 0x01, 0x18, 0x19, 0x21, 0x23, 0x39.</summary>
public sealed record EmptyMessage(ushort MessageId) : ILoginMessage
{
    public void WriteFields(LoginWriter w) { }
}

/// <summary>One u16 (class 0x1435ca200, 0x141f99160/0x141f991c0): S→C 0x0b, 0x0d, 0x0f, 0x17 (disconnect reason), 0x1e, 0x44.</summary>
public sealed record U16Message(ushort MessageId, ushort Value) : ILoginMessage
{
    public void WriteFields(LoginWriter w) => w.U16(Value);
    internal static U16Message Read(ushort id, ref LoginReader r) => new(id, r.U16());
}

/// <summary>One u32 (class 0x1431767e8, 0x140911210/0x140911270): S→C 0x1f, 0x20. Also C→S 0x46 (sender 0x140d146c0).</summary>
public sealed record U32Message(ushort MessageId, uint Value) : ILoginMessage
{
    public void WriteFields(LoginWriter w) => w.U32(Value);
    internal static U32Message Read(ushort id, ref LoginReader r) => new(id, r.U32());
}

/// <summary>One u8: C→S 0x3a (0x140d08cb0), 0x42 (0x142147160).</summary>
public sealed record U8Message(ushort MessageId, byte Value) : ILoginMessage
{
    public void WriteFields(LoginWriter w) => w.U8(Value);
    internal static U8Message Read(ushort id, ref LoginReader r) => new(id, r.U8());
}

/// <summary>One wide string (class 0x1435ca068, 0x141f98140/0x141f981a0): S→C 0x24, 0x25, 0x26, 0x28, 0x29; C→S 0x3f, 0x40.</summary>
public sealed record WStringMessage(ushort MessageId, string Text) : ILoginMessage
{
    public void WriteFields(LoginWriter w) => w.WStr(Text);
    internal static WStringMessage Read(ushort id, ref LoginReader r) => new(id, r.WStr());
}

/// <summary>Wide string + u8 (class 0x1435ca710, 0x141f9baf0/0x141f9bb70): C→S 0x0c, 0x2a, 0x2b. The byte is the realm byte | 0x40 for 0x2a/0x2b.</summary>
public sealed record WStringU8Message(ushort MessageId, string Text, byte Value) : ILoginMessage
{
    public void WriteFields(LoginWriter w) => w.WStr(Text).U8(Value);
    internal static WStringU8Message Read(ushort id, ref LoginReader r) => new(id, r.WStr(), r.U8());
}

// ---------------------------------------------------------------- server → client

/// <summary>S→C 0x04 (vtable 0x1435e6568, write 0x1421a1ab0, read 0x1421a1ba0). Result 0 moves the client to state 3 (logged in).</summary>
public sealed record LoginResult(ushort Result, string AccountName, byte[] Digest, byte Flags0, byte Flags1, byte[] Data) : ILoginMessage
{
    public const ushort Id = 0x04;
    public ushort MessageId => Id;
    public void WriteFields(LoginWriter w) => w.U16(Result).WStr(AccountName).Fixed(Digest, 32).U8(Flags0).U8(Flags1).Blob(Data);
    internal static LoginResult Read(ushort id, ref LoginReader r) => new(r.U16(), r.WStr(), r.Fixed(32), r.U8(), r.U8(), r.Blob());
}

/// <summary>S→C 0x11 (vtable 0x1435e6528, 0x1421a1f10/0x1421a1f80): u8 + u16-length blob, parsed by the client into 17 records of 0x80 bytes (0x1421439b0).</summary>
public sealed record Blob11(byte Kind, byte[] Data) : ILoginMessage
{
    public const ushort Id = 0x11;
    public ushort MessageId => Id;
    public void WriteFields(LoginWriter w) => w.U8(Kind).Blob(Data);
    internal static Blob11 Read(ushort id, ref LoginReader r) => new(r.U8(), r.Blob());
}

/// <summary>S→C 0x13 (vtable 0x1435e6588, 0x1421a21c0/0x1421a2240): the answer to C→S 0x12 (create character). The
/// create screen's listener (0x140cf7fb0 at +0xa10) treats 0 as done ("character_creation_done") and goes back to the
/// character list. When the flag is set the client posts to /api/tracking-conversions/.</summary>
public sealed record CreateCharacterResult(ushort Result, bool Tracking) : ILoginMessage
{
    public const ushort Id = 0x13;
    public ushort MessageId => Id;
    public void WriteFields(LoginWriter w) => w.U16(Result).Bool(Tracking);
    internal static CreateCharacterResult Read(ushort id, ref LoginReader r) => new(r.U16(), r.Bool());
}

/// <summary>
/// S→C 0x14 (vtable 0x1435e64a8, 0x1421a2330/0x1421a2490): instance (game server) assignment. AreaHash is looked up in
/// the area table ("Area reported by instance does not exist. Hash was: ").
/// </summary>
public sealed record InstanceInfo(string Host, ushort U16_30, uint U32_34, ushort AreaHash, uint U32_38, byte[] Key64, byte[] Data8, byte U8_86) : ILoginMessage
{
    public const ushort Id = 0x14;
    public ushort MessageId => Id;
    public void WriteFields(LoginWriter w) =>
        w.Str(Host).U16(U16_30).U32(U32_34).U16(AreaHash).U32(U32_38).Fixed(Key64, 64).Fixed(Data8, 8).U8(U8_86);
    internal static InstanceInfo Read(ushort id, ref LoginReader r) =>
        new(r.Str(), r.U16(), r.U32(), r.U16(), r.U32(), r.Fixed(64), r.Fixed(8), r.U8());
}

/// <summary>Element of S→C 0x15 (0x50-byte struct, 0x14250ddb0/0x14250e030). Wire order differs from struct order.</summary>
public sealed record ListEntry15(string Name, string Text, byte B49, byte B4d, byte B48, uint U40, byte B4a, ushort U44, ushort U46, byte B4b, byte B4c)
{
    internal void Write(LoginWriter w) => w.WStr(Name).WStr(Text).U8(B49).U8(B4d).U8(B48).U32(U40).U8(B4a).U16(U44).U16(U46).U8(B4b).U8(B4c);
    internal static ListEntry15 Read(ref LoginReader r) =>
        new(r.WStr(), r.WStr(), r.U8(), r.U8(), r.U8(), r.U32(), r.U8(), r.U16(), r.U16(), r.U8(), r.U8());
}

/// <summary>S→C 0x15 (vtable 0x1435e64e8, 0x1421a1d60/0x1421a1e00): u8 count + entries, two bytes, then a blob parsed like 0x11.</summary>
public sealed record List15(IReadOnlyList<ListEntry15> Entries, byte B28, byte B29, byte[] Data) : ILoginMessage
{
    public const ushort Id = 0x15;
    public ushort MessageId => Id;
    public void WriteFields(LoginWriter w)
    {
        w.U8(checked((byte)Entries.Count));
        foreach (var e in Entries) e.Write(w);
        w.U8(B28).U8(B29).Blob(Data);
    }
    internal static List15 Read(ushort id, ref LoginReader r)
    {
        int n = r.U8();
        var list = new List<ListEntry15>(n);
        for (int i = 0; i < n && !r.Short; i++) list.Add(ListEntry15.Read(ref r));
        return new(list, r.U8(), r.U8(), r.Blob());
    }
}

/// <summary>
/// A league: element of S→C 0x1a / 0x1b (0xe8-byte struct, read by 0x1424f2e40, constructed by 0x1408c0c90). Static
/// analysis 2026-10-18 (Agents/docs/protocol/01-login.md §7):
/// <list type="bullet">
/// <item>Name (+0x10, view at +0x00): what C→S 0x12 sends back and what 0x15's league text is compared with.</item>
/// <item>SsfParent (+0x60), HardcoreParent (+0x80), RuthlessParent (+0xa0): a variant names the league it is the SSF /
/// Hardcore / Ruthless form of. The create screen (0x140cccff0) shows one card per main league and its Hardcore, Solo
/// Self-Found and Ruthless check boxes switch to the variants found through these links (combinations chain: SSF
/// Hardcore links SsfParent to the Hardcore league).</item>
/// <item>Flags0 (+0xe0): bit 0 private (password box), bit 1 hardcore (confirmation, hardcore banner), bit 4 shown only
/// while +0xc8 is in the future, bit 7 main league (gets a card). Flags1 (+0xe1): bit 1 variant (no card of its own),
/// bit 5 hidden on some platforms, bit 6 ruthless (confirmation). Flags2 (+0xe2): bit 0 counted into login+0xe58.</item>
/// <item>StartStateSet (+0xc0): CharacterStartStateSet row for new characters. C1 (+0xc1): 1 scrolls to the league's card.</item>
/// </list>
/// Description (+0x40, view +0x30): the card text, a ClientStrings-style key when it starts with '#'; empty crashes the
/// client ("invalid string position", 0x140cc9370). The times (+0xd0, +0xd8, +0xc8, little-endian) are unconfirmed;
/// RawC4 defaults to 0xd1.
/// </summary>
public sealed record LeagueEntry(string Name, string Description, string SsfParent, string HardcoreParent, string RuthlessParent,
    ulong RawD0, ulong RawD8, ulong RawC8, byte Flags0, byte Flags1, byte Flags2, byte StartStateSet, byte C1, uint RawC4)
{
    public const byte Private = 0x01, Hardcore = 0x02, Timed = 0x10, Main = 0x80;
    public const byte Variant = 0x02, PlatformHidden = 0x20, Ruthless = 0x40;

    internal void Write(LoginWriter w) =>
        w.WStr(Name).WStr(Description).WStr(SsfParent).WStr(HardcoreParent).WStr(RuthlessParent).RawU64(RawD0).RawU64(RawD8).RawU64(RawC8)
         .U8(Flags0).U8(Flags1).U8(Flags2).U8(StartStateSet).U8(C1).RawU32(RawC4);
    internal static LeagueEntry Read(ref LoginReader r) =>
        new(r.WStr(), r.WStr(), r.WStr(), r.WStr(), r.WStr(), r.RawU64(), r.RawU64(), r.RawU64(),
            r.U8(), r.U8(), r.U8(), r.U8(), r.U8(), r.RawU32());

    internal static void WriteList(LoginWriter w, IReadOnlyList<LeagueEntry> list)
    {
        w.U32((uint)list.Count);
        foreach (var e in list) e.Write(w);
    }

    internal static List<LeagueEntry> ReadList(ref LoginReader r)
    {
        uint n = r.U32();
        var list = new List<LeagueEntry>();
        for (uint i = 0; i < n && !r.Short; i++) list.Add(Read(ref r));
        return list;
    }
}

/// <summary>S→C 0x1a (vtable 0x1435e6488, 0x1421a2040/0x1421a20b0): result + the leagues with progress to migrate from,
/// the answer to C→S 0x19 (listener 0x140d171a0 at +0x810; a non-zero result shows a "League Migrations" error, an
/// empty list "There are no available leagues to migrate progress from.").</summary>
public sealed record LeaguesWithProgress(ushort Result, IReadOnlyList<LeagueEntry> Leagues) : ILoginMessage
{
    public const ushort Id = 0x1a;
    public ushort MessageId => Id;
    public void WriteFields(LoginWriter w) { w.U16(Result); LeagueEntry.WriteList(w, Leagues); }
    internal static LeaguesWithProgress Read(ushort id, ref LoginReader r) => new(r.U16(), LeagueEntry.ReadList(ref r));
}

/// <summary>
/// S→C 0x1b (vtable 0x1435e64c8, 0x1421a2690/0x1421a2720): the league list (login+0xe88), which the character screen
/// copies (0x140d125c0) and the create-character screen offers; with no leagues its Create button stays disabled
/// (0x140cf9760). ServerTime is a raw little-endian i64 (the client stores it minus _time64). Flags bit 0 → +0x30, bit 1
/// → +0x31 (append to the existing list instead of replacing).
/// </summary>
public sealed record LeagueList(long ServerTime, IReadOnlyList<LeagueEntry> Leagues, bool Flag0, bool Append) : ILoginMessage
{
    public const ushort Id = 0x1b;
    public ushort MessageId => Id;
    public void WriteFields(LoginWriter w)
    {
        w.RawU64((ulong)ServerTime);
        LeagueEntry.WriteList(w, Leagues);
        w.U8((byte)((Flag0 ? 1 : 0) | (Append ? 2 : 0)));
    }
    internal static LeagueList Read(ushort id, ref LoginReader r)
    {
        long t = (long)r.RawU64();
        var list = LeagueEntry.ReadList(ref r);
        byte f = r.U8();
        return new(t, list, (f & 1) != 0, (f & 2) != 0);
    }
}

/// <summary>S→C 0x3c (vtable 0x1435ca2c0, 0x141f98f90/0x141f99010): two u32; the second is stored at +0xe60.</summary>
public sealed record TwoU32(uint A, uint B) : ILoginMessage
{
    public const ushort Id = 0x3c;
    public ushort MessageId => Id;
    public void WriteFields(LoginWriter w) => w.U32(A).U32(B);
    internal static TwoU32 Read(ushort id, ref LoginReader r) => new(r.U32(), r.U32());
}

/// <summary>S→C 0x3e (vtable 0x1435e6508, 0x141f9c9d0/0x1421a2860): u16 + bool.</summary>
public sealed record U16Bool(ushort Value, bool Flag) : ILoginMessage
{
    public const ushort Id = 0x3e;
    public ushort MessageId => Id;
    public void WriteFields(LoginWriter w) => w.U16(Value).Bool(Flag);
    internal static U16Bool Read(ushort id, ref LoginReader r) => new(r.U16(), r.Bool());
}

/// <summary>S→C 0x41 (vtable 0x1435cabf0, 0x141fa62c0/0x141fa6350): one narrow string.</summary>
public sealed record StringMessage(string Text) : ILoginMessage
{
    public const ushort Id = 0x41;
    public ushort MessageId => Id;
    public void WriteFields(LoginWriter w) => w.Str(Text);
    internal static StringMessage Read(ushort id, ref LoginReader r) => new(r.Str());
}

/// <summary>S→C 0x43 (vtable 0x1435ca048, 0x141fa5e60/0x141fa5ee0): u8 + wide string.</summary>
public sealed record U8WString(byte Value, string Text) : ILoginMessage
{
    public const ushort Id = 0x43;
    public ushort MessageId => Id;
    public void WriteFields(LoginWriter w) => w.U8(Value).WStr(Text);
    internal static U8WString Read(ushort id, ref LoginReader r) => new(r.U8(), r.WStr());
}

/// <summary>S→C 0x47 (vtable 0x1435e6548, 0x1421a2950/0x1421a2b30): u32, u16, then a u8 count of narrow strings.</summary>
public sealed record StringList47(uint A, ushort B, IReadOnlyList<string> Items) : ILoginMessage
{
    public const ushort Id = 0x47;
    public ushort MessageId => Id;
    public void WriteFields(LoginWriter w)
    {
        w.U32(A).U16(B).U8(checked((byte)Items.Count));
        foreach (var s in Items) w.Str(s);
    }
    internal static StringList47 Read(ushort id, ref LoginReader r)
    {
        uint a = r.U32(); ushort b = r.U16(); int n = r.U8();
        var list = new List<string>(n);
        for (int i = 0; i < n && !r.Short; i++) list.Add(r.Str());
        return new(a, b, list);
    }
}

// ---------------------------------------------------------------- client → server

/// <summary>
/// C→S 0x03 (vtable 0x1435e3658, write 0x142142fb0, senders 0x142145360 and 0x140d060a0): account login.
/// Version is 2. MachineIdA comes from 0x1421452c0 and MachineIdB from 0x1429a4490 (32-byte client identifiers),
/// PasswordDigest is 32 bytes (the client hashes passwords with SHA-256 over UTF-16LE, 0x140e21b30). Realm is the
/// realm byte (0x143a6b89c) | 0x40. Flag94 requests Remember Password; Flag95 means PasswordDigest
/// carries the previously returned LoginResult.Digest saved credential rather than a password hash.
/// </summary>
public sealed record AccountLogin(uint Version, string Account, byte[] MachineIdA, byte[] PasswordDigest, byte[] MachineIdB,
    bool Flag94, bool Flag95, byte Realm, byte B97) : ILoginMessage
{
    public const ushort Id = 0x03;
    public ushort MessageId => Id;
    public void WriteFields(LoginWriter w) =>
        w.U32(Version).Str(Account).Fixed(MachineIdA, 32).Fixed(PasswordDigest, 32).Fixed(MachineIdB, 32)
         .Bool(Flag94).Bool(Flag95).U8(Realm).U8(B97);
    internal static AccountLogin Read(ushort id, ref LoginReader r) =>
        new(r.U32(), r.Str(), r.Fixed(32), r.Fixed(32), r.Fixed(32), r.Bool(), r.Bool(), r.U8(), r.U8());
}

/// <summary>C→S 0x05 (vtable 0x1435e3568, 0x142142b10/0x142142be0, sender 0x1421456f0): login with a binary token.</summary>
public sealed record TokenLogin(byte Realm, byte[] MachineIdB, byte[] MachineIdA, byte B69, byte[] Token) : ILoginMessage
{
    public const ushort Id = 0x05;
    public ushort MessageId => Id;
    public void WriteFields(LoginWriter w) => w.U8(Realm).Fixed(MachineIdB, 32).Fixed(MachineIdA, 32).U8(B69).Blob(Token);
    internal static TokenLogin Read(ushort id, ref LoginReader r) => new(r.U8(), r.Fixed(32), r.Fixed(32), r.U8(), r.Blob());
}

/// <summary>C→S 0x06 / 0x07 (vtable 0x1435e3540, 0x1421428b0/0x1421429a0, senders 0x1421454f0 / 0x1421458e0): login with a string ticket.</summary>
public sealed record TicketLogin(ushort MessageId, string Ticket, byte Realm, byte[] MachineIdB, byte[] MachineIdA, byte B71) : ILoginMessage
{
    public void WriteFields(LoginWriter w) => w.Str(Ticket).U8(Realm).Fixed(MachineIdB, 32).Fixed(MachineIdA, 32).U8(B71);
    internal static TicketLogin Read(ushort id, ref LoginReader r) => new(id, r.Str(), r.U8(), r.Fixed(32), r.Fixed(32), r.U8());
}

/// <summary>C→S 0x08 (sender 0x140d5db80): u32 + 8 raw bytes. Sent through the object at global+0x350, not the login object (+0x358).</summary>
public sealed record U32Raw64(uint A, ulong RawB) : ILoginMessage
{
    public const ushort Id = 0x08;
    public ushort MessageId => Id;
    public void WriteFields(LoginWriter w) => w.U32(A).RawU64(RawB);
    internal static U32Raw64 Read(ushort id, ref LoginReader r) => new(r.U32(), r.RawU64());
}

/// <summary>C→S 0x0a (vtable 0x1435e3630, 0x142143340/0x1421433a0, sender 0x140cf30c0): two SHA-256 digests of UTF-16LE strings.</summary>
public sealed record TwoDigests(byte[] DigestA, byte[] DigestB) : ILoginMessage
{
    public const ushort Id = 0x0a;
    public ushort MessageId => Id;
    public void WriteFields(LoginWriter w) => w.Fixed(DigestA, 32).Fixed(DigestB, 32);
    internal static TwoDigests Read(ushort id, ref LoginReader r) => new(r.Fixed(32), r.Fixed(32));
}

/// <summary>C→S 0x0e (vtable 0x1435cb2d0, 0x141f99580/0x141f99600, sender 0x142147040): realm byte + u32.</summary>
public sealed record U8U32(byte Realm, uint Value) : ILoginMessage
{
    public const ushort Id = 0x0e;
    public ushort MessageId => Id;
    public void WriteFields(LoginWriter w) => w.U8(Realm).U32(Value);
    internal static U8U32 Read(ushort id, ref LoginReader r) => new(r.U8(), r.U32());
}

/// <summary>C→S 0x12 (vtable 0x1435e35c0, 0x1421434a0/0x142143580, sender 0x142146de0, called by the create screen's
/// 0x140cf5e60): create a character. Name is the name box, League the chosen <see cref="LeagueEntry.Name"/>. The two
/// u32 were 0 and S78 the chosen CharacterStartStates.dat Id ("DexDefault" for a Ranger, live 2026-10-18).
/// PasswordDigest is SHA-256 of the UTF-16LE league password (private leagues), or zeros.</summary>
public sealed record CreateCharacter(string Name, string League, uint U50, uint U54, string S78, byte[] PasswordDigest) : ILoginMessage
{
    public const ushort Id = 0x12;
    public ushort MessageId => Id;
    public void WriteFields(LoginWriter w) => w.WStr(Name).WStr(League).U32(U50).U32(U54).WStr(S78).Fixed(PasswordDigest, 32);
    internal static CreateCharacter Read(ushort id, ref LoginReader r) => new(r.WStr(), r.WStr(), r.U32(), r.U32(), r.WStr(), r.Fixed(32));
}

/// <summary>C→S 0x1c (sender 0x1421473d0): two u16; the second defaults to the client's count at +0xe58.</summary>
public sealed record TwoU16(ushort A, ushort B) : ILoginMessage
{
    public const ushort Id = 0x1c;
    public ushort MessageId => Id;
    public void WriteFields(LoginWriter w) => w.U16(A).U16(B);
    internal static TwoU16 Read(ushort id, ref LoginReader r) => new(r.U16(), r.U16());
}

/// <summary>C→S 0x22 (vtable 0x1435e3520, 0x142143750, sender 0x142147490): 5 raw bytes decoded from a 10-hex-digit string.</summary>
public sealed record HexCode(byte[] Code) : ILoginMessage
{
    public const ushort Id = 0x22;
    public ushort MessageId => Id;
    public void WriteFields(LoginWriter w) => w.Fixed(Code, 5);
    internal static HexCode Read(ushort id, ref LoginReader r) => new(r.Fixed(5));
}

/// <summary>C→S 0x2c (vtable 0x1435e3610, 0x142142e30/0x142142ef0, sender 0x140d01b40): Epic (EOS) login: wide string, realm, token blob.</summary>
public sealed record EpicLogin(string Name, byte Realm, byte[] Token) : ILoginMessage
{
    public const ushort Id = 0x2c;
    public ushort MessageId => Id;
    public void WriteFields(LoginWriter w) => w.WStr(Name).U8(Realm).Blob(Token);
    internal static EpicLogin Read(ushort id, ref LoginReader r) => new(r.WStr(), r.U8(), r.Blob());
}

/// <summary>C→S 0x3d (vtable 0x1435e35a0, 0x142143830/0x1421438c0, sender 0x1421471e0): two wide strings + bool.</summary>
public sealed record TwoWStringsBool(string A, string B, bool Flag) : ILoginMessage
{
    public const ushort Id = 0x3d;
    public ushort MessageId => Id;
    public void WriteFields(LoginWriter w) => w.WStr(A).WStr(B).Bool(Flag);
    internal static TwoWStringsBool Read(ushort id, ref LoginReader r) => new(r.WStr(), r.WStr(), r.Bool());
}

public enum LoginDirection { ServerToClient, ClientToServer }

/// <summary>Id → layout tables for both directions and a framing-free stream decoder.</summary>
public static class LoginCodec
{
    private delegate ILoginMessage Reader(ushort id, ref LoginReader r);

    private static ILoginMessage ReadKeyExchange(ushort id, ref LoginReader r) => new KeyExchange(r.Blob(), r.Blob());
    private static ILoginMessage ReadEmpty(ushort id, ref LoginReader r) => new EmptyMessage(id);

    // Mirrors the client's factory 0x1421a2cc0.
    private static readonly Dictionary<ushort, Reader> ServerToClient = new()
    {
        [0x01] = ReadEmpty, [0x02] = ReadKeyExchange, [0x04] = LoginResult.Read,
        [0x0b] = U16Message.Read, [0x0d] = U16Message.Read, [0x0f] = U16Message.Read,
        [0x11] = Blob11.Read, [0x13] = CreateCharacterResult.Read, [0x14] = InstanceInfo.Read, [0x15] = List15.Read,
        [0x17] = U16Message.Read, [0x1a] = LeaguesWithProgress.Read, [0x1b] = LeagueList.Read, [0x1e] = U16Message.Read,
        [0x1f] = U32Message.Read, [0x20] = U32Message.Read,
        [0x24] = WStringMessage.Read, [0x25] = WStringMessage.Read, [0x26] = WStringMessage.Read,
        [0x28] = WStringMessage.Read, [0x29] = WStringMessage.Read,
        [0x3c] = TwoU32.Read, [0x3e] = U16Bool.Read, [0x41] = StringMessage.Read, [0x43] = U8WString.Read,
        [0x44] = U16Message.Read, [0x47] = StringList47.Read,
    };

    // Collected from the client's senders (writes to the login socket at +0xdd0). 0x01 (no fields) was seen live after
    // 0x18 on the character screen (2026-10-16), probably a keepalive; it shares class 0x1431768d8 with S→C 0x01.
    private static readonly Dictionary<ushort, Reader> ClientToServer = new()
    {
        [0x01] = ReadEmpty, [0x02] = ReadKeyExchange, [0x03] = AccountLogin.Read, [0x05] = TokenLogin.Read,
        [0x06] = TicketLogin.Read, [0x07] = TicketLogin.Read, [0x08] = U32Raw64.Read, [0x0a] = TwoDigests.Read,
        [0x0c] = WStringU8Message.Read, [0x0e] = U8U32.Read, [0x12] = CreateCharacter.Read,
        [0x18] = ReadEmpty, [0x19] = ReadEmpty, [0x1c] = TwoU16.Read, [0x21] = ReadEmpty, [0x22] = HexCode.Read,
        [0x23] = ReadEmpty, [0x2a] = WStringU8Message.Read, [0x2b] = WStringU8Message.Read, [0x2c] = EpicLogin.Read,
        [0x39] = ReadEmpty, [0x3a] = U8Message.Read, [0x3d] = TwoWStringsBool.Read,
        [0x3f] = WStringMessage.Read, [0x40] = WStringMessage.Read, [0x42] = U8Message.Read, [0x46] = U32Message.Read,
    };

    public static bool IsKnown(LoginDirection dir, ushort id) => Table(dir).ContainsKey(id);

    /// <summary>
    /// Decodes one message from the start of <paramref name="buf"/>. Returns false if more bytes are needed. An unknown
    /// id throws: without a length prefix the rest of the stream cannot be resynchronized.
    /// </summary>
    public static bool TryDecode(LoginDirection dir, ReadOnlySpan<byte> buf, out ILoginMessage? message, out int consumed)
    {
        message = null; consumed = 0;
        var r = new LoginReader(buf);
        ushort id = r.U16();
        if (r.Short) return false;
        if (!Table(dir).TryGetValue(id, out var read))
            throw new InvalidDataException($"unknown {dir} login message 0x{id:x4}");
        var m = read(id, ref r);
        if (r.Short) return false;
        message = m; consumed = r.Position;
        return true;
    }

    private static Dictionary<ushort, Reader> Table(LoginDirection dir) => dir == LoginDirection.ServerToClient ? ServerToClient : ClientToServer;
}
