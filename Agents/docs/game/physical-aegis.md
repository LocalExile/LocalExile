# Physical Aegis

The Ghastly Theatre's `TriggeredPhysicalAegisSkillUnique__1` grants GEPL `52147` (GrantedEffects
`PhysicalAegis`, row `8958`, level 20). Its stat set `8943` supplies
`aegis_unique_shield_max_value=1000`, `active_skill_display_aegis_variation=4`, and
`aegis_recharge_delay_ms=10000`. The server resolves this exact non-manual grant through
`AllWornGrantedSkillsOf`; it does not create an Actor skill.

BuffDefinitions `960` is the active Aegis (`has_aegis_shield_type`, `IsRecovery`), `965` is depleted
(`has_depleted_physical_aegis`), and `970` is the invisible persistence row. The owner publishes all three through
the existing buff path. The active count carries remaining capacity; the depleted row carries no recovery value.

## Supported damage order

LocalExile's current accepted typed-hit order is:

1. avoidance, block, damage conversion/taken-as, resistance and armour mitigation;
2. totem damage redirection;
3. Physical Aegis from the remaining physical component;
4. Ward;
5. Energy Shield;
6. defensive Mana;
7. Life, including Petrified Blood.

The [PoE receiving-damage reference](https://www.poewiki.net/wiki/Receiving_damage) places Aegis before Ward and Ward
before ES/Life. This chosen pipeline, rather than the buff description's shorter "before Life or Energy Shield"
sentence alone, is the supported server policy.

Only accepted physical hit damage after mitigation consumes capacity. Nonphysical hits, damage over time, zero
damage, misses and blocked hits do not. Applying damage restarts the 10-second delay; the extracted active-buff
description says the Aegis restores after taking no Physical Damage from Hits, so partial damage also postpones
restoration. At the exact deadline the owner restores full capacity. A stat or notification query does not advance
that deadline.

## Conditional effects

The three Ghastly modifiers are read from the equipped host, not inferred from the grant:

- depleted `attack_and_cast_speed_+%_while_you_have_depleted_physical_aegis` feeds the existing attack and cast
  speed calculations;
- depleted `critical_strike_chance_+%_while_you_have_depleted_physical_aegis` feeds the final critical chance;
- `nearby_enemies_are_blinded_while_you_have_active_physical_aegis` adds a separate Blind source in a fixed
  50-cell item-nearby radius. The public wiki estimate is 5 m and the project's conversion is 10 cells per metre.
  This is not an aura skill, so aura-area modifiers do not change it.

Every final Blind reader revalidates the active source, exact modifier, life, immune/dead state and current distance.
Presentation membership is only a cleanup receipt; depletion, source removal and movement stop the Aegis contribution
immediately while timed and other Blind sources remain.

Source removal, death and module retirement retire capacity and buff instances. Re-equipping creates a new
source-owned full Aegis. Successful area handoff carries remaining capacity and recharge time; relog and a new mode
derive a fresh state from the current item-grant catalog.

The deterministic Gateway coverage exercises real extracted rows, ordinary typed-hit outcomes, all three conditions,
the exact recharge boundary and these lifecycle edges. Native buff/count, depletion/recovery and nearby-Blind
appearance remain to be checked in the client.
