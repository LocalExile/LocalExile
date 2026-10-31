# Projectile skills

The [projectile framework](projectile-framework.md) supplies one simulator, common modifiers, damage snapshots and
native output. `src/GameServer.Gateway/Projectiles/Skills/` registers named stages and catalog return policies.
A recipe attaches its intrinsic stages to the unresolved common baseline, then resolves generic modifiers once.
Ordinary bows and catalog gems retain the same counts, scoped extras, aim-sensitive spread, continuation priority,
prohibitions and phase-specific direct admission. Catalog metadata supplies straight flight for other projectile gems;
it does not establish those skills' special mechanics. Missing metadata or damage plans are refused before costs.

## Implemented behavior

| Skill | Enemy contact | Terrain | Maximum range / return endpoint | Quantity and area damage |
|---|---|---|---|---|
| Fireball | Full direct hit while continuing. One terminal area when no continuation remains; its collision victim receives one area-classified hit through direct admission | Terminal area if return cannot continue | Terminal area at the actual stop; a successful return defers it | Normal primary quantity; radius from exported level/quality stats |
| Kinetic Blast | Direct hit and four independent offset explosions at every admitted impact, including impacts that pierce/fork/chain/split/return | One cluster group, including turnaround; blocked placement sectors are skipped | One group when stopping, including range turnaround before return; another at final return stop | Normal primary quantity; explosions alone have 40% less area damage |
| Burning Arrow / of Vigour (`BurningArrow`, `BurningArrowAltX`) | Catalog straight flight; 100% physical-to-fire conversion; 25% base ignite chance, critical hits always ignite ([20](../protocol/20-damage.md) §3) | Common terrain and aimed height | Common | Normal primary quantity; quality is 1% more ignite damage; Vigour adds fire equal to 21–32% of maximum Life |
| Kinetic Blast of Clustering (`KineticBlastAltX`) | Direct hit and three independent offset explosions, modified by projectile quantity | Same terrain contract | Same stop contract | One primary; extra generic and attack projectiles increase cluster count; cannot Split; no native 40% penalty |

`KineticBlastAltY` is exported as `[DNT] KineticBlastAltY`. It uses the normal KB data profile for compatibility;
no public evidence here establishes a third playable gem. The normal/Clustering base cluster totals are four/three,
not five/four inferred from tooltip wording about additional explosions. Exported `cluster_burst_spawn_amount` wins.

Native skill-bar records publish resolved projectile count, pierce/fork/chain/split and return separately from raw modifiers.
KB also publishes `virtual_kinectic_blast_number_of_clusters` (the exported spelling) from its contact-area recipe.
Normal KB with GMP fires five projectiles while keeping its per-impact explosion count; Clustering fires one projectile
and adds GMP's four extra projectiles to its explosion count. Gem quality contributes before this projection.
Unlinking supports rebuilds these display totals; projection does not change gameplay inputs or apply modifiers twice.
The client selects native wording; visible layout still requires user-operated validation.
Skill parameter overrides also apply without native projectile metadata: known return prohibitions still suppress
return descriptions, while the lack of an executable definition continues to reject release.

A simultaneous volley admits one direct collision per target and phase. Denied siblings pass through without
consuming continuations or spawning collision explosions. Return opens a separate shared admission phase.
Fireball's terminal victim uses this direct admission but receives the captured area packet, so area increases and
Concentrated Effect apply once without a direct-plus-blast double hit. Each distinct explosion admits covered targets
independently: each KB contact group excludes only the enemy that triggered that group, including its delayed slots.
Explosions triggered by other enemies, walls or range stops can still hit that enemy and overlap independently.

Split, Pierce, Fork, Chain and Return use the common ordered continuation rules. Finite pierce hands off after its
last grant; prohibitions win. Clustering declares its area count consumer before resolution, so Fury Valve's quantity
still increases explosions while its Split cannot run. This composition has community corroboration rather than an
independent official current-version gameplay measurement.

A natural maximum-range stop and a stat-forced distance endpoint both produce the selected endpoint area. Neither
is a physical wall collision. Successful return defers Fireball's area but triggers KB's stop cluster before redirect.
Crossing the caster does not itself create a stop or explosion. Lifetime expiry, cancellation and teardown are silent
for these recipes; they are not classified as explosive distance/terrain stops. Exact lifetime-expiry equivalence
remains unmeasured. Return preserves remaining continuations and lifetime, excludes the turnaround victim and uses
the common fixed 150-cell overshoot. Only `returning_projectiles_always_pierce` grants return pierce-all. Terrain is
still active on return, and ordinary return does not receive a support-only damage penalty.

