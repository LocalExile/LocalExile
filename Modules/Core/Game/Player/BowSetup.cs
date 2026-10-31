using GameServer.Core.Protocol.Instance;


/// <summary>
/// The model record of a character whose main hand holds a bow, as the live server sends it. Frida 2026-10-25 on the
/// real client's player object (Inventories entries, a Ranger with a Crude Bow):
/// <code>
/// slot 0  (right hand)  type 14 (Bows)      stance 5 (Bow)   art DefaultArrow  (0xB2F6)
/// slot 1  (left hand)   type 0x68 (none)    stance 0xff      art the bow itself
/// slot 7  (the back)    type 0x68 (none)    stance 0xff      art DefaultQuiver (0x328B)
/// </code>
/// A bow is held in the left hand, so its model slot is 1, not the main hand's: with the bow in slot 0 the client drew
/// it in the right hand, hanging, and put the quiver in the left hand instead of on the back. The weapon type and stance
/// stay on the right hand's entry (the client reads the hands' types from slots 0 and 1 for skill restrictions). The
/// arrow and the quiver are the server's default bow visuals: no base item uses them, and the quiver appears even when
/// no quiver item is worn.
/// </summary>
static class BowSetup
{
    /// <summary>The client's weapon type for a hand that holds no weapon (13-items.md "What the model wears").</summary>
    public const byte NoWeaponType = 0x68;

    /// <summary>The stance byte of an entry that is not a weapon.</summary>
    public const byte NoStance = 0xff;

    /// <summary>The three entries of a bow character's record: the arrow in the right hand, the bow in the left, the
    /// quiver on the back. <paramref name="arrowArt"/> and <paramref name="quiverArt"/> are ItemVisualIdentity HASH16s.</summary>
    public static VisibleItem[] Entries(ItemCatalog.BaseType bow, ushort arrowArt, ushort quiverArt) =>
    [
        // slot 0 is the main hand: its weapon type and stance, the art of what that hand draws (an arrow for a bow).
        new(0, arrowArt) { Type = bow.ClassRow is >= 0 and < 255 ? (byte)(bow.ClassRow + 1) : (byte)14, Stance = bow.Stance },
        new(1, bow.VisualIdentity) { Type = NoWeaponType, Stance = NoStance },
        new(7, quiverArt) { Type = NoWeaponType, Stance = NoStance },
    ];
}
