"""a8q2 "Lunar Eclipse" (act8.md), Act 8 -> 9: its fight is a8q3's; the quest ends on entering The Blood Aqueduct from
The Harbour Bridge (the exit opens with a8q23-KilledBosses)."""
QUEST = "a8q2"
DONE = "Visited-9_1"
REQUIRES = ["a8q3"]

STEPS = [
    {"verb": "goto", "area": "2_8_13"},
    {"verb": "goto", "area": "2_9_1", "via": "transition"},
    {"verb": "wait_flag", "flag": "Visited-9_1", "timeout": 20},
]
