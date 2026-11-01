"""a10q3 "An End to Hunger" (act10.md): Innocence's "Kitava"; in The Ravaged Square Innocence breaks the horns
(a10q3-OpenedGate: The Canals open); in The Feeding Trough SinTrough's "Kitava" starts the fight; Kitava cannot die:
/kill ends the fight (EndGameAvailable, another -30% to resistances). Sin's portal home is not created: the docks by
waypoint. Lani's "Kitava" (row 1415) and her book (offer 66: 2 passives)."""
QUEST = "a10q3"
DONE = "a10q3-UsedRewardBook"
REQUIRES = ["a10q2"]

STEPS = [
    {"verb": "talk", "npc": "InnocenceTown", "option": "Kitava", "area": "2_10_town", "expect_flag": "a10q3-QuestReceived", "skip_if": ["a10q3-QuestReceived", "a10q3-OpenedGate"]},
    {"verb": "goto", "area": "2_10_2", "skip_if": ["a10q3-OpenedGate", "EndGameAvailable"]},
    {"verb": "tp", "target": "CanalHornBlockage", "area": "2_10_2", "expect_flag": "a10q3-SeenGate", "skip_if": ["a10q3-SeenGate", "a10q3-OpenedGate"], "optional": True},
    {"verb": "talk", "npc": "InnocenceSquare", "option": "Kitava", "area": "2_10_2", "expect_flag": "a10q3-OpenedGate", "skip_if": "a10q3-OpenedGate"},
    {"verb": "goto", "area": "2_10_5", "via": "transition", "skip_if": ["Visited-10_5", "EndGameAvailable"]},
    {"verb": "goto", "area": "2_10_6", "skip_if": "EndGameAvailable"},
    {"verb": "talk", "npc": "SinTrough", "option": "Kitava", "area": "2_10_6", "skip_if": "EndGameAvailable", "optional": True},
    {"verb": "kill", "target": "KitavaFinal", "area": "2_10_6", "appear_timeout": 15, "expect_flag": "EndGameAvailable", "skip_if": "EndGameAvailable"},
    {"verb": "goto", "area": "2_10_town", "via": "transition", "skip_if": ["LaniKitavaReward", "a10q3-HaveRewardBook"]},
    {"verb": "talk", "npc": "Act10/Lani", "option": "Kitava", "row": 1415, "area": "2_10_town", "expect_flag": "LaniKitavaReward", "skip_if": ["LaniKitavaReward", "a10q3-HaveRewardBook"]},
    {"verb": "reward", "npc": "Act10/Lani", "option": "Kitava Reward", "area": "2_10_town", "skip_if": "a10q3-HaveRewardBook"},
    {"verb": "use_item", "item": "Metadata/Items/QuestItems/Act10/Book-a10q3", "expect_flag": "a10q3-UsedRewardBook"},
    {"verb": "passives"},
]
