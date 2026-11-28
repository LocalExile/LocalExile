# Effect rules: design

Where the effect rules are going. What is built, and how to add a rule, is in
[combat effect rules](combat-effect-rules.md). This page is the plan for the rest: every stat that does something when
something happens, for every actor, without changing what the current code does.

## What the game shows

Facts from the 3.29 client, its data and GGG's servers. Each shapes a choice below.

- **Stats are the one vocabulary.** Items, passives, gems, buffs, monster mods and map mods all add stat lists. A stat
  value is a sum of contributions tagged by source, so one source can be taken away alone.
- **No table says what a stat does.** No client table maps a stat to an event, a chance, a target or an action, and the
  client has no general effect language. The rule rows are our own layer.
- **Two small tables switch code on by stat.** `ApplyDamageFunctions` names 11 handlers, mostly on-hit, each enabled by
  one stat; GGG's code keeps the enabled ones in a list on each actor. `GameObjectTasksFromStats` attaches a named task
  when a stat changes, under conditions not yet understood; the "every 4 seconds" effects are its main gameplay rows.
- **A skill use has its own stats.** An overlay over the skill's stats holds trigger, Vaal and per-projectile bonuses,
  written before the hit is worked out. Some writes last the whole use and its child actions, are replayed on a rebuild
  and are never deduplicated; others are wiped at the next refresh.
- **Buffs carry stats.** A `BuffDefinitions` row lists the stats it grants and its condition stats (Onslaught gives
  `currently_has_onslaught`).
- **Every actor is the same kind of object.** Players, monsters, minions and totems share the stats, buffs, life and
  actor parts.
- **Trigger setup is data; the firing is not.** Most trigger supports are two granted effects: a source half with the
  trigger stat (`cast_linked_spells_on_attack_crit_%`) and a `Triggerable` half with the cooldown and stored uses in its
  own per-level row. Some, such as Cast when Damage Taken, are one half. Items use the same stats.
- **Effects go out in chain order.** GGG's servers send each effect as its own message in the frame it happens. A
  frame's messages are sorted by root cause (the attacker, a projectile, or a reserved key for input, expiry and
  creation), then by step along that cause's chain: the hit, its results on the target, then recovery. This held in
  every frame seen, in 3.29 and 3.30; a triggered skill and its effects sit between them (seen in 3.30).
- **Predicted rolls are seeded from identity**: the object id, the action sequence, the target, not one world stream.
  Evasion is the exception: a running counter on the defender. How the server seeds
  its own rolls, and its order of crit, ailment, on-hit and on-kill rolls inside one hit, is not visible. Those stay
  our documented choices.

## The design

GGG's shape is "a stat switches on code at a fixed point". Ours is the same, with the switch, its conditions and its
numbers in rows that are checked when the data loads.

### Sources

A rule names its stat and the scope it is read from:

- `actor`: the actor's own sums (passives, item lines, buffs).
- `used_skill`: the skill behind the event, with its supports. A rule may list `actor` and `used_skill` together to sum
  them, as the player hit charges do.
