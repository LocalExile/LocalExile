using System.Buffers.Binary;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// A hideout doodad the server places from the hideout's decoration (Agents/docs/protocol/21-hideouts.md §2), e.g. the
/// map device (<c>Metadata/Terrain/Missions/Hideouts/Objects/StrDexIntMappingDevice</c>: Positioned, BaseEvents,
/// Animated, InteractionAction, HideoutDoodad, StateMachine, MinimapIcon). Components are written in the type's order
/// (<paramref name="Components"/>: the deserialized ones, data/object-types.json); a type with any other component is
/// refused (<see cref="CanSerialize"/>). StateMachine: one value per shared state (<paramref name="States"/>, in the
/// type's order), no timers.
/// </summary>
public sealed record HideoutObject(int GridX, int GridY, float Rotation, IReadOnlyList<string> Components, HideoutDoodad? Doodad = null,
    IReadOnlyList<int>? States = null, string? Model = null)
{
    static readonly HashSet<string> Supported = ["Positioned", "BaseEvents", "Animated", "InteractionAction", "HideoutDoodad", "StateMachine", "MinimapIcon"];

    public static bool CanSerialize(IReadOnlyList<string> components) => components.All(Supported.Contains);

    public byte[] Blob()
    {
        if (!CanSerialize(Components)) throw new ArgumentException($"unsupported components: {string.Join(", ", Components)}");
        var w = new BlobWriter();
        w.U8(0).U8(0);                                                              // object flags, base data entry count
        foreach (string c in Components)
            switch (c)
            {
                // As the stash's: x, y, rotation, u16 team 0, flags0, flags1 0x02 + two u8 (shown).
                case "Positioned": w.I32(GridX).I32(GridY).F32(Rotation).U16(0).U8(0).U8(0x02).U8(0).U8(1); break;
                case "Animated": if (Model is null) w.U8(0); else w.U8(1).WStr32(Model); break;
                case "InteractionAction": w.U8(0); break;
                case "HideoutDoodad": (Doodad ?? throw new ArgumentException("a HideoutDoodad component needs its doodad")).Write(w); break;
                case "StateMachine":
                    w.U8((byte)(States?.Count ?? 0)).U8(0);
                    foreach (int v in States ?? []) w.CompactI(v).U8(0);   // value, flags
                    break;
                case "MinimapIcon": w.U8(0); break;
                // BaseEvents is server-only: nothing on the wire.
            }
        w.U8(0);                                                                    // tasks
        return w.ToArray();
    }
}

/// <summary>
/// The map device's messages (Agents/docs/protocol/21-hideouts.md §5). C→S 0x00d8 is the window's Activate button
/// (handler 0x140c284f0, mode 0xd → 0x14216ce30; written by the vtable 0x1435ca958 class, as 0xc7): u16 BE (the +0x7b
/// u16 of the target the window chose from its slots, 0 when it found none), u8 (an index; 0 seen).
/// </summary>
public static class InstanceMapDevice
{
    public const ushort ActivateId = 0x00d8, SelectMapId = 0x00e7, MoveId = 0x00e8, SocketId = 0x00f6;
    public const int ActivateLength = 5, SelectMapLength = 5, MoveLength = 11, SocketLength = 8;

    public static (ushort Target, byte Index) ReadActivate(ReadOnlySpan<byte> m) => ((ushort)(m[2] << 8 | m[3]), m[4]);

    /// <summary>C→S 0x00e7: an Atlas map node picked in the hideout map device. u16 BE WorldAreas HASH16, u8 0x0d.</summary>
    public static (ushort Area, byte Mode) ReadSelectMap(ReadOnlySpan<byte> m) => ((ushort)(m[2] << 8 | m[3]), m[4]);

    /// <summary>C→S 0xe8: Ctrl + click with the Atlas map device open (0x14030e970 → 0x14216e750 when the window's
    /// +0x10b8 check passes and the item's inventory kind is 0 or 0x0c). Writer 0x141f9f660: u32 BE item id, u8 flags,
    /// u8 into (1 from the main inventory onto Map1, 0 back out), u8, u8 row (13 Map1), u8.</summary>
    public sealed record Move(uint ItemId, byte Flags, bool IntoDevice, byte Extra, byte Row);

    public static Move ReadMove(ReadOnlySpan<byte> m) =>
        new(BinaryPrimitives.ReadUInt32BigEndian(m[2..]), m[6], m[7] != 0, m[8], m[9]);

    /// <summary>C→S 0xf6: a Voidstone clicked into (or out of) an Atlas socket with the map device open
    /// (0x1407bf070 → 0x14216eac0). Writer 0x141f993f0: u32 BE item id, u8, u8 (both 0 on the live
    /// inventory Ctrl + click). The item's own AtlasVoidstoneSlots row chooses the socket.</summary>
    public sealed record Socket(uint ItemId, byte A, byte B);

    public static Socket ReadSocket(ReadOnlySpan<byte> m) =>
        new(BinaryPrimitives.ReadUInt32BigEndian(m[2..]), m[6], m[7]);

    public const ushort PortalsId = 0x008f;

