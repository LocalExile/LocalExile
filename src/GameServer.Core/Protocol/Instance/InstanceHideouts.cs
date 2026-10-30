namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// A hideout doodad the server places from the hideout's decoration (Agents/docs/protocol/21-hideouts.md §2), e.g. the
/// map device (<c>Metadata/Terrain/Missions/Hideouts/Objects/StrDexIntMappingDevice</c>: Positioned, BaseEvents,
/// Animated, InteractionAction, HideoutDoodad, StateMachine, MinimapIcon). Components are written in the type's order
/// (<paramref name="Components"/>: the deserialized ones, data/object-types.json); a type with any other component is
/// refused (<see cref="CanSerialize"/>). StateMachine: one value per shared state (<paramref name="States"/>, in the
/// type's order), no timers.
/// </summary>
public sealed record HideoutObject(int GridX, int GridY, float Rotation, IReadOnlyList<string> Components, HideoutDoodad Doodad,
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
                case "HideoutDoodad": Doodad.Write(w); break;
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
/// (handler 0x140c2a1c0, mode 0xd → 0x142166480; written by the vtable 0x1435bff20 class, as 0xc7): u16 BE (the +0x7b
/// u16 of the target the window chose from its slots, 0 when it found none), u8 (an index; 0 seen).
/// </summary>
public static class InstanceMapDevice
{
    public const ushort ActivateId = 0x00d8, SelectMapId = 0x00e7;
    public const int ActivateLength = 5, SelectMapLength = 5;

    public static (ushort Target, byte Index) ReadActivate(ReadOnlySpan<byte> m) => ((ushort)(m[2] << 8 | m[3]), m[4]);

    /// <summary>C→S 0x00e7: an Atlas map node picked in the hideout map device. u16 BE WorldAreas HASH16, u8 0x0d.</summary>
    public static (ushort Area, byte Mode) ReadSelectMap(ReadOnlySpan<byte> m) => ((ushort)(m[2] << 8 | m[3]), m[4]);

    public const ushort PortalsId = 0x008f;

    /// <summary>One of the device's portals in <see cref="Portals"/>: the portal object and the area it leads to (WorldAreas
    /// HASH16; 0 closes it), and the name shown with it (the owner's).</summary>
    /// <param name="Style">Entry +0x2b: with no portal variation, which of three built-in models the portal takes
    /// (0x141e2d240: 0, 1, 2 → hashes 0x8d2ebe26, 0x3380bc00, 0x0d4bf44e).</param>
    /// <param name="Variation">+0x26: a MicrotransactionPortalVariations row; the client draws that shop effect in place
    /// of the base portal (0 the default). This is how a portal cosmetic is applied: the portal itself is never replaced.</param>
    public readonly record struct Portal(uint ObjectId, ushort Area, string Owner = "", byte Style = 0, ushort Variation = 0);

    /// <summary>
    /// S→C 0x008f (class 0x1435dcc00, read 0x1421a0a00; dispatcher case 0x21 → 0x142186070): the map device's portals.
    /// u8 flags (bit0 → +0x32, bit1 → +0x33, bit2: a u64 follows at the end), u8 +0x2e, u8 +0x31, u32 BE the map device
    /// object, u16 BE +0x2c, u8 +0x2f, u8 +0x30, then the list (0x1421b9af0): u8 n, n × (u32 BE portal object, u16 BE area
    /// HASH16; only when the area is not 0: u16 BE +0x26 (a portal variation; 0 the default), u8, u8, u8 +0x2b, wstr). The
    /// handler gives each listed portal object (it must exist already) its destination through PortalClient vtable +0xb8
    /// (0x14161a020: the area, its model, rendering on) and runs the device's on_map_portal_opened; an entry whose area
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
/// component (0x141e2cee0) reads one u8, the multiplex portal type, which 0x141e2cf40 stores in the "portal" state
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

    /// <summary>S→C 0x2ba to the portal's Transitionable (update reader 0x1421c2310): u8 target state 1, open.</summary>
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
