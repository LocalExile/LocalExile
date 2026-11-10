# Hierophant

The ordinary Hierophant (Templar, `Ascendancy` row 13, start node 30940) has 16 nodes: `PassiveSkills` rows 1578 to
1593 (Ids `AscendancyHeirophant*`, the client's spelling). The `AscendancyCustomHeirophant*` rows (5415 to 5432) are a
Phrecia class's and are not covered here. Every stat is applied through its sum, so another source works the same way.

## Nodes

| Node | Stats (value) | Consumer |
| --- | --- | --- |
| Heirophant1, 13 | `mana_regeneration_rate_+%` 20, `arcane_surge_effect_+%` 20 | `PanelStats.ManaPerMinute`, `WithSupportGemBuffs` |
| Heirophant2, 3 | `mana_regeneration_rate_+%` 20, `summon_totem_cast_speed_+%` 20 | `ManaPerMinute`, `PlaceTotem` |
| Heirophant4, 5, 6 | `mana_regeneration_rate_+%` 20, `maximum_mana_+%` 8 | `ManaPerMinute`, pools |
| Heirophant15 | `mana_regeneration_rate_+%` 20, `sigil_damage_+%` 14 | regeneration only: no Brand executes (below) |
| Divine Guidance (922) | `maximum_mana_+%` 30, `base_damage_removed_from_mana_before_life_%` 10 | pools, `PlayerDamageTaken` |
| Divine Guidance | `additive_mana_modifiers_apply_to_damage_at_30%_value` | `Hierophant.PlayerStats` (`damage_+%`) |
| Sanctuary of Thought (29026) | `mana_%_to_add_as_energy_shield` 20, `mana_regeneration_rate_per_minute_%` 60 | `IncomingResourceDefences.Maximum`, `ManaPerMinute` |
| Sanctuary of Thought | `hierophant_area_of_effect_+%_per_50_unreserved_mana_up_to_100%` 1 | `Hierophant.Tick`, `SkillArea` |
| Pursuit of Faith (1105) | `number_of_additional_totems_allowed` 1, `totem_duration_+%` 100, `summon_totem_cast_speed_+%` 100 | `TotemLimit`, `CreateTotem`, `PlaceTotem` |
| Ritual of Awakening (34434) | `you_and_totem_life_regeneration_rate_per_minute_%_per_active_totem` 60, `mana_regeneration_rate_per_minute_%_per_active_totem` 30 | `Hierophant.Tick`, `PlayerStats` |
| Ritual of Awakening | `hierophant_passive_damage_+%_final_per_totem` 3 | `PlayerHitDamage.Resolve` |
| Conviction of Power (25651) | `maximum_power_and_endurance_charges_+` 1, `base_minimum_endurance_charges` 4, `base_minimum_power_charges` 4 | `ChargePanelStats`, `Charges.LimitsOf` |
| Illuminated Devotion (60462) | `area_of_effect_+%_while_you_have_arcane_surge` 30, `life_leech_from_spell_damage_permyriad_while_you_have_arcane_surge` 50 | `WithSupportGemBuffs` |
| Illuminated Devotion | `non_damaging_ailment_effect_+%_on_self_while_you_have_arcane_surge` -50 | `WithSupportGemBuffs`, `PlayerElementalAilments` |
| Arcane Blessing (40510) | `gain_arcane_surge_on_spell_hit_by_you_or_your_totems` | `ArcaneSurgeOnSpellHitLocked` |
| Arcane Blessing | `arcane_surge_on_you_spell_damage_+%_final_from_hierophant` 20 | `WithSupportGemBuffs`, `PlayerHitDamage.SpellMore` |
| Sign of Purpose (51492) | `sigil_recall_cooldown_speed_+%` 100, `brand_activation_rate_+%_final_during_last_20%_of_active_duration` 100 | not applied (below) |

## Arcane Surge

- One buff (BuffDefinitions 895, `arcane_surge`) for every source. Without the support it is "20% increased Cast Speed
  and 30% increased Mana Regeneration rate, for 4 seconds" (`ReminderTextArcaneSurge`); SupportArcaneSurge's constant
  stats are the same 4000 / 20 / 30.
- The buff's stats are `arcane_surge_cast_speed_+%`, `mana_regeneration_rate_+%` and
  `spell_damage_+%_final_from_arcane_surge`; `arcane_surge_effect_+%` raises the three (truncated), so Arcane Blessing's
  20% more is 28% with both 20% nodes. The support's own more Spell Damage is a gem stat, not raised.
- `cast_speed_+%` did not sum `arcane_surge_cast_speed_+%`, so the support's buff had no cast speed on the server; it
  now joins the sum. A totem casts with its owner's stats, minus the buff's cast speed and more Spell Damage.
- Arcane Blessing gains it on a spell hit that lands on a hostile monster, in the area and projectile executors (a
  spell totem's shots are projectiles). A refresh moving the deadline by less than 0.25 s keeps the client's buff.

## Model

- Divine Guidance: `damage_+%` gets 30% of the summed `maximum_mana_+%`, truncated toward zero.
- Sanctuary of Thought: unreserved mana is the pool's maximum minus the reservation (`UnreservedMana`), read by the
  250 ms tick, as the Summoned Totems count is; a change rebuilds the pools and sends the stats.
- Ritual of Awakening: Summoned Totems are the living ones (`TotemState.Living`). The count is sent to every player as
  `number_of_active_totems` (Stats row 3460). The 3% more applies in the skill's hit damage (`PlayerHitDamage`), so the
  skill stats and tooltip carry it and a totem's hits read it from its owner's stats; it does not reach ignite.
  Totems regenerate in the tick.
- Chill and shock on the player add `non_damaging_ailment_effect_+%_on_self` to their own effect-on-self stat.

## Not applied

- Brands: no Brand skill executes (no attachment, activation or recall), so `sigil_damage_+%`, Sign of Purpose's recall
  cooldown and activation frequency have nothing to act on.
- The non-damaging reduction acts on chill and shock only: the player's freeze has no magnitude here, and scorch,
  brittle and sap are not inflicted on the player.
- Arcane Blessing on hit executors outside the area and projectile paths (the default attack is not a spell).

## Not seen in the client

Live 2026-11-10 confirmed Arcane Blessing from the player's and a spell totem's Fireball (4 s buff, tooltip DPS 561 to
870 with both effect nodes), Illuminated Devotion's +30% area, Sanctuary of Thought's area and Energy Shield, Divine
Guidance's mana and damage (no double count in the tooltip), Pursuit of Faith (2 totems, 8 s to 16 s, faster
placement), Ritual of Awakening's regeneration and more damage per totem (tooltip 645, 664, 683 DPS with 0, 1, 2
totems), Illuminated Devotion's spell leech (0.5% of each spell hit: 23987 leeching 119.9 Life),
Conviction of Power, and Illuminated Devotion's halved chill (12-14% on a cold monster's hits, 6-7% during Arcane
Surge). Not observed, so only tested: the halved shock.
