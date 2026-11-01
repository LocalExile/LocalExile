# Character stat publication across area transfers

## Failure and ownership

Character equipment qualification is published in `Program.InactiveItems`, keyed by character name.
An area connection has its own raw developer inputs and stat-source cache.
The native client keeps its old socket open while the destination connection starts.
A retired source connection could still run recovery and panel publishers and replace the destination's active-item set.
An equipped gem's skill could disappear from the current native Actor until another stat rebuild repaired it.

The common fix belongs to the character-panel prerequisite, PR #318, before ordinary Inquisitor.
The #318 author retains ownership of `issue-75-character-stats`.
The contributor proposal updates `candidate/pr318-main361-admission` from exact owner head
`58edf093c7885082bf70d4ec3bc0847e27fb4ce5`, containing main
`c1b112e4890d909a11f612a68702b514e66bdb0d` and the owner's Arcane Might/stat composition.
The current upstream main is `4602dd8ff8321574dfc8e26de0610bd77b931186`.
#318 still needs its author's reconciliation with that newer base before upstream readiness.
An exact merge-tree check finds nine conflicted paths, including the replaced bow executors and panel/combat code.
The proposal follows the owner's head without taking over that broader integration.
It supersedes the older admission proposal; the owner already performed that merge independently.
The earlier candidate `f651d0e6` is retained locally as `archive/pr318-admission-f651`.
The new diff contains no Inquisitor gameplay, fixtures, data generation or catalog-path changes.

## Publication and recovery contract

`CharacterStatSources` checks Closed, LeavingArea and RageRetired under the connection's combat lock before
publishing its equipment snapshot or its qualification log.
The function can still calculate local sources for transfer capture and read-only callers.
The lifecycle check occurs after calculation, so a calculation queued before retirement cannot publish afterwards.
`Transfer` explicitly calls `conn.Retire()` after capturing all carried mechanics.
`CarryRage` also retains its original retirement contract for its direct callers, even with an empty Rage balance.

The continuous recovery, player, charge, Fortification and flask workers stop on retirement before socket closure.
Temporary LeavingArea suspends work without permanently stopping the connection.
Recovery resets its elapsed boundary and invalidates cached rates while leaving, allowing a failed transfer to resume
without applying cached regeneration across the loading interval.
It also advances and discards independently accumulated leech, Fortification, Antiquarian and Enduring Cry recovery.
Unexpired recovery sources remain active after a refused transfer; their loading-time recovery is not paid later.
Queued Energy Shield refreshes check lifecycle inside their send gate before source aggregation.
Rage/charge refreshes also reject old lifecycles before their preparatory stat reads.
`CriticalContext` retains its existing implementation without adding a full-calculation combat lock or a stale default.
Current connections retain normal requirement removal/restoration and panel publication.

This guard covers closed, leaving and retired connections, not a general per-character publication owner.
The admission path does not guarantee closing an older half-open socket when the same character relogs.
Ruthless's separate resource-admission owner protects its vitals, not this shared qualification dictionary.
A still-open, non-retired old connection and out-of-order calculations on one current connection remain follow-up work.

## Snapshot cache

