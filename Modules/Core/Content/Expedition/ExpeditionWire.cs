using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Modules.Expedition;

/// <summary>
/// The Expedition encounter on the wire: the payloads of ruleset ExpeditionRules' messages (S→C 0x2c9 data and C→S 0x2ca
/// actions, <see cref="InstanceRulesets"/>; the first payload byte is the message's type), and the objects' create
/// blobs. The payloads' fields are little-endian; every layout here is the two official Expedition captures'.
/// </summary>
public static class ExpeditionWire
{
    static void WriteUVarint(MemoryStream stream, int value)
    {
        var w = new LoginWriter();
        InstanceProtocol.CompactUInt(w, (uint)value);
        stream.Write(w.ToArray());
    }

    /// <summary>A signed value: the blob reader's signed compact int. The summary after one covered remnant is
    /// <c>03 02 8258 0f b81c 8050 0100 0700</c>, and the two values are that remnant's mods' rolls, 15 (<c>0f</c>) and 80
    /// (<c>8050</c>).</summary>
    static void WriteSVarint(MemoryStream stream, int value)
    {
        var w = new LoginWriter();
        InstanceProtocol.CompactInt(w, value);
        stream.Write(w.ToArray());
    }

    static ushort U16(int value) => (ushort)Math.Clamp(value, 0, ushort.MaxValue);

