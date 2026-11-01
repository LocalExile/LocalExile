"""a10q2 "Death and Rebirth" (act10.md): Bannon's "The Staff of Avarius"; Avarius, Reassembled dies in The
Desecrated Chambers and drops the staff; Bannon takes it and becomes Innocence (1456: a10q2-BannonTransformed);
Lani's reward (offer 65: armours, an Uncarved Gemstone)."""
QUEST = "a10q2"
DONE = "a10q2-QuestRewardTaken"
REQUIRES = ["a10q1"]

STEPS = [
    {"verb": "talk", "npc": "BannonTown", "option": "Welcome Back", "area": "2_10_town", "skip_if": ["BannonIntroA10", "a10q2-QuestReceived"], "optional": True},
    {"verb": "talk", "npc": "BannonTown", "option": "The Staff of Avarius", "area": "2_10_town", "expect_flag": "a10q2-QuestReceived", "skip_if": ["a10q2-QuestReceived", "a10q2-HaveItem", "a10q2-DeliveredItem"]},
    {"verb": "goto", "area": "2_10_4", "skip_if": ["a10q2-HaveItem", "a10q2-DeliveredItem"]},
    {"verb": "kill", "target": "AvariusCasticusWickerman", "area": "2_10_4", "appear_timeout": 15, "skip_if": ["a10q2-HaveItem", "a10q2-DeliveredItem"]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act10/AvariusStaff", "expect_flag": "a10q2-HaveItem", "skip_if": ["a10q2-HaveItem", "a10q2-DeliveredItem"]},
    {"verb": "talk", "npc": "BannonTown", "option": "The Staff of Avarius", "area": "2_10_town", "expect_flag": "a10q2-DeliveredItem", "skip_if": "a10q2-DeliveredItem"},
    {"verb": "talk", "npc": "BannonTown", "option": "The Staff of Avarius", "area": "2_10_town", "expect_flag": "a10q2-BannonTransformed", "skip_if": "a10q2-BannonTransformed"},
    {"verb": "talk", "npc": "Act10/Lani", "option": "Innocence", "area": "2_10_town", "expect_flag": "LaniOnInnocence", "skip_if": "LaniOnInnocence"},
    {"verb": "reward", "npc": "Act10/Lani", "option": "Innocence Reward", "area": "2_10_town", "pick": ["Uncarved Gemstone"]},
]
