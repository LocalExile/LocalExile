"""a2q11 "The Way Forward" (a1q9's road, Act 2's part): Captain Arteri guards the blocked pass in The Western Forest;
his death drops the Thaumetic Emblem, which opens the Thaumetic Seal (a1q9-RoadOpened: the spikes lift and the road to
Prisoner's Gate opens). Bestel in Lioneye's Watch gives a Book of Skill: one passive point once used."""
QUEST = "a2q11"
DONE = "a1q9-UsedRewardBook"

OPENED = "a1q9-RoadOpened"
BOOK = "a1q9-HaveRewardBook"

STEPS = [
    # Silk's clue about the pass (optional: the quest log's "Talk to Silk").
    {"verb": "talk", "npc": "Silk", "option": "Blocked Passage", "area": "1_2_town", "skip_if": ["a1q9-SilkClue", OPENED],
     "optional": True},
    {"verb": "goto", "area": "1_2_9", "skip_if": OPENED},
    {"verb": "kill", "target": "LieutenantLetter", "appear_timeout": 15, "skip_if": ["a2q11-HaveKey", OPENED]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/SpikeSealKey", "expect_flag": "a2q11-HaveKey",
     "skip_if": ["a2q11-HaveKey", OPENED]},
    {"verb": "interact", "target": "SpikeBlockageButton", "expect_flag": OPENED, "skip_if": OPENED},
    {"verb": "talk", "npc": "Bestel", "option": "Inspirational Poem", "area": "1_1_town", "expect_flag": "a1q9-Congratulated",
     "skip_if": ["a1q9-Congratulated", BOOK, DONE]},
    {"verb": "reward", "npc": "Bestel", "option": "Road Reward", "area": "1_1_town", "expect_flag": BOOK, "skip_if": [BOOK, DONE]},
    {"verb": "use_item", "item": "Metadata/Items/QuestItems/SkillBooks/Book-a1q9", "expect_flag": DONE, "skip_if": DONE},
    {"verb": "passives"},
]
