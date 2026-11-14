# Linked support gems

## Resolution

`GemSupports` reads native GrantedEffects, ActiveSkills and GrantedEffectsPerLevel tables.
Physical support gems only affect usable gems in the same equipped item and link group.
Item mods also contribute the support effects referenced by their `GrantedEffectsPerLevelKeys` to gems in that item.
These item supports do not require links and pass through the same compatibility and duplicate-family resolver.
An unambiguous displayed support-level stat uses the item's rolled value, including developer-set levels.
Non-level pseudo-supports retain their native fixed granted-effect definitions.
Compatibility uses active skill types, postfix AND/OR/NOT expressions, exclusions and weapon restrictions.
Added skill types are resolved to a fixed point, so socket order does not decide compatibility.
Duplicate families choose their upgraded variant, then highest level and quality, instead of stacking.
Different compatible supports combine; increased modifiers add and supported more/less modifiers multiply.
Gem level and quality are read from the gem; matching socket colours grant their native quality bonus.
Empower and Enhance change the supported active gem's effective level and quality.
Local socketed-gem level and quality modifiers apply to physical active/support gems in their source item.
GemTags supplies family membership; support-only bonuses can raise Empower but do not raise virtual item supports.
Stored gem experience and intrinsic requirements are unchanged.

## Native skill records

Each support contains a full granted-effect reference followed by a u32 source field.
The reference contains a flagged GEPL row, a flagged GESSPL row and any extra stat-set references.
A normal support therefore occupies 14 bytes, including the source field, which is currently zero.
Omitting the stat-set reference desynchronizes the Actor record and crashes the client.
The native reader is `0x14219d650`, calling `0x1424a7890` and `0x1424a7460`.
The constructor at `0x141eedc30` receives a support vector with stride 0x78.
GrantedEffects supplies the native skill-bar letter and attribute colour; no client assets are changed.
Item supports use those same references, including the white `d` of the Delve socketed attack/spell damage effects.
Effects with an empty native letter do not acquire an invented badge.
The real client accepted Cyclone with Increased Area of Effect and later constructed five support entries.

## Modelled effects

