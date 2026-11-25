# Effect rules: a guide

For players who want to know what works, for people adding an effect, and for AI agents picking the work up. The
reference for every table, argument and design decision is [effect rules](effect-rules.md); this page is the way in
and repeats none of it.

## What it is

Most of what an item, passive, buff or map modifier does can be said as one sentence: *when* something happens, *if*
some things are true, *do* something to someone, with numbers taken from somewhere. The effect rules system stores
that sentence as rows in world.db instead of code. The engine reads the rows at startup and runs them.

So "25% chance to Curse Enemies which Hit you with a random Hex, ignoring Curse Limit" is not a function. It is:

| Part | Row |
|---|---|
| Source | the stat `random_curse_when_hit_%_ignoring_curse_limit` on the character |
| When | `when_hit` |
| If | `chance`, percent from the item's roll |
| Do | `curse` with `"curse":"random"`, `"ignore":"curse_limit"`, on the enemy, level fixed 1 |

Change the trigger to `block` and it curses on block. Change `random` to `Despair`, drop `ignore`, and it is another
item. Nothing is compiled.

## What it can do today

- **Rules** (`fx_rules` and its `fx_rule_*` tables): an effect on an event. Triggers, conditions and actions are parts;
  the lists with what each takes are the tables `fx_triggers`, `fx_conditions`, `fx_actions` (`built = 1` means the
  engine runs it; `built = 0` is a name kept for lines that will need it).
- **Modifiers** (`fx_modifiers`): a stat that counts as a plain stat while a condition holds or per something
  ("10% increased Damage while Shocked" counts as `damage_+%` under the state `while_shocked`).
- **Aliases** (`fx_aliases`), **per attribute** (`fx_per_attribute`), **item-local lines** (`fx_item_local`): a stat that
  is another stat under a different name, per so many points of an attribute, or only while its item sits in a slot.
- **Skills items trigger** (`effect_skills`, `effect_buffs`): how a granted skill is released (an area hit, a pulse, an
  aura, a ground patch, a buff).
- **Limits** (`fx_limits`): what an action can be told to ignore.
- **The ledger** (`fx_stats`): every stat in the game with how it is handled (`rule`, `modifier`, `code`, `item`,
  `display`, `none`), why it is not handled yet, and `checked = 1` once someone has seen it work in the real client.

What it cannot do yet is listed at the end of the reference ("Not yet") and by `built = 0` in the parts tables.

## Using it

- **Is a line working?** Look its stat up in `fx_stats`. `handled = none` with a `why` is an open line.
- **Turning it off.** `EffectRules.Enabled` switches the table-driven rules off and the older code back on, for
  comparing the two.
- **What is live on a character.** `EffectRules.Describe(player)` lists the rules that stand for it right now.

## Adding an effect

Work down this list and stop at the first step that fits. Each step is cheaper than the next.

1. **It is an existing stat under another name** → one `fx_aliases` row.
2. **It is a plain stat under a condition, or per something** → one `fx_modifiers` row (or `fx_per_attribute`,
   or `fx_item_local` when the line belongs to the item or its ring slot).
3. **It happens on an event and every part exists** → a rule: one row each in `fx_rules`, `fx_rule_sources`,
   `fx_rule_when`, `fx_rule_do`, and `fx_rule_if` rows for its conditions.
4. **A part is missing** → add the part, then write the row. A new condition is a fact and its test; a new trigger is
   an event raised where it happens; a new action is a kind in `EffectKinds.cs`; a new limit is a name in
   `EffectLimits` and one test in the action that owns the limit. Give the part a row in its parts table.
5. **It changes how a calculation works** (a fixed critical strike chance, movement speed from evasion) → code in
   the function that calculation already lives in, and `handled = code` in `fx_stats`.

Then, always: mark the stat in `fx_stats`, in the same SQL file.

## Rules for adding and maintaining

- **A missing piece becomes a reusable part, never a one-off.** If the line says "ignoring X", "for # seconds",
  "within # metres" or "a random Y", that is an argument on the action, not a new function. The test: could the next
  item of this family be done with a row alone?
- **Every number is a value on the row** and says where it comes from: fixed, the item's roll, another stat, or the
  granted skill's own data.
- **One meaning, one place.** A line that changes a number the character panel shows belongs in the function the
  panel reads, so the panel and the hit agree.
- **After merging main, look for doubles.** Main builds lines too. Where main built the same line, remove this
  system's copy, so one effect never has two sources. Compare by stat and by feature name: a doubled feature can
  share no stat.
- **Data changes are new SQL files** in `sql/updates/pending_world/`. A file that has been pushed is never edited; a
  fix is the next file.
- **Per-hit code stays cheap.** A rule costs nothing for a character without its stat; keep it that way
  ([hot paths](performance.md#hot-paths)).
- **Say how sure you are.** `fit = approximate` in `fx_stats` when the row is close but not exact, with the
  difference in the note. `checked = 1` only for what was seen working in the real client.
- **Guesses are labelled.** A radius, an art name or a duration the data does not give is written as a guess in the
  row's note.
- The layer rules of [AGENTS.md](../../../AGENTS.md) hold: game rules are module code and world.db rows, never
  framework.

## For mods

A mod can register a new action kind (`EffectRules.Register`) or a new way to release a granted skill
(`Game.RegisterTriggeredSkillExecutor`), and listen to every combat event through `GameEvents.Combat`. The rows
themselves live in world.db and come from the server's SQL updates; a mod shipping rows of its own is not built yet,
and is the step that would make a custom item's effect a row like a real one's.

## Checking your work

1. Build, and start the server once: a row the engine cannot read is refused by name in the log (`[rules] refused
   ...`), and the rule count is printed.
2. Wear the item and read the character's live rules.
3. Try it in the real client, then set `checked = 1`.

## Where it can go

The same sentence shape fits monster modifiers, map modifiers, atlas passives, ground patches and minions as actors.
Those need their carriers and events built once; after that they are rows like the rest. The parts that are named but
not built (`built = 0`) are the map of that work.
