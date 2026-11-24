using System.Buffers.Binary;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// HUD mission timers and countdown timers (S->C <c>0x163</c> and S->C <c>0x162</c>).
/// <para>
/// Verified against 3.29.3c instance dispatcher <c>0x142150ae0</c>:
/// <list type="bullet">
/// <item><description><c>0x163</c> (case <c>0x6a</c>, <c>0x142156ba8</c>, Cls <c>0x1435ca870</c>, write <c>0x141f99580</c> / read <c>0x141fa67c0</c>): 7 bytes (<c>u8 timerType</c>, <c>u32 BE seconds</c>) -> instance <c>+0x91f8</c> (buffered pre-<c>0x21</c> at <c>+0xce80</c>), routing <c>Heist</c> (<c>8</c>) to <c>0x1404c3ad0</c> and all others to <c>MissionTimerManager::Start</c> (<c>0x1402296e0</c>). <c>seconds == 0</c> hides the timer.</description></item>
/// <item><description><c>0x162</c> (case <c>0x69</c>, <c>0x142156bfa</c>, Cls <c>0x1435cae30</c>, write <c>0x141f9a710</c> / read <c>0x141f9a790</c>): 10 bytes with mixed endianness (<c>u32 BE count</c>, <c>u32 LE countdownTimerId</c>) -> instance <c>+0x9278</c> -> PC <c>0x140dd9120</c> / Console <c>0x140d8e080</c> -> <c>0x14071e470</c>. <c>count == 0</c> or <c>countdownTimerId == 2</c> hides the widget.</description></item>
/// </list>
/// </para>
/// </summary>
public static class InstanceTimers
{
    public const ushort NonQuestTrackerRecordId = 0x15f;
    public const ushort CountdownTimerId = 0x162;
    public const ushort MissionTimerId = 0x163;

    /// <summary>One entry in <see cref="NonQuestTrackerRecord"/> (S→C 0x015f, write <c>0x1421ae770</c> +
    /// <c>0x1429a5f50</c>, read <c>0x141fa9720</c> + <c>0x1429a60d0</c>): <c>u16 BE key</c>,
    /// <c>u16 BE clientStringRow1Based</c> (0 = none, else 1-based <c>ClientStrings.dat</c> row),
    /// <c>u16 BE value</c>, plus optional numeric/string format arguments.</summary>
    public sealed record TrackerEntry(
        ushort Key,
        ushort ClientStringRow1Based,
        ushort Value = 0,
        IReadOnlyList<(byte Slot, int Value)>? NumericArgs = null,
        IReadOnlyList<string>? StringArgs = null);

    /// <summary>S→C 0x015f (write <c>0x1421ae770</c>, read <c>0x1421ae910</c> -> <c>0x141fa9720</c>,
    /// dispatcher case 0x67): non-quest HUD tracker record (<c>u16 BE trackerId</c>, <c>u8 flags</c>, <c>u8 count</c>,
    /// followed by <c>count</c> × <see cref="TrackerEntry"/>).</summary>
    public static byte[] NonQuestTrackerRecord(ushort trackerId, byte flags, IReadOnlyList<TrackerEntry> entries)
    {
        int count = Math.Min(entries.Count, byte.MaxValue);
        var w = new LoginWriter().U16(NonQuestTrackerRecordId).U16(trackerId).U8(flags).U8((byte)count);
        for (int i = 0; i < count; i++)
        {
            var e = entries[i];
            int numCount = Math.Min(e.NumericArgs?.Count ?? 0, byte.MaxValue);
            int strCount = Math.Min(e.StringArgs?.Count ?? 0, byte.MaxValue);
            w.U16(e.Key).U16(e.ClientStringRow1Based).U16(e.Value).U8((byte)numCount).U8((byte)strCount);
            for (int n = 0; n < numCount; n++)
            {
                var (slot, value) = e.NumericArgs![n];
                w.U8(slot);
                InstanceProtocol.CompactInt(w, value);
            }
            for (int s = 0; s < strCount; s++) w.WStr(e.StringArgs![s]);
        }
        return w.ToArray();
    }

    /// <summary>1-based <c>CountdownTimers.dat</c> row 0 (<c>MercenaryCountdown</c>).</summary>
    public const uint MercenaryCountdownTimerId = 1;

    /// <summary>1-based <c>MissionTimerTypes.dat</c> IDs (<c>row + 1</c>, stored at child <c>+0x2c0</c> in <c>0x140229170</c>).</summary>
    public static class MissionTimerType
    {
        public const byte Generic = 1;
        public const byte Incursion = 2;
        public const byte Zana = 3;
        public const byte Legion = 4;
        public const byte Breach = 5;
        public const byte Abyss = 6;
        public const byte Affliction = 7;
        public const byte Heist = 8;
        public const byte Ultimatum = 9;
        public const byte Royale = 10;
        public const byte Hellscape = 11;
        public const byte Kirac = 12;
        public const byte HideoutPvp = 13;
        public const byte Crucible = 14;
        public const byte BrambleBash = 15;
        public const byte Brequel = 16;
        public const byte Deepwater = 17;
        public const byte HideoutMaze = 18;

        public const byte Min = Generic;
        public const byte Max = HideoutMaze;
    }

    /// <summary>
    /// S->C <c>0x163</c> (7 bytes): <c>u16 BE 0x0163</c>, <c>u8 timerType</c> (1-based <c>MissionTimerTypes.dat</c> row + 1,
    /// <c>1..18</c>), <c>u32 BE seconds</c> (<c>0</c> hides).
    /// </summary>
    public static byte[] MissionTimer(byte timerType, uint seconds)
    {
        if (timerType is < MissionTimerType.Min or > MissionTimerType.Max)
            throw new ArgumentOutOfRangeException(nameof(timerType), timerType,
                $"Mission timer type must be in {MissionTimerType.Min}..{MissionTimerType.Max}.");
        var buf = new byte[7];
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0, 2), MissionTimerId);
        buf[2] = timerType;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(3, 4), seconds);
        return buf;
    }

    /// <summary>Hides the HUD mission timer for <paramref name="timerType"/> by sending <c>seconds = 0</c>.</summary>
    public static byte[] HideMissionTimer(byte timerType) => MissionTimer(timerType, 0);

    /// <summary>
    /// S->C <c>0x162</c> (10 bytes): <c>u16 BE 0x0162</c>, <c>u32 BE count</c>, <c>u32 LE countdownTimerId</c>
    /// (mixed endianness verified at <c>0x141f9a710</c> / <c>0x141f9a790</c>: <c>htonl</c>/<c>ntohl</c> on <c>+0x10</c>,
    /// raw unswapped read/write on <c>+0x14</c>). <paramref name="count"/> <c>== 0</c> or
    /// <paramref name="countdownTimerId"/> <c>== 2</c> hides the countdown widget (<c>0x14071e681</c>).
    /// </summary>
    public static byte[] CountdownTimer(uint count, uint countdownTimerId = MercenaryCountdownTimerId)
    {
        var buf = new byte[10];
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0, 2), CountdownTimerId);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(2, 4), count);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(6, 4), countdownTimerId);
        return buf;
    }

    /// <summary>Hides the HUD countdown timer widget by sending <c>count = 0</c>.</summary>
    public static byte[] HideCountdownTimer(uint countdownTimerId = MercenaryCountdownTimerId) =>
        CountdownTimer(0, countdownTimerId);
}
