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
    public const ushort MineArea = 64840;
    public const ushort UpgradesRequestId = 0x23e, UpgradeId = 0x229, PurchaseId = 0x22a;
    public const byte UpgradesUi = 0x1d;
    public const ushort RouteId = 0x241;

    /// <summary>S→C 0x226: previous and current sulphite, both u16 BE. Unlike the full state packet,
    /// this event refreshes the native gauge's fill fraction, including an unchanged-value refresh.</summary>
    public static byte[] SulphiteChanged(ushort previous, ushort current) =>
        new LoginWriter().U16(0x226).U16(previous).U16(current).ToArray();

    public readonly record struct RoutePoint(int BlockX, int BlockY, int CellX, int CellY);

    /// <summary>C→S 0x241: byte count, then four big-endian coordinates per selected path point.</summary>
    public static int? RouteLength(ReadOnlySpan<byte> bytes) => bytes.Length < 3 ? null : 3 + bytes[2] * 16;

    public static IReadOnlyList<RoutePoint> ReadRoute(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 3 || bytes[0] != 2 || bytes[1] != 0x41 || RouteLength(bytes) != bytes.Length)
            throw new ArgumentException("Incomplete Delve route", nameof(bytes));
        var points = new RoutePoint[bytes[2]];
        for (int i = 0; i < points.Length; i++)
        {
            var p = bytes.Slice(3 + i * 16, 16);
            points[i] = new(System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(p),
                System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(p[4..]),
                System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(p[8..]),
                System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(p[12..]));
        }
        return points;
    }

    /// <summary>Area-instance extra terrain data: a Delve block with a Mine Entrance and its first northward
    /// Mines/Azurite quest cell. The nested block uses little-endian fields, unlike the enclosing packet.</summary>
    public static byte[] FirstMineTerrain() =>
        [0x20, 0, // extra-world-data flags: Delve terrain block
         0, 0, 0, 0, 0, 0, 0x21, 0, // block origin, zero auxiliary fields, width 1 / height 2, no extra stat groups
         1, 0x10, 0, 0, 0, // Mine Entrance, Mines biome, north connection
         7, 0x40, 0, 0, 0]; // first Azurite quest encounter, reciprocal south connection

    /// <summary>
    /// S→C 0x228 (class 0x1435dc940, read 0x1421ad670; dispatcher case 0xa9 at 0x142152f98): the player's Delve data:
    /// u8, 20 raw bytes, 14 raw bytes. The handler builds a 34-byte record at instance +0xd874 (raw14 first, then
    /// raw20) and sets +0xd896 = 0x101; the record's +0x1e (raw20 bytes 16..18, little-endian) is the current Voltaxic
    /// Sulphite the Delve gauge shows, and its capacity comes from raw20 bytes 0..16 (0x14246e390: the upgrade levels).
    /// Live 2026-10-29: the sulphite reached the client (instance +0xd892 = 100). The gauge shows outside the mine
    /// (WorldAreas type 27) only in the Subterranean Chart window (0x7b kind 0x1c), which sets its flag +0x2f1.
    /// </summary>
    public static byte[] DelveData(ushort sulphite, uint azurite = 0, byte flares = 0, byte dynamite = 0,
        IReadOnlyList<byte>? upgrades = null)
    {
        var raw20 = new byte[20];
        var raw14 = new byte[14];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(raw20, azurite);
        if (upgrades is { Count: > 10 }) throw new ArgumentOutOfRangeException(nameof(upgrades));
        for (int i = 0; i < (upgrades?.Count ?? 0); i++) raw20[6 + i] = upgrades![i];
        raw14[8] = flares; raw14[9] = dynamite;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(raw20.AsSpan(16), sulphite);
        return new LoginWriter().U16(DelveDataId).U8(0).Bytes(raw20).Bytes(raw14).ToArray();
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

/// <summary>The native Voltaxic Generator's interactive part. The hub tile already supplies the large machine.</summary>
public sealed record DelveGeneratorObject(int GridX, int GridY, float Rotation)
{
    public const string MetadataPath = "Metadata/MiscellaneousObjects/Delve/DelveCraftingBench";
    public const string Model = "Metadata/Terrain/Doodads/Leagues/Delve/DelveMapViewerTutorial.ao";
    public byte[] Blob() => new BlobWriter().U8(0).U8(0)
        .I32(GridX).I32(GridY).F32(Rotation).U16(0).U8(0).U8(0)
        .U8(1).WStr32(Model) // the hub room's console model, rather than the type's old fallback machine
        .U8(0) // InteractionAction
        .U8(0) // MinimapIcon
        .U8(0).ToArray();
}

public sealed record DelveGeneratorCablesObject(int GridX, int GridY, float Rotation)
{
    public const string MetadataPath = "Metadata/MiscellaneousObjects/Delve/DelveCraftingBenchAttachment";
    public byte[] Blob() => new BlobWriter().U8(0).U8(0)
        .I32(GridX).I32(GridY).F32(Rotation).U16(0).U8(0).U8(0)
        .U8(0) // native cable model
        .U8(2).U8(0).CompactI(0).U8(2).CompactI(0).U8(2) // capacity and radius shared states
        .U8(0).ToArray();
}

/// <summary>The stationary hub Crawler uses the native Delve map viewer's targeting and interaction components,
/// with the Crawler's model. The roaming DelveRobot type itself has no Targetable component.</summary>
public sealed record DelveMapViewerObject(int GridX, int GridY, float Rotation)
{
    public const string MetadataPath = "Metadata/Terrain/Leagues/Delve/Objects/DelveMapViewer";

    public byte[] Blob() => new BlobWriter().U8(0).U8(0)
        .I32(GridX).I32(GridY).F32(Rotation).U16(0).U8(EffectObject.FixedRotation).U8(0)
        .U8(1).WStr32(DelveRobotObject.Model)
        .U8(1) // Transitionable
        .U8(0) // MinimapIcon
        .U8(0) // InteractionAction
        .U8(0).ToArray(); // tasks
}

/// <summary>A stationary Crawler's wheel or powered lamp, attached to a bone of its interactive body.
/// Animated bit 2 is the native object/bone attachment record, so idle motion and rotation carry the parts along.</summary>
public sealed record DelveCrawlerAttachmentObject(int GridX, int GridY, uint ParentId, string Model, string Bone,
    bool PoweredLight = false)
{
    public const string LightType = "Metadata/Terrain/Leagues/Delve/Objects/DelveLight";
    public const string LeftWheel = "Metadata/Terrain/Leagues/Delve/Objects/DelveRobotWheel_L.ao";
    public const string RightWheel = "Metadata/Terrain/Leagues/Delve/Objects/DelveRobotWheel_R.ao";
    public const string Torch = "Metadata/Effects/Environment/League_Delve/torch/DelveTorch.ao";

    public byte[] Blob()
    {
        var w = new BlobWriter().U8(0).U8(0)
            .I32(GridX).I32(GridY).F32(0).U16(0).U8(PoweredLight ? (byte)0x20 : (byte)0).U8(0);
        // DelveLight's native Positioned scale is 35%; keep the attached torch at normal effect scale.
        if (PoweredLight) w.F32(100f / 35).F32(1).F32(1);
        w.U8(5).WStr32(Model).U32(ParentId).F32(0).F32(0)
            .U32((uint)Bone.Length).Bytes(System.Text.Encoding.ASCII.GetBytes(Bone))
            .U8(0).U8(0).F32(0).F32(0).F32(0);
        if (PoweredLight)
        {
            w.U8(1).Stats([]).U32(0).U32(0).U8(0).U16(0); // Transitionable, Stats, Buffs
            Pool(w, 1); Pool(w, 0); Pool(w, 0); w.U8(0);
            // 0x1415d88d0 reads the first four bytes as the float radius (+0x58),
            // which 0x141e189e0 scales for the player/depth. Zero suppresses area illumination.
            w.F32(60).U32(0).U8(1).U8(0); // Crawler radius, no links, powered; not a robot variation.
        }
        return w.U8(0).ToArray();
    }

    static void Pool(BlobWriter w, uint value) => w.U32(value).U32(0).U16(0).U32(0).U32(0);
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

/// <summary>
/// An invisible walk blocker, <c>Metadata/NPC/Blocker</c> (Positioned object_size 5, blocking; BaseEvents, server only):
/// the client's walk goes around its square (07 §4, objects). The Crawler's own type does not block, so a few of these
/// stand along it.
/// </summary>
public sealed record WalkBlockerObject(int GridX, int GridY)
{
    public const string MetadataPath = "Metadata/NPC/Blocker";

    /// <summary>The square it blocks around its cell (object_size 5).</summary>
    public const int Size = 5;

    public byte[] Blob()
    {
        var w = new BlobWriter();
        w.U8(0).U8(0);                                              // object flags, base data entry count
        w.I32(GridX).I32(GridY).F32(0).U16(0).U8(0).U8(0);          // Positioned
        w.U8(0);                                                    // tasks
        return w.ToArray();
    }
}
