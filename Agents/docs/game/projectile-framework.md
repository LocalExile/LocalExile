# Projectile framework

`src/GameServer.Core/Projectiles/` supplies the single chronological simulator.
`src/GameServer.Gateway/Projectiles/` binds player skills, combat state and native presentation to it.
Ordinary bows and catalog-recognized projectile gems use these same production entry points.
Catalog fallback provides basic straight flight; a registered definition supplies special behavior.
Missing metadata or an unsupported damage plan is refused before action costs.

## Simulation and combat

Time uses 120000 integer quanta per second, with 30 Hz boundaries and exact chronological contacts
inside each boundary. Actor generations distinguish removed/replaced targets. The host serializes gameplay
under XP-before-Monsters locking and advances pending projectile history before replacing actor motion or life.
Hit, charge, Rage, reward and boss-stage changes commit synchronously. An ordered outbox presents the committed
results without waiting for network output inside the combat lock. Pause freezes the game clock; area transfer,
death/retirement and connection closure invalidate the appropriate owner epochs and pending work.

Release captures position, offensive stats, speed and critical inputs. Evasion, block, resistance, impale state,
conditional critical restrictions and the actual caster's spell-damage prohibition resolve at contact.
Gem release always retains separate direct and area damage packets, including for initially direct-only roots
whose children or release specialization introduce areas. Area increases and area-only support multipliers
apply to the area packet once; stage multipliers and projectile/form/return multipliers compose at impact.
Non-projectile damage callers retain their own classification.

## Continuation and hit admission

Continuation priority is split, pierce, fork, chain, then return. Prohibitions override the corresponding grant.
Finite pierce consumes one grant per admitted contact, then hands off to the next available continuation.
Boolean fork sources establish one base generation; explicit additional-fork counts add generations.
Raw and resolved aliases are combined without counting the same source twice. Count conversions declare
their consumer so an extra projectile cannot expand both primary emission and a secondary stage.
A declared non-primary consumer retains quantity when a competing split conversion is prohibited by the
profile or modifiers. Competing viable conversions remain an error; primary count conversion is unchanged.

The common player defaults share direct-hit admission by emission, separately for outbound and return phases.
Denied siblings pass through that victim. An evaded contact keeps flying without consuming continuation.
Independent area events have independent admission and may overlap any geometrically covered target.
Core contact tests combine projectile and actor radii; area tests combine area and actor radii. The Gateway
adapter currently passes zero actor radii, so area coverage checks target centers, including for large monsters.
Monster catalog `ObjectSize` feeds movement blocking but has not been calibrated as a circular combat radius.
A terminal collision victim shares direct admission with its terminal area, receiving one area-classified hit
instead of a direct hit plus another blast. An evaded terminal victim does not end the flight.

Contact areas run at admitted impacts while continuation remains possible. Terminal areas and end emissions
wait until no continuation, including return, remains. Recipes select terminal reasons and can exclude a
contact victim when the mechanic requires it. Offset areas support one sample per angular sector, a minimum
placement distance and skipped terrain-obstructed sectors. Radius, placement radius and damage multiplier
are separate inputs. Area recipes have a first-slot `Delay` and inter-slot `Interval` in simulation quanta;
zero defaults remain synchronous. Centers and trigger flight state are captured before continuation, while
actors and defenses resolve at each detonation. Pending areas survive their parent flight, retain admission
history, pause with simulation time and clear on teardown. Terrain-blocked centres are not resampled and do not
consume time slots: surviving areas start at `Delay` and follow consecutively at `Interval` spacing.
Sector sampling and recipe timing values do not establish official random distributions or measured timing.

`EndAreaOnReturnTurnaround` lets a recipe emit an allowed endpoint area before a successful return starts.
It respects the selected end reasons and leaves child emissions deferred; the default keeps terminal areas deferred.

