# Current-main implementation versus original-client damage research

## Scope

The research branch incorporates main **`d377bd256036242d2fb43f53eb7110b4897e1862`**.
This comparison describes LocalExile implementation to reuse when research informs later development.
It is **not original-client evidence** and does not close the missing R2/R3/R4 numerical or authority connections.

## Existing implementation to preserve and reuse

| Area | Current-main ownership / behavior | Research consequence |
|---|---|---|
| Gameplay orchestration | `Modules/Core/Game/` contains partial Game implementation; Gateway retains shared calculation/state helpers. `game.dll` references Gateway and has access to its internals. | Follow actual callers across the module boundary; do not treat these as two independent implementations. |
| Character and panel stats | `src/GameServer.Gateway/Player/CharacterStats.cs` aggregates character sources; `Modules/Core/Game/Player/PanelStats*` orchestrates panel publication. | Attribute aggregation and panel ownership must be checked against current code before proposing implementation changes. |
| Arcane Might | `src/GameServer.Gateway/Combat/ArcaneMight.cs`, called from `DamageStats.cs`, selects the highest eligible ratio from six contracts and consumes resolved spell increased damage. | Already implemented; do not recreate it. The existing [mechanic record](../game/arcane-might.md) retains scope and limitations. |
| Player Hit damage | `Combat/PlayerHitDamage.cs` retains conversion history while applying increased/more/support effects. Module `Combat/HitMitigation.cs` and Core `World/HitResistances.cs` handle target-side rules. | Existing server formulas are implementation, not proof of the original client's accepted-Hit-to-pool connection. |
| Player-inflicted monster ignites | Gateway `Monster/MonsterIgnites.cs` stores source settings and multiple instances, selecting the strongest active rate; module `Monster/MonsterIgnites.cs` supplies current target taken factors and applies loss. | The old statement that only RF exists is obsolete. Preserve existing ignite behavior in any future design. |
| Incoming player ailments | Gateway `Combat/PlayerElementalAilments.cs` and module `Combat/PlayerAilments.cs` implement ignite/chill/freeze/shock paths. | Helper constructors for other ailments do not alone establish their gameplay integration. |
| RF | Module `Player/Player.cs` performs live outgoing RF calculation and nearby-monster damage. | RF remains an existing implementation to compare; this research has not migrated it into another framework. |

The monster ignite integrator cuts intervals at instance start/end boundaries, selects the maximum active rate,
and carries fractional damage. The caller supplies one target-taken factor for the whole advance interval.
This supports live target sampling at calls; it does not reconstruct every target change within that interval.
Source faster modifiers increase rate and shorten duration in the existing settings construction.

## Distinct clocks are still present

| Path | Current-main source | Scheduling quantity |
|---|---|---|
| Connection server-frame writer | Gateway `Connection/Program.cs`, `Program.Connection.cs` | Configurable `FrameMs`, default 33 ms |
| Projectile step | Module `Projectile/Projectiles.cs` | `TimeSpan.FromSeconds(1d / 30)` |
| Resource recovery loop | Module `Player/Settings.cs`, `Player.cs` | Nominal 16 ms delay, elapsed-time calculations |
| Player tick / outgoing ignite and RF processing | Same module files | Nominal 250 ms delay, elapsed-time calculations |

These values describe the server implementation at the pinned revision. They are not measurements or evidence
of official server cadence, and nominal delays do not guarantee actual wall-clock execution frequency.
The presence of a generic `DamageOverTime.Tick` helper does not establish one shared target-rate cap or a
unified clock across its callers. Those claims require their own call and state evidence.

## Merge and validation boundary

The actual merge base was `c1b112e4890d909a11f612a68702b514e66bdb0d`.
There were nine overlapping documentation files and one textual conflict in the skills protocol reference.
Both the research's granted-vector/wire-cursor warning and main's passive-self-buff description were retained.
The other 102 research-only files were checked byte-for-byte against the pre-merge research head.
All 1,240 pre-existing public support records and 294 sealed navigation snapshot file records were verified
unchanged, as was the original completion-criteria record.

The research branch's difference from this main contains research documents and tools only; no independent
server/module/test implementation was added by this synchronization. This is not a game-build, live-client,
CI/deployment, or ready-for-merge validation claim. Main's existing implementations do not change the original
[research completion criteria](client-damage-calculation.md#8-research-workbench-and-completion-criteria).