Named cannot-return inventory: Spark/AltX/AltY, RainOfArrows, BlastRain, SplittingSteel, VenomGyre, WinterOrb,
ForbiddenRite, Cremation, RollingMagma, ToxicRain and PoisonousConcoction. FreezingPulse returns only from collision.
These policies do not claim complete behavior for each catalog skill.

## Radius, damage and presentation

| Profile | Base blast radius (cells) | Base placement radius (cells) | Explosion multiplier |
|---|---:|---:|---:|
| Kinetic Blast / internal AltY | 14 | 20 | 0.6 |
| Clustering | 19 | 28 | 1.0 |
| Fireball | Exported level/quality radius: 10–18 before quality | Centered | 1.0 |

The exported blast and secondary placement radii override the fallback values above. Release specialization overlays
the supported skill stats onto area-specific player stats, excluding the player's base increased area already present
in the panel total. Each radius is rebuilt from its own base and the shared supported area multiplier exactly once.
The generic damage adapter captures separate direct/area packets, including area increases and area-only supports;
KB applies `active_skill_area_damage_+%_final` only at the explosion stage. Skill-owned Arcane Might and Clustering's
maximum-mana physical addition enter the common attack damage construction, before stage multipliers.

Each group chooses a random initial rotation and divides the circle into N equal sectors for its resolved explosion
count. One angle is sampled independently inside each sector, visited counter-clockwise. Centers lie on the fixed
placement circle: 20 cells for normal KB and 28 for Clustering before area scaling. Blast and placement radii scale
separately. Terrain-blocked center paths are omitted without replacement or redistribution and consume no time slots.
Centers and terrain admission are captured at impact/stop; actors and defenses resolve at each later blast.
The fixed-circle geometry is a development approximation, not a measured official distribution. Unusual target sizes
can overlap geometrically; Core supports actor radii while the Gateway currently uses monster centers of radius zero.
Shared area scaling preserves the blast/placement ratio, not the hit count on a stationary nearby target.
For example, normal KB's inner uncovered distance grows from 6 to 12 cells when both radii double.
More area therefore increases overall coverage without guaranteeing single-target or nearby-target overlap.

`ClusterDelaySeconds = 0` and `ClusterIntervalSeconds = 1d / 30` are separate recipe parameters.
Slot i generates its native effect and commits its damage at trigger + delay + i * interval (zero-based i), so four
slots run at 0, 1/30, 2/30 and 3/30 seconds. The first blast is immediate and each later interval is exactly 4000
simulation quanta, without rounding to integer milliseconds. These are user-tuned feel values for development,
not frame measurements or established official timings. After terrain rejection, i indexes only surviving centers:
the first surviving blast is immediate and later blasts follow at consecutive intervals without timing gaps.
Pending blasts survive parent continuation/removal, retain the trigger phase and share the simulation pause/teardown.
The native secondary burst uses `kinetic_blast_small` (`Metadata/Effects/Spells/cluster_burst/mini.ao` by default)
at each actual center for 0.35 seconds, resolved through the equipped skill-art variation.
The separate `kinetic_blast_large` main-impact art is not a substitute for these secondary bursts.
Area art scales by actual blast radius in world units divided by the selected art's exported native radius.
For example, normal and Celestial small bursts export 125 world units; art with no known radius stays unscaled.
Exact official random distribution, radial spiral and sequential timing remain uncalibrated. Fireball art uses
the selected native variation for 1.5 seconds on actual area events, including the final return event. These lifetimes
are separate from the KB damage delays. Native height, art alignment and full visual equivalence remain pending
user-operated client validation.
Flight uses fixed `(captured terrain height ?? 0) + Game_PROJECTILE_HEIGHT`, with a default offset of five steps.
Missing captures therefore use a provisional absolute height, not client terrain plus an offset.
Effects follow client terrain independently through `EffectObject`; platform/doodad height
is still unresolved. A wall-contact regression verifies Fireball area damage and default/Stygian native packets;
it does not certify that those effects are visible above every surface in the client.
Water is not treated as a wall merely because it is unwalkable: the projectile mask admits values 1 through 5,
blocks 0, and also stops at area boundaries. Maximum range can create a KB group over water without a wall.
Endpoint logs distinguish Terrain from Distance; area logs expose the selected art, radius and scale.

## Evidence and versions

