# Phrecia Ascendancies module

`Modules/PhreciaAscendancies` owns the existing Phrecia family's class capabilities, opt-ins,
read-only installed-graph verification, allocated node-stat sources and native graph selector.
`Leagues/Phrecia/modules.json` explicitly composes it with the standard game modules.
Any other league can list `PhreciaAscendancies` independently.
League lists are startup configuration; rebuilding the module reloads its code.

## Scope

The initial extraction preserves Gambler, Antiquarian and Aristocrat from main.
Their class descriptors live under `Classes/<Name>`; a class is enabled only after its implementation is supported.
The generic catalog still reads all 19 identities for save validation, without making unfinished classes playable.
Behemoth adds its descriptor and node sources under `Classes/Behemoth`; its reusable stat-driven engines stay in Game.
Whisperer and Harbinger remain separate dependent PRs.
Other contributors' classes are not copied into this prerequisite.
Idols has extracted client data but no gameplay implementation and is a separate task.
There is no placeholder Idols module or dependency on one.

The existing `Game_VERIFIED_GAMBLER`, `Game_VERIFIED_ANTIQUARIAN` and `Game_VERIFIED_ARISTOCRAT`
opt-ins still require the reviewed combined 3.29.3b graph installed separately.
Behemoth uses the same read-only boundary behind `Game_VERIFIED_BEHEMOTH=1`.
The module verifies the original and alternate graph bytes read-only.
It never installs, repairs or writes the client archive.
Normal and unascended characters have no alternate-graph verification requirement.

## Engine boundary and progress

Ward, Adrenaline, stun recovery, luck, attributes, item requirements and other stat-driven consumers remain reusable
in `Core/Game`, including their non-Phrecia item and ordinary-class sources.
The optional module supplies Phrecia node stats before the existing equipment/attribute qualification pass.
The common resolver passes the current mode vector, preserving HardMode alternates for ordinary and optional nodes.
The existing allocation, connectivity, awards, paid refund and compare-and-save rules remain shared.
Save family, stable identity, revision, nodes, awards and unknown fields keep their existing representation.

`AscendancyPolicies` admits an optional family only when its provider is loaded, its host generation is active,
the connection's league lists that provider and the class has its compatibility opt-in.
Selection, pre-inventory admission, point awards and passive mutations use that same boundary.
The command lists available choices only.
A missing provider or unsupported saved identity refuses admission before inventory preparation and save normalization.
It never substitutes a normal class, trims optional nodes or deletes progress.
Restoring the module allows the existing progress to be admitted again.

## Reload ownership

`IModuleContext.Lifetime` is issued by the host for each load, including rollback loads.
Stopping makes it inactive, cancels its token and releases owned resources.
The family registration is owned by that lifetime, rather than by a module-name string alone.
Synchronous progress commits admitted under the provider gate finish before registration retirement.
Read-only policy queries take no provider lock, including saved-progress validation inside database transactions.
Mutations follow connection, provider, then database lock order.
Retirement removes the old policy and closes affected Phrecia area connections; players reconnect to current rules.
This conservative reload policy prevents continued play with missing class drawbacks.
Ordinary characters and connections in leagues that do not use the family remain open.
No durable progress is removed during reload, and an old registration handle cannot remove its replacement.

The lifetime API is cooperative: it does not drain arbitrary module tasks or revoke existing world wrappers.
Behemoth's shared Rampage engine binds its connection worker to Game and optional source-provider lifetimes.
The lock-free `SourceOwner` query exposes only a host-issued lifetime, without graph checks or provider locks.
Gateway cleanup resources contain identities, not old module delegates; retirement clears opaque combat state.
Mutations and sends also recheck current source flags and effect epochs after refunds, death and transfer.
No class-specific job runs without its optional node source.
The older Game-only worker remains historical evidence.
Bounded modular native checks cover reload/re-entry and Rampage dispatch; exact-head outcomes are recorded in the PR.
See [Behemoth's ownership and validation boundaries](behemoth.md).

## Validation and delivery

The common prerequisite precedes Behemoth, Whisperer, Harbinger, ordinary Inquisitor and ordinary Slayer/Deadeye.
Those PRs remain separate and are updated against actual merged predecessors.
Historical tests and native checks from before the module restructuring do not establish this candidate's readiness.
The Gateway regressions exercise host lifetime cleanup and optional-family admission, source/selector ownership,
durable progress retention, ordinary controls and replacement generation isolation without real-time waits.
The bounded native migration smoke checks one legal source-producing allocation for each of the three classes,
paid resets and normal graph controls, optional-module reload/re-entry with retained progress,
and an ordinary connection remaining playable across that reload.
It does not repeat historical whole-class combat matrices.
Current solution, Core/Data/Gateway and native acceptance receipts belong to the exact tested base/head in `logs/`.
