# Antiquarian completion

The delivery unit is the selected Antiquarian class, with legal connected eight-point builds and gameplay consumers.
The implementation branch is `codex/antiquarian-completion`, based on main `fc5f4b17`, tracked in issue #98.
All nine notables and nine small nodes passed whole-class native acceptance in three legal builds.
Behemoth is delivered separately in PR #328; its Rampage/Vaal engines are not imported here.

## Admission and client data

Set `Game_VERIFIED_ANTIQUARIAN=1` for the Gateway and choose `/ascendancy phrecia Antiquarian` on a Marauder.
The explicit opt-in requires the already installed compatible 3.29.3b graph.
Admission reads both PSG files directly from `Client/Content.ggpk` using the normal Node extraction decoder.
It checks ordinary SHA-256 `82D82CD3CD2DAD81606AF0DA9D18FCD53B2E92CF006550947D65A4F7E102DC32`
and combined alternate SHA-256 `F2766B5A4618CDBE7E2993973B4D653F155F71BDFE0E5D063A425196CC93D638`.
Missing/different bytes or an unavailable decoder refuse before changing the save.
There is no JSON-only bypass and no archive-writing code.
Other Phrecia classes retain the default gate; ordinary characters do not invoke this preflight.
Compatible graph installation remains separately reviewed maintenance; native Labyrinth acquisition remains #151.
Read-only graph extraction manifest v4 accepts either the original or the exact maintained graph.

## Actual class nodes

Use Phrecia graph membership, not the shared `AscendancyKey`, which also identifies ordinary Chieftain nodes.
All nine small nodes grant 10 Strength and 10% increased global defences.

| Node | Effect |
|---|---|
| 24105 Kalguuran Armour | Half of body armour's local Armour/Evasion becomes base Ward |
| 18893 Starlight Reflector | +100% Ward restoration recovery and +100% global defences |
| 34762 Runic Tablet | 20-second Adrenaline on an actual Ward break |
| 8636 Goliath Knuckle | +200 Strength; Intelligence becomes zero |
| 27339 Whaletooth Bangle | +1% skill duration per complete ten final Strength |
| 43431 Turtle Shell | +1000% stun recovery; regenerate 8% Life over one second when stunned |
| 36978 Lioneye's Hair | Always hit; 50% reduced Dexterity |
| 55407 Lioneye's Bow | Far Shot on projectile attacks |
| 45368 Lioneye's Shield | Equipped shield base block becomes 60%; -100 physical ranged attack damage taken |

Three builds cover every notable and small node without exceeding the eight-point budget:

| Build | Allocated nodes |
|---|---|
| Ward | 23940, 24105, 48353, 18893, 34223, 34762, 62851, 8636 |
| Strength and stun | 62851, 8636, 40664, 27339, 61085, 43431, 34708, 36978 |
| Lioneye | 34708, 36978, 5913, 55407, 31396, 45368, 23940, 24105 |

Production mutation tests cover allocation, paid full refund costing 40 points, reallocation and durable reload.

## Ward and Adrenaline

Body conversion uses the existing minimum-base local defence model, including local modifiers and quality.
Keep half points until the final global rating; conversion does not consume helmet, gloves, boots or shield defences.
Remaining Armour/Evasion use their ordinary increases; converted Ward uses Ward/global-defence increases once.
Ward item bases/local modifiers and the existing Iron Flask Ward bonuses feed the same authoritative pool.
Successful paid flask use also consumes the actual `local_flask_restore_ward` flag for immediate restoration.
Stat refreshes and refused uses never restore Ward, and restoration never grants Adrenaline by itself.

After block and mitigation, one hit consumes up to the available Ward before Energy Shield.
Any positive absorption breaks ordinary Ward, even if it absorbs the whole hit.
Broken Ward restores fully after two game seconds, divided by its restoration recovery multiplier.
Starlight Reflector therefore makes the normal delay one second.
Ward does not absorb damage over time; bypass and unbreakable Ward remain explicit stat controls.
Mixed hits consume the non-chaos portion first so remaining chaos still bypasses Energy Shield.
The actual native Ward/broken stats and broken-Ward buff are sent from committed state at the send gate.

Only a real unbroken-to-broken transition grants Runic Tablet's Adrenaline.
BuffTemplates row 503 / BuffDefinitions row 1090 supplies its six effects:
100% increased damage, 25% increased attack speed, cast speed and movement, and 10% physical damage reduction.
Repeated breaks while Adrenaline is active do not refresh its deadline.
Source removal and death cancel it; expiry recomputes pools, movement, skills and native stats.

## Strength, duration and stun

Goliath's zero Intelligence wins after all flat/increased attribute contributions.
Equipment activation, gem requirements, mana, ES scaling and native requirement attributes use that same final zero.
Lioneye's reduced Dexterity shares the normal attribute calculation before requirements, evasion and accuracy.
Whaletooth's derived duration is added once from complete groups of final Strength to a fresh stat snapshot.
It reaches supported owner skill-duration consumers; it does not change a minion's own skill duration.
The prerequisite implementation is composed from PR #323 head `8b681aef`.
Enduring Cry now supplies a concrete owner-duration consumer alongside its existing charge generation.
The target stat set gives 2500 ms and 120% Life/min per five Power, capped at 25 Power by its stat description.
Whaletooth and compatible duration supports scale that recovery interval, retaining its rate.
Unrepresentable intervals are bounded to one day; negative duration factors produce no recovery interval.
The native recovery buff, exact resource integral, death cleanup and transfer use the same paid skill snapshot.

