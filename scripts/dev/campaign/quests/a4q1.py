"""a4q1 "The Eternal Nightmare" (act4.md), its end: Malachai's second stage dies in the Black Core (a4q1-QuestComplete);
in Highgate Tasuni (615) and the withered Dialla (521 opens the Ascent), then her "Rapture Reward" (offer 29) for both
Eyes. The second stage is not placed with the area: it appears where the first falls (Quests On.SecondForm; the
game's first stage cannot die below 70%, a floor the server does not model), so /kill takes the first, then the second."""
QUEST = "a4q1"
DONE = "a4q3q4-QuestRewardTaken"
REQUIRES = ["a4q5"]

COMPLETE = "a4q1-QuestComplete"

STEPS = [
    {"verb": "goto", "area": "1_4_6_3", "skip_if": COMPLETE},
    {"verb": "kill", "target": "Metadata/Monsters/Malachai/MalachaiBoss", "appear_timeout": 15, "skip_if": COMPLETE},
    {"verb": "kill", "target": "MalachaiBoss2", "appear_timeout": 15, "expect_flag": COMPLETE, "skip_if": COMPLETE},
    {"verb": "talk", "npc": "Tasuni", "option": "Malachai", "index": 23, "area": "1_4_town", "expect_flag": "TasuniOnMalachaiDead",
     "skip_if": "TasuniOnMalachaiDead"},
    {"verb": "talk", "npc": "DiallaTownWithered", "option": "Malachai", "index": 4, "area": "1_4_town", "expect_flag": "DiallaOnMalachaiDead",
     "skip_if": "DiallaOnMalachaiDead"},
    {"verb": "talk", "npc": "DiallaTownWithered", "option": "The Rapture Device", "index": 6, "area": "1_4_town",
     "expect_flag": "a4q3q4-Congratulated", "skip_if": ["a4q3q4-Congratulated", DONE]},
    {"verb": "reward", "npc": "DiallaTownWithered", "option": "Rapture Reward", "area": "1_4_town", "skip_if": DONE},
]
