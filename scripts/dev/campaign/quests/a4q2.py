"""a4q2 "Breaking the Seal" (Agents/docs/game/campaign/act4.md): Dialla sends the player to Oyun (a4q1's first step);
Voll in The Dried Lake drops Deshret's Banner; the banner breaks Deshret's Seal in Highgate (a4q2-UsedBanner opens the
Mines). Oyun's reward (offer 30, skill gems)."""
QUEST = "a4q2"
DONE = "a4q2-QuestRewardTaken"

USED = "a4q2-UsedBanner"
HAVE = "a4q2-HaveBanner"

STEPS = [
    {"goto": "1_4_town"},
    {"verb": "talk", "npc": "DiallaTown", "option": "Oyun", "area": "1_4_town", "skip_if": ["a4q1-OyunQuest", USED], "optional": True},
    {"verb": "talk", "npc": "Oyun", "option": "Introduction", "area": "1_4_town", "skip_if": ["OyunIntroduced", USED]},
    {"verb": "talk", "npc": "Oyun", "option": "The Red Banner", "index": 1, "area": "1_4_town", "expect_flag": "a4q2-QuestReceived",
     "skip_if": ["a4q2-QuestReceived", USED]},
    {"verb": "goto", "area": "1_4_2", "skip_if": [HAVE, USED]},
    {"verb": "kill", "target": "VollBoss", "appear_timeout": 15, "expect_flag": "a4q2-KilledVoll", "skip_if": ["a4q2-KilledVoll", HAVE, USED]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act4/RedBanner", "expect_flag": HAVE, "skip_if": [HAVE, USED]},
    {"verb": "interact", "target": "MineEntranceSeal", "area": "1_4_town", "expect_flag": USED, "skip_if": USED},
    {"verb": "talk", "npc": "Oyun", "option": "The Red Banner", "index": 3, "area": "1_4_town", "expect_flag": "a4q2-Congratulated",
     "skip_if": ["a4q2-Congratulated", DONE]},
    {"verb": "reward", "npc": "Oyun", "option": "Red Banner Reward", "area": "1_4_town", "skip_if": DONE},
]
