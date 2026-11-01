"""a5q6 "The Ravenous God" (act5.md), Act 5 → 6: the Sign of Purity from the Tomb of the First Templar (The Ossuary)
opens the rooftop; in its socket it wakes Kitava, who never dies: /kill reaches his threshold (a5q6-CompletedBoss,
QuestStaticRewards 38: −30% to all resistances) and he leaves. Sin and Lilly wait on the dock; Lilly's "Sail to
Wraeclast" (NPCTalk 893, AreaTransitionTo) moves the player to Lioneye's Watch (Visited-6_town)."""
QUEST = "a5q6"
DONE = "Visited-6_town"
REQUIRES = ["a5q4"]

COMPLETED = "a5q6-CompletedBoss"
SEEN = "a5q6-SeenItem"

STEPS = [
    {"verb": "goto", "area": "1_5_6", "skip_if": [SEEN, COMPLETED]},
    {"verb": "tp", "target": "PurityBox", "optional": True, "skip_if": [SEEN, COMPLETED]},
    {"verb": "kill", "target": "near 30", "timeout": 20, "optional": True, "skip_if": [SEEN, COMPLETED]},
    {"verb": "interact", "target": "PurityBox", "area": "1_5_6", "skip_if": [SEEN, COMPLETED]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act5/KitavaKey", "expect_flag": SEEN, "skip_if": [SEEN, COMPLETED]},
    {"verb": "goto", "area": "1_5_8", "skip_if": COMPLETED},
    {"verb": "interact", "target": "ArenaSocket", "area": "1_5_8", "expect_flag": "a5q6-DeliveredItem", "skip_if": ["a5q6-DeliveredItem", COMPLETED]},
    {"verb": "kill", "target": "Kitava1", "area": "1_5_8", "appear_timeout": 15, "expect_flag": COMPLETED, "skip_if": COMPLETED},
    {"verb": "talk", "npc": "SinKitava", "option": "Kitava", "area": "1_5_8", "expect_flag": "SinOnKitavaDefeat", "skip_if": "SinOnKitavaDefeat",
     "optional": True},
    {"verb": "talk", "npc": "Act5/Lilly", "option": "Sail to Wraeclast", "area": "1_5_8", "no_wait": True},
    {"verb": "wait_flag", "flag": DONE, "timeout": 60},
]