`CharacterPlayerStatsSnapshot` (the whole character panel) runs about 90 times a second for one idle player: the Energy
Shield publication on every 16 ms recovery step, the recovery loop's rates, Rage, charges and the player tick. It is two
parts (`StatSnapshotCache.cs`), with the tail split into three pure stages. The head gathers the stat sources and runs every side effect that goes with them (pool
rebuilds, the charge, Fortification and Antiquarian observations, the leech and Fortification configuration, the
time-dependent counts, the qualification snapshot); it runs on every call, as before. The tail (the panel's derived
rows, the default attack's hit and ailment rows, accuracy, critical and off-hand rows) is a pure function of
`StatSnapshotTailInputs`: the head's stats in their order, the raw `/set_stat` values, the pools, the active set's
hands (rows 2 and 3, immutable item records, and which are inactive), the hands' local stats, life and maximum life,
Energy Shield recharge, whether the player is walking (the while-moving regeneration), Blood Charges held and shown, the elemental ailments, the suppression chance and the default
attack's effect. Inputs are captured at each stage boundary; the stages read nothing else of the connection.
`WithRampage` runs between the panel and offence stages on every call, at its original position.
Ailment reconciliation runs between the offence and critical stages on every call, before capturing ailments,
suppression and the combined action factor. This preserves derived immunity, freeze effect and action-speed floors.
A connection keeps its last result for each stage and reuses it while that stage's inputs are equal, so there is no invalidation to forget: any source the
head changes (equipment, gems, passives, level, buffs, auras, flasks, charges, leech, Fortification, mods and modules)
changes the head's stats or an input. A new area is a new connection with no tail.

The head runs `CharacterStatSources` twice (the sources and the own-aura restriction), each expanding the charges.
`WithCharges` reuses its last `ChargeEffects.Expand` while what that pure function reads is the same (the stats in
order, the `/set_stat` values, the effective counts and the conversions); `ChargeSession.Observe` still runs every time.

`Game_STAT_SNAPSHOT_VERIFY=1` computes every reused tail and charge expansion again and throws if they differ in any
value or in order; the snapshot tests turn it on in a scoped fixture. `StatSnapshotTailTests` changes each source in turn, on a stopped connection
clock, and compares with a connection that has no tail. The tests use the built Game assembly and a scoped rules lifetime and temporary save database.
They do not start the global module host or register optional providers that would affect other tests.
Regression coverage includes Rampage expiry with warm stages, additive action speed, base-speed floors and immunity
reconciliation after a cache hit.
Historical measurements before the three-stage integration (Release, one character in Lioneye's Watch): one snapshot 64 µs and 150 KB before, 20-22 µs and 61 KB after;
an idle player (the connection's loops over loopback) allocates 13-14 MB/s before and 5.4-6.1 MB/s after, on The Coast
16-18 MB/s and 7-9 MB/s; its process CPU, on a shared machine, 42-56 ms/s before and 29-37 ms/s after in town.

## Automated evidence

The reviewed code revision is `576625933f964c3528848ab73d774868b1985866`.
`AscendancyGatewayIntegrationTests.StatLifecycle.cs` adds 20 generic cases using real extracted items and gems.
It covers three stale lifecycles, a refresh retired while queued at the send gate, active publication, real
successful-transfer retirement, resource rollback/resumption, and seven continuous publishers.
The same-character fixture gives the earlier connection insufficient raw Dexterity and the current connection enough
to qualify a Short Bow containing Fireball.
Assertions inspect the shared snapshot before a current rebuild could hide a stale write.
The four stale-publication/resource-retirement cases fail on unmodified owner code `b001bfa3` and pass with the fix.
Three review regressions cover all four independent recovery consumers, active leech through an actual refused
`Transfer`, and preservation of the critical-context baseline across loading/resumption.
The latter two fail with the reviewed loading/CriticalContext behavior restored and pass with these fixes.
Evidence for the current review run uses `logs/pr318-review-*`; the initial validation below retains its old revision.
On `57662593`, the solution builds with zero warnings/errors and passes Core 394, Data 34 and Gateway 3192.
All suites have no failures or skipped tests; Gateway collections run serially.
Passive graphs v5 are extracted through the standard read-only extraction script for the owner's jewel fixtures.

Initial code `8b6a9c76` builds with zero warnings/errors and passes Core 389, Data 34 and Gateway 2578.
These are historical counts for that revision; serial Gateway collections have no skipped tests.
Ignored evidence: `logs/pr318-lifecycle-{build,core,data,gateway,before,focused}.log` and their TRX results.

## Native evidence and boundaries

The current review code `57662593` passes twelve native controls through `scripts/dev/live.ps1` again.
Its Gateway SHA-256 is `82ff61eb1c4d4bfd63d31d65d118e09632e980337a1244b5111d62da60b51f33`.
Evidence is in `logs/pr318-lifecycle-native-20261030-144229/{metadata,results}.json` and
`logs/pr318-review-native-{run,gateway}.log`.
The initial stopped runtime is restored.
The refused-transfer leech case and deterministic accumulator checks are automated controls, not native Auth rejection.

The initial twelve native controls on `8b6a9c76` also passed through `scripts/dev/live.ps1`.
Gateway SHA-256: `dae82555202af09f734443d52b050a39fbdc6f4ca77342605406bb0e4becbf29`.
The initial stopped runtime is restored after validation, which used a separate copied SQLite database.
Raw Dexterity removal removes the bow's Fireball from the current native Actor; restoration restores it.
Four alternating town/Coast transfers retain the qualified skill throughout read-only idle observations.
Two actual Fireball uses each reduce native Mana from 492 to 487 with Mana regeneration prohibited.
The ES regeneration panel receives 900/minute from base 600 and 50% recovery increase, with recharge disabled.
Native ES rises from 110 to 122 in 0.801 seconds, matching 15/second.
Removing the source clears the panel and stops actual ES recovery; relog restores the equipped skill after qualification.
The current skill probe reads both stat containers of the owned native Actor's Fireball, not an old skill object.
Evidence: `logs/pr318-lifecycle-native-20261030-032042/{metadata,results,payment-diagnostic}.json`,
the ignored smoke source `logs/pr318-lifecycle-native.py`, and `logs/pr318-lifecycle-native-gateway.log`.
Earlier diagnostic attempts used an incomplete Mana regeneration control and are not counted as acceptance.
These controls do not re-certify all 387 panel rows or deferred skill execution.

The existing issue #75 row ledger remains an audit ledger: its pending entries do not mean absent panel rows.
The owner's earlier native row-batch evidence retains its original code revision.
Related Behemoth, Whisperer and Harbinger changes remain outside this proposal.
Their downstream merge order must be revalidated against accepted heads; the typed-expiry seam remains open.