    /// <summary>One of the device's portals in <see cref="Portals"/>: the portal object and the area it leads to (WorldAreas
    /// HASH16; 0 closes it), and the name shown with it (the owner's).</summary>
    /// <param name="Style">Entry +0x2b: with no portal variation, which of three built-in models the portal takes
    /// (0x141e30580: 0, 1, 2 → hashes 0x8d2ebe26, 0x3380bc00, 0x0d4bf44e).</param>
    /// <param name="Variation">+0x26: a MicrotransactionPortalVariations row; the client draws that shop effect in place
    /// of the base portal (0 the default). This is how a portal cosmetic is applied: the portal itself is never replaced.</param>
    public readonly record struct Portal(uint ObjectId, ushort Area, string Owner = "", byte Style = 0, ushort Variation = 0);

    /// <summary>
    /// S→C 0x008f (class 0x1435e6648, read 0x1421a7370; dispatcher case 0x21 → 0x14218c9e0): the map device's portals.
    /// u8 flags (bit0 → +0x32, bit1 → +0x33, bit2: a u64 follows at the end), u8 +0x2e, u8 +0x31, u32 BE the map device
    /// object, u16 BE +0x2c, u8 +0x2f, u8 +0x30, then the list (0x1421c0460): u8 n, n × (u32 BE portal object, u16 BE area
    /// HASH16; only when the area is not 0: u16 BE +0x26 (a portal variation; 0 the default), u8, u8, u8 +0x2b, wstr). The
    /// handler gives each listed portal object (it must exist already) its destination through PortalClient vtable +0xb8
    /// (0x14161c0e0: the area, its model, rendering on) and runs the device's on_map_portal_opened; an entry whose area
    /// became 0 closes its portal (+0xc0) and runs on_map_portal_closed. A portal object never listed here is not drawn.
    /// </summary>
    public static byte[] Portals(uint deviceId, IReadOnlyList<Portal> portals)
    {
        var w = new GameServer.Core.Protocol.Login.LoginWriter().U16(PortalsId).U8(0).U8(0).U8(0).U32(deviceId).U16(0).U8(0).U8(0)
            .U8((byte)portals.Count);
        foreach (var p in portals)
        {
            w.U32(p.ObjectId).U16(p.Area);
            if (p.Area != 0) w.U16(p.Variation).U8(0).U8(0).U8(p.Style).WStr(p.Owner);
        }
        return w.ToArray();
    }
}

/// <summary>
/// A map device portal: <c>Metadata/MiscellaneousObjects/MultiplexPortal</c> (Positioned, BaseEvents, StateMachine,
/// Animated, Portal, Transitionable, MinimapIcon, InteractionAction; all read except BaseEvents). StateMachine: the
/// shared states of PortalBase.ot (standard_portal 1, portal, map_device_index -1, map_device_large 0). The Portal
/// component (0x141e30220) reads one u8, the multiplex portal type, which 0x141e30280 stores in the "portal" state
/// (MultiplexPortal.ot: 1 player, 2 town, 3 map device, 4 map return). The object alone is never drawn: S→C 0x008f
/// (<see cref="InstanceMapDevice.Portals"/>) gives it its area, model and rendering. Type 3 is drawn only after an open
/// transition (<see cref="OpenUpdate"/>), which hung or crashed the client, so map device portals use type 1 created
/// open (21-hideouts.md §5).
/// </summary>
public sealed record MapPortalObject(int GridX, int GridY, int DeviceIndex, byte PortalType = MapPortalObject.MapDevicePortal,
    byte Open = 0, float Rotation = 0)
{
    public const string MetadataPath = "Metadata/MiscellaneousObjects/MultiplexPortal";
    public const byte PlayerPortal = 1, TownPortal = 2, MapDevicePortal = 3, MapReturnPortal = 4;

    /// <summary>Transitionable in the object's component order (Positioned, BaseEvents, StateMachine, Animated, Portal,
    /// Transitionable …; Frida check-components).</summary>
    public const byte TransitionableIndex = 5;

    /// <summary>S→C 0x2bc to the portal's Transitionable (update reader 0x1421c8c80): u8 target state 1, open.</summary>
    public static byte[] OpenUpdate(uint objectId) => InstanceEntities.ComponentUpdate(objectId, TransitionableIndex, [1]);

    public byte[] Blob()
    {
        var w = new BlobWriter();
        w.U8(0).U8(0);                                                              // object flags, base data entry count
        w.I32(GridX).I32(GridY).F32(Rotation).U16(0).U8(0).U8(0x02).U8(0).U8(1);    // Positioned, as the hideout doodads'
        // StateMachine: standard_portal, portal, map_device_index, map_device_large; each a compact value and u8 flags.
        w.U8(4).U8(0);
        foreach (int v in new[] { 1, PortalType, DeviceIndex, 0 }) w.CompactI(v).U8(0);
        w.U8(0);                                                                    // Animated: the type's portal2.ao
        w.U8(PortalType);                                                           // Portal
        w.U8(Open);                                                                 // Transitionable
        w.U8(0);                                                                    // MinimapIcon
        w.U8(0);                                                                    // InteractionAction
        w.U8(0);                                                                    // tasks
        return w.ToArray();
    }
}
