"""a5q1a "Return to Oriath", Act 4's end (act4.md): the withered Dialla has opened the Ascent; on the summit coming near
the Resonator lever sets a5q1-SeenResonator and pulling it a5q1-UsedResonator, the gate of The Slave Pens; the Oriath
Portal then leads there. The quest leaves the log there (Visited-5_1)."""
QUEST = "a5q1a"
DONE = "Visited-5_1"
REQUIRES = ["a4q1"]

USED = "a5q1-UsedResonator"

STEPS = [
    {"verb": "goto", "area": "1_4_7", "skip_if": USED},
    {"verb": "tp", "target": "PortalDeviceLever", "expect_flag": "a5q1-SeenResonator", "skip_if": ["a5q1-SeenResonator", USED]},
    {"verb": "interact", "target": "PortalDeviceLever", "area": "1_4_7", "expect_flag": USED, "skip_if": USED},
    {"verb": "goto", "area": "1_5_1"},
    {"verb": "wait_flag", "flag": DONE, "timeout": 20},
]
