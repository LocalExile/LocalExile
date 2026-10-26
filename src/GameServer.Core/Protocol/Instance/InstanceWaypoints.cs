using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// Waypoints (Agents/docs/protocol/16-waypoints.md): the character's unlocked waypoints (S→C 0x4c), the World panel opened
/// for travel (S→C 0x7b kind 0) and the travel request from it (C→S 0x4d).
/// </summary>
public static class InstanceWaypoints
{
    public const ushort WaypointsId = 0x4c, OpenUiId = 0x7b, TravelId = 0x4d, VisitHideoutId = 0x17a;

    /// <summary>
    /// C→S 0x17a: the World panel's Hideout button, opened from a waypoint (live 2026-10-20). u16 n + n bytes UTF-8: the
    /// character whose hideout to go to (the player's own name for its own), u32 BE: the waypoint object the panel was
    /// opened from, u8 (0 seen). 21-hideouts.md §3.
    /// </summary>
    public sealed record VisitHideout(string Character, uint Waypoint, byte Flag);

    /// <summary>The length of a 0x17a at the start of <paramref name="data"/>, or null until its header has arrived.</summary>
    public static int? VisitHideoutLength(ReadOnlySpan<byte> data) =>
        data.Length < 4 ? null : 9 + System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data[2..]);

    public static VisitHideout ReadVisitHideout(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m[2..]);
        string name = System.Text.Encoding.UTF8.GetString(r.Blob());
        return new(name, r.U32(), r.U8());
    }

    /// <summary>Bytes of the unlocked-waypoint bitset (152 bits; the client reads at most 150 waypoint areas).</summary>
    public const int BitsetLength = 19;

    /// <summary>S→C 0x7b kind that opens the World panel in waypoint mode (0x142163420 case 0: listeners at instance
    /// +0xff8 → 0x140dcd570 → 0x140ab1440 → each panel's 0x140a97330).</summary>
    public const byte WorldPanelUi = 0;

    /// <summary>S→C 0x7b kind that opens the map device window (case 5 → listeners at instance +0x1778 → HUD 0x140dcd9a0:
    /// the window at HUD +0x7d8, mode 0xd). Kinds for its other modes go to +0x17f8 (0x140dce1f0, mode 0x5e) and +0x1878
    /// (0x140dce290, mode 0x56). 21-hideouts.md §4.</summary>
    public const byte MapDeviceUi = 5;

    /// <summary>S→C 0x7b kind 0x33: instance +0x22f8 → HUD virtual +0x8e0 (0x140ddb5c0) → 0x140c3e600.
    /// The first u32 is the CraftingBenchTypes row (0: ordinary bench, 1: runesmithing), not an object id.</summary>
    public const byte CraftingBenchUi = 0x33;

    /// <summary>Harvest crafting: 0x7b kind 0x39 -> instance +0x4ef8 -> HUD virtual +0xcc0
    /// (PC 0x140dcee40, controller 0x140d8d750). Recipes are populated from HarvestCraftOptions.dat.</summary>
    public const byte HorticraftingUi = 0x39;
    // 0x142163420 case 0x37 -> +0x5278 -> LAB_140dae778 -> HUD +0xcf8 -> 0x140dcf150.
    public const byte HeistLockerUi = 0x37;

    /// <summary>
    /// S→C 0x4c (class 0x1435dc7e0, read 0x14219f460; dispatcher case 0x12): u32 BE (+0x10, passed with the bitset to the
    /// listeners at instance +0x73f0), then 19 raw bytes (+0x14) copied to instance +0xcf31: bit i = the i-th WorldAreas
    /// row with HasWaypoint whose column +0x1e4 is 1, 2, 5 or 11 (0x141fdcf90; <see cref="World.WorldAreaWaypoints"/>).
    /// The World panel lights the pins of these areas and lets the player travel to them. When the current area's bit
    /// turns on in a later 0x4c of the same connection, the client plays the waypoint's activation (0x1421449f0 state 2).
    /// </summary>
    public static byte[] Waypoints(IEnumerable<int> unlockedBits, uint u10 = 0)
    {
        var bits = new byte[BitsetLength];
        foreach (int i in unlockedBits)
            if (i >= 0 && i < BitsetLength * 8) bits[i >> 3] |= (byte)(1 << (i & 7));
        return new LoginWriter().U16(WaypointsId).U32(u10).Fixed(bits, BitsetLength).ToArray();
    }

    /// <summary>
    /// S→C 0x7b (class 0x1435dc2a0, read 0x1421a0450; dispatcher case 0x1c → 0x142163420): open a UI. u8 kind (0x00..0x72),
    /// then four u32 BE (+0x14, +0x18, +0x1c, +0x20) whose use depends on the kind. Kind 0 opens the World panel for
    /// waypoint travel; its listener ignores the u32s, which the Gateway fills with the waypoint object id.
    /// </summary>
    public static byte[] OpenUi(byte kind, uint a = 0, uint b = 0, uint c = 0, uint d = 0) =>
        new LoginWriter().U16(OpenUiId).U8(kind).U32(a).U32(b).U32(c).U32(d).ToArray();

    /// <summary>C→S 0x4d: a waypoint picked on the World panel. Ctrl asks for the instance chooser.</summary>
    public sealed record Travel(ushort AreaHash16, uint U32, bool Ctrl);

    /// <summary>
    /// C→S 0x4d (9 bytes; class 0x1435c08b8, write 0x141f92e60; sent by the panel's pin click 0x140a9b580): u16 BE
    /// destination WorldAreas.HASH16 (the pin's row +0x2a), u32 BE (the panel's +0x524), u8 Ctrl held.
    /// </summary>
    public static Travel ReadTravel(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m[2..]);
        return new(r.U16(), r.U32(), r.U8() != 0);
    }

    public const int TravelLength = 9;
}

