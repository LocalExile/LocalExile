using System.Buffers.Binary;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// The owned-microtransaction list the client's store and cosmetics panel are fed from
/// (Agents/docs/protocol/23-microtransactions.md). S→C 0x1f3 is the full list: the client appends every record to its
/// model (<c>hub+0xd600</c>), fires the panel's add/update events and builds the rows itself.
/// </summary>
public static class InstanceMicrotransactions
{
    public const ushort PurchasedId = 0x1f3;
    public const ushort AddedId = 0x1f4;

    /// <summary>
    /// One owned microtransaction, in wire order. <paramref name="MtxTypeHash"/> lands at +0x04 and is the key the
    /// panel's row build resolves in the UI context's catalogue (<c>0x142228b00</c>, a <c>MtxTypes</c> HASH32): a value
    /// that is not in it leaves a null and the row layout faults (live 2026-10-22). <paramref name="Id"/> is the
    /// record's own id at +0x00, the key the model upserts and removes by, so it has to be unique per record.
    /// <paramref name="Qword"/> lands at +0x10 (carried into the row's display item) and
    /// <paramref name="Flags8"/>/<paramref name="Flags9"/> at +0x08/+0x09 (unknown).
    /// </summary>
    public sealed record Record(uint MtxTypeHash, uint Id, string Name = "", string Description = "",
        ulong Qword = 0, byte Flags8 = 0, byte Flags9 = 0);

    /// <summary>The flags byte of a message: bit 0 → +0x70, bit 1 → +0x71, bit 2 → +0x72. A fresh object has 1.</summary>
    public const byte DefaultFlags = 1;

    /// <summary>
    /// One worn cosmetic: the <c>MicrotransactionSlot</c> Id it fills (1–49; 20 = Portal) and the owned record in it.
    /// The client keeps a 50-entry table at session +0xd118, 24 bytes per slot: <paramref name="Id"/> (+0x00, the
    /// record's id), <paramref name="MtxTypeHash"/> (+0x04, the <c>MTXTypeGameSpecific</c> HASH32 its visuals resolve,
    /// 0x140ce16b0 and 0x141575ac0), <paramref name="Qword"/> (+0x08) and <paramref name="Option"/> (+0x10, the last
    /// byte of the 0x1f9 that picked it).
    /// </summary>
    public sealed record Equipped(byte Slot, uint Id, uint MtxTypeHash, ulong Qword = 0, byte Option = 0);

    /// <summary>Slots the client's table holds (dispatcher case 0x92 skips a key from 50 up).</summary>
    public const int SlotCount = 50;

    /// <summary>
    /// A skill slot of the Cosmetics tab (0x1f3's second list, message +0x40 → session +0xd5c8, 40 bytes each, kept
    /// sorted by <paramref name="Key"/>): the panel makes one slot per entry (0x140cde080 → 0x140ce17f0). Wire: raw u32
    /// key, then the target, a variant (0x1424b34d0): u8 index 1 = one socketed skill gem (0x1424b4510: u8
    /// <paramref name="Inventory"/> → +4, u8 <paramref name="Socket"/> → +8, u8 <paramref name="SlotType"/> → +9, u32 BE
    /// <paramref name="GemHash"/> → +0), then u32 BE the worn record's id, u32 BE its MTXTypeGameSpecific hash (0 = none
    /// worn; the slot's art, 0x140cded10 → 0x140ce06e0) and a raw u64. The panel finds the gem item itself
    /// (0x141f93130): the player's inventory of Inventories.dat row <paramref name="Inventory"/>, the item there whose
    /// socket <paramref name="Socket"/> holds a gem of BaseItemTypes HASH32 <paramref name="GemHash"/>; that item gives
    /// the slot its name, and without it every effect is refused as "type mismatch" (0x1424f2440: 0xb27). An effect
    /// fits (0x1424b2cb0) when its ItemisedVisualEffect row's EffectSlotType is <paramref name="SlotType"/> (a
    /// MicrotransactionSkillGemEffectSlotTypes row, one of the gem's SkillGems MtxSlotTypes) and its BaseItemTypes and
    /// ItemClasses lists hold the gem's (or are empty).
    /// The key is an opaque identity returned by 0x1fa, not a byte-sized list index. The Gateway keeps it only for an
    /// unchanged target lifetime and never rebinds a retired key on that connection (issue #306).
    /// Saved effects remain keyed by gem type and effect slot type independently of this transient wire identity.
    /// </summary>
    public sealed record SkillSlot(uint Key, uint GemHash, byte Inventory, byte Socket, byte SlotType = 0, uint RecordId = 0, uint MtxTypeHash = 0);

