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
    public IReadOnlyList<string> Components { get; init => field = Owned.List(value); } = Owned.List(Components);
    public IReadOnlyList<int>? States { get; init => field = Owned.List(value); } = Owned.List(States);
    static readonly HashSet<string> Supported = ["Positioned", "BaseEvents", "Animated", "InteractionAction", "HideoutDoodad", "StateMachine", "MinimapIcon", "ItemDisplayCase"];

    public static bool CanSerialize(IReadOnlyList<string> components) => components.All(Supported.Contains);

    public byte[] Blob()
    {
        if (!CanSerialize(Components)) throw new ArgumentException($"unsupported components: {string.Join(", ", Components)}");
        var w = new BlobWriter();
        w.U8(0).U8(0);                                                              // object flags, base data entry count
        foreach (string c in Components)
            switch (c)
            {
                case "Positioned": w.Positioned(GridX, GridY, Rotation); break;
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
    public const ushort ActivateId = 0x00d8, SelectMapId = 0x00e7, MoveId = 0x00e8, SocketId = 0x00f6,
        ActivateMemoryLineId = 0x0297, ActivateMavenInvitationId = 0x0298,
        UseItemOnAtlasNodeId = 0x0075, UseItemTypeOnAtlasNodeId = 0x0076, AtlasScryId = 0x01a7,
        AtlasVoidstoneDeltaId = 0x01a5, AtlasVaultPassId = 0x012a;
    public const int ActivateLength = 5, SelectMapLength = 5, MoveLength = 11, SocketLength = 8,
        ActivateMemoryLineLength = 3, ActivateMavenInvitationLength = 3,
        UseItemOnAtlasNodeLength = 12, UseItemTypeOnAtlasNodeLength = 17, AtlasScryLength = 10;

    public static (ushort Target, byte Index) ReadActivate(ReadOnlySpan<byte> m) => ((ushort)(m[2] << 8 | m[3]), m[4]);

    /// <summary>C→S 0x0297 (3 bytes, 0x140c285e8 mode 0x5e MemoryLineMaps1): u8 primordial boss option.</summary>
    public static byte ReadActivateMemoryLine(ReadOnlySpan<byte> m) => m[2];

    /// <summary>C→S 0x0298 (3 bytes, 0x140c2866c mode 0x56 MavenInvitations): u8 invitation slot index (0..10).</summary>
    public static byte ReadActivateMavenInvitation(ReadOnlySpan<byte> m) => m[2];

    /// <summary>C→S 0x00e7: an Atlas map node picked in the hideout map device. u16 BE WorldAreas HASH16, u8 0x0d.</summary>
    public static (ushort Area, byte Mode) ReadSelectMap(ReadOnlySpan<byte> m) => ((ushort)(m[2] << 8 | m[3]), m[4]);

    /// <summary>C→S 0xe8: Ctrl + click with the map device open, for an **Atlas Idol** (the ItemClasses row 94,
    /// `AtlasRelic`, which the client's Ctrl + click handler tests first: `cmp eax,0x5f` at `0x140310f44`). Writer
    /// `0x14216e750`, serializer `0x141f9f660`: u32 BE item id, then five single bytes, no byte swap — `+0x14`, `+0x15`,
    /// `+0x16` and the low/high of the handler's u16 argument at `+0x17`/`+0x18`. The first of the five is 1 when the item
    /// is in inventory row 84 (`ExpandedMainInventory1`, `0x14030ea5c`); there is no row-13 byte (the 0x0d the old note
    /// remembered is 0xe7's). A **map** never sends this message: it sends <see cref="MoveIntoDeviceId"/> or
    /// <see cref="MoveOutOfDeviceId"/>.</summary>
    public sealed record Move(uint ItemId, byte Flags, bool IntoDevice, byte Extra, byte Row);

    public static Move ReadMove(ReadOnlySpan<byte> m) =>
        new(BinaryPrimitives.ReadUInt32BigEndian(m[2..]), m[6], m[7] != 0, m[8], m[9]);

    /// <summary>C→S 0xe4 (into) and 0xe5 (out): Ctrl + click with the map device open, for the items that are not Atlas
    /// Idols — a map goes this way. Both are 12 bytes with u32 BE item id at +2 and u16 BE open map device object's +0x7b
    /// at +10, but their flag/direction packing differs:
    /// <list type="bullet">
    /// <item>0xe4 (sender <c>0x14216e300</c>, writer <c>0x141f9eac0</c>): byte 6 packs bit 0 (+0x24) and bit 1 (+0x26),
    /// bytes 7..8 are +0x28/+0x29, byte 9 packs bit 7 (+0x25, into device) | low 7 bits (+0x27).</item>
    /// <item>0xe5 (sender <c>0x14216e5d0</c>, writer <c>0x141f9eef0</c>): byte 6 packs bit 0 (+0x24), bit 1 (+0x25, into
    /// device) and bit 2 (+0x26), bytes 7..8 are +0x28/+0x29, byte 9 is plain +0x27.</item>
    /// </list></summary>
    public const ushort MoveIntoDeviceId = 0x00e4, MoveOutOfDeviceId = 0x00e5;
    public const int MoveItemLength = 12;

    public sealed record ItemClick(uint ItemId, byte Flags, byte Flags2, byte Flags3, bool IntoDevice, ushort Device);

    public static ItemClick ReadItemClick(ReadOnlySpan<byte> m)
    {
        ushort id = BinaryPrimitives.ReadUInt16BigEndian(m);
        uint itemId = BinaryPrimitives.ReadUInt32BigEndian(m[2..6]);
        ushort device = BinaryPrimitives.ReadUInt16BigEndian(m[10..12]);
        if (id == MoveOutOfDeviceId)
        {
            byte flags = (byte)((m[6] & 0x01) | ((m[6] & 0x04) >> 1));
            return new(itemId, flags, m[7], m[8], (m[6] & 0x02) != 0, device);
        }
        return new(itemId, m[6], m[7], m[8], (m[9] & 0x80) != 0, device);
    }

    /// <summary>C→S 0xf6: a Voidstone clicked into (or out of) an Atlas socket with the map device open
    /// (0x1407bf070 → 0x14216eac0). Writer 0x141f993f0: u32 BE item id, u8, u8 (both 0 on the live
    /// inventory Ctrl + click). The item's own AtlasVoidstoneSlots row chooses the socket.</summary>
    public sealed record Socket(uint ItemId, byte A, byte B);

    public static Socket ReadSocket(ReadOnlySpan<byte> m) =>
        new(BinaryPrimitives.ReadUInt32BigEndian(m[2..]), m[6], m[7]);

    /// <summary>S→C 0x01a5 (4 bytes, read 0x1421b09f0 / write 0x1421b0970, dispatcher case 0x81
    /// at 0x142157801): incremental Voidstone T16 region-mask delta (<c>u8 regionBit</c> 0..3, <c>u8 on</c>). Toggles
    /// bit <paramref name="regionBit"/> of <c>instance+0xd0f8</c> via <c>0x1407d08a0</c> and notifies <c>+0xb178</c>.</summary>
    public static byte[] AtlasVoidstoneDelta(byte regionBit, bool socketed) =>
        [0x01, 0xa5, regionBit, socketed ? (byte)1 : (byte)0];

    /// <summary>C→S 0x01a7 (10 bytes, write 0x141fa6080, sender 0x140788370): Atlas Scrying swap
    /// between two Atlas maps (<c>WorldAreas.HASH16</c> from, to) triggered from <paramref name="ObjectId"/>.</summary>
    public readonly record struct AtlasScryRequest(ushort FromAreaHash, ushort ToAreaHash, uint ObjectId);

    public static AtlasScryRequest ReadAtlasScry(ReadOnlySpan<byte> m) =>
        new(BinaryPrimitives.ReadUInt16BigEndian(m[2..]), BinaryPrimitives.ReadUInt16BigEndian(m[4..]),
            BinaryPrimitives.ReadUInt32BigEndian(m[6..]));

    /// <summary>C→S 0x0075 (12 bytes, write 0x141f9af00, caller 0x14079cb30): apply a currency item
    /// directly to an Atlas node (<c>u32 BE inventoryId</c> from <c>msg+0x14</c>, <c>u32 BE itemId</c> from
    /// <c>msg+0x10</c>, <c>u16 BE nodeHash16</c>).</summary>
    public readonly record struct UseItemOnAtlasNode(uint InventoryId, uint ItemId, ushort NodeHash16);

    public static UseItemOnAtlasNode ReadUseItemOnAtlasNode(ReadOnlySpan<byte> m) =>
        new(BinaryPrimitives.ReadUInt32BigEndian(m[2..]), BinaryPrimitives.ReadUInt32BigEndian(m[6..]),
            BinaryPrimitives.ReadUInt16BigEndian(m[10..]));

    /// <summary>C→S 0x0076 (17 bytes, write 0x141f9b0b0, caller 0x14079cb60): apply a currency type
    /// (Shift-held repeat) to an Atlas node (<c>u32 BE typeHash</c>, <c>u32 BE inventoryId</c>, <c>u16 BE tabIndex</c>,
    /// <c>u16 BE subIndex</c>, <c>u8 flags</c>, <c>u16 BE nodeHash16</c>).</summary>
    public readonly record struct UseItemTypeOnAtlasNode(uint TypeHash, uint InventoryId, ushort TabIndex, ushort SubIndex,
        byte Flags, ushort NodeHash16);

    public static UseItemTypeOnAtlasNode ReadUseItemTypeOnAtlasNode(ReadOnlySpan<byte> m) =>
        new(BinaryPrimitives.ReadUInt32BigEndian(m[2..]), BinaryPrimitives.ReadUInt32BigEndian(m[6..]),
            BinaryPrimitives.ReadUInt16BigEndian(m[10..]), BinaryPrimitives.ReadUInt16BigEndian(m[12..]),
            m[14], BinaryPrimitives.ReadUInt16BigEndian(m[15..]));

    /// <summary>S→C 0x012a (read 0x1421ad220 / write 0x1421ad110, dispatcher case 0x51): Atlas
    /// Vault Pass state. Enables <c>OpenAtlasPassButton</c> (<c>0x1406baef0</c>) and <c>AtlasPassRewardButton</c>
    /// (<c>HUD+0x530</c>). Wire: <c>u16 BE mapCompletions</c>, <c>WStr passId</c> (<c>BattlePasses.dat</c>),
    /// <c>u8 purchased</c>, <c>u8 active</c>, <c>u8 trackFlags</c>, <c>u64 LE unlockedMask</c>, <c>u64 LE claimedMask</c>,
    /// <c>u8 hasUnclaimedRewards</c>.</summary>
    public static byte[] AtlasVaultPass(ushort mapCompletionCount, string passId, bool purchased = false, bool active = true,
        byte trackFlags = 0, ulong unlockedMask = 0, ulong claimedMask = 0, bool hasUnclaimedRewards = false) =>
        new GameServer.Core.Protocol.Login.LoginWriter()
            .U16(AtlasVaultPassId)
            .U16(mapCompletionCount)
            .WStr(passId)
            .Bool(purchased)
            .Bool(active)
            .U8(trackFlags)
            .RawU64(unlockedMask)
            .RawU64(claimedMask)
            .Bool(hasUnclaimedRewards)
            .ToArray();

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
    /// <para>
    /// The label floating over the player's own portal is the Portal component's own nametag: slot +0xb8 calls the text
    /// getter 0x14161ca80, which takes the WorldAreas row the entry's area HASH16 resolved to ([component+0xb0], then its
    /// +8, the localized area name) and then tests a byte of the component (<c>0x14161caa7</c>); set, the label is
    /// ClientStrings 0x1fe5 <c>MapPortalsSuffixMapCompleted</c>, "{0} (Completed)" (<c>0x14161caf1</c>). That byte is
    /// +0x33, which only bit 1 of this message's flags fills (reader <c>0x1421a73c1</c>): the dispatcher passes it to the
    /// handler (<c>0x14218ca36</c>) and the handler forwards it, per listed portal, as the 7th argument of +0xb8
    /// (<c>0x14218cc2d</c>). It re-runs +0xb8 for every portal on every 0x008f, so a re-sent list relabels them.
    /// <paramref name="completed"/> sets that bit 1 and nothing else: bit 0 (0x01) is a different byte, +0x32, and
    /// selects the "just opened" render path, so it stays clear.
    /// </para>
    /// </summary>
    public static byte[] Portals(uint deviceId, IReadOnlyList<Portal> portals, bool completed = false)
    {
        // The flags byte: bit 1 (0x02) is the completed label, 0 is a plain one.
        var w = new GameServer.Core.Protocol.Login.LoginWriter().U16(PortalsId).U8((byte)(completed ? 0x02 : 0x00)).U8(0).U8(0)
            .U32(deviceId).U16(0).U8(0).U8(0).U8((byte)portals.Count);
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
        w.Positioned(GridX, GridY, Rotation);                                       // Positioned
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