/// <summary>
/// A waypoint, <c>Metadata/MiscellaneousObjects/Waypoint</c>: Positioned (object_size 6, not blocking), BaseEvents (server
/// only), Animated, HideoutDoodad, MinimapIcon (icon 9), InteractionAction (action "waypoint"), StateMachine (shared state
/// "active"), client Render + Targetable. Like an area transition it is a tile object: the area's .tmd record gives its
/// place, rotation, height and virtual .ao (08-area-transitions.md §5, §7).
/// <para>HideoutDoodad (slot 22 0x141e1c550, reached through slot 17 0x1415d9920) reads u32 + u8 + u8 only when the
/// object's data has +0x14e set: not in a town, but in a hideout it does (Buffer Underflow live 2026-10-20 without it).
/// The u32 is the doodad's MtxTypeGameSpecific HASH32, the first u8 its variation, the second a flag (0)
/// (<see cref="HideoutDoodad"/>, 21-hideouts.md §2). StateMachine as for
/// monsters: u8 1 shared state, u8 0 timers, compact int "active" + u8 flags. The client plays the waypoint's look
/// from S→C 0x4c, not from "active" (its client handler plays the "inert" animation).</para>
/// </summary>
public sealed record WaypointObject(int GridX, int GridY, string? Model = null, float Rotation = 0, float? Height = null, int Active = 0,
    HideoutDoodad? Hideout = null)
{
    public const string MetadataPath = "Metadata/MiscellaneousObjects/Waypoint";

    public byte[] Blob()
    {
        var w = new BlobWriter();
        w.U8(0).U8(0);                                                              // object flags, base data entry count
        // Positioned as an area transition's: x, y, rotation, u16, flags0 (0x04: the record's fixed height), flags1.
        w.I32(GridX).I32(GridY).F32(Rotation).U16(0).U8((byte)(Height is null ? 0 : 0x04)).U8(0);
        if (Height is float z) w.I32((int)MathF.Round(z / 7.8125f)).F32(z);
        if (Model is null) w.U8(0); else w.U8(1).WStr32(Model);                    // Animated
        Hideout?.Write(w);                                                          // HideoutDoodad, in a hideout only
        w.U8(0);                                                                    // MinimapIcon flags
        w.U8(0);                                                                    // InteractionAction flags
        w.U8(1).U8(0);                                                              // StateMachine: 1 shared state, 0 timers
        w.CompactI(Active).U8(0);                                                   // "active", flags
        w.U8(0);                                                                    // tasks
        return w.ToArray();
    }
}

/// <summary>
/// A hideout doodad's HideoutDoodad component data (reader 0x141e1c550), sent for objects in a hideout: u32 the doodad's
/// MtxTypeGameSpecific HASH32 (its HideoutDoodads row), u8 variation (index into Variation_AOFiles), u8 flag.
/// </summary>
public sealed record HideoutDoodad(uint Hash, byte Variation, byte Flag = 0)
{
    public void Write(BlobWriter w) => w.U32(Hash).U8(Variation).U8(Flag);
}