Target configuration: existing read-only exports from client **3.29.3b** (`ActiveSkills`, `GrantedEffects`,
`GrantedEffectStatSets`, `GrantedEffectStatSetsPerLevel`, `Stats`). Normal KB/AltX/AltY active rows 539/540/541 map to
stat sets 4612/4613/4614: cluster counts 4/3/4, radii 14/19/14, secondary radii 20/28/20 and area final -40/absent/-40.
AltX carries quantity-to-clusters and cannot-Split. These client values corroborate configuration, not server algorithms.

- [3.29.0 official patch notes](https://www.pathofexile.com/forum/view-thread/3985332): Fireball radius/effectiveness,
  both KB Arcane Might values, normal KB's area penalty, Clustering's 10–15% mana addition at levels 1–20.
- [Mark_GGG, 2 August 2017](https://www.pathofexile.com/forum/view-thread/1930316/filter-account-type/staff):
  a KB impact's explosion group cannot hit its own triggering enemy; other impact groups can hit that enemy.
  This historical official rule is used for the KB family; no independent 3.29.3b measurement establishes equivalence.
- [Mark_GGG, 20 April 2023](https://www.pathofexile.com/forum/view-thread/3373661/page/2#p24964202) and
  [Crucible FAQ](https://www.pathofexile.com/forum/view-thread/3361940): Fireball waits for exhausted continuation;
  KB explodes on impact or stopping, including while continuing; returning projectiles can pass the caster.
  These are official historical 3.21 explanations, not new measurements of 3.29.3b.
- [Fireball wiki rev 1698987](https://www.poewiki.net/w/index.php?title=Fireball&oldid=1698987),
  [KB rev 1681855](https://www.poewiki.net/w/index.php?title=Kinetic_Blast&oldid=1681855) and
  [Clustering rev 1681856](https://www.poewiki.net/w/index.php?title=Kinetic_Blast_of_Clustering&oldid=1681856):
  terminal/direct distinction, independent area overlap, sectors, blocked-sector omission and quantity conversion.
  Clustering's spiral/sequence is a community description; its precise timing is not established.
- [Projectile rev 1642414](https://www.poewiki.net/w/index.php?title=Projectile&oldid=1642414),
  [Return rev 1660588](https://www.poewiki.net/w/index.php?title=Return&oldid=1660588) and
  [Returning Projectiles Support rev 1681728](https://www.poewiki.net/w/index.php?title=Returning_Projectiles_Support&oldid=1681728):
  simultaneous-volley admission, denied sibling pass-through, return exclusion and source-specific pierce.
- [Official 3.12.0 notes](https://www.pathofexile.com/forum/view-thread/2935777) and
  [2022 mechanics Q&A](https://www.pathofexile.com/forum/view-thread/3284453): unavailable Chain may work later after
  Return; split projectiles return to the firing entity and share admission if crossing the original target.
- [PoEDB KB](https://poedb.tw/us/Kinetic_Blast), [Clustering](https://poedb.tw/us/Kinetic_Blast_of_Clustering) and
  [PoB skill data at 16de4b82](https://github.com/PathOfBuildingCommunity/PathOfBuilding/blob/16de4b82d57f1c0de6eb40f37143c32d4da36a02/src/Data/Skills/act_int.lua)
  corroborate the public normal/Clustering configuration. The audited wiki snapshot is dated 2026-10-13 and is not
  certified exact-patch evidence. Conflicting old Fireball prose radius 9 is superseded by the version-specific data.

## Client checks

The user operates the client. Automated runtime and held-output input tests establish damage/ordering behavior;
they do not establish official gameplay or visual parity.

- Fireball: lone target and grouped enemies; extra-projectile direct admission; independent neighboring/wall blast
  overlap; pierce followed by fork/chain; terminal victim once with area supports; return deferral and actual final art.
- KB: four separate bursts, repeated pierced impacts, terrain-blocked sectors, empty range and return turnaround;
  each contact group's victim exclusion, including large targets and delayed slots, while other groups can hit it;
  large-target/neighbor overlap; separate area/placement growth and explosion-only damage penalty.
- Clustering: three base bursts, quantity-to-explosions with GMP/Fury Valve, no Split, retained Fork and support penalty.
- Burning Arrow: the ignite rate near 25%, crits always igniting, the flames on the monster for the buff's time, burn
  damage through fire resistance and burning kills; Vigour's added fire with Life; greyed out without an active bow.
- Ordinary bow/catalog flight: close/far aim spread, finite pierce handoff, return terrain collision, pause/transfer and
  retirement. Report height/surface issues separately from contact/damage discrepancies.
