"""Campaign quest step files: one module per quest, `quests/<Quest Id>.py` (Ids from data/tables/Quest.json).

The runner loads them by file name (this package is not imported), runs them in `order.ORDER`, and reports a quest
without a file as "not implemented". Files starting with `_` are ignored. A module defines:

    QUEST = "a1q1"                      # its Quest Id (must equal the file name)
    DONE = "a1q1-Complete"              # QuestFlags Id meaning the quest is finished; set -> the quest is skipped
    REQUIRES = ["a1q5"]                 # optional: quests that must be done first (else this one is "blocked")
    STEPS = [ {...}, ... ]              # run in order

Each step is a dict with the verb under "verb" (aliases "do", "action") and the verb's fields, or the compact form
whose first key is the verb mapped to its main argument ({"kill": "Hillock"}, {"talk": "Tarkleigh", "option": ...}).
Every step may also have `skip_if` (a flag: skip the step when set), `area` (goto it first), `timeout` (seconds) and
`expect_flag` (wait for it afterwards). Verbs: goto, tp, kill, pickup, equip, interact, talk, reward, use_item,
wait_flag, command, pantheon, passives; see steps.py and Agents/docs/dev/campaign-automation.md.
Example (illustrative flags and names, not a real quest file):

    QUEST = "a1q1"
    DONE = "a1q1-Example-RewardTaken"
    STEPS = [
        {"goto": "1_1_1"},
        {"pickup": ["Wand", "Sword", "Bow"], "skip_if": "a1q1-Example-HasWeapon"},
        {"equip": ["Wand", "Sword", "Bow"]},
        {"verb": "kill", "target": "Hillock", "skip_if": "a1q1-Example-KilledHillock"},
        {"verb": "goto", "area": "1_1_town", "via": "transition"},
        {"verb": "reward", "npc": "Tarkleigh", "option": "Hillock Reward", "pick": ["Fireball", "Sparks"]},
    ]
"""
