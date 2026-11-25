using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Modules.Incursion;

/// <summary>
/// The Incursion messages and create bodies, rebuilt for the cells this server chooses from the official captures'
/// shapes (nothing captured is replayed). Messages are big-endian (LoginWriter), the bodies little-endian. The
/// component indices are the client's own (client.db <c>object_types.components</c>, the first <c>read</c> of each
/// type), and they are the indices the captured updates carry.
/// </summary>
public static class IncursionWire
{
    /// <summary>Alva in an area. Read: Positioned, Animated, BaseEvents, InteractionAction, NPC, MinimapIcon,
    /// StateMachine (lookat, open_room, open_temple).</summary>
    public const string AlvaType = "Metadata/NPC/League/Incursion/TreasureHunterWild";
    /// <summary>The portal beside Alva. Read: Positioned, BaseEvents, Animated, AreaTransition, Stats,
    /// ObjectMagicProperties, MinimapIcon, InteractionAction, Transitionable (3 states). It extends AreaTransition, so
    /// only this module's click handler moves the character.</summary>
    public const string Portal1Type = "Metadata/Terrain/Leagues/Incursion/Objects/IncursionPortal1";
    /// <summary>A cursed treasure. Read: Positioned, Animated, Transitionable, InteractionAction, StateMachine, Chest,
    /// ObjectMagicProperties, Stats.</summary>
    public const string TreasureType = "Metadata/Chests/IncursionChests/AlvaCursedTreasure";
    /// <summary>A closed door of the run's temple room.</summary>
    public const string DoorType = "Metadata/Terrain/Leagues/Incursion/Objects/ClosedDoorPast";
    /// <summary>A server effect that carries an .ao.</summary>
    public const string EffectType = "Metadata/Effects/ServerEffect";
    /// <summary>Alva's arrival effect [capture 19/19].</summary>
    public const string TeleportAo = "Metadata/Effects/Spells/monsters_effects/League_Incursion/diversefx/alva_valai/"
        + "teleport/alvateleport.ao";

    /// <summary>Alva's StateMachine component.</summary>
    public const byte AlvaStateMachine = 6;
    /// <summary>The portal's InteractionAction and Transitionable components.</summary>
    public const byte PortalInteraction = 7, PortalTransitionable = 8;
    /// <summary>A cursed treasure's Transitionable, InteractionAction and ObjectMagicProperties components.</summary>
    public const byte TreasureTransitionable = 2, TreasureInteraction = 3, TreasureMagicProperties = 6;

    /// <summary>S→C 0x163: the league timer (kind 2 for Incursion; Heist sends 8, Ultimatum 9) [capture].</summary>
    public const ushort TimerId = 0x163;
    /// <summary>S→C 0x164: the league progress bar (kind 0 for Incursion) [capture].</summary>
    public const ushort ProgressId = 0x164;
    public const byte TimerKind = 2, ProgressKind = 0;

    /// <summary>0x163: u8 kind 2, u32 whole seconds left [capture].</summary>
    public static byte[] Timer(int seconds) =>
        new LoginWriter().U16(TimerId).U8(TimerKind).U32((uint)Math.Max(0, seconds)).ToArray();

    /// <summary>0x164: f32 killed / monsters, u8 kind 0 [capture].</summary>
    public static byte[] Progress(float fraction)
    {
        Span<byte> be = stackalloc byte[4];
        BinaryPrimitives.WriteSingleBigEndian(be, fraction);
        return new LoginWriter().U16(ProgressId).Bytes(be).U8(ProgressKind).ToArray();
    }

    /// <summary>TreasureHunterWild: the position, <c>00000000 00 01 01 01 00</c> (Positioned's team and flags,
    /// Animated, InteractionAction, NPC, MinimapIcon), the StateMachine (lookat, open_room, open_temple) and the empty
    /// task list [capture 44/44].</summary>
    public static byte[] AlvaBody((int X, int Y) cell, IReadOnlyList<int> states, byte flag = 2) =>
        Position(flag, cell, 0f)
            .Bytes(Convert.FromHexString("000000000001010100"))
            .Bytes(StateMachine(states))
            .U8(0)
            .ToArray();

    /// <summary>IncursionPortal1: the position, <c>00 00 00 00 00</c>, the measured 19 bytes, the Transitionable
    /// state and the empty task list [capture 57/57].</summary>
    public static byte[] Portal1Body((int X, int Y) cell, byte tail0 = 1, byte tail1 = 1, byte flag = 2, float rotation = 0f) =>
        Position(flag, cell, rotation)
            .Zeros(5)
            .Bytes(Convert.FromHexString("0100710f000000000000004040010800000000"))
            .U8(tail0).U8(tail1)
            .U8(0)
            .ToArray();

