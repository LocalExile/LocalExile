namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// A quest object of a room or a tile that the server creates (the Gateway's quest engine, Agents/docs/protocol/12-quests.md
/// §5): a seal, a lever, a door, a quest chest, an altar. Its components are its .ot chain's deserialized ones
/// (data/object-types.json, scripts/re/ot_components.py: the first <c>read</c> of them, in order); this blob writes each the
/// way the objects already traced write it:
/// <list type="bullet">
/// <item>Positioned: x, y, rotation, u16, flags0 (0x04: a fixed height, i32 steps of 7.8125 and f32 z, as a tile record
/// gives it), flags1 0x02 with hidden 0 / shown 1 (the Glyph Wall, the Rhoa Nests);</item>
/// <item>Animated: 0 (the type's own model) or 1 and a u32-counted UTF-16 .ao path (a tile record's virtual model);</item>
/// <item>Transitionable (0x1421c24b0): u8 state, 1 … num_states (0 is invalid: it crashed the client);</item>
/// <item>Chest (0x141e138b0): u8 opened (bit 0), u8, u8; Stats and ObjectMagicProperties as an area transition's;</item>
/// <item>TriggerableBlockage (0x141e44290): u8 blocked, and only while blocked a u8 count (0 here);</item>
/// <item>NPC: has dialogue 0, no component that must be alive (0xff), as a boss that speaks;</item>
/// <item>InteractionAction, MinimapIcon: one flags byte, 0; BaseEvents: nothing.</item>
/// </list>
/// Types with any other deserialized component are not written (<see cref="CanSerialize"/>).
/// </summary>
public sealed record QuestObject(int GridX, int GridY, IReadOnlyList<string> Components, float Rotation = 0, float? Height = null,
    string? Model = null, byte TransitionState = 1, bool Opened = false, bool Blocked = false)
{
    static readonly HashSet<string> Supported = ["Positioned", "BaseEvents", "Animated", "Transitionable", "InteractionAction", "MinimapIcon",
        "TriggerableBlockage", "Chest", "Stats", "ObjectMagicProperties", "NPC"];

    /// <summary>Whether <see cref="Blob"/> can write a type with these deserialized components.</summary>
    public static bool CanSerialize(IReadOnlyList<string> components) =>
        components.Count > 0 && components[0] == "Positioned" && components.All(Supported.Contains);

    /// <summary>The index of the Transitionable component (for 0x2ba updates), or −1.</summary>
    public static int TransitionableIndex(IReadOnlyList<string> components) => components.ToList().IndexOf("Transitionable");

    public byte[] Blob()
    {
        if (!CanSerialize(Components)) throw new ArgumentException($"unsupported components: {string.Join(", ", Components)}");
        var w = new BlobWriter();
        w.U8(0);            // object flags
        w.U8(0);            // base data entry count
        foreach (string c in Components)
        {
            switch (c)
            {
                case "Positioned":
                    w.I32(GridX).I32(GridY).F32(Rotation).U16(0).U8((byte)(Height is null ? 0 : 0x04)).U8(0x02);
                    if (Height is float z) w.I32((int)MathF.Round(z / 7.8125f)).F32(z);
                    w.U8(0).U8(1);
                    break;
                case "Animated": if (Model is null) w.U8(0); else w.U8(1).WStr32(Model); break;
                case "Transitionable": w.U8((byte)(Math.Max((byte)1, TransitionState) & 0x7f)); break;
                case "Chest": w.U8((byte)(Opened ? 1 : 0)).U8(0).U8(0); break;
                case "Stats": w.U8(0).U32(0).U32(0).U8(0); break;
                case "ObjectMagicProperties": w.U16(0).U8(0).U8(0); break;
                case "TriggerableBlockage": if (Blocked) w.U8(1).U8(0); else w.U8(0); break;
                case "NPC": w.U8(0).U8(0xff); break;
                case "InteractionAction": w.U8(0); break;
                case "MinimapIcon": w.U8(0); break;
                // BaseEvents: no data.
            }
        }
        w.U8(0);            // tasks
        return w.ToArray();
    }
}
