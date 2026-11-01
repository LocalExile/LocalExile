/// <summary>
/// a1q5 "Mercy Mission" (Agents/docs/protocol/12-quests.md §7). Nessa asks for a medicine chest (her NPCTalk "Lioneye's
/// Watch" sets <see cref="ReceivedQuest"/>), Bestel says where his ship ran aground (<see cref="BestelClue"/>), Hailrake
/// guards the wreck on The Tidal Island and drops the Medicine Chest when he dies; Nessa takes it ("The Medicine Chest":
/// <see cref="NessaCongratulated"/>) and offers the rewards (QuestRewardOffers 1 and 2).
/// The dialogue flags are NPCTalk's own, the quest log follows the flags on the client (QuestStates), the chest's flags and
/// hand-in follow its .it Quest component (QuestItems) and Hailrake's drop is a rule of the engine's table (Modules/Core/World/QuestObjects/Acts/Act1.cs).
/// This class keeps the quest's names and the boat rooms' placement rule.
/// </summary>
static class MercyMission
{
    public const string Area = "1_1_2a";
    public const string ReceivedQuest = "a1q5-ReceivedQuest", BestelClue = "a1q5-BestelClue", HaveMedicineChest = "a1q5-HaveMedicineChest",
        NessaCongratulated = "a1q5-NessaCongratulated", QuestRewardTaken = "a1q5-QuestRewardTaken";

    /// <summary>MonsterVarieties 2887, WorldAreas 1_1_2a's Bosses: placed at the medicine_boat room's "mapboss".</summary>
    public const string Hailrake = "Metadata/Monsters/Cannibal/CannibalMaleBossHailrake";

    /// <summary>BaseItemTypes 1534, "Medicine Chest" (2×3, item class 31). Its .it Quest component: grant_flag
    /// a1q5-HaveMedicineChest (held while carried), remove_flag a1q5-NessaCongratulated (gone once Nessa has it).</summary>
    public const string MedicineChest = "Metadata/Items/QuestItems/MedicineSet1";

    /// <summary>QuestRewardOffers 1 (Nessa's "Medicine Chest Reward"): a normal Medium Life, Medium Mana or Quicksilver Flask
    /// at item level 4; QuestRewardOffers 2 ("Medicine Chest Reward 2", a1q5-QuestReward2Taken): a level 1 support gem of
    /// the character's class. Both are listed by the client once Nessa has the chest.</summary>
    public const int RewardOffer = 1, SupportRewardOffer = 2;

    /// <summary>The rooms around Hailrake's "mapboss" (Area2a, tag medicineboat). They have two doodads each, too few for
    /// the doodad fit, so the room points take their captured placement (AreaPopulation.FitRooms); no other room does.</summary>
    public static readonly IReadOnlySet<string> BoatRooms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Metadata/Terrain/Act1/Area2a/Rooms/medicine_boat_1.arm", "Metadata/Terrain/Act1/Area2a/Rooms/medicine_boat_2.arm",
    };
}