    /// <summary>S→C 0x223: the temple state, 44 bytes (read as <c>raw36, raw2, raw2, raw4</c>,
    /// Agents/docs/protocol/schema.md) [capture 8/8]:
    /// <list type="bullet">
    /// <item>bytes 0-1: byte 1 is 0xe0 in every sample, byte 0 varies and is not decoded (sent 0).</item>
    /// <item>bytes 2-3: 0x7d81 little-endian in every sample.</item>
    /// <item>bytes 4-25: the plan's 11 rooms' IncursionRooms HASH16, u16 little-endian, in slot order (every value of
    /// the samples is an IncursionRooms row, and the one slot a run changes is the room it opened).</item>
    /// <item>bytes 26-35: not decoded (they grow with the temple's progress); bytes 29 (0x60) and 30 (0x90) are
    /// constant in every sample and sent, the rest 0.</item>
    /// <item>bytes 36-39: the run's two architects' MonsterVarieties HASH16, big-endian, the room's resident first;
    /// 0 and 0 when no run is going.</item>
    /// <item>bytes 40-43: u32 big-endian, the slot the run opens as an index into the 11, 0xffffffff when no run is
    /// going.</item>
    /// </list>
    /// The caller then opens or closes the panel with 0x7b kind <see cref="TempleUiKind"/>.</summary>
    public static byte[] TempleState(IncursionTemple temple, bool open, IncursionTemple.PickedRoom? pick = null)
    {
        var body = new byte[44];
        body[1] = 0xe0;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), 0x7d81);
        var slots = temple.Slots;
        for (int i = 0; i < 11 && i < slots.Count; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4 + i * 2), IncursionTables.RoomHash16(temple.Rooms[slots[i]]));
        body[29] = 0x60;
        body[30] = 0x90;
        int ordinal = open && pick is not null ? IndexOfSlot(slots, pick.Slot) : -1;
        if (ordinal >= 0)
        {
            int? challenger = pick!.Resident is not null ? pick.First : pick.Second;
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(36), IncursionTables.ArchitectHash16(pick.Resident ?? pick.First));
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(38), challenger is { } row ? IncursionTables.ArchitectHash16(row) : (ushort)0);
            BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(40), (uint)ordinal);
        }
        else BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(40), 0xffffffffu);
        return new LoginWriter().U16(TempleStateId).Bytes(body).ToArray();
    }

    public const ushort TempleStateId = 0x223;

    /// <summary>The 0x7b kind of the Temple of Atzoatl panel: 1 opens it, 0 closes it. The official server opens it at
    /// a run's start, 7 messages after the 0x223 state [capture 4/4]; when it closes it is not measured (the captured
    /// close came long after the run's end), so this module closes it with the end state.</summary>
    public const byte TempleUiKind = 0x18;

    static int IndexOfSlot(IReadOnlyList<int> slots, int slot)
    {
        for (int i = 0; i < slots.Count; i++)
            if (slots[i] == slot) return i;
        return -1;
    }

    /// <summary>ClosedDoorPast: the position, <c>00 00</c>, the variant byte (4 or 5 in the captures; what draws it is
    /// not decoded, so 4 is sent), <c>00 11 00 00 00 cc b6 0b c3 40</c>, the state (1, 1) and <c>00 00</c>
    /// [capture 107/107].</summary>
    public static byte[] DoorBody((int X, int Y) cell, float rotation, byte variant = 4, byte flag = 2) =>
        Position(flag, cell, rotation)
            .U8(0).U8(0)
            .U8(variant)
            .Bytes(Convert.FromHexString("0011000000ccb60bc340"))
            .U8(1).U8(1)
            .U8(0).U8(0)
            .ToArray();

    /// <summary>AlvaCursedTreasure: the position, <c>00000000 40 01 01 01 00 01 00</c>, <c>00</c>, the chest variant
    /// byte (0x44 / 0x45 / 0x48), 12 zero bytes and <c>40 40 01 00</c> [capture 75/75].</summary>
    public static byte[] TreasureBody((int X, int Y) cell, float rotation, byte variant = 0x44, byte flag = 2) =>
        Position(flag, cell, rotation)
            .Bytes(Convert.FromHexString("0000000040010101000100"))
            .U8(0).U8(variant)
            .Zeros(12)
            .Bytes(Convert.FromHexString("40400100"))
            .ToArray();

    /// <summary>A ServerEffect with an .ao: the position, 4 zero bytes, Animated <c>01</c> with the path, and the empty
    /// task list [capture 19/19].</summary>
    public static byte[] EffectBody((int X, int Y) cell, string ao, float rotation = 0f, byte flag = 2) =>
        Position(flag, cell, rotation)
            .Zeros(4)
            .U8(1).WStr32(ao)
            .U8(0)
            .ToArray();

    /// <summary>The object flag (0x02: the create wakes it), no base data, and Positioned's x, y and rotation.</summary>
    static BlobWriter Position(byte flag, (int X, int Y) cell, float rotation) =>
        new BlobWriter().U8(flag).U8(0).I32(cell.X).I32(cell.Y).F32(rotation);

    /// <summary>A StateMachine's data as the creation blob and the update carry it (<see cref="StateMachineData.Write"/>):
    /// the official server's Alva updates have flag 0 on every state [capture: <c>0300 0100 0100 0000</c> and
    /// <c>0300 0200 0100 0000</c>].</summary>
    public static byte[] StateMachine(IReadOnlyList<int> values)
    {
        var w = new BlobWriter();
        new StateMachineData(values, 0).Write(w);
        return w.ToArray();
    }

    /// <summary>The full 0x2bc StateMachine update (<see cref="InstanceEntities.StateMachineUpdate"/>).</summary>
    public static byte[] StateMachineUpdate(uint id, byte component, IReadOnlyList<int> values) =>
        InstanceEntities.StateMachineUpdate(id, component, new StateMachineData(values, 0));
}
