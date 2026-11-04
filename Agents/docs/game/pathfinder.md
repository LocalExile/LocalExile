# Ordinary Pathfinder

The 14 allocatable nodes of the Default-graph Pathfinder (issue #602) and their consumers.
Every node's stat is read by the ordinary aggregation; this page lists the class rules that are not a plain stat sum.
Numbers are the normal values; Ruthless replacement rows are listed where the client supplies them.

## Nodes

| Node | Stats (value) | Consumer |
| --- | --- | --- |
| Flask Effect and Duration ×2 | `flask_effect_+%` 5, `flask_duration_+%` 8 | `FlaskEffectScaling`, `FlaskRecovery`, `UtilityFlasks`, `PanelStats.FlaskDurationIncrease` |
| Flask Effect, Chaos Damage | `flask_effect_+%` 5, `chaos_damage_+%` 15 | Generic chaos damage rows (`AilmentPanelStats`, gem support damage) |
| Flask Effect, Poison Duration | `flask_effect_+%` 5, `base_poison_duration_+%` 10 | `MonsterPoisons.Settings.From` (added here) |
| Flask Effect and Charges Gained ×3 | `flask_effect_+%` 5, `charges_gained_+%` 10 | `FlaskCharges.GainStats` / `GainMultiplier` |
| Master Distiller | `pathfinder_skills_consume_x_charges_from_a_bismuth_diamond_or_amethyst_flask` 3, `…penetrate_elemental_resistances_%…bismuth…` 25 (Ruthless 20), `…critical_strike_chance_+%…diamond…` 250 (150), `…physical_damage_%_to_add_as_chaos…amethyst…` 37 (23) | `Pathfinder.PayDistiller` on non-channelling uses |
| Nature's Reprisal | `withered_effect_+%` 50 (Ruthless 35), `withered_on_hit_for_2_seconds_%_chance` 25 | `HitDebuffs.WitherOnHitLocked` |
| Master Toxicist | `pathfinder_poison_damage_+100%_final_chance_during_flask_effect` 20, `unique_spread_poison_to_nearby_enemies_during_flask_effect` 1 (Ruthless `spread_poison_to_nearby_enemies_on_kill` 1) | `MonsterPoisons` settings and `Pathfinder.SpreadPoisonOnKill` |
| Nature's Adrenaline | `flask_charges_recovered_per_3_seconds` 3 (Ruthless 1) | `FlaskCharges.PerThreeSeconds` |
| Master Surgeon | `life_flask_effects_are_not_removed_at_full_life` 1, `pathfinder_flask_life_to_recover_+%_final` -50 | `FlaskRecovery.For` (the final multiplier is added here) |
| Nature's Boon | `magic_utility_flask_effect_+%` 30 (Ruthless 20) | `UtilityFlasks.For` for a magic flask |
| Master Alchemist | `flasks_%_chance_to_not_consume_charges` 50, `dispel_status_ailments_on_flask_use` 1 (Ruthless replaces the latter with `hard_mode_utility_flask_gain_charges_while_active`) | `SpendFlask` roll, `Pathfinder.DispelAilmentsOnFlaskUse`; Ruthless charge gain is the league's rule |

## During any Flask Effect

"During any Flask Effect" means a running life, mana or utility flask effect:
`conn.FlaskEffects.Any || conn.UtilityFlaskEffects.Any`. An instant-only recovery starts no effect, and charges left in a
flask whose effect ended do not count.

## Master Distiller

A non-channelling skill use that can hit consumes the node's charge count (3) from one flask of each of the three
bases in the flask bar, when that flask holds enough charges, in one S→C 0xbf. The use then gets, per paid type:

- Bismuth: `base_penetrate_elemental_resistances_%` 25 on its hits (Ruthless 20).
- Diamond: 250 increased critical strike chance (Ruthless 150), added to the use's chance.
- Amethyst: 37% of physical damage gained as extra chaos (Ruthless 23) in the use's conversion.

The payment is one immutable per-use record: the default attack (`Attack`), ordinary bow/wand ground attacks
(`Messages.cs`), and gem attacks/spells (`CastGemSkill` → projectiles or `AreaSkillHit`) each capture it before their
hit. Channeling, totems, warcries, auras, reservations, Righteous Fire, links, Shield Charge and location-instant skills
pay nothing.

## Master Toxicist

- The node's 20% chance for 100% more poison damage is rolled once per poison application, only while any flask effect
  runs (`MonsterPoisons.Settings.MoreChance`). Ruthless replaces the stat with the spread row and has no damage chance.
- A player kill of a monster that is still Poisoned (or died of poison) during any flask effect copies the victim's
  latest poison damage and duration to hostile monsters within 1.5 m (15 units); Ruthless does the same on any kill.
  No new chance roll is made.

## Not applied

- Distiller's extra chaos damage does not enter the panel poison rows: a poison from a Distiller-paid hit uses the
  ordinary panel poison damage (the server's existing poison model).
- Channeling, instant/location-instant skills, Shield Charge, trap/mine and totem skills do not pay Distiller charges.
- Life/mana flask recovery that is entirely instant starts no "flask effect" for the during-effect conditions.
- Nature's Boon applies to magic utility flasks only, as the stat says.
- A non-player kill (a minion's) does not spread Master Toxicist's poison.

## Tests

`tests/GameServer.Gateway.Tests/PathfinderNodesTests.cs` covers the poison duration/Toxicist chance, Master Surgeon's
final recovery multiplier, the Distiller plan and bonus mapping, and the free-use roll. The class has not had a native
client acceptance run in this change.
