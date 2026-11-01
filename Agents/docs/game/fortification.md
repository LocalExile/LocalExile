# Fortification

Fortification is temporary protection built by qualifying melee hits.
Each effective stack gives 1% less damage from hits, after armour, resistances and additional damage reduction.
It does not reduce damage over time.
The base maximum is 20 and each grant lasts six seconds on the player's game clock.
The hit-gain interval is shared across targets and skills: one grant per 0.1 seconds.

## Evidence and server model

The 3.29.3b extracted `Stats`, `PassiveSkills` and `BuffDefinitions` tables identify the sources and display fields.
`Character.ot` supplies the base maximum, duration and inherent hit reduction.
[GGG's 3.16 notes](https://www.pathofexile.com/forum/view-thread/3186390) describe threshold-based generation,
rarity bonuses, the stack limit and shared interval.
The base duration changed to six seconds in 3.19.
The exact official numerical generation algorithm has not been recovered.

**LocalExile model v1** deliberately supplies that missing algorithm:

```text
floor(20 * sqrt(rawHit / threshold) * rarityFactor * max(0, 1 + increasedGain / 100))
```

`rawHit` is the accepted melee hit after its critical multiplier and before the target's mitigation.
Stored Impale damage is excluded.
The direct packet rolls once; the stored reflected contribution is floored separately before it is added.
This prevents a fractional reflected amount from shifting the qualifying direct hit across a stack boundary.
`threshold` defaults to the target's maximum Life; specialised monster ailment thresholds are not loaded here.
The rarity factors are Normal 1, Magic 1.5, Rare 2 and Unique 3.
The result is capped per hit at the player's current maximum.
These constants and this curve are LocalExile choices, **not the GGG formula**.
Exact rational squared comparisons avoid square-root rounding differences at integer boundaries.
A zero result consumes no gain interval under this model.

Each grant expires independently; excess stacks remain stored while the displayed count is capped.
This implementation does not impose an unverified instance-count limit.
An area transfer preserves remaining lifetimes and the remaining gain interval, frozen during loading.
These lifetime and transfer choices are explicit server policies.
Death and disconnect clear the connection-owned state; no temporary stacks are saved to the character database.

## Production consumers

`FortificationSession` owns the state and integrates stack-seconds across every expiry and maximum change.
The accepted melee damage commit grants stacks under the combat lock, before notifications can yield.
Default bows, wands, projectiles, spells and minions do not grant through this path.
Blocked, missed, stale, paused, closed and dead-owner contacts do not grant.
Supported area attacks check their actual `Melee` skill type and share the same gain interval.
Existing limitations of the server's supported-attack accuracy and skill shapes are unchanged.

The effective count supplies `attack_speed_+%_per_fortification` and
`life_regeneration_per_minute_%_per_fortification` once.
For example, 20 stacks with values 1 and 12 add 20% increased attack speed and 4% maximum Life per second.
Thirty stacks add 30% increased attack speed and 6% maximum Life per second.
Regeneration uses exact elapsed stack-seconds rather than a cached rate continuing past expiry.
Its recovery now receives the same ordinary Life regeneration/recovery multipliers and prohibitions as the panel.
Changing a modifier settles the old rate first; the new factor applies only to subsequent stack-seconds.
Automated checks cover modifier changes, prohibition, signed developer sources and stopping at the existing expiry.
An explicit final regeneration developer override replaces the derived regeneration source.
Removing the generation source does not remove stacks already gained; their remaining lifetimes still run.

Monster hits and the `/hit`, `/spellhit` and `/blockhit` testing routes apply the same protection before Energy Shield.
The remaining damage is floored after applying the effective stack factor.
This work implements the hit-generated sources and these two per-stack consumers.
Other conditional Fortification modifiers, permanent Champion grants and specialised grants remain separate work.

## Native presentation and verification

BuffDefinitions row 1943 is `fortification`.
The normal S→C `0x1c1` buff carries stack count and time to the next independent expiry; `0x1c2` removes it.
The character receives stat keys 15038 (uncapped count), 15044 (effective count) and 15040 (maximum).
Buffs, current stats and Actor skill records are constructed together inside the send gate.
Fortification's own buff, stats and skill refresh uses one game-clock snapshot.
Other full stat/Actor updates, including charge and equipment changes, are constructed inside the send gate.
Immutable pools retain the Fortification revision used by their actual source snapshot.
A queued update rechecks death, pause and area ownership before it can write.

Automated checks cover the generation curve, interval, overflow, expiry, exact regeneration, source changes,
real default-attack dispatch, supported melee/spell separation and send-gate invalidation.
Twelve native checks confirmed the installed `fortification` buff, two-stack raw-hit generation,
expiry, caps of 20 and 30, attack-speed changes and an incoming 10-damage hit reducing actual player Life by 8.
A 57-Life character recovered at the expected 6% per second with 30 stacks, within two Life of the elapsed oracle.
Recovery stopped after expiry, area carry preserved the temporary stacks, and death/relog cleared them.
These checks used a fresh unascended Witch and raw stat inputs, not a Phrecia tree allocation.
The engine-neutral stat gate also has automated regressions for queued refreshes after expiry.
Native proof checks the Buff component's installation; it does not assert pixel-level artwork parity.
