using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Modules.Ritual;

/// <summary>
/// The Ritual objects' create blobs and the Ritual messages, byte for byte as the official captures give them.
/// <para>
/// Messages 0x165 / 0x166 / 0x167 are below 0x296, so the numbers hold in 3.29.3c (Agents/docs/protocol/schema.md: 0x165
/// raw4 raw4 raw4 raw1, min 13; 0x166 a list, raw1, raw1; 0x167 raw4). The create, the component update and the removal are
/// the framework's own 0x2bb / 0x2bc / 0x2bd.
/// </para>
/// </summary>
static class RitualWire
{
    public const ushort AltarStateId = 0x165, FavoursId = 0x166, TributeId = 0x167;

    /// <summary>Properties: the RitualRuneTypes row (u32), the kill progress (f32), a pool's altar (u32, the Interactable's id).</summary>
    public const uint PropRune = 0x027aa346, PropProgress = 0x460e6134, PropPoolOwner = 0x1723d91c;

    /// <summary>The Interactable's constant middle (bytes 40..145) and the Object's (29..98): the same in every capture
    /// but for Interactable byte 108, 0x40 once a kill was credited, else 0.</summary>
    const string InteractableMid =
        "01b7c401000000000000404001010002"
        + "00026601ab050000807f0000803f0100"
        + "00000000000000000000020000005700"
        + "00000001000000000000000000000000"
        + "00000000000000000000000000000000"
        + "00000000000000000000000000000000"
        + "00000000000000000000";
    const string ObjectMid =
        "0189df01000000000000404001000000"
        + "00000000000000000000000000000000"
        + "00000000000000000000000000000000"
        + "00000000000000000000000000000000"
        + "000000000000";

    /// <summary>The byte after 03 in the Interactable / Object tails: 0x17 / 0x09 for Golden2 (row 62), 0x1f / 0x0c for
    /// PhysicalBloodUber1 (row 73) in the captures. What it is is not known; the other runes take the second pair.</summary>
    static (byte Interactable, byte Object) RuneTail(int row) => row switch { 62 => (0x17, 0x09), _ => (0x1f, 0x0c) };

    /// <summary>The index of a component in an object type's list, which the update messages name [data: the client's .ot
    /// order, data/object-types.json]. The fallback is the index the .ot order gives, used when the type is not in the
    /// catalogue.</summary>
    public static byte Component(string type, string name, byte fallback) =>
        QuestObjectTypes.Find(type) is { } info && info.Components.ToList().IndexOf(name) is >= 0 and var i ? (byte)i : fallback;

    public static byte InteractableAction => Component(RitualEncounter.InteractableType, "InteractionAction", 7);
    public static byte InteractableStates => Component(RitualEncounter.InteractableType, "StateMachine", 8);
    public static byte ObjectStates => Component(RitualEncounter.ObjectType, "StateMachine", 6);
    public static byte LightPositioned => Component(RitualEncounter.LightType, "Positioned", 0);
    public static byte LightStates => Component(RitualEncounter.LightType, "StateMachine", 2);
    public static byte PoolTransition => Component(RitualEncounter.PoolType, "Transitionable", 3);

    /// <summary>RitualRuneInteractable (160 B): 2 properties, Positioned, the constant middle, 03 X 14 00 00, enabled,
    /// StateMachine (state, enabled), 00 00.</summary>
    public static byte[] Interactable((int X, int Y) cell, float rotation, int runeRow, float progress, int state, int enabled)
    {
        byte[] mid = Convert.FromHexString(InteractableMid);
        mid[108 - 40] = progress > 0 ? (byte)0x40 : (byte)0;
        var w = new BlobWriter().U8(0).U8(2);
        w.U32(PropRune).U8(0).U8(1).U8(8).U32((uint)runeRow);
        w.U32(PropProgress).U8(0).U8(1).U8(9).F32(progress);
        w.I32(cell.X).I32(cell.Y).F32(rotation).Zeros(4).Bytes(mid);
        w.U8(3).U8(RuneTail(runeRow).Interactable).U8(0x14).U8(0).U8(0).U8((byte)(enabled & 1));
        new StateMachineData([state, enabled], 0).Write(w);
        return w.U8(0).U8(0).ToArray();
    }

