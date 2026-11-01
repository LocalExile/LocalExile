"""a2q6 "Intruders in Black": in The Chamber of Sins Level 2 Helena hangs below the ledge while Fidelitas guards her.
His death lets her climb up; her "Rescued" opens the Strange Device, which holds the Baleful Gem. In town Helena, then
Greust's reward (offer 13, skill gems)."""
QUEST = "a2q6"
DONE = "a2q6-QuestRewardTaken"

KILLED = "a2q6-KilledDemon"
IN_TOWN = "a2q6-HelenaInTown"   # the Baleful Gem's extra flag: set when it is picked up

STEPS = [
    # Greust's start row needs a2b-StartNewQuests, which no data row sets this early: optional.
    {"verb": "talk", "npc": "Greust", "option": "Blackguards", "area": "1_2_town", "skip_if": ["a2q6-ReceivedQuest", IN_TOWN],
     "optional": True},
    {"verb": "goto", "area": "1_2_6_1", "skip_if": IN_TOWN},
    {"verb": "goto", "area": "1_2_6_2", "skip_if": IN_TOWN},
    {"verb": "tp", "target": "HelenaSins", "skip_if": ["a2q6-SeenHelena", IN_TOWN], "optional": True},
    {"verb": "kill", "target": "BossFourLeggedDemon", "appear_timeout": 15, "expect_flag": KILLED, "skip_if": [KILLED, IN_TOWN]},
    # Helena climbs up (Transitionable 2) and becomes targetable.
    {"verb": "talk", "npc": "HelenaSins", "option": "Rescued", "area": "1_2_6_2", "expect_flag": "a2q6-RescuedHelena",
     "skip_if": ["a2q6-RescuedHelena", IN_TOWN]},
    {"verb": "interact", "target": "PoisonCart", "area": "1_2_6_2", "skip_if": ["a2q6-HaveSkillGem", IN_TOWN]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/PoisonSkillGem", "expect_flag": "a2q6-HaveSkillGem",
     "skip_if": ["a2q6-HaveSkillGem", IN_TOWN]},
    {"verb": "talk", "npc": "HelenaTown", "option": "Rescued", "area": "1_2_town", "expect_flag": "HelenaIntroduced",
     "skip_if": ["HelenaIntroduced", DONE]},
    {"verb": "talk", "npc": "Greust", "option": "Helena", "area": "1_2_town", "expect_flag": "a2q6-Congratulated",
     "skip_if": ["a2q6-Congratulated", DONE]},
    {"verb": "reward", "npc": "Greust", "option": "Blackguard Reward", "area": "1_2_town", "skip_if": DONE},
    {"verb": "passives"},
]