- `hand`: the hand that made the hit. `MainHandAlias` and `OffHandAlias` give the key; the event says which hand hit.
- `grant`: a skill or support an item grants (today's `skill` source kind).
- `buff`: one buff instance's own values (stacks, level). We put its granted stats in the owner's `actor` sums; how the
  game does it is not visible.

The compiler refuses a scope the event cannot supply. The `Semantics` column of `Stats` does not tell a chance from an
amount, so the row says how its value is used with `from` (`roll` for a chance, an amount, a duration in ms), as the
shipped rules do.

### Activation

The compiler lowers each rule to an entry keyed by event, actor kind, scope and stat. Each actor (player, monster,
minion, totem) keeps the entries whose stat is set, rebuilt in its stat build, and an event visits only those.
`MarksForHit` keeps a per-player list like this today, rebuilt when the worn items or the rules change. A debug switch
can check the set against a full scan.

### Events

Each existing call site (kill, hit, when hit, block, stun, skill use, flask use) makes one call where it does its work
today. `ChargeEvent` grows into this shared event, and the charge code becomes one of its readers.

- **Facts are copied at the site:** crit, melee, spell, hand, enemy rarity and ailments, the actor and its owner, and
  the values its rules read. Today `ChargeSession` rolls a queued hit at its tick with the actor's stats and weapon as
  they are then, so a change since the hit can leak in. Every rule in one event sees the actor as it was when the
  event started; what an earlier rule changes counts from the next event.
- **Owners:** the actor that deals a hit or kill owns the event. A rule names whose event it listens to (self, or my
  minion, totem or trap), so a player's on-kill line never fires for a minion's kill and a "when your minion kills"
  line does.
- **Blocked hits** skip the attacker's stat-granted on-hit rules, not the skill's own effects, and still run the
  defender's when-hit and block rules.
- Messages go out in chain order; work queued for the owner's tick keeps its place in it.

### Order and randomness

Each rule has one position in its event (`fx_rules.position`), and the compiler refuses two rules with the same one.
A rule that takes away what a later rule
grants must run first.

A migrated roll keeps its place and draws exactly when the old code drew: whether a failed condition, a 0% or 100%
chance, a blocked hit or a non-primary target takes a draw is set per family and covered by its seeded test. Draws stay
with the site's generator. A roll the client predicts moves to its identity seed in its own commit, with new expected
values for its tests.

### Conditions

A condition is a typed check. A rule's checks are ANDed; one check may hold a list that passes if any item passes, and
any check may be negated. This is the shape Path of Building uses. Checks read the event's facts; stats of the actor or
another actor, including buff condition stats; counters and thresholds ("5 or more poisons"); "recently", from a time
recorded when the event happens; equipment; and the skill's types. A row with an unknown check fails to load.

### Effects

1. **Stat contribution.** A conditional actor stat ("more damage while X") is added in the stat build while X holds. A
   use write (a per-repeat support bonus, trigger damage) is a list of stat values kept for the whole use, replayed on a
   rebuild, or for one repeat. Neither is an after-hit action.
2. **Event action.** Charge, buff, debuff, ailment, recover, remove, damage, flask charge, trigger skill. Each reuses
   the mechanic's existing code and decides nothing the old code did not.
3. **Task.** "Every N seconds" effects run a timer while their stat is set.
4. **Handler.** One-off mechanics (Headhunter, the kill explosions) stay C# behind a name; a row switches them on and
   gives their numbers.

A mod adds an action, a check or a handler in C# and registers it with the compiler, so its rows are checked like ours.

### Triggered skills

One pipeline serves gems and items, reading from data where the data has it:

- the source stat and the event it waits for;
- eligibility from `Triggerable` and `spell_uncastable_if_triggerable`. `TriggerOnlyReason` (`TriggerSkills.cs`) is
  about 30 hand-written cases today; keep only the ones the data does not cover;
- the cooldown and stored uses from the triggered half's per-level row;
- a skill with two trigger sources is not triggered; the cost is paid when it fires; the cooldown recovers on server
  frames, except with several stored uses;
- the trigger's damage stats, written as a use write.

A triggered skill's hits trigger nothing unless the data allows it; where the data says nothing, the rule says what we
chose.

### Actors

Rules name their actor kind: player, minion, totem, trap or mine, monster. One rule serves all of them where the stat
means the same thing, as the hit charges (`{"who":"monster"}`) and minion kill charges (`{"who":"minion"}`) do now.

## Safety and tests

- `Game_EFFECT_RULES=0`, read at startup, runs a family's old C# until that code is deleted. A family with no old code
  (the block trigger) is off with it.
- Each family gets a seeded test that compares the awards and the next random draw with the old code, as
  `ChargeWorldRuleTests` does. Old code is deleted only after that.
- Rows load once per world snapshot. A bad row fails the reload and the previous snapshot stays.
- Per-event cost gets a guard like the stat-sources guard in [performance](performance.md). An actor with no active
  rule pays one check.
- An `fx_stats` table, as drafted in [#1375](https://github.com/LocalExile/LocalExile/pull/1375), lists every stat and
  what handles it. It comes over in the first step, and each step after updates it.

## Order of work

Each step is its own pull request with its tests.

1. One position per rule; every charge rule rolled on an event as rows; `fx_stats`. Done, except the per-actor active
   sets: rules still add the used skill's stats through a lookup, so the sets wait for a `used_skill` scope (step 5),
   and the event's stats and weapon are still read when the charge queue rolls. Charges a cursed enemy or a nearby ally
   grants, and those taken from the player, stay C# until step 2.
2. Recover, debuff, ailment and remove actions, with the on-kill and on-block recovery.
3. The triggered-skill pipeline, one source family at a time.
4. Minions, monsters and totems as actors.
5. Stat contributions, then tasks.
6. Flasks, areas and ground patches as sources.
