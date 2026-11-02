namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// The Pantheon (Agents/docs/game/campaign/pantheon.md §4–5). C→S 0x1ee (3 bytes, sender 0x14097dfc0, the panel's select
/// handler): u16 id, u8 = PantheonPanelLayout row + 1; the row's IsMajorGod says whether it replaces the major or the
/// minor god. The client sends it only in a town, a hideout, the Rogue Harbour or while a session condition holds
/// (CanChangePantheon). The choice is shown by the Player component's +0x1ae / +0x1ad
/// (<see cref="InstanceEntities.PlayerPantheonUpdate"/>, <see cref="PlayerObject.PantheonMajor"/>).
/// </summary>
public static class InstancePantheon
{
    public const ushort ChooseGodId = 0x1ee;
    /// <summary>The whole message, id included (the framing table, InstanceProtocol).</summary>
    public const int ChooseGodLength = 3;

    /// <summary>The chosen PantheonPanelLayout row + 1 (0 when the message is short).</summary>
    public static byte ReadChooseGod(ReadOnlySpan<byte> m) => m.Length >= 3 ? m[2] : (byte)0;

    /// <summary>A C→S 0x1ee, as the client writes it (tests, the control bridge).</summary>
    public static byte[] ChooseGod(byte rowPlusOne) => [ChooseGodId >> 8, ChooseGodId & 0xff, rowPlusOne];
}
