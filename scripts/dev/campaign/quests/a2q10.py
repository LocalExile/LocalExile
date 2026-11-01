"""a2q10 "The Great White Beast": Yeena's quest; the beast lives in The Den off The Old Fields. Her reward (offer 14) has
flasks and belts; the normal flasks are the ones the server builds."""
QUEST = "a2q10"
DONE = "a2q10-QuestRewardTaken"

KILLED = "a2q10-KilledBeast"

STEPS = [
    {"verb": "talk", "npc": "Yeena", "option": "The Great White Beast", "area": "1_2_town", "expect_flag": "a2q10-QuestReceived",
     "skip_if": ["a2q10-QuestReceived", KILLED]},
    {"verb": "goto", "area": "1_2_2", "skip_if": KILLED},
    {"verb": "goto", "area": "1_2_2a", "skip_if": KILLED},
    {"verb": "kill", "target": "BeastBossWhite", "appear_timeout": 15, "expect_flag": KILLED, "skip_if": KILLED},
    # After the kill Yeena's second "The Great White Beast" (NPCTalk 131) congratulates.
    {"verb": "talk", "npc": "Yeena", "option": "The Great White Beast", "area": "1_2_town", "expect_flag": "a2q10-Congratulated",
     "skip_if": ["a2q10-Congratulated", DONE]},
    {"verb": "reward", "npc": "Yeena", "option": "Great White Beast Reward", "area": "1_2_town",
     "pick": ["Greater Life Flask", "Quicksilver", "Greater Mana Flask", "Bismuth"], "skip_if": DONE},
    {"verb": "passives"},
]
