# Roadmap: Skills, auras, stats and combat

Part of the [Roadmap](../Roadmap.md); what already works in this area is in
[status/skills-and-combat.md](../status/skills-and-combat.md).
Items keep the Roadmap's priority groups, and are in rough priority order within each group.

## 1. Quality of life

Aura intent persistence, town suppression, own-aura restriction (#304) and death reset are implemented.
Own-aura restriction has native paired-buff, bonus, reservation, relog and death controls.
Validate native grey-icon prediction and broader town/combat/character-switch round trips.
The suspend/restore lifecycle is explicit LocalExile policy; see protocol/11-skills.md sections 10c and 13.

## 3. Gameplay

- **Fortification extensions:** the hit-generated system is implemented and native-verified separately from Behemoth.
  Other conditional sources, permanent grants and specialised monster ailment thresholds remain separate work.
  See [Fortification](../docs/game/fortification.md).

- **Ordinary aura activation:** nine previously rejected self auras now share reservation, presentation and the
  supported stat/damage paths.
  Remaining work: Discipline's ES pool, Pride's enemy debuff, conditional consecrated ground, herald/minion/banner
  mechanics.
  See protocol/11-skills.md section 13.
- **Aura caster art:** the distinct EPKFiles2 visuals are selected on activation and area restoration;
  the live source-only regression is addressed with a separate received buff for the top-left icon, awaiting
  confirmation.
  Radius-scaled aura rings, if added, still need their actual radius.
- **Completed area fix:** Cyclone's `/set_aoe` final radius is now sent to the client and shared with hit selection;
  see Status and protocol/11-skills.md (live check pending).

### The player's real stats

- **Known:** client stat key = Stats.dat row + 1 ([11](../docs/protocol/11-skills.md) §8); `/set_stat` takes names.
  The server publishes its authoritative final values; the attack's are computed in `SkillCatalog.Unarmed.Stats`.
  The client has derived-stat evaluators, but its network getter honors explicit overrides, including zero.
  Omitted keys read type stats; native update transactions can publish derived outputs there.
  Ordinary network Actor/skill providers and same-key update/notification behavior are statically bound.
  Supplying a raw network input does not establish native recomputation of omitted derived outputs.
  See [client damage research](../docs/re/client-damage-calculation.md); do not rely on automatic raw-stat derivation.
  Mana per complete group of final Strength now has native raw-source/gear controls and automated pool coverage.
  Raw-source reservation refresh and clamping have decrypted wire coverage; recheck active auras natively.
  See [Status](../status/skills-and-combat.md) and [11](../docs/protocol/11-skills.md) section 4.
  Mental Conditioning now combines mana per Strength with mana-to-armour conversion in current-main tests.
  Historical joint native conversion controls passed at `c9cd1864`; Phrecia selection stays separate.
  Ordinary one-use cooldown recovery now shares the native base/rate and a server base-time budget, with atomic
  preflight before action side effects.
  Initial Dash recovery, early raw rejection, pause and native mid-cooldown rate changes pass live alongside
  layout/send-gate checks; remaining base progress is retained without consuming another use.
  Remaining cooldown work includes multi-use recharge, shared groups, bypass, typed/conditional/more recovery,
  official tick boundaries.
  See [11](../docs/protocol/11-skills.md) §9.
- **Spell cast timing (#192):** native base milliseconds are preserved and cast speed adjusts server waits and
  displayed rate once. Automated checks and real-client -80%/+100%/zero clip durations pass.
  Projectile release/contact alignment was not separately measured.
  Instant casts and attack timing stay separate; conditional action-speed and special native mechanics remain open.
  Attack gems now share level-aware inherent speed with server pacing/contact and native total-duration/APS stats.
  Native input verified the ordinary 0.714 s bow duration and Burning Arrow 1.02 s; automated checks also pass.
  Attack-gem damage/DPS and conditional speed remain separate work.
- **Spell hit panel (#188):** selected-spell ranges now share combat's converted packet and hitless spells clear
  inherited weapon hit damage. Automated checks pass; verify selection, equipment updates and relog in the client.
  DPS remains a pre-crit/pre-mitigation average; crit weighting, integer-roll expectation, cooldowns and DoT/beam
  displays remain separate work. Quality conversion and existing modifier precedence are unchanged.
  Selected attack-gem main-hand ranges and pre-crit averages now share the existing converted combat packet.
  Native selection, added-damage refresh/clearing, server hit and relog checks pass alongside automated tests.
  Equipment UI refresh remains to be checked separately.
  Off-hand alternation, conditional damage, supports and modifier-scope corrections remain separate work.
- **Warcry restriction (#297):** shared actor admission has regression and seven real-client controls in Status.
  Blocked input, source clearing, paid retries and an accepted cry's release are verified.
  See [11](../docs/protocol/11-skills.md) section 10b; broader warcry engines and Phrecia enablement remain separate.
- **Done when:** life, mana, attributes, resistances and their derived values come from the server's own calculation
  (Characters.dat base values, level, items, passives) and are sent in the Stats component.
  The overcapped resistances' derived evasion and critical increases now follow that rule and are native-verified.
  Generic increased attributes have historical native raw-source, derived-pool and requirement controls at `60044ad`.
  Current-main mana-per-Strength, requirement and inactive-weapon composition has automated coverage.
  Eleven rebased native controls verify final Strength mana groups, increased mana once and ordinary gear requirements.
  Ordered joint native controls at `c9cd1864` verify conversion after final Strength and odd Intelligence.
  Remaining native composition includes own-item percentage retention, cascading equipment, passive refunds and swaps.
  The fractional algorithm remains a documented PoB server model; official-server parity is not established.
  See [Status](../status/skills-and-combat.md) and [the calculation model](../docs/protocol/11-skills.md) section 4.
  Phrecia play compatibility remains a separate prerequisite.
  Power of Purpose has historical original-head raw-source, armour/mana, actual attack-consumer and relog controls.
  Native idle reservation, Iron Reflexes and MORE compositions remain; their model and automated tests are in
  [the calculation notes](../docs/game/power-of-purpose.md).
  Combined increased attributes, mana-per-Strength and conversion have regressions on base `939b44fe`.
  Joint native controls at `c9cd1864` remain historical; the rebased candidate needs separate native acceptance.

### Auras: reservation and effects

- **Known:** `GrantedEffectsPerLevel` carries the reservation; the Life pool's reserved field and S→C 0x1b1 update it;
  Purity of Elements/Fire/Ice/Lightning, Clarity, Vitality, Precision and Haste are implemented with their buff
  visuals and `/auras`, awaiting live verification ([11](../docs/protocol/11-skills.md) 13,
  [Status](../status/skills-and-combat.md)).
  Own-aura admission and suspension have source/dispatch/send-gate/lifecycle coverage, including Rage refreshes.
  Fifteen native controls verify installed pairs, actual bonuses/Mana, refusal, restoration, relog and death.
  Native grey-icon prediction and persistent restricting equipment remain separate verification.
  Duplicate-instance support context and queued ordinary acknowledgements have separate regression controls.
  Complete Lone Messenger, party auras and unsupported curse/enemy/banner/Vaal executors remain separate.
- **Start:** the auras' 3D effects (BuffVisuals EPKs of the `IsSkillBuff` rows do not render, unlike Righteous
  Fire's); the combat auras (Anger, Hatred, Wrath, Zealotry, Pride), Heralds, banners, curse auras and
  Determination/Grace/Discipline's armour/evasion/ES effects.
  The skill-bar indicator's buff association is implemented (0x1c1 bit3; live check pending, see Status).
  Support gems come with the gem system ("Gems" below); party and minion auras and life reservation with their own
  systems.
- **Done when:** the standard auras reserve correctly, their effects match PoB, and the character panel and HUD show
  the same reserved and unreserved values.

### Area-entry protection verification

- **Known:** Grace Period protection and the native timed buff are implemented outside towns and hideouts.
- **Start:** verify the 60-second countdown, first-step removal and damage prevention in the real client.
  The icon is visible; its corrected floating-point timer needs a live recheck after the upstream integration.
- **Done when:** the timer counts down and protection ends on movement, actions or expiry.

### Damage model (foundation done, deeper defence next)

- **Client damage research:** recover Hit and DoT calculations before proposing further server mechanics.
  Network Actor/skill input freshness and the distinct native transaction/startup boundary are established.
  Typed routing, cap/overflow boundaries, mode-dependent ticks and absolute assignment have static evidence.
  Reuse the [complete maximum-resistance equations](../docs/re/client-maximum-resistance-inputs.md);
  investigate actual freshness separately from the two recovered undeclared reads. Do not restage the formulas.
  Outgoing RF parameters and 14408/8198/5981 formulas are bound separately from incoming base rates.
  Resolve source instances into base rates and actual publication eligibility.
  Reuse [bleeding policy inputs](../docs/re/client-bleeding-policy-inputs.md): conditional Crimson Dance,
  replacement stack limit, minion-suppressed movement override and exerted aggravation chance are numerical inputs.
  Find an actual instance-selection/application consumer; the finite registration graph has no declared downstream
  consumer for the three policy outputs and does not cover indirect/inlined reads.
  Reuse [native DoT queue composition](../docs/re/client-dot-native-composition.md): 105 outputs now compose through
  original rank/dirty/dispatch instructions. Supplied eligibility/cache and local caps do not close instance publication
  or prove an official global cap; continue from actual source/target records rather than restaging these formulas.
  Reuse [the native getter-to-pool continuation](../docs/re/client-native-dot-pool-composition.md): record lookup and
  numerical loss now compose under supplied native provider binding. Actual actor selection and source publication
  remain open; avoid recreating scalar rate adapters for this already verified numerical boundary.
  Reuse versioned source-sampling/target-dynamic rules and native duration/faster compensation.
  Reuse the complete six source-duration modifier equations, including conditional sums and ordered binary32 arithmetic;
  do not rescan them as instance construction. Base-duration application and actual instance assignment remain open.
  Reuse the six additional skill-duration numeric stages and their source input units; do not flatten their intermediate rounding.
  Resolve actual 1192/3745/6188 override selection and instance assignment separately; the 6187/6188 UI literals are ClientStrings labels.
  Existing owner-stat-to-clock subscriptions and source-rate expiry publication remain separate open edges.
  Reuse recovered clock input transport/defaults and partial/replacement asymmetry; later recalculation stays open.
  Reuse [the retained replacement composition](../docs/re/client-buff-replacement-composition.md): actual temporary
  construction and field transfer preserve identity and some initialization inputs. Do not model it as reinsertion,
  reapply the local speed floor, or assume clock changes withdraw the independent explicit loss-rate override.
  Reuse [body-to-source attribution composition](../docs/re/client-buff-wire-source-composition.md): both retained
  paths overwrite the transported source, including zero; partial/replacement eligibility and extra context differ.
  Do not repeat this body/selector prefix or interpret it as strongest-instance selection, rate publication or final attribution.
  Empty Id2 for ignited/poison selects null in the audited startup catalogue, not an implicit default backend.
  Follow concrete numerical observer/script leads; do not repeat that closed factory/registry question.
  Reuse actual Buff observer payloads, lifecycle program storage/invocation and selected presence predicates.
  Initial granted values share the recovery gate; reuse their byte-order and prefix-preserving storage contracts.
  The attraction-shape observer has no-op gain/loss callbacks; do not reopen it as a numerical publisher.
  These contracts do not yet identify a numerical subscriber; do not expand unrelated presentation code.
  Reuse recovered outgoing ailment coefficients, faster/slower and critical-multiplier branches.
  Reuse complete MH/OH/spell chance builders and the selected defender modifier composition.
  Reuse all twelve named bleeding, 56 ignite and 72 poison rates with their category/critical builders.
  Preserve implicit ignite tincture inputs, poison hand gates, wrapping totals and chaos-only always-output gates.
  Reuse the complete fire self-rate 8180 equation; its primitive source publication remains a separate open edge.
  Reuse complete physical/chaos self-rates 8179/8183, including transfer gates and the physical post-cap tail.
  Reuse thirty typed spell component equations and four life-cost/weapon-as-spell contributor builders.
  Reuse twelve final spell range equations, including ordered conversion terms and both special branch families.
  Reuse twenty ordered attack ranges and four hand totals; base 6172 and added 5761 have separate scaling.
  Preserve attack Lightning Purist minimum 0.5/maximum 1.25 and every chaos-path destination remainder.
  Hand selection stays with the projectile research; formula closure does not close actual hit-to-pool loss.
  Preserve the longest chaos-path remainder exception and signed integer total wrapping in comparisons.
  Preserve wand half-splitting, hand-cast/type gates and distinct integer/float32 boundaries.
  Fire intermediate-added reads active-Buff count outside declared dependencies; grants/refresh stay separate.
  These callbacks consume supplied bleed/ignite/poison rates; primitive instance publication remains open.
  Defender-conditional presence output 9189 groups 37 inputs; bind its numerical use before treating it as a resolver.
  The native cache receiver ABI is established; do not promote untyped offset matches into actual consumers.
  Reuse [native dependency ordering](../docs/re/client-stat-dependency-order.md) and changed-output propagation.
  Preserve actual duplicate declarations; neither the ordered queue nor declared edges guarantee every runtime read.
  IsApplyDamageFunctionStat selects guarded active-index maintenance for eleven on-hit side effects.
  Reuse its typed metadata consumer; actual function execution and numerical hit application remain separate.
  Three concrete Buff Stats subscribers update state/presentation; do not repeat them as clock-refresh candidates.
  Reuse typed ailment permissions, target acquisition gates and ordinary/critical-only chance inputs.
  Connect their real infliction consumers without assuming the chance sums perform a roll or create an instance.
  Keep effective critical probability distinct from per-event trials, confirmation and defender critical modifiers.
  Aura source metadata and versioned stack rules constrain authority without supplying the native rate publisher.
  Concrete Fireball/Melee contact and avoidance decisions are bound separately from panel formulas and feedback.
  Preserve Fireball base-versus-client factory scope and mode/kind filters; reuse parallel projectile research.
  Reuse received-result control admission and mask/duration rewrites; selected critical feedback is not a roll.
  Reuse [release/result/checkpoint composition](../docs/re/client-hit-checkpoint-resource-composition.md):
  actual ProcessResult can consume independent resource assignments at its three nested checkpoints.
  Preserve supplied ordered-mode/core/TLS boundaries; do not count checkpoints as frames or infer a hit amount.
  The retained Life integer candidate now belongs to [ArakaaliHide](../docs/re/client-arakaali-hide-life.md),
  with selected approximately-65% targeting and exact pool/rounding limits. Reuse that positive classification;
  do not repeat it as an unclassified Hit-damage lead or infer ordinary invocation from the supplied-state checks.
  The finite [24 direct setter-input sites](../docs/re/client-resource-setter-sources.md) are also classified;
  the new variable path is a guarded LegionBreakout target-Life floor operation with inlined pool policy.
  Seek a different actual numerical producer/receiver or stronger upstream binding; this is no indirect-write census.
  Actual wire/core production stays with projectile research; pursue missing numerical damage connections here.
  Bow/Wand specialization, equipment-to-setter updates and startup registry winners are bound.
  Follow numerical consequences only where a concrete extension output requires them; do not expand all rendering.
  Melee attachment/controller outputs and DefaultArrow's five component update targets are bound.
  Selected Entity task factories, timers and secondary subscriptions are bound separately from component updates.
  Reuse the selected action-start task's bound animation publisher/listener when a relevant trace reaches it.
  Selected Buff lifecycle/quantity listeners resolve to Ruin count effects/audio; reuse that closed boundary.
  Selected Sand Spitter source Actor callbacks and attachment outputs are bound separately.
  Owner clocks initialize at insertion; existing-instance network updates overwrite duration/speed (default 1.0).
  Separate any local owner-stat refresh claim from that established synchronization contract and damage rate.
  Flame Wall secondary Buff has a concrete polling override restoring captured duration from owner stat 12588.
  Reuse that selected path; general owner timing-stat subscriptions and existing-instance speed refresh remain open.
  Reuse the actual Buff capacity and exhausted-duration contract for reason-4 Life loss.
  Generic Buff contribution and source-rate publication remain open.
  Reuse the separate Buff resource forecasts and first-eligible Life-callback source context; neither publishes rate.
  Four Hit script subscribers have exact asymmetric contexts; trace their actual program consequences.
  Reuse the selected Ferrofluid source/capture/named-task cleanup; its render-pack block is not numerical damage.
  Life-to-Buffs guards and the decoded nonpositive-to-positive Life clear trigger are bound.
  Trace other guard producers and source-rate consequences separately from that assignment branch.
  GroundEffect kill/expiry now reaches the owner's conditional Buffs guard and a concrete lifecycle boundary.
  Reuse its typed parent/token ownership; numerical source-to-target publication remains a separate open edge.
  A named RF template declares a rate input; connect its actual instance and target to native publication.
  Keep reference-damage defence estimates separate from accepted-hit mitigation and resource loss.
  Reuse taken-as coefficients and armour applicability formulas; preserve their declared/actual dependency differences.
    Reuse complete additional physical/elemental reduction and physical-ceiling input equations.
    Preserve conditional qualifications, small ceilings and Strength of Blood's integer per-minute conversion.
    Seven physical reads are absent from its declared vector; do not assume that vector is exhaustive.
  PreloadFromStats and the selected Cold Snap backend have concrete resource/presentation outputs; do not reclassify
  them as numerical publishers or expand their renderer without a separate numerical edge.
  Reuse [penetration input equations](../docs/re/client-penetration-inputs.md); typed and hand totals already include
  generic/highest-uncapped contributions. Preserve separate event-qualified inputs and undeclared condition reads.
  Bind actual resistance selection, ignore/prohibition/penetration application instead of adding these totals twice.
  Connect accepted contacts to numerical damage/critical/mitigation and pool receivers; these edges remain open.
  Reuse [actual received-result/resource compositions](../docs/re/client-hit-result-resource-composition.md):
  missing-action ProcessResult can succeed with no current/carry/reservation change in the supplied empty signals.
  Current target ES and protects-Mana gate a sequence-derived control trial; resource assignment order matters.
  Do not repeat the exhaustive u16 trial or treat its duration rewrite as numerical damage.
  [Current Fireball Action lookup/callback/event cleanup](../docs/re/client-hit-action-present-composition.md) now
  executes for supplied states. Do not repeat that empty-event path; ordinary construction/ingress, nonempty
  numerical subscribers and the accepted-Hit numerical publisher remain separate next boundaries.
  Reuse integer pool cost/sacrifice paths separately; trace payment-gate provenance before claiming client prepayment.
  Their concrete numerical writes do not close accepted-Hit damage or DoT source publication.
  Reuse [terminal Mana-refund arithmetic](../docs/re/client-terminal-mana-refunds.md), preserving FireMortar scope,
  the separate reason-body ingress limit, fresh cost-stat reads and unsigned product widening.
  Reuse the [maximum-stat subscriber and mode gate](../docs/re/client-pool-maximum-refresh.md): the two bound
  client World implementations suppress proportional current adjustment, while Life alone clears carry.
  Do not replace that contract with unconditional rescaling or confuse it with accepted-Hit resource loss.
  Reuse [recharge-control and total-rate authority](../docs/re/client-recharge-rate-authority.md): active notifications
  can cancel positive delay and suppress reset, while the pool step integrates its separately read total recovery rate.
  Local timer expiry and the installed no-op active-state publication do not supply a missing DoT/Hit source publisher.
  [Parsed Stats-to-Life composition](../docs/re/client-network-rate-composition.md) establishes received total-rate loss,
  not source-instance publication. Preserve its extra full-step checkpoint and raw/final override distinction.
  Reuse [initial Stats byte composition](../docs/re/client-initial-stats-rate.md): the original component reader,
  compact primitives and subsequent Life loss are bound. Do not count duplicate pairs as additive DoT sources,
  or treat this selected component body as a whole creation packet or an observed official rate publisher.
  Reuse [the direct contribution map](../docs/re/client-native-contribution-callers.md): 103 retained calls in
  13 owners have object/argument boundaries. Graph-owned remove-then-rebuild and type/action contributions are
  separate from a concrete target-rate publisher; do not repeat this finite inventory as a new source search.
  Reuse [owner/instance clock composition](../docs/re/client-buff-clock-authority.md): actual insertion and
  selected owner notifications do not replace a received-instance refresh policy. Network speed zero bypasses
  the initializer floor; definition force-one still governs ticks. Find the generic actual refresh publisher
  without repeating these supplied-state controls or interpreting timer speed as DPS.
  Reuse [Buff lifecycle/rate composition](../docs/re/client-buff-rate-independence.md): parsed removal and
  eligible timer expiry do not clear the supplied explicit aggregate in the selected empty-lifecycle state.
  Separate instance synchronization from rate withdrawal; recover the source publisher and its actual ordering,
  without assuming empty subscribers, automatic rate clearing or a measured 33ms cadence.
    [Critical Link export](../docs/re/client-critical-link-export.md) positively classifies one critical-input
    consumer as three raw-value appends. Reuse its action/definition bindings; actual grant publication remains open.
    The completed bounded immediate-key navigation is not a whole-program absence proof or Hit numerical closure.
  Preserve [resource input read order](../docs/re/client-resource-read-order.md): saved recovery rates, later pool state,
  sequential Life loss and preloaded ES loss differ. Reuse [the composed ordered resource route](../docs/re/client-resource-checkpoint-composition.md):
  peer-established World checkpoints/parsed dispatch now execute the actual setter and later recovery arithmetic.
  Wire parsing, queue production and official checkpoint choices remain separate; do not repeat the peer's World audit.
  Reuse [AddBuff magnitude transport](../docs/re/client-buff-wire-magnitudes.md): the writer and reader share a recovery
  gate but use different length sources. Keep their cardinalities aligned; do not place non-recovery DoT magnitude
  in this omitted vector or confuse the separate counted vector with a proven DPS carrier.
  Follow other event configurations beyond GEAL/native listeners; selected player/Sand Spitter files have no handler.
  Extend the selected 22-source semantic catalogue through positive data/native links to numerical publication.
  Preserve its exact inheritance, repeated fields, mode and component distinctions; six scripts are catalogued.
  Prior keyword inventories are bounded searches, not full semantic audits; reuse projectile-side source findings.
  Reuse the pinned PR #373 comparison: `0x1b4` is a skill-stat update; private capture claims need source records.
  Bounded negative scans are not absence proofs.
  Prioritize server-usable calculations and original C/S contracts; full rendering and live parity are not gates.
  Track artifact hashes, exact anchors and evidence boundaries in the
  [research workbench](../docs/re/client-damage-calculation.md#8-research-workbench-and-completion-criteria).
  This is static research, not authorization for client execution or a server refactor.

- **Done:** hits are per-type `DamagePacket`s; conversion follows physical, lightning, cold, fire, chaos, including
  chained conversion, the 100% cap and gain-as-extra.
  DoT/ailment formulas and fractional ticks are in the same model, and per-type resistance mitigation works on
  packets.
  The default attack, Fireball and monster melee use packets and read the player's/skill's conversion stats
  ([20](../docs/protocol/20-damage.md)).
  Player hits on monsters take the target's base resistance per type (`MonsterTypes.Resistances` →
  `MonsterResistances` by the level's difficulty, `MonsterResistanceCatalog`/`HitMitigation`), with the hit's
  unconditional penetration subtracted from the capped value and no second clamp (`Penetration`): general
  per-element and elemental stats on every hit, attack-only variants on attacks, the cannot-penetrate flag
  suppressing the elemental ones; DoT and physical take none. Monster armour is still not modelled.
  The player's real life, resistances, armour and evasion are computed from level, passives and base equipment, and
  armour/evasion mitigate monster hits (Status: Armour and Evasion).
  Ordinary physical monster melee can now trigger native player hit recovery with matching server cancellation.
  Copied-save live checks verified walking, pending cast cancellation, released flight and a stun-immune control.
  Selected Antiquarian now has native Ward/Adrenaline, Turtle Shell, always-hit/Far Shot and shield controls.
  Whaletooth extends actual Enduring Cry recovery; see [its model and evidence](../docs/game/antiquarian.md).
- **Known:** both sides still have flat damage ranges ([11](../docs/protocol/11-skills.md) 5, 6);
  ordinary player ES absorption, regeneration and interrupted recharge are implemented and live-checked.
  Added damage has its first stat: the attack's added lightning gets
  `attack_skills_have_added_lightning_damage_equal_to_%_of_maximum_mana` percent of the maximum mana pool
  (Whisperer "we know all", [20](../docs/protocol/20-damage.md) §2b).
  Arcane Might's six modern contracts share attack/panel consumers, maximum independent grants and skill quality.
  Normal Crown and mastery lifecycle have automated coverage; their native acceptance remains pending.
  Earlier Whisperer raw-stat native checks do not establish this expanded composition's native behavior.
  Iron Will/Strength provenance and missing upstream conditional spell producers require separate work.
  Unsupported skill/variant execution and unrelated Crown leech remain separate; see
  [Arcane Might boundaries](../docs/game/arcane-might.md) and [20](../docs/protocol/20-damage.md) §2c.
  Steel Willed's attack increase from complete groups of final armour has shared regression and native coverage.
  Boolean chaos-hit armour has native raw-source Life-loss controls and shared incoming-hit regressions.
  Mixed monster packets and ES/block/suppression/Fortification have automated coverage; recheck them natively.
  See Status and protocol/20-damage.md section 2e.
  Rampager's raw spell restriction has hit/panel and RF regressions; eleven native controls are historical (#293).
  See [status](../status/skills-and-combat.md) for the verified consumer scope.
  Fresh ordered joint hit controls pass at `c9cd1864`; original RF, Ice Nova and relog checks remain historical.
  Rampager's other mechanics and class admission remain separate work.
  Recent-hit armour defence now composes with physical/chaos mitigation in actual and queued contact regressions.
  Full stat writers, including Rage, own auras and Mageblood, have expiry and encoded-pair acknowledgment regressions.
  Dirty aura/Fortification composition also checks decoded wire order, independent expiry and idle correction.
  Its eighteen original-head controls remain historical; fresh ordered joint controls pass at `c9cd1864`.
  Native cap/order uses one raw40 contact; four-contact timing and forced full-writer races remain automated coverage.
  See [the model](../docs/game/steel-willed-armour.md) for event policies and evidence boundaries.
  Partial armour percentages, other Behemoth nodes, class admission and suppression transfer remain separate.
  Fresh composed default-attack mana, penetration and critical-ignore checks pass at source `1366f48`.
  Verify the remaining supported-gem, modifier and bow combinations natively; retain their automated regressions.
  Unconditional penetration is integrated with base/mod resistance, fractional totals and target damage taken.
  Critical elemental ignore and non-critical penetration now compose with it on the same rolled packet.
  Rerun critical ignore against a rebuilt final DLL: the previous combined run used the preceding #225 artifact.
  Recheck the resolved bow-contact and target-modifier composition natively before claiming new combined proof.
  Special ES mechanics, conditional penetration, exposure, ailments and further added-damage/support effects remain open.
  Ordinary Frostblink's bounded ground-instant acceptance passed actual Q, retry and invalid-request checks.
  Its hovered stationary-monster 0x191 path now uses the native size-aware object aim and object-bound confirmation.
  Automated acceptance/queued rejection checks and manual native endpoint experiments pass.
  Actual hovered Q reached native/server `(317,220)` with one object confirmation and one 12-mana cost.
  A framed early ground retry was refused without extra cost or confirmation.
  Production framed off-axis/far controls matched native/server endpoints.
  Framed moving-target, partial-terrain, missing-target and noninstant controls were refused without spending.
  Moving targets, coincident random aim and partial object-candidate terrain fallback remain outside this path.
  Production use during an active walk still needs measurement; native manual walk-stop experiments passed.
  Its departure/arrival damage, chilled ground, enemy-dependent recovery and shared Blink groups remain open.
  Extend terrain and movement restrictions from native evidence before accepting other instant movement skills.
- **Start:** special ES mechanics, exposure, conditional penetration and the remaining critical strike stats
  from the stats both sides already carry; monster skills beyond the first granted effect (ranged, spells).
  Extend player stun rules to ES avoidance, substituted thresholds, conditional defences and monster-specific modifiers.
  Spell base critical chance now comes from each skill's level and shares its panel/combat calculation.
  Native skill switching, combined bonuses, no-crit, relog and hitless-spell controls are verified.
  The overcapped-resistance modifiers (Sea Legs' flags: cold overcap as increased evasion, lightning overcap as
  increased critical chance) are implemented through the pools with automated and original-head native checks.
  Recheck the combined armed 830 +45% =1204 result and extreme signed cancellation on the resolved stack natively.
  Recheck Purity plus typed/all-element dev replacements and zero maximum overrides after the aura-source correction.
  The pure base overrides are in as a generic stat consumer (`override_weapon_base_critical_strike_chance` and the
  unarmed base in `AttackBaseCriticalStrike`): Slayer Overwhelm, Blind Prophet's Sightless and Behemoth Final Blow
  have native-verified base calculations, active-set isolation and cannot-crit gates (Status and
  protocol/11-skills.md §critical strikes).
  Recheck the resolved stack's shared half-up midpoint natively; the original head's native checks remain historical.
  Recheck development generic/spell increases and rounding live after the input-consistency review fixes.
  Deliver their nodes through the separate ascendancy lifecycle; Slayer's per-nearby-enemy crit multiplier is separate.
  Generic conditional rolls and the unarmed melee multiplier are implemented and native-verified separately.
  Continue native spell/projectile checks and the resolved critical-hit/Fortification composition on `effb797`.
  Verify successful paid Final Blow refunds once Phrecia play has an agreed client-compatible graph.
  Focus/curse lifecycles and statistical RNG validation remain separate from the deterministic policy tests.
  Added-base and other conditional/more crit remain separate work.
  Add support effects through `DamagePacket.Add`, `Scale`, `Convert` and `AddGain`.
  When owned-actor execution is implemented, supply actual-caster stats to `SkillDamageContext` explicitly.
  Do not inherit an owner's spell-damage restriction onto independent minions or daemons without a skill rule.
- **Done when:** damage numbers match the character panel and the monsters' levels.

### Incoming elemental ailment extensions

- Ordinary supported monster basic contacts now exercise ignite/chill/freeze/shock and final avoidance.
  Finish dedicated native buff icons and source/threshold/conditional-immunity parity before claiming a general ailment engine.
  Hostile projectile/spell adapters and alternative-ailment sources remain separate work.
  See [Gambler](../docs/game/gambler.md).

### Rage extensions

- Generic Rage generation, limits, installed buff/count, actual damage, decay, pause and lifecycle are native-verified.
  Remaining native composition includes supported projectiles, area sweeps, linked/active equipment sources,
  notification delays beyond the shared interval and slow destination loading.
  Recheck charge, equipment and command refreshes queued across expiry and separate decay-speed modifiers.
  The 140-damage/11-Fortification composition has automated accepted-contact coverage; verify it natively.
  Measure official capped-attempt refresh behavior and area-loading timer parity before replacing policy v1.
  See [26-rage.md](../docs/protocol/26-rage.md); Vaal costs and whole-class selection remain separate mechanics.

### Vaal execution and remaining soul checks

- Collection, bounded shared budgets and the proved Actor payloads (#308) pass automated and bounded native checks.
  Source checks on base `939b44fe` cover attribute/mana composition, supports, payment and current publication.
  The proved missing skill-record bit1 is implemented from raw Vaal metadata, with parsed and socketed regressions.
  Forty-two pinned 3.29.3b checks cover real-kill charging, the cap, cost changes, zero cost and prevention.
  They also cover nonlethal/friendly controls and unsupported input without action echo or resource loss.
  Remaining native acceptance covers duplicates, removal/readd, cooldown refresh and transient lifecycle.
  Verify held death notifications, Vaal Haste/Grace aura costs, death/resurrection and slow destination loading.
  Include attribute gear removal before inventory notifications; current automated coverage is not native proof.
  See [27-vaal-souls.md](../docs/protocol/27-vaal-souls.md) and Status for the explicit LocalExile policies.
  Vaal executors, payment, official distribution/capacity parity and the SGP countdown require separate work.
  Unsupported Vaal casts remain refused; Behemoth and whole-class enablement remain separate.

### Death screen variants

- **Known:** S→C 0x55 flags bit0/2/3 are mapped ([11](../docs/protocol/11-skills.md) §7);
  bit4 and the trailing u32 / u8 / u16 are not.
- **Done when:** Sanctum and other modes show their own options.
  Hardcore Exit (C→S 0x153) is implemented; death migration and permanent Ruthless death are tracked in
  [league modes](../docs/game/league-modes.md).

### Skill projectiles

- **Known:** the [framework](../docs/game/projectile-framework.md) owns production projectile routing and the
  only Core simulator. Catalog-known gems and ordinary bows have basic straight flight and resolved modifiers;
  [registered skills](../docs/game/projectile-skills.md) supply terminal Fireball blasts and separate KB clusters.
  Clustering converts quantity to explosions and forbids Split; the internal AltY row is a compatibility alias.
  Direct admission, independent explosion overlap and supported area scaling have automated runtime/input coverage.
  Chronological contacts, hit quotas, pause/transfer, release snapshots and held-output independence have automated
  production-input coverage, including raw item modifiers, scoped area damage and continuation/lifetime boundaries.
  Basic catalog flight does not implement all special skill behavior; unknown metadata is refused before costs.
  Earlier native packet/collision controls need retesting on the new path; actor positions still use integer walk
  estimates and shared projectile radii; Gateway actor radii are zero, making area coverage center-based even
  for large monsters. Calibrate combat body geometry against the available movement `ObjectSize` data.
  Live hostile projectile adapters remain future work.
- **Start:** user-run Fireball/KB checks from the skill guide, including return, terrain and overlapping explosions.
  Calibrate cluster radial distribution, counter-clockwise sequence timing and native art against client evidence.
  Build a server-side surface-height/world model for platforms and doodads (#182), height grids for more areas
  (or in the walk-map recorder), and collision with doodads and objects that have their own colliders.
- **Done when:** Fireball and bow arrows look like the real ones in every area: at the caster's torso on terrain and
  platforms, stopping at walls and objects.
