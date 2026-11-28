using System.Buffers.Binary;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// Neutral wire codecs for Heist planning, contract signing, blueprint reveals, rogue state, and alert level HUD
/// (verified against 3.29.3c/d <c>Client/Game.exe</c>).
/// <list type="bullet">
/// <item><description>C→S <c>0x0279</c> (writer <c>0x141fa3d30</c> → <c>0x141fab790</c>): <c>u8 count</c>, <c>count × u8</c> rogue rows (<c>0xff</c> = empty).</description></item>
/// <item><description>C→S <c>0x027d</c> (writer <c>0x141fa3ee0</c>, 5 B): <c>u8 wingIndex</c>, <c>u8 roomTarget</c> (<c>0xff</c> wing, <c>0xfe</c> escape, else room index), <c>u8 revealerRow</c>.</description></item>
/// <item><description>C→S <c>0x027e</c> (writer <c>0x140911160</c>, 2 B): confirm blueprint plans (empty payload).</description></item>
/// <item><description>C→S <c>0x027f</c> (writer <c>0x141fa3fc0</c> → <c>0x141fab790</c>): <c>u8 wingIndex</c>, <c>u8 count</c>, <c>count × u8</c> rogue rows (<c>0xff</c> = empty).</description></item>
/// <item><description>S→C <c>0x023b</c> (case <c>0xb2</c>, reader <c>0x141fa3f70</c>, 5 B): echo of a blueprint reveal (<c>u8 wingIndex</c>, <c>u8 roomTarget</c>, <c>u8 revealerRow</c>).</description></item>
/// <item><description>S→C <c>0x027c</c> (case <c>0xd1</c>, reader <c>0x1421b4e20</c>, 150 B = 2 B id + 148 B body): <c>u8 flag</c> (<c>+0x00</c>), <c>9 × 4 × u32 BE</c> rogue job XP (<c>+0x04..+0x93</c>), <c>u8 whakano</c> (<c>+0x01</c>), <c>u8 niles</c> (<c>+0x03</c>), <c>u8 gianna</c> (<c>+0x02</c>).</description></item>
/// <item><description>S→C <c>0x0164</c> (case <c>0x6b</c>, reader <c>0x1421aeb30</c>, 7 B): <c>u32 BE value</c>, <c>u8 kind</c> (kind <c>2</c> = float <c>[0..1]</c> Heist alert bar → <c>0x140dcc7b0</c> → <c>0x1404c38d0</c>).</description></item>
/// </list>
/// </summary>
public static class InstanceHeist
{
    public const ushort LockerMoveId = 0x00fc;
    public const ushort AlertLevelId = 0x0164;
    public const ushort RevealResultId = 0x023b;
    public const ushort SignContractId = 0x0279;
    public const ushort HeistStateId = 0x027c;
    public const ushort RevealBlueprintId = 0x027d;
    public const ushort ConfirmBlueprintId = 0x027e;
    public const ushort SetBlueprintCrewId = 0x027f;

    public const int LockerMoveLength = 8;
    public const int AlertLevelLength = 7;
    public const int RevealResultLength = 5;
    public const int RevealBlueprintLength = 5;
    public const int ConfirmBlueprintLength = 2;
    public const int HeistStateLength = 150;

    /// <summary>Number of playable rogues (<c>HeistNPCs</c> rows <c>0..8</c>) and job XP slots per rogue in S→C <c>0x027c</c>.</summary>
    public const int RogueCount = 9;
    public const int JobSlotsPerRogue = 4;

    /// <summary>Sentinel value in C→S <c>0x0279</c> / <c>0x027f</c> meaning an unassigned crew slot.</summary>
    public const byte EmptyRogueSlot = 0xff;

    /// <summary>Sentinel <c>RoomTarget</c> values in C→S <c>0x027d</c> / S→C <c>0x023b</c> (verified at <c>0x1404a1a17</c>).</summary>
    public const byte RevealTargetWing = 0xff;
    public const byte RevealTargetEscapeRoute = 0xfe;

    /// <summary><c>S→C 0x0164</c> kind byte routing to the Heist alert bar (<c>instance +0x4ff8</c> → PC HUD <c>+0xce8</c>).</summary>
    public const byte AlertBarKind = 2;

    public readonly record struct RevealBlueprintRequest(byte WingIndex, byte RoomTarget, byte RevealerRow);
    public readonly record struct SetBlueprintCrewRequest(byte WingIndex, byte[] Crew);

    /// <summary>Parses C→S <c>0x00fc</c> (Ctrl + click into or out of the Heist Locker, 8 bytes):
    /// <c>u32 BE itemId</c> (+2), <c>u8 fromExpanded</c> (+6), <c>u8 intoLocker</c> (+7).</summary>
    public static InstanceItems.LeagueWindowMove ReadLockerMove(ReadOnlySpan<byte> payload) =>
        new(BinaryPrimitives.ReadUInt32BigEndian(payload[2..]), payload[6] != 0, payload[7] != 0);

