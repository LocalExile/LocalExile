"""a1q2 "The Caged Brute": Tarkleigh's "Brutus" (after The Lower Prison is visited) gives it and Nessa her help
("Prison Reward"); Brutus waits in the Warden's section of The Upper Prison (Piety reads in her room on the way:
SeenPietyPrison); his death opens Prisoner's Gate. Tarkleigh gives the reward."""
QUEST = "a1q2"
DONE = "a1q2-QuestRewardTaken"
REQUIRES = ["a1q4"]

KILLED = "a1q2-KilledBrutus"

STEPS = [
    {"verb": "goto", "area": "1_1_4_1", "skip_if": ["Visited-1_7", KILLED]},
    {"verb": "goto", "area": "1_1_5", "skip_if": ["Visited-1_7", KILLED]},
    {"verb": "goto", "area": "1_1_6", "skip_if": ["Visited-1_7", KILLED]},
    {"verb": "goto", "area": "1_1_7_1", "skip_if": ["Visited-1_7", KILLED]},
    {"verb": "talk", "npc": "Tarkleigh", "option": "Brutus", "area": "1_1_town", "expect_flag": "a1q2-ReceivedQuest",
     "skip_if": ["a1q2-ReceivedQuest", KILLED], "optional": True},
    {"verb": "talk", "npc": "Nessa", "option": "Brutus", "area": "1_1_town", "skip_if": ["NessaChatBrutus", KILLED], "optional": True},
    {"verb": "reward", "npc": "Nessa", "option": "Prison Reward", "area": "1_1_town", "skip_if": "a1q2-NessaRewardTaken",
     "optional": True},
    {"verb": "goto", "area": "1_1_7_1", "skip_if": KILLED},
    {"verb": "goto", "area": "1_1_7_2", "skip_if": KILLED},
    # Piety's room in the Warden's section (on the way to Brutus): seeing her sets SeenPietyPrison (Act1Complete).
    {"verb": "tp", "target": "PietyPrison", "skip_if": ["SeenPietyPrison", KILLED], "optional": True},
    {"verb": "wait_flag", "flag": "SeenPietyPrison", "timeout": 10, "skip_if": ["SeenPietyPrison", KILLED], "optional": True},
    {"verb": "kill", "target": "Brutus", "appear_timeout": 15, "expect_flag": KILLED, "skip_if": KILLED},
    {"verb": "goto", "area": "1_1_8", "via": "transition"},
    {"verb": "talk", "npc": "Tarkleigh", "option": "Brutus", "area": "1_1_town", "expect_flag": "a1q2-TarkleighCongratulated",
     "skip_if": ["a1q2-TarkleighCongratulated", DONE], "optional": True},
    {"verb": "reward", "npc": "Tarkleigh", "option": "Brutus Reward", "area": "1_1_town", "skip_if": DONE},
    {"verb": "passives"},
]
