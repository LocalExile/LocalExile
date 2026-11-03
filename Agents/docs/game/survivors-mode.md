# Survivors league

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

The run is a seven-stage horde: 14 monsters alive at the start, 220 at the reference run's end, refilled every 0.12 s by
a sixth of the gap (at most 4 a step) from three consecutive tiers of the area's roster, exactly the reference's curve.
Monster life and damage both grow +250% over the run, and monster hits on the player deal 60% of their damage
(Fortitude lowers it 1/1.2 a level), so standing still is not safe.
Every 20 s a ring of the area's ranged varieties (archers, wands, mortars) spawns at 22 cells and shoots; a closing
surround ring follows halfway through each 135 s boss cycle: the player has to move, not just out-heal.
Each 2-minute stage runs its horde and its area's own champions (the lore boss 15 s in, another every 60 s); when the
stage's clock ends the horde stops spawning and a **gate boss** rises with its servants. Only its death opens the way
onward, and the seventh gate boss wins the run. The clock keeps running through the gate fight, so the reference's
horde curve caps rather than ending the run.
A run has **no entry protection**: the 60 s grace period is removed when the run starts and at every stage, so the
horde and the gate boss press from the first second.
Stage changes follow the run's fixed area list, one Act 1 area per stage (The Twilight Strand, The Coast, The Mud Flats,
…), so every stage has monsters; a town is never a stage.

### Gate bosses

Seven bosses, one per stage, each on a real model that fits the area (Hillock, Merveil, the Abyssal slug, Abberath,
the Weaver, Admiral Darnaw, the Incarnation of Fear), given its own name, damage type, move cycle and servants, and
scaled up through `base_actor_scale_+%`: the client's big boss bar follows the model's `BossHealthBar`.
The move repertoire is a **slam** around the boss (dodge out of its reach), a **burst** telegraphed under the player
(dodge out of the aimed circle before the contact), a **fan** or **ring** of the area's own projectiles, and a **blink**;
each kit cycles its own subset, summons its servants (capped at 12) and enrages below half life (faster moves, +20%
damage).
The boss's life follows the stage's average build, not the player's own: the reference boss ladder scaled from its old
105 s window to 120 s (`Game_SURVIVORS_BOSS_LIFE`, default 3, tunes the ladder). A stronger build kills it faster, a
weaker one slower. Its hits are scaled to the player's own pool so a missed hit removes about a seventh of effective
life; armour, evasion, block and resistance then buy the tanky build its extra hits instead of turning one into a
one-shot.
A scripted boss skips the ordinary monster AI (`SpawnedMonster.Scripted`) and its built-in stage scripts; the
controller drives every action and phase.

A run starts with **no run skill**: the opening window offers the first skill, and the next skill is offered once the
current one holds every support it can take, so the player builds the skill set rather than receiving a fixed gem.
**Resurrecting after a death restarts the run from the very beginning**: the opening area, an empty character and the
first-skill choice; a voluntary `/survivors stop` does not restart it.
The window's chat line names the skill each upgrade applies to (a support shows `Support -> Skill`), and `/survivors
status` prints the same labels.
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
The window offers **six** choices. A learned skill's **missing supports come first**: the window lists the supports it
does not have yet, so a link set can actually be completed (every offered skill can link at least five of the run's
eight supports; a gem that cannot is left out of the catalogue).
**Rank-ups** appear only once that skill holds every support it can take.
A **new skill** is capped by the stage — the first location allows one, the second two, up to five — and needs every
learned skill complete; after the fifth location only supports and ranks grow. Then the reference's weighted pool
(blessing ranks, new blessings, a unique every third level) fills any remaining slots.
The chat line that opens the window names the skill each choice applies to (`Added Cold Damage Support -> Fireball`),
and `/survivors status` prints the same labels.
S→C `0xc0` must register inventory `0x600` before any `0xbf` change for it.
Reversing that order crashes the client with "Got an inventory set change for an inventory that isn't in the set".
For the same reason a stage change waits while a reward window is open, and a pick that races the window's first packets
skips its `0xbf` clear instead of sending it to an unregistered inventory.
S→C `0x87` opens the window; its ordinary C→S `0x88`/`0x8a` item id selects the reward.
S→C `0xbf` removes the offered ids and `0x84` with object 0 closes the dialogue.

The world clock stops for a choice, and S→C `0x1d3` pauses the client's world with it (the Escape menu's own pause
message): the server already stops ordering monster walks, but only `0x1d3` stops the client walking each monster to
its last ordered cell. Closing the window (or the run ending) sends `0x1d3 00` and restarts the clock.
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
- A complete fresh-character seven-stage balance run and broader per-gem native acceptance remain follow-up work.

`/survivors status`, `pick`, `test`, `link`, `matrix`, `rank`, `unique`, `uniques` and `curve` expose diagnostics.
`Game_SURVIVORS_TRACE=1` records cast inputs; `Game_SURVIVORS_MAX_ALIVE` and
`Game_SURVIVORS_STAGE_SECONDS` bound native verification runs.
