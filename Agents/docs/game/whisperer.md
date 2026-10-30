# Whisperer completion

The delivery unit is all nine real Phrecia notables and nine small nodes in legal connected eight-point builds.
Work is tracked in issue #98 on `codex/whisperer-completion`.
The published standalone refresh uses pinned main `704bf1d2c2763f71ac174e7aaa6bcbf727f63159`.
Build and Core/Data/Gateway checks pass; fresh native selection and legal allocation pass.
The new incoming-hit/flask smoke remains incomplete because its first hit occurred during entry protection.
Historical three-build native acceptance passed at `e5daf05f` on main `6a5697ff`.
Historical affected checks passed again at `0e2d47ee` on main `59154b74`.
Antiquarian is included through main's merged PR #330; this feature adds Whisperer without importing Behemoth's branch.

## Admission

Set `Game_VERIFIED_WHISPERER=1` and choose `/ascendancy phrecia Whisperer` on a Ranger.
This admits only Phrecia identity `Deadeye`, revision `3.29.3b`, after direct read-only archive verification.
Ordinary PSG SHA-256 must be `82D82CD3CD2DAD81606AF0DA9D18FCD53B2E92CF006550947D65A4F7E102DC32`.
The already installed combined alternate must be `F2766B5A4618CDBE7E2993973B4D653F155F71BDFE0E5D063A425196CC93D638`.
Missing/different data or a failed Node decoder refuses before changing saved progress.
The current exporter manifest v5 accepts the exact original or maintained graph without installing anything.
Ordinary characters require no alternate-graph preflight; Antiquarian retains its own verified-graph opt-in.
Gambler and Aristocrat retain their own exact-identity opt-ins; other Phrecia classes retain the default gate.
Graph installation remains separately reviewed maintenance, and native Altar acquisition remains #151.

## Nodes and builds

Use actual Phrecia graph membership, excluding normal Deadeye nodes sharing the Ascendancy table key.
Each small node supplies 6% increased maximum Mana and 10% increased attack damage.

| Node | Target effect and executable consumer |
|---|---|
| 22720 we know all | 6% of maximum Mana added as attack Lightning; 100% increased Mana cost |
| 50932 your mind is destroyed | Additive spell damage modifiers also apply to attacks at 150% |
| 26415 don't panic yet | 100% physical damage taken from Mana before Life; 20% damage taken recouped as Mana |
| 53715 breathe deep | Non-instant Mana flask recovery also recovers Life; Mana flask effects remain at full Mana |
| 2266 there is more | +1 maximum Power/Frenzy Charge; minimum four of both with their combat effects |
| 863 wait a little | Gain 10 Rage after actually spending a total of 200 Mana |
| 4174 an infinite pursuit | No maximum Mana from Intelligence; one Mana per final Dexterity |
| 23799 have you forgotten? | Attack projectiles fork, with one additional fork |
| 5888 not a word | Cannot be Blinded; 50% increased Blind effect; 100% chance to Blind on hit |

| Build | Eight allocated nodes |
|---|---|
| Mana and damage | 17123, 4174, 1116, 22720, 27553, 50932, 15390, 26415 |
| Recovery, charges and Blind | 30419, 53715, 21095, 2266, 540, 863, 9982, 5888 |
| Fork and attack contacts | 17123, 4174, 1116, 22720, 24726, 23799, 9982, 5888 |

The builds cover every real notable/small node without exceeding eight points.
Production allocation, paid full refunds costing 40 refund points, reallocation and durable reload have regressions.

## Mana and payment

Final resolved Dexterity feeds the flat Mana bucket before increased maximum Mana.
The Intelligence flag suppresses only its Mana contribution; Intelligence still supplies requirements and ES scaling.
The same flat inputs compose with Strength Mana and Power of Purpose, with ordinary absent-source rounding retained.
The supported gem cost includes its existing support multiplier, then the character's signed cost modifiers once.
Native granted skills and actual payment use that same final cost; insufficient Mana cannot pay or grant Rage.
Reservation, incoming Mana mitigation, flask removals and generic pool clamps never count as spending for this node.
Every completed 200 Mana grants the node's Rage through the existing cap/prevention/decay policy.
The remaining fraction is connection-owned, carried once through a successful ticket and cleared on source removal.

