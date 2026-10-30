# Harbinger implementation and acceptance

Harbinger is the Phrecia Witch identity `Occultist`, revision `3.29.3b`; ordinary Occultist is separate.
The eight native notable names are `<<HRB31>>` through `<<HRB38>>` in the pinned data.
`Game_VERIFIED_HARBINGER=1` admits only that identity after read-only installed-graph verification.
The graph must already be installed; startup never writes client archives.
Native Labyrinth/Altar acquisition remains #151.

## Standalone refresh and evidence boundary

This published refresh adapts frozen Harbinger `59231e83` onto exact main
`704bf1d2c2763f71ac174e7aaa6bcbf727f63159`, with one standalone feature commit and no other class imports.
Earlier `b86ae94a`/`4602dd8f`, `595833c5` and original PR339 `e7d4cf43` retain separate historical evidence.
Main already contains Aristocrat #345 and crowd/channel #336; their admission and combat hooks are retained.
Native `FinalPlayerStats` retains `SkillBuildScope` caching and Aristocrat attribute consumers.
Server timing uses a separate unprojected snapshot so native Chill is never applied twice.
Connection-specific skill catalogs, gem requirements, wand context, Arcane Might and panels are preserved.
Companions retain population hooks and main's AI exclusion; hostile spawns retain main's rarity-glow publication.
Hostile spawns retain collision, emerge, projectile release metadata, speed rolls and monotonic contacts.
Curse walk speed multiplies the existing roll; action duration is captured at the accepted swing gate.
Focus receipts bind successful payment to the current action and channel generation.
Main's stage payments, gem revalidation, freeze/stun suspension and silence checks remain authoritative.

Automated regressions bind spawn rolls, companion exclusion, accepted swings, native caches and paid channels.
Standard/Ruthless timing and companion-only admission retain main's real-gem weapon/payment/release checks.
Both native stat paths retain the raw panel snapshot; dual-wield server timers apply action speed and Chill once.
Hybrid gates project live queues from the real writer boundary, preserving paid snapshots and BASE budgets.
Projection consumes no recovery/endpoints; leech/loading and converted-charge category-3 clocks remain separate.
Recovery commits reconciled ailment damage and synchronous Hardcore death before curse/companion publication.
Held-output regressions cover attached rosters, received curses and disconnect after lethal Ignite.
Utility flask start publishes current recipient stats, remaining base time and maintained Onslaught atomically.
Queued expiry/death/replacement cannot publish an extra buff after its original source has ended.
If expiry occurs during stat construction, the built stats-only packet still publishes consumed zero clears.
Outgoing Ignites share recipient BASE clocks; curse replacement/removal and late natural expiry split at the deadline.
Strongest/fallback DPS, mitigation and fractions remain unscaled; rate reconciliation retains damage for the writer.
Tested source `8415e0bf` builds with zero warnings/errors and passes Core 404, Data 34 and Gateway 3591.
The same 954 public data inputs were unchanged; receipts are in `logs/harbinger-mainfc/`.
Independent source review found no concrete P1/P2 defect in the changed Ignite, admission or publication seams.
Fresh native smoke below covers the Arcane companion; it does not certify the full refreshed class or other PRs.

## Actual graph and consumers

The implicit start is 18378; membership comes from the actual Phrecia graph rather than table ownership alone.
Every small node grants 15% increased Mana regeneration and 10% increased ES recharge rate.

| Node | Target effect | Consumer |
|---|---|---|
| 62155 `<<HRB31>>` | 50% Mana/ES recovery rate | Regeneration, timed Mana flasks, ES recharge |
| 20105 `<<HRB32>>` | Arcane companion | Owned actor and two recipient buffs |
| 41042 `<<HRB33>>` | Faster debuffs, slower buffs | Category-specific remaining-base clocks |
| 18859 `<<HRB34>>` | Focus companion | Paid channel gate and incoming effects |
| 3702 `<<HRB35>>` | 55% cooldown recovery | Paid budgets and native rates |
| 60867 `<<HRB36>>` | Time companion | Attack/cast/walk action speed |
| 34822 `<<HRB37>>` | Curse area, free Temporal Chains aura | Area and supported Blasphemy reservation |
| 39702 `<<HRB38>>` | Life/Mana as ES, 30% bypass | Final pools and post-Ward non-chaos routing |

Legal builds A/B cover all sixteen nodes; C adds three companions with the ES notable:
```text
A: 24174 20105 53334 62155 11043 18859 32223 41042
B: 1811 60867 23770 3702 58661 34822 63336 39702
C: 24174 20105 1811 60867 11043 18859 63336 39702
```
Full refunds cost forty points; failed saves retain both durable and runtime sources.

## Companions and owner clocks

