"""a6q1 "The Brine King" (act6.md), Act 6 → 7: the third Nessa in Merveil's cavern and the Black Flag chest beside her;
at The Beacon the lever refuels and lights it (the fuel carts are not created) and the beacon statue takes the flag
(a6q1-DeliveredFlag: Weylam comes ashore). Weylam's "Sail to the Brine King's Reef" (1006) and, after Tsoagoth, Sin's
god talk (1003: the Pantheon panel, the runner picks the gods) and Weylam's "Sail to the Bridge Encampment" (1010); in
the Bridge Encampment the runner makes sure both gods are chosen."""
QUEST = "a6q1"
DONE = "Visited-7_town"
REQUIRES = ["a6q2"]

DELIVERED = "a6q1-DeliveredFlag"
KILLED = "a6q1-KilledBoss"

STEPS = [
    {"verb": "goto", "area": "2_6_13", "skip_if": ["a6q1-HaveFlag", DELIVERED]},
    {"verb": "tp", "target": "NessaMerveilsCaverns", "expect_flag": "a6q1-SeenNessa3", "skip_if": ["a6q1-SeenNessa3", DELIVERED], "optional": True},
    {"verb": "tp", "target": "BlackFlagChest", "optional": True, "skip_if": ["a6q1-HaveFlag", DELIVERED]},
    {"verb": "kill", "target": "near 30", "timeout": 20, "optional": True, "skip_if": ["a6q1-HaveFlag", DELIVERED]},
    {"verb": "interact", "target": "BlackFlagChest", "area": "2_6_13", "skip_if": ["a6q1-HaveFlag", DELIVERED]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act6/BlackFlag", "expect_flag": "a6q1-HaveFlag", "skip_if": ["a6q1-HaveFlag", DELIVERED]},
    {"verb": "goto", "area": "2_6_14", "skip_if": [DELIVERED, "Visited-6_15"]},
    {"verb": "tp", "target": "BlackCrest_BeaconLever", "expect_flag": "a6q1-SeenBeacon", "skip_if": ["a6q1-SeenBeacon", DELIVERED], "optional": True},
    {"verb": "interact", "target": "BlackCrest_BeaconLever", "area": "2_6_14", "expect_flag": "a6q1-RefueledBeacon",
     "skip_if": ["a6q1-RefueledBeacon", DELIVERED]},
    {"verb": "interact", "target": "BlackCrest_BeaconInteract", "area": "2_6_14", "expect_flag": DELIVERED, "skip_if": DELIVERED},
    {"verb": "talk", "npc": "WeylamBeacon", "option": "Introduction", "area": "2_6_14", "skip_if": ["WeylamBeaconIntro", "Visited-6_15"],
     "optional": True},
    {"verb": "talk", "npc": "WeylamBeacon", "option": "Sail to the Brine King's Reef", "area": "2_6_14", "no_wait": True,
     "skip_if": ["Visited-6_15", KILLED]},
    {"verb": "wait_flag", "flag": "Visited-6_15", "timeout": 60, "skip_if": KILLED},
    {"verb": "goto", "area": "2_6_15", "skip_if": KILLED},
    {"verb": "kill", "target": "BrineKing/BrineKing", "appear_timeout": 15, "expect_flag": KILLED, "skip_if": KILLED},
    {"verb": "talk", "npc": "SinBrineKing", "option": "The Brine King", "area": "2_6_15", "expect_flag": "SinBrineKingReward",
     "skip_if": "SinBrineKingReward", "optional": True},
    {"verb": "pantheon", "optional": True},
    {"verb": "talk", "npc": "WeylamReef2", "option": "Sail to the Bridge Encampment", "area": "2_6_15", "no_wait": True},
    {"verb": "wait_flag", "flag": DONE, "timeout": 60},
    # The gods once more in the Bridge Encampment, where the choice is always allowed (a town): not optional, so a run
    # whose Reef choice did not take fails here instead of going on without a Pantheon.
    {"verb": "goto", "area": "2_7_town"},
    {"verb": "pantheon"},
]
