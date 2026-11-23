"""a3q1 "Lost in Love": Clarissa kneels among the Blackguards in The City of Sarn; when their Guard Captain dies she can
stand (Transitionable 2) and asks for Tolman. In The Crematorium Piety escapes from Tolman's chair, which holds his
bracelet. Clarissa takes it; Maramoa rewards (offer 18, gems). (a3q2 then gives the Sewer Keys.)"""
QUEST = "a3q1"
DONE = "a3q1-QuestRewardTaken"

RESCUED = "a3q1-RescuedGirl"
HAVE_ITEM = "a3q2-HaveTolmanItem"
DELIVERED = "a3q2-DeliveredItem"

STEPS = [
    {"verb": "talk", "npc": "Maramoa", "option": "Clarissa", "area": "1_3_town", "expect_flag": "a3q1-QuestReceived",
     "skip_if": ["a3q1-QuestReceived", RESCUED, "a3q2-SeenTolmanItem"], "optional": True},
    {"verb": "goto", "area": "1_3_1", "skip_if": RESCUED},
    {"verb": "tp", "target": "ClarissaSlums", "skip_if": ["a3q1-SeenGirl", RESCUED], "optional": True},
    {"verb": "kill", "target": "GuardLieutenantClarissa", "appear_timeout": 15, "skip_if": RESCUED},
    # Freed (Transitionable 2) when the captain dies: ClarissaSlums' "Tolman" (NPCTalk 410).
    {"verb": "talk", "npc": "ClarissaSlums", "option": "Tolman", "area": "1_3_1", "expect_flag": RESCUED, "skip_if": RESCUED},
    {"verb": "goto", "area": "1_3_2", "skip_if": [HAVE_ITEM, DELIVERED]},
    {"verb": "goto", "area": "1_3_3_1", "skip_if": [HAVE_ITEM, DELIVERED]},
    {"verb": "tp", "target": "TolmanChair", "skip_if": [HAVE_ITEM, DELIVERED], "optional": True},
    {"verb": "kill", "target": "PietyEscape", "skip_if": [HAVE_ITEM, DELIVERED], "optional": True},
    {"verb": "kill", "target": "GuardTolman", "appear_timeout": 1, "optional": True, "skip_if": [HAVE_ITEM, DELIVERED]},
    {"verb": "interact", "target": "TolmanChair", "skip_if": [HAVE_ITEM, DELIVERED]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/TolmanBracelet", "expect_flag": "a3q2-HaveTolmanItem",
     "skip_if": [HAVE_ITEM, DELIVERED]},
    {"verb": "talk", "npc": "ClarissaTown", "option": "Tolman", "area": "1_3_town", "expect_flag": DELIVERED, "skip_if": DELIVERED},
    {"verb": "talk", "npc": "Maramoa", "option": "Clarissa", "area": "1_3_town", "expect_flag": "a3q1-Congratulated",
     "skip_if": ["a3q1-Congratulated", DONE]},
    {"verb": "reward", "npc": "Maramoa", "option": "Clarissa Reward", "area": "1_3_town", "skip_if": DONE},
    {"verb": "passives"},
]
