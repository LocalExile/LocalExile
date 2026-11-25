using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Modules.Mercenary;

/// <summary>
/// The messages of a mercenary's talk and duel, as the two official duels send them (one in an Act 10 area, one in a map);
/// every value below is in both once the entity ids are aside. The ruleset data and its action use
/// <see cref="InstanceRulesets"/>, the panel <see cref="InstanceWaypoints.OpenUi"/>.
/// </summary>
public static class MercenaryWire
{
    /// <summary>The 0x7b kind the press opens: the mercenary panel (a waypoint's is 0, Ultimatum's 0x3a).</summary>
    public const byte TalkPanelKind = 0x61;

    /// <summary>S→C 0x285 (world group, 13 bytes: the entity header, a u16 and a u8). Both official duels send two of them and
    /// nothing names what they mean, so they go out as sent.</summary>
    public const ushort MarkId = 0x285;

    /// <summary>The first mark follows the press, the second the countdown. The official stream carries the player / the mercenary
    /// id and 0x9002 / 0x1000 in the 10-byte object header; the server writes it as 0 (the world output owns ordering).</summary>
    public const ushort TalkValue = 0x61ad, CountdownValue = 0x2d50;

    public static byte[] Mark(uint entityId, uint chain, ushort subframe, ushort value) =>
        new LoginWriter().U16(MarkId).U32(entityId).U32(chain).U16(subframe).U16(value).U8(0).ToArray();

    /// <summary>The duel choice, a 0x2ca action of MercenaryEncounterRules: eight payload bytes, the same in both official duels.
    /// The panel's other choices are in no capture.</summary>
    public static readonly byte[] DuelChoice = [0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00];

    /// <summary>MercenaryAreaRules' two bodies, sent in the same order at nearly every area entry of the official server:
    /// <c>00 00 00</c> then <c>06 00</c>. What they mean is not decoded. Its other sub-messages (2 and 3 among them) are in no
    /// official stream, and one of them ended a client.</summary>
    public static readonly byte[] AreaRulesBody1 = [0x00, 0x00, 0x00], AreaRulesBody2 = [0x06, 0x00];

    /// <summary>MercenaryEncounterRules' first body, which lists what the mercenary wears: <c>0</c>, the house's attribute mask
    /// (<see cref="MercenaryGear.Attributes"/>), the item count, then per item its ItemVisualIdentity HASH16 (u16) and five zero
    /// bytes.</summary>
    public static byte[] EncounterRulesBody(byte attributes, IReadOnlyList<ushort> items)
    {
        byte[] body = new byte[3 + items.Count * 7];
        body[1] = attributes;
        body[2] = (byte)items.Count;
        for (int i = 0; i < items.Count; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(3 + i * 7), items[i]);
        return body;
    }

    /// <summary>The teams of a map mercenary's create (70, as in all 22 official ones) and of a hired one (0x8001).</summary>
    public const ushort MercenaryTeam = 70, AlliedTeam = 0x8001;

    /// <summary>The four shared states of a Mercenaries type, in its .ot order: encounter_state, hired_state, leavetalk_enabled, light.</summary>
    public static readonly int[] MetStates = [1, 0, 0, 1], TalkStates = [1, 0, 1, 1], CountdownStates = [7, 0, 1, 1],
        QuietStates = [7, 0, 0, 1], DuelStates = [2, 0, 0, 1], HiredStates = [0, 1, 0, 1];

    /// <summary>The frames, counted from the duel choice, of the second mark, the quiet state and the switch to the duel. The
    /// official duels put them at 19 / 25 / 54 and 17 / 23 / 54; the first two are their means.</summary>
    public static readonly int[] CountdownFrames = [18, 24, 54];

    /// <summary>One frame in seconds (the official frames run at 30 to the second).</summary>
    public const double FrameSeconds = 1.0 / 30;
}
