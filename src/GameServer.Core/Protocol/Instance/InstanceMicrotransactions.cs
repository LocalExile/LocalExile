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
    public const ushort WornSlotUpdateId = 0x1ff;
    public const ushort SkillSlotUpdateId = 0x200;
    public const ushort SkillSlotRemoveId = 0x201;
    public const ushort ExtraSlotUpdateId = 0x202;
    public const ushort ExtraSlotRemoveId = 0x203;
    public const ushort RecordUpsertId = 0x204;
    public const ushort RecordRemoveId = 0x205;
    public const ushort ReclaimQueryResultId = 0x20c;
    public const ushort RecordStatUpdateId = 0x210;
    public const ushort RecordStatResetId = 0x211;

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

    /// <summary>An extra cosmetic slot (0x1f3's third list at +0x58, S→C 0x202/0x203, C→S 0x1fb/0x1fc/0x1fe):
    /// raw u32 LE <paramref name="Key"/> plus the worn entry fields (0x1424b2b90).</summary>
    public sealed record ExtraSlot(uint Key, uint Id, uint MtxTypeHash, ulong Qword = 0, byte Option = 0);

    /// <summary>
    /// S→C 0x1f3 (class 0x1435e72a0, read 0x1421b11a0, dispatcher case 0x92 at 0x142157842): records the client appends
    /// to its owned list, then the worn cosmetics. The handler first resets the whole 50-slot worn table, then fills a
    /// slot per <paramref name="equipped"/> entry (u8 slot, u32 BE id, u32 BE hash, raw u64, u8), so every 0x1f3 carries
    /// all of them: a slot left out is emptied. The skill slots (<see cref="SkillSlot"/>) and extra slots
    /// (<see cref="ExtraSlot"/>) follow, then the display flags byte (bit 0 default, bit 1 from 0x1f7, bit 2 from 0x1f8).
    /// </summary>
    public static byte[] Purchased(IReadOnlyList<Record> records, IReadOnlyList<Equipped>? equipped = null,
        IReadOnlyList<SkillSlot>? skills = null, IReadOnlyList<ExtraSlot>? extras = null, byte flags = DefaultFlags)
    {
        equipped ??= [];
        skills ??= [];
        extras ??= [];
        if (skills.Count > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(skills), skills.Count, "0x1f3 counts skill slots in one byte");
        if (records.Count > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(records), records.Count, "0x1f3 counts records in one byte");
        if (equipped.Count > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(equipped), equipped.Count, "0x1f3 counts worn slots in one byte");
        if (extras.Count > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(extras), extras.Count, "0x1f3 counts extra slots in one byte");
        var w = new LoginWriter().U16(PurchasedId).U8((byte)records.Count);
        foreach (var r in records) WriteRecord(w, r);
        w.U8((byte)equipped.Count);
        foreach (var e in equipped) WriteWornEntry(w.U8(e.Slot), e.Id, e.MtxTypeHash, e.Qword, e.Option);
        w.U8((byte)skills.Count);
        foreach (var s in skills.OrderBy(s => s.Key)) WriteSkillEntry(w, s);
        w.U8((byte)extras.Count);
        foreach (var x in extras.OrderBy(x => x.Key)) WriteWornEntry(w.RawU32(x.Key), x.Id, x.MtxTypeHash, x.Qword, x.Option);
        return w.U8(flags).ToArray();
    }

    static LoginWriter WriteWornEntry(LoginWriter w, uint id, uint mtxTypeHash, ulong qword, byte option) =>
        w.U32(id).U32(mtxTypeHash).RawU64(qword).U8(option);

    static LoginWriter WriteSkillEntry(LoginWriter w, SkillSlot s) =>
        w.RawU32(s.Key).U8(SkillGemTarget).U8(s.Inventory).U8(s.Socket).U8(s.SlotType).U32(s.GemHash).U32(s.RecordId).U32(s.MtxTypeHash).RawU64(0);

    /// <summary>S→C 0x1ff (vtable 0x1435e7480, write 0x1421b19c0, read 0x1421b1a30, case 0x96): incremental worn-slot update.</summary>
    public static byte[] WornSlotUpdate(Equipped entry) =>
        WriteWornEntry(new LoginWriter().U16(WornSlotUpdateId).U8(entry.Slot), entry.Id, entry.MtxTypeHash, entry.Qword, entry.Option).ToArray();

    /// <summary>S→C 0x200 (vtable 0x1435e6de0, write 0x1421b1b20, read 0x1421b1b90, case 0x97): incremental skill-slot upsert.</summary>
    public static byte[] SkillSlotUpdate(SkillSlot slot) =>
        WriteSkillEntry(new LoginWriter().U16(SkillSlotUpdateId), slot).ToArray();

    /// <summary>S→C 0x201 (vtable 0x1435cb210, write 0x141fa2bb0, read 0x141fa2c10, case 0x98): remove a skill slot by raw u32 LE key.</summary>
    public static byte[] SkillSlotRemove(uint key) =>
        new LoginWriter().U16(SkillSlotRemoveId).RawU32(key).ToArray();

    /// <summary>S→C 0x202 (vtable 0x1435e6c08, write 0x1421b1ca0, read 0x1421b1d10, case 0x99): incremental extra-slot upsert.</summary>
    public static byte[] ExtraSlotUpdate(ExtraSlot entry) =>
        WriteWornEntry(new LoginWriter().U16(ExtraSlotUpdateId).RawU32(entry.Key), entry.Id, entry.MtxTypeHash, entry.Qword, entry.Option).ToArray();

    /// <summary>S→C 0x203 (vtable 0x1435cb210, write 0x141fa2bb0, read 0x141fa2c10, case 0x9a): remove an extra slot by raw u32 LE key.</summary>
    public static byte[] ExtraSlotRemove(uint key) =>
        new LoginWriter().U16(ExtraSlotRemoveId).RawU32(key).ToArray();

    /// <summary>S→C 0x204 (vtable 0x1435e7820, write 0x1421b1e70, read 0x1421b1f10, case 0x9b): upsert one owned record
    /// into personal (+0xd600 when <paramref name="isStore"/> is false), store owned (+0xd618), or guild (+0xd630).</summary>
    public static byte[] RecordUpsert(Record record, bool isStore = false, bool isGuild = false, bool justReceived = false)
    {
        var w = new LoginWriter().U16(RecordUpsertId).Bool(isStore).Bool(isGuild);
        WriteRecord(w, record);
        return w.Bool(justReceived).ToArray();
    }

    /// <summary>S→C 0x205 (vtable 0x1435e69a8, write 0x1421b2090, read 0x1421b2130, case 0x9c): remove one owned record by id.</summary>
    public static byte[] RecordRemove(uint recordId, bool isStore = false, bool isGuild = false) =>
        new LoginWriter().U16(RecordRemoveId).Bool(isStore).Bool(isGuild).U32(recordId).ToArray();

    /// <summary>S→C 0x20c (vtable 0x1435e7a20, write 0x1421b21f0, read 0x1421b25e0, case 0x9d): empty in-use reclaim query response.</summary>
    public static byte[] ReclaimQueryEmpty() =>
        new LoginWriter().U16(ReclaimQueryResultId).U16(0).U16(0).U16(0).ToArray();

    /// <summary>S→C 0x210 (vtable 0x1435e6d08, write 0x1421b2940, read 0x1421b29c0, case 0x9e): update one record's stat counter value.</summary>
    public static byte[] RecordStatUpdate(uint recordId, ulong value) =>
        new LoginWriter().U16(RecordStatUpdateId).U32(recordId).RawU64(value).ToArray();

    /// <summary>S→C 0x211 (vtable 0x1431767e8, write 0x140911210, read 0x140911270, case 0x9f): clear one record's stat counter.</summary>
    public static byte[] RecordStatReset(uint recordId) =>
        new LoginWriter().U16(RecordStatResetId).U32(recordId).ToArray();

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

    /// <summary>C→S 0x1f7 / 0x1f8 (3 bytes, write 0x140e27920, senders 0x140ce04f0 / 0x140ce0580): cosmetic display flag toggles.</summary>
    public const ushort DisplayFlag1Id = 0x1f7, DisplayFlag2Id = 0x1f8;
    public const int DisplayFlagLength = 3;

    public static bool ReadDisplayFlag(ReadOnlySpan<byte> m)
    {
        if (m.Length < DisplayFlagLength) throw new InvalidDataException($"0x1f7/0x1f8 is {m.Length} bytes, not {DisplayFlagLength}");
        ushort id = BinaryPrimitives.ReadUInt16BigEndian(m);
        if (id != DisplayFlag1Id && id != DisplayFlag2Id) throw new InvalidDataException($"not a 0x1f7/0x1f8: {m[0]:x2}{m[1]:x2}");
        return m[2] != 0;
    }

    /// <summary>C→S 0x1fb (7 bytes, write 0x141f96e50, sender 0x140cdf650): equip an extra cosmetic slot (u32 BE record id, u8 option).</summary>
    public const ushort ExtraEquipId = 0x1fb;
    public const int ExtraEquipLength = 7;

    public static (uint RecordId, byte Option) ReadExtraEquip(ReadOnlySpan<byte> m)
    {
        if (m.Length < ExtraEquipLength || BinaryPrimitives.ReadUInt16BigEndian(m) != ExtraEquipId)
            throw new InvalidDataException("invalid 0x1fb extra equip packet");
        return (BinaryPrimitives.ReadUInt32BigEndian(m[2..]), m[6]);
    }

    /// <summary>C→S 0x1fc (6 bytes, write 0x141fa2bb0, sender 0x140cdfbe0): unequip an extra cosmetic slot (raw u32 LE key).</summary>
    public const ushort ExtraUnequipId = 0x1fc;
    public const int ExtraUnequipLength = 6;

    public static uint ReadExtraUnequip(ReadOnlySpan<byte> m)
    {
        if (m.Length < ExtraUnequipLength || BinaryPrimitives.ReadUInt16BigEndian(m) != ExtraUnequipId)
            throw new InvalidDataException("invalid 0x1fc extra unequip packet");
        return BinaryPrimitives.ReadUInt32LittleEndian(m[2..]);
    }

    /// <summary>C→S 0x1fd (4 bytes, write 0x141fa2cb0, sender 0x140cdfe00): set a worn cosmetic slot's visual tier (u8 slot, u8 tier).</summary>
    public const ushort SlotTierId = 0x1fd;
    public const int SlotTierLength = 4;

    public static (byte Slot, byte Tier) ReadSlotTier(ReadOnlySpan<byte> m)
    {
        if (m.Length < SlotTierLength || BinaryPrimitives.ReadUInt16BigEndian(m) != SlotTierId)
            throw new InvalidDataException("invalid 0x1fd slot tier packet");
        return (m[2], m[3]);
    }

    /// <summary>C→S 0x1fe (7 bytes, write 0x141fa2de0, sender 0x140cdfe60): set an extra cosmetic slot's visual tier (raw u32 LE key, u8 tier).</summary>
    public const ushort ExtraTierId = 0x1fe;
    public const int ExtraTierLength = 7;

    public static (uint Key, byte Tier) ReadExtraTier(ReadOnlySpan<byte> m)
    {
        if (m.Length < ExtraTierLength || BinaryPrimitives.ReadUInt16BigEndian(m) != ExtraTierId)
            throw new InvalidDataException("invalid 0x1fe extra tier packet");
        return (BinaryPrimitives.ReadUInt32LittleEndian(m[2..]), m[6]);
    }

    /// <summary>C→S 0x20b (6 bytes, write 0x140911210, sender 0x140c11790): query where an owned MTX record is in use.</summary>
    public const ushort ReclaimQueryId = 0x20b;
    public const int ReclaimQueryLength = 6;

    /// <summary>C→S 0x20e (2 bytes, sender 0x140933e00) and C→S 0x20f (6 bytes, sender 0x140933d70): global / per-record MTX stat counter reset.</summary>
    public const ushort ResetAllStatsId = 0x20e, ResetRecordStatsId = 0x20f;
    public const int ResetAllStatsLength = 2, ResetRecordStatsLength = 6;

    public static uint ReadRecordIdU32(ReadOnlySpan<byte> m, ushort expectedId)
    {
        if (m.Length < 6 || BinaryPrimitives.ReadUInt16BigEndian(m) != expectedId)
            throw new InvalidDataException($"invalid 0x{expectedId:x4} packet");
        return BinaryPrimitives.ReadUInt32BigEndian(m[2..]);
    }

    /// <summary>The skill slots' target variant index for a skill gem (0x1424b34d0's table 0x143645770: 0 empty, 1 a skill
    /// gem, 2 two u32s).</summary>
    public const byte SkillGemTarget = 1;

    /// <summary>An 0x1f3 as read back: the records, the worn slots, the skill slots, the extra slots and the flags byte.</summary>
    public sealed record PurchasedList(IReadOnlyList<Record> Records, IReadOnlyList<Equipped> Equipped,
        IReadOnlyList<SkillSlot> Skills, byte Flags, IReadOnlyList<ExtraSlot>? Extras = null);

    /// <summary>
    /// Parses an 0x1f3 (with its u16 id) the way the client's reader does.
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
        int extraCount = r.U8();
        var extras = new List<ExtraSlot>(extraCount);
        for (int i = 0; i < extraCount && !r.Short; i++)
            extras.Add(new ExtraSlot(r.RawU32(), r.U32(), r.U32(), r.RawU64(), r.U8()));
        byte flags = r.U8();
        if (r.Short) throw new InvalidDataException("0x1f3 is incomplete");
        return new PurchasedList(records, equipped, skills, flags, extras);
    }
}
