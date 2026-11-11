# Status: Skills, auras, stats and combat

Part of [Status](../Status.md); what to build next in this area is in
[roadmap/skills-and-combat.md](../roadmap/skills-and-combat.md).

## Works (verified with the real client)

- Storm Call: each landing sets a marker that strikes when the duration ends and calls down the others.
  Docs: [11](../docs/protocol/11-skills.md) §9a.
- Holy Sweep's art: base `skill_art_variation` 2 plus the worn effect's value.
  Docs: [23](../docs/protocol/23-microtransactions.md) §3.
- Holy Sweep's area: its swings hit around the player. Docs: [11](../docs/protocol/11-skills.md) §9a.
- Flicker Strike: the server teleports the player to the target, with its charge speed and buff.
  Docs: [11](../docs/protocol/11-skills.md) §9a.
- Cooldown bypass: a use during the cooldown spends a charge. Docs: [11](../docs/protocol/11-skills.md) §9.
- Fortification from melee hits. Docs: [Fortification](../docs/game/fortification.md).
- Shield skills beside a one-handed weapon (`is_dual_wielding`), and Shield Charge's charge and shield damage.
  Docs: [11](../docs/protocol/11-skills.md) §10a and §10k, [13](../docs/protocol/13-items.md).
- Antiquarian's nodes. Docs: [Antiquarian](../docs/game/antiquarian.md).
- Conditional critical policy (#242): signed clamp, luck, unarmed multiplier and one roll per gem use.
  Docs: [11](../docs/protocol/11-skills.md), conditional critical policy.
- Generic Rage (#281). Docs: [26](../docs/protocol/26-rage.md).
- Mana per final Strength (#287). Docs: [11](../docs/protocol/11-skills.md) §4.
- Player Actor scale (#299). Docs: [04](../docs/protocol/04-player-entity.md#41a-player-actor-scale-299).
- Armour against chaos hits (#288). Docs: [20](../docs/protocol/20-damage.md) §2e.
- Increased attributes (#285). Docs: [11](../docs/protocol/11-skills.md) §4, [13](../docs/protocol/13-items.md) §2d.
- Power of Purpose (#291). Docs: [Power of Purpose](../docs/game/power-of-purpose.md).
- The default attack, and weapons whose requirements are not met leaving the hands.
  Docs: [11](../docs/protocol/11-skills.md) §4 and §10a.
- Combat timing: stationary attacks, wand bolts, monster flinches and the monster planner.
  Docs: [11](../docs/protocol/11-skills.md) §4–6.
- Impale. Docs: [20](../docs/protocol/20-damage.md) §3a, [11](../docs/protocol/11-skills.md) §14.
- Offence panel damage rows from the hit the server swings with. Docs: [11](../docs/protocol/11-skills.md) §14.
- Whisperer "your mind is destroyed": spell increases also scale attacks.
  Docs: [20](../docs/protocol/20-damage.md) §2c.
- Behemoth Steel Willed: attack damage per 450 final armour. Docs: [20](../docs/protocol/20-damage.md) §2d.
- Steel Willed incoming-hit armour. Docs: [Steel Willed armour](../docs/game/steel-willed-armour.md).
- Accuracy, the Accuracy Mastery's target conditions and attack evasion.
  Docs: [11](../docs/protocol/11-skills.md) §4–5, [20](../docs/protocol/20-damage.md) §2h.
- Armour, Evasion and Energy Shield from passives and equipment, Iron Reflexes and Chaos Inoculation.
  Docs: [11](../docs/protocol/11-skills.md) §4, [13](../docs/protocol/13-items.md) §2.
- Gem skills, their projectiles and triggered releases.
  Docs: [11](../docs/protocol/11-skills.md) §9–10, [25](../docs/protocol/25-gem-supports.md).
- Resistances, with the Kitava penalties. Docs: [11](../docs/protocol/11-skills.md) §12.
- Spell Suppression. Docs: [11](../docs/protocol/11-skills.md) §12.
- Movement speed and action speed. Docs: [07](../docs/protocol/07-latency-visibility-movement.md) §3.
- Block and Spell Block. Docs: [11](../docs/protocol/11-skills.md) §12.
- Endurance, Frenzy, Power, converted and Blood Charges. Docs: [11](../docs/protocol/11-skills.md) §15.
- Discharge and Discharge of Misery. Docs: [11](../docs/protocol/11-skills.md) §15.
- Character panel sums. Docs: [11](../docs/protocol/11-skills.md) §14.
- Character-panel leech limits and hit leech. Docs: [20](../docs/protocol/20-damage.md) §5b.
- Life and Mana regeneration, and Zealot's Oath. Docs: [20](../docs/protocol/20-damage.md) §5.
- Determination and Grace. Docs: [11](../docs/protocol/11-skills.md) §13.
- Discipline. Docs: [11](../docs/protocol/11-skills.md) §13,
  [20](../docs/protocol/20-damage.md), "Player energy shield".
- Buffs and Righteous Fire. Docs: [11](../docs/protocol/11-skills.md) §12.
- Warcry admission restriction (#297). Docs: [11](../docs/protocol/11-skills.md) §10b.
- Own-aura restriction (#304). Docs: [11](../docs/protocol/11-skills.md) §10c.
- Spell skills deal no damage (#293). Docs: [20](../docs/protocol/20-damage.md) §2g.
- Spells are released at the cast animation's contact keyframe. Docs: [11](../docs/protocol/11-skills.md) §9.
- Pride on enemies. Docs: [11](../docs/protocol/11-skills.md) §12.

## Partly working

- Ice Shot primary projectile resolution uses IceArrow; 3.29.3c protocol-driven primary-hit controls pass; cone splash remains open.
- Multi-strike skills: a gem attack whose StatSet carries `base_skill_number_of_additional_hits` repeats its whole
  sweep once per strike, so Double Strike (1) lands its second hit in the same use. Each strike is its own hit — its own
  target scan, hit chance, damage roll, on-hit effects and ailments — and shares the use's one critical strike roll:
  poewiki's Double Strike says the attack "can hit the same target twice", that "each hit is independent for the
  purposes of damage and on-hit behaviours", and that "each use of Double Strike does share the same critical strike
  roll for all its hits". Both swings of one use use the same weapon (`both swings from one attack will use the same
  weapon`), so the dual-wield off-hand pipeline is not involved. The server's current gem-hit path reads the main-hand weapon
  for both hits; per-use main/off-hand alternation is not implemented here. The second strike lands on the skill clip's own
  next contact keyframe, which the rigs give per class and weapon
  (doublestrike_sword: 300 and 666 of 1000 ms on the Str rig, 299 and 899 of 1133 on the Dex one), read from
  `data/animation-contacts.json`'s `contacts` key; without that key (an older extract) it is modelled half an attack
  later and the log says so. Spells, channels and Ground Slam's wave never repeat this way, and no other multi-strike
  skill is enabled: a clip's number of contact frames is not evidence that each one deals damage (Cyclone's `whirl_*`
  has two per loop and its channel already sweeps on its own cadence). Not implemented: the non-`base_` variant
  `skill_number_of_additional_hits` (usually from supports) is not read, as no source shows whether the two add.
  Automated tests cover the per-frame read, the Dex rig's different second frame, and the older-extract fallback.
  Code: `Combat.cs` (`AreaSkillHit`, `GemAttackStrikeGap`), `SkillMultiStrike.cs`, `AnimationContacts.cs`, `scripts/re/anim_contacts.py`;
  wiki [Double Strike](https://www.poewiki.net/wiki/Double_Strike).

- Combat load: message/frame and fresh projectile-release snapshots; input-checked skill/equipment caches ignore
  within-level gem XP and extended active recent windows; immutable item-local stats, condition parsers and projectile
  recipes are reused. Aura reconciliation compares live sources, restrictions, equipment and reservation inputs;
  support resolution shares raw gem sources, quest rewards follow flag changes, and projectile motion is reused only
  until the next contact. F1 NETWORK retains real input-queue latency; pathfinding and load spikes remain under investigation.
  Code: `PlayerStatsCache.cs`, `OwnAuras.cs`, `GemLevels.cs`, `ProjectileRuntime.cs`, `StatConditions.cs`.

- Survivors league and three optional modules, including the seven stage gate bosses; the catalogue keeps only skills the flight/pulse presentation can play (every projectile gets a visible fallback), ground area spells land on their target, the support pool is every normal support gem whose numbers the cast pipeline reads, and enemies are 5x tougher and 4x harder-hitting (ranged shots at 30%) with a DPS threat on life, nearby experience orbs, a first window that pauses before any spawn, 25%-of-life death zones and corpse cleanup; uniques persist between runs and bosses are 5x their ladder: [client presentation and limits](../docs/game/survivors-mode.md).

- Attack-speed admission: gem attacks now use the supported attack speed (gem and support stats plus the inherent
  GrantedEffectsPerLevel multiplier) for both the admission gate and the swing interval, instead of mixing it with
  `SkillAttackSeconds`. Tornado Shot's -20% and player/bow attack speed apply. Projectile speed adds the player's
  `projectile_speed_+%` to the skill's `base_projectile_speed_+%`. Automated pace and projectile-speed checks pass;
  native Burning Arrow numeric controls confirm integer final-speed rounding before duration, while broader native
  admission checks remain pending. Code: `GemSupports.Rules.cs`, `Combat.cs`, `ProjectileSpeed.cs`.
- Lifetap Support: supported skills pay their whole Mana cost as Life, including the 300% cost multiplier; the paid
  Life is accumulated against `support_lifetap_spent_life_threshold`. Reaching it grants the 4 s (plus quality) Lifetap
  buff, whose more damage applies to supported hits and whose 20% increased Life recovery applies to flasks.
  Vaal uses pay the same Life cost. The buff is presented through the own-aura send gate; the hidden spent-Life buff
  and minion buffs are not sent, and damage over time does not receive the more multiplier.
  Automated test covers Tornado Shot with level-1 Lifetap: 9 Mana becomes 27 Life, the first use grants the buff,
  and the hit and flask bonuses apply. Code: `Combat/Lifetap.cs`, `Player.cs`, `ConditionalSupportDamage.cs`;
  docs: [25](../docs/protocol/25-gem-supports.md).
- Blood Magic: `keystone_blood_magic` (the passive node and item-granted keystones such as Malachai's Awakening and
  Tukohama's Fortress) converts every skill's whole Mana cost to Life in `PayMana`, without Lifetap's multiplier,
  counter or buff; a skill whose Life cost is not below current Life is refused, and reservations stay on Mana.
  Automated test: Tornado Shot's 9 Mana becomes 9 Life with Mana unchanged. Code: `Player.cs`;
  docs: [25](../docs/protocol/25-gem-supports.md).
- Bow projectile counts: actual bow attacks now read `number_of_additional_arrows` and
  `number_of_additional_arrows_while_main_hand_accuracy_is_3000_or_more` on top of the generic projectile additions, so
  bow/quiver modifiers and passives such as Master Fletcher add primary arrows. The arrow inputs are gated to a real
  bow and do not reach wand, spell or melee projectiles; the selected-skill panel also publishes the arrow total.
  The terminal secondary group is a separate change (implemented in #549).
  Automated tests cover the bow/wand separation and the panel rows; no native check yet.
  Code: `GatewayProjectileModifiers.cs`, `GatewayProjectileDefaults.cs`, `ProjectilePanel.cs`;
  [RE count inputs](../docs/re/projectiles/count-stat-inputs.md) section 3.
- Tornado Shot terminal secondaries: each primary arrow pierces its contacts and emits three
  `TornadoShotSecondaryArrow` flights at its endpoint, spaced around the client's native terminal RNG ray
  (`[sequence, mother-derived byte, owner, repeats]`). The client predicts the primary and secondary arrows itself
  (Frida: seven local and seven server `TornadoShotArrow` objects for one use), so the server hides both and only
  simulates their damage; one secondary per set can hit an enemy, and the children keep
  support Pierce/Fork/Chain. Their range uses the client's 0.3 s secondary action lifetime (a Frida trace caps the
  final callback at 300 ms), so projectile speed scales it, and Point Blank/Far Shot distance includes the primary
  leg. Tests cover the profile, the client RNG vector, the 300 ms lifetime and the continued PB/Far Shot distance; the
  2026-11-02 live check confirmed the single primary origin, the fan and the damage.
  Code: `GatewayProjectileSkillProfiles.cs`, `ProjectileStages.cs`, `ProjectileTypes.cs`, `Projectiles.cs`;
  wiki [Tornado Shot](https://www.poewiki.net/wiki/Tornado_Shot),
  [RE terminal geometry](../docs/re/projectiles/tornado-geometry.md).
- Kinetic Bolt (`ZigZagProjectile`): fires `Metadata/Projectiles/ZigZagWandProjectile` (`SkillCatalog.ProjectileFallbacks`),
  converts theoretical forks into zig-zags (`virtual_projectiles_cannot_fork = 1`) and publishes
  `kinetic_bolt_number_of_zig_zags` in the skill record.
  Primary bolts travel 15 cells, then alternate +-45 deg (10 cells) and +-90 deg (20 cells) on leg end, non-evaded
  enemy hit or wall hit without spending Pierce/Chain/Return, emitting `ZigZagSecondaryWandProjectile` with shared hit
  history (`HidePredictedVisual = true` when client-predicted); remaining Pierce/Chain/Return resume afterward.
  Live checks verified casting, multi-turn monster hits and wall shots in Predictive mode; Fork, Returning, AltX,
  Ballista Totems and Volley parallel geometry remain unverified live.
  Code: `SkillCatalog.cs`, `KineticBoltLegs.cs`, `GatewayProjectileModifiers.cs`, `GatewayProjectileSkillProfiles.cs`,
  `ProjectilePanel.cs`, `ProjectileRelease.cs`, `ProjectileRuntime.cs`.
- Petrified Blood: the 35% Mana reservation buff, its hit-only Life-loss split at half Life, the four-second deferred
  loss, the non-flask recovery cap at Low Life and the Life cost above it are implemented. Life leech instances and
  the client's leech display keep the full maximum-unreserved-Life cap; they are not removed at Low Life, but their
  actual recovery is capped like regeneration and Recoup. Recoup is based on the hit before Petrified Blood and its
  four-second instance also keeps running at Low Life; a level-21/20-quality gem's 30% deferred share is offset by 30%
  Recoup as Life over the same four seconds. The invisible life/mana/energy-shield leech buffs are presented with the
  full per-minute rate, so the client can draw the leech overlay; only the actual pool update is capped. The deferred
  rate is presented with invisible BuffDefinitions 1796 (`slow_life_loss_from_hit_damage`), drawing the dark life-orb
  overlay for the loss still owed.
  Low Life uses the shared 50% default (`low_life_threshold_%_override` then `low_life_threshold_%`), not a fixed
  half of maximum Life; the 3.29.3b Life mastery `Life4` sets the override to 55%. The client's own
  `life_recovery_per_minute` is published as 0 inside a 250 ms regeneration margin of the cap and restored farther
  below it, so its pool prediction cannot cross the boundary.
  Activation, deactivation, saved intent across areas, death cleanup and the own-aura/received-buff presentation share
  the existing aura send gate; the buff is not an aura and the aura restriction leaves it alone.
  Not implemented: the buff-specific reservation-efficiency rows, the client tooltip's Life cost, and any non-hit
  Life loss (damage over time and the already-deferred loss are not protected).
  Automated tests cover the split, level-1/level-20 deferral, the threshold, the recovery cap (including a leech
  instance retained above Low Life while its recovery is capped), and the Life cost; no native check yet. Code:
  `Combat/PetrifiedBlood.cs`, `Combat/LowLife.cs`,
  `Modules/Core/Game/Combat/PetrifiedBlood.cs`; docs: [20](../docs/protocol/20-damage.md) section 2i.
- Shared Mana/Recoup and Blind: actual Whisperer nodes feed generic Game engines through the optional module.
  Typed hits preserve Ward/ES ordering; accepted skill/Vaal spending, continuous flask Life copying and Recoup
  share the existing resource writer, with synchronous lethal-Ignite cleanup before Mana recovery.
  Timed/smoke/passive-aura/Rampage Blind use one strongest value across ordinary and owned contacts and presentation.
  Two deterministic Gateway Facts cover resource, paid-refund, send-gate, transfer and retirement composition,
  including distinct-Game utility expiry/End payment and recovery-flask admission before blocked output.
  Historical `57053948` passed native 36 controls on `cc0f9c34`; the combined newer main needs fresh exact-head acceptance.
  Its current minion/link/footstep roles retain main's cadences and share generation ownership with the resource writer.
  Accepted minion expiry and player/owned-kill gameplay precede awaits; neutral output survives replacement.
  Queued quest/tree/equipment projections retain newer progress; accepted charge events leave live periodic time intact.
  All charge-buff consumers project and acknowledge at the send gate, avoiding stale buffered ticker output.
  Deferred quest kills retain synchronous phase/reward output; freeze and held-move admission share the cipher gate.
  Grouped deterministic controls cover their pending and accepted output across distinct-Game replacement.
  See [Whisperer](../docs/game/whisperer.md).

- Selected supported-spell panel: final linked-support damage replaces both spell and inherited hand ranges,
  clearing removed types and updating pre-critical average/DPS from the supported cast time.
  Eight pipeline controls cover damage, projectiles, added damage, gem level, type prohibition, link lifecycle
  and a hitless spell with an equipped weapon. Fourteen native captures cover twelve Standard-fixture stages:
  eleven positive formatter/owned-skill checks and one skill-removal check pass strict identity checks.
  Brutality and Righteous Fire have explicit zero hit fields and visually suppressed rows; their zero-row
  formatter evidence remains partial. Unlink/relink uses host removal and re-equipping, not in-place mutation.
  Relog restores the baseline and exact item contents; original copied saves and stashes remain unchanged.
  Official critical-weighted DPS, damage over time and special skill mechanics remain separate.
  Code: Gateway (`PlayerSkills`), `SpellPanelStats.cs`; tests: `SupportedSpellPanelTests.cs`.
  Docs: [11](../docs/protocol/11-skills.md) §9.

- Ordinary percentage attributes: typed and all-attribute increases act once on the complete class/passive/active-item
  flat bucket. Pools, equipment qualification and gem requirements share the resulting final attributes.
  Calculated zero attributes publish zero; inactive items contribute nothing, and a socketed gem's unmet requirement
  makes its host inactive again (main's rule restored 2026-10-29, [19](../docs/protocol/19-gems.md); the port had kept
  the host active). Forty-seven new automated cases and 63 selected checks cover both modes, real Utmost sources,
  signed/zero modifiers, dependent pools, source removal/recovery, final developer overrides and weapon swaps.
  Thirty-four native controls (17 per mode) verify final values, explicit zeroes, derived pools, item activation,
  source removal/recovery and active weapon-set persistence after relog. Ordinary Fireball input succeeds at INT 68,
  is refused at 62 against its 67 requirement, and succeeds after recovery (those controls predate the restored host
  deactivation: the host now goes inactive with its gem).
  Source/runtime hashes and original saves hold; isolated item contents/cells return to their canonical state.
  Supplied-source controls do not certify unique acquisition. Half-up fractional rounding follows pinned PoB; official-server rounding,
  signed equipment cycles, attribute conversions, conditional/more operators and mercenary gates remain open.
  Wide final evaluation does not claim arbitrary raw int-source aggregation is overflow-proof.
  Six ordinary pool coefficients use those completed attributes and actual level in the flat Life/Mana buckets,
  before final pool increases: signed `life_per_level`/`mana_per_level`, Life per ten Dexterity/Intelligence,
  per four Dexterity and per two Intelligence.
  Native -12/-6/-3 Life-per-level penalties subtract from inherent growth; attribute sources count
  complete groups once. Existing Strength-based Mana, ordinary halves and equipment ES-to-Life flats retain their order.
  One developer snapshot replaces whole coefficients, including zero; commands rebuild/clamp pools and publish the
  affected resource at the send gate, retaining Mana reservation.
  All 71 new cases (45 pure/native-source and 26 Gateway) plus 82 existing regressions pass: 153 focused, no skips.
  Full Gateway passes 3874/3874 with no skips; earlier unchanged Core 648/Data 37 from this session remain applicable
  (4559 applicable cases; only Gateway rerun after these changes). That run's hook records seven actual unobserved
  disposed-NetworkStream faults: six `MonsterKilled` → `Send` and one `NotifyMonsterHit` through the projectile-hit
  path to `Send`. Its 390 first-chance records are separate; no diagnostic errors/truncation/cap hit or new pool-source
  path appears. Passing tests do not imply fault-free background execution; new native checks remain pending.
  The existing final pool floor differs from pinned PoB's final rounding; official fractional parity is unverified.
  Real modifier/isolated passive fixtures do not prove acquisition, event-passive admission or full conversion nodes,
  and these six inputs do not complete the Life/Mana rows or conditional/more pool operators.
  Item ES per ten final Strength and Evasion per ten final Intelligence now join their ordinary INC buckets once.
  Native coefficients 1/2, completed attribute groups, slot/shield exclusions, IR and signed wide cancellation have
  33 pure and 14 both-mode Gateway cases; actual commands cover whole-source zero, ES clamping and no refill.
  Fourteen fresh level-100 native controls (seven each in Standard and Ruthless) verify supplied-source publication
  and reload: ES 103/104/124/135/124/113/135 and EV 15/16/22/26/22/18/26.
  Six capacity increases retain damaged current ES in 487–749 ms; four removals clamp full ES to 124/113.
  Both fresh entries restore current/max ES 135. Completed World updates bind each target to its actual source action.
  Acquisition, full Shaper's Touch and Defence-row formatter bindings remain open.
  Inherent ES now uses complete groups of ten final Intelligence, correcting the former doubled `/5` term.
  Static native Intelligence help text agrees with pinned PoB; exact official fractional arithmetic remains unverified.
  These new controls cover the corrected totals, no-refill, clamp and reload; older native totals remain historical.
  Corrected recharge/full-ES conditions and native queued-send scheduling still need separate validation.
  Eleven new baseline cases and selected compositions pass in 216 focused cases.
  Full Gateway 3939 covers all updated tests, including the percentage-attribute and tick cases omitted by that filter.
  Unchanged prior Core 648/Data 37 remain applicable; no tests skipped.
  Retained diagnostic faults and native/persistence limits are qualified in the defence reference below.
  Five native prohibitions now remove only inherent Strength Life/melee, Dexterity accuracy/EV and Intelligence Mana/ES;
  the narrow Life/Mana flags overlap once with their broad counterpart. Final attributes, requirements and item group
  terms remain active. One whole raw developer snapshot supports zero replacements and affected resource publication.
  All 16 full native vectors retain their companion attributes and saved payloads in both-mode automated controls.
  Eighty new cases pass (64 pure/native-source, 16 Gateway); fresh full Gateway 4019/Core 648/Data 37 pass with no skips.
  Its hook records six actual disposed-stream `MonsterKilled`→`Send` task faults and 386 separate first-chance records;
  no new flag path or diagnostic error/truncation/cap hit is captured. This is not a fault-free-run claim.
  Native flag publication/reload and all-source immediate Life publication remain unverified; ordinary equipment Life
  still clamps on the existing recovery tick. Key18620 has no checked direct source and is not an IR alias.
  Code: `CharacterStats.cs`, `ItemModStats.cs`, Gateway (`CharacterStatSources`, `PassiveBonus`, `ClassAttributes`).
  Docs: [11](../docs/protocol/11-skills.md) §4, [13](../docs/protocol/13-items.md) §2d,
  [attribute-scaled defences](../docs/protocol/20-damage.md#player-energy-shield-2026-10-25).

- Weapon-local damage and timing: active weapons supply a typed packet with multiplicative physical quality,
  local flat/increased damage, no-physical/quality-prohibition flags and local attack speed applied once.
  Ordinary melee/ranged and supported attacks share the actual context; final per-type rows clear removed types.
  Automated controls cover weapon swaps, inactive hand-local exclusion and shared conversion/Rage composition.
  Pool/charge/timing conditions and shield base block also exclude inactive or disabled equipment.
  Twenty-four native publication controls pass in Standard/Ruthless; the independent local-mod/quality tooltip
  matches 36-70 physical damage and 1.80 attacks per second. Source/DLL hashes and inventories held.
  Authoritative offhand alternation and alternative quality remain separate.
  Local critical flat-before-increase ordering has automated coverage in the critical-strike implementation below;
  the preceding native weapon checks do not validate that change.
  Supported-contact composition retains automated coverage and needs its own native check.
  A Core raw conversion-history seam is available with 21 new path cases and 12 existing packet controls passing.
  The full Core suite passes 648 tests. `PlayerHitDamage` now resolves one visited-type increased bucket, inherited
  support/aura more, final-destination prohibitions and common charge more across ordinary/supported hits.
  It preserves explicit contexts, source precedence, no-conversion floating grouping and ranged release snapshots.
  Forty-three new Gateway cases and 340 focused checks pass, followed by all 3602 Gateway tests and 37 Data tests.
  Thirty-two native numeric controls pass across Standard/Ruthless: 24 original explicit-field controls and eight
  ordinary-bow sparse-zero controls. The first sparse Standard baseline is retrospective; the next seven are
  prospective. Actual natural getter zeros are distinct from explicit wire zeros or hidden-row rendering.
  Four restoration/relog controls, 184 full item/socket acknowledgements, original-save preservation and 714 unchanged
  source/runtime inputs pass. The client is closed and normal `data/characters.db` restored. Native publication does not
  certify authoritative hit contact, critical-weighted DPS, conditional/endpoints/ailment/DoT or official rounding.
  Existing disposed-stream cleanup faults and separate native connection closures remain recorded in the evidence.
  Code: `ItemWeaponStats.cs`, `DamageStats.cs`, `SkillCatalog.cs`, `SupportedAttackDamage.cs`.
  Docs: [20](../docs/protocol/20-damage.md) §2a.


The generic [projectile framework](../docs/game/projectile-framework.md) owns production gem/bow routing and
the sole 30 Hz runtime. Catalog fallback provides basic flight; registered skill definitions supply special rules
and can require explicit recipes.

The [client projectile research](../docs/re/012-projectile-execution.md) now separates shared execution,
client/server input contracts and six ordinary witnesses from the current server implementation.
Final bindings, finite arithmetic, local contact/history, continuations and selected skill-stage joins are documented.
Public scalar/vector and synthetic observation checks are separate from retained original-instruction controls.
Gameplay TLS state/order, official Fireball direct/area result routing and actual effective-input timing remain open.
This is a research baseline, not new live skill acceptance or complete native/server parity.

- Arcane Might (#364) shares one highest-eligible-ratio consumer across current attacks and damage panels.
  Normal Crown of Eyes, Whisperer's existing consumer and wand mastery use the six modern stat contracts.
  Independent grants do not stack; skill base plus quality still adds before the maximum comparison.
  Weapon-local damage now scales in ordinary and supported attacks; restricted types are cleared from skill panels.
  Automated cases cover actual direct/area target Life loss, snapshots, mastery refund and Crown persistence/removal.
  All 24 inventoried numeric skill constants and three quality records have calculation/data checks.
  Those checks do not enable unsupported skills, variants, mercenaries or Phrecia allocation.
  Iron Will/Strength provenance, missing upstream spell inputs and unrelated leech remain outside this consumer.
  Native user checks confirm ordinary Crown ratios, KB non-stacking and Fireball isolation in the damage panels.
  KB's absolute damage exposed an effectiveness-offset decoding error; raw 4000 now resolves to 140%, not 40%.
  Corrected absolute damage and mastery lifecycle still need native verification.
  See [scope and coverage](../docs/game/arcane-might.md) and [20](../docs/protocol/20-damage.md) §2c.
- Ruthless resolves native skill/gem, passive and mod alternatives per connection, including zero-valued overrides.
  Ordinary characters in the same Gateway keep ordinary catalogs.
  Hardcore death is terminal for the old connection; a queued level-up cannot revive it.
  Hardcore migration and permanent Ruthless death have automated lifecycle coverage.
  Earlier native checks were against the original fork; native checks on the current integration are recorded separately.
  Native skill/passive/mod alternatives and late-action guards have automated coverage.
  Main's projectile kill path retains Ruthless rewards, gem XP and active utility-flask restrictions.
  Synchronous quest/gem commits retain the Hardcore terminal-progress guard; composed regressions pass.
  See [league modes](../docs/game/league-modes.md).
- Grace Period grants 60 seconds of entry protection outside towns and hideouts.
  First movement, a skill, a flask or expiry ends it; incoming damage, hostile targeting and RF damage are suppressed.
  The native buff uses floating-point seconds for its countdown; the icon was checked live, timer retest is pending.
  See [11](../docs/protocol/11-skills.md), "Area-entry Grace Period".
- Vaal souls (#308): genuine owner kills collect into bounded shared ActiveSkills-row budgets.
  Creation and skill updates publish the proved native Actor lists; automated wire/dispatcher checks pass.
  Duplicates, source/cost changes, removal/readd, range, prevention, queued sends and transient lifecycle are covered.
  Eligibility commits with lethal mutations before death notifications, including shared hits and Righteous Fire.
  Vaal aura costs use per-instance Aura types and accepted supports, with explicit developer replacement precedence.
  Player soul loss commits at zero Life; delayed death notices preserve actual resurrection earnings.
  Vaal gem/support requirements use current equipment snapshots while gear notifications are held.
  Source checks on base `939b44fe` preserve wide raw attributes, mana, pool flags and supported skill payment.
  Skill-record bit1 (0x02) now retains native Vaal soul inputs using catalog raw stored uses in both lookup paths.
  Parsed creation/update and real socketed tests cover zero cost/balance, ordinary partners and inactive hosts.
  A pinned 3.29.3b session passes 42 collection/counter checks: real kills, cap, cost, zero capacity and prevention.
  Native required-soul and manager consumers agree, including natural gem level-ups and unsupported-input refusal.
  The build and all three suites pass; exact private composition with PR #294 also passes.
  Distribution and capacity are documented LocalExile policies, not a claim of official algorithm parity.
  Native duplicate/removal, death/relog/transfer, aura costs and held-notification composition remain unverified.
  Unsupported Vaal casts are refused before action echoes or cooldown reservation, including effective zero cost.
  Vaal Ground Slam now has a shared executor, atomic soul/Rage payment and pausable prevention timer.
  Attack readiness commits before payment/action reservation, including object-targeted flags bit0.
  Other Vaal executors remain explicit refusals; the bounded modular native session did not repeat a full Vaal matrix.
  See [Behemoth](../docs/game/behemoth.md) for execution models and source ownership.
  See [27-vaal-souls.md](../docs/protocol/27-vaal-souls.md).
- Ground Slam now hits as the forward wave its data action names, not as one circle: the skills whose ActionTypes action
  is "DistanceScaledGroundSlam" (GrantedEffects GroundSlam, GroundSlamAltX, GroundSlamAltY, GroundSlamRoyale and
  PlaytestSlam) send a sector from where the player stands whose front advances to the skill's reach, each monster hit
  as the front arrives it and the rest of the sweep dropped on death, area change or a closed connection. Vaal Ground
  Slam's own skill is a different action, so its 40-cell radial blast is unchanged, as are Ice Nova's ring, Cyclone,
  Rolling Magma and every other circle. The player's gem resolves to GrantedEffects "GroundSlam"; the +40% final radius
  and 4 melee repeats belong to the old "ExileGroundSlam" row, which no GemEffects row grants. The tables carry no wave
  length, base angle or speed (the four `ground_slam_*` shape stats are held by no player stat set and no Mod of the
  3.29 table), so `GroundSlamWave` holds three estimates — a 60-cell length at the generic 12-cell area radius, moved by
  however far the skill's own radius sits from it; half angle 30 deg (half the 60-degree cone `ground_slam_cone_angle_override`
  names), grown by the gem's `ground_slam_angle_+%`; and 60 cells a second — to calibrate against a live measurement.
  The wave's close-range damage (`groundslam_damage_to_close_targets_+%_final`, real in the gem's own stats) is still
  applied by the Vaal executor only. A live session's 0x0024 object uses reach the same sweep, with the wave's origin
  already the post-walk swing position, so the gap between its object uses and its sweeps was swings whose hit the server
  dropped (22 of 80 swing echoes), not a missing area part. Client fact (measured live 2026-11-07): clicking a monster
  and immediately walking away plays the whole swing and draws the wave, so the client queues the Move instead of
  cancelling the attack; the server was dropping the paid hit on the action counter alone. A gem melee swing
  (`holdsItsSwing`: an attack that is Melee, not channelled) now keeps its hit over a moved action counter, and only a
  death, an area change, a pause, a freeze or a stun's hit recovery ends it. This was every gem melee attack, not Ground
  Slam alone (Cleave, Sweep, Ground Slam's alternate-quality forms and Vaal Ground Slam's own blast all take the same
  path), and each such swing's contact now comes from its own rig clip (`GemAttackContactDelay`: groundslam_maul's 0.633
  of the attack, where the class's attack1_maul, the default attack's swing, is 0.692) — both are logged with the sweep.
  Not yet checked with the real client.
  Remaining: the client queues its Move until the clip's cut-in (Lockstep.cs) while the server walks on the Move as it
  arrives, so the server's position leads by up to about 0.3 s during a swing; holding the Move until the contact
  (`HeldMove`) would align them, and needs `Messages.cs`, `PlayerAilments.cs` and a `ClientState` field.
  Code: `SkillArea.cs` (`GroundSlamWave`), `Combat.cs` (`AreaSkillHit`, `GemAttackContactDelay`),
  `AnimationContacts.cs` (`SkillAttackContact`).
- Warcry release (Enduring Cry, the only warcry executor): the cry's time follows warcry speed and action speed as the
  client's does, and its release runs the shared warcry effects within its radius: corpse explosions that consume the
  corpse and quest chests opening (Deathrattle).
  The explosion takes the player's global damage increases and conversion.
  Every one-use warcry, for every class (Enduring, Rallying, Intimidating, Seismic, Infernal, Ancestral, General's and
  Battlemage's Cry), now uses the generic one-use cooldown, with `warcry_cooldown_speed_+%` added to
  its recovery as the client adds it; the live cooldown checks under "Instant skills and cooldowns" ran before
  warcries took that path.
  Gateway coverage; not yet checked with the real client.
  Each exploding corpse shows a generic gore burst; the native art is not traced.
  Global flat added damage and flat crit chance (the wiki's Explode page applies both), on-hit effects and the
  client's 0.22 s wave delay are not modelled.
  Code: `Warcries.cs`, `UseChargeSkillCore`, `SingleUseCooldown.cs`.
  Docs: [11](../docs/protocol/11-skills.md) section 10i.
- Strike targeting: a melee Strike's spirit strikes (`melee_attack_number_of_spirit_strikes`) hit the monsters the
  client picks, mirrored from its scan and its seeded shuffle, for gem Strikes and the default attack.
  With `melee_strike_skill_strike_previous_location` a Strike also hits where its previous use landed, and the
  server sends the `skill_location_memory` buff with that first use, from which the client records the later uses and
  strikes the previous location itself.
  Gateway coverage; not yet checked with the real client.
  Not mirrored: the client's line-of-sight test and its choice among more than 20 candidates.
  Not for the Strike gems typed Projectile (Frost Blades, Lightning Strike, Molten Strike, Wild Strike and their
  transfigured gems except Frost Blades of Katabasis, which has no Projectile type and gets both):
  the server launches only their projectiles and has no melee hit for them, so the spirit strikes the client draws
  for them deal no damage.
  Code: `StrikeTargets.cs`, `Strikes.cs`.
  Docs: [11](../docs/protocol/11-skills.md) section 10d.
- Passive enemy auras: a passive's BuffTemplates enemy aura puts its buff on hostile monsters in its radius, and its
  hit conversion converts their hits (Born under Fire).
  Auras with stats the server does not apply yet are neither sent nor applied.
  Gateway coverage; not yet checked with the real client.
  Code: `PassiveBuffAuras.cs`, `MonsterSwing`.
  Docs: [11](../docs/protocol/11-skills.md) section 10e.
- Passive-triggered skills: Ancestral Commander's Spirit chieftain options trigger on a Unique monster's crit, Savage
  Hit or blocked hit, spend their shared 20 s cooldown and give the chieftain's Leadership buff for 12 s (with increased
  skill effect and minion duration), during which
  that cooldown does not recover; death ends the buff.
  Kahuturoa's share of armour against elemental and chaos hits and Kiloava's Life, Mana and ES on block act.
  Server monsters have no base critical strike chance, so Kahuturoa's crit trigger fires only on a Unique with a
  forced always-crit mod (`/mob_mods`), never in normal play.
  The chieftain rises beside the player as his minion and fights the monsters near him until the buff ends.
  Gateway coverage; not yet checked with the real client.
  Not modelled: the chieftain's skills past its first, the skills' records (the client shows only the buff), Utula's
  leech and Rakiata's Mark trigger (see "Not working yet"), and carrying the buff, minion or cooldown through an area
  change.
  Code: `PassiveGrantedSkills.cs`, `SpiritChieftains.cs`, `MonsterSwing`.
  Docs: [11](../docs/protocol/11-skills.md) section 10f, [20](../docs/protocol/20-damage.md) section 2e.
- Player minions that fight: a minion on the player's team, marked as his for the client, walks with him, goes for
  the nearest hostile monster near both, swings its first skill with his minion modifiers (its base hit from
  DefaultMonsterStats `MinionDamage`, the ally table), and its kills give him
  experience and loot; the instance save, his area hits and his on-kill charges leave it out.
  This first-skill controller serves Spirit chieftains; golems use their own controller below.
  Gateway coverage; not yet checked with the real client.
  Code: `PlayerMinions.cs`, `MinionAttacks.cs`, `RunMonsterAi`.
  Docs: [11](../docs/protocol/11-skills.md) section 10h.
- Life Recoup returns a share of each hit taken over the next 4 s ([20](../docs/protocol/20-damage.md) §5).
- Ancestral Commander defences: `damage_reduction_rating_from_body_armour_doubled` doubles the body armour's armour
  (and, with Iron Reflexes, its converted evasion) before the increases.
  `missing_unreserved_life_%_gained_as_life_before_hit` gains that share of missing Life before every non-evaded
  monster swing and dev hit, blocked ones included (Life reservation is not modelled).
  `movement_speed_cannot_be_reduced_below_base` bounds the movement speed total below by 0, as the client does from
  the same flag.
  Gateway coverage; not yet checked with the real client (move into the armour and movement speed entries under
  Works after a live check).
  Docs: [ancestral-commander.md](../docs/game/ancestral-commander.md).
- Aura restoration bootstrap fix: socketed gem requirements no longer deactivate their entire host item during pool
  initialization.
  Equipment contributes attributes before individual gem validation;
  saved auras remain candidates on town/hideout round trips.
  Regression covers fresh-connection Precision/Clarity equipment and all 17 supported self auras.
  Live retest pending.
- Aura lifecycle: towns suppress all aura effects/reservation while retaining per-character enabled intent.
  Exit/relogin/character return restores available remembered auras without rechecking cast affordability;
  manual activation still validates mana.
  Death clears saved/runtime auras; skill refresh resumes temporarily unavailable effects.
  The shared own-aura restriction has separate native controls above.
  Discipline's town, death and gem-host lifecycle also passed historical native checks in LocalExile.
  Fresh merged-branch and other aura-family round trips remain partial.
  See protocol/11-skills.md.
- Aura presentation (2026-10-25): self auras select their distinct EPKFiles2 art.
  A live check found that source-only buffs hid the top-left icons;
  source and received buff instances now coexist, restore and end together.
  Icon restoration awaits client confirmation.
  See [11](../docs/protocol/11-skills.md) section 13.
- Ordinary aura expansion (2026-10-25, awaiting client confirmation): Determination, Grace, Anger, Hatred,
  Wrath, Zealotry, Malevolence and Envy now activate with reservation and paired visual/HUD buffs.
  Armour/evasion, added hit damage, gain-as-cold, spell damage/crit and RF's more DoT are connected.
  Discipline's ordinary ES pool and recharge are verified separately above; conditional effects remain partial.
  Reservation overflow is refused.
  See [11](../docs/protocol/11-skills.md) section 13.
- Flesh and Stone and Blood and Sand share a persisted stance: recasting switches both without releasing reservation;
  `/auras off <name|effectId|all>` explicitly disables them. Blood and Sand modifies melee area damage and area.
  Flesh and Stone uses the current gem's distance-dependent more physical hit damage / less incoming hit damage;
  the obsolete blind, global reduced-damage and physical-DoT bonuses are removed. Native stance switching and off passed.
  Code: `AuraCatalog` (`FleshAndStone*`, `RawStats`), `Game.ToggleAura` / `EnemyDebuffsTick`, `MonsterMods`.
- Passive-granted skills: a passive's `GrantedEffectsPerLevel` skill joins the gem skills at the row's fixed level,
  with a stable id, no supports, quality 0 and no requirements; it casts, reserves, saves and ends like a gem's.
  Only self auras are listed until other granted skills (minions, other kinds) have executors.
  A granted triggered skill is never listed and runs from its trigger.
  Summon Elemental Relic (Paladin's Elemental Valiance, the ordinary Guardian's Unwavering Crusade) rolls on kills and
  on hits against rares and uniques, behind its cooldown, in Ruthless too.
  Used by the Paladin Aura options (Determination, Grace, Discipline); automated coverage only, not checked with the
  real client. See [11](../docs/protocol/11-skills.md) section 13 and [Paladin](../docs/game/paladin.md).
- Owned minions: relics and chieftains remain timed; all 17 playable golem gems summon, fight and take generic hostile
  hits/projectiles. Golems follow, scale with gem level/quality and supports, grant their distinct owner buffs and save
  activation across areas. Local rule: each activated golem returns two game seconds after death while its source is
  usable. All variants summoned in the real client; ordinary combat, buffs and repeated returns checked live.
  Nearby ordinary auras now affect owned fighters. Partial: exact official AI priorities, some support interactions,
  sharing golem owner buffs with other players and bespoke boss damage.
  See [11](../docs/protocol/11-skills.md) section 10h; relics remain in section 13 and [Paladin](../docs/game/paladin.md).
- Link skills: Soul, Flame, Vampiric, Protective, Intuitive and Destructive Link link one of the player's minions when
  he has `link_skills_can_target_minions` (Deep Connections); with no party play there is nothing else to link.
  A link lasts its duration, costs mana every second, breaks after 4 s out of range or when its cost is not paid, and
  its target's death kills the player (a refund of the relics' passive ends the link without one).
  `number_of_active_links` goes to the client; Absolution and Field Medicine use
  the links. Not modelled: the beam, line of sight, the gems' effects on the target, and party allies.
  Automated coverage only, not checked with the real client.
  See [11](../docs/protocol/11-skills.md) section 13 and [Paladin](../docs/game/paladin.md#link-skills).
- Saved aura toggles (2026-10-25, awaiting client confirmation): active effects persist per character and restore on
  relog, area entry and server restart, subject to usable gems and available reservation.
  Helena's picker now receives all 103 exported personal hideouts, including MTX templates;
  switching templates now uses the native selection request and persists the chosen home.
  See [11](../docs/protocol/11-skills.md) and [21](../docs/protocol/21-hideouts.md).
- Spells disabled (`your_spells_are_disabled`, Devout of Gruthkul): every spell use is refused on the warcry and
  own-aura admission routes, and the skill record carries `skill_disabled_by_stat`.
  Running spell auras are suspended with their intent kept and restored after the source goes; a burning Righteous
  Fire keeps burning the player and deals enemies nothing.
  Local policy for already-running effects; Gateway coverage only, not checked with the real client.
  Code: Gateway (`SpellsDisabled`, `ActorSkillDisabledCore`, `SuspendRestrictedOwnAuras`).
  Docs: [11](../docs/protocol/11-skills.md) §10j.
- Passive blind aura (Devout of Solaris, Born in the Shadows: BuffTemplates `SaboteurBlind`): living hostile monsters
  within 35 cells get the `blinded` buff each player tick, which a death, a refund or leaving the radius takes off.
  Blind from it or the smoke cloud lowers their accuracy and evasion by 20%, lets
  `critical_strike_chance_+%_vs_blinded_enemies` raise the player's critical chance against them (default attack, area
  sweeps, footstep skills, projectiles) and `damage_taken_+%_from_blinded_enemies` lower their hits and ignites.
  Hits Blind by their on-hit and critical-strike chances for 4 s ([11](../docs/protocol/11-skills.md) §12).
  Other passive auras do nothing yet.
  Gateway coverage only (a Polytheist and an ordinary Saboteur), not checked with the real client.
  Code: Gateway (`PassiveBuffAuras`, `TickPassiveAuras`, `Game.Blinded`, `CriticalChanceOn`).
  Docs: [11](../docs/protocol/11-skills.md) §12.
- Passive-granted footstep skill (Devout of Abberath: level 20 Abberath's Fury): while the player walks, each footstep
  of the Templar's run animation (two per 800 ms, faster with movement speed and slower when chilled) explodes at the
  player's feet with the skill's art and hits every living hostile monster within 7 cells (grown by spell area) as an
  area spell without a gem: 300-450 fire with the player's increases, critical strikes, block and the usual on-hit
  effects.
  Not in town, the grace period, while frozen, stunned or dead, nor with spells disabled; a movement skill's blink is
  no walk, and so is Shield Charge's, which the wiki says triggers it (a known gap).
  A landed hit ignites the monster with its 25% chance or a critical strike (the player's ignites on monsters below).
  The radius comes from the explosion art (no table gives one), and other passive-granted skills do nothing yet.
  Gateway coverage only, not checked with the real client.
  Code: Gateway (`PassiveGrantedSkills`, `RunFootstepSkills`, `TriggerFootstepSkill`).
  Docs: [11](../docs/protocol/11-skills.md) §12.
- **Projectile modifiers.** Generic profiles consume resolved primary counts, speed, pierce/fork/chain/split/return
  grants and independent prohibitions in the sole staged runtime. Ordinary bows and catalog-known projectile gems
  work without a skill provider. A basic recipe is straight flight, not complete special-skill behavior.
  Return opens another hit-admission phase and reads the shared returning-damage multiplier. A client-drawn spell
  rolls its partial return chance per projectile with the client's seed (static RE, checked live 2026-10-31: the
  projectiles that came back were the ones that dealt return damage), and flies the client's 150-cell range out and
  past the caster on the way back (`projectile_maximum_range_override` replaces it). Return chance,
  return speed, contact budgets, overshoot and terrain behavior are configurable through skill definitions.
  Boolean fork sources grant one generation, and raw bow always-pierce sources bind to the same common profile.
  One movement instance is capped at 150 cells (15 m) through `projectile_maximum_range_cells`; fork, chain and split
  start a new instance with a fresh budget, so total travel can exceed 15 m. Return retains remaining continuation
  counts and the original lifetime; return pierce-all requires its own stat.
  Action speed does not change projectile range: projectile speed only shortens the flight, and `action_speed_-%` is
  composed additively with chill/freeze for attack, cast and movement pacing; Tailwind/Harbinger sources remain #246.
  Direct and area snapshots keep area increases/supports scoped to their damage stages.
  Point Blank scales attack projectile hits by travel ([projectiles](../docs/game/projectile-framework.md)).
  Area effects scale to the committed radius when the selected art exports a native radius; unknown art stays unscaled.
  Terrain-blocked area centers are omitted without resampling or reserving time; surviving areas keep consecutive intervals.
  Skill explosions, pulses, children, forms, native art and specialized resources belong to skill definitions.
  Missing metadata or unsupported damage plans are refused before costs, without generic area-hit fallback.
  [Named skills](../docs/game/projectile-skills.md) implement terminal Fireball blasts and Kinetic Blast clusters.
  Burning Arrow uses catalog flight with its 100% fire conversion, a 25% base ignite chance (critical hits always
  ignite) and Vigour's fire added from maximum Life; projectile ignite burns monsters through their fire resistance
  with the native `ignited` buff ([20](../docs/protocol/20-damage.md) §3). Automated only; native ignite visuals and
  damage still need a user-run check.
  Direct volleys share admission separately outbound and returning; denied siblings pass through.
  Fireball waits for exhausted continuation and gives the terminal victim one area-classified hit.
  Normal KB creates four offset explosions with 40% less area damage; Clustering creates three plus quantity modifiers,
  keeps one primary and cannot Split.
  The internal AltY row retains normal-KB compatibility, without establishing a third playable gem.
  Separate blast/placement radii use supported area stats once; independent explosions can overlap covered victims.
  Each KB contact group excludes its own triggering enemy, including delayed explosions; other groups may hit it.
  Automated checks cover large triggering victims, pierced neighbors and wall/range groups. Gateway actor radii
  remain zero, so live area coverage uses monster centers rather than calibrated body intersections.
  KB centers use a fixed scaled circle and random sector angles, with separate first-delay/inter-slot parameters:
  immediate first blast, then 1/30 s between slots. These are user-tuned feel values, not official measurements.
  Secondary explosions use their own small-burst art and equipped MTX; native art scales to the actual blast radius.
  Skill-bar explosion totals use the resolved cluster recipe, including quality and Clustering's projectile conversion.
  Fireball wall-contact damage and default/Stygian packets have regression coverage; visual visibility remains unverified.
  Flights retain fixed terrain-plus-offset height (bare offset without a grid); explosion effects follow client terrain.
  A player's primary shot flies at the higher of its release and aimed ground: terrain rising more than 12 steps above
  that stops it, so a shot aimed below a cliff hits the face and one aimed on top clears it
  ([framework](../docs/game/projectile-framework.md), "Aimed height"). LocalExile model v1, awaiting a native check.
  Official geometry/timing, surface height and art equivalence still need client validation.
  Code: Core `Projectiles/`, Gateway `Projectiles/` and `Projectiles/Skills/`; see the
  [framework guide](../docs/game/projectile-framework.md).
- **Instant skills and cooldowns (verified live 2026-10-21).** Haste, the Purities and Righteous Fire carry
  `skill_is_instant`, so the client sends them as C→S 0x191 and keeps walking;
  the server applies them and answers S→C 0x1ce, which starts the skill bar's cooldown.
  Bladefall of Trarthus no longer hangs the client (its volley interval is in the record) and runs as its toggle: a
  volley hitting the area around the player every interval, ground blades, the mana spent per second until it runs out
  (live 2026-11-07: volleys and hits checked; the end at no mana not seen yet).
  Skill records retain GEPL Cooldown as the native base and carry the cooldown count.
  Ordinary one-use spells/attacks now resolve player and skill generic recovery once, and the server keeps remaining
  base-time budget through stat changes and pause.
  Preflight reserves before action/movement/echo side effects; queued effects retain the original action/area ticket.
  Real-client Dash initial +100%/-50% recovery, early raw rejection without movement/echo/mana, and pause checks pass.
  The native positive-rate cache required an explicit cooldown-state refresh after skill-stat changes;
  this preserves remaining/total base time at the send gate without consuming another use.
  Native Dash mid-cooldown recovery changes now pass live: one entry changes rate with base/total/progress preserved,
  early input has no echo/cost, and reuse succeeds after native expiry.
  Wire, active/expired/zero-rate and queued encrypted-send checks also pass.
  Multi-use, duplicate same-effect gems, shared groups, extra uses, typed/conditional/more recovery and official
  tick equivalence remain outside this slice.
  Skill records carry the GEPL Cooldown and the stats the cooldown count, and the server refuses a skill until its
  cooldown is over.
  Ground-target C→S 0x190 now frames its 12 bytes and routes ordinary Frostblink through mana/cooldown acceptance.
  The ground 0x1ce uses the raw aim; the server mirrors native 32/42-cell distance limits and ends the old walk.
  Placement requires a captured grid and clear terrain, barriers and endpoint clearance.
  Action tokens and pending releases remain unchanged; other location instant movement is withheld.
  Actual Q input and a cooldown retry matched native/server positions, with two 12-mana costs and two confirmations.
  Early retry, noninstant Fireball and unsigned-invalid coordinate requests changed no position or mana.
  Manual native experiments separately verified near/far clamps and the old predicted walk ending after a blink.
  Hovered-monster Frostblink sends 0x191 and now shares movement acceptance, with an object-bound 0x1ce.
  Its size-aware object candidate trace precedes the distance clamp;
  it does not reuse the monster centre as a ground aim.
  Both preflight and the send gate require a living known-size monster with no active walk, and recheck placement.
  Invalid preflight changes no mana/cooldown; later invalidation withholds confirmation under the existing cost policy.
  Automated actual-send tests cover current target/player sampling, target death/walk, pause, transfer, barriers and unequip.
  Four manual native object confirmations verified clear near/far/diagonal/off-axis endpoints.
  A reflected terrain fallback is deliberately rejected.
  Actual hovered Q on a stationary monster emitted 0x191 and one object-bound 0x1ce with flags 0x20.
  From `(280,220)` against `(300,220)`, native/server position became `(317,220)` for one 12-mana cost.
  A framed ground retry at 0.5 seconds changed no mana or position and sent no extra confirmation.
  Combined #197 synchronization retained base 3000 ms, total 3 seconds and rate 1.
  Production framed off-axis/far object requests matched native/server endpoints with one 12-mana cost each.
  Framed moving-target, reflected-terrain, missing-target and noninstant requests changed no mana or position.
  They sent no confirmation, and the invalid-combination control kept native cooldown entries empty.
  Production movement during an active walk has not been measured.
  Frostblink damage, chilled ground, enemy-dependent recovery and shared Blink cooldown groups remain unsupported.
  The skill bar keeps its slots when skills are re-sent or a gem is socketed again (the saved bar goes out as 0x53)
  ([11](../docs/protocol/11-skills.md) §9, §11).
  A slot's "Always Attack Without Moving" (C→S 0x4f) is saved and goes back in 0x53, so it stays ticked.
- **Auras (the simple set is verified live).** Reservation is read from GrantedEffectsPerLevel (`ManaReservationFlat`
  / `ManaReservationPercent`), reduced by the passive/mastery reservation efficiency and sent as the Life pool's
  reserved mana (and in every 0x1b1 mana update).
  Aura skills toggle on cast; Purity of Elements/Fire/Ice/Lightning, Clarity, Vitality, Precision, Haste,
  Determination, Grace, Discipline, Anger, Hatred, Wrath, Zealotry, Malevolence and Envy apply their gem's level and
  quality effects and buff icons; their 3D effects (`affected_player.epk`, `faster_attack.epk` …) do not render yet.
  Pride applies its enemy debuff: every hostile monster in its radius carries BuffDefinitions 1331 and takes its
  `physical_damage_aura_nearby_enemies_physical_damage_taken_+%` (19% at level 20) rising linearly to `_max` (39%)
  after 4 s, both scaled by aura effect; the ramp resets on leaving the aura and feeds hits and physical damage over
  time. Live 2026-11-01: `Auras: Pride (0 flat, 50%)`, `0x01c1` with row 1331 on a Rhoa, `0x01c2` on toggle-off, and
  death cleared both.
  The skill-bar active frame and rotating circle now receive the buff's skill id/action hash association (0x1c1 bit3),
  including area-entry restoration; automated checks pass, live icon verification pending.
  Permanent spell buffs, five heralds, Skitterbots and Temporal Rift share source/reservation reconciliation.
  Purity grants elemental-ailment immunity; Arctic Armour gives stationary hit mitigation, freeze immunity and chilled
  trail; Tempest Shield grants shield spell block, shock immunity and block retaliation. Herald kill/poison procs use
  the ordinary damage/minion pipeline. Temporal Rift recalls position/resources from four seconds ago, with native proof.
  Nearby player auras reach allies and controlled fighters using caster effect; duplicate families select each stat's
  strongest value. Life reservation uses an independent budget, Arrogance and pool/recovery caps; curse auras use
  the hex lifecycle. Skitterbots apply scaled chill/shock and trigger placed traps/mines, rearming a mine once;
  full trap/mine skill-specific payloads, banner/Vaal executors and Agony's retail Virulence decay remain partial.
  ([11](../docs/protocol/11-skills.md) 13).
- Animated equipment admission respects melee/ranged variants, identification, item level and six-socket weapon exclusion.
  Summon/mine/trap input and cleanup work; special payloads remain partial ([11](../docs/protocol/11-skills.md) 10h).
- Hexes/marks have limits and combat effects; reflection and special payloads remain partial (`MonsterCurses`).
- Totems: Rejuvenation, adopted projectile spells and bow/wand ballistas execute ([11](../docs/protocol/11-skills.md) §10g).
- Passive self buffs (`PassiveBuffAuras`): a passive's BuffTemplates buff on the player adds its stats to the
  passives' sum and is sent with 0x1c1 after an allocation and on area entry, and taken off with 0x1c2 on a refund
  (Surfcaster's Ghost of the Deep).
  Only the Gateway tests cover it; the icon and whether the client keeps the buff after a death are not checked live.
  Enemy and ally passive auras are not applied.
  Code: `PassiveBuffAuras.cs`.
  Docs: [11](../docs/protocol/11-skills.md) §12.
- Onslaught on kill grants the Onslaught buff and its speed ([13](../docs/protocol/13-items.md) §8a).
- Kill-driven buffs from item stats: `player_gain_rampage_stacks` starts a Rampage streak and its kills feed the
  "1% increased Rarity of Items found per X Rampage Kills" stat (whole groups of X); the melee-hit source keeps
  granting stacks on hits as before. The "on Rampage threshold" modifiers now trigger once per tier crossed: life
  recovery, dispelling ailments, physical damage immunity and phasing for their listed durations. The per-stack
  modifiers for area of effect (per 25 kills) and Chain Hook / Shield Charge attack speed (per 10 kills) count, and a
  kill grants the extra Vaal Soul while the streak is active. `gain_soul_eater_during_flask_effect` grants Soul Eater
  while a flask effect runs: 5% increased attack and cast speed and 1% character size per soul, one soul per kill
  within 60 cells, a base maximum of 45 plus item stack modifiers, and one lost every 0.5 s after 4 s without a kill.
  Soul Ascension adds the item's own source: the player's hit on a rare or unique enemy eats one soul through
  `gain_soul_eater_stack_on_hit_vs_unique_cooldown_ms`, no more than once every 0.5 s and with no radius (no beam for
  these souls; the official object is not captured).
  The player gets its icon and count
  (`soul_eater_player`, 279) and the invisible `soul_eater_aura` (277), the client's `using_flask` flag is sent, and each
  soul draws the official `ServerBeamEffect` line from the corpse to the player (see
  [11-skills.md](../docs/protocol/11-skills.md) §12).
  Soulthirst's loss of eaten souls on flask use is not implemented.
  Not modelled at present: the threshold smoke cloud (no timed smoke ground), Unholy Might (no Unholy Might system),
  the frenzy charge every 50 kills (the charge session has no Rampage-count rule), starting a streak at maximum
  endurance charges and losing them when it ends, and the map/leaguestone Rampage modifiers.
  Code: `Rampage.cs`, `RampageSession.cs`, `SoulEater.cs`, `SoulEaterState.cs`, `GemSupports.Rules.cs`, `VaalSouls.cs`.
- Onslaught (on kill and a Silver Flask's) grants Path of Building's Speed modifier: 20% increased attack speed in the
  attack-speed stat and the swing timing, 20% increased cast speed in the cast-speed stat, and 20% increased movement
  speed in the walk ([13](../docs/protocol/13-items.md) §8a).
  Code: `UtilityFlasks.cs`, `OnslaughtOnKill.cs`.
- **Stats.** The client's stat key is the Stats.dat row + 1 with the vendored schema
  ([11](../docs/protocol/11-skills.md) §8); `/set_stat` takes stat names or keys.
  The [357-stat ledger](../docs/dev/issue75-stat-ledger.md) tracks current individual review state and remaining
  scope. The inventory validates exact membership; these states do not count completed implementations.
  The player's life, mana, accuracy, armour and evasion come from its class and level, with the attributes' bonuses
  (energy shield, evasion and melee physical damage increases);
  accuracy also takes its flat/increased passives, weapon-specific stats and Accuracy Mastery (rating for the panel,
  the two target-conditional effects at the roll), and the final rating value is
  sent as `main_hand_accuracy_rating` / `off_hand_accuracy_rating` for the Offence panel;
  armour and evasion also take their flat/increased passives and the base equipped armour pieces (body, helmet,
  gloves, boots); Life/Mana also take signed per-level and complete final-attribute-group sources before their
  existing flat/increased projection. Its final floor remains a documented local choice; current automated results
  are in the attribute entry above, with new native checks pending.
  See [11](../docs/protocol/11-skills.md) §4.
- **Attack speed passives.** Generic, melee, one-/two-handed, weapon-family, shield and dual-wield passive attack
  speed is summed into the client's `main_hand_attack_speed_+%`;
  dual wielding adds PoB's 10% more and the server's unarmed attack uses the same final value.
  Attack gems apply their extracted inherent speed multiplier on the server only; the client applies its own, so the
  total base duration it is sent stays the weapon's. Pacing, contact/release timing, channel intervals and selected APS
  share the server's duration. Frida verified the clips with a 714 ms bow: ordinary 0.714 s, Burning Arrow 1.02 s.
  An attack's server-created projectile is released 60 ms before its contact (`AttackReleaseDelay`), for the time before
  the server reads the action, its tick and the output queue. Burning Arrow's arrow arrives about 25 ms after the client's
  muzzle flash (was about 75 ms); the default arrow 0-52 ms after its contact, in two clusters (about 265 and 319 ms).
  Supports, conditional/per-charge speed, special time overrides and attack-gem damage/DPS remain separate work.
  Selected attack-gem main-hand ranges now share the converted combat packet, including skill effectiveness.
  All twelve ranges clear absent types; pre-crit averages retain fractions and DPS uses the emitted APS value.
  Creation and refresh pass raw character stats and the actual weapon separately from inherited display values.
  Native Burning Arrow showed 9-23 fire/total, physical zero, per-use 16.52 and DPS 16.19 at APS 0.98.
  Added cold damage showed 9-9 cold and 18-32 total; clearing it removed the cold rows.
  Relog retained the base result.
  A noncritical server hit dealt 24 damage, while ordinary attack retained its original ranges and averages.
  Packet, catalog, conversion, source-isolation and native row-format tests also pass.
  Ordinary attacks, spells and off-hand display paths are unchanged.
  Conditional/support effects, crit weighting and broader weapon modifier scope remain separate work.
  The default attack also sends the panel's `hundred_times_damage_per_second` and
  `display_hundred_times_damage_per_skill_use`.
  Live verified: the dual-wield character's APS follows the passives (2.93 → 2.98 after a node).
  Minion, totem and timed/conditional stats are not modelled;
  the character panel's APS stays cached after a passive allocation (reopening the panel or switching its tab does not
  refresh it; only a relog does).
  ([11](../docs/protocol/11-skills.md) §4)
- **Spell cast timing (#192).** Catalog base/virtual cast milliseconds now stay unchanged for native spell actions,
  which apply cast speed themselves.
  Server waits and displayed casts per second share `SpellCastTiming`; attacks keep their existing path.
  The old -80% Fireball recovery capture lasted 18.75 seconds instead of 3.75 due to two divisions by 0.2.
  Automated 0/+100/-80 timing, instant/channel classification, invalid-rate and existing aura/sum checks pass.
  Real-client Q Fireball clips measured 3.750000157 s at -80%, 0.375 s at +100%, and 0.75 s at zero.
  Native virtual base stayed 750 ms; emitted cast rates were 0.27, 2.67 and 1.33 casts/s respectively.
  Projectile release/contact alignment was not separately measured.
  "More" cast speed (Spell Echo's 40%, Screaming Reels' 60% with a Fishing Rod, Fanaticism's, the totem supports' and
  the other sources of the native combined final) uses the native calculator's formula in the server wait and in the
  spell record's `cast_speed_+%`.
  That record value now also includes a support's own cast speed (Faster Casting), as the server wait already did.
  Neither change is checked with the real client; only the Gateway tests cover them.
  Area spells without a projectile (Ice Nova) release at their `spell_aoe` clip's contact (Witch 266/766 of the cast,
  `AnimationContacts.SpellAreaRelease`; the clip choice read from the IceNova action's code), no longer at the end of the
  cast (about 450 ms late for a 700 ms Ice Nova).
  Ice Nova's victims are hit as its ring reaches them, over 0.2 s (`IceNovaRing`), and a released ring still lands when
  the player moves or casts again.
  Not checked frame by frame with the real client yet.
  Instant casts remain instant; channel damage cadence and conditional action-speed mechanics are unchanged.
  Numeric server durations below 1 ms are floored, and invalid factors/unrepresentable waits produce no spell effect.
  Code: `SpellCastTiming.cs`, `AnimationContacts.cs`, Gateway (`GemSkills`, `CastMs`, `CastGemSkill`, `LaunchProjectile`,
  `AreaSkillHit`).
  Docs: [11](../docs/protocol/11-skills.md) section 9.
- **Selected spell hit damage panel (#188).** Spell total/per-type ranges and historical pre-crit average damage/DPS
  now use the same converted packet as projectile and area combat.
  All range fields are explicit, including zeros for absent types; hitless spells clear inherited weapon DPS/per-use.
  Final selected spell records use the supported combat packet for spell and inherited main-hand ranges.
  The issue #75 review correction now weights ordinary/supported average damage and DPS with current critical
  chance/multiplier and signed luck, retaining exact action duration and clamping the native integer bounds.
  Attack estimates also include normal-reference landing probability and separate critical confirmation, using
  the ordinary/selected accuracy profile. Raw global/local cannot-evade updates estimates and both ordinary
  main-hand chance aliases; final diagnostic chances do not feed back into estimates. Nineteen added cases
  cover six reproduced omissions, spell controls, actual support link/removal, bypass, luck and bounds;
  the expanded focused run passes 55. Native revalidation, integer-roll expectation, multi-hit,
  dual-hand, cooldown and DoT calculations remain open. See [11](../docs/protocol/11-skills.md).
  This includes explicit zero ranges after spell damage suppression, without changing attack displays.
  Automated mixed conversion/gain, added-damage effectiveness, hitless, attack and native row-format checks pass.
  Native selection/getter checks show Fireball 10-15 fire and Freezing Pulse 10-15 cold with a +12% spell-damage wand.
  A conversion/gain control split Fireball into fire and chaos rows and dealt a noncritical 14-damage server hit.
  Scorching Ray cleared inherited hit ranges and weapon DPS, including after added spell damage refreshed its record.
  Ranges use server floors; corrected averages retain packet fractions and exclude target mitigation,
  per-type integer-roll expectation and cooldown/downtime.
  Beam/DoT displays, conditional damage rules and broader conversion semantics remain outside this slice.
  Code: `SpellHit.cs`, `SpellPanelStats.cs`, Gateway (`GemSkills`, `LaunchProjectile`, `AreaSkillHit`).
  Docs: [11](../docs/protocol/11-skills.md) sections 9 and 14.
- **Critical strikes.** The server rolls unconditional crit passives: the equipped main hand's `WeaponTypes.dat`
  critical chance (permyriad) raised by generic/attack/melee/weapon-shape increased crit chance for attacks,
  each spell's extracted base chance, and 150% plus `base_critical_strike_multiplier_+`;
  critical damage is multiplied and logged.
  Unarmed has 0% base crit.
  Two pure base stats replace that base before the increased chance, the way PoB's `WeaponBaseCritChance` replaces
  an attack pass's `source.CritChance` when that hand has a weapon:
  `override_weapon_base_critical_strike_chance` (Stats row 9892, permyriad) on the armed main hand — Slayer
  Overwhelm (graph id 17315) grants 800 = 8.00% and Blind Prophet's Sightless (graph id 24698) grants 900 =
  9.00% — and `base_melee_critical_strike_chance_while_unarmed_%` (Stats row 3906, permyriad; Behemoth Final Blow,
  graph id 10135, grants 1000 = 10.00%) on the unarmed melee attack.
  Each is a base, not a fixed final chance: generic increased crit still applies on top;
  the weapon override stays out of unarmed and spell rolls, and the unarmed base stays out of a weapon hand.
  Only the active weapon set supplies the attack base and its weapon-specific increases.
  Ordinary added BASE now combines generic and attack/spell-specific raw actor and selected skill/support sources
  after the intrinsic base or its override and before increased chance, with signed wide sums and final half-up cap.
  Native weapon-local flat critical chance precedes local increased chance and its permyriad rounding.
  Linked Increased Critical Strikes supplies its own BASE and increase once; known zero intrinsic chance may receive
  added BASE, while missing/negative data, hitless skills and cannot-crit gates still fail closed.
  Native modifier identities, mode-specific Ascendant Assassin sources, active-set isolation, removal, zero and
  support composition have automated coverage; current combined results are in the panel entry above.
  Sixteen positive Fireball/ICS native controls on `0aabeb6` pass at actual level 100 in both modes.
  Selected permyriad values 500/550/1200/1120 render as 5/5.5/12/11.2%, including actor/support removal, relinking and relog.
  Two current-Actor Fireball-removal controls and two default sentinels also pass without numeric-zero inference.
  The original Standard D0 sentinel remains UNPROVED; its separately reviewed after-capture correction uses current
  Fireball/Default identities and historical C2 numeric evidence, with no simultaneous Fireball receiver claim.
  Its version freeze preceded the Ruthless sentinel capture.
  Canonical stock/charges and protected data held; gem-ID remaps and retained participant state are qualified in
  protocol 11 §5, and normal services were restored.
  Native critical hits, rendered zero and the remaining attack/local/Assassin/rounding contexts remain unverified here.
  Isolated carriers do not establish legal unique acquisition, and the local-flat chance fixture retains its real
  no-extra-critical-damage flag without certifying multiplier behavior. Pinned PoB 3.29.x is a labelled fallback.
  `/set_stat` replaces the input and rebuilds the pools, so the panel and the rolls agree.
  Generic/spell development increases replace the same complete raw input used by spell rolls, including aura sources.
  Attack/spell increased percentages and the pooled spell reference now follow those effective inputs.
  Automated regressions cover these replacements, signed sum bounds and shared integer rounding.
  Attack and spell chances now share half-up permyriad rounding: 650 with +1% gives 657.
  Native evidence below targets original head `8c4c62d`; recheck the changed midpoint and resolved stack natively.
  Five follow-up native checks verify generic/spell input agreement and maximum/minimum integer saturation.
  Twelve native checks verified 830 → 800 → 900, increased chance → 1800, cannot-crit → zero,
  empty-set/unarmed behavior, weapon restoration and relog.
  Ascendancy allocation remains in the separate lifecycle change.
  Global, virtual-global and attack-specific cannot-crit flags suppress the attack chance.
  `SpellCriticalStrike` reads neither base override.
  The final main-hand and spell crit chance/multiplier stats go to the client for the Offence panel;
  selected spell chance and server projectile/area rolls now share raw player and skill inputs once.
  Precision and Zealotry retain their existing bonuses without counting them twice.
  Actual Spell skill types set the panel's casting flag; attacks and non-spell warcries keep it clear.
  Hitless spells and unsupported action types cannot fall through to weapon damage or attack critical rolls.
  Native panel/getter checks show Fireball 5%, Freezing Pulse and Ice Nova 7.5%, and Arc 6%, including after relog.
  Combined generic/spell bonuses add once; the no-crit flag clears the native chance and hides its row.
  A hitless Scorching Ray cast dealt no weapon damage, while the same connection's Fireball dealt a critical hit.
  Catalog, chance, panel gates, aura sources and hit routing also have automated checks.
  Four unconditional MORE/LESS chance sources now run before final cap/rounding. Real Controlled Destruction
  changes selected 10% to 2% for both attacks and spells; its link/requirement/removal lifecycle and actual parsed
  critical draws agree. The real unique -40% source changes an ordinary weapon's 10% to 6%, with explicit zero
  and item removal restoring 10%. Source cancellation, extreme bounds, independent factor composition and
  half-up rounding have regressions; current native verification is pending.
  Surfcaster's Baited Breath makes every hit critical while a Fishing Rod is held, a 0% base included; cannot-crit wins.
  Focus skill and curse lifecycles, guaranteed-critical effects, off-hand publication/execution and other
  enemy-state/more crit mechanics remain separate work.
  `/crit` shows the attack values and a pooled 5%-base spell reference ([11](../docs/protocol/11-skills.md) §5).
- **Overcapped-resistance modifiers.** While the flag stat is set, the uncapped resistance above its maximum is a
  generic increased modifier, exactly PoB's `ResistOverCap` (the maximum capped at 90): +1% increased evasion per 1%
  of cold overcap, +1% increased critical chance per 1% of lightning overcap, additive with the other increases for
  attacks and spells alike.
  The flags are ordinary stats, so any source sets them: the Surfcaster's Sea Legs notable (graph 61696, with its
  +40% cold-and-lightning pair resistance the aggregator now sums into both elements), an item mod, or `/set_stat`.
  An explicit `/set_stat` value replaces its own input (per-type, pair and all-element resistance, and per-type or
  global maximum resistance) instead of adding to the aggregate, and rebuilds the pools; a Kitava change rebuilds them
  again.
  `PoolsFor` derives the increases once per pool build and stores them in the pools, so the monster-attack evade
  rolls, the panel rows and the spell crit consumers read the same numbers; the uncapped resistance values stay
  untouched, and Iron Reflexes' converted evasion keeps the overcapped increase once.
  Automated checks cover below/equal/above cap, the 90 maximum, both penalties, flag removal and override, additive
  behaviour, spell/attack isolation and the Iron Reflexes conversion.
  Combined automated checks retain the 830 +45% =1204 armed chance and complete signed critical cancellation.
  Purity-aware raw replacements now remove the aura contribution for that exact key, including explicit zero maxima.
  Real aura/catalog/pool regressions pass; the corrected aura-override composition needs a native recheck.
  The resolved stack needs native rechecking; the following thirteen checks belong to original head `39cbda2`.
  Real-client stat checks verified evasion 15 → 22 and spell crit 500 → 725 at 120% resistances,
  raised maxima, replacement of global inputs, both Kitava penalties, flag removal and relog.
  Ascendancy allocation remains part of the separate lifecycle change.
  See [11](../docs/protocol/11-skills.md) §4, §5 and §12.
- **Projectiles.** One chronological runtime drives production gem/bow flight, contacts and continuation.
  It runs at 30 Hz boundaries with subtick contacts and commits damage/rewards before network output.
  Native gem skill records derive projectile count, pierce, chain, fork, split and return from shared parameter rules.
  These display-only projections respect prohibitions and refresh after support removal without changing release inputs.
  Missing projectile metadata allows generic panel parameters while executable release remains unavailable.
  Release captures position and offense; target movement, evasion, defenses and impales resolve on contact.
  Issue #75 accuracy entropy/critical confirmation, one critical roll per use, raw penetration, actual-roll impales and
  direct-hit leech run in this same engine. Resource recovery catches up projectiles before consuming leech; corpses
  retain the ordinary lifetime. Combined automated regressions cover Ward/slot defences, mana conversion and contact
  recovery. Details and test receipts: [merge review](../docs/dev/issue75-review.md#upstream-merge-resolution).
  The later main merge composes Gambler, Vaal credit, incoming ailments and league modes with these stat consumers.
  Combined regressions cover direct-only leech and once-only soul credit under held output, plus mode-aware defences.
  Its [validation report](../docs/dev/issue75-review.md#main-merge-league-modes-gambler-and-vaal-souls) records the decisions.
  Native revalidation of that combined build remains pending; the wider 357-stat goal stays paused.
  Pause freezes the game horizon, transfer/retirement invalidates epochs, and released shots survive later actions.
  Ordinary bows and wands share one release path on that runtime: the shot is scheduled at the accepted input's weapon
  contact (`attack1_bow`, or the wand clip the native ACT selects for the input's sequence) and the action echo follows
  it; a cancelled action, death, freeze or hit recovery releases nothing. The wand bolt plays its rig's impact clip where
  it stops and is removed after that clip on the same game clock, so pause holds it and a transfer drops the removal.
  The arrow stays a second where it hit (`ArrowStuckSeconds`) before removal. Both send `0x1af` (kind 3, the shot's
  action sequence, flags 0x4201 plus the critical bit, code by damage type) on contact for the target's blood and
  particles; the main-hand bit follows the live capture, the skill index and header words it also fills stay zero. Projectile objects are created at their release point and their first move runs from there: the lazy
  snapshot used to create them where the simulation already had them, one to ten cells past the bow in a live log
  (2026-10-29). On a lockstep client the default attack's record carries the shot's count, as GGG's do, and the
  client draws the bolt or arrow itself with no server object; the server simulates it on the client's contact and
  range and releases it on the client's own release frame ([07](../docs/protocol/07-latency-visibility-movement.md)
  §6), so a hit recovery before it stops both (checked live 2026-11-05 on orders at a monster: one client
  `WandProjectile` per shot of 41 and one `DefaultArrow` of 41, no projectile `0x2bb`, the client's release 0–7 ms
  after the server's for the wand and 1–3 for the bow, p10–p90; it was 16–40 ms after on the first version and 66–99
  ms before with server objects) ([11](../docs/protocol/11-skills.md) §4, §10). A shot at the ground (attack in place)
  is released the same way but not checked live. A predictive client keeps the server's objects: its record
  has no count, without which the release fires nothing (a native trace on 2026-10-29 saw no client arrow in 40).
  The live hit-timing fit put our arrows at 367 cells/s against live's 308, but the Gateway releases DefaultArrow at
  the table's 3260 units/s = 299.9 cells/s (logged on every release), so the fit's slope measures something else
  (the roadmap). They leave from the
  releasing clip's contact keyframe (`data/animation-contacts.json` positions, `anim_contacts.py` v3): the Ranger's
  bow 5.5 cells ahead along the aim at 16 steps up, a wand's tip 7.5 cells ahead at 12 (the 5-step default, tuned on a
  fireball, had the arrow trail start at the hip, live 2026-10-29); the keyframe's lateral part is not applied.
  Monster projectiles: a mortar attack's shot (Sand Spitter, a Cannibal's ThrowFire: `monster_mortar_attack` via
  GrantedEffects SkillID → MonsterMortar → Projectiles) is drawn by the client, whose record carries the MonsterMortar
  row and the skill's count as GGG's does, with no server object (checked live 2026-11-05 on a lockstep and a
  predictive client: one client `SandspitterMortarProjectile` per order, no server object;
  [11](../docs/protocol/11-skills.md) §6); a spell mortar (`monster_mortar`) keeps its server object. Not measured yet:
  where the client's mortar lands against the server's hit.
  The server simulates it on the same runtime, released at the clip the client plays for the
  skill (MonsterMortar.Animation → the ACT's `SpellProjectile` pool, the native selector on the action's 0xffff
  sequence: attack1, contact at 37.5 % of the attack) from that clip's keyframe (4.2 cells behind the body that faces
  away, so toward the player, 9 steps up: `projectile_uses_contact_position`; `data/monster-contacts.json`,
  `scripts/re/monster_contacts.py`), lobbed at the player's cell but no closer than the skill's
  `projectile_minimum_range` (4.6 cells), team 2, and its landing splash (`Game_MONSTER_MORTAR_RADIUS`, default 10
  cells) runs the monster contact pipeline (`HostileProjectileOffense`, `Program.ProjectileHostile.cs`). Not checked
  live yet: that the client seeds a monster's clip choice from the sequence, and the dead zone. Another monster's
  projectile object plays its rig's flight clip from spawn and its impact clip where it lands
  (`data/projectile-clips.json`: every Projectiles row's clip indexes and lengths,
  and its .ao default animation), without which an object whose .ao declares no default animation (the mortar's; the
  wand bolt's declares one) is invisible. The data loaders turn their feature off
  with one warning on an unreadable file instead of faulting the pump, and monster ACT pools come from the root block
  only (a nested stance's entries had replaced them for 191 varieties; review 2026-10-30). Weapon-projectile monsters
  (`monster_projectile_weapon`, SkillID 0) still hit instantly with no shot; see the roadmap.
  Wand main-hand variation follows each class ACT pool and the native input-seeded selector; sixteen actual ground
  inputs covered all four Witch clips with matching selection and an object input also matched (historical LocalExile
  evidence on the earlier flight loop; the runtime path still needs a native recheck). Complete off-hand timing/damage
  and nonzero intrinsic attack repeats remain open.
  Real-input held-output, moving-target, wall, evasion, pierce, return, pause, transfer, impale and actor-scale checks
  exercise this same runtime. Native bow/skill controls and animation alignment still require client validation.
  Straight flight uses captured ground plus the release keyframe's height, else `Game_PROJECTILE_HEIGHT` (default 5);
  missing captures use the bare offset.
  Creation, glide and stop retain the same height; independent effects still follow client terrain.
  Platforms/doodads still need a surface model (#182). Integer walk estimates and shared radius remain approximations.
  Code: Core `Projectiles/`, Gateway `Projectiles/` (`Program.ProjectileRanged.cs`), `Program.RangedAttacks.cs`,
  `OrdinaryRangedAttack.cs`, `OrdinaryRangedShot.cs`, `Program.Combat.cs` and `ClientState.cs`.
  Docs: [framework](../docs/game/projectile-framework.md), [11](../docs/protocol/11-skills.md) section 10.
- **Combat.** The player's default attack kills monsters with server-side damage (no monster armour yet; each damage
  type of a hit takes the monster's own base resistance with the hit's penetration subtracted from the capped value,
  `HitMitigation`/`Penetration`; the player's hit lands at the attack animation's contact keyframe, a monster's still
  at half its attack time).
  Monsters use their first granted effect and swing back; their attacks roll player evasion and then go through armour
  and block.
  Ordinary physical melee hits can trigger native player TakeHit recovery through 0x1d1.
  Native proof stopped predictive walking and casting, blocked new actions, and cleared the recovery flag afterward.
  Server recovery cancels pending actions and channel hits and stops the walk estimate for the same interval.
  Recovery or a freeze also suspends a running channel: it pays and sweeps nothing until the player recovers.
  That channel suspension has automated coverage only, with no native check yet.
  Actual physical monster hits stopped walking and cancelled a slow cast before release in copied-save live checks.
  Released Fireball flight continued, and a stun-immune control took damage without recovery.
  With a bow equipped, a hit cancelled pending Burning Arrow, blocked an ordinary arrow during recovery,
  and allowed a fresh arrow afterward; an already released Burning Arrow kept flying.
  Chance, avoidance, immunity, recovery and delayed object walk-ups have automated checks.
  ES builds, substituted thresholds and unsupported conditional stun defences are skipped conservatively.
  See [11](../docs/protocol/11-skills.md), player hit recovery, for scope and protocol evidence.
  The player's life comes from the class/level/passive pools.
  The server has no shared simulation loop: hits use timers and attack time is bounded by a 30/s tick (`TickRate`).
  Monster decisions use 50 ms game-time steps, a separate 100 ms repath budget and a three-cell target threshold.
  Full attack duration, pending-swing generations and stage completion guard the faster planner against duplicate work.
  Native controls verified initial response, deliberate turns, full attack pacing and actual menu pause/resume.
  Core, Data and Gateway suites pass, including cadence, pending-swing and stage-completion regressions.
  Item mods count through the global item stats;
  346 varieties with NPC / ControlZone / ... components are refused.
- **Critical-strike elemental resistance rules (Inevitable Judgement).** The hit's
  already-rolled critical boolean decides the resistance: the packet rolls once (the original per-type draws), the
  existing single critical draw keeps its original place after them, and the mitigation resolves on the
  already-rolled packet without a second roll — a critical elemental hit with
  `critical_strikes_ignore_elemental_resistances` mitigates with an effective resistance
  of zero, negative included, overriding any penetration; the attack-only variant covers attacks only; a
  non-critical elemental hit keeps its capped resistance and `non_critical_strikes_penetrate_elemental_resistances_%`
  (10) subtracts after the cap, stacked with the hit's general penetration;
  `cannot_penetrate_or_ignore_elemental_resistances` restores the plain capped resistance.
  Physical, chaos and damage over time are outside the rules; Inevitable Judgement (graph node 48214) grants the
  generic ignore and the 10 penetration through the ordinary passive stat sum.
  The shared helper (`HitResistances`, the Gateway's `MonsterDamage`) covers default attacks, bows and gem hits.
  Default and area damage logs name the decision; gem projectiles emit a separate resistance trace.
  Core and Gateway checks cover positive/negative resistance, maximum independence and the node's exact stat keys.
  Eight native checks read monster life: fixed 100 fire dealt 60 normally and 70 with non-critical penetration.
  Guaranteed critical hits dealt 150 with generic or attack-only ignore, and 90 after removal or prohibition.
  Those captures belong to original head `13c41e5`; combined penetration/modifier/bow paths need native rechecking.
  Automated checks preserve same-key source sums, fractional totals, target damage taken and the original RNG order.
  Sequential C24 handler tests read real `/set_stat` changes after a prior hit on the same connection and target.
  Skill `0x102`, sequence 0 and flags `0x408` give ignore 300, ordinary critical 330 and prohibition 240.
  Friendly and hostile controls pass without a production hostility change.
  The combined run labelled `462c883` used a DLL proven by hash to be #225 `4a71968`; its ninth case is not #226 proof.
  Ascendancy allocation remains in its separate lifecycle change.
  Code: `HitResistances.cs`, `HitMitigation.cs`, Gateway (`MonsterDamage`).
  Docs: [20](../docs/protocol/20-damage.md) §4.
- **Client Buff descriptor ownership/transfer research (#370, #466, offline only).**
  Original bytes bind the prepared structure to item
  Mods `+0x38` and entity ObjectMagicProperties `+0xc8`, with a structural item-assignment path.
  A separate `+0x1d8` cache is excluded from the descriptor path by pointer provenance and record stride.
  This does not establish a meaningful descriptor consumer, actual target DoT publication or live behavior.
  The revised delivery records these structural findings; actual readers and outputs remain Known issues K1-K3.
  Original N1 is partial, N2 remains open and R2/R3/R4 stay Partial.
  See the [report](../docs/re/client-buff-descriptor-consumers.md).
- **Damage model (foundation, with armour/evasion).** A hit is a per-type `DamagePacket`;
  `DamageConversion` handles the game's physical, lightning, cold, fire, chaos order, chained conversion, skill/global
  precedence, the 100% cap and gain-as-extra.
  DoT is a single type with fractional tick carry, and `Ailments` has the ignite/bleed/poison base formulas.
  The default attack, Fireball and monster melee build packets and read the player's/skill's conversion stats;
  packet mitigation applies each type's own resistance, and the physical part then goes through armour.
  Righteous Fire's burn uses the DoT tick model.
  A damage-type immunity (`*_immunity`, `elemental_damage_immunity`) zeroes that type on the player's hits, from the
  same sources the panel's Immune rows use (`DamageImmunities.cs`).
  Global added attack damage per complete group of final Strength, Dexterity or Intelligence joins the raw attack
  range for the panel rows and the hit (Hyrri's Demise, (Replica) Iron Commander, Replica Alberon's Warpath per 80
  Strength despite its `_per_50_strength` id; [20](../docs/protocol/20-damage.md) §2b).
  The 3.29 whole-type item prohibitions ("Cannot deal non-Chaos Damage", "Deal no Physical Damage", ...) share one list:
  hits, the bleed, ignite and poison rows (each gated on its own type), the poison hits inflict and the caster's
  outgoing DoT drop those types.
  Automated tests only; the native check is pending.
  Hits taken read damage taken and Mana before Life stats ([20](../docs/protocol/20-damage.md#damage-taken-from-hits)).
  Ordinary player energy shield derives from equipment, intelligence and passives.
  Equipment ES uses the saved variable base roll and native rounding before and after local modifiers and quality.
  It absorbs non-chaos hits before life and recovers through regeneration and delayed, damage-interrupted recharge.
  The existing 16 ms elapsed-time loop supplies the client's native ES recovery prediction.
  Current ES and its prediction are sampled together at the network send gate, including duplicate suppression.
  Encrypted-wire held-gate tests cover a queued clamp, cleared prediction, death and unchanged-state suppression;
  native checks of this queued-send case remain pending.
  Live checks covered absorption, regeneration, interrupted recharge and chaos bypass.
  Zealot's Oath is implemented; other special ES keystones, recoup and conditional substitutions remain
  unimplemented; ordinary ES leech uses the shared recovery writer (§5b).
  See [ES scope](../docs/protocol/20-damage.md#player-energy-shield-2026-10-25).
  Maximum-mana added lightning has original-head native checks and resolved automated checks.
  The player's hits chill, freeze and shock monsters: threshold maximum life, strongest effect with its own duration,
  shock as increased damage taken (hits and Righteous Fire), chill and freeze in the monster AI and walk, the monster
  mods' ailment stats (of the Ice, of the Tempest, Unstoppable, Mirrored) and the chilled/frozen/shocked buffs.
  Elemental Focus and the never-ailment stats stop those ailments ([20](../docs/protocol/20-damage.md) §3).
  Chill can raise damage taken and lessen the monster's swings (Glacial Wave), a freeze can have a minimum duration
  and a monster that hits the player can be frozen (Brine Bound) or chilled (The Pandemonius).
  A monster hit the player blocks can be chilled or shocked for 4 s ("Chill/Shock Attackers on Block").
  "As though dealing more damage" raises the chill, shock or freeze it inflicts ([20](../docs/protocol/20-damage.md)).
  Shock and chill minimums (Beacon of Ruin) and caps can be raised ([20](../docs/protocol/20-damage.md)).
  "All/Chaos/... Damage can Chill/Freeze/Shock" count those types, and "Hits always Shock" needs no chance.
  A corpse keeps its killing hit's ailments and a kill can shock it, unless the hit killed it frozen and it shatters.
  Killing a shocked monster with Berek's Respite gives the monsters within 2 m the same shock.
  Each chill, freeze and shock spreads to living enemies within its own `virtual_*_proliferation_radius` of the
  monster or corpse it is on, like an aura.
  An ignite with a proliferation radius (Ignite Proliferation Support, gloves, notable, flask, main hand, Elemental
  Proliferation; the largest, grown by area) does the same: each AI tick the strongest spreading ignite in reach burns
  a living ignitable monster at its damage per second until it ends, competing with its own ignites. Corpses keep
  spreading until shattered or consumed. The copy does not spread or count as inflicting. Verified live 2026-11-08 (Fireball + Ignite
  Proliferation: 536 spreads at 16 cells, 246 burn kills, no client crash). Not yet: a killing hit's ignite on the
  corpse, the skill-less default bow attack, Berek's Respite's ignite twin.
  An area skill applies a Mirrored monster's reflected ailments after its last target, and a sweep stopped partway
  still rewards its kills.
  The buffs, the sent action speed and the frozen pose are not checked with the real client.
  See [20](../docs/protocol/20-damage.md#outgoing-chill-freeze-and-shock-on-monsters).
  Hits inflict elemental Exposure by their chances, -10% for 4 s by default ([20](../docs/protocol/20-damage.md) §2).
  Elemental Equilibrium removes the Exposure of a hit's elements and inflicts -25% Exposure of the others.
  Not yet: Exposure from skills and auras, aura-dependent penetration, other added damage,
  increased/more support modifiers and outgoing ailment application/duration ([20](../docs/protocol/20-damage.md)),
  except the player's chill, freeze, shock, ignite, bleed and poison on monsters.
  Projectile hits ignite from the panel's ignite rows captured at release, and the default attack and melee or area
  skills from the same rows at their hit; Abberath's Fury's footstep hits work out
  their own (90% of the hit's flat fire per second for 4 s, the ignite increases, damage over time multipliers, +50% on
  a critical strike, and duration stats).
  Both add to the monster's one set of ignites: only the strongest burns, through the monster's fire resistance, every
  player tick, with one timed `ignited` buff and kills.
  They burn on while the player is dead (unverified), with no progress from a dead Hardcore character's kills.
  The melee and area ignite and the footstep ignite are not checked with the real client (`MonsterIgnites.cs`,
  [20](../docs/protocol/20-damage.md) §3).
  Attack hits (the default attack, melee and area attacks, attack projectiles and bow shots) bleed monsters from the
  panel's bleeding rows with the attack bleed chances, for 5 s and the bleed duration stats: only the strongest bleeds
  (Crimson Dance: the 8 strongest), three times while the monster moves or aggravated, with one timed `bleeding` buff
  and kills (`MonsterBleeds.cs`; not checked with the real client).
  A hit's poisons deal the panel's poison rows (`MonsterPoisons.cs`), not checked with the real client.
  Incoming ordinary monster basic-contact ailments now have runtime and Gateway coverage:
  critical fire ignite, cold chill/freeze and critical lightning shock, with avoidance, expiry, immunity, lethal ignite/ES and freeze action cancellation.
  `action_speed_cannot_be_reduced_below_base` keeps the action factor at 1 under chill and freeze (Gateway coverage,
  not yet checked with the real client).
  Reversed chill (`chill_effect_is_reversed`) is increased action speed while freeze still stops the player, and
  `all_damage_taken_can_chill` chills from any hit's total damage (Surfcaster's Changing Seasons).
  The sent stats now include the final `virtual_action_speed_+%` that the native walk and action clips read; that
  native effect is not checked live.
  Native checks verify real monster ignite/avoidance, lethal death-panel/checkpoint behavior and nonlethal RF.
  Alternative ailments and hostile spell adapters remain unimplemented; monster mortar shots land through the same
  contact pipeline ("Monster projectiles" below).
  See [Gambler](../docs/game/gambler.md) for the exact bounded scope.
  Double damage chances double hits, and triple damage overrides them ([20](../docs/protocol/20-damage.md) §2).
  `unaffected_by_shock` keeps the shock and `is_shocked` without the damage-taken increase; `unaffected_by_ignite`
  keeps the ignite and `is_ignited` without its damage.
  "While moving" evasion and regeneration follow the server's walk, and more maximum attack damage scales every attack
  hit's maxima next to Rage; Gateway coverage only ([Polytheist](../docs/game/polytheist.md)).
  Impact, Close Combat and Shield Crush scale melee hits by proximity ([20](../docs/protocol/20-damage.md) §2j).
  Unconditional penetration works on the player's outgoing hits (general per-element and elemental stats, the
  attack-only variants, the cannot-penetrate flag; DoT and physical take none).
  Player and skill contributions to the same raw penetration stat add without overwriting either source.
  Nine native checks verified fixed 100-fire hits for 60 → 80 → 90 → 100 damage as penetration stacked,
  the prohibition returning 60, cold penetration giving 120 against zero resistance, and chaos 75 → 105.
  Each check read the actual native monster life after the authoritative update.
  Those captures belong to original head `0d800d1`; resolved bow and target-modifier paths need native rechecking.
  Target mods compose with base resistance before penetration; damage taken follows mitigation and rounds once.
  Ten fresh composed native groups verify critical rounding, active overcap and default-attack mana lightning.
  Actual mixed-hit Life losses are 160/220 before criticals, then 300 with ignore, 330 without and 240 prohibited.
  Build versions and source hashes match `1366f48`; supported-gem, modifier and bow combinations remain automated.
- Continuous life/mana recovery (2026-10-24): regeneration, flask recovery and self RF now share a 16 ms
  elapsed-game-time loop, replacing regeneration's 250 ms jumps; pool fractions and caps are preserved.
  Implemented with regression tests; real-client visual verification after Gateway restart is pending.
  See [20](../docs/protocol/20-damage.md), "Continuous resource updates".
- Consecrated ground while stationary (Paladin's Anointed Champion): a ground of the stationary radius stays where the
  player stands and goes when he moves, dies or is in a town; the effect of his grounds (the Sulphur Flask's too)
  lingers after the last moment one covered him, and `consecrated_ground_effect_+%` raises it (5% to 7.5% of life per
  second). Automated coverage only; the ground art and the linger are not checked with the real client.
  See [13](../docs/protocol/13-items.md) §8a and [Paladin](../docs/game/paladin.md).
- The player's debuffs on monsters: every warcry taunts the hostile monsters in its radius when its cast ends, the
  Basalt Flask in its 30 cells (3 s, `taunt_duration_+%`, the `taunted` buff with its two values); passive enemy auras
  (Light of Divinity's Augury of Penitence) sit on the hostile monsters near the living player.
  Their increased damage taken (taunted 20% from Bring the Battle, elemental 25%) adds to the monster's own in every
  player hit and in Righteous Fire's burn; `warcries_have_infinite_power` gives Enduring Cry the maximum charges.
  Hits Intimidate and Unnerve: 10% increased hit damage taken for 4 s ([11](../docs/protocol/11-skills.md) §12).
  Hits Wither: 6% increased chaos damage taken per stack, up to 15 stacks ([11](../docs/protocol/11-skills.md) §12).
  The other warcries' own buffs, exerts and summons, taunt immunity and other passive enemy auras are not done.
  Automated coverage only; not checked with the real client.
  See [11](../docs/protocol/11-skills.md) §12 and [Paladin](../docs/game/paladin.md).
- **Cyclone area of effect (2026-10-25).** `/set_aoe` now resends Cyclone's calculated final radius in the skill
  record, using the same radius calculation for server hits.
  Cyclone/CycloneAltX use a 16-cell base when their exported stat set has none.
  The player's increased area counts once for every area skill (`AreaPlayerStats`); it counted twice before.
  Spell-only area does not affect the attack.
  Tested calculations; live visual check pending.
  The first live check found that direct radius alone did not scale the channelled visual;
  its conditional path now also receives aggregate area (0xd7) and radius percentage (0x1591).
  Cyclone's additional melee-range and stage modifiers remain partial.
  Its sweeps follow the player walking toward the aimed cell (C→S 0x197) at the gem's movement speed, not the cursor.
  Checked with the real client (2026-11-08): the sweeps advance with the player.
  See protocol/11-skills.md.
- **Self-targeted spells (2026-10-29).** Self-targeted spells (ActiveSkillTargetTypes 4 or 10: Ice Nova, Discharge,
  Shock Nova) hit around the caster wherever they are aimed, as the client draws them; before, an Ice Nova cast on a
  monster went off around the monster.
  Totem, mine and trap skills and Novas without a self target (Shockwave Totem, Stormblast Mine) keep the
  aimed point.
  With `nova_spells_cast_at_target_location` (Devout of Lunaris) self-targeted Novas hit around the aimed point, and
  Nova skill records carry `skill_is_nova_skill` and, with the stat, the derived
  `virtual_self_targeted_skills_target_location_instead`, so the client casts them at the cursor (live 2026-10-30).
  Area skill records carry their final `active_skill_area_of_effect_radius`, which the client draws them at; without it
  Ice Nova drew nothing (live 2026-10-30).
  Code: `SkillCatalog.IsSelfTargeted`, `SkillCatalog.IsNova`, Gateway (`SpellCentre`, `AreaSkillHit`).
- Ice Shot: its primary arrows (`IceArrow`) hit; the cone splash is open.
  Docs: [11](../docs/protocol/11-skills.md) §10.
- Multi-strike skills (Double Strike): each strike is its own hit, sharing one critical roll.
  Docs: [11](../docs/protocol/11-skills.md) §9a.
- Combat load: input-checked caches and snapshots; pathfinding and load spikes are still under investigation.
  Docs: [performance](../docs/dev/performance.md).
- Survivors league and its three optional modules. Docs: [Survivors mode](../docs/game/survivors-mode.md).
- Attack-speed admission and projectile speed for gem attacks. Docs: [11](../docs/protocol/11-skills.md) §10.
- Lifetap Support. Docs: [25](../docs/protocol/25-gem-supports.md).
- Blood Magic. Docs: [25](../docs/protocol/25-gem-supports.md).
- Bow arrow counts from bow and quiver modifiers. Docs: [11](../docs/protocol/11-skills.md) §10,
  [RE count inputs](../docs/re/projectiles/count-stat-inputs.md) §3.
- Tornado Shot's terminal secondary arrows. Docs: [11](../docs/protocol/11-skills.md) §10,
  [RE terminal geometry](../docs/re/projectiles/tornado-geometry.md).
- Kinetic Bolt's zig-zags. Docs: [11](../docs/protocol/11-skills.md) §10.
- Petrified Blood. Docs: [20](../docs/protocol/20-damage.md) §2i.
- Shared Mana/Recoup and Blind from Whisperer's nodes. Docs: [Whisperer](../docs/game/whisperer.md).
- Selected supported-spell panel. Docs: [11](../docs/protocol/11-skills.md) §9.
- Percentage attributes, attribute-scaled pools and defences, and inherent-bonus prohibitions.
  Docs: [11](../docs/protocol/11-skills.md) §4, [13](../docs/protocol/13-items.md) §2d,
  [20](../docs/protocol/20-damage.md#player-energy-shield-2026-10-25).
- Weapon-local damage and timing. Docs: [20](../docs/protocol/20-damage.md) §2a.
- Projectile framework: one 30 Hz runtime for gem and bow projectiles.
  Docs: [framework](../docs/game/projectile-framework.md), [client research](../docs/re/012-projectile-execution.md).
- Arcane Might (#364). Docs: [Arcane Might](../docs/game/arcane-might.md), [20](../docs/protocol/20-damage.md) §2c.
- Ruthless and Hardcore rules per connection. Docs: [league modes](../docs/game/league-modes.md).
- Grace Period on area entry. Docs: [11](../docs/protocol/11-skills.md), "Grace Period on combat-area entry".
- Vaal souls (#308) and Vaal Ground Slam. Docs: [27](../docs/protocol/27-vaal-souls.md),
  [Behemoth](../docs/game/behemoth.md).
- Ground Slam's forward wave, and gem melee swings that keep their hit over a queued Move.
  Docs: [11](../docs/protocol/11-skills.md) §9a.
- Warcry release and cooldowns. Docs: [11](../docs/protocol/11-skills.md) §10i.
- Strike targeting: spirit strikes and the previous location. Docs: [11](../docs/protocol/11-skills.md) §10d.
- Passive enemy auras. Docs: [11](../docs/protocol/11-skills.md) §10e.
- Passive-triggered skills: Ancestral Commander's Spirit chieftains.
  Docs: [11](../docs/protocol/11-skills.md) §10f, [20](../docs/protocol/20-damage.md) §2e.
- Player minions that fight. Docs: [11](../docs/protocol/11-skills.md) §10h.
- Life Recoup. Docs: [20](../docs/protocol/20-damage.md) §5.
- Ancestral Commander defences. Docs: [Ancestral Commander](../docs/game/ancestral-commander.md),
  [20](../docs/protocol/20-damage.md) §4.
- Aura restoration bootstrap fix. Docs: [11](../docs/protocol/11-skills.md), "Aura restoration bootstrap correction".
- Aura lifecycle: town suppression, restoration and death. Docs: [11](../docs/protocol/11-skills.md), "Aura lifecycle".
- Aura presentation: distinct art, and source and received buffs together.
  Docs: [11](../docs/protocol/11-skills.md) §13.
- Ordinary aura expansion: Determination, Grace, Anger, Hatred, Wrath, Zealotry, Malevolence and Envy.
  Docs: [11](../docs/protocol/11-skills.md) §13.
- Flesh and Stone and Blood and Sand. Docs: [11](../docs/protocol/11-skills.md) §12.
- Passive-granted skills and Summon Elemental Relic.
  Docs: [11](../docs/protocol/11-skills.md) §13, [Paladin](../docs/game/paladin.md).
- Owned minions: relics, chieftains and the 17 golems. Docs: [11](../docs/protocol/11-skills.md) §10h.
- Link skills. Docs: [11](../docs/protocol/11-skills.md) §13, [Paladin](../docs/game/paladin.md#link-skills).
- Saved aura toggles. Docs: [11](../docs/protocol/11-skills.md), [21](../docs/protocol/21-hideouts.md).
- Spells disabled (Devout of Gruthkul). Docs: [11](../docs/protocol/11-skills.md) §10j.
- Passive blind aura. Docs: [11](../docs/protocol/11-skills.md) §12.
- Passive-granted footstep skill (Abberath's Fury). Docs: [11](../docs/protocol/11-skills.md) §12.
- Projectile modifiers and named projectile skills. Docs: [framework](../docs/game/projectile-framework.md),
  [skills](../docs/game/projectile-skills.md), [20](../docs/protocol/20-damage.md) §3.
- Instant skills, cooldowns, Bladefall of Trarthus and Frostblink. Docs: [11](../docs/protocol/11-skills.md) §9, §11.
- Auras, reservation, Pride, heralds and permanent spell buffs. Docs: [11](../docs/protocol/11-skills.md) §13.
- Animated equipment, summons, mines and traps. Docs: [11](../docs/protocol/11-skills.md) §10h.
- Hexes and marks. Docs: [11](../docs/protocol/11-skills.md) §12.
- Totems: Rejuvenation, projectile spells and ballistas. Docs: [11](../docs/protocol/11-skills.md) §10g.
- Passive self buffs. Docs: [11](../docs/protocol/11-skills.md) §12.
- Onslaught on kill and from a Silver Flask. Docs: [13](../docs/protocol/13-items.md) §8a.
- Rampage and Soul Eater from item stats. Docs: [11](../docs/protocol/11-skills.md) §12.
- Stats: the client's stat keys and the player's base pools. Docs: [11](../docs/protocol/11-skills.md) §4, §8, §14.
- Attack speed passives and attack timing. Docs: [11](../docs/protocol/11-skills.md) §4.
- Spell cast timing (#192). Docs: [11](../docs/protocol/11-skills.md) §9.
- Selected spell hit damage panel (#188). Docs: [11](../docs/protocol/11-skills.md) §9 and §14.
- Critical strikes. Docs: [11](../docs/protocol/11-skills.md) §5.
- Overcapped-resistance modifiers. Docs: [11](../docs/protocol/11-skills.md) §4, §5 and §12.
- Projectiles: release, contact, lockstep client shots and monster mortars.
  Docs: [framework](../docs/game/projectile-framework.md), [11](../docs/protocol/11-skills.md) §6 and §10.
- Combat: server-side damage, monster AI and player hit recovery. Docs: [11](../docs/protocol/11-skills.md) §5–6.
- Critical-strike elemental resistance rules (Inevitable Judgement). Docs: [20](../docs/protocol/20-damage.md) §4.
- Client Buff descriptor research (#370, #466, offline only).
  Docs: [report](../docs/re/client-buff-descriptor-consumers.md).
- Damage model: packets, conversion, ailments, penetration and player ES. Docs: [20](../docs/protocol/20-damage.md),
  [Gambler](../docs/game/gambler.md), [Polytheist](../docs/game/polytheist.md).
- Continuous life and mana recovery. Docs: [20](../docs/protocol/20-damage.md), "Continuous resource updates".
- Consecrated ground while stationary. Docs: [13](../docs/protocol/13-items.md) §8a,
  [Paladin](../docs/game/paladin.md).
- The player's debuffs on monsters: taunt, enemy auras, Intimidate, Unnerve and Wither.
  Docs: [11](../docs/protocol/11-skills.md) §12, [Paladin](../docs/game/paladin.md).
- Cyclone's area of effect. Docs: [11](../docs/protocol/11-skills.md), "Cyclone area updates".
- Self-targeted spells. Docs: [11](../docs/protocol/11-skills.md) §9a.
- Winter Orb. Docs: [11](../docs/protocol/11-skills.md) §9a.
- Dark Bargain (self-cast only; Summon Skeletons does not work yet). Docs: [11](../docs/protocol/11-skills.md) §9a.

## Not working yet

- Projectile spawn height on platforms and doodads: the server has no surface-height model (#182).
  Docs: [11](../docs/protocol/11-skills.md) §10.
- Spirit of Utula's Leadership and Rakiata's Mark trigger need their own chieftain executors.
  Docs: [11](../docs/protocol/11-skills.md) §10f.
- Where Flicker Strike lands beside the monster is the server's guess, and its buff ignores increased buff effect.
  Docs: [11](../docs/protocol/11-skills.md) §9a.
- Holy Sweep's hammers deal no damage: the server has no hammerfall hits.
  Docs: [11](../docs/protocol/11-skills.md) §9a.
- Effects attached to bones (Righteous Fire's orbs) are not sent; banners and combat auras are not implemented.
  Docs: [11](../docs/protocol/11-skills.md) §12.
