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
/// DiesAfterTime; its live Transitionable state is 1).</para>
/// <para>The life pool must stay above 0 (live 2026-11-12): a live pool is what makes the client target the object, so the
/// statue can be hovered and clicked. At 0 the statue stays visible but is no longer targetable or interactable. The price is
/// the target nameplate the client draws for a targetable object: the shrine's name plus a rarity line it fills in as
/// "Undefined" because no monster/map data backs it.</para>
/// </summary>
public sealed record ShrineObject(int GridX, int GridY, float Rotation = 0, float? Height = null, string? Model = null,
    int Row = 0, uint Life = 1, bool Lesser = false)
{
    /// <summary>The components the client reads for a shrine, in its order.</summary>
    public static readonly string[] Components =
        ["Positioned", "BaseEvents", "Stats", "Animated", "Buffs", "Life", "ObjectMagicProperties", "Shrine", "MinimapIcon", "InteractionAction"];
    public static readonly string[] LesserComponents = [.. Components, "Transitionable", "DiesAfterTime"];

    /// <summary>Shrine in <see cref="Components"/>; S→C 0x2bc updates use this index.</summary>
    public const byte ShrineComponentIndex = 7;

    /// <summary>Bit 7 of the Shrine key byte (0x141e3e0c0 → +0x24): marks the shrine as claimed.</summary>
    public const byte ClaimedFlag = 0x80;

    /// <summary>Key 0xff: no shrine row (0x141e3e0c0 sets +0x24 = 1 and returns after the +0x50 byte).</summary>
    public const byte NoShrineRow = 0xff;

    /// <summary>
    /// S→C 0x2bc Shrine component update payload (slot 20 reader <c>0x1415e49f0</c>): calls creation reader
    /// <c>0x141e3e0c0</c> (key byte with bit 7 claimed flag + low 7-bit <c>Shrines.dat</c> row, u8 +0x50, and
    /// when key != 0xff an extra-row count u8), followed by u32 LE taker object id (+0x20). When claimed flips
    /// 0 → 1, the client plays Animated clip 0, ShrineSounds, and Shrines.Description for the local taker.
    /// </summary>
    public static byte[] UpdateBlob(byte row, uint takerObjectId, bool claimed = true, byte state = 0)
    {
        var w = new BlobWriter();
        byte key = row == NoShrineRow ? NoShrineRow : (byte)((row & 0x7f) | (claimed ? ClaimedFlag : 0));
        w.U8(key).U8(state);
        if (key != NoShrineRow) w.U8(0);
        w.U32(takerObjectId);
        return w.ToArray();
    }

    public byte[] Blob()
    {
        var w = new BlobWriter();
        w.U8(0);            // object flags
        w.U8(0);            // base data entry count
        foreach (string c in Lesser ? LesserComponents : Components)
        {
            switch (c)
            {
                case "Positioned": w.Positioned(GridX, GridY, Rotation, fixedHeight: Height is float z && !float.IsNaN(z) ? BlobWriter.FixedHeight(z) : null); break;
                case "Animated": if (Model is null) w.U8(0); else w.U8(1).WStr32(Model); break;
                case "Stats": w.U8(0).U32(0).U32(0).U8(0); break;
                case "Buffs": w.U16(0); break;
                // life, mana, energy shield pools (current, reserved, i16, u32, u32), then the flags byte.
                case "Life": Pool(w, Life); Pool(w, 0); Pool(w, 0); w.U8(0); break;
                // u16 flags: rarity in bits 0-2 (0 = normal) and bit 3 identified. Without bit 3 the client tags the target
                // with its unidentified line ("Undefined" for this object), which is what a hovered shrine showed.
                case "ObjectMagicProperties": w.U16(0x08).U8(0).U8(0); break;
                // Shrine (0x141e3e0c0): the key byte is the Shrines row (0xff = no shrine, returns after +0x50 without
                // reading the extra-shrine count), then the u8 at +0x50, then for key != 0xff the extra-row count.
                case "Shrine":
                    byte key = (byte)Row;
                    w.U8(key).U8(0);
                    if (key != NoShrineRow) w.U8(0);
                    break;
                case "MinimapIcon": w.U8((byte)(Row == NoShrineRow ? 0x06 : 0x00)); break;
                case "InteractionAction": w.U8(0); break;
                case "Transitionable": w.U8(1); break; // States are one-based; 0 crashes the native post-create event.
                // BaseEvents: no data.
            }
        }
        w.U8(0);            // tasks
        return w.ToArray();
    }

    static void Pool(BlobWriter w, uint current) => w.U32(current).U32(0).U16(0).U32(0).U32(0);
}
