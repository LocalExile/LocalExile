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
  The server must send final values (the client derives none): the attack's are computed in
  `SkillCatalog.Unarmed.Stats`.
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
  Whisperer's 150% spell-increase transfer has shared attack regression coverage and native hit/panel checks.
  Remaining conditional modifier sources are separate; see [20](../docs/protocol/20-damage.md) §2c.
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
- **Done when:** Sanctum and other modes show their own options; hardcore's "Exit" works (with Esc → Exit, C→S 0x153).

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
