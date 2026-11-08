namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// A blade a Bladefall volley leaves in the ground: <c>Metadata/MiscellaneousObjects/GroundBlade</c> (Positioned,
/// BaseEvents, Animated, Transitionable, StateMachine <c>blade_type</c>, LimitedLifespan; Render is not read). The blob
/// is the one the original server sends for Bladefall of Trarthus (2026-11-07):
/// <list type="bullet">
/// <item>object flags 0x02 (created awake, so no 0x1a9), no base data;</item>
/// <item>Positioned: x, y, a rotation, team 1, flags 0, 0;</item>
/// <item>Animated 0x0b: bit0 + bit1 a MiscAnimated row (BladefallGroundBladeAudio) in place of a path, bit3 animation 2
/// from time 0 at speed 1 (its fall);</item>
/// <item>Transitionable 1; StateMachine: one shared state (<c>blade_type</c> 0), no timer; LimitedLifespan reads nothing.</item>
/// </list>
/// About a second later the original server sets its Animated back to the type's own (0x2ba, <see cref="Landed"/>).
/// </summary>
public sealed record GroundBladeObject(int GridX, int GridY, float Rotation, uint MiscAnimatedRow, int BladeType = 0)
{
    public const int FallAnimation = 2;

    /// <summary>The Animated component's index in the type's component order, for its 0x2ba update.</summary>
    public const byte AnimatedIndex = 2;

    public byte[] Blob()
    {
        var w = new BlobWriter();
        w.U8(0x02).U8(0);                                              // object flags (awake), base data entries
        w.I32(GridX).I32(GridY).F32(Rotation).U16(1).U8(0).U8(0);     // Positioned
        w.U8(0x0b).U32(MiscAnimatedRow).U32(FallAnimation).F32(0).F32(1);   // Animated
        w.U8(1);                                                       // Transitionable
        w.U8(1).U8(0).CompactI(BladeType).U8(0);                       // StateMachine
        w.U8(0);                                                       // tasks
        return w.ToArray();
    }

    /// <summary>The 0x2ba data that sets the blade's Animated back to the type's own model once it has fallen.</summary>
    public static byte[] Landed() => [0];
}
