using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Modules.Mercenary;

/// <summary>
/// Wire constants and helpers for Mercenaries of Trarthus (rulesets <c>0xa1</c> / <c>0xa2</c>, UI open <c>0x7b</c>
/// kinds <c>0x60</c> / <c>0x61</c>, StateMachine presets, and <see cref="InstanceMercenaries"/> record builders).
/// </summary>
public static class MercenaryWire
{
    /// <summary>S→C 0x7b kind 0x60: hired MercenaryLoadout (args: objectId, key 0..3).</summary>
    public const byte LoadoutPanelKind = InstanceWaypoints.MercenaryLoadoutUi;

    /// <summary>S→C 0x7b kind 0x61: EncounterMercenaryLoadout (arg: objectId).</summary>
    public const byte TalkPanelKind = InstanceWaypoints.EncounterMercenaryLoadoutUi;

    /// <summary>Manager-1 inventory IDs (<c>instance + 0xca10</c>) for an encounter mercenary's gear &amp; optional rucksack.</summary>
    public const uint EncounterGearInventoryId = 0x640, EncounterRucksackInventoryId = 0x641;

    /// <summary>QuestFlags Id whose HASH32 is <c>0xbd0b7d78</c> (row 2020): required on the encounter owner for Take/Rematch/Hire.</summary>
    public const string OwnerQuestFlag = "IsMapOwner";

    /// <summary>S→C 0x285 (world group, 13 bytes: entity header, u16, u8).</summary>
    public const ushort MarkId = 0x285;

    /// <summary>Marks following talk open and countdown start.</summary>
    public const ushort TalkValue = 0x61ad, CountdownValue = 0x2d50;

    public static byte[] Mark(uint entityId, uint chain, ushort subframe, ushort value) =>
        new LoginWriter().U16(MarkId).U32(entityId).U32(chain).U16(subframe).U16(value).U8(0).ToArray();

    /// <summary>Rematch with Should Recruit = 1, C→S 0x2ca payload for ruleset 0xa1.</summary>
    public static readonly byte[] DuelChoice = [0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00];

    /// <summary>Builds a wire <see cref="InstanceMercenaries.MercenaryBaseRecord"/> using verified HASH16/HASH32 lookups.</summary>
    public static InstanceMercenaries.MercenaryBaseRecord BaseRecord(
        ushort buildHash16, byte level, uint firstNameHash32, uint secondNameHash32, ushort flavourHash16,
        IReadOnlyList<MercenaryTables.RolledSkill> skills) =>
        new(buildHash16, level, firstNameHash32, secondNameHash32, flavourHash16,
            [.. skills.Select(s => new InstanceMercenaries.MercenarySkillWire(s.SkillHash16, s.SupportHashes16))]);

    /// <summary>The teams of a map mercenary's create (70) and of a hired/allied one (0, player's friendly team for green overhead bar).</summary>
    public const ushort MercenaryTeam = 70, AlliedTeam = 0;

    /// <summary>The four shared states of a Mercenaries type, in .ot order: encounter_state, hired_state, leavetalk_enabled, light.</summary>
    public static readonly int[] MetStates = [1, 0, 0, 1], TalkStates = [1, 0, 1, 1], CountdownStates = [7, 0, 1, 1],
        QuietStates = [7, 0, 0, 1], DuelStates = [2, 0, 0, 1], HiredStates = [0, 1, 0, 1],
        // encounter_state 8, "Party Member idle" in Mercenary.ot: the companions that wait beside the main Mercenary.
        PartyIdleStates = [8, 0, 0, 1];

    /// <summary>The frames, counted from the duel choice, of the second mark, the quiet state and the switch to the duel.</summary>
    public static readonly int[] CountdownFrames = [18, 24, 54];

    /// <summary>One frame in seconds (30 fps).</summary>
    public const double FrameSeconds = 1.0 / 30;
}