    /// <summary>RitualRuneObject (109 B): 1 property (progress), Positioned, the constant middle, 03 X 14 00 00,
    /// StateMachine (state), 00.</summary>
    public static byte[] Object((int X, int Y) cell, float rotation, int runeRow, float progress, int state)
    {
        var w = new BlobWriter().U8(0).U8(1);
        w.U32(PropProgress).U8(0).U8(1).U8(9).F32(progress);
        w.I32(cell.X).I32(cell.Y).F32(rotation).Zeros(4).Bytes(Convert.FromHexString(ObjectMid));
        w.U8(3).U8(RuneTail(runeRow).Object).U8(0x14).U8(0).U8(0);
        new StateMachineData([state], 0).Write(w);
        return w.U8(0).ToArray();
    }

    /// <summary>RitualRuneLight (32 B, 44 B with a scale): Positioned (u16 0, flags 0x04, | 0x20 with three f32 of scale),
    /// i32 -10, f32 78.125, 00, StateMachine (state), 00.</summary>
    public static byte[] Light((int X, int Y) cell, float rotation, int state, float? scale)
    {
        var w = new BlobWriter().U8(0).U8(0).I32(cell.X).I32(cell.Y).F32(rotation).U16(0);
        if (scale is float s) w.U8(0x24).U8(0).F32(s).F32(1).F32(1);
        else w.U8(0x04).U8(0);
        w.I32(-10).F32(78.125f).U8(0);
        new StateMachineData([state], 0).Write(w);
        return w.U8(0).ToArray();
    }

    /// <summary>BloodPool (32 B): flag 02 (the create wakes it), 1 property (its altar's id), Positioned, 00 01 00.</summary>
    public static byte[] Pool((int X, int Y) cell, float rotation, uint altar)
    {
        var w = new BlobWriter().U8(2).U8(1);
        w.U32(PropPoolOwner).U8(0).U8(1).U8(1).U32(altar);
        return w.I32(cell.X).I32(cell.Y).F32(rotation).Zeros(4).U8(0).U8(1).U8(0).ToArray();
    }

    /// <summary>RitualBlocker (34 B): flag 02, Positioned at rotation 0 with the scale (radius / 23, 1, 1), 00 01 00 00.</summary>
    public static byte[] Blocker((int X, int Y) cell, int radiusCells) =>
        new BlobWriter().U8(2).U8(0).I32(cell.X).I32(cell.Y).F32(0).U16(0).U8(0x20).U8(0)
            .F32(radiusCells / 23f).F32(1).F32(1).U8(0).U8(1).U8(0).U8(0).ToArray();

    /// <summary>The light's scale as its Positioned update (component 0): 20 00 + f32 x, 1, 1.</summary>
    public static byte[] LightScale(float scale) => new BlobWriter().U8(0x20).U8(0).F32(scale).F32(1).F32(1).ToArray();

    /// <summary>0x165: u32 BE the altar (its Interactable), u32 BE x, u32 BE y, u8 state (0 inactive, 1 ready, 2 running,
    /// 3 done).</summary>
    public static byte[] AltarState(uint altar, (int X, int Y) cell, int state) =>
        new LoginWriter().U16(AltarStateId).U32(altar).U32((uint)cell.X).U32((uint)cell.Y).U8((byte)state).ToArray();

    /// <summary>0x167: u32 BE the area's tribute.</summary>
    public static byte[] Tribute(int total) => new LoginWriter().U16(TributeId).U32((uint)Math.Max(0, total)).ToArray();

    /// <summary>0x166, the favours window: u32 BE tribute, u16 BE n, n x (u32 BE id, u16 BE cost, u16 BE cost, u8 flag),
    /// u8 altars, u8 done. The window's buy and defer messages are in no capture, so the server sends the window's
    /// rows and does not act on a choice.</summary>
    public static byte[] Favours(int tribute, IReadOnlyList<(uint Id, ushort Cost, ushort Cost2, byte Flag)> rows, int altars, int done)
    {
        var w = new LoginWriter().U16(FavoursId).U32((uint)Math.Max(0, tribute)).U16((ushort)rows.Count);
        foreach (var (id, cost, cost2, flag) in rows) w.U32(id).U16(cost).U16(cost2).U8(flag);
        return w.U8((byte)altars).U8((byte)done).ToArray();
    }
}
