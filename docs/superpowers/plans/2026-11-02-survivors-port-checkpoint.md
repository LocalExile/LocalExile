# Survivors mode — full checkpoint (2026-11-02, end of session)

Everything about the Vampire Survivors-style mode, where it lives, what is verified and what is next.
The feature was originally built on the pre-refactor Gateway, then ported to the current `Modules` architecture.

## TL;DR

- The port is **done, builds and passes all tests**, and is verified live in the new tree.
- **PR #502** (https://github.com/LocalExile/LocalExile/pull/502) is **ready for review, `mergeable=true`**; the only
  blocker is GitHub waiting for the maintainer to approve the fork's `docs-scan` workflow run (normal for fork PRs).
- **Issue #503** (https://github.com/LocalExile/LocalExile/issues/503) tracks the remaining per-gem/per-support
  verification.
- Next session: pick up at "Next steps" below.

## Update: gem mechanics pass (2026-11-02, late)

Commit `d2dbfa10` (pushed, PR #502 head) makes every run skill cast with its own game-data rows instead of one flat
damage number:

- Gem level = run rank. Base damage (`HitDamage`), damage effectiveness, conversion/gain, crit chance/multiplier,
  cast time, projectile count, pierce/chain/fork/split, area, duration and ailment chances are read from the gem.
- The hit runs through the Gateway's PoB port (`PlayerHitDamage.Resolve`, PoB 16de4b82 `calcDamage`); supports add
  their real gem stats (`EffectStats` at the skill's rank) to the same dictionary.
- Signature mechanics: Fireball explosion, Kinetic Blast clusters, Tornado Shot secondaries, Spark's seeded wander and
  0.66 s hit window, Ice Spear's second form, Ball Lightning/Storm Burst pulses, chains (Lightning Arrow, Cobra Lash),
  always-pierce gems, Vaal Spark/Lightning Trap volleys, Vaal Burning Arrow pierce.
- Support formulas verified live: Added Fire 25% of physical (EK 14-22 -> Fire 3.5-5.5), Added Cold 4-6 x 300%
  effectiveness (12-18), Faster Casting 0.75 -> 0.63 s, Increased Area radius 4 -> 4.6, Increased Critical 5% ->
  11.2%, Faster Projectiles hit x1.2 and speed x1.5, Life Leech 2% of damage.
- New dev commands: `/survivors test <gem>`, `/survivors link <support>`, `/survivors rank <1-8>`; a per-cast trace
  under `Game_SURVIVORS_TRACE=1`.
- Verification: all 25 projectile gems cast; Core 460, Data 45, Gateway 73 tests pass; build 0/0.
- Remaining (issue #503): DOT components (caustic ground, Soulrend debuff, poison) need a monster ground-DOT model,
  per-gem cast animations, and the full support x gem matrix.
- The level-up window can be left open by a replaced run; `SurvivorsOfferIfDue` now refuses to deal an offer for a run
  that is no longer `conn.Survivors`, and the run's `finally` restarts a clock it may have stopped.

## Update: lore boss gate and the full unique catalogue (2026-11-02, later)

- **Boss gate.** Each stage now spawns the area's own unique (`MonsterPacks` boss) 15 run-seconds in. The stage timer
  only opens the next area when that boss is dead; otherwise the run stays and says "The way is sealed until <boss>
  falls." The message names the area (`AreaTransitionCatalog`) and the boss. `Game_SURVIVORS_STAGE_SECONDS` (>= 15)
  shortens the stage for verification. Live: Hillock in the Twilight Strand, then Fire Fury in The Coast; the sealed
  branch was seen with a 15 s stage (boundary before the boss spawn), then the boss died and the run moved on.
- **Uniques in the run.** Every cast now merges `FinalPlayerStats` (passives and equipped uniques) with the gem,
  supports and blessings, so a unique's damage, cast speed, crit, area, projectile speed, penetration, leech and
  ailment stats reach the run's hit. The pool only offers items that fit an equipment row (no jewels/flasks/maps).
  The Searing Touch raised Fireball from `Fire 20.35-31.65 / Cold 27.13-40.70` to `Fire 44.56-69.32 / Cold
  39.61-59.42` live.
- **Unique sweep.** `/survivors uniques`: 1299 definitions, 1299 create, 1072 fit an equipment row, 1071 with a
  recognized stat, 0 missing Mods.dat rows; only Tabula Rasa has no stat (its identity is white sockets).
  `UniqueCatalogueTests` locks this in CI.
- **Dev commands:** `/survivors unique <name>` equips a named unique; `/survivors uniques` sweeps the catalogue.
- Remaining: DOT components, cast animations, the support x gem matrix, and unique mechanics that are conditional
  rather than plain stats (per charge/at full life/on block).

## Where everything is

| What | Where |
|---|---|
| Port worktree | `E:\LocalExile-port` (git worktree), branch `port/survivors-mode` |
| Ported branch on the fork | `GafGafGaf5612/LocalExile:feat/survivors-mode-preview` (latest commit on that branch) |
| Original (pre-refactor) work | `E:\LocalExile`, branch `feat/survivors-mode` — kept as reference, do not delete |
| PR | https://github.com/LocalExile/LocalExile/pull/502 |
| Follow-up issue | https://github.com/LocalExile/LocalExile/issues/503 |
| Core model & tests | `src/GameServer.Core/World/SurvivorsMode.cs`, `tests/GameServer.Core.Tests/SurvivorsRunTests.cs` |
| Gameplay adapter | `Modules/Core/Game/Survivors/Survivors.cs` (`static partial class Game`) |
| Per-connection state | `src/GameServer.Gateway/Connection/ClientState.Survivors.cs` |
| Docs | `Agents/docs/game/survivors-mode.md` (ported, with preview art `docs/preview/survivors-mode-preview.png`) |
| Checkpoint (this file) | worktree `docs/superpowers/plans/` and a copy in `E:\LocalExile\docs\superpowers\plans` |

## Commits (port branch, newest first)

```
d2dbfa10 feat: every survivors gem casts with its own stats and PoB formulas
35eb8214 docs: full survivors port checkpoint for the next session
a6acfbe9 fix: closing the survivors window takes the first reward instead of reopening
be57c615 feat: the survivors level-up window reopens instead of hanging the run
af566a76 docs: drop the external reference link flagged by docs-scan
76926637 docs: strip hidden control characters and the BOM flagged by docs-scan
ceecd25e fix: survivors projectiles are server-drawn; herald auras and hit effects
71f25849 feat: herald aura skills and an empty character at every survivors start
f59aa07b docs: survivors checkpoint - unique stats and ground sweep
0fb344c8 fix: survivors unique stats reach the character; sweep the ground every run second
c92079b7 docs: survivors port checkpoint - kills and clean merge
0c073203 fix: survivors projectile profile after the upstream arrow parameter
de07fbd6 merge: upstream main into the survivors port
...
```
`git log --oneline -15` in `E:\LocalExile-port` for the full list.

## Feature state (all verified live unless noted)

| Feature | State |
|---|---|
| Horde | from the area's MonsterPacks, 14 alive at start to 120, magic/rare rolls, boss every 135 s, ring wave halfway |
| Monster scaling | life ×1.75, damage ×1.9 by 15:00 (`MonsterModSet` with `maximum_life_+%` / `damage_+%`) |
| Run skills | every projectile skill gem the data resolves; gem level = run rank; the gem's own cast time, 7% shorter per rank, divided by increased cast speed (floor 0.2 s) |
| Projectiles | one per target (nearest, or toughest for Pyroblast/Aimed Shot/Exorcism/Starfire), up to the gem's own count; the gem's pierce/chain/fork/split/area/duration apply |
| Damage | PoB calcDamage: the gem's base at rank (gem level) + added × effectiveness, converted, increased/more; the catalogue's median rank-1 hit equals the area's median monster life; crit ×1.5 |
| Supports | 8 real support gems, up to 5 per skill, each linked at the skill's rank and read for its real stats (added fire/cold/lightning, cast speed, area, crit, projectile speed/damage, life leech) |
| Blessings | 8 run-local passives (Might, Haste, Area, Critical, Leech, Wisdom, Fortitude, Swiftness) |
| Unique items | every 3rd run level from the full 1299-definition PoB catalogue (1072 equipment); equipped on pick; requirements waived; mods reach the character and the run casts (FinalPlayerStats merged); only Tabula Rasa has no stat |
| Heralds | Ash/Ice/Thunder/Agony/Purity as `Herald` kind: aura pulse (12 cells, grown by rank/area) every cast, gem aura buff on the player (`0x1c1`) + impact packet (`0x1af`) on each hit |
| Skill bar | run skills as Actor skills (`0x2ba`), ids from `0x0300`, slots from Q; cooldown `0x1ce` per target + `0x02ba` trailer |
| Loot | no `DropLoot`, no `SendGroundItems`, no gold auto-loot during a run; ground swept at start, on area resume and every run second (`0x2bb`, store `Take`) |
| Inventory | every `/survivors start` empties the character (main inventory, equipment, flasks, second weapon set, row 102), one `0xbf` per row |
| Pause | clock stops while the choice window is open; resumes after the pick |
| Window close | closing the window (click outside / Esc / 0x85) **takes the first reward**; the run never hangs |
| Kills | `RecordKill` hooks in `CommitProjectileKill` and `MonsterKilledBy` |
| Area change | at the stage boundary the run moves to the next area **only after the area's lore boss dies**; carries over (`SurvivorsCarried`); `Game_SURVIVORS_STAGE_SECONDS` shortens the stage for tests |
| Commands | `/survivors start|stop|pick <1-3>|status`, dev `/survivors level` (banks levels), dev `/survivors herald` (learns the first herald) |

## Verified live (2026-11-02, new architecture)

- `survivors run started ... 29 skills, 8 blessings`, horde spawns, projectiles kill (`damage=23 life=0/15`).
- Kill counting: 40 kills / run level 5 in 16 s; 368 kills / level 13 in 1:26 with a longer run.
- Reward window: `0x00c0` + `0x0087`, three named choices, `/survivors status` lists them with ids.
- Unique item: `Stasis Prison` equipped in row 1; stats fix logged `base_life_regeneration_rate_per_minute 0->187`,
  `base_number_of_golems_allowed 0->3` (The Primordial Chain).
- Heralds: `0x01c1 survivors herald aura Herald of Ash (BuffDefinitions row 344)`, `hit by Herald of Ash for 23,
  life 0/15 (dies)`, `0x01af player hit on monster ...`.
- Loot: 351 ground items hidden at start, 0 `create world item` during a run; inventory reset rows logged.
- Window close: `offer cleared after choosing Herald of Ash` + `offer closed`, run continues.
- Area change (old branch, same code path): run carried to the next area.

## Tests and build

`cd E:\LocalExile-port`

```
dotnet build GameServer.slnx          # 0 errors, 0 warnings
dotnet test tests/GameServer.Core.Tests       # 455 passed
dotnet test tests/GameServer.Data.Tests       # 45 passed
dotnet test tests/GameServer.Gateway.Tests    # 73 passed
```

## Environment (worktree setup, one-time, already done)

- `E:\LocalExile-port\Client` is a **junction** to `E:\LocalExile\Client`.
- `E:\LocalExile-port\Tools\ooz\node_modules` is a **junction** to `E:\LocalExile\Tools\ooz\node_modules`.
- `E:\LocalExile-port\data` was extracted with `scripts/dev/extract-data.ps1` (includes `game-files.zip`).
- Copied from `E:\LocalExile\data` (secrets/saves, **never commit**): `login-keys.json`, `instance-secret.bin`,
  `saves.db`, `saves.db-shm`, `saves.db-wal`.
- Ports: Auth `20493`, Patch `8080`, Gateway `6112`; test character `SurvivorMvp`, account `survivormvp`,
  password `mvp-survivors-1`.

## Resume commands

```
cd E:\LocalExile-port
git status                                  # branch port/survivors-mode
dotnet build GameServer.slnx
powershell -ExecutionPolicy Bypass -File scripts\dev\live.ps1     # ~2.5 min
python scripts\dev\client_control.py login:survivormvp --password-stdin   # password mvp-survivors-1
python scripts\dev\client_control.py character:play:SurvivorMvp
python scripts\dev\client_control.py command "/god on"
python scripts\dev\client_control.py command "/survivors start"
python scripts\dev\client_control.py command "/survivors level"    # dev: open the choice window at once
python scripts\dev\client_control.py command "/survivors herald"   # dev: learn a herald at once
python scripts\dev\client_control.py command "/survivors status"
```

## Gotchas learned this session

- **Build while the game runs** fails with `MSB3027/MSB3021` file locks: stop the client and the servers first.
- **Fork PR checks**: the `docs-scan` workflow now runs on this fork's commits without a manual approval; earlier
  pushes showed "Waiting for Wrathen to approve this commit". It still reports any hidden character, BOM or external
  link it finds.
- **docs-scan rules**: no hidden control characters (U+0007 etc.) and **no BOM** in added files; external links need
  maintainer review. The scan posts a comment with the exact file/line.
- **Reward inventory** (`0x600`): register it with `0xc0` before any `0xbf`, or the client dies with
  "Got an inventory set change for an inventory that isn't in the set".
- Run skill ids start at `0x0300` (outside the gem band); the bar puts them from slot 3 (Q).
- `ResolveProjectileDefinition` is private to `game.dll`; the survivors file lives in that assembly so it can call it.
- Heralds have no projectile: they are a `Herald` skill kind with an aura pulse, and their buff row comes from
  `GrantedEffects.ActiveSkill → ActiveSkills.Id → BuffDefinitions.Id`.

## Next steps

1. **DOT components** (issue #503): Caustic Arrow / Vaal Caustic Arrow's caustic ground, Soulrend's debuff and poison
   need a monster ground-DOT model; their hit portions already work on the gem's real numbers.
2. **Per-gem cast animations** (documented in `Agents/docs/game/survivors-mode.md` "Needs testing").
3. **PR #502**: head `2fe060c9`, `mergeable=true`, `mergeable_state=clean`, docs-scan green. Merge it when the
   maintainer is ready; the preview label stays in the title.
4. Port the status entry: `Agents/status/skills-and-combat.md` still describes the old branch; update it on the port
   branch (the docs file is already ported).
5. Optional: grow `UniqueCatalog` definitions used by the item offers; more blessings; herald variants.
