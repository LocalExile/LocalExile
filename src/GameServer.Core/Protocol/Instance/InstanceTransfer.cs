using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// Changing area through an area transition, and the instance chooser (Agents/docs/protocol/09-area-change.md).
/// <para>Flow: C→S 0x24 (use skill on the area transition object) → S→C 0x2b2 (change area: the client shows the loading screen and
/// answers C→S 0x2cb, built by 0x142148690) → S→C 0x27 (the destination instance's address, ticket and key, on the
/// current instance connection) → the client connects to it. In game nothing listens to login 0x14 (the login
/// connection's listener list at +0x298 is empty), so an area change cannot come from Auth.
/// With Ctrl held the 0x24 flags carry 0x0004, and the server answers 0xae (instance list) instead, which opens the
/// client's InstanceChooserWindow (ShowForTransition(area, vector&lt;ClientInstanceListEntry&gt;, bool)).</para>
/// </summary>
public static class InstanceTransfer
{
    public const ushort UseSkillOnObjectId = 0x24, ChangeAreaId = 0x2b2, InstanceListId = 0xae, LeaveAreaAckId = 0x2cb,
        ConnectInstanceId = 0x27, ChooseInstanceId = 0xaf;

    /// <summary>C→S 0xaf (3 bytes): the instance chooser's pick, u8 row of the last 0xae list, or <see cref="NewInstanceRow"/>
    /// for the "New" row (captured live 2026-10-16: 00AFFF on New with an empty list).</summary>
    public const byte NewInstanceRow = 0xff;

    /// <summary>0x24 flag bit set when Ctrl is held (seen as 0x040c vs 0x0408 on area transition clicks).</summary>
    public const ushort CtrlFlag = 0x0004;

    /// <summary>0x24 flag bit set while the inventory panel is open (0x040a vs 0x0408 on ground item clicks, live
    /// 2026-10-18): the picked-up item goes to the cursor instead of the inventory.</summary>
    public const ushort InventoryOpenFlag = 0x0002;

    /// <summary>
    /// C→S 0x24 (12 bytes, 0x25's sibling aimed at an object): u32 target object id, u16 skill id (0x0266 = interact,
    /// seen on area transitions), u16 sequence, u16 flags (0x0408; | 0x0004 with Ctrl).
    /// </summary>
    public sealed record UseSkillOnObject(uint TargetId, ushort SkillId, ushort Sequence, ushort Flags)
    {
        public bool Ctrl => (Flags & CtrlFlag) != 0;

        /// <summary>The use's start (0x1ab) has already gone to the client: a lockstep client's frame pass sends it before
        /// the handler runs, so the handler sends none of its own. A second start put the client back to its from cell
        /// and had it path again (live 2026-11-03: two cells and a frame lost on every pickup).</summary>
        public bool Started { get; init; }
    }

    public static UseSkillOnObject ReadUseSkillOnObject(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m[2..]);
        return new(r.U32(), r.U16(), r.U16(), r.U16());
    }

    /// <summary>
    /// S→C 0x2b2 (class 0x1435bfee0, write 0x141f92980; dispatcher case 0xe8): u16 destination WorldAreas.HASH16, u16
    /// LoadingScreenOverrides row (1-based, 0 = the area's default). The handler notifies the loading-screen listeners
    /// (+0x6d70) and calls 0x142148690, which sends C→S 0x2cb once and flushes. An unknown hash is fatal ("Instance sent
    /// change area notification that does not exist").
    /// </summary>
    public static byte[] ChangeArea(ushort destinationHash16, ushort loadingScreen = 0) =>
        new LoginWriter().U16(ChangeAreaId).U16(destinationHash16).U16(loadingScreen).ToArray();

    /// <summary>
    /// S→C 0x27 (class vtable 0x1435dc280, write 0x14219ddf0, read 0x14219df90; dispatcher case 0xe): connect to another
    /// instance. Fields in wire order: str host (+0x10), u16 port (+0x86), u32 IPv4 (+0x30), u32 (+0x34, not passed on),
    /// u16 area HASH16 (+0x84), u32 ticket (+0x38), u8 (+0x88), u8 (+0x89), 64-byte instance key (+0x3c), 8 bytes
    /// (+0x7c). The handler builds the same instance-details object as login 0x14 (0x142173680: host, port, ip, ticket,
    /// area, key, data8, u8 +0x89) and hands it to the game state's listener at instance+0xa00, which calls
    /// 0x140d59b70 ("Got Instance Details from login server") and opens the new instance connection.
    /// </summary>
    public static byte[] ConnectInstance(string host, ushort port, uint ticket, ushort areaHash16, byte[] key64)
    {
        uint ip = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(System.Net.IPAddress.Parse(host).GetAddressBytes());
        return new LoginWriter().U16(ConnectInstanceId).Str(host).U16(port).U32(ip).U32(0).U16(areaHash16).U32(ticket)
            .U8(0).U8(0).Fixed(key64, 64).Fixed(new byte[8], 8).ToArray();
    }

    /// <summary>One row of the instance chooser (ClientInstanceListEntry, 0x28 bytes in the client). Live 2026-10-16: the
    /// u64 shows as "Created" (unix seconds, local time) and the u32 as "Time Left" (seconds); an empty name list shows
    /// "None". The client answers with the row index (0xaf), so no instance id is sent.</summary>
    public sealed record InstanceListEntry(ulong CreatedUnix, uint SecondsLeft, byte U8, IReadOnlyList<string> Players);

    /// <summary>
    /// S→C 0xae (class 0x1435dca40, write 0x1421a2300; dispatcher case 0x2e → listeners +0x6cf0): u16 area HASH16, u8
    /// (+0x14), u8 n entries; each: raw u64 (+0x00, created, unix seconds), u32 BE (+0x08, seconds left), u8 (+0x0c),
    /// u8 m + m × wstr (+0x10, player names).
    /// </summary>
    public static byte[] InstanceList(ushort areaHash16, IReadOnlyList<InstanceListEntry> entries, byte u14 = 0)
    {
        var w = new LoginWriter().U16(InstanceListId).U16(areaHash16).U8(u14).U8(checked((byte)entries.Count));
        foreach (var e in entries)
        {
            w.RawU64(e.CreatedUnix).U32(e.SecondsLeft).U8(e.U8).U8(checked((byte)e.Players.Count));
            foreach (var p in e.Players) w.WStr(p);
        }
        return w.ToArray();
    }
}