    static byte[] Cells(byte type, params (int X, int Y)[] cells)
    {
        var payload = new byte[1 + 8 * cells.Length];
        payload[0] = type;
        for (int i = 0; i < cells.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(1 + 8 * i), cells[i].X);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(5 + 8 * i), cells[i].Y);
        }
        return payload;
    }

    /// <summary>Type 00: the detonator, its state (0 placed, 2 detonated) and its cell.</summary>
    public static byte[] Detonator(byte state, (int X, int Y) cell)
    {
        var payload = Cells(0, cell);
        return [payload[0], state, .. payload.AsSpan(1)];
    }

    /// <summary>Type 00 state 1: the detonator, the chain's end and the explosives left.</summary>
    public static byte[] Active((int X, int Y) detonator, (int X, int Y) end, ushort left)
    {
        var cells = Cells(0, detonator, end);
        var payload = new byte[2 + 16 + 2];
        payload[1] = 1;
        cells.AsSpan(1).CopyTo(payload.AsSpan(2));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(18), left);
        return payload;
    }

    /// <summary>Type 01: the chain's end, the explosives' count and every explosive.</summary>
    public static byte[] Placed((int X, int Y) end, IReadOnlyList<(int X, int Y)> explosives)
    {
        var head = Cells(1, end);
        var list = Cells(0, [.. explosives]);
        return [.. head, (byte)explosives.Count, .. list.AsSpan(1)];
    }

    /// <summary>Type 03: a compact count and that many (compact stat, signed compact value), then u16 covered and u16
    /// total. The stats are the covered remnants' modifiers' own, summed (<see cref="ExpeditionTables.SumStats"/>).</summary>
    public static byte[] Summary(IReadOnlyList<(uint Stat, int Value)> stats, int covered, int total)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(3);
        WriteUVarint(stream, stats.Count);
        foreach (var (stat, value) in stats)
        {
            WriteUVarint(stream, (int)stat);
            WriteSVarint(stream, value);
        }
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(tail, (ushort)covered);
        BinaryPrimitives.WriteUInt16LittleEndian(tail[2..], (ushort)total);
        stream.Write(tail);
        return stream.ToArray();
    }

    /// <summary>Type 04: the dirt, a u16 count and then (x / 10, y / 10, size 3 / 2 / 1) each, the large ones first.</summary>
    public static byte[] Dirt(IReadOnlyList<(int X, int Y, byte Size)> dirt)
    {
        var payload = new byte[1 + 2 + dirt.Count * 3];
        payload[0] = 4;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1), (ushort)dirt.Count);
        for (int i = 0; i < dirt.Count; i++)
        {
            payload[3 + i * 3] = (byte)Math.Clamp(dirt[i].X, 0, 255);
            payload[4 + i * 3] = (byte)Math.Clamp(dirt[i].Y, 0, 255);
            payload[5 + i * 3] = dirt[i].Size;
        }
        return payload;
    }

    /// <summary>Type 08: the markers' extent, four i32 (min x, min y, max x + 1, max y + 1).</summary>
    public static byte[] MarkersBox((int MinX, int MinY, int MaxX, int MaxY) box) =>
        Cells(8, (box.MinX, box.MinY), (box.MaxX, box.MaxY));

    /// <summary>Type 06: the explosives' extent, four u16 (min x, min y, max x + 1, max y + 1).</summary>
    public static byte[] ExplosivesBox((int MinX, int MinY, int MaxX, int MaxY) box)
    {
        var payload = new byte[1 + 8];
        payload[0] = 6;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1), U16(box.MinX));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(3), U16(box.MinY));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(5), U16(box.MaxX));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(7), U16(box.MaxY));
        return payload;
    }

    /// <summary>Type 07: a blast, 01 then u16 x, u16 y and the radius.</summary>
    public static byte[] Blast((int X, int Y) cell, byte radius)
    {
        var payload = new byte[1 + 1 + 4 + 1];
        payload[0] = 7;
        payload[1] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), U16(cell.X));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), U16(cell.Y));
        payload[6] = radius;
        return payload;
    }

    /// <summary>An action's placement: type 00, i32 x, i32 y and the explosives left [capture: all 15 placements]. Null
    /// for any other action.</summary>
    public static (int X, int Y)? ReadPlacement(ReadOnlySpan<byte> payload) =>
        payload.Length >= 9 && payload[0] == 0
            ? (BinaryPrimitives.ReadInt32LittleEndian(payload[1..]), BinaryPrimitives.ReadInt32LittleEndian(payload[5..]))
            : null;

    /// <summary>
    /// Positioned: i32 x, i32 y, f32 rotation, u16 team, u8 flags (0x20 a uniform scale and two stretches as three f32),
    /// u8 0, then what the flags carry. Every Expedition create of the two captures carries flags 0x00 (16 bytes, no world
    /// position) except the 24 elite markers, which carry 0x20 with (1.5, 1.0, 1.0).
    /// </summary>
    static void Positioned(BlobWriter w, (int X, int Y) cell, float rotation, float scale)
    {
        w.I32(cell.X).I32(cell.Y).F32(rotation).U16(0).U8((byte)(scale > 0 ? 0x20 : 0x00)).U8(0x00);
        if (scale > 0) w.F32(scale).F32(1f).F32(1f);
    }

    /// <summary>
    /// An Expedition object's create blob (detonator, marker, remnant, explosive, pole, fuse, chest, the blast's effect):
    /// the first byte 0x02 makes the create wake the object itself (InstanceEntities.CreateInWakeZone), then one entry per
    /// component the client deserializes for this type, in the type's own order (<see cref="ExpeditionTables.ObjectType"/>),
    /// then tasks 0. The client's create reader walks every component of the type, so the whole list has to be there: a
    /// blob that ends early is read past its end and closes the client.
    /// <para>
    /// Null when the type has no <c>object_types</c> row, or when its read list holds a component this writer has no
    /// measured body for (<see cref="Writable"/>), or a fuse without its <see cref="Beam"/>: then no create is sent.
    /// </para>
    /// </summary>
    public static byte[]? Object(string type, (int X, int Y) cell, double rotation, byte level = 0,
        string? ao = null, byte animated = 0x00, byte interaction = 0x00, float scale = 0,
        IReadOnlyList<ItemMod>? mods = null, IReadOnlyList<int>? stateValues = null, Beam? beam = null, byte[]? baseData = null)
    {
        var (components, states) = ExpeditionTables.ObjectType(type);
        if (components.Count == 0 || !components.All(Writable.Contains)) return null;
        if (components.Contains("Beam") && beam is null) return null;
        if (stateValues is not null && states is not null) states = states with { SharedStates = stateValues };
        // The flags byte, then the base-data entries (a count and the entries as the official writes them).
        var w = new BlobWriter().U8(0x02).U8((byte)(baseData is null ? 0 : 1));
        if (baseData is not null) w.Bytes(baseData);
        foreach (string component in components)
            switch (component)
            {
                case "Positioned": Positioned(w, cell, (float)rotation, scale); break;
                // u8 flags; bit 0 adds a u32-counted UTF-16 .ao path, which a type with no model of its own needs (the
                // marker). The detonator's official creates carry 0x40, which reads nothing more.
                case "Animated":
                    if (ao is { Length: > 0 }) w.U8((byte)(animated | 0x01)).WStr32(ao);
                    else w.U8(animated);
                    break;
                case "InteractionAction": w.U8((byte)(interaction & ~0x04)); break; // u8 flags (bit 2 adds a u8 and a u16)
                case "MinimapIcon": w.U8(0x00); break;
                // No stat pair, then the 9 bytes every Stats segment of an official create ends with: u32 0, f32 3.0, u8 1
                // (all 13 remnant and 24 chest creates).
                case "Stats": w.U8(0x00).U32(0).F32(3.0f).U8(0x01); break;
                // A remnant and a dug chest carry rarity 0, identified: the official remnant's flags are 0x0028 (identified
                // and an explicit list of two mods), the chest's 0x0008. Then the component's own trailing byte.
                case "ObjectMagicProperties":
                    MagicProperties.Write(w, rarity: 0, identified: true, mods ?? []);
                    w.U8(0x00);
                    break;
                case "StateMachine": (states ?? new StateMachineData([], 0)).Write(w); break;
                // The fuse: c8 01 b7 0c 00, then one 16-byte record per end — its world point (the cell's centre), its
                // object id and its kind. The same in all 38 official fuse creates; what the five leading bytes mean is
                // not known.
                case "Beam":
                    w.U8(0xc8).U8(0x01).U16(0x0cb7).U8(0x00);
                    foreach (var (end, kind) in new[] { (beam!.From, beam.FromKind), (beam.To, beam.ToKind) })
                    {
                        var (wx, wy) = ExpeditionTables.BeamPos(end.Cell);
                        w.F32(wx).F32(wy).U32(end.Id).U32(kind);
                    }
                    break;
                // The area level in the middle of three bytes (the official chest's 00 49 00 at level 73).
                case "Chest": w.U8(0x00).U8(level).U8(0x00); break;
                case "Transitionable": w.U8(0x01); break;
                // BaseEvents, LimitedLifespan, Functions, Preload, Render, Targetable, WorldDescription: no data.
            }
        return w.U8(0x00).ToArray(); // tasks
    }

    /// <summary>A StateMachine update of an object of this type (S→C 0x2bc, the full layout: <see cref="StateMachineData.Write"/>,
    /// <c>InstanceEntities.StateMachineUpdate</c>): the type's own states with these values, <paramref name="overridden"/> making the
    /// client keep them (flags 0x02). Null when the type has no StateMachine or a different number of states.</summary>
    public static byte[]? StateUpdate(uint id, string type, IReadOnlyList<int> values, bool overridden)
    {
        var (components, states) = ExpeditionTables.ObjectType(type);
        int index = components.ToList().IndexOf("StateMachine");
        if (index < 0 || states is null || values.Count != states.SharedStates.Count) return null;
        return InstanceEntities.StateMachineUpdate(id, (byte)index,
            states with { SharedStates = values, Overridden = overridden ? Enumerable.Range(0, values.Count).ToHashSet() : new HashSet<int>() });
    }

    /// <summary>An update of one component of an object of this type, by the component's name in the type's own order.</summary>
    public static byte[]? ComponentUpdate(uint id, string type, string component, params byte[] data)
    {
        int index = ExpeditionTables.ObjectType(type).Components.ToList().IndexOf(component);
        return index < 0 ? null : InstanceEntities.ComponentUpdate(id, (byte)index, data);
    }

    /// <summary>The components <see cref="Object"/> has a body for. A type that reads any other one gets no create.</summary>
    static readonly HashSet<string> Writable =
        ["Positioned", "BaseEvents", "Animated", "InteractionAction", "MinimapIcon", "Stats", "ObjectMagicProperties",
         "StateMachine", "Chest", "Transitionable", "LimitedLifespan", "Functions", "Preload", "Render", "Targetable",
         "WorldDescription", "Beam"];

    /// <summary>One end of a fuse: the object it is attached to and that object's cell.</summary>
    public sealed record BeamEnd(uint Id, (int X, int Y) Cell);

    /// <summary>A fuse's two ends and their kinds, in the order the official writes them (the fuse's own cell is
    /// <see cref="From"/>'s in all 38 creates).</summary>
    public sealed record Beam(BeamEnd From, uint FromKind, BeamEnd To, uint ToKind);

    /// <summary>The kinds a Beam names its ends by [capture: a pole 9 (56 ends), an explosive 6 (9 ends); the detonator
    /// is 5 in one capture and 6 in the other, one end each, and 5 is sent].</summary>
    public const uint BeamDetonator = 5, BeamExplosive = 6, BeamPole = 9;
}
