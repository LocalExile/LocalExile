using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Modules.Abyss;

/// <summary>
/// The Abyss objects' create blobs, byte for byte as the two official captures carry them, and the chest's click. The first
/// byte is the object flags: 0x02 makes the create wake the object itself (<see cref="InstanceEntities.CreateInWakeZone"/>),
/// as both captures do.
/// </summary>
public static class AbyssWire
{
    /// <summary>Stats with no pair and its 9-byte tail, and the 8 constant bytes the captured chest carries after it
    /// [capture 1/1].</summary>
    const string StatsTail = "000000000000404001";
    const string ChestTail = "0800000000000100";

    /// <summary>AbyssCrack: Positioned (flags 0x40 | the mirror bit, and the world position), Animated 0x03 + its
    /// MiscAnimated row, Transitionable (0x80 on the first sight), MinimapIcon 0, tasks 0 [capture 72/72].</summary>
    public static byte[] Crack((int X, int Y) cell, (float X, float Y) world, float rotation, uint animRow, int mirror, byte state)
    {
        var w = new BlobWriter().U8(0x02).U8(0);
        Positioned(w, cell, rotation, (byte)(0x40 | (mirror & 1)), world, height: false);
        w.U8(0x03).U32(animRow);
        w.U8((byte)(0x80 | (state & 0x7f)));
        w.U8(0x00);
        return w.U8(0x00).ToArray();
    }

    /// <summary>AbyssFinalNodeBase: as the crack, Transitionable 1, TriggerableBlockage (blocked, no entry), MinimapIcon 0,
    /// tasks 0 [capture 2/2].</summary>
    public static byte[] Pit((int X, int Y) cell, (float X, float Y) world, float rotation, uint animRow, byte state = AbyssTables.PitClosed)
    {
        var w = new BlobWriter().U8(0x02).U8(0);
        Positioned(w, cell, rotation, 0x40, world, height: false);
        w.U8(0x03).U32(animRow);
        w.U8(state);                                    // Transitionable: closed when created
        w.U8(0x01).U8(0x00);                           // TriggerableBlockage: blocked, no entry list
        w.U8(0x00);                                    // MinimapIcon
        return w.U8(0x00).ToArray();                   // tasks
    }

    /// <summary>AbyssPlinth: two properties carrying the pit's entity id, Positioned (flags 0x04: a fixed height, 0), an
    /// .ao path as its Animated, StateMachine down, tasks 0 [capture 1/1].</summary>
    public static byte[] Plinth((int X, int Y) cell, float rotation, uint pit, string ao)
    {
        var w = new BlobWriter().U8(0x02).U8((byte)AbyssTables.PitPropertyKeys.Length);
        foreach (uint key in AbyssTables.PitPropertyKeys) w.U32(key).U8(0x00).U8(0x01).U8(0x01).U32(pit);
        Positioned(w, cell, rotation, 0x04, default, height: true);
        w.U8(0x01).WStr32(ao);                         // Animated: an .ao path
        new StateMachineData([0], 0).Write(w);         // StateMachine: one value, down
        return w.U8(0x00).ToArray();                   // tasks
    }

    /// <summary>The reward chest: Positioned (flags 0x02: shown), then the captured Animated / Chest / Stats bytes: the area
    /// level in the middle of Chest's three, no stat and the stats tail, and the 8 constant bytes [capture 1/1: 0x2d at the
    /// level 45 area].</summary>
    public static byte[] Chest((int X, int Y) cell, float rotation, byte level)
    {
        var w = new BlobWriter().U8(0x02).U8(0);
        Positioned(w, cell, rotation, 0x02, default, height: false);
        w.U8(0x00);                                    // Animated: the type's own model
        w.U8(0x00).U8(level).U8(0x00);                 // Chest: the area level in the middle, two zero bytes
        w.U8(0x00);                                    // Stats: no pair
        w.Bytes(Convert.FromHexString(StatsTail));
        return w.Bytes(Convert.FromHexString(ChestTail)).ToArray();
    }

    /// <summary>S→C 0x1bd, the click action the official open of the chest starts with: chest, the standard order
    /// (key and checkpoint 0, as every world record here carries, so the frame keeps it ahead of the open), 01 00 00, player
    /// [capture 1/1: the same body, with the player and 0x9002 where the order is]. The client's open (its highlight and
    /// name off) follows it.</summary>
    public static byte[] ChestClick(uint chest, uint player) =>
        new LoginWriter().U16(0x1bd).U32(chest).U32(0).U16(0).U8(1).U8(0).U8(0).U32(player).ToArray();

    // Positioned: i32 x, i32 y, f32 rotation, u16 team, u8 flags (0x40 world position, 0x04 fixed height, 0x02 shown),
    // u8 0, and what those flags carry: the world position as two f32, or the height as an i32 in 7.8125 steps and an f32.
    static void Positioned(BlobWriter w, (int X, int Y) cell, float rotation, byte flags, (float X, float Y) world, bool height)
    {
        w.I32(cell.X).I32(cell.Y).F32(rotation).U16(0).U8(flags).U8(0x00);
        if ((flags & 0x40) != 0) w.F32(world.X).F32(world.Y);
        if (height) w.I32(0).F32(0);
    }
}
