# Standalone ranged attacks and monster-action port

This branch ports LocalExile `67deb2a` onto `main` at `939b44f`.
The source checkpoint remains `83ead2e`; other job branches and the combined PoB/stats branch are separate.

## Scope

Ordinary bows and wands share projectile release, live collision and hit handling.
Wand release uses the accepted input sequence and the character ACT's main-hand animation pool.
The projectile plays its native impact animation before removal.
Stationary melee contacts no longer receive a walk-in offset.
Animation-contact extraction includes the native wand pools and bumps the manifest entry to version 2.

Monster movement/attacks use bounded game-time cadence and per-monster action generations.
Native TakeHit recovery cancels stale ordinary movement/attacks, while boss stages keep their own lifecycle.
Queued echoes and damage commit through the actual send gate; pause freezes their game time.
Invalidating incoming hits also retires ordinary monster generations, preventing a failed transfer from reviving them.
Nonflinching hits return without waiting on a paused animation notification.

## Upstream and source integration

The source's later Rage/retired-connection guards were carried into the ranged path.
The renamed OrdinaryRangedShot retains the destination's existing Rage and conditional-critical calculations.
Merged onto main's projectile framework, the legacy flight loop is gone: ordinary bows and wands release through the
shared runtime (`Projectiles/Program.ProjectileRanged.cs`), scheduled at the weapon contact before the action echo, and
the wand impact clip is a runtime presentation whose removal lifetime runs on the same game clock.
Monster contact keeps main's Gambler, ailment, Antiquarian, Hardcore and grace-period rules inside the action gate, and
the AI planner keeps main's surround cells and blocker frames on the 50 ms cadence.
Monster contact retains the current destination's injected incoming RNG, recent-hit armour decisions,
chaos-hit armour, resistance/immunity processing, Fortification, Energy Shield, block gains and Rage gains.
Charge-on-hit accounting remains single rather than duplicating the pre-existing contact update.

Existing Gateway fixtures now call the new action-generation attack interface.
Their original damage/block/ES expectations remain intact.
The older pause fixture now observes the action echo after resume, consistent with the new send-gate model.
A queued-flinch test measures generation changes relative to the transfer's own invalidation.
No private Ruthless catalog, PoB importer, character-stat port or league presentation was folded into this branch.

## Live reference (official servers)

On 2026-10-29, evrySniff (`../evrySniff`, next to the evryPath checkout) recorded the retail 3.29.3b client on GGG's
servers. It was a level 1 Ranger with a bow: character creation, the Twilight Strand and Lioneye's Watch, about two
minutes, 30 default attacks and 15 uses of the starting gem. The comparison capture is the same player on this
branch's stack the same evening: 28 default attacks and 18 gem uses against the Gateway started at 22:51, after
2dd0d763 and before 185ee748 moved the arrow release point. The capture files stay local:
they hold account data and are not committed. The analysis used `sniff.py` on both.

