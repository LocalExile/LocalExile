"""a7q7 "Lighting the Way" (act7.md): Yeena at the webbed temple in The Vaal City (seen: a7q4-SeenBlockage) asks for
seven Fireflies (1043); their chests have no room or tile, the engine scatters them over The Dread Thicket; Yeena
takes all seven (1042) and burns the webs: The Temple of Decay opens."""
QUEST = "a7q7"
DONE = "a7q7-DeliveredItems"
REQUIRES = ["a7q1"]

STEPS = [
    {"verb": "goto", "area": "2_7_11", "skip_if": ["a7q7-QuestReceived", "a7q7-HaveItem1"]},
    {"verb": "tp", "target": "ArakaaliTempleEntranceBlock", "area": "2_7_11", "expect_flag": "a7q4-SeenBlockage", "skip_if": "a7q4-SeenBlockage", "optional": True},
    {"verb": "talk", "npc": "YeenaVaalCity", "option": "Fireflies", "row": 1043, "area": "2_7_11", "expect_flag": "a7q7-QuestReceived", "skip_if": ["a7q7-QuestReceived", "a7q7-HaveItem1"], "optional": True},
    {"verb": "goto", "area": "2_7_9"},
    {"verb": "interact", "target": "FireflyChest1", "area": "2_7_9", "skip_if": ["a7q7-HaveItem1", "a7q7-DeliveredItems"]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act7/Firefly1", "expect_flag": "a7q7-HaveItem1", "skip_if": ["a7q7-HaveItem1", "a7q7-DeliveredItems"]},
    {"verb": "interact", "target": "FireflyChest2", "area": "2_7_9", "skip_if": ["a7q7-HaveItem2", "a7q7-DeliveredItems"]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act7/Firefly2", "expect_flag": "a7q7-HaveItem2", "skip_if": ["a7q7-HaveItem2", "a7q7-DeliveredItems"]},
    {"verb": "interact", "target": "FireflyChest3", "area": "2_7_9", "skip_if": ["a7q7-HaveItem3", "a7q7-DeliveredItems"]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act7/Firefly3", "expect_flag": "a7q7-HaveItem3", "skip_if": ["a7q7-HaveItem3", "a7q7-DeliveredItems"]},
    {"verb": "interact", "target": "FireflyChest4", "area": "2_7_9", "skip_if": ["a7q7-HaveItem4", "a7q7-DeliveredItems"]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act7/Firefly4", "expect_flag": "a7q7-HaveItem4", "skip_if": ["a7q7-HaveItem4", "a7q7-DeliveredItems"]},
    {"verb": "interact", "target": "FireflyChest5", "area": "2_7_9", "skip_if": ["a7q7-HaveItem5", "a7q7-DeliveredItems"]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act7/Firefly5", "expect_flag": "a7q7-HaveItem5", "skip_if": ["a7q7-HaveItem5", "a7q7-DeliveredItems"]},
    {"verb": "interact", "target": "FireflyChest6", "area": "2_7_9", "skip_if": ["a7q7-HaveItem6", "a7q7-DeliveredItems"]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act7/Firefly6", "expect_flag": "a7q7-HaveItem6", "skip_if": ["a7q7-HaveItem6", "a7q7-DeliveredItems"]},
    {"verb": "interact", "target": "FireflyChest7", "area": "2_7_9", "skip_if": ["a7q7-HaveItem7", "a7q7-DeliveredItems"]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act7/Firefly7", "expect_flag": "a7q7-HaveItem7", "skip_if": ["a7q7-HaveItem7", "a7q7-DeliveredItems"]},
    {"verb": "goto", "area": "2_7_11"},
    {"verb": "talk", "npc": "YeenaVaalCity", "option": "Fireflies", "row": 1042, "area": "2_7_11", "expect_flag": "a7q7-DeliveredItems"},
]