GrantedEffectsPerLevel 50989/50990/50991 select varieties 5892/5893/5894 through alternate_minion 12/13/14.
Summons have one-second base casts, six-second cooldowns and one-of-each limits.
Owned actors have one Life, are immortal/non-hostile/non-corpse, and are excluded from saved monsters.
Buff binding uses GrantedEffects.SkillID and AddBuffToTargetVarieties.__col5, giving definitions 895/929/922/924.
Arcane grants 20% cast speed and 58% Mana regeneration, then 10% Lightning penetration; no guessed damage/shock.
Time grants `action_speed_-% = -10`; Focus grants ailment/stun immunity, curse suppression and 20% less damage.
Focus requires a paid live channel to start a cast; an already received buff keeps its own lifetime.
Buff casts take 700 ms; Arcane/Focus last four base seconds with eight-second base cooldowns, Time eight/ten.
Minion cooldown recovery is independent of owner recovery and pauses during that skill's source buff.
Following beyond twelve cells and metadata-order casting are local policies, not recovered official AIS.
Lease/source/epoch checks guard queued publication; loading/pause freeze clocks, death/logout clear companions.
Successful refunds remove sources; failed refunds and old-area releases cannot destroy a newer lease.

Mana recovery scales continuous regeneration/flasks once; costs, reservations and instant amounts are unchanged.
Extra ES adds 10% of final Life and 20% of final Mana before increased ES, without feeding derived ES back.
Focus runs before Ward; non-chaos ES bypass runs after Ward; eligible self-burning uses fractional bypass carry.
Action speed never changes projectile flight, cooldown recovery or recovery DPS.
`EffectClock` preserves base budgets and adds generic/category/Temporal Chains inputs before a 0.25 floor.
Buff masks select 1/7/13/16; debuff masks select 2/5/11/14; Temporal Chains excludes its own other-effects term.
Finite buffs carry remaining BASE seconds after current recipient stats at the same accepted send gate.
Category-3 charges retain minimum/infinite rules, negative losses and carry without buff-only scaling.
Taunt row 408 carries GrantedStats `[PlayerId, 1]` and required recovery words before trailing vector data.
Fortification, Enduring Cry, Adrenaline and flasks preserve budgets and unscaled DPS; Grace Period is category 18.
Ignite gate tests cover rate/expiry/death/replacement/identity/epoch, zero prefixes and later payment exactly once.

## Temporal Chains scope

A socketed gem self-casts; linked Blasphemy reserves Mana unless the notable grants zero reservation.
Refund restores real supported reservation; gem/support removal ends membership; maintained buffs are infinite.
Strength/intrinsic-level changes replace same-target snapshots; unchanged snapshots retain their instance.
Hexproof, immunity, reflection and Focus suppression have consumers; suppression retains a running self curse.
Expired unreconciled entries fail deadline checks; changing received rate preserves the remaining base budget.
Generic/curse increased duration add, support more-duration multiplies, then recipient duration/rate apply.
There is one stored curse per target/self, not a multi-caster strongest-Temporal-Chains engine.
Party propagation, other curses, monster spell AI and alternative ailment engines remain outside this executor.

## Current native smoke

Pinned client source `ccad428e` combines `8415e0bf` with the separate PR388 explicit-password controller fix.
Production C# and tests are identical to the tested class source; no authentication-server bypass is used.
Loaded service paths, hashes and versions are recorded in `pr339-native-current/logs/native/`.
Five bounded checks passed: class/graph selection, connected nodes 24174/20105, an owned Arcane actor with native
+20% cast speed and natural buff removal, a paid five-point source refund with fresh granted-skill absence,
and ordinary Occultist fallback with graph selector zero.
The run does not measure internal buff timer slopes, Temporal Chains/Ignite timing or all eight notables.
Only its own client and four services were stopped afterward; the initial runtime was empty.

## Historical native evidence

Historical runtime `4329deccff392af16d58ae3b30bf73a1ecaaf070` used main `0f49c07d`, before this adaptation.
Its build was 0 warnings/errors, Core 691/Data 18/Gateway 4063; source PR339 later ended at `e7d4cf43`.
Historical private composition `de790b8c17fce2263349cc8c2121c1df751b13f2` passed 695/18/4188.
Those old counts and private resolutions are historical evidence only.
Native logs `native-harbinger-main-20261029-011847` used `65d8e19f` with an isolated legacy-Auth fixture.
They covered paid channels, companions, travel/death/checkpoint/relog/restart, not current mode admission security.
`native-harbinger-charges-taunt-20261029-031649` used `4329decc` for nine charge/taunt endpoint/payload checks.
Earlier logs covered Arcane/Focus/Time, Mana/ES recovery, cooldown isolation, ordinary ailments and typed bypass.
`native-harbinger-owner-curse2-20261029-002457` covered suppression/refund; maintained-aura checks used `a464fb6d`.
Failed attempts remain failures; corrected updater guards and taunt payload results do not erase them.

## Parent integration still required

The refreshed code is published in PR339; exact related-head composition and its readiness decision remain separate.
Joint Focus → typed Ward → ES bypass → physical defensive Mana, recoup and unscaled flask-to-Life copying remain there.
Joint incoming Ignite/Blind fallback, recovery cleanup and Behemoth Vaal/Rampage checks remain private.
Retain prepared Rage input matching and synchronous Hardcore death persistence across those shared resolutions.
Historical private heads and standalone test counts do not prove compatibility of the refreshed combined sequence.
