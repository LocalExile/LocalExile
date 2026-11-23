# Effect rules

One data-driven engine for "when X happens, do Y" effects: a stat turns a rule on for an actor, an event fires it (or it
stands while worn), checks, chance and cooldown gate it, and an effect runs. The rules are rows in world.db, so a new
trigger is usually a row and not code. Code: `Modules/Core/Game/Effects/` (`EffectRules.cs` the engine,
`EffectKinds.cs` the effect kinds, `GrantedSkillRules.cs` item-granted skills, `TimedBuffs.cs`, `HitEvents.cs`).

## Why it exists

Before it, each on-kill, on-hit or on-block effect was its own code: about 80 charge rules written out in
`ChargeGains.cs`, Onslaught on kill in `OnslaughtOnKill.cs`, each trigger gem its own payload. An item-granted skill with
a trigger stat did nothing unless someone wrote a handler: of 188 such skills in the client's data, the code could
release 12. With the engine, the same 188 are reached by adding rows, and what a row can say (which event, which facts,
which chance, which effect, on what) is the same for a unique's line, a passive, a monster modifier or a mod's own
content. What it does not replace yet: the charge rules (they share the events and move over later) and the trigger
gems' own executors.

## The three tables (world.db)

| Table | One row is | Columns |
|---|---|---|
| `effect_rules` | a rule a stat turns on | `stat`, `ord`, `rule` (JSON, below), `enabled`, `reviewed`, `note` |
| `effect_skills` | how a released skill is carried out and looks | `effect` (GrantedEffects Id), `how`, `art`, `scale`, `seconds`, `radius`, `hits`, `every`, `spread`, `enabled`, `checked`, `note` |
| `effect_buffs` | the buff a released skill gives its user | `effect`, `buff` (BuffDefinitions Id), `routine`, `seconds`, `stats` (JSON), `enabled`, `checked`, `note` |

Only `enabled = 1` rows load, and for rules only `reviewed = 1`. A row with anything unknown is refused and logged, never
half-applied. `checked = 0` marks a look or a number that is a guess: `SELECT effect, art, radius, note FROM effect_skills
WHERE checked = 0;` lists what still needs comparing with the real game (today: all of them).
Rows are added with a new file in `sql/updates/pending_world/` ([databases](databases.md)); the tables' own files are in
`sql/base/world/`.

## A rule

```json
{"when":"kill", "who":"self", "carrier":"skill", "only":"FireBurstOnHit",
 "checks":["ignited","!critical"], "any":false, "need":[{"stat":"strength","min":150}],
 "chance":VALUE, "cooldown":VALUE, "uses":VALUE, "depth":0,
 "do":[{"kind":"skill","at":"monster"}]}
```

