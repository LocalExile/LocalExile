# Steel Willed: armour against one hit

## Primary inputs and evidence boundary

3.29.3b `PassiveSkills` graph node 2103, Steel Willed, supplies 10 for Stats key 20680:
`chance_to_double_armour_effect_on_hit_%_per_enemy_hit_taken_recently_capped`.
Stats key 4534 is `number_of_times_have_been_hit_by_enemy_recently`.
ReminderText row 31 defines Recently as four seconds.
The separate generic input is key 8404, `chance_to_double_armour_effect_on_hit_%`.
The derived output is key 12268, `virtual_chance_to_double_armour_effect_on_hit_%`.
The absent names `defend_with_%_more_armour` and `chance_to_defend_with_double_armour` are not inputs.

Parent task `localexile-steel-willed-hit-armour-boundary-effb-r1` inspected the original `Game.exe`.
Its recorded SHA-256 begins `78a0e227`; this implementation uses that research without another native run.
Virtual resolver `0x1428b7310` reads key 20680 at `0x1428b7414`, counter 4534 at `0x1428b74ab`,
and multiplies at `0x1428b7522`.
It caps that contribution at 30 at `0x1428b77e4..0x1428b77f6`.
It separately reads generic key 8404 at `0x1428b77a3` and adds it at `0x1428b77ff`.
The 30% cap applies to the per-contact contribution, before adding the generic chance.
See the [address index](../protocol/address-index.md#skills-11-skillsmd).

Path of Building commit `16de4b82d57f1c0de6eb40f37143c32d4da36a02` corroborates the stat interpretation:

- [Stat description line 132809][description] describes the per-hit chance and cap.
- [ModParser line 2455][parser] maps it to double-armour chance with a multiplier and limit.
- [ConfigOptions line 1378][config] supplies an externally configured recent-hit count.

[description]:
https://github.com/PathOfBuildingCommunity/PathOfBuilding/blob/16de4b82/src/Data/StatDescriptions/stat_descriptions.lua
[parser]: https://github.com/PathOfBuildingCommunity/PathOfBuilding/blob/16de4b82/src/Modules/ModParser.lua#L2455
[config]: https://github.com/PathOfBuildingCommunity/PathOfBuilding/blob/16de4b82/src/Modules/ConfigOptions.lua#L1378

These inputs do not prove official hit-event sequencing, blocked-hit handling or temporary-state lifecycle.
Those choices below are explicitly LocalExile model v1.
Suppression transfer keys 17878/15079, class admission, graph changes and other Behemoth mechanics remain separate.

## LocalExile model v1

Each accepted incoming enemy attack or spell contact records one game-clock timestamp, even if blocked or reduced
to zero damage by immunity, suppression, mitigation or absorption.
Repeated contacts from the same enemy count separately.
Misses, evasion, rejected contacts, outgoing attacks, self/environmental hits and damage over time add no timestamp.
The contact history exists even when neither armour modifier is present.
Counting and benefiting are independent: another armour-mitigated hit can use existing chance without counting itself.

Resolve the hit using previously committed contacts, then record the current contact in the same `Monsters` lock.
Capture that membership immediately at the sampled contact time, before any stat reader can prune a later boundary.
Source construction may advance past an expiry without changing the captured membership used for this hit's decision.
Earlier commits at the same timestamp count.
The per-contact input of 10 produces successive isolated chances 0%, 10%, 20%, 30%, 30% before the generic input.
A timestamp is active only while its age is strictly less than four game-clock seconds.
It expires at exactly four seconds; pausing stops that clock.

The chance uses wide signed arithmetic:

```text
chance = clamp(generic + min(30, activeContactCount * perContactInput), 0, 100)
```

For an unblocked hit with positive non-immune armour-mitigated damage and positive armour/chance, draw `Next(100)` once.
Physical damage and chaos damage with `base_armour_applies_to_chaos_damage` enabled are relevant components.
A draw strictly below the chance doubles that hit's armour; 29 succeeds at 30%, and 30 fails.
Blocked, irrelevant, zero-armour and zero-chance hits consume no armour draw.
The feature-absent monster path preserves entropy, damage-component and block draw order.
One immutable decision supplies every relevant component and the hit's diagnostic label; diagnostics never reroll.
Effective armour clamps negative base armour to zero and saturates doubling at signed `int.MaxValue`.
`Pools.Armour`, the permanent rating and Steel Willed's outgoing attack bonus never receive the doubled value.

## Connection lifecycle and presentation

One area connection owns its timestamp queue; it has no ticket store or save representation.
New area entry and relog start empty, including reentry to the same retained instance.
A failed transfer keeps the current history, subject to elapsed game time.
Beginning a transfer invalidates pending monster swings even when the transfer later fails.
Successful transfer clears the departing history and carries no contacts to the destination.
Death clears history in the lethal commit before notification awaits, and resurrection invalidates pending swings again.
Both connection shutdown paths clear it before awaiting shutdown work.
An older death notification cannot clear a resurrected player's newer contacts.

The AI captures each swing's incoming-hit epoch when collecting the attack under the combat lock.
It carries that admission token through movement/boss-bar sends before invoking the delayed swing.
Its initial action echo rechecks that epoch, owner admission and the same hostile living monster inside the send gate.
A discarded echo ends the swing before its contact delay; pause alone preserves an already accepted attack echo.
At contact it checks closed/leaving/dead owner, epoch, current monster identity, hostility, life, range and evasion.
Pause is checked again inside the commit lock after the asynchronous pause wait.
An already queued swing waits for resume and cannot commit during a pause race.

The full character-stat snapshot always replaces manual values of authoritative counter 4534 and virtual chance 12268.
Their values come from current timestamps and resolved raw modifier sources together.
The existing 250 ms player tick sends changed values, including idle expiry to zero.
Packet construction samples state inside the existing shared send gate and combat lock, so queued full or partial
refreshes cannot publish a pre-expiry counter after a later expiry refresh.
Each full or partial player-stat send acknowledges the exact recent-count/chance pair encoded inside its send gate.
This includes Rage changes, dirty own-aura reconciliation and Mageblood flask-bar refreshes.
Mageblood constructs its full packet after entering the gate; Rage and aura writers retain their admission predicates.
Wire regressions cover return to a previously presented pair and queued full sends following an expiry refresh.
Fortification constructs its tail inside the aura wrapper callback, after any dirty full-stat prefix.
Acknowledgments follow wire order even when recent contacts expire during an intervening skill build.
Decoded-wire regressions also check the next idle correction and preserve Fortification's own captured time.
Pure stat construction never changes that acknowledgment, and skill-only updates do not acknowledge player stats.
The remembered presented tuple only avoids duplicate packets; it never supplies combat chance or expiry state.
No Buff record or parallel Fortification lifecycle engine is needed.

`/hit` and `/spellhit` are synthetic incoming enemy test contacts, rather than self-origin damage.
`/blockhit` is the same test contact with attack/spell block; it records once even on block.
All three check admission before RNG, charge events, pool mutation or contact recording.
Paused, leaving, closed and dead owners are refused.
The commands still require a positive input amount; immunity and block provide zero-damage contacts.
`/set_stat` accepts raw inputs 20680 and 8404 for development.
Manual values of 4534 or 12268 remain stored development inputs but never override combat or sent authoritative values.
The existing outgoing `LastEnemyHitAt` and delayed charge-event timestamps are not this counter's clock.

## Mitigation composition and verification

Current main's `PlayerHitMitigation.AfterResistance` receives the same explicit effective armour for physical and
enabled chaos components, after each type's resistance and rounding.
Additional physical reduction remains physical; elemental reduction never reduces chaos.
The chaos subtotal accumulates after armour reduction, before Fortification and ES absorption/bypass.
Source construction refreshes lazy aura/Fortification pools before reading final resistance, evasion, block or armour.
The accepted contact captures Fortification's existing game-clock snapshot and prior recent-contact membership.
Queued echoes and paused swings resolve final defences after their waits, inside the contact's combat lock.
At 500 armour, a 100 physical hit deals 50 normally or 33 with 1000 effective armour.
With 20% additional physical reduction those results are 30 and 13.
Automated parsed 0x15 routes exercise all three development commands, actual monster swings, mixed MonsterMod packets,
strict chance/time boundaries, signed limits, source changes, rejection, pause, delayed work, death and presentation.
They also cover repeated same-enemy contacts, Fortification/ES composition and feature-absent RNG identity.
Current-main regressions combine physical/chaos packets with Endurance Charges, resistance, Fortification and ES.
Parsed chaos spell contacts retain suppression after Fortification; chaos still bypasses ordinary ES.
Queued swings cover source zero/removal, recent expiry, changed rating/chaos flag and replaced final defences.
Lazy own-aura suspension is exercised through both actual MonsterSwing and parsed development contact paths.
Raw pool rebuilds preserve main's Strength-mana source restoration and authoritative recent stat acknowledgments.
Production game-data paths remain `data/`, and Gateway tests use the shared `TestEnvironment` initializer.

The former PR292 seam is integrated on base `79cc0337b331b69e49b3c35ea3e34c67e7d547ce`.
A 100 physical + 100 chaos hit at armour 500 with chaos armour enabled gives 100 ordinarily or 66 doubled.
With 20 Fortification those become 80 and 52.
The existing shared helper retains ordinary pool armour when no per-hit override is supplied.
The suppression-to-armour chance transfer remains separate work.

The following eighteen native checks are historical evidence recorded for original head `777b292`.
They used a fresh unascended Witch with 500 armour from a synthetic Plate Vest.
A 100 physical hit removed 50 Life normally or 33 when the generic input guaranteed doubled armour.
The physical spell-hit route shared that result; a fire hit retained its own mitigation.
Permanent armour stayed 500 and Steel Willed's fixed outgoing chaos hit stayed 102 in native stats and target Life.
Four contacts produced the capped 30% contribution; adding 20% generic chance produced 50%, not a 30% total cap.
A manual counter value could not replace the authoritative count.
A pause exceeding four wall seconds preserved contacts; idle expiry then published the remaining generic chance.
The full-refresh regression was checked natively: after publishing two contacts, the first expiry corrected it to one.
A real level-68 Zombie contact used the same temporary rating of 1000 and the expected native player Life loss.
Lethal damage, resurrection, area change and relog cleared the connection history as documented.
These raw-input checks establish the implemented LocalExile model, not the official server's event sequencing.
Mixed packets, block/zero contacts, exact roll boundaries, stale AI and queued sends have automated coverage.
The ordered #290 / #294 / #296 / #300 composition at `c9cd1864` passes 28 fresh native controls.
Final attributes and converted mana stay active on one new unascended Marauder.
An incoming 100 physical removes 67 Life with permanent Armour 251, or 50 when doubling is guaranteed.
The permanent native rating remains 251; history reports one then two accepted contacts and later clears.
One accepted Fire1 with raw per-contact input 40 and generic 20 gives count 1 / chance 50: cap 30 applies first.
A full generic stat refresh followed by idle expiry clears the counter and leaves generic chance 20.
Four-simultaneous-contact timing is automated coverage, not fresh native evidence for this head.
A prior trial accepted 26 controls but timed out waiting for that four-contact witness; its exact cause is unproven.
Its bundle and result remain preserved; the narrowed cap fixture changes no game clock, deadline or production rule.
Fortification/aura/Rage/Mageblood deadline ordering has decoded-wire regressions, not a forced native race check.
Gateway/Core/Storage/Host hashes and Git versions, listener DLL and working directory matched before entry.
Native Behemoth allocation remains separate work.
