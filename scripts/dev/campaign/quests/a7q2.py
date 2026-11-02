"""a7q2 "Essence of the Artist" (act7.md), with a7q3's start: Sin's and Helena's talks, Maligaro's Map from The
Crypt's Container of Sins (Silk appears with it), the map used at the Reverie Device in The Chamber of Sins Level 1
(a7q2-DeliveredItem): its portal (MaligaroPortalTransition) leads into the Sanctum. Black Death drops the venom,
Maligaro's death; Sin (1137), Helena's reward (offer 47, boots)."""
QUEST = "a7q2"
DONE = "a7q2-QuestRewardTaken"
REQUIRES = ["a7q5"]

STEPS = [
    {"verb": "talk", "npc": "Act7/SinTown", "option": "Inquisitor Maligaro", "area": "2_7_town", "expect_flag": "a7q2-QuestReceived", "skip_if": ["a7q2-QuestReceived", "a7q2-KilledBoss"]},
    {"verb": "talk", "npc": "Act7/Helena", "option": "Maligaro", "area": "2_7_town", "skip_if": ["a7q2-MapClue", "a7q2-HaveItem", "a7q2-KilledBoss"], "optional": True},
    {"verb": "goto", "area": "2_7_4", "skip_if": ["a7q2-HaveItem", "a7q2-DeliveredItem", "a7q2-KilledBoss"]},
    {"verb": "interact", "target": "MaligaroMapChest", "area": "2_7_4", "skip_if": ["a7q2-HaveItem", "a7q2-DeliveredItem", "a7q2-KilledBoss"]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act7/MaligaroMap", "expect_flag": "a7q2-HaveItem", "skip_if": ["a7q2-HaveItem", "a7q2-KilledBoss"]},
    {"verb": "goto", "area": "2_7_5_1", "skip_if": ["a7q2-DeliveredItem", "a7q2-KilledBoss"]},
    {"verb": "talk", "npc": "SilkSins", "option": "Black Venom", "area": "2_7_5_1", "expect_flag": "a7q3-QuestReceived", "skip_if": ["a7q3-QuestReceived", "a7q3-HaveItem"], "optional": True},
    {"verb": "interact", "target": "MaligaroOrrery", "area": "2_7_5_1", "expect_flag": "a7q2-DeliveredItem", "skip_if": ["a7q2-DeliveredItem", "a7q2-KilledBoss"]},
    {"verb": "goto", "area": "2_7_5_map", "via": "transition", "skip_if": ["a7q2-KilledBoss"]},
    {"verb": "kill", "target": "BlackDeathMaligaro", "area": "2_7_5_map", "appear_timeout": 15, "skip_if": ["a7q3-HaveItem", "a7q3-DeliveredItem"]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act7/BlackVenom", "expect_flag": "a7q3-HaveItem", "skip_if": ["a7q3-HaveItem", "a7q3-DeliveredItem"]},
    {"verb": "kill", "target": "Maligaro2", "area": "2_7_5_map", "appear_timeout": 15, "expect_flag": "a7q2-KilledBoss", "skip_if": "a7q2-KilledBoss"},
    {"verb": "talk", "npc": "SinMaligaro", "option": "Maligaro", "area": "2_7_5_map", "skip_if": "SinMaligaroReward", "optional": True},
    {"verb": "talk", "npc": "Act7/Helena", "option": "Maligaro", "area": "2_7_town", "expect_flag": "HelenaOnMaligaroDead", "skip_if": "HelenaOnMaligaroDead"},
    {"verb": "reward", "npc": "Act7/Helena", "option": "Maligaro Reward", "area": "2_7_town"},
]