Turtle Shell runs only after the existing supported physical melee stun is accepted.
The common stat projection already maps base stun recovery to the final recovery stat; never add it twice.
Recovery uses the existing rounded native TakeHit interval and invalidates pending player actions.
LocalExile v1 uses one one-second recovery interval; another accepted stun refreshes it without stacking rates.
The resource writer integrates exact elapsed game time and consumes each earned fraction once.
Source removal stops future recovery, preserving only already earned recovery; death clears all debt.
The native regeneration projection is excluded from the writer's baseline to avoid double recovery.
Temporary recovery changes also send authoritative Life, correcting native prediction overshoot at expiry.
Unsupported global stun thresholds/immunities and non-melee stun engines retain their existing boundaries.

## Projectile and shield consumers

Always-hit bypasses the accuracy roll on supported ordinary melee and attack-projectile paths, retaining target block.
Projectile flags are captured at release, consistent with the offensive snapshot; melee reads accepted contact state.
Far Shot uses cumulative projectile travel rather than the owner's current location.
The pinned PoB `16de4b8` CalcOffence ramp is 0.8 through distance 10, 1 at 25 and 1.6 at 70 and beyond.
Interpolation is linear in the existing server combat-distance units.
It applies to attack projectiles before critical/mitigation/impale, never spells, melee or damage over time.
Fork/split continuations retain the runtime's existing travelled distance.
Unknown zero-size area bounds no longer erase projectiles when a walk capture is absent.

Only the active usable shield supplies the replacement base block; no shield gives no replacement.
Additional block modifiers and the normal cap remain in their existing stages.
The ranged flat physical reduction follows per-type armour/reduction and precedes Fortification and Ward.
It never affects elemental components, melee, spells or damage over time and cannot make damage negative.
Monster attacks classify ranged eligibility from skill metadata and actual Bow/Wand item-visual class references.
Generic monster Melee has both Projectile and Melee tags; Projectile alone must never make a melee weapon ranged.
The existing monster contact engine is retained; this does not add global enemy projectile flight/parity.
`/hit physical 150 ranged` exercises the same bounded incoming ranged classification for diagnostics.

## Lifecycle and acceptance

All deadlines use the connection's pausable game clock.
Successful ticket transfer carries remaining Ward delay, Adrenaline and earned/pending stun recovery once.
Destination loading freezes the remaining durations; old-area actors and pending attacks are not carried.
Relog and Gateway restart clear transient effects while retaining the class and paid passive progress.

### Native evidence

The single 3.29.3b client passed 37 checks through the documented native control bridge, without desktop input.
Each run created its own synthetic Marauder and verified the actual selected Phrecia identity, graph and saved nodes.
Packed class 193 and graph stat 21146 confirmed Antiquarian rather than normal Chieftain.
All eight-point allocations were connected, and full refunds spent 40 refund points.

| Build | Checks and observed behavior | Evidence directory |
|---|---|---|
| Ward | 12: Plate Vest 19 becomes 22 Ward/22 Armour after conversion and scaling; chaos hit breaks Ward; all six Adrenaline stats; one-second restoration; 21 wall seconds paused; paid source refund/reallocation; remaining-time area carry/expiry; death/resurrection; paid Iron Flask restores 502 Ward for 40 charges; full refund | `logs/native-ward-20261028-170649/` |
| Strength and stun | 11: final Strength 272, Intelligence 0, Dexterity 7; real level-8 Fireball requirement refusal; actual Hungry Corpse melee stun and 8% recovery; paid Turtle refund/reallocation; paid Enduring Cry heals 251 over 3.175 seconds versus 198 over 2.5 seconds without Whaletooth; ten actual always-hit contacts and miss control after refund; relog; full refund | `logs/native-strength-20261028-164056/` |
| Lioneye | 14: real shield base block 60%; physical ranged Life loss 33 versus melee/spell 133; paid shield refund and shield removal; real bow arrows deal 80 near/160 far versus 100 without Far Shot; paid full refund; new eight-point Ward build survives Gateway process restart with Adrenaline cleared; ordinary Juggernaut restores Default graph | `logs/native-lioneye-20261028-170126/` |

Ward/Iron Flask used source `270450a5d90da64c72cd5702e6fce72a5164e279` over main
`fc5f4b171c459982e850904e90122dc61a6c8708`.
Its Gateway SHA-256 was `e94a4053eff0762093c15919ce79f5c1e0db241b60fec1166452c07aedfb34e7`.
The other two runs used `a8bacb9a684f06f5ab2d0fde848f9e15a45c5064` over the same base, with Gateway SHA-256
`c6cf815c19e8bef1674a6ded712f5f0d7f9d10c4b27539d9f0de9359eff0d521`.
The only later behavioral change is the paid Iron Flask restoration covered by the final Ward run.
Every run checked clean source, embedded Gateway/Core/Host revisions, the listening DLL path and repository-root CWD.
Metadata, read-only class events and results are retained locally in those ignored directories.
No client archive writes or client restarts were performed.
Cleanup left the existing single client at login and restored the accepted normal Gateway without either opt-in.

The native incoming ranged comparison uses the same production mitigation path through a diagnostic hit.
Actual player bow travel/contact and an actual monster melee stun were checked separately.
This acceptance covers the explicit supported server models above, not every enemy projectile/stun executor,
every skill-duration engine, the native Altar, or official-server parity of rounding and timing.