    /// <summary>Parses C→S <c>0x0279</c> (sign contract): <c>u16 BE 0x0279</c>, <c>u8 count</c>, <c>count × u8</c>.</summary>
    public static bool TryReadSignContract(ReadOnlySpan<byte> packet, out byte[] rogues)
    {
        rogues = [];
        if (packet.Length < 3 || BinaryPrimitives.ReadUInt16BigEndian(packet[..2]) != SignContractId) return false;
        int count = packet[2];
        if (packet.Length < 3 + count) return false;
        rogues = packet.Slice(3, count).ToArray();
        return true;
    }

    /// <summary>Parses C→S <c>0x027d</c> (reveal blueprint detail, 5 bytes): <c>u8 wing</c>, <c>u8 target</c>, <c>u8 revealer</c>.</summary>
    public static bool TryReadRevealBlueprint(ReadOnlySpan<byte> packet, out RevealBlueprintRequest request)
    {
        request = default;
        if (packet.Length < RevealBlueprintLength || BinaryPrimitives.ReadUInt16BigEndian(packet[..2]) != RevealBlueprintId)
            return false;
        request = new(packet[2], packet[3], packet[4]);
        return true;
    }

    /// <summary>Parses C→S <c>0x027f</c> (assign blueprint wing crew): <c>u8 wing</c>, <c>u8 count</c>, <c>count × u8</c>.</summary>
    public static bool TryReadSetBlueprintCrew(ReadOnlySpan<byte> packet, out SetBlueprintCrewRequest request)
    {
        request = default;
        if (packet.Length < 4 || BinaryPrimitives.ReadUInt16BigEndian(packet[..2]) != SetBlueprintCrewId) return false;
        byte wing = packet[2];
        int count = packet[3];
        if (packet.Length < 4 + count) return false;
        request = new(wing, packet.Slice(4, count).ToArray());
        return true;
    }

    /// <summary>
    /// S→C <c>0x023b</c> (5 bytes): confirms a blueprint reveal (<c>wingIndex</c>, <c>roomTarget</c>, <c>revealerRow</c>).
    /// </summary>
    public static byte[] RevealResult(byte wingIndex, byte roomTarget, byte revealerRow)
    {
        var buf = new byte[RevealResultLength];
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0, 2), RevealResultId);
        buf[2] = wingIndex;
        buf[3] = roomTarget;
        buf[4] = revealerRow;
        return buf;
    }

    /// <summary>
    /// S→C <c>0x027c</c> (150 bytes = 2 B opcode + 148 B payload): rogue job XP and blueprint reveals left.
    /// Trailing wire order verified at <c>0x1421b4f03..0x1421b4f65</c>: <c>whakano</c> (<c>+0x01</c>),
    /// <c>niles</c> (<c>+0x03</c>), <c>gianna</c> (<c>+0x02</c>).
    /// </summary>
    public static byte[] HeistState(bool hasRunnableContract, ReadOnlySpan<uint> rogueJobXp,
        byte whakanoReveals, byte giannaReveals, byte nilesReveals)
    {
        const int expectedXp = RogueCount * JobSlotsPerRogue;
        if (rogueJobXp.Length != expectedXp)
            throw new ArgumentException($"Expected {expectedXp} job XP entries ({RogueCount} × {JobSlotsPerRogue}).", nameof(rogueJobXp));
        var buf = new byte[HeistStateLength];
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0, 2), HeistStateId);
        buf[2] = hasRunnableContract ? (byte)1 : (byte)0;
        for (int i = 0; i < expectedXp; i++)
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(3 + i * 4, 4), rogueJobXp[i]);
        buf[147] = whakanoReveals;
        buf[148] = nilesReveals;
        buf[149] = giannaReveals;
        return buf;
    }

    /// <summary>
    /// S→C <c>0x0164</c> (7 bytes): HUD alert/gauge packet (<c>u32 BE value</c>, <c>u8 kind</c>).
    /// </summary>
    public static byte[] HudGauge(uint rawValue, byte kind)
    {
        var buf = new byte[AlertLevelLength];
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0, 2), AlertLevelId);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(2, 4), rawValue);
        buf[6] = kind;
        return buf;
    }

    /// <summary>
    /// S→C <c>0x0164</c> kind <c>2</c> (7 bytes): Heist alert bar fill ratio clamped to <c>[0, 1]</c>.
    /// </summary>
    public static byte[] AlertLevel(float ratio)
    {
        float clamped = float.IsFinite(ratio) ? Math.Clamp(ratio, 0f, 1f) : 0f;
        return HudGauge(BitConverter.SingleToUInt32Bits(clamped), AlertBarKind);
    }
}
