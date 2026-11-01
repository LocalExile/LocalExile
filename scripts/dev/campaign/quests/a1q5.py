"""a1q5 "Mercy Mission": Nessa gives the quest, Bestel his clue, Hailrake on The Tidal Island drops the Medicine
Chest, Nessa takes it and gives her two rewards."""
QUEST = "a1q5"
DONE = "a1q5-QuestRewardTaken"
REQUIRES = ["a1q1"]

HANDED_IN = "a1q5-NessaCongratulated"

STEPS = [
    {"verb": "talk", "npc": "Nessa", "option": "Lioneye's Watch", "area": "1_1_town", "expect_flag": "a1q5-ReceivedQuest",
     "skip_if": ["a1q5-ReceivedQuest", HANDED_IN]},
    {"verb": "talk", "npc": "Bestel", "option": "The Medicine Chest", "area": "1_1_town", "expect_flag": "a1q5-BestelClue",
     "skip_if": ["a1q5-BestelClue", "a1q5-HaveMedicineChest", HANDED_IN]},
    {"verb": "goto", "area": "1_1_2", "skip_if": ["a1q5-HaveMedicineChest", HANDED_IN]},
    {"verb": "goto", "area": "1_1_2a", "skip_if": ["a1q5-HaveMedicineChest", HANDED_IN]},
    {"verb": "kill", "target": "Hailrake", "appear_timeout": 15, "skip_if": ["a1q5-HaveMedicineChest", HANDED_IN]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/MedicineSet1", "expect_flag": "a1q5-HaveMedicineChest",
     "skip_if": ["a1q5-HaveMedicineChest", HANDED_IN]},
    {"verb": "talk", "npc": "Nessa", "option": "The Medicine Chest", "area": "1_1_town", "expect_flag": HANDED_IN,
     "skip_if": HANDED_IN},
    {"verb": "reward", "npc": "Nessa", "option": "Medicine Chest Reward", "pick": ["Quicksilver", "Life Flask"],
     "skip_if": "a1q5-QuestRewardTaken"},
    {"verb": "reward", "npc": "Nessa", "option": "Medicine Chest Reward 2", "skip_if": "a1q5-QuestReward2Taken",
     "optional": True},
    {"verb": "passives"},
]