The server applies linked area modifiers, added attack/spell damage, selected unconditional more/less damage,
conversion/gain-as-extra, damage prohibitions, attack/cast/projectile speed, critical modifiers and mana costs.
Reservation uses support cost multipliers; changing links refreshes active aura reservation values.
Arrogance Support adds its stat set's `base_skill_reserve_life_instead_of_mana` and `aura_effect_+%` (19 at level 20)
to the aura it supports: `ReservationContext` moves the reservation from mana to life, and `AuraSupport` supplies the
increased effect (the aura still affects its caster, unlike Generosity's `aura_cannot_affect_self`). Arrogance's own
reservation multiplier (201% at level 20) multiplies the aura's percentage, so level-20 Grace reserves 100.50% of
maximum life instead of 50% of maximum mana. That product is computed as an exact decimal percentage, not a binary
`double`, so 201% of 50% keeps its last permyriad (`GemSupports.SupportedReservation`).
AttackSpeedMultiplier is an increased percentage: Cyclone 200 means three times base attack speed.
Lifetap Support turns the supported skill's whole Mana cost into Life (`base_skill_cost_life_instead_of_mana`, with
its 300% cost multiplier), counts the Life paid against `support_lifetap_spent_life_threshold`, and grants the Lifetap
buff (BuffDefinitions 1846) for the support's 4000 ms base plus 100 ms per 1% quality. While it runs, supported hits
take `support_lifetap_damage_+%_final_while_buffed` and flasks recover 20% increased Life. A paid Vaal use follows the
same Life cost.
The Blood Magic keystone (the passive tree node or an item's `keystone_blood_magic`, for example Malachai's Awakening)
converts every skill's whole Mana cost to Life the same way, without Lifetap's multiplier, counter or buff; a skill is
refused when its Life cost is not below current Life. Mana reservation is unchanged.
Physical more damage is applied before conversion; elemental and chaos modifiers apply to their resulting damage.
Conditional modifiers are not treated as unconditional bonuses.
Skill panel values and ordinary hit execution use the same supported-skill calculation.
The Delve socketed attack/spell more-damage effects multiply the matching hit once through this calculation.
Awakened Increased Area of Effect's `inc_aoe_plus_more_area_damage_+%_final` is one of those unconditional keys.
Five supports' more damage depends on the hit's target or the player, so `WithConditionalSupportDamageLocked`
scales each target's roll after the outgoing-damage hooks, on the area and projectile hit paths:
Hypothermia against a chilled enemy, Behead against an enemy at half its maximum Life or less (PoB
`LowPoolThreshold`), Damage on Full Life for an attack while the player's Life is full, Lifetap while the player
has its buff, and Vile Toxins for every poison on the enemy (at most the support's eight).
The ailment share of "Hits and Ailments" follows the hit for chill, freeze and shock; ignite still takes its damage
from the skill's ailment rows, without these multipliers.
Channelled attacks pay the supported cost on subsequent channel hits.

Culling Strike is the server's alone: the client's hit logic never reads the culling stats.
After a player's melee, area or projectile hit deals its damage, a surviving enemy at or below the threshold share of
its maximum Life dies (`TakeOwnerHitLocked`, `CullingStrike.cs`), and its death goes out like any other.
`kill_enemy_on_hit_if_under_20%_life`, `_15%_` and `_10%_` (the support's implicit stat) set 20, 15 and 10;
`crits_have_culling_strike` on a critical hit and `spells_have_culling_strike` on a spell hit set 10.
A boss stage that the hit starts, or one still running, keeps the boss alive, and `immune_to_culling_strike` (the
Executioner archnemesis mod) stops the cull.
The support's quality stat `recover_%_maximum_life_on_cull` (0.1 per 1% quality) recovers that share of maximum Life
on a cull, applied at once and sent with the next vitals update like instant leech.
Hits from minions, warcries, passive-granted skills and Rampage do not cull yet, nor do the conditional sources
(against bleeding, burning, frozen, cursed or marked enemies, bow attacks, weapon-local critical strikes, flasks), the
flat Life and Mana gained on cull, or the "Culled Recently" stats.

Green supports with a condition or an execution of their own now run: Cull the Weak culls normal and magic enemies at
`kill_normal_or_magic_enemy_on_hit_if_under_x%_life` (Culling Strike's share still applies to every rarity), Pacifism's
`hits_cannot_kill_enemies` leaves the enemy at 1 Life and cancels the cull, Lethal Dose applies
`additional_poisons_+_to_apply_vs_non_poisoned_enemies` extra poisons with one damage roll per hit, Hypothermia's
`additional_chance_to_freeze_chilled_enemies_%` counts an enemy chilled by an earlier hit (not the hit's own chill),
Sadism's `support_faster_ailments_ailment_duration_+%_final` shortens its ignites, bleeds and poisons, Vicious Projectiles'
`support_projectile_attack_speed_+%_final` is a factor of the supported attack's rate, and Greater Chain's
`support_chain_count_+%_final` multiplies the whole chain count, rounded up (`GatewayProjectileModifiers`).

Greater Fork's `chance_to_fork_extra_projectile_%` rolls per projectile at the fork and adds a third child straight ahead
(`ProjectileProfile.ForkExtraProjectileChancePercent`). Rupture's critical bleeding hits add up to four three-second
Ruptures on the monster, each raising its bleeding damage taken by `support_rupture_bleeding_damage_taken_+%_final`
(`MonsterRuptures`, `BleedTaken`); "bleeding expires more quickly" and the native Rupture buff are not modelled, because a
bleed's duration is captured when it is applied. Close Combat's supported melee hit grants Combat Rush for its listed
duration, and a travel skill the support does not support then takes
`support_slashing_buff_attack_speed_+%_final_to_grant` as more attack speed.
A successfully paid travel use consumes the buff.
Its speed is captured for that action, so reading release timing
after payment keeps the bonus. A refused use preserves it, and a travel skill supported by Close Combat consumes it
without gaining the speed bonus. Block Chance Reduction's `support_reduce_enemy_block_and_spell_block_%` lowers the
monster's block roll and a blocked supported hit applies Overpowered
(`apply_overpowered_on_enemy_block_reduced_block_and_spell_block_%` for `support_overpowered_base_duration_ms`); monster
spell block does not exist, so the spell-block half has nothing to lower. Machinations counts the character's allocated
keystones (`KeystoneCount`) for its chaos gain.
Its increased cost joins the supported skill's `base_cost_+%`, additive with ordinary increased/reduced Mana cost
before the final floor.
The same cost is published and paid, including a cost converted to Life.

Awakened supports (their 3.29 rows are kept in the data) add mechanics of their own: every `supported_<type>_skill_gem_level_+`
raises the supported gem's level like the generic `supported_active_skill_gem_level_+`; Awakened Brutality's
`chance_to_crush_on_hit_%` crushes for 4 s and a crushed monster's physical damage reduction is 15% lower
(BuffDefinitions `crushed`); Awakened Spell Echo's `spell_echo_plus_chance_double_damage_%_final` rolls only on the final
repeat; Awakened Generosity's `non_curse_aura_effect_+%` scales the aura it supports, and its `aura_cannot_affect_self`
keeps that aura out of the player's stats (the buff icon still shows, and an enemy-debuff aura such as Pride still
applies).
Cruelty Support grants its buff on a hit of a supported skill: `CrueltyPctFor` computes `round(100 * (1 + cruelty_effect_+%/100) * (D/L)^0.2)`
from the hit's damage `D` (capped at the life the target could lose) and the monster's maximum life `L`, capped at 40%.
The buff is per supported skill and keeps the strongest running value while refreshing its 4 s duration (plus
`cruelty_duration_+%`); that value is injected as `cruelty_damage_over_time_+%_final` into the skill's own stats while
its buff runs, so its Damage over Time debuffs take it. Presentation is a single buff (BuffDefinitions 1815) showing
the strongest running value, with `u82 = 0` and one value (the percent), re-sent on a value change or a deadline move of
at least 0.25 s (the Arcane Surge pattern).

Multistrike's repeats are hits of the same use: `MeleeAttackRepeats` splits the skill's attack time between them, each
repeat after the first takes its `multistrike_damage_+%_final_on_{first,second,third}_repeat`, and the support's attack
speed reads its actual `support_multiple_attacks_melee_attack_speed_+%_final` (the old code read a stat that does not
exist, so the base support was a pure damage penalty). Melee Splash hits the enemies around each struck one for the
support's less damage; its radius is the 12-cell default grown by the area modifier and
`melee_splash_area_of_effect_+%_final` (`MeleeSplash`).

Spell Cascade repeats a supported area spell in a line: one extra area per side along the caster-to-target
direction (`support_spell_cascade_number_of_cascades_per_side`, 1) and, with `support_spell_cascade_sideways`
(Greater and Awakened), one per side across it. Each centre is one effective radius from the aimed point, the radius
the hit itself uses, so area of effect scales the spacing; the support's area and damage finals stay ordinary skill
stats. Captured from the 3.29.3b client on 2026-11-04 (`scripts/re/frida/sniff-support-geometry.js`): Reap at 28.5
effective radius placed its five Awakened areas 28-29 cells apart (three along the aim, two across it), the aimed cell
in the middle; the client draws each effect half a radius toward the caster, so the hit centres are the aim +- radius.
`LaunchProjectile` schedules one `AreaSkillHit` per area with the use's critical roll, so overlapping areas each hit.
Not built: the client's per-area delay (`support_spell_cascade_area_delay_+%`) and whether the damage penalty applies
to the centre area as well as the repeats (the stat is applied to every area, as PoB does).

Arrow Nova turns a supported bow attack into one payload arrow that lands at the aimed cell and fires the skill's
whole volley in a circle from there (`projectiles_rain` and `skill_is_rain_skill`). Captured from the 3.29.3b client
on 2026-11-04 (Split Arrow, 18 arrows): every child started on the aim cell and the directions were exactly 20 degrees
apart, the first on the aim ray about 0.66 s after the use at 33 cells; the client drew the volley's children, so
`ArrowNovaChildren` is `HideVisual` and the server only simulates their damage. The official server draws the payload
with the support's `rain_rig.ao` `PermanentServerEffect` at the caster's cell and again at the landing cell, attaching
`split_arrow.ao` to the rig's `aux_projectile_attach` bone (the arrow rides the rig's own transform, so the rig must
stand at the player, not at the bow's release point). The
rig's `Animated` bit3 id is 6 at the caster and 0 at the landing, and the attachment record sends kind 0 and mask 3
(`EffectObject.AnimationId`, `AttachKind`, `AttachFlags`); sending 0 on both ends played the landing backwards. The
landing comes 415 ms after the use and the nova 221 ms later; `ArrowNovaChildren` uses `EndEmission.Delay` and takes
the shared modifier pass's extra arrows. Not built: the per-child launch order.

Red and blue supports whose stats had no consumer now apply too: Maim's `maim_on_hit_%` maims for 4 s and a maimed
monster takes its `support_maimed_enemies_physical_damage_taken_+%` extra physical damage and 30% reduced movement
(PoB's Maimed condition); Bloodlust's more melee physical damage applies against a bleeding enemy; Pulverise's
`support_pulverise_attack_speed_+%_final` and Rage's `attack_speed_+%_with_at_least_10_rage` join the attack-speed
factors; Power Charge on Critical adds its more damage per power charge; Volatility scales the attack's minimum and
maximum damage (`Volatility.cs`); Combustion's `ignites_apply_fire_resistance_+` rides on the ignite as a fire Exposure
for its duration; Item Quantity's `base_killed_monster_dropped_item_quantity_+%` multiplies the loot of the last hit's
kill (`ItemQuantityFactor`) and Item Rarity's `base_killed_monster_dropped_item_rarity_+%` raises that kill's rarity
chances (`IncreasedItemRarity`); both also fill the selected skill's panel rows
(`display_skill_increased_item_quantity_+%`/`..._rarity_+%`); a damage-over-time kill keeps no skill attribution; Iron Grip's `keystone_strong_bowman`
adds Strength's damage bonus to projectile attack physical damage and Iron Will's `strong_casting` to spell damage
(`DamageStats.StrengthDamageBonus`). Immolate's added fire against burning enemies, Bloodthirst's added physical from
maximum life on low life, Bonechill's cold damage taken equal to the chill, and Coursing Current's equivalent
shock/chill apply in the per-target support hook. Fist of War makes every 1.8 s slam a big hit (damage and area);
Devour recovers its life/mana on a killing blow and consumes the corpse (the object is removed); Sacrifice pays current life once at
the original player cast's first valid release (including Fireball/Spark), shared by repeats and cascades.
Its added chaos enters raw spell damage before effectiveness, conversion, increases, more and damage prohibitions;
unreleased cancelled, replaced-source or retired casts pay nothing. Payment publishes current life/reservation;
area poison uses the same added chaos base. Source identity survives gem experience/level-lock edits, not replacement.
Proxy/trigger executors must supply their own actual caster and never reuse the player payment; Spellblade adds its share of the one-hand melee weapon as spell damage; Hallow's Hallowing Flame gives
the next attack 25% of its physical damage as extra fire (scaled by the magnitude stat, then removed); and Excommunicate
suppresses an excommunicated monster's chaos damage for its 3 seconds.

Arcane Surge Support tracks mana spent on each supported spell and grants its buff when that reaches the support's
threshold; the buff adds its cast speed, mana regeneration and more Spell Damage for its base duration. Inspiration
gains a charge on a supported mana spend, five at most, or loses all of them when a single use reaches the support's
threshold; each charge adds its more Elemental Damage and increased Critical Strike Chance, and its reduced Mana Cost
applies to the supported skill. Controlled Blaze counts enemies ignited in the last four seconds: each one adds the
support's more Ignite Damage up to its cap and its less Damage down to its floor, and its base ignite chance applies
to the hit. Cruelty's hit damage already reaches supported hits and their ailments; its buff's additional damage over
time is not modelled.

In the live Cyclone example, the level-one support adds 30% increased area to the character's 136%.
A base radius of 16 becomes 26 cells after area-to-radius conversion and integer rounding.
Tests cover link isolation, duplicate families, multiple supports, compatibility expressions, costs,
area quality, attack speed, damage prohibitions, conversion ordering and complete support record framing.
Item-support regressions cover native references, rolled level 40, compatibility, duplicates with physical gems,
link independence, removal, local level/quality and the 40% socketed-attack hit multiplier.
A real-client Cyclone control accepted the item-support records and displayed blue `s`/`c`, red `W` and white `d`.
Its character panel and tooltip included the supported critical values; the Gateway's own skill rows contained the
40% Delve attack multiplier.
This verifies the native display path, not every support's special execution or a live before/after quality control.

Ruthless makes every `support_ruthless_big_hit_max_count`-th use (3) of each supported skill a Ruthless Blow: that
use's melee hits, spirit strikes included, take `support_ruthless_big_hit_damage_+%_final` as more damage (70% at
level 1, 98% at 20; poedb). Each skill keeps its own count, counted as the use starts, so a use that misses still
counts (the wiki: the count advances whether or not the hit lands); that a use cancelled before its contact counts
too is not verified. The count is server state only;
the client's invisible `ruthless_big_hit_counter` buff is not sent. Not built: the blow's 0.8 s stun duration, its
more damage with bleeding, poison and ignite, and the count across Multistrike repeats.

PoEDB comparisons: [Second Wind](https://poedb.tw/us/Second_Wind_Support) (200% cost multiplier,
+1 non-instant use, 24% reduced recovery at level 1, 5% at 20 and up to 5% increased recovery from quality),
[Machinations](https://poedb.tw/us/Machinations_Support) (120% multiplier, 10% increased cost per Keystone),
and [Close Combat](https://poedb.tw/us/Close_Combat_Support) (two-second Combat Rush, consumed by travel,
20% more travel attack speed only without Close Combat). Values are read from the installed 3.29.3c tables;
PoEDB is a comparison source, not a replacement for those versioned tables.

## Remaining execution gaps

A native compatibility match and a displayed support letter do not prove that its special mechanic is implemented.
Spell Echo's repeats are modelled ([11](11-skills.md) "Spell repeats"), except Ice Nova's and the final-repeat stats.
Other trigger payloads, totems, mines, minion behaviour, attack repeats, projectile branching, leech and conditional effects
still require their own execution paths.
Second Wind uses the supported skill's cooldown/recovery inputs: non-instant skills gain its extra stored use,
instant skills retain their original count, and spent uses recover in sequence on game time. Active recovery-rate
changes preserve progress; removing the support changes capacity without refunding spent uses. Native Actor cooldown
updates replace the complete spent-use queue. Duplicate same-effect gems and the special cooldown exclusions in
`SingleUseCooldown` retain their existing limitations. Momentum's stacks need a skill-use and movement signal;
Barrage needs its own release pattern, not a stat.
Unleash's seals, minion damage on summoned minions and the curses that Blasphemy and Hextouch would apply still need
their own mechanics or client-side geometry. The blessing supports need a temporary aura lifecycle; Overheat needs
Fortification consumption and Overexertion needs warcry exertion.
Persistent damage effects, including Righteous Fire, do not yet refresh every derived parameter on link changes.
Only the unconditional damage keys listed by GemSupportDamage and the five target conditions above receive
multiplicative processing.
Lifetap's spent-Life counter and buff are server state: the hidden `lifetap_life_spent` buff is not sent, minions do
not gain the buff, and its more damage does not reach damage over time.
The native catalogue includes historical and placeholder variants; their presence does not make them obtainable.
Do not describe the entire support catalogue as mechanically complete.

## Damage-taken trigger execution

3.29.3c `SupportCastWhenDamageTaken` GEPL level 1 has 250% cost and 250 ms cooldown; its stat set has
`cast_on_damage_taken_threshold = 528`. Compatibility additionally compares active/support `PlayerLevelReq`.
Accepted hit damage counts after taken modifiers and before Ward, ES and Mana absorption; costs and DoT do not count.
Counters are per skill instance; copies of the same effect share trigger cooldown. Trigger release uses the original supported projectile payload and ignores player
windup/action replacement; it does not repeat Spell Echo or run another manual cast.
Adopted projectile spells, Ice Nova, native curses and QuickGuard/ImmortalCall/MoltenShell have independent releases.
Reservations, minions, placements and unimplemented non-projectile recipes stay unavailable.
Guard cooldown recovery begins when the buff ends; guard damage absorption precedes CWDT observation.
CoC alternates linked executable spells after an accepted attack critical hit, with native per-effect cooldown
and separate spell payment; attacks retain manual use. CwC uses its native interval on the projectile pump,
independently of the channel's hit/payment cadence, and drops future work when the paid channel becomes invalid.
Other generic triggers remain unavailable.
Queued triggers retain stable source gem identity and the support configuration. XP/lock edits retain identity;
actual gem swaps or changed levels/links cancel queued work. Reused skill IDs alone are insufficient identity. Pause freezes pending work without erasing the counter.
Triggered roots use server projectile objects because there is no local input action to predict them.
The existing spell volley and Spark wander/duration specialization still runs before server presentation is selected.
Native targeting, visual parity and threshold timing require client verification.
