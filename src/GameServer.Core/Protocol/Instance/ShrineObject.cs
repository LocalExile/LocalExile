namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// A shrine of a map (`Metadata/Shrines/Shrine`): its .ot chain deserializes Positioned, BaseEvents, Stats, Animated,
/// Buffs, Life, ObjectMagicProperties, Shrine, MinimapIcon and InteractionAction (data/object-types.json, read = 10;
/// Render and Targetable are not reached by the read loop). Written as the objects already traced write their
/// components: QuestObject's Positioned/Stats/Animated/MinimapIcon/InteractionAction, a monster's Buffs (u16 0) and
/// Life (three pools, then the flags byte), an area transition's ObjectMagicProperties, and the Shrine component's
/// decoded form (0x141e3e0c0: u8 key — 0xff means no shrine, else bit 7 is stored at +0x24 and the low 7 bits are the
/// Shrines row the client looks up for the icon and name — then the u8 it stores at +0x50).
/// <para>The type is static and non-blocking, object size 7 (LesserShrine is 2 and adds Transitionable and
/// DiesAfterTime, which this writer does not carry).</para>
/// <para>The life pool must stay above 0 (live 2026-11-12): a live pool is what makes the client target the object, so the
/// statue can be hovered and clicked. At 0 the statue stays visible but is no longer targetable or interactable. The price is
/// the target nameplate the client draws for a targetable object: the shrine's name plus a rarity line it fills in as
/// "Undefined" because no monster/map data backs it.</para>
/// </summary>
public sealed record ShrineObject(int GridX, int GridY, float Rotation = 0, float? Height = null, string? Model = null,
    int Row = 0, uint Life = 1)
{
    /// <summary>The components the client reads for a shrine, in its order.</summary>
    public static readonly string[] Components =
        ["Positioned", "BaseEvents", "Stats", "Animated", "Buffs", "Life", "ObjectMagicProperties", "Shrine", "MinimapIcon", "InteractionAction"];

    public byte[] Blob()
    {
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
                case "Stats": w.U8(0).U32(0).U32(0).U8(0); break;
                case "Buffs": w.U16(0); break;
                // life, mana, energy shield pools (current, reserved, i16, u32, u32), then the flags byte.
                case "Life": Pool(w, Life); Pool(w, 0); Pool(w, 0); w.U8(0); break;
                // u16 flags: rarity in bits 0-2 (0 = normal) and bit 3 identified. Without bit 3 the client tags the target
                // with its unidentified line ("Undefined" for this object), which is what a hovered shrine showed.
                case "ObjectMagicProperties": w.U16(0x08).U8(0).U8(0); break;
                // Shrine (0x141e3e0c0): the key byte is the Shrines row (the client copies that row for the icon it draws
                // over the object), then the u8 the reader stores at +0x50, then its closed list's trailing byte.
                case "Shrine": w.U8((byte)Row).U8(0).U8(0); break;
                case "MinimapIcon": w.U8(0); break;
                case "InteractionAction": w.U8(0); break;
                // BaseEvents: no data.
            }
        }
        w.U8(0);            // tasks
        return w.ToArray();
    }

    static void Pool(BlobWriter w, uint current) => w.U32(current).U32(0).U16(0).U32(0).U32(0);
}