    /// <summary>
    /// S→C 0x1f3 (class 0x1435e72a0, read 0x1421b11a0, dispatcher case 0x92 at 0x142157842): records the client appends
    /// to its owned list, then the worn cosmetics. The handler first resets the whole 50-slot worn table, then fills a
    /// slot per <paramref name="equipped"/> entry (u8 slot, u32 BE id, u32 BE hash, raw u64, u8), so every 0x1f3 carries
    /// all of them: a slot left out is emptied. The skill slots follow (<see cref="SkillSlot"/>); the list after them
    /// (message +0x58) is sent empty, its elements are not modelled, and the flags byte keeps the client's own default.
    /// </summary>
    public static byte[] Purchased(IReadOnlyList<Record> records, IReadOnlyList<Equipped>? equipped = null,
        IReadOnlyList<SkillSlot>? skills = null)
    {
        equipped ??= [];
        skills ??= [];
        if (skills.Count > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(skills), skills.Count, "0x1f3 counts skill slots in one byte");
        if (records.Count > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(records), records.Count, "0x1f3 counts records in one byte");
        if (equipped.Count > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(equipped), equipped.Count, "0x1f3 counts worn slots in one byte");
        var w = new LoginWriter().U16(PurchasedId).U8((byte)records.Count);
        foreach (var r in records) WriteRecord(w, r);
        w.U8((byte)equipped.Count);
        foreach (var e in equipped) w.U8(e.Slot).U32(e.Id).U32(e.MtxTypeHash).RawU64(e.Qword).U8(e.Option);
        w.U8((byte)skills.Count);
        foreach (var s in skills.OrderBy(s => s.Key))
            w.RawU32(s.Key).U8(SkillGemTarget).U8(s.Inventory).U8(s.Socket).U8(s.SlotType).U32(s.GemHash).U32(s.RecordId).U32(s.MtxTypeHash).RawU64(0);
        return w.U8(0).U8(DefaultFlags).ToArray();
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
    /// The 0x1f4 flags byte: which of the hub's three lists the message replaces. Dispatcher case 0x93
    /// (<c>0x14215836c</c>) tests bit 0 first, then bit 1, so one message fills one list, and each list has its own
    /// "loaded" flag the panels wait on (session offsets; the dispatcher's r13 is the session + 8):
    /// <see cref="Records"/> (0) → list +0xd600, loaded +0xcd3c: the Microtransactions panel's records;
    /// <see cref="StoreOwned"/> (1) → list +0xd618, loaded +0xcd40: the store's owned list, which is what puts
    /// <c>ALREADY OWNED</c> on a shop tile; <see cref="StoreSecond"/> (3) → list +0xd630, loaded +0xcd3e: the guild's
    /// list, which the client asks for only when the character is in a guild.
    /// </summary>
    public const byte Records = 0;
    public const byte StoreOwned = 1;
    public const byte StoreSecond = 3;

    /// <summary>
    /// C→S 0x1f2 (3 bytes, class vtable 0x1435caa38, write 0x141fa2740): the client asks for the lists it has not got
    /// yet, one bit each, and shows "Loading…" until the answer sets that list's loaded flag. The Microtransactions panel
    /// asks for <see cref="Lists.Records"/> when it opens (0x142173e20), the store for its owned lists (0x142173e80),
    /// the hideout picker for <see cref="Lists.Hideouts"/> (0x140bbaec0). A bit is asked once per session: the client
    /// sets its "requested" flag (+0xcd3d/+0xcd41/+0xcd45/+0xcd3f/+0xcd43) and never asks again.
    /// </summary>
    public const ushort RequestId = 0x1f2;
    public const int RequestLength = 3;

    /// <summary>The 0x1f2 bits: which list each one asks for, and the message that answers it.</summary>
    [Flags]
    public enum Lists : byte
    {
        /// <summary>0x1f4 with <see cref="InstanceMicrotransactions.Records"/>.</summary>
        Records = 1,
        /// <summary>0x1f4 with <see cref="InstanceMicrotransactions.StoreOwned"/>.</summary>
        StoreOwned = 2,
        /// <summary>0x1f5 without the guild bit.</summary>
        Hideouts = 4,
        /// <summary>0x1f4 with <see cref="InstanceMicrotransactions.StoreSecond"/>.</summary>
        GuildStore = 8,
        /// <summary>0x1f5 with the guild bit.</summary>
        GuildHideouts = 0x10,
    }

    public static Lists ReadRequest(ReadOnlySpan<byte> m) => (Lists)(m[2] & 0x1f);

    /// <summary>
    /// S→C 0x1f5 (class 0x1435e7620, read 0x1421b1850, dispatcher case 0x94 at 0x142158576): the unlocked hideouts. u8
    /// flags (bit 0: the guild's list, +0xd648 and loaded flag +0xcd42, else the character's, +0xd660 and +0xcd44; bit 1
    /// is read but not modelled), a compact count, then one byte per hideout: a <c>Hideouts.dat</c> row index. It
    /// replaces the list and sets its loaded flag, which is what takes the hideout picker off "Loading…".
    /// </summary>
    public const ushort HideoutsId = 0x1f5;

    /// <summary>S→C 0x1f6: fixed 128-bit campaign hideout unlock mask, raw bytes (row 0 is byte 0 bit 0).
    /// The picker reads this separately from the purchased-hideout list. Sending even an empty mask initializes
    /// the session bitset before FUN_1421741b0 indexes it.</summary>
    public static byte[] HideoutUnlocks(IEnumerable<int> rows)
    {
        var bits = new byte[16];
        foreach (int row in rows)
        {
            if (row is < 0 or >= 128) throw new ArgumentOutOfRangeException(nameof(rows), row, "Hideout unlock mask has 128 bits");
            bits[row / 8] |= (byte)(1 << (row % 8));
        }
        return new LoginWriter().U16(0x1f6).Bytes(bits).ToArray();
    }

    public static byte[] Hideouts(IReadOnlyList<byte> rows, bool guild = false)
    {
        var w = new LoginWriter().U16(HideoutsId).U8(guild ? (byte)1 : (byte)0);
        InstanceProtocol.CompactUInt(w, (uint)rows.Count);
        foreach (byte row in rows) w.U8(row);
        return w.ToArray();
    }

    /// <summary>
    /// S→C 0x1f4 (class 0x1435e7460, read 0x1421b1570, dispatcher case 0x93): the record list again, but this one
    /// <b>replaces</b> the list <paramref name="flags"/> selects and then fires that list's event — for
    /// <see cref="Records"/> the panel's reset, which is what makes it rebuild its rows (live 2026-10-22 it is the
    /// message that takes the panel off its "Loading…" placeholder and shows the records as tiles); for
    /// <see cref="StoreOwned"/> the store's "the owned list changed" event, after which its open window marks the tiles
    /// (23-microtransactions.md §1). Its count is a <c>u32</c>.
    /// </summary>
    public static byte[] Added(IReadOnlyList<Record> records, byte flags = Records)
    {
        var w = new LoginWriter().U16(AddedId).U32((uint)records.Count);
        foreach (var r in records) WriteRecord(w, r);
        return w.U8(flags).ToArray();
    }

    /// <summary>
    /// C→S 0x1f9 (class vtable 0x1435cb0c8, write 0x141fa28c0), the Cosmetics tab's pick: the slot flyout sends it when
    /// an entry is clicked (0x140cdce00) and with record 0 when a slot is emptied (0x140cdfb00). 8 bytes, no length
    /// prefix: the u16 id, u8 the <c>MicrotransactionSlot</c> Id (20 = "Portal"), u32 BE the picked record's
    /// <see cref="Record.Id"/> and a u8 option (0 in every pick seen). The client changes nothing itself: the slot is
    /// worn once a 0x1f3 says so. Live 2026-10-22, a click on Arctic Portal Effect: <c>01F9 14 0000000B 00</c>.
    /// Opening a slot's flyout sends nothing; it lists the records 0x1f4 gave.
    /// </summary>
    public const ushort SlotId = 0x1f9;
    public const int SlotLength = 8;

    /// <summary>The <c>MicrotransactionSlot</c> row of the portal effect slot (Id 20, ShopTag 85, Name "Portal").</summary>
    public const byte PortalSlot = 20;

    /// <summary>Parses an 0x1f9: the slot, the picked record's id (0: empty the slot) and the option byte.</summary>
    public static (byte Slot, uint RecordId, byte Option) ReadSlot(ReadOnlySpan<byte> m)
    {
        if (m.Length < SlotLength) throw new InvalidDataException($"0x1f9 is {m.Length} bytes, not {SlotLength}");
        ushort id = BinaryPrimitives.ReadUInt16BigEndian(m);
        if (id != SlotId) throw new InvalidDataException($"not an 0x1f9: {m[0]:x2}{m[1]:x2}");
        return (m[2], BinaryPrimitives.ReadUInt32BigEndian(m[3..]), m[7]);
    }

    /// <summary>
    /// C→S 0x1fa, the Cosmetics tab's pick in a skill slot (<see cref="SkillSlot"/>): 10 bytes, no length prefix: the u16
    /// id, the slot's raw u32 <see cref="SkillSlot.Key"/> as 0x1f3 sent it and u32 BE the picked record's
    /// <see cref="Record.Id"/> (0: empty the slot). Like 0x1f9 it changes nothing on the client until a 0x1f3 says so.
    /// Live 2026-10-23, picks in the slot keyed 51: <c>01FA 33000000 00000172</c>, and <c>01FA 33000000 00000000</c> to
    /// empty it. Unknown to the Gateway before, the read loop stopped at it and the pick was lost.
    /// </summary>
    public const ushort SkillPickId = 0x1fa;
    public const int SkillPickLength = 10;

    /// <summary>Parses an 0x1fa: the skill slot's key and the picked record's id (0: empty the slot).</summary>
    public static (uint Key, uint RecordId) ReadSkillPick(ReadOnlySpan<byte> m)
    {
        if (m.Length < SkillPickLength) throw new InvalidDataException($"0x1fa is {m.Length} bytes, not {SkillPickLength}");
        ushort id = BinaryPrimitives.ReadUInt16BigEndian(m);
        if (id != SkillPickId) throw new InvalidDataException($"not an 0x1fa: {m[0]:x2}{m[1]:x2}");
        return (BinaryPrimitives.ReadUInt32LittleEndian(m[2..]), BinaryPrimitives.ReadUInt32BigEndian(m[6..]));
    }

    /// <summary>The skill slots' target variant index for a skill gem (0x1424b34d0's table 0x143645770: 0 empty, 1 a skill
    /// gem, 2 two u32s).</summary>
    public const byte SkillGemTarget = 1;

    /// <summary>An 0x1f3 as read back: the records, the worn slots, the skill slots and the flags byte.</summary>
    public sealed record PurchasedList(IReadOnlyList<Record> Records, IReadOnlyList<Equipped> Equipped, IReadOnlyList<SkillSlot> Skills, byte Flags);

    /// <summary>
    /// Parses an 0x1f3 (with its u16 id) the way the client's reader does. Throws when the lists at +0x40/+0x58 are not
    /// empty, because their elements are only partly known.
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
            if (r.U8() != 0) throw new InvalidDataException($"record {i} carries a variation list; 23-microtransactions.md §1");
            if (r.U8() != 0) throw new InvalidDataException($"record {i} carries a +0x120 variant; 23-microtransactions.md §1");
            if (r.Short) break;
            records.Add(new Record(typeHash, id, name, description, qword, flags8, flags9));
        }
        int worn = r.U8();
        var equipped = new List<Equipped>(worn);
        for (int i = 0; i < worn && !r.Short; i++)
            equipped.Add(new Equipped(r.U8(), r.U32(), r.U32(), r.RawU64(), r.U8()));
        int skillCount = r.U8();
        var skills = new List<SkillSlot>(skillCount);
        for (int i = 0; i < skillCount && !r.Short; i++)
        {
            uint key = r.RawU32();
            if (r.U8() != SkillGemTarget) throw new InvalidDataException($"skill slot {i}: only skill gem targets are modelled");
            byte inventory = r.U8(), socket = r.U8(), slotType = r.U8();
            skills.Add(new SkillSlot(key, r.U32(), inventory, socket, slotType, r.U32(), r.U32()));
            r.RawU64();
        }
        byte c = r.U8(), flags = r.U8();
        if (r.Short) throw new InvalidDataException("0x1f3 is incomplete");
        if (c != 0) throw new InvalidDataException($"0x1f3 list +0x58 has {c} entries, not modelled");
        return new PurchasedList(records, equipped, skills, flags);
    }
}
