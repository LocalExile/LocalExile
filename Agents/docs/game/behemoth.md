# Behemoth

Behemoth is the Phrecia Berserker capability in
`Modules/PhreciaAscendancies/Classes/Behemoth`.
It requires that optional module in the connection's league, `Game_VERIFIED_BEHEMOTH=1`, and the reviewed
3.29.3b combined graph already installed separately.
The common module verifies archive bytes read-only; ordinary characters need no alternate graph.
Idols remains independent and unimplemented by this PR.
The extracted `Ascendancy` row identifies it as `Berserker`, ClassNo 2, Characters table row 0.
That character row is the Marauder, whose `IntegerId` and catalog `BaseClass` are 1.

This candidate reconstructs PR #328 over main `2a9904758c3a9fad0df7cb963c7450b8468a55e2`.
The original feature head `4c90a1f9`, atomic Vaal correction `74e3a1f8`, modular port `4d5f9bca` and
reload correction `adb1de26` remain historical source references on their private branches.
Their old foundation commits, deleted fixtures and intermediate Game-only admission policy are not merged here.
The solution build and two deterministic Gateway regressions are the bounded implementation checks.
Parent verification at `23ef5712` passed Core 436, Data 45, Gateway 6 and eleven bounded modular native checks.
The contact-accounting follow-up requires renewed exact-head checks; final revisions/results are recorded in the PR
and ignored validation receipts rather than inferred from an earlier head.
Historical whole-class/native results do not establish current whole-class parity.

## Capability and engine ownership

The optional module owns the Behemoth descriptor, opt-in, installed-graph admission, mode-aware allocated node
sources and graph selector through `PhreciaFamilyPolicy` and `AscendancyPolicies`.
Missing/disabled providers refuse optional admission without changing durable class progress.
No Behemoth identity, graph check or opt-in is added to Game or Gateway.

Reusable stat consumers stay in `Core/Game`: Fortification, Rage, Vaal costs, attributes, unarmed critical strikes,
armour, and the Rampage engine under `Combat/Rampage`.
Rampage runs from `melee_hits_grant_rampage_stacks`; it has no Phrecia class check or class-specific worker.
A connection without that source creates no Rampage pump, tier jobs or owned actors.
Other stat sources can use these engines independently of the optional class provider.

`AscendancyPolicies.SourceOwner` is a pure, lock-free provenance query.
It returns the host lifetime only for a live, supported optional selection in the connection's configured league.
It performs no graph verification, database mutation or provider-gate acquisition.
Ordinary and unascended selections return null.

A keyed Gateway `ConnectionWorker` binds opaque combat state to the Game load and optional source-provider load.
Both host lifetimes own callback-free Gateway cleanup resources containing connection/worker/generation identities.
The connection retains no old module callback or task delegate.
Retirement synchronously clears jobs, actors, pending strikes and Vaal reservations, removes owned monsters and
retains only primitive object/buff removal receipts for any still-live scene.
Old completion cannot release a newer lease, and inactive Game continuations cannot recreate owned state.
Worker activation claims one running pump; an idle pump releases its running claim without losing source ownership.

An independent Phrecia reload retires that provider's work and closes its affected optional areas under main's
conservative policy; the player reconnects to the new generation with durable nodes retained.
A Game reload uses its host lifetime, without replacing main's contract that keeps old game logic until replacement.
Ordinary connections are not closed by Phrecia retirement.
Death, area transfer and closing also retire transient combat state synchronously.
Effect mutation and send gates recheck the lifetime, source flag and effect epoch, including same-generation refunds.
Converted paid Vaal effects also stop if their conversion source is removed.

## Node consumers and legal builds

The target has nine notables and nine small Strength/Armour nodes.
Each small grants 3% increased Strength and 10% increased Armour.
Every notable path passes through Rampager, so its restrictions apply in every legal build.

