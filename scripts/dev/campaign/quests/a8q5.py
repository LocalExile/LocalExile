"""a8q5 "The Wings of Vastiri" (act8.md): Hargan's quest; Hector Titucius dies in The Bath House and his tomb opens
(not targetable before), the Wings handed in, Hargan's reward (offer 56: jewels, rings)."""
QUEST = "a8q5"
DONE = "a8q5-QuestRewardTaken"
REQUIRES = ["a8q1"]

STEPS = [
    {"verb": "talk", "npc": "Act8/Hargan", "option": "The Wings of Vastiri", "area": "2_8_town", "expect_flag": "a8q5-QuestReceived", "skip_if": ["a8q5-QuestReceived", "a8q5-HaveItem", "a8q5-DeliveredItem"]},
    {"verb": "goto", "area": "2_8_5", "skip_if": ["a8q5-HaveItem", "a8q5-DeliveredItem"]},
    {"verb": "kill", "target": "Exiles/Titucius", "area": "2_8_5", "appear_timeout": 15, "expect_flag": "a8q5-KilledBoss", "skip_if": ["a8q5-KilledBoss", "a8q5-HaveItem", "a8q5-DeliveredItem"]},
    {"verb": "interact", "target": "TituciusChest", "area": "2_8_5", "skip_if": ["a8q5-HaveItem", "a8q5-DeliveredItem"]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act8/WingsOfVastiri", "expect_flag": "a8q5-HaveItem", "skip_if": ["a8q5-HaveItem", "a8q5-DeliveredItem"]},
    {"verb": "talk", "npc": "Act8/Hargan", "option": "The Wings of Vastiri", "area": "2_8_town", "expect_flag": "a8q5-DeliveredItem", "skip_if": "a8q5-DeliveredItem"},
    {"verb": "reward", "npc": "Act8/Hargan", "option": "Wings of Vastiri Reward", "area": "2_8_town", "pick": ["Cobalt Jewel", "Crimson Jewel", "Viridian Jewel", "Jewel"]},
]