After block and mitigation, Ward absorbs before ES and retains Antiquarian's Ward-break Adrenaline.
Partial Ward bypass preserves the eligible physical remainder before Mana mitigation.
ES then absorbs physical and other non-chaos components; chaos retains its normal bypass.
The eligible physical remainder takes available Mana first, with any shortage reaching Life.
Other damage types and damage over time do not use this physical-only rule.
Hit recoup uses damage remaining after Ward, including its ES/Mana portions, and integrates four-second grants.
The target's `ReminderTextRecoup` explicitly says only hits and four seconds.
Decimal recoup fractions remain owned by the effect, publishing whole earned points through the normal resource writer.
Filling Mana discards all earned Recoup, including sub-point recovery, without paying it into the full pool.
The shared full-pool commit seam covers instant Mana leech, instant flasks and Mana gained on block.
Unexpired Recoup grants and actual Mana-spend progress survive that settlement.
Already earned recoup retains its hit snapshot after source removal; new hits use current sources.
All intervals use pausable game time; area loading freezes remaining durations and earned recovery is consumed once.
Death clears recoup and spending progress; relog/restart retains passive progress and clears transient state.

## Flasks, charges and projectiles

Only continuous Mana flask recovery also restores Life; immediate recovery and instant-at-end effects do not.
The effect retains its Mana queue ownership, lifetime and normal full-pool/death/area rules.
The accepted flask use snapshots the source, and repeated stat rebuilds do not create recovery.
Power/Frenzy minimums use existing charge state, native buffs and per-charge attack/cast/damage/critical effects.
Refunding the node removes its minimums and maxima through the same source reconciliation.
The existing projectile runtime receives two forks for attack projectiles and zero additional forks for spells.
Released projectile inputs retain their offensive snapshot, including the chance/effect/duration of Blind.
Unknown zero-sized area bounds do not erase projectiles where no captured walk grid is available.

## Blind

`ReminderTextBlind` pins 20% less Accuracy/Evasion for four seconds.
The notable's 50% increased effect produces 30% less of each, rather than stacking two Blind multipliers.
Supported ordinary, gem and projectile hit paths inflict it only after an accepted unblocked hit.
Spells can Blind; damage over time cannot.
The strongest active Blind wins, including against an independent smoke-cloud source.
Shorter strong effects can expire back to weaker effects with remaining time.
Native buff 179, condition stat 973 and final less-Accuracy/Evasion stats share the authoritative timer.
Actual player/monster accuracy and evasion consumers read the same effect; spells retain their normal hit policy.
Incoming monster hit mods can inflict Blind through the same immunity check.
`/blind [seconds]` exercises incoming Blind for development controls and respects real class immunity.
Gaining immunity clears the owned Blind; death and successful transfer follow the owner lifecycle.
Broader elemental ailment, conditional Blind and unsupported enemy-skill engines remain separate work.

## Historical native acceptance

The existing single 3.29.3b client passed 38 checks through its documented native control bridge.
No client restart, archive write, mouse or keyboard input was needed.
The selected Phrecia graph had stat 21146 = 1 and packed class 130; ordinary Deadeye retained 130 with graph 21146 = 0.
All nine notables and nine small nodes were allocated through the three legal builds above.

| Native evidence directory under `logs/` | Checks | Accepted behavior |
|---|---:|---|
| `native-whisperer-a-20261028-191827` | 12 | Mana, typed defence, Recoup, costs, damage, refunds |
| `native-whisperer-b-20261028-192203` | 14 | Mana/Rage, charges, flasks, Blind, death, relog, refunds |
| `native-whisperer-c-20261028-193124` | 12 | Bow forks/removal, Rage carry/decay, restart, ordinary Deadeye |