| Node | Consumer |
|---|---|
| 49937 Rampager | Aura/warcry restriction, no spell damage, size and all thirteen Rampage tiers |
| 28815 Dire Rampart | Life regeneration per current Fortification |
| 37308 Tireless Frenzy | Maximum Fortification and attack speed per stack |
| 48586 Sheer Fury | Maximum Rage and maximum-gain proc |
| 38080 Mental Conditioning | Final Strength to Mana with existing Mana/Armour conversion |
| 13061 Fueled by Hate | Self-used Vaal attack soul-to-Rage cost, prevention and Rage-gain restriction |
| 10135 Final Blow | Unarmed base crit/multiplier and low-Life critical luck |
| 2103 Steel Willed | Armour-derived attack damage and chance for physical hits to ignore reduction |
| 29437 Incorruptible | Armour applies to chaos hit damage |

Three connected eight-point builds cover every notable and small node:

| Build | Allocated nodes |
|---|---|
| Fortification | 7128, 49937, 4561, 28815, 59943, 37308, 42923, 38080 |
| Rage/crit | 7128, 49937, 16671, 48586, 9053, 13061, 22167, 10135 |
| Armour/chaos | 7128, 49937, 42923, 38080, 24188, 2103, 34257, 29437 |

The consumers for shared stat keys already in main are retained, including per-hand equipment/local stats,
mode alternatives, stun/leech, source qualification and existing class drawbacks.
Class acceptance must exercise these legal allocations, paid refunds/reallocation, death, transfer, relog and restart.
Raw developer sources are useful engine controls, but do not prove optional class admission or node wiring.

## Rampage server model

Policy v1 grants one stack per accepted owner melee contact and one per attributed hostile kill, including both
on a melee killing blow.
Kill credit is idempotent per monster, and a streak expires after five seconds of game time.
Non-melee, friendly and inactive contacts return before reading player sources.
Eligible contacts and kills reconcile only Rampage state, reusing fresh caller/source decisions within one contact.
Later sweep victims resolve the current raw source after notification awaits, preserving refund/removal checks.
They do not rebuild full player stats per victim; movement changes still use main's action-speed factor.
This removes the reviewed repeated work; no measured throughput or latency improvement is claimed.
Pause holds that clock; successful area transfer carries only streak and remaining time once.
Old jobs, strikes, actors and projectiles do not transfer.
The native buff publishes count and duration, with increased damage/movement derived from the current streak.
Movement retains main's action-speed/ailment factor and applies Rampage changes at the contact or expiry time.

The thirteen catalog-backed plans execute owned daemon casts, circular projectiles, independent repeats/ripples,
corpse consumption, chained explosions, blind, interrupting stun and animated weapons.
Daggers attack a target, bows release real arrows, and swords sweep nearby targets.
Hostile monsters can attack those weapons; owned actors cannot be hit by their owner or saved as area monsters.
Daemon damage uses its own skill level and minion increases, independently of the owner's spell prohibition/Rage.

The explicit LocalExile geometry/timing is twelve circular projectiles, 120-cell range, 0.1-second ripples,
0.25-second independent repeats/weapon contacts, 20-cell corpse explosions and 40-cell fallback novas.
Main's projectile travel cap and composed action speed remain authoritative.
Actual table damage, counts, duration, repeats and radius changes remain catalog-driven.
Blind uses the data-scaled four-second duration and a 20% accuracy/evasion reduction.
Stun invalidates the enemy action generation, respects immunity and uses the data-scaled 0.35-second duration.
These are documented server models, not a claim of official scheduling or geometry parity.

All attributed kills retain per-league `GameEvents.Kill` dispatch, XP, drops and their existing mod hooks.
`KillContext.PlayerKill` filters player-only triggers/charges and stolen-mod acquisition for owned effects.
Vaal distance scaling is applied before main's outgoing-damage/rarity subscribers.
No previously captured Program callback substitutes for the current connection's league dispatch.

