# Effect rules

One data-driven system for what things do in the game: a stat turns a rule on for an actor, something fires it (an
event) or it simply stands (while worn, while in an area, while on a patch of ground), checks, chance and cooldown gate
it, and an effect runs on a target. The rules are rows in world.db, so adding or changing an effect is usually a row
and not code. Code: `Modules/Core/Game/Effects/` (`EffectRules.cs` the engine, `EffectKinds.cs` the effect kinds,
`GrantedSkillRules.cs` item-granted skills, `TimedBuffs.cs`, `HitEvents.cs`).

## Scope

The aim is every effect the game has that fits this shape, from any source: item mods (each recreated one for one),
passives, granted and gem skills, charges, buffs and auras, monster modifiers, map mods, atlas passives, ground
patches, and what mods add. Sources are interchangeable through carriers (an item, a skill, a buff, a monster's mod
set, an area, a patch of ground hold stats and hand them on), and actors are too (a player, a minion, a totem, a
monster). Nothing is left out on purpose; the only limit on what goes in is speed.

**What is built so far is the first slice**: the engine, the combat events, and item-triggered and always-on item
skills. It is where the engine was proven, not what it is for. Next, in order: minions and monsters as actors; areas
as carriers (map mods, fragments, atlas passives, a worn item's map modifier); ground patches as carriers; then every
item mod, with the charge rules and the existing one-off code moving over as each row matches what the code did.

The rules it is built by:

- A trigger, condition, target or effect that does not exist yet is added as a reusable part of the tables, never as a
  one-off beside them.
- Every number is a value on the row ("4 seconds" is "X seconds" with X = 4), and names its source: fixed, the rule's
  own stat, another stat, the carrier's data.
- Anything that could be an option is one, and anything that can be on or off has a switch.
- An effect fires when it properly should (the right hits, the right actor), not only where older code happened to.

## Why it exists

Today each effect is its own code: about 80 charge rules written out in
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

## The fx tables

The next form of the rules, being filled stat by stat. `FxRules.Compile` turns each fx rule into the engine's own
rule when the game data loads, so the 30 first rules now come from these tables; a stat with an fx rule leaves its
`effect_rules` row out, and a rule that names a part with no code yet is left out with the reason in the log. The unit is the stat: a mod is a list of stats (client.db `Mods`), so a stat rebuilt once is
rebuilt for every mod, passive or area modifier that carries it.

| Table | Holds |
|---|---|
| `fx_triggers`, `fx_conditions`, `fx_timers`, `fx_values`, `fx_targets`, `fx_actions` | the parts rules are built from, one row each, with what it takes, whether its code exists (`built`), where it came from and what it does |
| `fx_quantities` | the numbers the game works out for an actor (maximum life, action speed), and whether code or a table does the arithmetic |
| `fx_modifiers` | the short form for a plain number: a stat changes a quantity (flat, increased, more, override, minimum, maximum, flag), under conditions |
| `fx_rules` with `fx_rule_sources`, `fx_rule_when`, `fx_rule_if`, `fx_rule_do` | a rule: what switches it on, any of its triggers, its condition groups (all in a group, any group), its actions in order |
| `fx_stats` | every stat: its kind, how it is handled (modifier, rule, item, code, display, deferred, none), the files that read it, and for one not rebuilt, why and how it could fit |

`fx_modifiers.counts_as` (added by an update, so it is not in the base file) is the plain stat a conditional stat's
amount is counted onto while its conditions hold; a condition there may carry `"not": true`. The first 1,856 rows were
read from the code (the plain stats it names, and what `StatConditions` decides and folds for each stat) and record
what happens today. `StatConditions.LoadTable` reads them when the game data loads: for a stat with a row, the plain
stats come from `counts_as`, and the condition comes from the row when every part is a state flag, a weapon set-up,
auras or one recent event (641 stats); any other part (a count, an amount) stays the id's own. The effect rules'
on/off switch turns this off too (`StatConditions.UseTable`). Run over all 23,362 stats with 60 random subjects each,
table and id agree everywhere; per stat the table costs less (the plain stats are a stored array).

The charge rules of `ChargeGains.cs` (62 of its 66; not the four of one skill each, nor the charges gained over time)
are fx rules, one per stat. They use the trigger `skill_hit`: the hit as the charge rules count it (`ChargeEvent.Hit`),
which carries its facts, where `hit` (`HitLanded`) is every hit of the character's side. A chance may add the event's
skill's own share of the stat (`"skill": true`). While a rule is live for a character (`EffectRules.LiveFor`),
`ChargeGains.Roll` leaves its stat to the rule; a character without the stat still gets a skill's own share from the
code. The `charge` action gains, fills to the maximum, or takes one or all, of a type or a random one.

Intimidate, Unnerve, Crush and Hinder on hit (`HitDebuffs.cs`) are fx rules on the `hit` trigger with `"who": "any"`.
A chance may be several stats added up and rolled once (`{"from":"sum","of":[{"stat":...,"when":"attack"}]}`): the rule
is one engine rule, live while any of the stats is there, and each term counts only for an event with its fact. The
`debuff` action times the debuff from the hit's own clock (`HitLanded.At`).

Maim, Blind, Withered and Exposure on hit are rules the same way. The rule rolls the chance and names the debuff, its
base duration and, for Exposure, its base magnitude (the action's value). What the debuff asks of the monster (immunity,
`avoid_maim_%_chance`) and what the hitting side's other stats do to it (`blind_duration_+%`, `withered_effect_+%`, the
Exposure extras) stays in `ApplyNamedDebuffLocked`, read from the hit's own stats (`HitLanded.PlayerStats` with
`SkillStats`); `fx_modifiers` lists those stats. A term's `when` may be `deals_fire`, `deals_cold` or `deals_lightning`
(`HitLanded.Damage`, after mitigation). Elemental Equilibrium and the projectile-only Maim chance are still the code's.

