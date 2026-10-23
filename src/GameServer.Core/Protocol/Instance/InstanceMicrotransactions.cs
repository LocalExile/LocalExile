using System.Buffers.Binary;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// The owned-microtransaction list the client's store and cosmetics panel are fed from
/// (Agents/docs/protocol/22-microtransactions.md). S→C 0x1f3 is the full list: the client appends every record to its
/// model (<c>hub+0xd600</c>), fires the panel's add/update events and builds the rows itself.
/// </summary>
public static class InstanceMicrotransactions
{
    public const ushort PurchasedId = 0x1f3;
    public const ushort AddedId = 0x1f4;

    /// <summary>
    /// One owned microtransaction, in wire order. <paramref name="MtxTypeHash"/> lands at +0x04 and is the key the
    /// panel's row build resolves in the UI context's catalogue (<c>0x142222170</c>, a <c>MtxTypes</c> HASH32): a value
    /// that is not in it leaves a null and the row layout faults (live 2026-10-22, §4). <paramref name="Id"/> is the
    /// record's own id at +0x00, the key the model upserts and removes by, so it has to be unique per record.
    /// <paramref name="Qword"/> lands at +0x10 (carried into the row's display item) and
    /// <paramref name="Flags8"/>/<paramref name="Flags9"/> at +0x08/+0x09 (unknown).
    /// </summary>
    public sealed record Record(uint MtxTypeHash, uint Id, string Name = "", string Description = "",
        ulong Qword = 0, byte Flags8 = 0, byte Flags9 = 0);

    /// <summary>The flags byte of a message: bit 0 → +0x70, bit 1 → +0x71, bit 2 → +0x72. A fresh object has 1.</summary>
    public const byte DefaultFlags = 1;

    /// <summary>
    /// S→C 0x1f3 (class 0x1435dc3e0, read 0x1421aa830, dispatcher case 0x92): the owned list. The three further lists
    /// the reader also takes (message +0x28/+0x40/+0x58, §3 of the doc) are sent empty — their element semantics are not
    /// known — and the flags byte keeps the client's own default.
    /// </summary>
    public static byte[] Purchased(IReadOnlyList<Record> records)
    {
        if (records.Count > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(records), records.Count, "0x1f3 counts records in one byte");
        var w = new LoginWriter().U16(PurchasedId).U8((byte)records.Count);
        foreach (var r in records) WriteRecord(w, r);
        return w.U8(0).U8(0).U8(0).U8(DefaultFlags).ToArray();
    }

    static void WriteRecord(LoginWriter w, Record r) =>
        w.U8(r.Flags8)
            .U32(r.MtxTypeHash)
            .U32(r.Id)
            .RawU64(r.Qword)
            .WStr(r.Name)
            .WStr(r.Description)
            .U8(r.Flags9)
            .U8(0)   // no list at +0x58 (+0x118 = 0)
            .U8(0);  // no variant at +0x120 (+0x188 = 0)

    /// <summary>
    /// The 0x1f4 flags byte: which of the hub's three lists the message replaces (dispatcher case 0x93 reads the byte as
    /// <c>+0x28</c> and <c>+0x29</c>, bit 0 and bit 1 in order, so the branches are exclusive — one message fills one
    /// list). <see cref="Records"/> is the list the Microtransactions panel shows; <see cref="StoreOwned"/> is the store's
    /// owned list, which is what puts <c>ALREADY OWNED</c> on a shop tile (live 2026-10-22, §8);
    /// <see cref="StoreSecond"/> fills the store's second list, which no visible page has shown yet.
    /// </summary>
    public const byte Records = 0;
    public const byte StoreOwned = 1;
    public const byte StoreSecond = 3;

    /// <summary>
    /// S→C 0x1f4 (class 0x1435dc580, read 0x1421aac00, dispatcher case 0x93): the record list again, but this one
    /// <b>replaces</b> the list <paramref name="flags"/> selects and then fires that list's event — for
    /// <see cref="Records"/> the panel's reset, which is what makes it rebuild its rows (live 2026-10-22 it is the
    /// message that takes the panel off its "Loading…" placeholder and shows the records as tiles); for
    /// <see cref="StoreOwned"/> the store's "the owned list changed" event, after which its open window marks the tiles
    /// (22-microtransactions.md §8). Its count is a <c>u32</c>.
    /// </summary>
    public static byte[] Added(IReadOnlyList<Record> records, byte flags = Records)
    {
        var w = new LoginWriter().U16(AddedId).U32((uint)records.Count);
        foreach (var r in records) WriteRecord(w, r);
        return w.U8(flags).ToArray();
    }

