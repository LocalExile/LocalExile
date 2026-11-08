using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// Pausing the area from the Escape menu (Agents/docs/protocol/22-pause.md).
/// <para>Flow: Escape opens the menu (game state "GamePause", 0x140cf0f10) → instance vtable +0x20 (0x142169660) sends
/// C→S 0x1d4 with 1 → the server pauses and answers S→C 0x1d3 with both flags → the client pauses its world. Resume
/// ("GameUnpause", 0x140cf13d0 → 0x1421696d0) sends 0x1d4 with 0 and the server answers 0x1d3 with 0. The client sends
/// 0x1d4 only where 0x1421494b0 allows a pause (not a town, a hideout and a few other area kinds), and only on the first
/// open and last close (counter at instance +0xcf24).</para>
/// </summary>
public static class InstancePause
{
    public const ushort RequestId = 0x1d4, StateId = 0x1d3;

    /// <summary>C→S 0x1d4 (3 bytes, class 0x1435ca088, write 0x140e27920): u16 id, u8 1 = pause, 0 = resume.</summary>
    public const int RequestLength = 3;

    public static bool ReadRequest(ReadOnlySpan<byte> m) => m[2] != 0;

    /// <summary>
    /// S→C 0x1d3 (world group, class 0x1435e8538, read 0x1421cacc0; world handler 0x14218ddf0): u8 flags, bit 0 →
    /// world +0xb9c, bit 1 → world +0xb9d (world vtable +0x1d8, the client's 0x141660c50). The world is paused while
    /// both are set: the "Pause" environment applies, sounds stop and the Escape menu shows PauseInstance. A world
    /// message: the client queues it until the world is active.
    /// </summary>
    public static byte[] State(bool paused) => new LoginWriter().U16(StateId).U8(paused ? (byte)0x03 : (byte)0x00).ToArray();
}
