"""a7q3 "Web of Secrets" (act7.md): Silk takes the venom (and the map) and gives the Obsidian Key (offer 48); coming
to the barrier before Level 2 with the key lifts it (a7q2-ClearedSinBlock), and the key used at the passage cover in
Level 2 opens the way into The Den (a7q1-UsedKey)."""
QUEST = "a7q3"
DONE = "a7q1-UsedKey"
REQUIRES = ["a7q2"]

STEPS = [
    {"verb": "goto", "area": "2_7_5_1", "skip_if": ["a7q1-HaveKey", "a7q2-ClearedSinBlock"]},
    {"verb": "talk", "npc": "SilkSins", "option": "Black Venom", "area": "2_7_5_1", "expect_flag": "a7q3-DeliveredItem", "skip_if": ["a7q3-DeliveredItem", "a7q1-HaveKey"]},
    {"verb": "reward", "npc": "SilkSins", "option": "Black Death Reward", "area": "2_7_5_1", "pick": ["Obsidian Key"], "skip_if": "a7q1-HaveKey"},
    {"verb": "tp", "target": "MaligaroSinsBarrier", "area": "2_7_5_1", "expect_flag": "a7q2-ClearedSinBlock", "skip_if": "a7q2-ClearedSinBlock"},
    {"verb": "goto", "area": "2_7_5_2", "via": "transition"},
    {"verb": "interact", "target": "MaligaroPassageCover", "area": "2_7_5_2", "expect_flag": "a7q1-UsedKey"},
]
