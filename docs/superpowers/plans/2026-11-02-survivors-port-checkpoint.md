# Survivors mode: port to the Modules architecture (checkpoint, 2026-11-02)

The survivors feature was written against the pre-refactor tree (`Program.*` partials in
`src/GameServer.Gateway`). Upstream `main` (`dfcdc8f3`) moved gameplay into the `game` module
(`Modules/Core/Game/**`, `static partial class Game`) and left only transport in the Gateway.
This branch is the port, based directly on `dfcdc8f3`.

- Worktree: `E:\LocalExile-port`
- Branch: `port/survivors-mode` (commit `2ae80933` "wip: port survivors mode...")
- Build: `dotnet build GameServer.slnx` - **0 errors, 0 warnings**
- Playable: **no** - the `/survivors` command is not registered yet (see Next 1)

## Done

| Piece | Where |
|---|---|
| Core rules, run state, tests | `src/GameServer.Core/World/SurvivorsMode.cs`, `tests/GameServer.Core.Tests/SurvivorsRunTests.cs` (unchanged from the old branch) |
| Adapter (spawner, casts, offers, area carry) | `Modules/Core/Game/Survivors/Survivors.cs` (`static partial class Game`) |
| Per-connection state | `src/GameServer.Gateway/Connection/ClientState.Survivors.cs` (`Survivors`, offer maps, catalog, item ids, pause flag) |
| Run skills as Actor skills | `PlayerSkills` appends `SurvivorsGrantedSkills`; `SendSkillBar` places their ids from slot 3 (Q) and works without a saved bar; `SendSkills` resends the bar while a run is live |
| Loot hidden | `DropLoot` returns early during a run; gold auto-loot skipped in `Player.cs` |
| Damage quartered | `CommitMonsterHitLocked` (after Fortification) uses `SurvivorsRules.IncomingDamage` |
| Reward-window click | `VendorsFeature.BuyFromShop` / `CtrlBuyFromShop` call `Game.ChooseSurvivorsSkill` before the shop/NPC paths |
| Area entry | `Modules/Core/Game/Player/Messages.cs` calls `ResumeSurvivors` after `PopulateArea` |
| Monster scaling | `SurvivorsMonsterMods` builds `MonsterModSet` with `maximum_life_+%` / `damage_+%` (the new `SpawnMonster` has no life/damage scale parameters) |

## Next

1. **`/survivors` command** (the only thing that keeps the mode unplayable). Register it in
   `GameModule.Load` with `module.OnChatCommand("survivors", "/survivors [start|stop|pick <1-3>|status] (dev: ...)",
   async c => await SurvivorsCommand(c))`, and port the body from
   `E:\LocalExile\src\GameServer.Gateway\Program.Commands.cs` (search `.Add("survivors"`). It needs
   `conn = ConnOf(c.Player)`, `(remote, stream, ciphers) = conn.Wire`, and the same start/stop/pick/status branches.
   `StartSurvivors`, `EndSurvivors`, `ApplySurvivorsChoice`, `SurvivorsClock` already exist in `Survivors.cs` with the
   old signatures (all take `remote/stream/ciphers/conn`).
2. **Run end**: `SendSkills` after `conn.Survivors = null` must prune the run ids from `conn.ClientSkillIds` and resend
   the bar (the old code did it in `EndSurvivors`; check that the new `ClientSkillIds` bookkeeping still matches).
3. **Unique items**: `UniqueCatalog` changed (`All` is `GameDataCatalog.Uniques`, `Create(Definition, Random)` exists).
   `SurvivorsItemPool` and `SurvivorsEquip` compiled, but the pool/equip path has not been exercised; the item's
   `RequiredLevel = 0` and the `virtual_*_for_requirements` override still need to be ported into the new stats build
   (old `Program.Progression.cs` `inactive.ExceptWith(conn.SurvivorsItemIds)`, and `FinalPlayerStatsCore`'s override -
   both must be re-applied in `Modules/Core/Game/...`).
4. **Tests**: Core/Data suites run anywhere; the Gateway suite needs extracted `data/` in `E:\LocalExile-port`
   (`scripts/dev/extract-data.ps1`, or a junction to `E:\LocalExile\data` only if the data version matches).
5. **Live**: `scripts/dev/live.ps1` in the worktree (needs `data/`), then `/survivors start`, a level-up pick, an item
   pick, and an area change.
6. **PR**: push `port/survivors-mode` to the fork and force-update `feat/survivors-mode-preview` so PR #502 becomes
   mergeable; drop the "needs a port" paragraph from its body, keep the preview note. Issue #503 (verify every
   projectile skill and its supports) stays open.

## Old branch, kept as reference

`E:\LocalExile` branch `feat/survivors-mode` (PR #502, draft) still has the pre-refactor implementation; use it as the
source of the adapter logic and the docs (`Agents/docs/game/survivors-mode.md`). The port copies the docs too once the
feature runs again.

## API notes for the next session

- `Send(remote, stream, ciphers, bytes, log)` - no `conn` (unchanged from the old adapter).
- `Game` is `static partial class Game` (no namespace), assembly `game.dll`; a new file under `Modules/Core/Game/` is
  part of it automatically.
- `ResolveProjectileDefinition` is private to `game.dll` - callable from our file, not from modules.
- `SpawnMonster(..., mods: MonsterModSet, extraStats: ..., owned: ...)` - scaling is a `MonsterModSet`.
- `ProjectilesFor(...)`, `ProjectileRuntime.Time`, `ProjectileUse`, `ProjectileOffense` keep their old shapes.
- `ItemsOf`, `PlayerInventories`, `ShapeOf`, `SendEquipment`, `SendCharacterStats` keep their old shapes in the new tree.