- `when`: `hit`, `kill`, `when_hit`, `block`, `stun`, `skill_use`, or `worn`. `worn` is no event: the rule stands while
  its stat is there (an item's always-on skill) and its effects are started and ended, so it has no chance, cooldown or
  uses.
- `who`: whose event (`self` by default; `totem`, `trap`, `mine`, `minion`, or `any`).
- `carrier`: where the stat is read. Without it, the actor's own stats. `"skill"`: each skill a worn, active item grants,
  with that skill's own stats; `only` narrows the rule to one of them by its GrantedEffects Id.
- `checks`: facts of the event, `!` for not; all must hold, or any with `"any":true`.
  - hit: critical, spell, attack, melee, bow, killed, frozen, ignited, bleeding, marked, has_mark, normal, magic, rare,
    unique, rare_or_unique
  - kill: frozen, ignited, bleeding, poisoned, and the rarities
  - when_hit: critical. block: spell, attack. stun: melee.
  - skill_use: vaal, curse, trap, attack, spell, melee, bow, travel, movement, warcry, strike, slam, channelled, brand,
    aura, guard
- `need`: stats of the actor, checked when its rules are worked out, never per event.
- VALUE: `{"fixed":n}`, `{"from":"stat"}` (the rule's own stat), `{"stat":"name"}`, or `{"data":"name"}` (a number of
  the carrier: a granted skill has `cooldown`, `uses`, `level`); each with optional `scale`, `fallback`, `min`, `max`.
- `depth`: how many triggered effects may stand between the actor's own action and the event (the loop guard: by
  default a triggered skill's hits trigger no further skill).
- `do`: effects, each a `kind` with its own parameters.

## Effect kinds

| Kind | Does | Parameters |
|---|---|---|
| `onslaught` | Onslaught for a time | `seconds` |
| `charge` | charges, through the charge rules' own step | `type`, `count` |
| `buff` | a timed buff | `seconds`, `effect`, and `template` or `buff` + `values` |
| `skill` | releases the carrier's granted skill through its executor | `at`: `monster` or `self` |
| `stats` | stats the actor has while a `worn` rule stands | `stats`: name to value |

A kind is registered with `EffectRules.Register(new Kind(...))`; a mod can add its own. A kind that can stand has
`Stand`; one that only stands has `StandOnly`.

## Executors for granted skills (the `skill` kind)

Which executor takes a skill is decided by data, first match:

| Executor | Takes | Does |
|---|---|---|
| `curse` | a curse or mark in the curse catalog (an item's "…Triggered" variant is its curse) | applies it to the event's monster |
| `summon` | a skill in the summon catalog | the gems' own summon routine |
| `buff` | a skill with an `effect_buffs` row | the skill's own routine (`routine`: `BloodRage`) or a timed buff with the row's values |
| `worn` | `effect_skills.how = worn` | nothing itself: the skill only carries other effects of a `worn` rule |
| `aura` | `how = aura` | a `DamageAura` (the Righteous Fire burn's mechanism) with the skill's "damage to deal per minute", while worn. `art`: the buff on those inside, the buff the wearer states as the aura's source, a circle drawn at the radius |
| `area` | `how = area` or `pulse` | one area hit where the event was (several for a storm: `hits`, `every`, `spread`), with the row's art as an effect object. `pulse` under a `worn` rule: the same hit around the wearer every `every` seconds while it moves |

`Game.RegisterTriggeredSkillExecutor`, `Game.RegisterBuffRoutine` and `Game.AllowTriggeredAreaSkill` add more from a
module or a mod. A granted skill with a rule and an executor is among the Actor's skills and cannot be used by hand.

## Adding something

- A unique's trigger whose skill an executor already takes: one `effect_rules` row for its trigger stat (most exist) and
  one `effect_skills` or `effect_buffs` row for the skill.
- A new condition: a fact in `EffectRules.Facts` and its test in `Fact`. A new event: a member of `What`, raised through
  `Game.RaiseCombatEvent`.
- A new kind of effect: a `Kind`. A new way to carry out a skill: an executor.
- Never a one-off beside the engine: the point is that the next author finds the part already there.

## Switches and tools

`EffectRules.Enabled` (off: the old code runs instead; a stat a rule drives reads 0 for the old code through
`EffectRules.Unhandled`), `EffectRules.LoopGuard`, `EffectRules.ChanceScale` (every chance times this, for testing),
`Game.TriggeredAreaArt`, `Game.TriggeredAreaDraw`. `GameEvents.Combat` hands every combat event to modules and mods;
`EffectRules.Describe(player)` lists an actor's live rules.

## Speed

Per hit with no hit rule on the actor: 25 to 35 ns. With two rules that do not fire: about 200 ns. Firing: about 320 ns
plus the effect. A rule's stat, chance, cooldown and `need` are resolved when the actor's stats change
(`EffectRules.Configure`, from `PlayerStatsUpdated`), not per event.

## Client findings behind it (3.29.3c)

- S→C 0x1ce (`InstantAction`, triggered) makes the client run a skill's action as a triggered one, but for the item
  skills tried (Lightning Bolt, Fire Burst, Fire Burst on Kill) it draws nothing: their action classes gather targets
  and create no effect, and no table links them to art. The server shows the art as an effect object instead.
- The client refuses a triggered action whose target is far off screen (`Client couldn't execute a triggered … action`
  in `Client\logs\LatestClient.txt`, with the ActionTypes HASH16).
- `MiscAnimated.__col4` is an effect's own radius in units (250 units to 23 cells); 0 when the table does not say
  (`FireNovaEnchantment`), and then an art fitted to a radius is the wrong size.
- A buff row with two effect lists (`BuffVisuals.EPKFiles1`, `EPKFiles2`): the first is drawn on whoever wears the buff,
  the second on the one who states it as an aura's source (`Buff.AuraSource`). Death's Oath stated as an ordinary buff
  put the victims' flames on the wearer. Its aura effect is only a swirl on the character, not a circle at the radius.
- `ActiveSkills.IsManuallyCasted` is true for the item-triggered marks too: it does not tell a triggered grant from a
  cast one. The skill's own trigger stat does.
- A mod whose display value is 0 has no text line on the item (`FireDamageToNearbyEnemiesOnKillUnique__1`); its twin
  with value 1 shows "Trigger Level 1 Fire Burst on Kill".
- An always-on item skill has `cast_on_gain_skill` in its stat set (the aegises, Death Aura, Blinding Aura, Abberath's
  Fury, Death Walk); most of them have no radius or period in their data.

## Not yet

Real projectiles for the skills that are a volley (Bone Nova, Icicle Burst, Molten Burst, Starfall are one-hit
stand-ins), ground effects, the "with this weapon" and "socketed in this item" facts, the four elemental aegises, minion
and monster hits as events, areas and ground patches as carriers, and moving the charge rules over.