Physical damage 100 consumed 100 Mana with unchanged Life, then recoup restored exactly 20 Mana.
Pause froze recovery; fire damage remained Life damage, and ES absorbed before defensive Mana.
Increasing final Dexterity 32 to 60 raised maximum Mana 587 to 622 despite Intelligence rising to 114.
The actual level-one Fireball cost was 10 with `we know all`, versus 5 after its paid refund.
Controlled attack damage was 215 versus 140 with spell transfer removed, and 189 versus 140 with Mana Lightning removed.
Two actual 100-Mana casts granted 10 Rage; pause froze it, area transfer carried it once and decay completed.
A Small Mana Flask recovered 50 Life at full Mana; paid removal of `breathe deep` removed that Life recovery.
Incoming Blind was refused with immunity, then reduced native Accuracy 200 to 160 after the paid refund.
An actual hit applied native monster Blind condition 973 and -30% Accuracy/Evasion, then expired naturally.
One equipped-bow arrow dealt 100 to five arranged targets through both fork generations.
After paid Fork removal, the same shot dealt `[100, 0, 0, 0, 0]`.
Death cleared Rage; minimum four Power/Frenzy Charges returned after resurrection and relog.
Gateway restart retained the selected class and all eight nodes, restored minimum charges and cleared transient Rage.
Each complete build refunded for 40 points; leaf refunds cost five, with reallocation checked.

Native source: `e5daf05f7f001804f91fde521ae40674032a18f4`.
Base: `6a5697ff2731d2e3fafa83b27fa5ff1e71290149`.
Gateway/Core/Host embedded revisions all matched that clean source.
Gateway SHA-256: `2c7b22c6f1890d0be711936d77d5bf3f732818816e4e2f5a4117931cdcd2d568`.
The listening Gateway ran from this worktree, initially PID 35520 and PID 40996 after the deliberate restart.
Each accepted directory contains `metadata.json`, `results.json` and class traces.
Failed attempts remain separate.
The first Fork attempt used a default melee attack; the accepted attempt explicitly equipped a Crude Bow.
Earlier old-base partial sessions are provisional and are not counted in the 38 accepted checks.

Main `59154b74` subsequently merged Vaal soul budgets, Whaletooth duration and Forest Tracking.
The feature merged that main and retained both `RestoreWhisperer` and `RestoreVaalSouls` after `RestoreRage`.
Shared equipment/requirement snapshots and skill/resource paths received another 24 native checks:

| Current-base evidence directory under `logs/` | Checks | Scope |
|---|---:|---|
| `native-whisperer-a-20261028-193937` | 12 | Mana, typed ES/Mana, Recoup, costs, damage, refunds |
| `native-whisperer-c-20261028-194055` | 12 | Bow forks/removal, Rage carry/decay, restart, ordinary Deadeye |

Historical production source: `0e2d47ee47469003df6a133ad99cadeb9aee59ff`.
Historical integration base: `59154b749c7711a2b2fe68d7db44c7956d17938a`.
Gateway/Core/Host embedded revisions all matched this clean source.
Gateway SHA-256: `f2410d578214902e213cb520951951bb6cc903d6d14da5f3b389454c6bd36e08`.
The current-base Gateway ran from the feature worktree as PID 12376, then PID 45140 after deliberate restart.
No Whisperer implementation changed after the original native acceptance; the new source adds current main.

After both native windows, only Gateway was restored through the original root's `live.ps1`.
Final read-only verification at 2026-10-28 16:42:11 UTC confirmed PID 47420, root CWD and client 44256 at login.
The accepted root DLL SHA-256 remained
`a0b06c49b687a0c315fd91a3eea3bd5fdb214622dccd93d916d7e355e4421d8b`.
Direct process environment reads confirmed the approved isolated DB and all four verified-class flags absent.
Evidence is `logs/native-lease-main59154/restored.json`; the original window's restoration was at 16:32:43 UTC.
The runtime lease was explicitly released to both coordinating threads; no subsequent runtime access is planned.

## Historical integration and validation