More actions: `curse` puts a hex on the hit's monster at the gem level its value gives ("Curse Enemies with X on Hit";
`refresh_after` is how long a running curse is left alone); `buff` with `phasing` or `arcane_surge` gives the game's own
buff of that name; `charge` with type `spirit` gives a Spirit Charge. An amount the hit or block code works out itself
(recovery on block, the chances to poison, bleed, freeze, impale, stun and knock back) is modifier rows of a quantity
and one rule written as 'not built': the code still spends it.

A rule on the `interval` trigger (`{"seconds": ...}`, fixed or the stat's own value) is not an event rule. The 14
charges-over-time stats are such rules: `FxRules.Periodics` hands them to the charge system, whose timers use that list
while the rules are on and `ChargeGains.Periodics` otherwise, each stat in the same timer slot.

The `when_hit` and `block` events carry what their site had (`ChargeEvent.PlayerStats`, `HitAt`, `MonsterId`), and their
monster is the attacker. The `ailment` action puts an ailment without damage on it (chill, shock or freeze for the
duration timer; scorch, brittle or sap at their own length), and `debuff` with `blinded` blinds it.

A stat that counts once per charge held is a modifier row with a `per` (`{"of":"power_charge"}`, also `special`,
`maximum`, `cap`, and `when` for "with no / any charges"); the row's quantity is the stat it adds to, one row for each.
`ChargeEffects.LoadTable` reads them, and `Expand` uses the tables' lists while the rules are on.

A stat no code reads can be built from rows alone, and then works only while the rules are on: a rule made of
existing parts (origin `parts`), or a modifier row whose `conditions` the table can answer and whose `counts_as` is a
stat the calculator already reads (the Labyrinth boot enchantments). A rule on `hit` can ask the monster's state as the
hit found it (`enemy_state`: bleeding, poisoned, chilled, shocked, blinded, cursed, frozen, ignited), and one on
`when_hit` or `block` the attacker's rarity.

`recover` gives or takes life, mana or energy shield at once (flat, or a percent of the maximum with `"of":"maximum"`).
`raise` records an event by name for the duration timer (4 seconds), and a modifier row's `recently` condition can ask
for it. A stat whose rule records the event its own condition waits for keeps its id in the sums whatever the condition
says (`StatConditions.KeepsOwnId`), or the rule could never be live.

An area modifier that gives the area's monsters a stat they already act on is a modifier row with the target
`enemies_area` (the quantity is the monster's stat, the scale how many of it one point gives): `MapMonsterEffects` adds
those after its own pairs while the rules are on. `scripts/dev/fx_needs.py` notes for every stat nothing reads what it
would need (`fx_stats.fit`), and ranks the needs.

`grant` gives OTHER actors a bundle of stats (`Bundles.cs`, `Grants.cs`). A bundle is read from the game's own data:
a unique's lines (`"unique"`), Mods rows (`"mod"`) or listed stats (`"stats"`: a number, `[min, max]`, or
`{"from":"roll"}` for the giving line's own rolled value); stats that describe the item itself are left out. `roll`
(where in its ranges the bundle sits, 100 unless said) and `effect` (a percent of all of it) are values like any other.
A bundle can also be one of the game's aura templates (`"template"`: a BuffTemplates Id, or `"line"` for the giving
line's own, Mods `BuffTemplate1`): its buff's `GrantedStats` with the line's rolled numbers, its `AuraRadius` as the
reach, its BuffDefinitions row as the buff its carriers are shown with (`"shows"` names one by hand). That is where
"nearby" is a number: 60 cells for the stat auras, 35 for Blind, 25 for Intimidate, 30 for the other debuff auras.
Given to a monster, a stat that `fx_reads_as` names another for is given as that one (elemental damage taken is the
three elements' on a monster). To `self` a grant stands, or lasts for the duration timer from an event (a timed buff);
to `players_area` every player in the area has it while the rule is active.

An area gives too. Modifier rows with the target `players_area` count an area's stat as a stat its players act on
(`AddAreaStatsForPlayer`, in the stat sources), as `enemies_area` rows do for its monsters; a rule whose source is of
kind `area` gives the area's monsters or players a bundle (`FxRules.AreaGrants`; flat life is not counted for
monsters there, their life is worked out when they are made).

To `enemies_area` or `enemies_near` (`radius` in cells) the grant stands while its rule is active: a step every 200 ms
puts it on the living enemies it names, the ones that arrive later too, and takes it off when the rule ends or they
leave the radius. To `enemy` it lasts for the duration timer on the event's monster. A monster carries it as a temporary
stat source keyed `fx:<player id>:...`, so its stat graph adds and removes it; `ForgetGrants` takes a leaving player's
off the instance's monsters. A stat does on a monster what monster code reads it for; flat maximum life is taught in
`ChangeGrantLocked`. Two sources of one bundle stack.

A rule with a source of kind `item` reads a local stat (Stats `IsLocal`) and has the worn item as its carrier:
the stat never reaches the character's sums, and `ItemModStats` drops the whole row of a line that speaks about other
creatures, so `RuleSources` reads each worn item's own lines as rolled. The rule is live once per worn item that has
the stat. In those rows the game's data pairs the display stat with the stat meant for the nearby creature (54 Mods
rows); the rules name the stats monster code reads instead, since several of the paired ones are player names.

A character's stat that is a general stat for one skill ("40% increased Fireball Damage") is a row of
`fx_skill_stats` (stat, skill, the skill stat it adds to): `ApplySkillSpecificStats` adds the character's value to the
skill's own stats where they are put together (`SupportedSkillAt`, after the supports), so whatever merges a skill's
stats with the character's reads it. The skill is named by its ActiveSkills Id or its GrantedEffects Id in snake case.

An aura or buff skill's own "increased effect" stat is a row of `fx_aura_effects` (the skill's GrantedEffects Id, the
stat), added to the fifteen `AuraCatalog.PerAuraEffectStats` has in code (`PerAuraEffectPctFor`, while the tables are
on): the heralds, Arctic Armour, Tempest Shield.

