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
The native reader is `0x142196ce0`, calling `0x1424a0dc0` and `0x1424a0990`.
The constructor at `0x141ee73d0` receives a support vector with stride 0x78.
GrantedEffects supplies the native skill-bar letter and attribute colour; no client assets are changed.
Item supports use those same references, including the white `d` of the Delve socketed attack/spell damage effects.
Effects with an empty native letter do not acquire an invented badge.
The real client accepted Cyclone with Increased Area of Effect and later constructed five support entries.

## Modelled effects

The server applies linked area modifiers, added attack/spell damage, selected unconditional more/less damage,
conversion/gain-as-extra, damage prohibitions, attack/cast/projectile speed, critical modifiers and mana costs.
Reservation uses support cost multipliers; changing links refreshes active aura reservation values.
AttackSpeedMultiplier is an increased percentage: Cyclone 200 means three times base attack speed.
Lifetap Support turns the supported skill's whole Mana cost into Life (`base_skill_cost_life_instead_of_mana`, with
its 300% cost multiplier), counts the Life paid against `support_lifetap_spent_life_threshold`, and grants the Lifetap
buff (BuffDefinitions 1846) for the support's 4000 ms base plus 100 ms per 1% quality. While it runs, supported hits
take `support_lifetap_damage_+%_final_while_buffed` and flasks recover 20% increased Life. A paid Vaal use follows the
same Life cost.
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
`support_slashing_buff_attack_speed_+%_final_to_grant` as more attack speed; the buff ends at its deadline rather than
when that travel skill is used. Block Chance Reduction's `support_reduce_enemy_block_and_spell_block_%` lowers the
monster's block roll and a blocked supported hit applies Overpowered
(`apply_overpowered_on_enemy_block_reduced_block_and_spell_block_%` for `support_overpowered_base_duration_ms`); monster
spell block does not exist, so the spell-block half has nothing to lower. Machinations counts the character's allocated
keystones (`KeystoneCount`) for its chaos gain; its skill-cost half has no consumer, as skill costs read player stats
only.

Awakened supports (their 3.29 rows are kept in the data) add mechanics of their own: every `supported_<type>_skill_gem_level_+`
raises the supported gem's level like the generic `supported_active_skill_gem_level_+`; Awakened Brutality's
`chance_to_crush_on_hit_%` crushes for 4 s and a crushed monster's physical damage reduction is 15% lower
(BuffDefinitions `crushed`); Awakened Spell Echo's `spell_echo_plus_chance_double_damage_%_final` rolls only on the final
repeat; Awakened Generosity's `non_curse_aura_effect_+%` scales the aura it supports, and its `aura_cannot_affect_self`
keeps that aura out of the player's stats (the buff icon still shows, and an enemy-debuff aura such as Pride still
applies). The rest of their extra stats wait on the subsystems below.

In the live Cyclone example, the level-one support adds 30% increased area to the character's 136%.
A base radius of 16 becomes 26 cells after area-to-radius conversion and integer rounding.Tests cover link isolation, duplicate families, multiple supports, compatibility expressions, costs,
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

## Remaining execution gaps

A native compatibility match and a displayed support letter do not prove that its special mechanic is implemented.
Spell Echo's repeats are modelled ([11](11-skills.md) "Spell repeats"), except Ice Nova's and the final-repeat stats.
Triggers, totems, mines, minion behaviour, attack repeats, projectile branching, leech and many conditional effects
still require their own execution paths.
Second Wind's extra cooldown use needs multi-use cooldown state (one stored use is all that is tracked); Momentum's
stacks need a skill-use and movement signal; Barrage and Arrow Nova need their own release patterns, not a stat.
Persistent damage effects, including Righteous Fire, do not yet refresh every derived parameter on link changes.
Only the unconditional damage keys listed by GemSupportDamage and the five target conditions above receive
multiplicative processing.
Lifetap's spent-Life counter and buff are server state: the hidden `lifetap_life_spent` buff is not sent, minions do
not gain the buff, and its more damage does not reach damage over time.
The native catalogue includes historical and placeholder variants; their presence does not make them obtainable.
Do not describe the entire support catalogue as mechanically complete.
