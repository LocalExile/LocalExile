# Linked support gems

## Resolution

`GemSupports` reads native GrantedEffects, ActiveSkills and GrantedEffectsPerLevel tables.
Socketed supports affect usable gems in the same equipped item and link group.
Passive-granted host-0 skills retain their separate ownership and receive no invented socket links.
Compatibility uses active skill types, postfix AND/OR/NOT expressions, exclusions and weapon restrictions.
Added skill types are resolved to a fixed point, so socket order does not decide compatibility.
Duplicate families choose their upgraded variant, then highest level and quality, instead of stacking.
Different compatible supports combine; increased modifiers add and supported more/less modifiers multiply.
Gem level and quality are read from the gem with applicable global/family modifiers.
An ordinary matching socket colour grants no invented quality bonus; such bonuses require an actual stat source.
Empower and Enhance change the supported active gem's effective level and quality.
Family/global passive level bonuses compose once with those changes; intrinsic gem requirements still use
the gem's unboosted level, rather than its effective skill level.

## Native skill records

Each support contains a full granted-effect reference followed by a u32 source field.
The reference contains a flagged GEPL row, a flagged GESSPL row and any extra stat-set references.
A normal support therefore occupies 14 bytes, including the source field, which is currently zero.
Omitting the stat-set reference desynchronizes the Actor record and crashes the client.
The native reader is `0x142196ce0`, calling `0x1424a0dc0` and `0x1424a0990`.
The constructor at `0x141ee73d0` receives a support vector with stride 0x78.
GrantedEffects supplies the native skill-bar letter and attribute colour; no client assets are changed.
The real client accepted Cyclone with Increased Area of Effect and later constructed five support entries.

## Modelled effects

The server applies linked area modifiers, added attack/spell damage, selected unconditional more/less damage,
conversion/gain-as-extra, damage prohibitions, attack/cast/projectile speed, critical modifiers and mana costs.
Reservation uses support cost multipliers; changing links refreshes active aura reservation values.
AttackSpeedMultiplier is an increased percentage: Cyclone 200 means three times base attack speed.
Physical more damage is applied before conversion; elemental and chaos modifiers apply to their resulting damage.
Conditional modifiers are not treated as unconditional bonuses.
Skill panel values and ordinary hit execution use the same canonical typed damage and critical consumers.
Weapon base and added damage retain separate signed GESSPL normalization (`10000 + raw`).
Critical increases retain signed long sums and canonical half-up permyriad rounding; prohibitions remain gates.
Raw skill and quality mana inputs join additively before support cost multipliers.
Channelled attacks pay the supported cost on subsequent channel hits.

In the live Cyclone example, the level-one support adds 30% increased area to the character's 136%.
A base radius of 16 becomes 26 cells after area-to-radius conversion and integer rounding.
Tests cover link isolation, duplicate families, multiple supports, compatibility expressions, costs,
area quality, attack speed, damage prohibitions, conversion ordering and complete support record framing.

## Remaining execution gaps

A native compatibility match and a displayed support letter do not prove that its special mechanic is implemented.
Triggers, totems, mines, minion behaviour, repeats, projectile branching, leech and many conditional effects
still require their own execution paths.
Righteous Fire activation and refresh now read supports-aware raw stats.
Other persistent effects still need their individual execution and refresh contracts.
Only the unconditional damage keys explicitly listed by GemSupportDamage receive multiplicative processing.
The native catalogue includes historical and placeholder variants; their presence does not make them obtainable.
Do not describe the entire support catalogue as mechanically complete.