Historical source `0e2d47ee` passed the solution build with zero warnings/errors: Core 646, Data 18, Gateway 3728.
Logs and TRX files are `logs/whisperer-main59154-*` and `logs/test-results/whisperer-main59154-*.trx`.
The earlier `e5daf05f` build also passed: Core 640, Data 18 and Gateway 3593.
Gateway uses serial test collections because existing wall-clock fixtures can expire under parallel load.
The full tests cover encrypted resource publication, rounding, supported costs, ownership and ordinary regressions.

The disposable integration was tested at `5d134744bd4eee3a117b81f26b881281ea0bccb6`.
Its solution build was warning-free; Core 646, Data 18 and Gateway 3820 passed.
Final Gambler docs merge `13ed78db6f711f5bf6a03673ac07f78ad2c78b9e` changes only five documentation files.
The actual history retains updates and manual resolutions for this intended order:
main + Behemoth, then Vaal, duration, Forest, Gambler and Whisperer.

| Original tested input | Exact head |
|---|---|
| Main | `6a5697ff2731d2e3fafa83b27fa5ff1e71290149` |
| Behemoth #328 | `07699a51a0b235ca668c6223a51d1362d375d7e9` |
| Vaal #317 | `51846363b8ffca747feec584d9be33d571b2841c` |
| Duration #323 | `5c00a2f48fe0969454e78e793af997e3844efaa0` |
| Forest #327 | `b5fccd124cc373ade827ed37a881798653d8687b` |
| Gambler #332 | `69716ccd3b6ebec2f4bb33330c92fceddcdb47cc` (runtime `fb76fd76`) |
| Whisperer #333 | `e5daf05f7f001804f91fde521ae40674032a18f4` |

The current-base private refresh is `ecd979cbef297472f26a377c213963975d178b7f`.
It includes main `59154b74`, Whisperer production `0e2d47ee` and Gambler production
`4209b3cf0da549e20286d30f3352ee0c6d724fd6`, retaining Behemoth `07699a51` and the manual resolutions.
Its `src/`, `Tools/`, `scripts/`, schema and solution match tested `5d134744` byte for byte.
Tests differ only by a removed historical EOF blank line; `git diff --ignore-blank-lines -- tests` is empty.
Both feature owners independently verified this equivalence and exact input ancestry.
The 3820-test result belongs to tested `5d134744`, not a claimed duplicate run on the refresh commit.
Final feature documentation descendants leave these production inputs unchanged.

The shared merge must retain typed Ward eligibility before ES/Mana, Ward-break Adrenaline and actual-cost-only Rage.
It must compose strongest smoke/timed/Rampage Blind and retain all death/carry cleanup before publication awaits.
Gambler introduced an additional lethal-Ignite boundary absent from the original standalone Whisperer.
The historical private resolution skips Mana recovery and clears Recoup immediately in that lethal frame.
Without it, the encrypted writer regression observed Mana 324 instead of zero before ordinary death cleanup.
Existing `ClearRageOnDeath` already clears Whisperer/Blind; the defect was same-frame recovery before that helper.
Commit `5d134744` includes the correction plus encrypted writer and gate-held carry regressions.
The main `4602dd8` refresh now includes that correction in standalone Whisperer.
See the coordinating checkout's `logs/verification-current.json` and `logs/private-lethal-composition-5d134744.diff`.
Unrelated implementations stay out of this feature branch; automatic or reversed merge orders are not certified.

## Historical main 4602dd8 candidate

The private candidate starts from PR333 `7b9e1061862ea410d05f2304bd8ebb43a0b554de` and merges pinned main.
The delivered history consolidates that result into one commit whose sole parent is pinned main.
Main's `CommitMonsterHitLocked` owns both melee and hostile-projectile contacts.
Whisperer tracks the mitigated physical component there, retaining Gambler luck, Shock, ailments and block policy.
Ward precedes ES and physical defensive Mana; Recoup snapshots only the post-Ward hit.
Lethal Ignite clears Recoup, spending progress, Blind and recovery carries before any death-publication await.
That frame cannot recover Mana; RF retains its separate one-Life minimum and Hardcore death stays synchronous.
Full-Mana flask admission uses the actual recovery snapshot when continuous Mana recovery can reach missing Life.
Ordinary full-pool refusal, charges, authoritative corrections and `0x263` results remain intact.
Instant and instant-at-end recovery do not qualify for the extra recipient.
Main's resolved attributes, jewelry, Arcane Might, channel guards, support cache and mode catalogs remain intact.
Ordinary bow/wand releases freeze effective Accuracy and Blind; gem releases retain `ClientPredicted`.
Graph admission remains exact-identity, opt-in and read-only; production data paths remain relative to `data/`.