Return preserves remaining split/pierce/fork/chain counts and the original lifetime. An expired projectile
cannot start another flight. Returning children retain their phase and cannot return a second time.
`returning_projectiles_always_pierce` explicitly grants unlimited return piercing; ordinary return does not.
Terrain collision remains enabled unless the profile requests otherwise. Common player recipes exclude
the turnaround victim and continue 150 cells past the captured caster position. The fixed overshoot and
remaining-count implementation are approximations; moving-anchor tracking and exact engine equivalence
are not established. Phase-specific hit rules, contact caps, speed and terrain policy remain configurable.
`OutboundHitLimit` is an independent cap on actor attempts while traveling to the endpoint; finite pierce
must use `Pierce`, never that cap.

## Skill definitions and presentation

`ProjectileSkillParameters` owns profile resolution and optional native panel-stat projection.
`ProjectileSkillDefinition` combines those parameters with metadata, optional terrain admission, windup
cancellation, release specialization and presentation.
Paid attacks normally survive Move; spells and definitions with release-time resources retain action cancellation.
Cooldown tickets, death, recovery and
epoch restrictions still apply. Specialization runs once in the simulation commit and may provide frozen
per-initial-projectile payloads; continuation children inherit their parent's payload.
Accepted hit recovery permanently cancels unreleased paid attacks, even after recovery expires;
already released flights continue independently.

Gem skill records resolve the same profile for native projectile-count, pierce, chain, fork, split and return inputs.
Prohibitions override displayed continuation grants; unlimited pierce uses the native all-targets flag.
Arrow pierce display aliases are cleared after their applicable contributions enter the combined profile budget,
so they cannot shadow the generic total or the cannot-pierce row.
Final display values modify only the outgoing skill record, never raw supported stats or later release calculations.
Parameters may project specialized stage counts through `PanelStats` after the common projection.
Return descriptions distinguish guaranteed return from a percentage chance; prohibited return suppresses both
and the return-pierce description. Cannot-pierce also suppresses the return-pierce description.
Catalog projectile skills without native projectile metadata still resolve known generic parameters and skill overrides.
This stat-only path never supplies executable metadata or bypasses release admission; special mechanics remain
dependent on skill coverage. An invalid parameter recipe omits projection without blocking the remaining skill list.
Native description templates choose the wording and visibility.

`ProjectilePresentation` supplies flight mesh/clip, second-form clip and event effects. Non-gem sources may
supply presentation directly through `ProjectileOffense`. The common host owns object lifecycle, terrain
height, arcs, redirects and effect cleanup. Core actor/team contracts are source-neutral; the current Gateway
damage adapter targets monsters. Live hostile projectile adapters remain unimplemented.
Area event effects use the actual event radius and the selected art's exported radius to set uniform native scale.
Both callback and payload effects use this conversion; art without a known radius keeps its native scale.
Endpoint logs include the stop reason, position and projectile-grid state; area logs include art, radius and scale.
Straight flights use `(captured terrain height ?? 0) + Game_PROJECTILE_HEIGHT` as their fixed height marker.
The integer setting defaults to 5 steps (39.0625 world units upward), including for an invalid setting.
Creation, glide and stop retain the same signed height. Missing captures use the bare offset as a provisional
absolute height, preserving the main flight contract; this is not client-sampled terrain plus a relative offset.
Lobbed arcs use the configured lift for the initial start when both ground endpoints are known; later bounces
start at ground level. Without both endpoints they retain client terrain sampling instead of a guessed parabola.
Independent area effects still follow client terrain. Ground clipping and platform/doodad surfaces remain unverified.

Automated checks exercise the production runtime and session, including held-output gameplay, moving
targets, walls, evasion, continuation combinations, real item stat sources and lifetime boundaries.
Native animation alignment, platform/doodad surfaces and visual equivalence still require client validation.
Actor motion uses integer walk estimates and shared collision radii; basic catalog flight does not establish
complete behavior for every skill.

See [projectile skills](projectile-skills.md) for registered behavior, evidence and client checks.