    /// <summary>
    /// C→S 0x1f9, the cosmetic slot's pick: the COSMETICS tab's slot flyout sends it when an entry is clicked (live
    /// 2026-10-22, §9). 8 bytes, no length prefix: the u16 id, a u32 <c>MicrotransactionSlot</c> row Id (20 = "Portal",
    /// the frame at the panel's bottom right) and a u16 selection — for the portal slot, the
    /// <c>MicrotransactionPortalVariations</c> <c>Index</c> (<c>11</c> for "Arctic Portal Effect"), which is the same
    /// number the catalogue and the 0x1f4 records key by. A 0 selection means the base effect.
    /// </summary>
    public const ushort SlotId = 0x1f9;
    public const int SlotLength = 8;

    /// <summary>The <c>MicrotransactionSlot</c> row of the portal effect slot (Id 20, ShopTag 85, Name "Portal").</summary>
    public const uint PortalSlot = 20;

    /// <summary>
    /// Parses an 0x1f9: which slot (a <c>MicrotransactionSlot</c> row Id) and what was picked. The id is big-endian
    /// like every message id, the two fields after it are plain little-endian (live bytes <c>14 00 00 00 0B 00</c>).
    /// </summary>
    public static (uint SlotRow, ushort Selection) ReadSlot(ReadOnlySpan<byte> m)
    {
        if (m.Length < SlotLength) throw new InvalidDataException($"0x1f9 is {m.Length} bytes, not {SlotLength}");
        ushort id = BinaryPrimitives.ReadUInt16BigEndian(m);
        if (id != SlotId) throw new InvalidDataException($"not an 0x1f9: {m[0]:x2}{m[1]:x2}");
        return (BinaryPrimitives.ReadUInt32LittleEndian(m[2..]), BinaryPrimitives.ReadUInt16LittleEndian(m[6..]));
    }

    /// <summary>An 0x1f3 as read back: the records and the counts of the three lists this codec does not model.</summary>
    public sealed record PurchasedList(IReadOnlyList<Record> Records, byte CountA, byte CountB, byte CountC, byte Flags);

    /// <summary>
    /// Parses an 0x1f3 (with its u16 id) the way the client's reader does. Throws when the lists at +0x28/+0x40/+0x58
    /// are not empty, because their elements are only partly known (§3).
    /// </summary>
    public static PurchasedList ReadPurchased(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m);
        if (r.U16() != PurchasedId) throw new InvalidDataException($"not an 0x1f3: {m[0]:x2}{m[1]:x2}");
        int count = r.U8();
        var records = new List<Record>(count);
        for (int i = 0; i < count; i++)
        {
            byte flags8 = r.U8();
            uint typeHash = r.U32();
            uint id = r.U32();
            ulong qword = r.RawU64();
            string name = r.WStr();
            string description = r.WStr();
            byte flags9 = r.U8();
            if (r.U8() != 0) throw new InvalidDataException($"record {i} carries a variation list; 22-microtransactions.md §4");
            if (r.U8() != 0) throw new InvalidDataException($"record {i} carries a +0x120 variant; 22-microtransactions.md §4");
            if (r.Short) break;
            records.Add(new Record(typeHash, id, name, description, qword, flags8, flags9));
        }
        byte a = r.U8(), b = r.U8(), c = r.U8(), flags = r.U8();
        if (r.Short) throw new InvalidDataException("0x1f3 is incomplete");
        if (a != 0 || b != 0 || c != 0) throw new InvalidDataException($"0x1f3 lists +0x28/+0x40/+0x58 are {a}/{b}/{c}, not empty; 22-microtransactions.md §3");
        return new PurchasedList(records, a, b, c, flags);
    }
}