Validation records belong to `logs/pr333-main4602/`, including the exact candidate and public-data snapshot.
The solution build uses `--no-incremental --disable-build-servers -m:1`.
Core, Data and Gateway run serially with `xUnit.MaxParallelThreads=1` on the final commit.
The source snapshot contains 943 flat tables and 11 explicit generated public files, verified by SHA-256.
No client, archive, keys, accounts, native process or shared runtime is used in this refresh.
Historical native evidence above does not certify this candidate.

PR328's frozen `a90fe57497b4de4749ae36510dd8c77bd5553b55` is a reference only and is not imported.
The parent owns private integration in `328 ? 333 ? 339` order.
It must reconcile Rampage with strongest timed/smoke Blind and Harbinger's elapsed-rate and damage-routing contracts.
The older private composition is historical evidence, not proof of the refreshed sequence.

## Historical main 595833c candidate

This refresh preserves the previous frozen `35d3e72f` candidate and adds current main through one consolidated commit.
Its sole parent is `595833c52c615c2439f512be5ed76466b71a25fc`; no Behemoth or Harbinger implementation is imported.
The final Dexterity Mana coefficient joins main's resolved flat pool sources once.
The no-Intelligence Mana stat reuses main's inherent-grant flag, including explicit zero/off source restoration.
Per-level and attribute-group inputs, narrow/broad prohibitions and odd-Intelligence conversion precision remain intact.
Power of Purpose retains current slot-specific defences, rounding and final Intelligence inputs.

Main's Life/Mana/ES leech shares the existing resource writer with Recoup and flask recovery.
Earlier projectile contacts settle before that boundary; pool-full trimming and instant recovery flags remain intact.
Lethal Ignite clears all recovery, spending progress and Blind before publication and skips same-frame Mana recovery.
Temporary loading discards earned Recoup, including subpoint residue, while retaining unexpired grants and spending.
Failed transfer resumes without paying loading time; successful transfer captures carried state before retirement.
Main's late equipment-publication guards, source snapshots, selected-skill caches and whole-item requirements remain.

Hybrid flasks retain independent Life and Mana durations, rates, queues, expiry and native buffs.
The Mana component alone receives the accepted `ManaAlsoLife` snapshot and copies its continuous elapsed recovery.
Admission checks that actual component before spending charges, using captured level and modifier inputs.
Immediate and instant-at-end Mana do not copy; source changes while charge output waits affect only later uses.
Ordinary full-pool refusal and successful `0x263` results retain main's authoritative resource corrections.

Blind adjusts calculated ordinary, selected and offhand accuracy profiles once, after their own sources.
Main's shared defender entropy and critical-confirmation checks remain; target Evasion resolves at contact.
Bow/wand and gem releases keep effective Accuracy, Blind, critical, leech and local-source snapshots.
Gem releases retain `ClientPredicted`; no extra evasion roll replaces the current contact transaction.

Automated evidence is recorded under `logs/pr333-main595/`, with final source, tree and public-data hashes.
The 954 ordinary public inputs are the existing 943 flat tables and 11 explicitly named generated files.
The final solution build and serialized Core/Data/Gateway suites run on the consolidated source.
No native acceptance or client/runtime access was performed for this refresh.
Historical native sessions above do not certify this source or the parent's later combined sequence.
The parent owns exact private integration with Behemoth `5c53e2b850615609d534afa800526fc22cf3fb9d`, then Harbinger.
Strongest timed/smoke/Rampage Blind and owned-weapon consumers remain private integration work.
The parent also owns their combined presentation.

