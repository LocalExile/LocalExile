"""a10q1 "Safe Passage" (act10.md): Lani's "Bannon"; coming near Bannon on The Cathedral Rooftop brings Plaguewing,
whose death rescues him (a10q1-RescuedBannon, he stands); his thanks, his welcome in town, Lani's reward (offer 64:
flasks)."""
QUEST = "a10q1"
DONE = "a10q1-QuestRewardTaken"
REQUIRES = ["a9q1"]

STEPS = [
    {"verb": "goto", "area": "2_10_town"},
    {"verb": "talk", "npc": "Act10/Lani", "option": "Bannon", "area": "2_10_town", "expect_flag": "a10q1-QuestReceived", "skip_if": ["a10q1-QuestReceived", "a10q1-RescuedBannon"]},
    {"verb": "goto", "area": "2_10_1", "skip_if": "a10q1-RescuedBannon"},
    {"verb": "tp", "target": "BannonRooftop", "area": "2_10_1", "expect_flag": "a10q1-SeenBannon", "skip_if": ["a10q1-SeenBannon", "a10q1-RescuedBannon"]},
    {"verb": "kill", "target": "KitavaDemonPlaguewing", "area": "2_10_1", "appear_timeout": 15, "expect_flag": "a10q1-RescuedBannon", "skip_if": "a10q1-RescuedBannon"},
    {"verb": "talk", "npc": "BannonRooftop", "option": "Thank You", "area": "2_10_1", "skip_if": "a10q1-TalkedToBannon", "optional": True},
    {"verb": "talk", "npc": "BannonTown", "option": "Welcome Back", "area": "2_10_town", "skip_if": "BannonIntroA10", "optional": True},
    {"verb": "talk", "npc": "Act10/Lani", "option": "Bannon", "area": "2_10_town", "expect_flag": "LaniBannonRescued", "skip_if": "LaniBannonRescued"},
    {"verb": "reward", "npc": "Act10/Lani", "option": "Bannon Reward", "area": "2_10_town", "pick": ["Basalt Flask", "Silver Flask", "Sulphur Flask", "Stibnite Flask", "Flask"]},
]
