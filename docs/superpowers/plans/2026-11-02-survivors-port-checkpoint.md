# Survivors mode: port to the Modules architecture (checkpoint, 2026-11-02)

The survivors feature was written against the pre-refactor tree (`Program.*` partials in
`src/GameServer.Gateway`). Upstream `main` (`dfcdc8f3`) moved gameplay into the `game` module
(`Modules/Core/Game/**`, `static partial class Game`) and left only transport in the Gateway.
This branch is the port, based directly on `dfcdc8f3`.

- Worktree: `E:\LocalExile-port`
- Branch: `port/survivors-mode`
- Build: `dotnet build GameServer.slnx` - **0 errors, 0 warnings**
- Playable: **yes**, verified live in the new tree (2026-11-02, 20:2x-20:3x)

## Verified live in the new architecture

- `/survivors start|stop|status` work; the run spawns its horde, casts Fireball, monsters die
  (`fireball-explosion ... damage=23 life=0/15`), and the skill bar and cooldowns go out
  (`0x0053`, `0x01ce` per target + the `0x02ba` cooldown trailer).
- `/survivors level` (dev) grants run experience at once; the reward window opened with three named
  choices (`Ball Lightning`, `Freezing Pulse`, `Soulrend`), and `/survivors status` lists their ids.
- Picking a skill learns it (`Learned Burning Arrow, rank 1.`), republishes the Actor skills and the bar.
- A unique item appeared as a choice (`Stasis Prison`) and the pick equipped it:
  `0x00bf survivors unique Carnal Armour equipped in row 1` and `Equipped Stasis Prison.`
  (the new `UniqueCatalog` has many more uniques than the old two).

## Done in the port

| Piece | Where |
|---|---|
| Core rules, run state, tests | `src/GameServer.Core/World/SurvivorsMode.cs`, `tests/GameServer.Core.Tests/SurvivorsRunTests.cs` |
| Adapter (spawner, casts, offers, items, area carry) | `Modules/Core/Game/Survivors/Survivors.cs` (`static partial class Game`) |
| Per-connection state | `src/GameServer.Gateway/Connection/ClientState.Survivors.cs` |
| `/survivors` command | `GameModule.Load` -> `module.OnChatCommand("survivors", ...)` -> `Game.SurvivorsCommand` |
| Run skills as Actor skills | `PlayerSkills` appends `SurvivorsGrantedSkills`; `SendSkillBar` places them from slot 3 (Q) and works without a saved bar; `SendSkills` resends the bar while a run is live |
| Unique requirements waived | `RequiredLevel = 0` on the offered item; `virtual_*_for_requirements` raised in `FinalPlayerStatsFrom` while `SurvivorsItemIds` is non-empty; `Progression.cs` removes those ids from the inactive set |
| Loot hidden | `DropLoot` returns early during a run; `SendGroundItems` returns early; gold auto-loot skipped; `StartSurvivors` sends `0x02bb RemoveObject` for every item in `Instance.Ground` |
| Damage quartered | `CommitMonsterHitLocked` (after Fortification) uses `SurvivorsRules.IncomingDamage` |
| Reward-window click | `VendorsFeature.BuyFromShop` / `CtrlBuyFromShop` call `Game.ChooseSurvivorsSkill` first |
| Area entry | `Modules/Core/Game/Player/Messages.cs` calls `ResumeSurvivors` after `PopulateArea` |
| Monster scaling | `SurvivorsMonsterMods` builds `MonsterModSet` with `maximum_life_+%` / `damage_+%` |

## Tests

- Core: 455 passed (includes the 19 survivors tests).
- Data: 45 passed.
- Gateway: 48 passed, 5 failed: `PhreciaFourClassesTests.Each_class_is_admitted_with_its_own_opt_in` (4 cases)
  and `BehemothRuntimeTests.Provider_retirement_releases_owned_state...` - both unrelated to the survivors
  code (league admission / worker retirement) and failing in this environment.

## Merge and PR

- main moved to `7d489618` (70 commits on top of the port's base); the merge into `port/survivors-mode` was **clean, 0 conflicts**.
- One upstream API change needed a fix: `ProjectileSkillParameters.Resolve` now takes an `arrows` flag; the survivors cast passes `ArrowsFor(conn, skill.Effect)`.
- Tests after the merge: Core 455, Data 45, Gateway 73, all passing; build 0 warnings.
- PR #502 is ready for review (not a draft), `mergeable=true`; the only blocker is the maintainer approving the fork's `docs-scan` workflow.
- Live re-check after the merge: 40 kills at 00:16, run level 5, offer open with three named choices, and **0** world items created during the run.

## Latest fixes (2026-11-02, evening)

- **Unique stats now reach the character.** The stat cache in CharacterStatSources was the blocker: an exempted
  item stayed out of the cached ctive list. Progression.cs now adds the run's items to ctive, drops their ids
  from the previous inactive set, and the cache key carries SurvivorsItemIds. Live: `The Primordial Chain` logged
  `base_life_regeneration_rate_per_minute 0->187, base_number_of_golems_allowed 0->3`.
- **Ground loot.** `SurvivorsClearGround` runs at start, on area resume and once per run second; it takes every
  item out of the instance store and sends `0x02bb RemoveObject`. Live: 65 hidden, 0 `create world item`.

## Open items

1. **Ground items (fixed).** The pile seen during a run was loot left by earlier sessions in the area's
instance store. `StartSurvivors` now sends `0x02bb RemoveObject` for every `Instance.Ground` item before
the run: the live pass logged **351 hidden items** and the floor cleared. A few labels near a fresh corpse
remained (created after the sweep); if they matter, re-sweep on the first run tick.
2. Area change at 02:00 in the new tree (`ResumeSurvivors` + carry) not exercised yet.
3. Push `port/survivors-mode` to the fork and update PR #502 (title/body: drop "needs port", keep the
   preview note); issue #503 (verify every projectile skill and its supports) stays open.
4. Port the docs (`Agents/docs/game/survivors-mode.md` still lives on the old branch) and the preview
   image into the new tree.

## Resume commands

```
cd E:\LocalExile-port
git status                      # branch port/survivors-mode
dotnet build GameServer.slnx
powershell -ExecutionPolicy Bypass -File scripts\dev\live.ps1
python scripts\dev\client_control.py login:survivormvp --password-stdin   # password mvp-survivors-1
python scripts\dev\client_control.py character:play:SurvivorMvp
python scripts\dev\client_control.py command "/survivors start"
python scripts\dev\client_control.py command "/survivors level"
python scripts\dev\client_control.py command "/survivors status"
```

The worktree needs `data/` (extracted with `scripts/dev/extract-data.ps1`; `Client/` and
`Tools/ooz/node_modules` are junctions to `E:\LocalExile`), `data/login-keys.json` and `data/saves.db`
copied from `E:\LocalExile\data` (secrets and saves, never committed).