## Instant full-Mana review correction

Independent review found that instant Mana leech could fill Mana and leave earned fractional Recoup until the next tick.
Spending Mana before that tick let the old 0.2 combine with a later 0.8 and incorrectly recover one Mana.
The correction settles and discards earned Recoup inside `TrimLeechLocked` when the current unreserved Mana cap is full.
`LeechOnHitLocked` now invokes that same seam after instant recovery, retaining existing leech full-pool policies.
Instant flask and block-gain commits already use the seam; no resource-writer or flask adaptation is duplicated.
Pending whole Recoup is also discarded rather than paid into an already full pool.
Future grants, their remaining durations and Mana-spend progress are retained; non-full instant leech does not discard.

Deterministic regressions use production instant-leech, instant-flask and block commits before actual skill payment.
They inspect settlement immediately after the commit so a later stat read cannot conceal delayed cleanup.
Six full-pool cases fail on frozen `1fc4f710` with retained 0.2 or 3.2; the non-full control passes there.
The corrected candidate's build, serialized suites, TRX and source/data hashes are recorded in `logs/pr333-main595-r2/`.
This bounded correction changes only the leased leech file and Whisperer-specific tests/documentation.
Main remains pinned to `595833c`; Behemoth and Harbinger implementations remain outside the standalone candidate.
No new native acceptance or shared runtime access is claimed.

## Current-main maintenance refresh

The standalone refresh preserves frozen `fd13e3a7` and consolidates Whisperer over exact main `704bf1d2`.
The only textual conflict joined the monster's separate timed Blind and Ignite state; both remain.
Main's weapon qualification, skill restrictions, model refresh, aimed projectile height and bench fixes remain intact.
Clean release merges retain one `ProjectileOffense` identity with separate Accuracy, Blind and Ignite snapshots.
Final hand Accuracy aliases stay outputs; selected profiles apply Blind once and preserve critical confirmation.
New Burning Arrow and Fireball regressions capture both effects, change the caster's sources, then land the old shot.
They verify that later releases see the new sources while the first still applies its captured Blind and Ignite.
Existing Ignite history, evasion entropy, typed contacts and weapon-restriction regressions remain unchanged.

The instant full-Mana correction above still settles whole and fractional Recoup through the shared trimming seam.
Typed Ward, ES and physical Mana routing, continuous flask Life copies and full-Mana admission remain intact.
Current leech policies, late stat publication, selected-skill caches, loading discard, carry and retirement remain.
No Behemoth or Harbinger implementation is imported; the parent owns their integration and PR publication.
PR333 has received the updated class code; related class heads still need their declared sequential integration.

Tested source `3d284712` builds with zero warnings/errors and passes Core 404, Data 34 and Gateway 3477.
The serial suite receipts are in `logs/current-main704/`.
Earlier source/data proofs remain in `logs/pr333-main-fc202a95/`.
The solution build uses `--no-incremental --disable-build-servers -m:1`; suites use `xUnit.MaxParallelThreads=1`.
The same 943 flat tables and 11 explicit generated public files are ordinary local copies, checked without data writes.
Production paths remain relative and Gateway tests retain the exact shared `TestEnvironment` initializer.
Fresh client source `7698ec86` combines tested `fc03f113` with the separate PR388 password-controller fix.
Its production C# and tests match `fc03f113`; the later main update adds only monster rarity glow and its tests/docs.
The loaded service paths, versions and hashes are pinned in `pr333-native-current/logs/native/`.
Actual class selection, the native graph selector and six connected nodes passed.
The physical-hit step stopped on the server's entry-protection immunity; damage and flask checks were not accepted.
The fixture must perform an ordinary action to end protection before repeating those checks.
No production immunity bypass or combat change was made in response to this fixture failure.
Only this run's client and services were stopped; its initial runtime was empty.
Historical full-class sessions remain separate evidence and do not certify current related-class integration.