## Vaal admission

Vaal Ground Slam is the explicit supported Vaal executor: a 40-cell radial attack around the caster, supported
area scaling, weapon damage/effectiveness and linear close-target bonus from the skill's actual stat.
It uses main's current attack/contact timing, critical, mitigation, Fortification and Rampage pipelines.
Other Vaal executors remain explicit refusals before action echoes or payment, including zero-cost inputs.
Ordinary Vaal Ground Slam can pay its souls without a Phrecia capability.

Fueled by Hate adds raw GEPL souls to support-scaled ordinary base Rage cost, then applies generic/Rage increases,
final cost and efficiency with truncation and final half-away rounding.
Soul reductions and support multipliers do not reduce the converted portion.
Primary native cost references are `0x1428a8660` and `0x1428c3380`.

The admission critical section checks requirements, every resource and the shared attack deadline before action,
movement, cooldown reservation or payment.
That same admission commits payment and the next attack deadline before the native echo, including object flags bit0.
Execution consumes the paid reservation once and does not check or reserve that deadline again.
Approach-only checks do not reserve timing/payment; arrival rechecks under the original action token.
A later movement cancellation retains an accepted payment and prevents its hit.
Nonattack and instant cooldown paths retain their own pacing.
The interruption generation is captured with payment, preserving current main's action/contact cancellation semantics.

GEPL SoulGainPreventionDuration starts at admission on the pausable game clock and blocks the node's hit Rage gains.
Expiry publishes even if Rage itself is unchanged.
Death clears resources/reservation/prevention synchronously; successful transfer freezes remaining prevention
through loading and consumes its ticket once.
No transient combat resource is persisted in character saves.

## Validation scope

The two new deterministic Gateway guards use the actual catalog and optional module policy to check the Behemoth
identity, opt-in, league admission and Rampager source before exercising provider/Game generation retirement,
duplicate activation,
old-generation isolation, primitive actor/buff cleanup, transfer/expiry/death, and actual early Vaal object/ground
input followed by valid paid admission, duplicate rejection, movement cancellation and cooldown controls.
The Vaal regression fails when the atomic readiness check is removed.
The same Fact checks that ineligible contacts and explicitly sourced hit/kill accounting do not call player-source
builders, while preserving hit-plus-kill credit, idempotence and current-source removal over a stale snapshot.
They use explicit state/send gates and do not sleep or wait for real time.
They do not prove the native Rampage tier/actor presentation or every legal class build on this modular source.

The eleven native checks at `23ef5712` covered the Berserker choice/graph, Rampager allocation and size, a real
owner hit-plus-kill producing two stacks, independent Phrecia/Game reload closure and retained-progress re-entry,
eight post-reload melee kills dispatching tier 15 followed by natural expiry, paid refund and ordinary fallback.
Ordinary connections remained in the area across each reload and consumed distinct live Strength 777/778 inputs.
The tier-15 result is a server-dispatch log following native kills, not a complete actor-visual matrix.
Game reload resets developer damage overrides; the ignored harness reinstalls those controls before continuing.
Neither all thirteen tiers nor the full Vaal matrix was repeated in that bounded modular session.

Historical acceptance at `9657e0ad` covered all thirteen tiers, three legal builds, paid resets, actual Vaal payment,
pause, death, transfer, relog, restart and ordinary controls.
Later source `74e3a1f8` passed full suites and a bounded native matrix before the modular-main restructuring.
The old 160-focused and twelve admission artifacts remain ignored references under `logs/pr328-b690/reference`
and `logs/pr328-p2` with their exact source revisions.
Current source receipts belong under `logs/pr328-phrecia`, `logs/pr328-identity` and `logs/pr328-contact-perf`.
Final parent validation pins Gateway, Game and PhreciaAscendancies with Core/Auth/Patch/Host at its exact tested head;
the PR records those full-suite, review and bounded native outcomes.
