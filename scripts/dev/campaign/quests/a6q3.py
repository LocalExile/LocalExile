"""a6q3 "The Father of War" (act6.md), with a6q1's start (Tarkleigh's "Nessa", the first Nessa in The Mud Flats): the
Dishonoured Queen drops the Eye of Conquest, the totem door takes it (The Karui Fortress opens); Tukohama's death opens
the way to The Ridge and Sin appears (993: the Pantheon panel, CanChangePantheon): the runner picks the gods there.
Tarkleigh's Book of Skill (offer 41; QuestStaticRewards 14: +1 passive)."""
QUEST = "a6q3"
DONE = "a6q3-UsedRewardBook"
REQUIRES = ["a5q6"]

DELIVERED = "a6q3-DeliveredItem"
KILLED = "a6q3-KilledBoss"

STEPS = [
    {"verb": "talk", "npc": "Act6/Tarkleigh", "option": "Introduction", "area": "2_6_town", "skip_if": ["TarkleighIntroduction2", "a6q1-QuestReceived"],
     "optional": True},
    {"verb": "talk", "npc": "Act6/Tarkleigh", "option": "Nessa", "index": 1, "area": "2_6_town", "expect_flag": "a6q1-QuestReceived",
     "skip_if": ["a6q1-QuestReceived", "a6q1-SeenNessa1"], "optional": True},
    {"verb": "goto", "area": "2_6_4", "skip_if": DELIVERED},
    {"verb": "tp", "target": "NessaMudFlats", "expect_flag": "a6q1-SeenNessa1", "skip_if": "a6q1-SeenNessa1", "optional": True},
    {"verb": "tp", "target": "MudFlatsKaruiDoor", "expect_flag": "a6q3-SeenEntrance", "skip_if": ["a6q3-SeenEntrance", DELIVERED]},
    {"verb": "talk", "npc": "Act6/Tarkleigh", "option": "The Karui Fortress", "area": "2_6_town", "expect_flag": "a6q3-QuestReceived",
     "skip_if": ["a6q3-QuestReceived", DELIVERED], "optional": True},
    {"verb": "goto", "area": "2_6_4", "skip_if": ["a6q3-HaveItem", DELIVERED]},
    {"verb": "kill", "target": "KaruiElder1", "area": "2_6_4", "appear_timeout": 15, "skip_if": ["a6q3-HaveItem", DELIVERED]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act6/KaruiEye", "expect_flag": "a6q3-HaveItem", "skip_if": ["a6q3-HaveItem", DELIVERED]},
    {"verb": "tp", "target": "MudFlatsKaruiDoor", "optional": True, "skip_if": DELIVERED},
    {"verb": "kill", "target": "near 30", "timeout": 20, "optional": True, "skip_if": DELIVERED},
    {"verb": "interact", "target": "MudFlatsKaruiDoor", "area": "2_6_4", "expect_flag": DELIVERED, "skip_if": DELIVERED},
    {"verb": "goto", "area": "2_6_5", "skip_if": KILLED},
    {"verb": "kill", "target": "Tukohama/Tukohama", "appear_timeout": 15, "expect_flag": KILLED, "skip_if": KILLED},
    {"verb": "talk", "npc": "SinTukohama", "option": "Tukohama", "area": "2_6_5", "expect_flag": "SinTukohamaReward",
     "skip_if": "SinTukohamaReward", "optional": True},
    {"verb": "pantheon", "optional": True},
    {"verb": "talk", "npc": "Act6/Tarkleigh", "option": "Tukohama", "index": 9, "area": "2_6_town", "expect_flag": "a6q3-Congratulated",
     "skip_if": ["a6q3-Congratulated", "a6q3-HaveRewardBook", DONE]},
    {"verb": "pantheon", "optional": True},
    {"verb": "reward", "npc": "Act6/Tarkleigh", "option": "Tukohama Reward", "area": "2_6_town", "skip_if": ["a6q3-HaveRewardBook", DONE]},
    {"verb": "use_item", "item": "Metadata/Items/QuestItems/Act6/Book-a6q3", "expect_flag": DONE, "skip_if": DONE},
    {"verb": "passives"},
]
