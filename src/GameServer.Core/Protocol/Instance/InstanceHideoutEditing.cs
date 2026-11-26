using System.Buffers.Binary;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>Native hideout editor, music box and Item Display Case messages (3.29.3c).</summary>
public static class InstanceHideoutEditing
{
    /// <summary>C→S 0x59 (sender 0x1421669d0) / 0x5a (sender 0x142166a40), 16 bytes (writer 0x141f99e90), from the
    /// editor's placement 0x140204570: place a hideout decoration. 0x59 names an owned record (0x1f4 flags 1, its id),
    /// 0x5a the decoration's MtxTypes HASH32 itself (the panel's decorations that need no record). u32 BE the id or
    /// hash, u16 BE x, u16 BE y, f32 BE rotation, u8 variation, u8 flags (bit 0 = mirrored).</summary>
    public const ushort PlaceId = 0x59, PlaceByHashId = 0x5a;
    public const int PlaceLength = 16;

    public readonly record struct Place(bool ByHash, uint Id, ushort X, ushort Y, float Rotation, byte Variation, byte Flags)
    {
        public bool Mirrored => (Flags & 1) != 0;
    }

    public static Place ReadPlace(ReadOnlySpan<byte> m)
    {
        ushort op = m.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(m) : (ushort)0;
        if (m.Length != PlaceLength || (op != PlaceId && op != PlaceByHashId))
            throw new ArgumentException("Invalid hideout place packet", nameof(m));
        return new(op == PlaceByHashId, BinaryPrimitives.ReadUInt32BigEndian(m[2..]), BinaryPrimitives.ReadUInt16BigEndian(m[6..]),
            BinaryPrimitives.ReadUInt16BigEndian(m[8..]),
            BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32BigEndian(m[10..])),
            m[14], m[15]);
    }

    /// <summary>C→S 0x5b (15 bytes, sender 0x142166ab0, writer 0x141f9a110): move/rotate/flip a placed hideout object.</summary>
    public const ushort MoveId = 0x5b;
    public const int MoveLength = 15;
    public readonly record struct Move(uint ObjectId, ushort X, ushort Y, byte Flags, float Rotation)
    {
        public byte Variation => (byte)(Flags >> 1);
        public bool Mirrored => (Flags & 1) != 0;
    }

    public static Move ReadMove(ReadOnlySpan<byte> m)
    {
        if (m.Length != MoveLength || BinaryPrimitives.ReadUInt16BigEndian(m) != MoveId)
            throw new ArgumentException("Invalid hideout move packet", nameof(m));
        return new(BinaryPrimitives.ReadUInt32BigEndian(m[2..]), BinaryPrimitives.ReadUInt16BigEndian(m[6..]),
            BinaryPrimitives.ReadUInt16BigEndian(m[8..]), m[10],
            BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32BigEndian(m[11..])));
    }

    /// <summary>C→S 0x5d (6 bytes): reclaim/remove a placed decoration by object id (u32 BE).</summary>
    public const ushort ReclaimId = 0x5d;
    public const int ReclaimLength = 6;

    public static uint ReadReclaim(ReadOnlySpan<byte> m)
    {
        if (m.Length != ReclaimLength || BinaryPrimitives.ReadUInt16BigEndian(m) != ReclaimId)
            throw new ArgumentException("Invalid hideout reclaim packet", nameof(m));
        return BinaryPrimitives.ReadUInt32BigEndian(m[2..]);
    }

    /// <summary>C→S 0x5c (6 bytes, the generic u32 class 0x1431767e8): the editor's Delete key (0x140204210, VK_DELETE)
    /// on the selected object. u32 BE its object id.</summary>
    public const ushort RemoveId = 0x5c;
    public const int RemoveLength = 6;

    public static uint ReadRemove(ReadOnlySpan<byte> m)
    {
        if (m.Length != RemoveLength || BinaryPrimitives.ReadUInt16BigEndian(m) != RemoveId)
            throw new ArgumentException("Invalid hideout remove packet", nameof(m));
        return BinaryPrimitives.ReadUInt32BigEndian(m[2..]);
    }

    /// <summary>C→S 0x5e (3 bytes, sender 0x140213f10, writer 0x140e27920): u8 mode (0 = reclaim all decorations,
    /// 1 = reset hideout to defaults).</summary>
    public const ushort ResetId = 0x5e;
    public const int ResetLength = 3;

    public static byte ReadReset(ReadOnlySpan<byte> m)
    {
        if (m.Length != ResetLength || BinaryPrimitives.ReadUInt16BigEndian(m) != ResetId)
            throw new ArgumentException("Invalid hideout reset packet", nameof(m));
        return m[2];
    }

    /// <summary>C→S 0x255 (4 bytes, writer 0x141f99160, sender 0x140bb8010): select hideout music box track
    /// (u16 BE Music.dat row; 0xffff = default).</summary>
    public const ushort MusicId = 0x255;
    public const int MusicLength = 4;
    public const ushort DefaultMusicRow = 0xffff;

    public static ushort ReadMusic(ReadOnlySpan<byte> m)
    {
        if (m.Length != MusicLength || BinaryPrimitives.ReadUInt16BigEndian(m) != MusicId)
            throw new ArgumentException("Invalid hideout music packet", nameof(m));
        return BinaryPrimitives.ReadUInt16BigEndian(m[2..]);
    }

    /// <summary>S→C 0x7b kind 0x49 (handler 0x140dcb9c0): opens the Item Display Case window bound to the object id.</summary>
    public const byte DisplayCaseUi = 0x49;

    /// <summary>C→S 0x67 (14 bytes, writer 0x141f973b0, sender 0x140705a40): place/swap cursor item in Item Display Case.
    /// u32 BE object id, u32 BE inventory id (cursor), u32 BE item id.</summary>
    public const ushort DisplayCasePlaceId = 0x67;
    public const int DisplayCasePlaceLength = 14;

    public readonly record struct DisplayCasePlace(uint ObjectId, uint InventoryId, uint ItemId);

    public static DisplayCasePlace ReadDisplayCasePlace(ReadOnlySpan<byte> m)
    {
        if (m.Length != DisplayCasePlaceLength || BinaryPrimitives.ReadUInt16BigEndian(m) != DisplayCasePlaceId)
            throw new ArgumentException("Invalid display case place packet", nameof(m));
        return new(BinaryPrimitives.ReadUInt32BigEndian(m[2..]), BinaryPrimitives.ReadUInt32BigEndian(m[6..]),
            BinaryPrimitives.ReadUInt32BigEndian(m[10..]));
    }

    /// <summary>C→S 0x68 (7 bytes, writer 0x141f96e50, senders 0x140705940 / 0x1407059c0): toggle or remove displayed item.
    /// u32 BE object id, u8 flag.</summary>
    public const ushort DisplayCaseToggleId = 0x68;
    public const int DisplayCaseToggleLength = 7;

    public readonly record struct DisplayCaseToggle(uint ObjectId, bool Flag);

    public static DisplayCaseToggle ReadDisplayCaseToggle(ReadOnlySpan<byte> m)
    {
        if (m.Length != DisplayCaseToggleLength || BinaryPrimitives.ReadUInt16BigEndian(m) != DisplayCaseToggleId)
            throw new ArgumentException("Invalid display case toggle packet", nameof(m));
        return new(BinaryPrimitives.ReadUInt32BigEndian(m[2..]), m[6] != 0);
    }

    /// <summary>Positioned's update reader 0x141e33c50: little-endian mask bit 3, then f32 radians.</summary>
    public static byte[] Rotation(uint objectId, float radians) =>
        InstanceEntities.ComponentUpdate(objectId, 0, new BlobWriter().U16(8).F32(radians).ToArray());

    /// <summary>S→C 0x161: world handler 0x14218ddf0 calls 0x14218be70 to restore the hideout's
    /// baseline walk grids and reapply all current model hulls. Its unused object header is zero.</summary>
    public static byte[] RebuildCollision() =>
        new GameServer.Core.Protocol.Login.LoginWriter().U16(0x161).U32(0).U32(0).U16(0).ToArray();

    /// <summary>S→C 0x214 (vtable 0x1435e6ea0, write 0x1421b2a70, read 0x1421b2b00, dispatcher case 0xa1 at
    /// 0x142159e5d): syncs in-use bit 0 on personal (+0xd618) or guild (+0xd630) hideout decoration MTX records.
    /// Wire: u16 BE 0x0214, u16 BE hideoutId, u8 inUse (bool), u16 BE count, count × u32 BE recordId.</summary>
    public const ushort InUseDecorationsId = 0x214;

    public static byte[] InUseDecorations(ushort hideoutId, bool inUse, IReadOnlyList<uint> recordIds)
    {
        if (recordIds.Count > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(recordIds), recordIds.Count, "0x0214 counts record ids in u16");
        var w = new GameServer.Core.Protocol.Login.LoginWriter()
            .U16(InUseDecorationsId)
            .U16(hideoutId)
            .Bool(inUse)
            .U16((ushort)recordIds.Count);
        foreach (uint id in recordIds) w.U32(id);
        return w.ToArray();
    }

    /// <summary>S→C / C→S 0x0213 (class 0x1435ca770, writer 0x141fa3630 → 0x141fab3f0, reader 0x141fa36e0 → 0x141fab5f0):
    /// hideout template export/import. u16 BE hideout_hash (Hideouts.HASH16), u16 BE music_hash (Music.HASH16, 0 = default),
    /// u8 environment, u16 BE count, count × (u32 BE MtxTypeGameSpecific.HASH32, u16 BE x, u16 BE y, u16 BE rotation, u8 fv).</summary>
    public const ushort LayoutId = 0x0213;
    public const int LayoutHeaderLength = 9;
    public const int LayoutEntryLength = 11;
    /// <summary>Framing cap on doodad records in C→S 0x0213 (9 + 4096 × 11 = 45,065 bytes &lt; MaxClientCarryBytes).</summary>
    public const int MaxLayoutDoodads = 4096;

    public readonly record struct LayoutDoodad(uint Hash, ushort X, ushort Y, ushort Rotation, byte Fv)
    {
        public byte Variation => (byte)(Fv & 0x7f);
        public bool Mirrored => (Fv & 0x80) != 0;
        public float RotationRadians => DecodeRotation(Rotation);
    }

    public sealed record Layout(ushort HideoutHash, ushort MusicHash, byte Environment, IReadOnlyList<LayoutDoodad> Doodads);

    public static byte PackFv(byte variation, bool mirrored) => (byte)((variation & 0x7f) | (mirrored ? 0x80 : 0));

    public static ushort EncodeRotation(float radians)
    {
        if (!float.IsFinite(radians)) return 0;
        double turns = radians / (2.0 * Math.PI);
        turns -= Math.Floor(turns);
        return (ushort)((int)Math.Round(turns * 65536.0) & 0xffff);
    }

    public static float DecodeRotation(ushort encoded) => encoded / 65536f * (2f * MathF.PI);

    public static int? LayoutLength(ReadOnlySpan<byte> m) =>
        MeasureLayoutLength(m, out int length) == WireMeasure.Ok ? length : null;

    internal static WireMeasure MeasureLayoutLength(ReadOnlySpan<byte> data, out int length)
    {
        length = 0;
        if (data.Length < LayoutHeaderLength) return WireMeasure.Incomplete;
        int count = BinaryPrimitives.ReadUInt16BigEndian(data[7..]);
        if (count > MaxLayoutDoodads) return WireMeasure.Invalid;
        int total = LayoutHeaderLength + count * LayoutEntryLength;
        if (data.Length < total) return WireMeasure.Incomplete;
        length = total;
        return WireMeasure.Ok;
    }

    public static byte[] EncodeLayout(Layout layout)
    {
        if (layout.Doodads.Count > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(layout), "Hideout layout exceeds u16 doodad count.");
        var w = new GameServer.Core.Protocol.Login.LoginWriter()
            .U16(LayoutId)
            .U16(layout.HideoutHash)
            .U16(layout.MusicHash)
            .U8(layout.Environment)
            .U16((ushort)layout.Doodads.Count);
        foreach (var d in layout.Doodads)
            w.U32(d.Hash).U16(d.X).U16(d.Y).U16(d.Rotation).U8(d.Fv);
        return w.ToArray();
    }

    public static Layout ReadLayout(ReadOnlySpan<byte> m)
    {
        if (m.Length < LayoutHeaderLength || BinaryPrimitives.ReadUInt16BigEndian(m) != LayoutId)
            throw new ArgumentException("Invalid hideout layout packet", nameof(m));
        ushort hideoutHash = BinaryPrimitives.ReadUInt16BigEndian(m[2..]);
        ushort musicHash = BinaryPrimitives.ReadUInt16BigEndian(m[4..]);
        byte environment = m[6];
        ushort count = BinaryPrimitives.ReadUInt16BigEndian(m[7..]);
        if (count > MaxLayoutDoodads || m.Length != LayoutHeaderLength + count * LayoutEntryLength)
            throw new ArgumentException("Invalid hideout layout doodad count or length", nameof(m));
        var doodads = new LayoutDoodad[count];
        for (int i = 0, at = LayoutHeaderLength; i < count; i++, at += LayoutEntryLength)
        {
            doodads[i] = new(
                BinaryPrimitives.ReadUInt32BigEndian(m[at..]),
                BinaryPrimitives.ReadUInt16BigEndian(m[(at + 4)..]),
                BinaryPrimitives.ReadUInt16BigEndian(m[(at + 6)..]),
                BinaryPrimitives.ReadUInt16BigEndian(m[(at + 8)..]),
                m[at + 10]);
        }
        return new(hideoutHash, musicHash, environment, doodads);
    }
}
