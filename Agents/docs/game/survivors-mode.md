# Survivors league (preview)

Survivors is a separate character-creation league, not a switch for an ordinary character.
Its parent is Void: event rewards cannot migrate into Standard.
Entering from a town or hideout goes to The Twilight Strand; the first hostile area starts the run automatically.
Stage changes carry the run, learned skills and granted equipment; a fresh login starts a fresh run.
Ordinary exits and waypoint travel are refused during a run, so they cannot bypass its stage gate or erase progress.
Carry is bound to Auth's destination ticket, not the character name; a failed transfer retains the current run.
Run progress is not saved across server restarts.

The three modules under `Modules/Survivors/` separate automatic skills, rewards and the run's lifecycle.
The existing `/survivors` diagnostics and restart controls are available only to characters in this league.
Starting a fresh run removes carried, worn and flask items; stopping a run removes its transient skills.
Ordinary leagues do not load these modules for their players.

## Run shape

The run is a 15-minute horde: 14 monsters alive at the start, 500 by the bell, refilled every 0.08 s by a quarter of
the gap (at most 8 a step) from five consecutive tiers of the area's roster; the early ramp is steep (about 290 alive
by the halfway bell).
Monster life and damage both grow +250% over the run, and monster hits on the player deal 60% of their damage
(Fortitude lowers it 1/1.2 a level), so standing still is not safe.
Every 12 s a ring of the area's ranged varieties (archers, wands, mortars) spawns at 22 cells and shoots; a closing
surround ring follows every 45 s: the player has to move, not just out-heal.
A stage boss arrives 15 s in and gates the way onward; after it, another boss rises every 60 s, rolling the reference's
ladder.
Stage changes pick a random area at the run clock's growing level (1 at the start, 20 by the bell), never a town
(towns have no monster packs); the pool keeps only areas whose packs resolve to at least three spawnable varieties, so
a stage never lands somewhere with nothing to fight, and the current area is skipped when another is available.

Run skills are every gem the client can draw: a projectile gem flies its own `Projectiles` row, an area gem (Ice Nova,
Firestorm and the like) casts a **nova pulse** around the player with the gem's own area hit, and a Herald pulses its
aura. Heralds and areas share the pulse pipeline; an area gem's pulse radius is the run's 48-cell base grown by its
rank and increased area. Melee area skills (Cyclone, Ground Slam, Cleave) are attacks and stay out of the pool.
A cast clip plays only while the player stands still: while moving it would replace the walk clip and read as strafing.
Every run starts with one **Small Life Flask** in the flask bar, full, refilled by kills like any flask, so the emptied
character still has recovery.
No monster kill drops items or gold, on either the melee or the projectile path; the ground is also swept every run
second.

## Native reward window

The level-up choice uses the client's quest-reward inventory, not a custom UI.
The window offers **six** choices: rank-ups, supports, blessings and (every third level) a unique, drawn from the
reference's weighted pool. A **new skill** waits until every learned skill that can take supports has all five linked;
the window offers supports first, so a skill grows complete before the bar gains another slot.
S→C `0xc0` must register inventory `0x600` before any `0xbf` change for it.
Reversing that order crashes the client with "Got an inventory set change for an inventory that isn't in the set".
S→C `0x87` opens the window; its ordinary C→S `0x88`/`0x8a` item id selects the reward.
S→C `0xbf` removes the offered ids and `0x84` with object 0 closes the dialogue.

The world clock stops for a choice.
Closing the window with a stray click or Escape accepts its first reward rather than leaving an invisible paused run.
Disconnecting or unloading the run module releases its pause and cancels its ticker.
An expired run cannot finish an old reward or launch another projectile into a replacement run.

## Skill presentation

Run skills use Actor ids from `0x0300`, outside the socketed-gem band and basic-action ids.
Their records carry one stored use and a cooldown duration; both are needed for the native cooldown sweep.
S→C `0x53` binds them to free slots from Q onward without saving them to the character's skill bar.
The `0x1ce` instant action and `0x2ba` single-use cooldown trailer update the sweep.
Manual input for a run skill is consumed: otherwise a client action can duplicate the server-created projectile.

Cast animation uses **`0x1b2` kind 0**, not `0x1d1`.
The latter cancels the player's action and locks movement for its duration.
The former replaces the rig clip without taking over the player's action.
A 3.29.3b Frida trace confirmed Fireball's `spell_projectile_unarmed` clip 386 reaching the body controller.
Fireball's projectile received impact clip 1 (`fireball_explode`) through the same handler.
Class/weapon clip indices come from `animation-contacts.json`; flight and impact clips come from `projectile-clips.json`.

The projectile is server-created (`ClientPredicted=false`); the instant action presents its cooldown, not its flight.
Fireball and Kinetic Blast additionally create their area effects.

Heralds are run-specific damage pulses, not reproductions of the ordinary Herald trigger mechanics.
Their aura and icon use the effect's ActiveSkills/BuffDefinitions row through `0x1c1`.
Hits use `0x1af` with the appropriate damage-type code and the Herald's own impact art at up to three targets:
Ash `flameherald_explode`, Ice `iceherald_explode`, Thunder `lightning_strike_herald`,
Agony `mortarimpact`, Purity `slam_impact`.
The effect object is removed after its clip ends.

## Reward equipment

The reward is rolled when the window is dealt; selecting it equips the exact item that was shown.
The item's level requirement is sent as 0, and the native attribute check reads the three
`virtual_*_for_requirements` stats while granted equipment is worn.
The server separately exempts granted item ids from inactive-equipment resolution.
Removing an id only from the inactive set is insufficient: its item must also join the active stat-source list.
Requirements resume when the run ends; a new run clears previous rewards.

## Limits

- No custom arena, cards or clock HUD; the normal skill bar and reward window are reused.
- Area gems pulse around the player; a gem with neither a projectile nor an area type (Arc, a curse) is still not offered.
- Caustic ground, Soulrend's debuff and poison inputs are not integrated into these automatic casts.
- Conditional unique mechanics depend on the existing stat and combat implementation, not merely catalogue presence.
- A complete fresh-character 15-minute balance run and broader per-gem native acceptance remain follow-up work.

`/survivors status`, `pick`, `test`, `link`, `matrix`, `rank`, `unique`, `uniques` and `curve` expose diagnostics.
`Game_SURVIVORS_TRACE=1` records cast inputs; `Game_SURVIVORS_MAX_ALIVE` and
`Game_SURVIVORS_STAGE_SECONDS` bound native verification runs.