- **No arrow objects.** Over all 45 live shots, the server sent no `0x2b9` arrow and no `0x2bb` removal. A shot was
  the `0x1ab` echo, `0x1ac` and a `0x2ba` on the player at once, then `0x1af`, `0x1b3` and `0x1b1` on the target when
  it landed, so the live client draws its own arrows. This branch creates `Metadata/Projectiles/DefaultArrow` objects
  (and the gem's projectile) for each shot. The Frida trace that saw no client arrows on our server therefore points
  at something in our answer that stops the client from drawing its own. The likeliest causes are the skill ids
  (live `0x4000` for the default attack and `0x8002` for the gem, ours `0x102`/`0x103`) and the `0x1af` fields below;
  neither has been tested.
- **`0x1af` on the player's arrow hits:**

  | Field | Live | This branch |
  |---|---|---|
  | object header | the attacker's id, then a u32 that differs on every hit and u16 `0x0001` | zeros |
  | skill index byte | 2 on all 43 | 0 |
  | flags | `0x0200` (main hand) on all 43. The default attack: `0x4201`/`0x4601` with `0x0001` (blood); the gem: `0x4204`/`0x4604` with `0x0004` in its place. `0x0400` with a 300 ms stun on 22 hits, `0x0020` on 6 crits, `0x8000` once (meaning unknown) | `0x4001` (+ `0x0020`) at the capture; `0x4201` (+ `0x0020`) since 2026-10-30, index and header still zero |
  | damage code | 0 or 8 (Physical, variation 0 or 1) for the default attack; 2, 10 or 18 (Fire, variations 0–2) for the gem | by damage type, variation 0 |
  | kind | 3 | 3 |

- **Hit timing.** Live, the hit arrives 258 ms + 3.25 ms per cell of shooter-to-target distance after the echo, so the
  arrow flies about 308 cells/s (30 hits, 8–72 cells). Live delays also fall on about 33 ms steps. The gem's hits fit
  267 ms + 3.90 ms per cell. This branch fits 249 ms + 2.72 ms per cell (about 367 cells/s): the release is close.
  The slope is not the simulation speed, though: the Gateway releases DefaultArrow at the table's 3260 units/s,
  299.9 cells/s (logged on every release), close to live's 308; what makes our hits arrive early per cell is open
  (candidates: the contact at the target's collision radius, the echo's gate delay).
- **Undecoded:** S→C `0x1b4` went 27 times to the player only. It carries the attack's skill id (`0x4000`/`0x8002`)
  and a list of compact-int points, and may be the shot's path.

A second live capture on 2026-10-30 covered Sand Spitters. It was the same kind of character, on the Twilight Strand
from login. Live 0x20 registered `Metadata/Monsters/SandSpitters/SandSpitter12@1` (row 490), the variety and level
this branch spawns there, and all 11 hits the player took came from three of them (type `0xe63d7611`). In the first
capture the player was never hit (its `0x1b1` changes were mana, pool 1).

- **No mortar objects.** No `0x2b9` was created around the spits, so the live client draws the mortar as well. This
  branch sends `Metadata/Projectiles/SandspitterMortarProjectile` objects.
- **The spit and its hit.** Each spit is `0x1ab` flags `0x80` with the player as the target object and a real action
  sequence (`0x80bc`, `0x80bd`, …; the Spitter's skills there were `0x4000`–`0x4002`). Each hit is `0x1af` kind 3,
  flags `0x4201` (`0x4221` on the one crit), code 0, skill index 2, with the sequence of the spit that caused it. This
  branch sends the spit with sequence `0xffff`, and the hit with `0xffff` and skill index 0.
- **Damage.** On live, 10 hits took exactly 4 life each and the crit took 5. This branch's level 1 Spitter hits for
  5–7: `DefaultMonsterStats` level 1 `Damage` 4.99 × `DamageMultiplier` 120 (its MonsterTypes row
  `SandSpitterTwilightStrand` ignores attack speed) = 5.99, ±20% `DamageSpread`. Its 2026-10-29 log shows 5, 6 and 6.
  The skill (`MMASandSpitterProjectile`, stat set 624) has no damage stat and base effectiveness 1. A ±20% spread
  can't give a constant 4, so live computes or rolls this hit differently; what it does is open.
- **Rate.** A Spitter's spits were at least about 990 ms apart (its `AttackSpeed` 990), often 1–2.5 s.

## Validation and limits

The solution builds with zero warnings/errors.
Before the merge onto main, the standalone port passed Core 525, Data 18 and Gateway 3,368 (429 focused bow/wand,
monster lifecycle, recent-hit and Rage cases; 38 chaos-armour cases after adapting the last reflection-based helper).
After the merge (main's trimmed suites, projectile framework and surround AI): Core 353, Data 34 and Gateway 1,335
pass with serialized collections. Under the repository's unlimited test parallelism
`A_running_enemy_worker_sends_its_first_chase_promptly_after_the_player_enters_aggro_range` (300 ms bound) can flake
under load and pass again in isolation; `Chill_slows_attack_and_spell_timers_once_in_both_modes` could too until main
moved it to the connection clock.
Merging main at c1b112e4 (jewels, currency validation, Delve, the debugging aids) onto 8d2c99d5: Core 358, Data 34 and
Gateway 1,994 pass at the default parallelism, with `passive-graphs` v5 and `projectile-clips` regenerated. Every line
main added since 5d5b5150 is in the merged tree. Main's `Game_TRACE=ai` gate on chase log lines moved with the AI loop
into `StepMonsterAi`; bow and wand shots take main's `Melee`/`WieldingWand` damage rules through `WeaponFor`.
Four Python animation-extraction tests pass.
The first focused/full attempts are retained in local artifacts alongside their corrections.
The final Gateway runs serialize test collections through a local build-output runner file.

Native screenshots/traces described in the imported protocol/status notes are historical LocalExile evidence.
Their raw artifact paths resolve in the preserved source checkout.
No native client was launched for this standalone port and no existing server runtime was replaced.
Fresh native validation and combinations with other job branches remain separate work.

From the repository root, with extracted catalogs available under `data/`:

```powershell
dotnet build GameServer.slnx
'{"parallelizeTestCollections":false,"maxParallelThreads":1}' |
    Set-Content tests/GameServer.Gateway.Tests/bin/Debug/net10.0/xunit.runner.json
dotnet test tests/GameServer.Core.Tests --no-build
dotnet test tests/GameServer.Data.Tests --no-build
dotnet test tests/GameServer.Gateway.Tests --no-build
python -m unittest discover -s scripts/dev -p test_anim_contacts.py -v
```

The test runner file is local build output, not a repository-wide test configuration change.
Production data stays relative to the checkout's `data/`; the shared test initializer is unchanged.