A number that is set in stone once rolled is "unscalable" (`ModScaling`): the game marks it per stat (Stats
`IsScalable`) and per line (Mods `IsUnscalable`), and the tables hold the same marks as rows the server reads
(`fx_stats.unscalable`, `fx_lines`), so a new unique's line can be marked or an existing one opened up by a row; a mod
does it while loaded with `ModScaling.Override`. Asked by everything that changes a line's number after it is rolled:
increased effect of modifiers, catalysts and enchantment magnitudes, and a bundle's `effect` (an unscalable number is
given whole at any effect above 0). Whatever rewrites an item later asks the same question.

`EffectRules.Add` takes rules in the tables' shape (`EffectRules.NewRule`: a mod is not compiled against the storage
layer) from a mod or module and loads them with the tables' (disposing the result takes them back): how a mod ships
rules of its own without a world.db update.

A plain number is granted to the calculator the game module already has (the stat graph); `fx_modifiers` records what
the number means so that a formula can later move to a table one quantity at a time, each measured. Nothing reads these
tables during play: they are loaded at startup into arrays by stat id, and an actor's rules are worked out when its
sources change, not per hit. `scripts/dev/fx_stats.py` writes the first sort of `fx_stats` (11,660 stats: 2,758 named
by code, 8,902 by nothing); later updates correct rows and never regenerate the file.

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
| `stats` | stats the actor has while a `worn` rule stands | `stats`: name to value; `percent` of them (a value) |
| `grant` | a bundle of stats for the actor or others, standing or timed | `bundle`, `to`, `roll`, `effect`, `radius`, `seconds` |

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

