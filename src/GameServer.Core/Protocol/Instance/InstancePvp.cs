using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// PvP queue bar messages on the instance connection (3.29.3c S→C 0x118 / 0x119, C→S 0x11a / 0x18d).
/// </summary>
public static class InstancePvp
{
    /// <summary>S→C 0x118 (3.29.3c Cls 0x1435e7120, write 0x1421ac250, read 0x1421ac320, dispatcher case 0x45 at
    /// 0x14215551a → instance +0x8cf8 → PC HUD 0x140dccbc0 → PvPQueueBar::Set 0x140abcc50): show or update the PvP
    /// queue bar (HUD +0x4b0) and optional countdown timer (HUD +0x4b8).</summary>
    public const ushort QueueStatusId = 0x0118;

    /// <summary>S→C 0x119 (3.29.3c Cls 0x1431768d8, empty, dispatcher case 0x46 at 0x142155612 → instance +0x8d78 →
    /// PC HUD 0x140dccce0): hide the PvP queue bar (and PvP timer outside PvP arenas).</summary>
    public const ushort QueueHideId = 0x0119;

    /// <summary>C→S 0x11a (2 bytes, empty, Cls 0x1431768d8, senders 0x140ac1750 / 0x1408e7070): Leave Queue button on
    /// PvPQueueBar or decline on the match-ready dialog.</summary>
    public const ushort LeaveQueueId = 0x011a;

    /// <summary>C→S 0x18d (2 bytes, empty, Cls 0x1431768d8, sender 0x140ac1700): Withdraw button on PvPQueueBar.</summary>
    public const ushort WithdrawId = 0x018d;

    /// <summary>
    /// Encodes S→C 0x118 matching 0x1421ac250 / 0x1421ac320: u16 BE 0x0118, u8 <paramref name="otherPlayers"/> (+0x12),
    /// compact WStr <paramref name="queueName"/> (+0x28 via 0x140e0b250), u8 <paramref name="isChallenge"/> (+0x10),
    /// u8 <paramref name="showWithdraw"/> (+0x11), raw u64 LE <paramref name="nextMatchTimeUnix"/> (+0x48).
    /// </summary>
    public static byte[] QueueStatus(byte otherPlayers, string queueName, bool isChallenge = false,
        bool showWithdraw = true, ulong nextMatchTimeUnix = 0) =>
        new LoginWriter()
            .U16(QueueStatusId)
            .U8(otherPlayers)
            .WStr(queueName)
            .Bool(isChallenge)
            .Bool(showWithdraw)
            .RawU64(nextMatchTimeUnix)
            .ToArray();

    /// <summary>Encodes S→C 0x119 (empty, 2 bytes): hides the PvP queue bar. Always returns a fresh array.</summary>
    public static byte[] QueueHide() => [(byte)(QueueHideId >> 8), (byte)(QueueHideId & 0xff)];
}
