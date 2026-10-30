using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// The Delve chart (Agents/docs/protocol/21-hideouts.md, Niko): the client opens it on S→C 0x7b kind 0x1c, then asks for
/// its data with C→S 0x23f (just the id). S→C 0x240 (class 0x1435dbfa0, read 0x1421b3d00) answers: u32 (+0x10), u16
/// (+0x14), u16 n, then n × (u16, u16, u64, u8, u32). What the fields mean is being worked out live; an empty chart
/// first.
/// </summary>
public static class InstanceDelve
{
    public const ushort ChartRequestId = 0x023f, ChartId = 0x0240, DelveDataId = 0x228;

    /// <summary>
    /// S→C 0x228 (class 0x1435dc940, read 0x1421ad670; dispatcher case 0xa9 at 0x142152f98): the player's Delve data:
    /// u8, 20 raw bytes, 14 raw bytes. The handler builds a 34-byte record at instance +0xd874 (raw14 first, then
    /// raw20) and sets +0xd896 = 0x101; the record's +0x1e (raw20 bytes 16..18, little-endian) is the current Voltaxic
    /// Sulphite the Delve gauge shows, and its capacity comes from raw20 bytes 0..16 (0x14246e390: the upgrade levels).
    /// Live 2026-10-29: the sulphite reached the client (instance +0xd892 = 100). The gauge shows outside the mine
    /// (WorldAreas type 27) only in the Subterranean Chart window (0x7b kind 0x1c), which sets its flag +0x2f1.
    /// </summary>
    public static byte[] DelveData(ushort sulphite)
    {
        var raw20 = new byte[20];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(raw20.AsSpan(16), sulphite);
        return new LoginWriter().U16(DelveDataId).U8(0).Bytes(raw20).Bytes(new byte[14]).ToArray();
    }

    /// <summary>One chart entry as the reader takes it: u16, u16, u64, u8, u32 (meaning being worked out).</summary>
    public readonly record struct Cell(ushort A, ushort B, ulong C, byte D, uint E);

    public static byte[] Chart(uint a = 0, ushort b = 0, IReadOnlyList<Cell>? cells = null)
    {
        var w = new LoginWriter().U16(ChartId).U32(a).U16(b).U16((ushort)(cells?.Count ?? 0));
        foreach (var c in cells ?? []) w.U16(c.A).U16(c.B).U32((uint)(c.C >> 32)).U32((uint)c.C).U8(c.D).U32(c.E);
        return w.ToArray();
    }
}

/// <summary>
/// The Delve Crawler, <c>Metadata/Terrain/Leagues/Delve/Objects/DelveRobot</c> (the cart by the mine entrance). Its .ot
/// deserializes 11 components (Positioned, BaseEvents, Animated, Transitionable, Stats, Buffs, Life, DelveLight,
/// MinimapIcon, SectorEvents, StateMachine; Render and Wheels are client-only). DelveLight (0x1415d88d0) reads u32, u32 n,
/// u8, u8, the Crawler's u32 variation, n × u32; SectorEvents reads nothing; StateMachine has the shared states sector and mtx_variation.
/// </summary>
public sealed record DelveRobotObject(int GridX, int GridY, float Rotation)
{
    public const string MetadataPath = "Metadata/Terrain/Leagues/Delve/Objects/DelveRobot";
    public const string Model = "Metadata/Terrain/Leagues/Delve/Objects/DelveRobot.ao";

    public byte[] Blob()
    {
        var w = new BlobWriter();
        w.U8(0).U8(0);                                                                    // object flags, base data entries
        w.I32(GridX).I32(GridY).F32(Rotation).U16(0).U8(0).U8(0x02).U8(0).U8(1);         // Positioned (shown)
        w.U8(1).WStr32(Model);                                                            // Animated
        w.U8(1);                                                                          // Transitionable: state 1
        w.Stats([]).U32(0).U32(0).U8(0);                                                  // Stats
        w.U16(0);                                                                         // Buffs
        Pool(w, 1); Pool(w, 0); Pool(w, 0); w.U8(0);                                      // Life, mana, energy shield, flags
        // DelveLight (0x1415d88d0): u32, u32 n, u8, u8; for a type with flag +0xa7 (the Crawler) a u32 variation, an index
        // into DelveRobotVariations.dat (0 Default, 1 DelveBear) read by 0x1415d75d0; then n × u32. Without the variation
        // the client read the next bytes as it and crashed out of range (live 2026-10-29).
        w.U32(0).U32(0).U8(0).U8(0).U32(0);
        w.U8(0);                                                                          // MinimapIcon
        w.U8(2).U8(0).CompactI(0).U8(0x02).CompactI(0).U8(0x02);                          // StateMachine: sector, mtx_variation
        w.U8(0);                                                                          // tasks
        return w.ToArray();
    }

    static void Pool(BlobWriter w, uint current) => w.U32(current).U32(0).U16(0).U32(0).U32(0);
}