## Design decisions

- Simple rows live in world.db so packs and mods can add them; an effect kind that needs code lives in code.
- Several rules on one event each roll on their own, in a fixed order (`ord`).
- The default chain depth is 0: a triggered skill triggers no other. It is a value on the row, and the guard a switch.
- A rule whose condition the server does not model does not load, and says so in the log; it is never half-applied.
- Old code is kept until its row matches what it did, then reads 0 for the stat (`EffectRules.Unhandled`).
- "The character's own" excludes totems, traps and mines; brands, heralds and mirages count as the character's.
- In a party an area takes the stats of the player who opens the map (for the area carrier, not built yet).
- A stat that changes how an area is generated must be on the area before it is generated, so a worn area stat is
  read at map opening (`GameEvents.MapOpening`).
- An applied `pending_world` file is never edited: every later change to a row is a new file with an `UPDATE`.

The reverse-engineering behind the art and buff choices is in
[item-triggered skills](../re/item-triggered-skills.md).

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

## Minion hits

A minion's hit raises the hit event (`HitLanded`) with `Who` = `minion`, at the minion's own hit in `GolemCombat`
and at a plain owned minion's attack (`MinionHitTargetLocked`); explosions and other secondary minion damage do not. A rule runs for it only when its trigger says
`"who": "minion"`: `"any"` is the character and its totems, traps and mines, whose hits use the character's stats.
The rule's numbers still come from the player's stats (the `minion_...` line is the player's).

## Per attribute

`fx_per_attribute` (`stat`, `counts_as`, `attribute`, `every`, `scale`) holds stats that give another stat once for
every so many points of strength, dexterity or intelligence. `AttributeOffenceSources.Apply` adds them after its own
list, with complete groups of the final attribute, while the rules are on. A stat that belongs to its item (Stats
`IsLocal`) never reaches that sum, so a row for one does nothing.

## Aliases and `remove`

`fx_aliases` holds stats that are another stat under a second id, with no condition (`stat`, `counts_as`, `scale`).
`ItemStatAliases.Apply` adds them after its own list, only while the rules are on. A stat with a condition belongs in
`fx_modifiers` with `counts_as` instead. The part `remove` takes an ailment off the actor at once (`what`: bleeding,
poison, chill freeze, ignite, shock, corrupted blood, elemental, ailments; the value is seconds of protection after
it) with the removal a flask's own suffix uses. Curses and other debuffs are not removed by it.

## Flasks

A flask in the flask bar carries rules as a worn item does (source kind `item`, the flask's own local stat). Its
`flask_use` rules run when that flask is used; its standing (`active`) rules hold only while its effect runs, which is
"during Effect". `flask_use` is also a trigger for character stats ("when you use a Flask"), for every flask; the
condition `flask_kind` tells utility, life, mana and hybrid apart. The flask code's own list (`UtilityFlasks.DuringEffect`)
still gives the lines it names; the rules give the ones it never read. An effect that starts or ends has no stat of
its own, so the flask code asks for the rules to be configured again (`TellStatsUpdated`).

## Not yet

Real projectiles for the skills that are a volley (Bone Nova, Icicle Burst, Molten Burst, Starfall are one-hit
stand-ins), ground effects, the "with this weapon" and "socketed in this item" facts, the four elemental aegises, minion
and monster hits as events, areas and ground patches as carriers, and moving the charge rules over. For grants:
monsters carrying rules and granted skills of their own (a bundle is stats only), allies and minions as receivers,
a life or mana flask's recovery as an effect, a flask's "increased effect" on its rule lines, scarabs as givers, a line without a stat of its own as a rule's source (an aura line whose only stat is
the one it gives), and the debuff auras of `generic_buff_aura` templates (Crushed, Scorched, Chilled, Covered in Ash,
Malediction, Debilitated: the template's second list names the debuff).
