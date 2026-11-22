# Roadmap: Skills, auras, stats and combat

Part of the [Roadmap](../Roadmap.md); what already works in this area is in
[status/skills-and-combat.md](../status/skills-and-combat.md).
Items keep the Roadmap's priority groups, and are in rough priority order within each group.

## 1. Quality of life

- **Aura round trips:** validate the own-aura restriction's native grey-icon prediction and broader
  town/combat/character-switch round trips.
  The suspend/restore lifecycle is LocalExile policy ([11](../docs/protocol/11-skills.md) §10c and §13).

## 3. Gameplay

- **Fortification extensions:** other conditional sources, permanent grants and monster ailment thresholds.
  See [Fortification](../docs/game/fortification.md).
- **Aura caster art:** confirm the received buff's top-left icon live; radius-scaled aura rings need their radius.
- **Ordinary Ice Shot:** verify the cone's damage aperture and contact boundaries, including successive pierced
  targets, before implementing the fully cold area hit. Check the three impact art tiers in an isolated client
  with quality and helmet changes. [#925](https://github.com/LocalExile/LocalExile/issues/925)
- **Spark start cell:** the server flies each spark on the client's wander but from the server's walk position, so a
  drawn spark can still hit with no damage.
  Start: trace the client's start cell and each leg's start time per frame (`scripts/re/frida/trace-spark-legs.js`)
  next to the server's `spark seq=` lines, then start the server's legs from the same cell and frame.
  Done when: every drawn spark hit deals damage once, and no damage lands without one.
- **Spectral Throw hit timing:** hits can land before or after the drawn weapon reaches a monster, most on the return
  toward monsters walking at the thrower ([11](../docs/protocol/11-skills.md) §10).
  Start: trace the client's weapon (`scripts/re/frida/trace-projectile-path.js`) next to the server's hit lines on one
  clock, with pauses and default attacks between throws; then read every class and stance's Melee views from the ACT
  and .ao in `scripts/re/anim_contacts.py` instead of `SpectralThrowRelease`'s table.
  Done when: each hit lands as the drawn weapon reaches the monster, out and back, for every class and weapon.
- Verify the native repeat of Ice Nova of Deep Freeze.
- Verify Ice Nova of Frostbolts live at the 100-cell targeting boundary and follow its rings on two moving Frostbolts.
- Verify Vortex in a native session, including the explosion, removal of predicted Frostbolts, ground coverage on
  spawning or emerging monsters, marked-target placement and worn effects. Check triggered or proxy Vortex releases
  and Runegraft of the Novamark on other nova actions. Verify ordinary Shock Nova placement and timing live,
  including worn effects. Verify Procession's moving marks, directional repeats, terrain edges and proxy casts live.
  Verify Fireball, Spark, Freezing Pulse and Frostbolt in a native session with a moving object and a delayed
  Lightning Warp landing during Spell Echo. Verify the Witch's visible Elemental Staff clip across weapon swaps
  in a native session. Check whether a runtime event can refresh the bound target (`0xc4a5`) during those
  casts, and verify when a Move interrupts an ordinary cast or its final Echo in the client.
  Trace turns from movement skills and channels before a stationary Procession.
- **Cyclone's area:** check the `/set_aoe` radius live.

### The player's real stats

The issue #75 target is the 357 virtual stats behind the character panel ([11](../docs/protocol/11-skills.md) §14).
Each needs its sources, consumers and lifecycle traced; no completion follows from a historical native run or a
matching source name.
The issue #75 native matrices were run on the source LocalExile revisions, not repeated on this port.

- **Accuracy and criticals:** review the remaining ordinary-bow snapshots and special repeats, complete the remaining
  accuracy conditions, correct off-hand sources and publish all chance/always-hit rows; dual-hand, multi-hit and
  integer-roll expectations are open ([20](../docs/protocol/20-damage.md) §2h).
- **Impale:** conditional weapon/Pride chance, extra applications and consumption overrides, Impaler mechanics,
  native count/visual publication, corpse consumers and official duration rounding ([20](../docs/protocol/20-damage.md)
  §3a).
- **Cooldowns:** remaining stored-use sources, conditional recovery modifiers and official tick boundaries
  ([11](../docs/protocol/11-skills.md) §9).
- **Attack timing:** an attack longer than its clip stretches the clip's slow span, which `AttackContactDelay` does not
  model; the arrow arrives about 25 ms after the muzzle flash, with a fixed 60 ms lead tuned on localhost rather than
  one that follows the connection's latency ([11](../docs/protocol/11-skills.md) §4).
- **Spell and attack panels:** integer-roll expectation, cooldowns, DoT/beam displays, in-place link mutation,
  conditional damage and support/modifier scope; check equipment UI refresh live.
  Attack base/added coefficients are separate; negative weapon conditions, remaining weapon families,
  skill-specific added inputs and native checks stay in #368.
  Verify the native panel across helmet swaps for skill-specific damage, Toxic Rain's physical damage
  gained as extra chaos, and the projectile counts for Toxic Rain, Spark and Split Arrow. Check both helmet tiers
  with Split Arrow of Splitting in the client. Local casts cover its split targets, Vortex ground and Toxic Rain pods.
  Other standalone damage-over-time executors still need equipment checks. [#1181](https://github.com/LocalExile/LocalExile/issues/1181),
  [#1184](https://github.com/LocalExile/LocalExile/issues/1184), [#1185](https://github.com/LocalExile/LocalExile/issues/1185),
  [#1186](https://github.com/LocalExile/LocalExile/issues/1186)
- **Warcries:** executors for the other warcries; explosions need global added damage, flat crit chance, on-hit
  effects, their native art and the wave delay ([11](../docs/protocol/11-skills.md) §10i).
- **Character panel (#75)** ([11](../docs/protocol/11-skills.md) §14):
  - Regeneration: other conversions than Zealot's Oath, named conditional/more modifiers and other recovery systems.
  - Charges: Brand attachment/cap enforcement, the Affliction non-damaging effect and Absorption recoup consumers.
    Reap's damage over time debuff, Vaal Reap's temporary Blood Charge maximum, and executing the secondary hits
    beyond Bear Trap's hit (Herald of Ice, Bear Trap's immobilise and decaying debuff).
    The charge gains, triggers, per-charge targets and charge types [11](../docs/protocol/11-skills.md) §15 lists as
    not modelled (Divine, Brine, Crab, Fanatic and Celestial Charges, and stealing).
  - Deferred (owner's decision, 2026-10-29): Spirit and Siphoning Charges. Their only 3.29 player sources are unique
    mods (`maximum_spirit_charges_per_abyss_jewel_equipped`,
    `maximum_siphoning_charges_per_elder_or_shaper_item_equipped`), so they need unique definitions beyond
    Mageblood (#60), abyss sockets on the other uniques that have them and a consumer for `Item.Influences`
    ([13](../docs/protocol/13-items.md) §2f). Blitz and Challenger have no 3.29 source.
  - Penetration: the conditional forms not read yet (Zealotry's critical-only, Enmity's
    Embrace's overcapped fire, recent kill or block, flask, tincture, while chilled, single-skill).
  - Ailments: validate the twelve duration/effect aliases, then ailment chance/eligibility, damaging ranges,
    duration/application and stacking. Left/right ring operators are slot-local: do not sum them globally.
  - Defences: official fractional/group arithmetic, the remaining prohibitions and conversions, all-source immediate
    Life publication, other Shaper's Touch effects, Ward and the conditional/more pool operators.
  - Attributes: signed equipment cycles, attribute conversions, conditional/more operators and mercenary gates.
  - On kill and flasks: full on-kill recovery/percentage/prohibition rules, block-animation timing, conditional flask
    sources and queued timer/predictor presentation.
  - Leech ([20](../docs/protocol/20-damage.md) §5b): other conversions, conditional and weapon-family sources,
    skill-specific recovery speeds and the speed/defence/ailment/DoT leech-state consumers; other Life-retention
    sources. Do not add overlapping displayed scopes, substitute regeneration for leech, broaden the attack-only item
    scope, infer Vaal Pact from legacy raw flag names, or multiply one boolean doubling per item copy.
  - Damage: alternative quality, endpoint-only modifiers and official rounding.
    Hits still multiply a target's physical damage taken and Maim separately; DoT adds them ([20](../docs/protocol/20-damage.md)).
  - Dual wielding ([11](../docs/protocol/11-skills.md) §4): when the client calls the hand selector and its stage for
    Multistrike repeats and Barrage's projectiles (they keep the use's hand) and channels (Cyclone keeps the main hand);
    the off hand's own stun threshold reduction and weapon-family modifiers; a wand beside a melee weapon (the default
    attack keeps the main hand); both-hand skills' on-hit locals and ailments from the off hand; Varunastra's hand
    eligibility; a live check that the server's hand matches the client's swing.
  - Righteous Fire: verify final burn timing and rounding in the client on gem removal, weapon swap and loss of
    required attributes.
  - Continue auditing the remaining rows against the current implementation before adding formulas.
- **Done when:** life, mana, attributes, resistances and their derived values come from the server's own calculation
  (Characters.dat base values, level, items, passives) and are sent in the Stats component.
  Open beyond that: own-item percentage retention, cascading equipment, passive refunds and swaps natively; Power of
  Purpose's idle reservation, Iron Reflexes and MORE natively ([Power of Purpose](../docs/game/power-of-purpose.md));
  official-server fractional parity; Phrecia play compatibility.

### Auras: reservation and effects

- **Known:** [11](../docs/protocol/11-skills.md) §13 has reservation, the implemented auras and the own-aura lifecycle.
  Passive enemy auras other than the implemented ones (the Occultist's) need consumers for their stats first (§10e).
- **Start:** the auras' 3D effects (BuffVisuals EPKs of the `IsSkillBuff` rows do not render, unlike Righteous Fire's);
  banners, enemy aura executors, the other Vaal auras and unsupported conditional effects.
  Vaal auras: allies do not receive them, and no skill bar soul gain prevention countdown is shown.
  Verify the herald combat matrix, retail Virulence decay, the skill-specific trap/mine payloads beyond Skitterbots'
  trigger/rearm, and the skill-bar indicator's buff association (0x1c1 bit3) live.
- **Done when:** the standard auras reserve correctly, their effects match PoB, and the character panel and HUD show
  the same reserved and unreserved values.

### Area-entry protection verification

- **Start:** verify the Grace Period's 60-second countdown, first-step removal and damage prevention live.
- **Done when:** the timer counts down and protection ends on movement, actions or expiry.

### Damage model (foundation done, deeper defence next)

- **Client Buff descriptor consumer follow-up (#370, Known issues K1-K3):** follow one prepared descriptor
  from `ChaosDegenerationAuraPlayersUniqueBodyStr3` to an actual reader and explain its output.
  The [ownership/transfer report](../docs/re/client-buff-descriptor-consumers.md) is the bounded #466 delivery.
  Resume only with new same-object transfer or typed-reader evidence, as a separate batch under #370.
- **Known:** [20](../docs/protocol/20-damage.md) has the damage model; both sides still have flat damage ranges
  ([11](../docs/protocol/11-skills.md) §5, §6). Monster armour is not modelled.
- **Start:**
  - Special ES mechanics, skill Exposure, the remaining conditional penetration, damaging ailments on monsters beyond
    ignites and bleeds, and further added-damage/support effects.
  - Scorch, Brittle and Sap sources not read yet: Oath of Summer (Warden: ignite becomes scorch, a second scorch),
    the confluxes (Leadership's Price), Scorched/Brittle/Sapped Ground, "while affected by" auras, skill-named
    and Prismatic sources, chilling areas, proliferation and "recently" conditions. Sap on the player needs its less
    damage on the player's hits first (then Simulacrum's "Monsters inflict Sapped" can roll). Scorch and Brittle do not
    reach the hits that read a monster's resistances or roll crits without a time: Rampage weapons, boss hazards
    (Doedre, Sirus, Kitava, arenas, Vaal Oversoul) and shrine novas.
  - Remaining weapon-scoped fire per Strength, lightning per Dexterity/Intelligence and spell per-Intelligence added damage.
  - Monster skills beyond the first granted effect (ranged, spells).
  - Player stun: ES avoidance, substituted thresholds, conditional defences and monster-specific modifiers.
  - Criticals: other conditional more chance, guaranteed-critical effects, off-hand chance/multiplier publication and
    execution, Focus/curse lifecycles. The off-hand range/timing publication has no critical fields: do not infer
    completion from its other weapon fields.
  - Whisperer, Arcane Might, Steel Willed, Rampager and Behemoth's remaining scope is in their docs
    ([Whisperer](../docs/game/whisperer.md), [Arcane Might](../docs/game/arcane-might.md),
    [Steel Willed armour](../docs/game/steel-willed-armour.md), [Behemoth](../docs/game/behemoth.md)).
  - Frostblink: moving targets, use during an active walk and enemy-dependent cooldown recovery. Extend terrain and movement restrictions from native evidence before accepting
    other instant movement skills.
  - Add support effects through `DamagePacket.Add`, `Scale`, `Convert` and `AddGain`. When owned-actor execution is
    implemented, supply the actual caster's stats to `SkillDamageContext`; do not inherit an owner's spell-damage
    restriction onto independent minions without a skill rule.
  - Recheck natively: supported-gem, modifier and bow combinations; critical ignore against a rebuilt DLL; the
    resolved bow-contact and target-modifier composition; overcapped-resistance and critical rounding on the resolved
    stack; Controlled Destruction and the unique -40% critical source; queued ES capacity changes.
- **Done when:** damage numbers match the character panel and the monsters' levels.

### Elemental ailment extensions

- Check the sent `virtual_action_speed_+%` and the frozen pose with the real client.
  Open: the shatter visual (the packet that removes the corpse is not known), unique/boss thresholds, the monster
  variety's own immunities and a native check of the proliferation radius's area scaling
  ([20](../docs/protocol/20-damage.md#outgoing-chill-freeze-and-shock-on-monsters)).
- Dedicated native buff icons and source/threshold/conditional-immunity parity before a general ailment engine;
  hostile spell adapters and alternative-ailment sources ([Gambler](../docs/game/gambler.md)).

### Rage extensions

- Native composition with supported projectiles, area sweeps, equipment sources, notification delays and slow
  destination loading; queued refreshes across expiry and decay-speed modifiers.
  Capped regeneration now refreshes activity at any advance cadence (policy v2); measure
  official capped-attempt refresh for discrete gains and area-loading timer parity before replacing policy v2.
  See [26-rage.md](../docs/protocol/26-rage.md).

### Vaal execution and remaining soul checks

- Native acceptance of duplicates, removal/readd, cooldown refresh, held death notifications, Vaal Haste/Grace aura
  costs, death/resurrection and slow destination loading ([27](../docs/protocol/27-vaal-souls.md)).
  Validate the [modular Behemoth candidate](../docs/game/behemoth.md) natively; other Vaal executors and official
  distribution/capacity parity remain open.
  Vaal minion totem, trap and mine execution remains open; these casts are refused before souls or actions are spent.
  Vaal Animate Weapon, full ascended special attacks and minion stance targeting remain open.

### Death screen variants

- **Known:** S→C 0x55 flags bit0/2/3 are mapped ([11](../docs/protocol/11-skills.md) §7);
  bit4 and the trailing u32 / u8 / u16 are not.
- **Done when:** Sanctum and other modes show their own options.

### Skill projectiles

- **Client-contract research:** use the frozen [delivery matrix](../docs/re/projectiles/delivery-matrix.md)
  before extending the simulator or changing phase prediction ownership.
  Keep the [K1/K2/K3 followups](../docs/re/projectiles/verification-and-limitations.md) separate.
  Do not make full rendering, every stat producer or complete engine teardown prerequisites for using closed rules.
- **Known:** the [framework](../docs/game/projectile-framework.md) owns production routing and the only simulator;
  [registered skills](../docs/game/projectile-skills.md) add special behaviour. Tornado Shot's secondary group now
  starts in the primary's final return phase. Ordinary and returning speed increases now add in that phase.
  The speed masteries and the skill's own final speed now scale flight. Check Ethereal Knives quality,
  Vengeant Cascade returns and the secondary arrows' radial flight in both clients.
- **Open:**
  - Nonzero intrinsic attack repeats; per-skill duration before the 150-cell cap and a
    native check of the continuation boundary; the action-speed/Tailwind contract (#246).
  - Verify both Ball Lightning orbs' visible launch cells, bolts, first and last strikes, and expiry in both
    clients. Compare a close player cast and a spell totem cast with the 3.29.3c forward release, and
    compare single and multiple orbs when a strike coincides with a wall impact. Check that each visible
    trap or mine projectile stops in the last open cell when several reach a wall together. Check how
    the client draws the last part of an orbit when its final waypoint falls short of the stop.
    The Static variant still needs a separate check (#1101).
  - Combat body geometry: actor positions use integer walk estimates and Gateway actor radii are zero, so area
    coverage is centre-based; calibrate against `ObjectSize`.
  - Monster weapon projectiles: `monster_projectile_weapon`, `monster_snipe` and `monster_puncture` (109 varieties'
    first effect) take their projectile from the monster's weapon art (MonsterVarieties
    `Weapon1_ItemVisualIdentityKeys`, e.g. `SkeletonArrow1`), which the server does not read, so they hit instantly
    with no shot. Monster spell projectiles (`MonsterProjectileSpell`) need their own adapter.
  - Mortars: the splash radius is a development default and `projectile_spread_radius` (3) is not modelled; monster
    melee still lands at half the attack time although the pools carry `Melee` keyframes. Check live whether the
    client seeds a monster's clip choice from the 0xffff sequence (the Spitter's attack2 would spit 75 ms earlier and
    1.3 cells farther back), the handedness of a keyframe's lateral offset, `MinimumAttackDistance` and the dead zone.
- **Live reference** ([11](../docs/protocol/11-skills.md#17-live-reference-ranged-attacks-on-gggs-servers)):
  probe whether a predictive client draws its own shot from the record's count before dropping its server arrows;
  measure where the client's mortar lands against the server's hit, and trace and probe the spell mortars
  (`monster_mortar`, `monster_mortar_around_caster`) before dropping their server objects.
  Explain the hit-timing fit (the arrow releases at the table's 299.9 cells/s, close to live's 308, yet hits arrive
  2.72 ms per cell against live's 3.25), add the 300 ms stun bit on hits, find why a level 1 Sand Spitter hits for 5–7
  here and a constant 4 on live, and decode S→C `0x1b4` (player only, the attack's skill id and a point list).
- **Start:** user-run Fireball/KB checks from the skill guide, including return, terrain and overlapping explosions.
  Check a near-wall Fireball impact and a stuck arrow when the server first presents them after contact.
  Check the aimed-height model live, then find the client's own projectile height/terrain rule for its predicted gem
  projectiles and replace the 12-step clearance; send the wall-hit sound of a server `DefaultArrow` at terrain.
  Calibrate cluster radial distribution, sequence timing and native art against client evidence.
  Build a surface-height/world model for platforms and doodads (#182), height grids for more areas, and collision with
  doodads and objects that have their own colliders.
- **Done when:** Fireball and bow arrows look like the real ones in every area: at the caster's torso on terrain and
  platforms, stopping at walls and objects.
