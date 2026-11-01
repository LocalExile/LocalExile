"""a10q5 "Map to Tsoatha" (act10.md): Lilly's quest; the Pearl Case in The Reliquary holds The Teardrop; handed in,
Lilly's reward (offer 68: belts)."""
QUEST = "a10q5"
DONE = "a10q5-QuestRewardTaken"
REQUIRES = ["a10q1"]

STEPS = [
    {"verb": "talk", "npc": "Act10/Lilly", "option": "Map to Tsoatha", "area": "2_10_town", "expect_flag": "a10q5-QuestReceived", "skip_if": ["a10q5-QuestReceived", "a10q5-HaveItem", "a10q5-DeliveredItem"]},
    {"verb": "goto", "area": "2_10_8", "skip_if": ["a10q5-HaveItem", "a10q5-DeliveredItem"]},
    {"verb": "interact", "target": "QuestChests/Teardrop", "area": "2_10_8", "skip_if": ["a10q5-HaveItem", "a10q5-DeliveredItem"]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act10/Teardrop", "expect_flag": "a10q5-HaveItem", "skip_if": ["a10q5-HaveItem", "a10q5-DeliveredItem"]},
    {"verb": "talk", "npc": "Act10/Lilly", "option": "Map to Tsoatha", "area": "2_10_town", "expect_flag": "a10q5-DeliveredItem", "skip_if": "a10q5-DeliveredItem"},
    {"verb": "reward", "npc": "Act10/Lilly", "option": "Tsoatha Reward", "area": "2_10_town", "pick": ["Belt", "Sash"]},
]
