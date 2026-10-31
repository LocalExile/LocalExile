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
Physical more damage is applied before conversion; elemental and chaos modifiers apply to their resulting damage.
Conditional modifiers are not treated as unconditional bonuses.
Skill panel values and ordinary hit execution use the same supported-skill calculation.
The Delve socketed attack/spell more-damage effects multiply the matching hit once through this calculation.
Channelled attacks pay the supported cost on subsequent channel hits.

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

## Remaining execution gaps

A native compatibility match and a displayed support letter do not prove that its special mechanic is implemented.
Triggers, totems, mines, minion behaviour, repeats, projectile branching, leech and many conditional effects
still require their own execution paths.
Persistent damage effects, including Righteous Fire, do not yet refresh every derived parameter on link changes.
Only the unconditional damage keys explicitly listed by GemSupportDamage receive multiplicative processing.
The native catalogue includes historical and placeholder variants; their presence does not make them obtainable.
Do not describe the entire support catalogue as mechanically complete.
