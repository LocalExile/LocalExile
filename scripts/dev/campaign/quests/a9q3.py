"""a9q3 "The Storm Blade" (act9.md): Petarus and Vanja's quest; at the mummy event chest in The Vastiri Desert the
encounter begins and its sand warriors come; their deaths end it and drop the Storm Blade; handed in, their reward
(offer 60: rare weapons and armour)."""
QUEST = "a9q3"
DONE = "a9q3-QuestRewardTaken"
REQUIRES = ["a8q2"]

STEPS = [
    {"verb": "goto", "area": "2_9_town"},
    {"verb": "talk", "npc": "PetarusVanja", "option": "The Storm Blade", "area": "2_9_town", "expect_flag": "a9q3-QuestReceived", "skip_if": ["a9q3-QuestReceived", "a9q3-HaveItem", "a9q3-DeliveredItem"]},
    {"verb": "goto", "area": "2_9_3", "skip_if": ["a9q3-HaveItem", "a9q3-DeliveredItem"]},
    {"verb": "tp", "target": "MummyEventChest", "area": "2_9_3", "expect_flag": "a9q3-BeginEncounter", "skip_if": ["a9q3-BeginEncounter", "a9q3-HaveItem", "a9q3-DeliveredItem"]},
    {"verb": "kill", "target": "MummyEvent", "area": "2_9_3", "appear_timeout": 15, "expect_flag": "a9q3-FinishEncounter", "skip_if": ["a9q3-FinishEncounter", "a9q3-HaveItem", "a9q3-DeliveredItem"]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act9/StormSword", "expect_flag": "a9q3-HaveItem", "skip_if": ["a9q3-HaveItem", "a9q3-DeliveredItem"]},
    {"verb": "talk", "npc": "PetarusVanja", "option": "The Storm Blade", "area": "2_9_town", "expect_flag": "a9q3-DeliveredItem", "skip_if": "a9q3-DeliveredItem"},
    {"verb": "reward", "npc": "PetarusVanja", "option": "Storm Blade Reward", "area": "2_9_town", "pick": ["Wand", "Sceptre", "Dagger"]},
]
