# Cluster jewel passives: full list

Generated from the target client's `Mods.json` / `PassiveSkills.json` and Path of Building's `ModJewelCluster.lua` and `stat_descriptions.lua`. `works` means the server reads every stat and evaluates it unconditionally; `partial` means a condition is not modelled or some stats have no consumer; `inert` means the row exists but no stat is read; `not mapped` means the notable has no `PassiveSkills` row. Conditional stat names are flagged conservatively, so a `partial` row may still work when its condition happens to be irrelevant.

## Small-passive themes (42 distinct grants, 211 mods)

The theme is the stat every small passive of a cluster jewel grants. The client stores it as `local_affliction_jewel_small_nodes_grant_*`; the server maps the 20 entries in `ClusterJewelStats.ThemeStats`.

| Theme stat | What the small passives grant | Server |
|---|---|---|
| `local_affliction_jewel_small_nodes_grant_%_life_regeneration_per_minute` | Added Small Passive Skills also grant: Regenerate 0.1% of Life per Second<br>Added Small Passive Skills also grant: Regenerate 0.15% of Life per Second<br>Added Small Passive Skills also grant: Regenerate 0.2% of Life per Second<br>Added Small Passive Skills also grant: Regenerate 0.25% of Life per Second<br>Added Small Passive Skills also grant: Regenerate 0.3% of Life per Second | works |
| `local_affliction_jewel_small_nodes_grant_all_attributes` | Added Small Passive Skills also grant: +2 to All Attributes<br>Added Small Passive Skills also grant: +3 to All Attributes<br>Added Small Passive Skills also grant: +4 to All Attributes<br>Added Small Passive Skills also grant: +5 to All Attributes<br>Added Small Passive Skills also grant: +6 to All Attributes | works |
| `local_affliction_jewel_small_nodes_grant_armour` | Added Small Passive Skills also grant: +(11-20) to Armour<br>Added Small Passive Skills also grant: +(21-30) to Armour<br>Added Small Passive Skills also grant: +(31-40) to Armour<br>Added Small Passive Skills also grant: +(41-53) to Armour<br>Added Small Passive Skills also grant: +(54-66) to Armour | works |
| `local_affliction_jewel_small_nodes_grant_attack_and_cast_speed_+%_while_affected_by_a_herald` | Added Small Passive Skills also grant: 1% increased Attack and Cast Speed while affected by a Herald<br>Added Small Passive Skills also grant: 2% increased Attack and Cast Speed while affected by a Herald<br>Added Small Passive Skills also grant: 3% increased Attack and Cast Speed while affected by a Herald | not mapped |
| `local_affliction_jewel_small_nodes_grant_attack_and_cast_speed_+%_with_channelling_skills` | Added Small Passive Skills also grant: Channelling Skills have 1% increased Attack and Cast Speed<br>Added Small Passive Skills also grant: Channelling Skills have 2% increased Attack and Cast Speed<br>Added Small Passive Skills also grant: Channelling Skills have 3% increased Attack and Cast Speed | not mapped |
| `local_affliction_jewel_small_nodes_grant_attack_and_cast_speed_+%_with_chaos_skills` | Added Small Passive Skills also grant: 1% increased Attack and Cast Speed with Chaos Skills<br>Added Small Passive Skills also grant: 2% increased Attack and Cast Speed with Chaos Skills<br>Added Small Passive Skills also grant: 3% increased Attack and Cast Speed with Chaos Skills | not mapped |
| `local_affliction_jewel_small_nodes_grant_attack_and_cast_speed_+%_with_cold_skills` | Added Small Passive Skills also grant: 1% increased Attack and Cast Speed with Cold Skills<br>Added Small Passive Skills also grant: 2% increased Attack and Cast Speed with Cold Skills<br>Added Small Passive Skills also grant: 3% increased Attack and Cast Speed with Cold Skills | not mapped |
| `local_affliction_jewel_small_nodes_grant_attack_and_cast_speed_+%_with_elemental_skills` | Added Small Passive Skills also grant: 1% increased Attack and Cast Speed with Elemental Skills<br>Added Small Passive Skills also grant: 2% increased Attack and Cast Speed with Elemental Skills<br>Added Small Passive Skills also grant: 3% increased Attack and Cast Speed with Elemental Skills | not mapped |
| `local_affliction_jewel_small_nodes_grant_attack_and_cast_speed_+%_with_fire_skills` | Added Small Passive Skills also grant: 1% increased Attack and Cast Speed with Fire Skills<br>Added Small Passive Skills also grant: 2% increased Attack and Cast Speed with Fire Skills<br>Added Small Passive Skills also grant: 3% increased Attack and Cast Speed with Fire Skills | not mapped |
| `local_affliction_jewel_small_nodes_grant_attack_and_cast_speed_+%_with_lightning_skills` | Added Small Passive Skills also grant: 1% increased Attack and Cast Speed with Lightning Skills<br>Added Small Passive Skills also grant: 2% increased Attack and Cast Speed with Lightning Skills<br>Added Small Passive Skills also grant: 3% increased Attack and Cast Speed with Lightning Skills | not mapped |
| `local_affliction_jewel_small_nodes_grant_attack_and_cast_speed_+%_with_physical_skills` | Added Small Passive Skills also grant: 1% increased Attack and Cast Speed with Physical Skills<br>Added Small Passive Skills also grant: 2% increased Attack and Cast Speed with Physical Skills<br>Added Small Passive Skills also grant: 3% increased Attack and Cast Speed with Physical Skills | not mapped |
| `local_affliction_jewel_small_nodes_grant_attack_speed_+%` | Added Small Passive Skills also grant: 1% increased Attack Speed<br>Added Small Passive Skills also grant: 2% increased Attack Speed<br>Added Small Passive Skills also grant: 3% increased Attack Speed | works |
| `local_affliction_jewel_small_nodes_grant_base_aura_area_of_effect_+%` | Added Small Passive Skills also grant: 2% increased Area of Effect of Aura Skills<br>Added Small Passive Skills also grant: 4% increased Area of Effect of Aura Skills<br>Added Small Passive Skills also grant: 6% increased Area of Effect of Aura Skills | not mapped |
| `local_affliction_jewel_small_nodes_grant_base_cast_speed_+%` | Added Small Passive Skills also grant: 1% increased Cast Speed<br>Added Small Passive Skills also grant: 2% increased Cast Speed<br>Added Small Passive Skills also grant: 3% increased Cast Speed | mapped to `base_cast_speed_%`, no consumer |
| `local_affliction_jewel_small_nodes_grant_base_critical_strike_multiplier_+` | Added Small Passive Skills also grant: +1% to Critical Strike Multiplier<br>Added Small Passive Skills also grant: +2% to Critical Strike Multiplier<br>Added Small Passive Skills also grant: +3% to Critical Strike Multiplier | works |
| `local_affliction_jewel_small_nodes_grant_base_elemental_status_ailment_duration_+%` | Added Small Passive Skills also grant: 3% increased Duration of Elemental Ailments on Enemies<br>Added Small Passive Skills also grant: 4% increased Duration of Elemental Ailments on Enemies<br>Added Small Passive Skills also grant: 5% increased Duration of Elemental Ailments on Enemies | not mapped |
| `local_affliction_jewel_small_nodes_grant_base_projectile_speed_+%` | Added Small Passive Skills also grant: 2% increased Projectile Speed<br>Added Small Passive Skills also grant: 3% increased Projectile Speed<br>Added Small Passive Skills also grant: 4% increased Projectile Speed | not mapped |
| `local_affliction_jewel_small_nodes_grant_base_skill_area_of_effect_+%` | Added Small Passive Skills also grant: 1% increased Area of Effect<br>Added Small Passive Skills also grant: 2% increased Area of Effect<br>Added Small Passive Skills also grant: 3% increased Area of Effect | not mapped |
| `local_affliction_jewel_small_nodes_grant_chaos_resistance_%` | Added Small Passive Skills also grant: +(7-8)% to Chaos Resistance<br>Added Small Passive Skills also grant: +3% to Chaos Resistance<br>Added Small Passive Skills also grant: +4% to Chaos Resistance<br>Added Small Passive Skills also grant: +5% to Chaos Resistance<br>Added Small Passive Skills also grant: +6% to Chaos Resistance | works |
| `local_affliction_jewel_small_nodes_grant_charges_gained_+%` | Added Small Passive Skills also grant: 1% increased Flask Charges gained<br>Added Small Passive Skills also grant: 2% increased Flask Charges gained<br>Added Small Passive Skills also grant: 3% increased Flask Charges gained | not mapped |
| `local_affliction_jewel_small_nodes_grant_cold_resistance_%` | Added Small Passive Skills also grant: +(10-11)% to Cold Resistance<br>Added Small Passive Skills also grant: +(2-3)% to Cold Resistance<br>Added Small Passive Skills also grant: +(4-5)% to Cold Resistance<br>Added Small Passive Skills also grant: +(6-7)% to Cold Resistance<br>Added Small Passive Skills also grant: +(8-9)% to Cold Resistance | works |
| `local_affliction_jewel_small_nodes_grant_curse_area_of_effect_+%` | Added Small Passive Skills also grant: 1% increased Area of Effect of Hex Skills<br>Added Small Passive Skills also grant: 2% increased Area of Effect of Hex Skills<br>Added Small Passive Skills also grant: 3% increased Area of Effect of Hex Skills | not mapped |
| `local_affliction_jewel_small_nodes_grant_damage_+%` | Added Small Passive Skills also grant: 2% increased Damage<br>Added Small Passive Skills also grant: 3% increased Damage<br>Added Small Passive Skills also grant: 4% increased Damage<br>Added Small Passive Skills also grant: 5% increased Damage<br>Added Small Passive Skills also grant: 6% increased Damage | works |
| `local_affliction_jewel_small_nodes_grant_damage_over_time_+%` | Added Small Passive Skills also grant: 1% increased Damage over Time<br>Added Small Passive Skills also grant: 2% increased Damage over Time<br>Added Small Passive Skills also grant: 3% increased Damage over Time | not mapped |
| `local_affliction_jewel_small_nodes_grant_dex` | Added Small Passive Skills also grant: +(12-14) to Dexterity<br>Added Small Passive Skills also grant: +(2-3) to Dexterity<br>Added Small Passive Skills also grant: +(4-5) to Dexterity<br>Added Small Passive Skills also grant: +(6-8) to Dexterity<br>Added Small Passive Skills also grant: +(9-11) to Dexterity | works |
| `local_affliction_jewel_small_nodes_grant_elemental_resistance_%` | Added Small Passive Skills also grant: +2% to all Elemental Resistances<br>Added Small Passive Skills also grant: +3% to all Elemental Resistances<br>Added Small Passive Skills also grant: +4% to all Elemental Resistances<br>Added Small Passive Skills also grant: +5% to all Elemental Resistances<br>Added Small Passive Skills also grant: +6% to all Elemental Resistances | works |
| `local_affliction_jewel_small_nodes_grant_evasion` | Added Small Passive Skills also grant: +(11-20) to Evasion<br>Added Small Passive Skills also grant: +(21-30) to Evasion<br>Added Small Passive Skills also grant: +(31-40) to Evasion<br>Added Small Passive Skills also grant: +(41-53) to Evasion<br>Added Small Passive Skills also grant: +(54-66) to Evasion | works |
| `local_affliction_jewel_small_nodes_grant_fire_resistance_%` | Added Small Passive Skills also grant: +(10-11)% to Fire Resistance<br>Added Small Passive Skills also grant: +(2-3)% to Fire Resistance<br>Added Small Passive Skills also grant: +(4-5)% to Fire Resistance<br>Added Small Passive Skills also grant: +(6-7)% to Fire Resistance<br>Added Small Passive Skills also grant: +(8-9)% to Fire Resistance | works |
| `local_affliction_jewel_small_nodes_grant_int` | Added Small Passive Skills also grant: +(12-14) to Intelligence<br>Added Small Passive Skills also grant: +(2-3) to Intelligence<br>Added Small Passive Skills also grant: +(4-5) to Intelligence<br>Added Small Passive Skills also grant: +(6-8) to Intelligence<br>Added Small Passive Skills also grant: +(9-11) to Intelligence | works |
| `local_affliction_jewel_small_nodes_grant_lightning_resistance_%` | Added Small Passive Skills also grant: +(10-11)% to Lightning Resistance<br>Added Small Passive Skills also grant: +(2-3)% to Lightning Resistance<br>Added Small Passive Skills also grant: +(4-5)% to Lightning Resistance<br>Added Small Passive Skills also grant: +(6-7)% to Lightning Resistance<br>Added Small Passive Skills also grant: +(8-9)% to Lightning Resistance | works |
| `local_affliction_jewel_small_nodes_grant_mana_regeneration_+%` | Added Small Passive Skills also grant: (7-8)% increased Mana Regeneration Rate<br>Added Small Passive Skills also grant: (9-10)% increased Mana Regeneration Rate<br>Added Small Passive Skills also grant: 4% increased Mana Regeneration Rate<br>Added Small Passive Skills also grant: 5% increased Mana Regeneration Rate<br>Added Small Passive Skills also grant: 6% increased Mana Regeneration Rate | works |
| `local_affliction_jewel_small_nodes_grant_maximum_energy_shield` | Added Small Passive Skills also grant: +(10-12) to Maximum Energy Shield<br>Added Small Passive Skills also grant: +(13-16) to Maximum Energy Shield<br>Added Small Passive Skills also grant: +(17-20) to Maximum Energy Shield<br>Added Small Passive Skills also grant: +(4-5) to Maximum Energy Shield<br>Added Small Passive Skills also grant: +(6-9) to Maximum Energy Shield | works |
| `local_affliction_jewel_small_nodes_grant_maximum_life` | Added Small Passive Skills also grant: +(11-13) to Maximum Life<br>Added Small Passive Skills also grant: +(14-16) to Maximum Life<br>Added Small Passive Skills also grant: +(2-3) to Maximum Life<br>Added Small Passive Skills also grant: +(4-7) to Maximum Life<br>Added Small Passive Skills also grant: +(8-10) to Maximum Life | works |
| `local_affliction_jewel_small_nodes_grant_maximum_mana` | Added Small Passive Skills also grant: +(11-13) to Maximum Mana<br>Added Small Passive Skills also grant: +(14-16) to Maximum Mana<br>Added Small Passive Skills also grant: +(2-5) to Maximum Mana<br>Added Small Passive Skills also grant: +(6-8) to Maximum Mana<br>Added Small Passive Skills also grant: +(9-10) to Maximum Mana | works |
| `local_affliction_jewel_small_nodes_grant_minion_attack_and_cast_speed_+%` | Added Small Passive Skills also grant: Minions have 1% increased Attack and Cast Speed<br>Added Small Passive Skills also grant: Minions have 2% increased Attack and Cast Speed<br>Added Small Passive Skills also grant: Minions have 3% increased Attack and Cast Speed | not mapped |
| `local_affliction_jewel_small_nodes_grant_minion_attack_and_cast_speed_+%_while_you_are_affected_by_a_herald` | Added Small Passive Skills also grant: Minions have 1% increased Attack and Cast Speed while you are affected by a Herald<br>Added Small Passive Skills also grant: Minions have 2% increased Attack and Cast Speed while you are affected by a Herald<br>Added Small Passive Skills also grant: Minions have 3% increased Attack and Cast Speed while you are affected by a Herald | not mapped |
| `local_affliction_jewel_small_nodes_grant_minion_life_regeneration_rate_per_minute_%` | Added Small Passive Skills also grant: Minions Regenerate 0.1% of Life per Second<br>Added Small Passive Skills also grant: Minions Regenerate 0.15% of Life per Second<br>Added Small Passive Skills also grant: Minions Regenerate 0.2% of Life per Second | not mapped |
| `local_affliction_jewel_small_nodes_grant_sigil_target_search_range_+%` | Added Small Passive Skills also grant: 1% increased Brand Attachment range<br>Added Small Passive Skills also grant: 2% increased Brand Attachment range<br>Added Small Passive Skills also grant: 3% increased Brand Attachment range | not mapped |
| `local_affliction_jewel_small_nodes_grant_str` | Added Small Passive Skills also grant: +(12-14) to Strength<br>Added Small Passive Skills also grant: +(2-3) to Strength<br>Added Small Passive Skills also grant: +(4-5) to Strength<br>Added Small Passive Skills also grant: +(6-8) to Strength<br>Added Small Passive Skills also grant: +(9-11) to Strength | works |
| `local_affliction_jewel_small_nodes_grant_summon_totem_cast_speed_+%` | Added Small Passive Skills also grant: 1% increased Totem Placement speed<br>Added Small Passive Skills also grant: 2% increased Totem Placement speed<br>Added Small Passive Skills also grant: 3% increased Totem Placement speed | not mapped |
| `local_affliction_jewel_small_nodes_grant_trap_and_mine_throwing_speed_+%` | Added Small Passive Skills also grant: 1% increased Trap and Mine Throwing Speed<br>Added Small Passive Skills also grant: 2% increased Trap and Mine Throwing Speed<br>Added Small Passive Skills also grant: 3% increased Trap and Mine Throwing Speed | not mapped |
| `local_affliction_jewel_small_nodes_grant_warcry_duration_+%` | Added Small Passive Skills also grant: 2% increased Warcry Duration<br>Added Small Passive Skills also grant: 3% increased Warcry Duration<br>Added Small Passive Skills also grant: 4% increased Warcry Duration | not mapped |

## Notables (301 mods, 301 `PassiveSkills` rows)

| Notable | Effect | Server |
|---|---|---|
| **Adrenaline** | 8% increased maximum Life; 6% increased Attack Speed while Leeching | partial (1/2 stats read); conditional |
| **Advance Guard** | Attack Skills deal 30% increased Damage while holding a Shield; 5% increased Movement Speed while holding a Shield; Ignore all Movement Penalties from Armour | inert (no stat read); conditional |
| **Aerialist** | +10% chance to Suppress Spell Damage; 5% increased Dexterity | works |
| **Aerodynamics** | 10% increased Projectile Damage; 10% increased Projectile Speed; Projectiles Pierce an additional Target | works |
| **Agent of Destruction** | 25% increased Elemental Damage while affected by a Herald; 10% chance to Freeze, Shock and Ignite while affected by a Herald | inert (no stat read); conditional |
| **Aggressive Defence** | Fortifying Hits grant 60% increased Fortification; 30% increased Fortification Duration; 20% increased Damage with Attack Skills while Fortified | partial (2/3 stats read); conditional |
| **Alchemist** | +20% to Cold Resistance; 8% increased Attack and Cast Speed; 20% increased Life Recovery from Flasks | works |
| **Ancestral Echo** | 20% increased Totem Placement speed; 10% increased Attack and Cast Speed if you've summoned a Totem Recently | inert (no stat read); conditional |
| **Ancestral Guidance** | Totems' Action Speed cannot be modified to below Base Value; Totems Regenerate 120% of Life per second | inert (no stat read); conditional |
| **Ancestral Inspiration** | Gain Arcane Surge when you Summon a Totem; Spells cast by Totems deal 25% increased Damage | inert (no stat read); conditional |
| **Ancestral Might** | 15% increased Totem Life; 30% increased Totem Duration; 30% increased Totem Damage if you haven't Summoned a Totem in the past 2 seconds | inert (no stat read); conditional |
| **Ancestral Preservation** | Totems have 15% additional Physical Damage Reduction; Totems gain +25% to Chaos Resistance; Totems gain +20% to all Elemental Resistances | inert (no stat read) |
| **Ancestral Reach** | 25% increased Totem Damage; 25% increased Totem Placement speed; 25% increased Totem Placement range | inert (no stat read) |
| **Antifreeze** | +20% to Cold Resistance; 30% chance to Avoid being Frozen; +1% to maximum Cold Resistance | works |
| **Antivenom** | +23% to Chaos Resistance; 30% chance to Avoid being Poisoned; +1% to maximum Chaos Resistance | works |
| **Arcane Adept** | Channelling Skills deal 20% increased Damage; 5% increased Attack and Cast Speed while Channelling; Gain Arcane Surge after Channelling for 1 second | inert (no stat read); conditional |
| **Arcane Heroism** | 10% chance to gain Arcane Surge when you Hit a Unique enemy; 30% increased Effect of Arcane Surge on you | inert (no stat read); conditional |
| **Arcane Pyrotechnics** | 20% increased Trap Damage; 20% increased Mine Damage; Gain Arcane Surge when your Trap is Triggered by an Enemy; Gain Arcane Surge when your Mine is Detonated targeting an Enemy | inert (no stat read); conditional |
| **Arcing Shot** | Arrows gain Damage as they travel farther, dealing up to 50% increased Damage with Hits to targets; Arrows gain Critical Strike Chance as they travel farther, up to 100% increased Critical Strike Chance | inert (no stat read) |
| **Assert Dominance** | 25% increased Area of Effect if you've Killed at least 5 Enemies Recently | inert (no stat read); conditional |
| **Astonishing Affliction** | 20% increased Effect of Non-Damaging Ailments; 20% increased Damage with Hits and Ailments against Enemies affected by Ailments; 20% increased Duration of Elemental Ailments on Enemies | partial (2/3 stats read) |
| **Basics of Pain** | 20% increased Damage; 30% increased Global Critical Strike Chance | works |
| **Battle-Hardened** | 35% increased Global Physical Damage; 30% increased Evasion Rating and Armour | works |
| **Battlefield Dominator** | 15% increased Area of Effect if you've Stunned an Enemy with a Two Handed Melee Weapon Recently; Attacks with Two Handed Weapons deal 25% increased Damage with Hits and Ailments; 10% reduced Enemy Stun Threshold | partial (1/3 stats read); conditional |
| **Blacksmith** | +20% to Fire Resistance; 25% increased Armour; 40% of Fire Damage Leeched as Life | partial (2/3 stats read); conditional |
| **Blanketed Snow** | Damage Penetrates 10% Cold Resistance against Chilled Enemies | inert (no stat read) |
| **Blast-Freeze** | 20% increased Cold Damage; Freezes you inflict spread to other Enemies within 1.2 metres | partial (1/2 stats read) |
| **Blessed** | +13% to Chaos Resistance; 6% increased maximum Life; 10% increased maximum Mana | works |
| **Blessed Rebirth** | Minions have 20% increased maximum Life; Minions created Recently cannot be Damaged | inert (no stat read); conditional |
| **Blizzard Caller** | Frostbite can affect Hexproof Enemies; 30% increased Duration of Freezes you inflict on Cursed Enemies | inert (no stat read); conditional |
| **Blood Artist** | base_strength 20; +6% to Physical Damage over Time Multiplier if you've Spent Life Recently | partial (1/2 stats read); conditional |
| **Bloodscent** | Gain 2 Rage on Hit with Axes or Swords | inert (no stat read); conditional |
| **Blowback** | Ignites you inflict deal Damage 8% faster; 10% chance to Ignite | partial (1/2 stats read) |
| **Bodyguards** | Minions have 10% increased maximum Life; Minions have 10% chance to Knock Enemies Back on Hit with Attacks | inert (no stat read) |
| **Born of Chaos** | +3% to maximum Chaos Resistance | works |
| **Brand Loyalty** | Enemies take 3% increased Damage for each of your Brands Attached to them | inert (no stat read) |
| **Brewed for Potency** | 24% increased Damage over Time; 20% increased Life and Mana Recovery from Flasks; 10% increased Flask Charges gained | partial (1/3 stats read) |
| **Broadside** | Bow Skills have 25% increased Area of Effect | inert (no stat read); conditional |
| **Brush with Death** | +5% to Damage over Time Multiplier; Recover 1% of Life on Kill; Recover 1% of Energy Shield on Kill | inert (no stat read); conditional |
| **Brutal Infamy** | Attacks with Two Handed Melee Weapons deal 20% increased Damage with Hits and Ailments; 40% increased Damage with Hits and Ailments against Unique Enemies | inert (no stat read) |
| **Burden Projection** | 30% increased Spell Damage; 8% chance to Knock Enemies Back on Hit with Spell Damage | partial (1/2 stats read); conditional |
| **Burning Bright** | 25% increased Burning Damage; 8% increased Area of Effect | partial (1/2 stats read) |
| **Calamitous** | 30% increased Elemental Damage with Attack Skills; 10% chance to Freeze, Shock and Ignite; 15% increased Effect of Non-Damaging Ailments | partial (2/3 stats read); conditional |
| **Call to the Slaughter** | Minions deal 15% increased Damage; Minions created Recently have 30% increased Movement Speed; Minions created Recently have 10% increased Attack and Cast Speed | partial (1/3 stats read); conditional |
| **Capacitor** | -30% increased Effect of Shock on you; Unaffected by Shocked Ground; You cannot be Shocked if you've been Shocked Recently | partial (1/3 stats read); conditional |
| **Careful Handling** | 15% increased Trap Damage; 15% increased Mine Damage; 4% increased maximum Life; 6% increased maximum Mana | partial (2/4 stats read) |
| **Chilling Presence** | Nearby Enemies are Chilled | inert (no stat read) |
| **Chip Away** | Brand Recall has 4% increased Cooldown Recovery Rate per Brand, up to a maximum of 40%; Brand Recall grants 20% increased Brand Attachment range to recalled Brands | inert (no stat read); conditional |
| **Circling Oblivion** | 25% increased Damage over Time; 15% increased Duration of Ailments on Enemies | partial (1/2 stats read) |
| **Clarity of Purpose** | 15% increased maximum Mana; 30% increased Mana Regeneration Rate | works |
| **Cold-Blooded Killer** | +5% to Cold Damage over Time Multiplier; Recover 2% of Life on Killing a Chilled Enemy | inert (no stat read); conditional |
| **Cold Conduction** | 25% increased Effect of Lightning Ailments you inflict on Chilled Enemies; 25% increased Effect of Cold Ailments you inflict on Shocked Enemies | inert (no stat read); conditional |
| **Cold to the Core** | 1% increased Cold Damage per 25 Strength; 1% increased Cold Damage per 25 Dexterity; 1% increased Cold Damage per 25 Intelligence | inert (no stat read); conditional |
| **Combat Rhythm** | 10% increased Attack Speed if you've Hit with your Main Hand Weapon Recently; 10% increased Movement Speed if you've Hit with your Off Hand Weapon Recently | inert (no stat read); conditional |
| **Compound Injury** | 35% increased Damage with Bleeding you inflict on Maimed Enemies | inert (no stat read) |
| **Confident Combatant** | 1% increased Damage per 1% Chance to Block Attack Damage | inert (no stat read); conditional |
| **Conjured Wall** | 25% increased Spell Damage; +8% Chance to Block Spell Damage if you've Cast a Spell Recently | partial (1/2 stats read); conditional |
| **Conservation of Energy** | 10% increased maximum Energy Shield; 30% of Spell Damage Leeched as Energy Shield | partial (1/2 stats read); conditional |
| **Cooked Alive** | 15% chance to Ignite; Enemies Ignited by you have -5% to Fire Resistance | partial (1/2 stats read) |
| **Corrosive Elements** | Fire Skills have a 25% chance to apply Fire Exposure on Hit; Cold Skills have a 25% chance to apply Cold Exposure on Hit; Lightning Skills have a 25% chance to apply Lightning Exposure on Hit; 15% increased Elemental Damage | partial (1/4 stats read) |
| **Cremator** | 30% increased Fire Damage; Ignited Enemies Killed by your Hits are destroyed | inert (no stat read); conditional |
| **Cry Wolf** | Exerted Attacks deal 30% increased Damage; 20% increased total Power counted by Warcries | inert (no stat read) |
| **Cult-Leader** | Minions deal 35% increased Damage while you are affected by a Herald | inert (no stat read); conditional |
| **Daring Ideas** | 18% increased maximum Mana; 40% of Attack Damage Leeched as Mana; 25% increased Cost Efficiency of Attacks | partial (1/3 stats read); conditional |
| **Doedre's Spite** | Enfeeble can affect Hexproof Enemies; 35% increased Critical Strike Chance against Cursed Enemies | inert (no stat read); conditional |
| **Dark Ideation** | 2% increased Chaos Damage per 100 maximum Mana, up to a maximum of 80% | inert (no stat read); conditional |
| **Dark Messenger** | Herald Skills deal 20% increased Damage; Herald Skills have 25% increased Area of Effect | inert (no stat read); conditional |
| **Darting Movements** | +12% chance to Suppress Spell Damage while moving; 3% increased Movement Speed | partial (2/2 stats read); conditional |
| **Deadly Repartee** | Attack Skills deal 25% increased Damage while Dual Wielding; +6% Chance to Block Attack Damage while Dual Wielding; 30% increased Attack Critical Strike Chance while Dual Wielding | partial (2/3 stats read); conditional |
| **Deep Chill** | +5% to Cold Damage over Time Multiplier; 30% increased Effect of Cold Ailments | partial (1/2 stats read) |
| **Deep Cuts** | 15% chance to Impale Enemies on Hit with Attacks; Impales you inflict last 1 additional Hit | partial (1/2 stats read); conditional |
| **Devastator** | 20% increased Attack Damage; 20% increased Damage with Ailments from Attack Skills; Enemies Killed with Attack Hits have a 15% chance to Explode, dealing a tenth of their Life as Physical Damage | partial (1/3 stats read); conditional |
| **Disciples** | Minions deal 20% increased Damage while you are affected by a Herald; Summoned Sentinels have 25% increased Cooldown Recovery Rate | inert (no stat read); conditional |
| **Disease Vector** | +6% to Damage over Time Multiplier for Poison while wielding a Claw or Dagger; Enemies Poisoned by you cannot Regenerate Life | inert (no stat read); conditional |
| **Disorienting Display** | 25% increased Elemental Damage; 10% chance to Blind nearby Enemies when you use an Elemental Skill | partial (1/2 stats read) |
| **Disorienting Wounds** | 25% increased Damage with Bleeding; 25% chance to Blind with Hits against Bleeding Enemies | inert (no stat read); conditional |
| **Distilled Perfection** | 25% increased Life Recovery from Flasks; 25% increased Mana Recovery from Flasks; 10% increased Flask Effect Duration | works |
| **Doedre's Apathy** | Temporal Chains can affect Hexproof Enemies; Enemies you Curse are Unnerved | inert (no stat read) |
| **Doedre's Gluttony** | Punishment can affect Hexproof Enemies; You have Culling Strike against Cursed Enemies | inert (no stat read) |
| **Doryani's Lesson** | 25% increased Elemental Damage; 20% of Elemental Damage Leeched as Life | partial (1/2 stats read); conditional |
| **Dragon Hunter** | +20% to Fire Resistance; 30% increased Armour; 15% chance to Defend with 200% of Armour | partial (3/3 stats read); conditional |
| **Dread March** | Minions have 10% increased maximum Life; Minions have 10% additional Physical Damage Reduction; Minions have +10% to Chaos Resistance; Minions have 10% increased Movement Speed | inert (no stat read) |
| **Drive the Destruction** | 80% of Attack Damage Leeched as Life; 25% increased Attack Damage when on Full Life; Attacks have 10% chance to Maim on Hit | inert (no stat read); conditional |
| **Eldritch Inspiration** | 20% increased Chaos Damage; 16% increased maximum Mana; 20% increased Mana Regeneration Rate | partial (2/3 stats read) |
| **Elegant Form** | +10% chance to Suppress Spell Damage; 20% chance to Avoid Elemental Ailments | works |
| **Empowered Envoy** | Herald Skills deal 40% increased Damage; 20% increased Effect of Herald Buffs on you | inert (no stat read); conditional |
| **Endbringer** | 20% increased Damage while affected by a Herald; 5% increased Damage for each Herald affecting you | inert (no stat read); conditional |
| **Enduring Composure** | 30% increased Armour; Gain 1 Endurance Charge every second if you've been Hit Recently | partial (2/2 stats read); conditional |
| **Enduring Focus** | Channelling Skills deal 25% increased Damage; 25% chance to gain an Endurance Charge each second while Channelling | inert (no stat read); conditional |
| **Enduring Ward** | 20% chance to gain an Endurance Charge when you Block; +10% Chance to Block Attack Damage while at Maximum Endurance Charges | partial (1/2 stats read); conditional |
| **Energy From Naught** | +70 to maximum Energy Shield; 15% increased Energy Shield Recharge Rate | works |
| **Essence Rush** | 30% of Spell Damage Leeched as Energy Shield; 5% increased Attack and Cast Speed while Leeching Energy Shield; 40% increased Damage while Leeching Energy Shield | inert (no stat read); conditional |
| **Eternal Suffering** | 25% increased Chaos Damage; 50% chance to inflict Withered for two seconds on Hit if there are 5 or fewer Withered Debuffs on Enemy | inert (no stat read); conditional |
| **Evil Eye** | Enemies you Curse take 6% increased Damage | inert (no stat read); conditional |
| **Expansive Might** | Mace, Sceptre or Staff Attacks deal 20% increased Damage with Hits and Ailments; 10% increased Area of Effect per second you've been stationary, up to a maximum of 50% | inert (no stat read); conditional |
| **Expendability** | 10% chance to throw up to 1 additional Trap or Mine | inert (no stat read) |
| **Expert Sabotage** | Mines have a 10% chance to be Detonated an Additional Time; Mines have 20% increased Detonation Speed | inert (no stat read) |
| **Exploit Weakness** | Vulnerability can affect Hexproof Enemies; Recover {}% of Life when you Kill a Cursed Enemy | inert (no stat read); conditional |
| **Explosive Force** | Gain 10% of Wand Physical Damage as Extra Chaos Damage; Enemies Killed with Wand Hits have a 10% chance to Explode, dealing a quarter of their Life as Chaos Damage | inert (no stat read); conditional |
| **Exposure Therapy** | +5% to Damage over Time Multiplier; +30% Chaos Resistance against Damage Over Time | partial (1/2 stats read); conditional |
| **Eye of the Storm** | 40% increased Global Critical Strike Chance; +10% to Damage over Time Multiplier for Ignite from Critical Strikes; 20% increased Effect of non-Damaging Ailments you inflict with Critical Strikes | partial (1/3 stats read); conditional |
| **Eye to Eye** | 25% increased Projectile Damage; Projectiles deal 35% increased Damage with Hits against nearby Enemies | partial (1/2 stats read) |
| **Fan of Blades** | Attack Skills fire an additional Projectile while wielding a Claw or Dagger; 20% increased Projectile Attack Damage with Claws or Daggers | inert (no stat read); conditional |
| **Fan the Flames** | Ignites you inflict spread to other Enemies within a Radius of 1.5 metres | inert (no stat read) |
| **Fasting** | 20% increased Flask Charges gained; 10% increased Movement Speed while under no Flask Effects | inert (no stat read); conditional |
| **Fearsome Warrior** | Attacks with Two Handed Melee Weapons deal 25% increased Damage with Hits and Ailments; 8% increased Area of Effect; 25% chance to Intimidate nearby Enemies for 4 seconds on Melee Kill | partial (1/3 stats read); conditional |
| **Feasting Fiends** | Minions have 10% increased maximum Life; Minions deal 10% increased Damage; Minions Leech 40% of Damage as Life | partial (1/3 stats read); conditional |
| **Feast of Flesh** | 8% increased maximum Life; 40% of Attack Damage Leeched as Life; Gain 10 Life per Enemy Hit with Attacks | partial (2/3 stats read); conditional |
| **Feed the Fury** | 40% of Attack Damage Leeched as Life; 30% increased Damage while Leeching; 15% increased Attack Speed while Leeching | inert (no stat read); conditional |
| **Fettle** | 10% increased maximum Life; +20 to maximum Life | works |
| **Fiery Aegis** | +5% Chance to Block Attack Damage; Hallowing Flame you inflict has 1% increased magnitude per 2% Attack Block chance | partial (1/2 stats read); conditional |
| **Fire Attunement** | 30% reduced Ignite Duration on you; Unaffected by Burning Ground; You cannot be Ignited if you've been Ignited Recently | partial (1/3 stats read); conditional |
| **Spiteful Presence** | Hatred has 50% increased Mana Reservation Efficiency; 20% increased Effect of Cold Ailments | partial (1/2 stats read) |
| **Flexible Sentry** | +4% Chance to Block Attack Damage; 4% Chance to Block Spell Damage; 25% reduced Elemental Ailment Duration on you; -25% increased Effect of Chill and Shock on you | partial (3/4 stats read); conditional |
| **Flow of Life** | 24% increased Damage over Time; 4% increased maximum Life; Regenerate 36% of Life per second | partial (2/3 stats read); conditional |
| **Follow-Through** | Projectiles deal 20% increased Damage with Hits and Ailments for\neach remaining Chain, up to a maximum of 100% | inert (no stat read); conditional |
| **Force Multiplier** | 25% increased Global Physical Damage; 5% chance to deal Double Damage | partial (1/2 stats read) |
| **Fuel the Fight** | 8% increased Attack Speed; 40% of Attack Damage Leeched as Mana; 20% increased Damage while Leeching | partial (1/3 stats read); conditional |
| **Furious Assault** | 25% increased Global Physical Damage; 8% increased Attack and Cast Speed | works |
| **Genius** | 8% increased maximum Mana; 5% increased Intelligence | works |
| **Gladiatorial Combat** | 2% increased Attack Damage per 75 Armour or Evasion Rating on Shield; +1% to Critical Strike Multiplier per 10 Maximum Energy Shield on Shield | inert (no stat read); conditional |
| **Gladiator's Fortitude** | Attack Skills deal 25% increased Damage while holding a Shield; 5% increased maximum Life | partial (1/2 stats read); conditional |
| **Graceful Execution** | base_dexterity_and_intelligence 15; 5% increased Attack Speed with Two Handed Melee Weapons; 15% increased Accuracy Rating with Two Handed Melee Weapons; 25% increased Critical Strike Chance with Two Handed Melee Weapons | works |
| **Grand Design** | 20% increased Brand Damage; 10% increased Brand Attachment range; Brand Skills have 10% increased Duration | inert (no stat read) |
| **Grim Oath** | Gain 10% of Physical Damage as Extra Chaos Damage | inert (no stat read) |
| **Introspection** | Auras from your Skills have 10% increased Effect on you | partial (1/1 stats read); conditional |
| **Guerilla Tactics** | 20% increased Trap Damage; 20% increased Mine Damage; 10% increased Trap Throwing Speed; 10% increased Mine Throwing Speed; 5% increased Movement Speed if you've thrown a Trap or Mine Recently | partial (2/5 stats read); conditional |
| **Haemorrhage** | 30% increased Global Critical Strike Chance; +10% to Damage over Time Multiplier for Ailments from Critical Strikes | partial (1/2 stats read) |
| **Haunting Shout** | Enemies Taunted by your Warcries are Intimidated | inert (no stat read) |
| **Heart of Iron** | Gain 10% of Maximum Life as Extra Armour | inert (no stat read) |
| **Heavy Hitter** | 30% increased Attack Damage; 30% increased Damage with Ailments from Attack Skills; 10% reduced Enemy Stun Threshold; 20% chance to double Stun Duration | partial (2/4 stats read) |
| **Heraldry** | Nearby Enemies have Fire Exposure while you are affected by Herald of Ash; Nearby Enemies have Cold Exposure while you are affected by Herald of Ice; Nearby Enemies have Lightning Exposure while you are affected by Herald of Thunder | inert (no stat read); conditional |
| **Hex Breaker** | Remove a Curse after Channelling for 2 seconds; 8% increased Attack and Cast Speed while Channelling | inert (no stat read); conditional |
| **Hibernator** | 30% reduced Freeze Duration on you; -30% increased Effect of Chill on you; Unaffected by Chilled Ground; You cannot be Frozen if you've been Frozen Recently | partial (2/4 stats read); conditional |
| **Hit and Run** | Attack Skills deal 25% increased Damage while Dual Wielding; +10% chance to Suppress Spell Damage if you've Hit an Enemy Recently | partial (1/2 stats read); conditional |
| **Holistic Health** | 8% increased maximum Life; 10% increased maximum Mana | works |
| **Holy Conquest** | 25% increased Brand Damage; Brands have 25% increased Area of Effect if 50% of Attached Duration expired | inert (no stat read); conditional |
| **Holy Word** | Your Warcries inflict Hallowing Flame | inert (no stat read) |
| **Hound's Mark** | 20% increased Effect of your Marks; 20% increased Damage with Hits and Ailments against Marked Enemy | inert (no stat read); conditional |
| **Hulking Corpses** | Minions have 25% increased maximum Life; Raised Zombies have 5% chance to Taunt Enemies on Hit | inert (no stat read) |
| **Improvisor** | 6% increased Attack Speed; 10% increased maximum Mana; Gain 3 Mana per Enemy Hit with Attacks | partial (3/3 stats read); conditional |
| **Insatiable Killer** | Attack Skills deal 20% increased Damage while Dual Wielding; 5% increased Attack Speed while Dual Wielding; 5% chance to gain a Frenzy Charge on Kill while Dual Wielding | partial (2/3 stats read); conditional |
| **Inspired Oppression** | 10% increased Effect of Non-Damaging Ailments; 20% increased Elemental Damage; 30% increased Mana Regeneration Rate if you have Frozen an Enemy Recently; 30% increased Mana Regeneration Rate if you have Shocked an Enemy Recently | partial (2/4 stats read); conditional |
| **Insulated** | +20% to Lightning Resistance; 30% chance to Avoid being Shocked; +1% to maximum Lightning Resistance | works |
| **Intensity** | 10% increased Area Damage; Spells which can gain Intensity have +1 to maximum Intensity | partial (1/2 stats read) |
| **Invigorating Portents** | Minions deal 20% increased Damage while you are affected by a Herald; Minions have 10% increased Movement Speed for each Herald affecting you | inert (no stat read); conditional |
| **Iron Breaker** | Hits have 50% chance to ignore Enemy Physical Damage Reduction; 30% increased Global Physical Damage | partial (1/2 stats read) |
| **Lasting Impression** | Herald Skills deal 50% increased Damage over Time; 30% increased Damage over Time while affected by a Herald | inert (no stat read); conditional |
| **Lead By Example** | base_strength_and_dexterity 10; When you Warcry, you and nearby Allies gain Onslaught for 4 seconds | partial (1/2 stats read); conditional |
| **Life from Death** | Minions have 15% increased maximum Life; Regenerate 120% of Life per second if a Minion has Died Recently; Minions Recover 4% of Life on Minion Death | inert (no stat read); conditional |
| **Liquid Inspiration** | 15% increased maximum Mana; 20% increased Mana Recovery from Flasks; 25% chance to gain a Power Charge when you use a Mana Flask | partial (2/3 stats read); conditional |
| **Lord of Drought** | Flammability can affect Hexproof Enemies; 30% increased Damage with Ignites inflicted on Cursed Enemies | inert (no stat read); conditional |
| **Low Tolerance** | Poisons you inflict on non-Poisoned Enemies deal 300% increased Damage | inert (no stat read); conditional |
| **Mage Bane** | Attack Skills deal 20% increased Damage while Dual Wielding; 20% chance to gain a Power Charge when you Block; +6% Chance to Block Spell Damage while Dual Wielding | partial (2/3 stats read); conditional |
| **Mage Hunter** | 20% increased Spell Damage; 6% Chance to Block Spell Damage; 20% chance to gain a Power Charge when you Block | partial (3/3 stats read); conditional |
| **Magnifier** | 10% increased Area of Effect; +10% to Global Critical Strike Multiplier | works |
| **Martial Mastery** | base_strength 20; 10% increased Attack Speed with Two Handed Melee Weapons; 10% increased Attack Speed if you have at least 600 Strength | partial (2/3 stats read); conditional |
| **Martial Momentum** | 8% increased Attack Speed while Dual Wielding; 16% increased Accuracy Rating while Dual Wielding; 32% increased Damage if you've used a Travel Skill Recently | partial (2/3 stats read); conditional |
| **Martial Prowess** | 20% increased Attack Damage; 6% increased Attack Speed; 15% increased Global Accuracy Rating; 20% increased Damage with Ailments from Attack Skills | partial (3/4 stats read) |
| **Master of the Maelstrom** | Elemental Weakness can affect Hexproof Enemies; +1% to all maximum Elemental Resistances if you have Killed a Cursed Enemy Recently | inert (no stat read); conditional |
| **Master of Command** | Banner Skills have 20% increased Area of Effect; Banner Skills have 10% increased Aura Effect | inert (no stat read) |
| **Master of Fear** | Enemies you Curse are Unnerved | inert (no stat read) |
| **Master of Fire** | Nearby Enemies have Fire Exposure | inert (no stat read) |
| **Master the Fundamentals** | 35% increased Global Physical Damage; -35% increased Elemental Damage; +10% to all Elemental Resistances | works |
| **Mender's Wellspring** | 25% increased Life Recovery from Flasks; Life Flasks gain 1 Charge every 3 seconds; Remove Bleeding when you use a Life Flask | partial (2/3 stats read); conditional |
| **Militarism** | 8% increased maximum Life; 30% increased Armour | works |
| **Mindfulness** | 15% increased maximum Mana; 80% increased Mana Regeneration Rate while stationary | partial (1/2 stats read); conditional |
| **Misery Everlasting** | Despair can affect Hexproof Enemies; Recover {}% of Energy Shield when you Kill a Cursed Enemy | inert (no stat read); conditional |
| **Mob Mentality** | Exerted Attacks deal 25% increased Damage; Warcries have 5% Chance to grant an Endurance, Frenzy or Power Charge per Power | inert (no stat read); conditional |
| **Molten One's Mark** | +2% to maximum Fire Resistance; Regenerate 60% of Life per second | partial (2/2 stats read); conditional |
| **Mystical Ward** | 6% Chance to Block Spell Damage; Regenerate 90% of Energy Shield per second; 30% of Spell Damage Leeched as Energy Shield | partial (2/3 stats read); conditional |
| **Natural Vigour** | 8% increased maximum Life; 30% increased Evasion Rating | works |
| **No Witnesses** | 10% chance to gain Elusive on Kill; 25% increased Elusive Effect | inert (no stat read); conditional |
| **Non-Flammable** | +20% to Fire Resistance; 30% chance to Avoid being Ignited; +1% to maximum Fire Resistance | works |
| **Numbing Elixir** | -40% increased Effect of Curses on you during Effect of any Mana Flask; -40% increased Effect of Non-Damaging Ailments on you during Effect of any Life Flask | inert (no stat read); conditional |
| **One with the Shield** | Recover 50 Life when you Block; 50% increased Defences from Equipped Shield; +6% Chance to Block Attack Damage while holding a Shield | partial (1/3 stats read); conditional |
| **Openness** | +30 to maximum Mana; 20% increased maximum Mana; 15% increased Mana Cost Efficiency | partial (2/3 stats read) |
| **Opportunistic Fusilade** | 35% increased Damage with Wands if you've dealt a Critical Strike Recently; 50% increased Critical Strike Chance with Wands | partial (1/2 stats read); conditional |
| **Overlord** | Melee Hits with Maces, Sceptres or Staves Fortify for 6 seconds; 30% increased Damage with Maces, Sceptres or Staves | inert (no stat read); conditional |
| **Overshock** | 30% increased Lightning Damage; 40% increased Effect of Lightning Ailments | works |
| **Overwhelming Malice** | 10% chance to gain Unholy Might for 4 seconds on Critical Strike | inert (no stat read); conditional |
| **Paralysis** | 30% increased Lightning Damage; Lightning Skills have 10% reduced Enemy Stun Threshold; 10% chance to double Stun Duration | partial (1/3 stats read) |
| **Peace Amidst Chaos** | 8% increased maximum Life; Regenerate 120% of Life per second while stationary; 20% increased Armour while stationary | partial (1/3 stats read); conditional |
| **Peak Vigour** | 8% increased maximum Life; 20% increased Life Recovery from Flasks | works |
| **Phlebotomist** | 20% increased Global Critical Strike Chance; +6% to Physical Damage over Time Multiplier if you've dealt a Critical Strike Recently | partial (1/2 stats read); conditional |
| **Powerful Assault** | 20% increased Area Damage; Area Skills have 10% chance to Knock Enemies Back on Hit | partial (1/2 stats read) |
| **Powerful Ward** | 20% chance to gain a Power Charge when you Block; +10% Chance to Block Spell Damage while at Maximum Power Charges | partial (1/2 stats read); conditional |
| **Practiced Caster** | 20% increased Spell Damage; 5% increased Cast Speed; 35% chance to Ignore Stuns while Casting | partial (2/3 stats read); conditional |
| **Destructive Aspect** | Pride has 50% increased Mana Reservation Efficiency; 12% increased Area of Effect of Aura Skills | partial (1/2 stats read) |
| **Precise Focus** | 30% increased Critical Strike Chance while Channelling; +20% to Critical Strike Multiplier if you've been Channelling for at least 1 second | inert (no stat read); conditional |
| **Precise Retaliation** | +30% to Critical Strike Multiplier if you have Blocked Recently; 60% increased Critical Strike Chance if you haven't Blocked Recently | inert (no stat read); conditional |
| **Pressure Points** | 30% increased Global Critical Strike Chance; Your Critical Strikes have a 5% chance to deal Double Damage | partial (1/2 stats read) |
| **Primordial Bond** | 10% increased Damage per Summoned Golem; 20% increased Effect of Buffs granted by your Golems; Golems have 25% increased Maximum Life | inert (no stat read); conditional |
| **Prismatic Carapace** | +1% to all maximum Elemental Resistances; 30% increased Armour | works |
| **Prismatic Dance** | +1% to all maximum Elemental Resistances; 30% increased Evasion Rating | works |
| **Prismatic Heart** | 30% increased Elemental Damage; +10% to all Elemental Resistances | works |
| **Prodigious Defence** | +4% Chance to Block Attack Damage; 4% Chance to Block Spell Damage; 30% increased Attack Damage while holding a Shield | partial (2/3 stats read); conditional |
| **Provocateur** | 40% increased Critical Strike Chance against Taunted Enemies; +10% to Critical Strike Multiplier against Taunted Enemies | inert (no stat read) |
| **Pure Agony** | Minions deal 20% increased Damage while you are affected by a Herald; +5 to Maximum Virulence | inert (no stat read); conditional |
| **Pure Aptitude** | base_intelligence 20; Purity of Lightning has 80% increased Mana Reservation Efficiency; 15% increased Energy Shield Recharge Rate | partial (2/3 stats read) |
| **Electric Presence** | Wrath has 50% increased Mana Reservation Efficiency; 20% increased Effect of Lightning Ailments | partial (1/2 stats read) |
| **Pure Guile** | base_dexterity 20; Purity of Ice has 80% increased Mana Reservation Efficiency; 5% Global chance to Blind Enemies on hit | partial (2/3 stats read); conditional |
| **Pure Might** | base_strength 20; Purity of Fire has 80% increased Mana Reservation Efficiency; 40% increased Stun and Block Recovery | partial (2/3 stats read) |
| **Purposeful Harbinger** | Auras from your Skills have 8% increased Effect on you for\neach Herald affecting you, up to a maximum of 40% | inert (no stat read); conditional |
| **Quick Getaway** | 5% increased Attack and Cast Speed; 25% increased Global Critical Strike Chance; 5% increased Movement Speed if you've dealt a Critical Strike Recently | partial (2/3 stats read); conditional |
| **Quick and Deadly** | 60% increased Main Hand Attack Damage while wielding two different Weapon Types; 30% increased Off Hand Attack Speed while wielding two different Weapon Types | inert (no stat read); conditional |
| **Unrestrained Focus** | Unaffected by Chill while Channelling; Channelling Skills have 8% increased Attack and Cast Speed | inert (no stat read); conditional |
| **Rattling Bellow** | base_strength 20; Exerted Attacks have 8% chance to deal Double Damage | partial (1/2 stats read) |
| **Raze and Pillage** | Minions gain 6% of Physical Damage as Extra Fire Damage; Minions have 20% chance to Ignite; Minions deal 20% increased Damage with Hits and Ailments against Ignited Enemies | inert (no stat read); conditional |
| **Readiness** | 30% reduced Evasion Rating if you haven't been Hit Recently; 30% chance to Avoid Bleeding; 30% chance to Avoid being Impaled | partial (1/3 stats read); conditional |
| **Remarkable** | 8% increased Cast Speed with Brand Skills; Skills which create Brands have 35% chance to create an additional Brand | inert (no stat read) |
| **Rend** | +5% to Physical Damage over Time Multiplier; 20% increased Bleeding Duration | inert (no stat read) |
| **Renewal** | Minions have 5% chance to deal Double Damage while they are on Full Life; Minions Regenerate 60% of Life per second | inert (no stat read); conditional |
| **Repeater** | 30% increased Projectile Damage; 8% increased Attack and Cast Speed | works |
| **Replenishing Presence** | Non-Curse Aura Skills have 20% increased Duration | inert (no stat read); conditional |
| **Riot Queller** | +6% Chance to Block Attack Damage; Enemies Taunted by you take 6% increased Damage | partial (1/2 stats read) |
| **Rot-Resistant** | +13% to Chaos Resistance; Regenerate 72% of Life per second; Regenerate 36% of Energy Shield per second; Regenerate 18% of Mana per second | partial (4/4 stats read); conditional |
| **Rote Reinforcement** | +20 to maximum Life; +6% Chance to Block Attack Damage; 20% chance to gain an Endurance Charge when you Block | partial (3/3 stats read); conditional |
| **Rotten Claws** | Minions have a 12% chance to Impale on Hit with Attacks | inert (no stat read); conditional |
| **Run Through** | 15% increased Physical Damage with Axes or Swords; 10% chance to Impale Enemies on Hit with Axes or Swords; 10% increased Impale Effect; Axe or Sword Attacks deal 15% increased Damage with Ailments | inert (no stat read); conditional |
| **Sadist** | 15% increased Elemental Damage if you've Chilled an Enemy Recently; 25% increased Elemental Damage if you've Shocked an Enemy Recently; 20% increased Elemental Damage if you've Ignited an Enemy Recently | inert (no stat read); conditional |
| **Sage** | +20% to Lightning Resistance; 20% increased Mana Regeneration Rate; Regenerate 90% of Life per second | partial (3/3 stats read); conditional |
| **Sap Psyche** | 20% increased Spell Damage; 30% increased Mana Regeneration Rate; Regenerate 60% of Energy Shield per second if you've Cursed an Enemy Recently | partial (2/3 stats read); conditional |
| **Savage Response** | 30% increased Global Critical Strike Chance; +30% to Critical Strike Multiplier if you've taken a Savage Hit Recently | partial (1/2 stats read); conditional |
| **Savour the Moment** | 10% increased maximum Energy Shield; Regenerate 180% of Energy Shield per second while stationary | partial (1/2 stats read); conditional |
| **Scintillating Idea** | 20% increased maximum Mana; Damage Penetrates 5% Lightning Resistance | works |
| **Seal Mender** | Skills Supported by Unleash have 30% increased Seal gain frequency | inert (no stat read) |
| **Second Skin** | +4% Chance to Block Attack Damage; 4% Chance to Block Spell Damage; 30% increased Armour | works |
| **Seeker Runes** | 25% increased Brand Damage; Unattached Brands gain 20% increased Brand Attachment Range per\nsecond, up to a maximum of 100% | inert (no stat read); conditional |
| **Self-Control** | Discipline has 80% increased Mana Reservation Efficiency; 25% increased Mana Regeneration Rate | partial (1/2 stats read) |
| **Self-Fulfilling Prophecy** | +15% to Critical Strike Multiplier if you dealt a Critical Strike with a Herald Skill Recently; +100% to Critical Strike Chance of Herald Skills | inert (no stat read); conditional |
| **Septic Spells** | 20% chance to Poison on Hit with Spell Damage; Spell Skills have +5% to Damage over Time Multiplier for Poison; 5% increased Cast Speed | partial (2/3 stats read); conditional |
| **Set and Forget** | 25% increased Trap Damage; 40% increased Trap Trigger Area of Effect; Skills used by Traps have 15% increased Area of Effect | inert (no stat read) |
| **Shifting Shadow** | base_dexterity 20; 10% Global chance to Blind Enemies on hit; 20% increased Evasion Rating | partial (3/3 stats read); conditional |
| **Shrieking Bolts** | 35% increased Projectile Damage; 10% chance to Taunt Enemies on Projectile Hit | partial (1/2 stats read); conditional |
| **Skeletal Atrophy** | Summoned Skeletons have 30% of Physical Damage Converted to Chaos Damage; Summoned Skeletons have 10% chance to Wither Enemies for 2 seconds on Hit | inert (no stat read) |
| **Skullbreaker** | +15% to Global Critical Strike Multiplier; 8% reduced Enemy Stun Threshold | works |
| **Sleepless Sentries** | 20% increased Totem Duration; You have Onslaught if you've Summoned a Totem Recently | inert (no stat read); conditional |
| **Smite the Weak** | Attacks have 10% chance to Maim on Hit; 40% increased Attack Damage against Maimed Enemies | inert (no stat read); conditional |
| **Smoking Remains** | 35% increased Fire Damage; 10% chance to create a Smoke Cloud on Kill | inert (no stat read); conditional |
| **Snaring Spirits** | 30% increased Totem Damage; Totems Hinder Enemies near them when Summoned | inert (no stat read); conditional |
| **Snowstorm** | Gain 8% of Lightning Damage as Extra Cold Damage against Chilled Enemies | inert (no stat read) |
| **Special Reserve** | Regenerate 120% of Life per second during any Flask Effect; 20% increased Damage during any Flask Effect | inert (no stat read); conditional |
| **Spiked Concoction** | 100% chance to gain Alchemist's Genius when you use a Flask; 5% increased Attack and Cast Speed during any Flask Effect | inert (no stat read); conditional |
| **Spring Back** | 10% increased maximum Energy Shield; 5% faster start of Energy Shield Recharge | works |
| **Volatile Presence** | Anger has 50% increased Mana Reservation Efficiency; 20% increased Duration of Fire Ailments | partial (1/2 stats read) |
| **Steady Torment** | +6% to Damage over Time Multiplier for Bleeding you inflict on Poisoned Enemies; +6% to Damage over Time Multiplier for Poison you inflict on Bleeding Enemies; 15% increased Bleeding Duration; 15% increased Poison Duration | inert (no stat read) |
| **Stoic Focus** | Channelling Skills deal 25% increased Damage; +5% Chance to Block Attack Damage while Channelling; +5% Chance to Block Spell Damage while Channelling | inert (no stat read); conditional |
| **Storm Drinker** | Damage Penetrates 8% Lightning Resistance; 50% of Lightning Damage Leeched as Energy Shield | partial (1/2 stats read); conditional |
| **Stormrider** | 10% chance to gain a Power Charge when you Shock a Chilled Enemy; 25% increased Cold Damage with Hits against Shocked Enemies; 25% increased Lightning Damage with Hits against Chilled Enemies | inert (no stat read); conditional |
| **Storm's Hand** | Gain 10% of Wand Physical Damage as Extra Lightning Damage; 25% of Wand Physical Damage converted to Lightning Damage | inert (no stat read) |
| **Streamlined** | 20% increased Projectile Damage; 20% increased Projectile Speed | works |
| **Strike Leader** | +4% Chance to Block Attack Damage; Attack Skills deal 30% increased Damage while holding a Shield; +2 metre to Melee Strike Range while Holding a Shield | partial (1/3 stats read); conditional |
| **Stubborn Student** | 15% increased maximum Mana; 20% increased Armour; +1 Armour per 10 Unreserved Maximum Mana | partial (2/3 stats read); conditional |
| **Student of Decay** | +13% to Chaos Resistance; 25% increased Damage over Time | partial (1/2 stats read) |
| **Sublime Form** | Grace has 50% increased Mana Reservation Efficiency; +10% to all Elemental Resistances | partial (1/2 stats read) |
| **Sublime Sensation** | 8% increased maximum Life; 10% increased maximum Energy Shield | works |
| **Mortifying Aspect** | Malevolence has 50% increased Mana Reservation Efficiency; +11% to Chaos Resistance | partial (1/2 stats read) |
| **Supercharge** | Lightning Damage with Non-Critical Strikes is Lucky | inert (no stat read); conditional |
| **Surefooted Striker** | 5% chance to deal Double Damage if you've dealt a Critical Strike with a Two Handed Melee Weapon Recently; 40% increased Critical Strike Chance with Two Handed Melee Weapons | partial (1/2 stats read); conditional |
| **Surging Vitality** | 8% increased maximum Life; Every 4 seconds, Regenerate 600% of Life over one second | partial (1/2 stats read); conditional |
| **Surprise Sabotage** | +8% to Critical Strike Multiplier with Traps; +8% to Critical Strike Multiplier with Mines; Trap Damage Penetrates 4% Elemental Resistances; Mine Damage Penetrates 4% Elemental Resistances | inert (no stat read) |
| **Tempered Arrowheads** | Bow Skills have +6% to Damage over Time Multiplier; Bow Skills have 10% increased Skill Effect Duration; 10% increased Duration of Ailments inflicted while wielding a Bow | inert (no stat read); conditional |
| **Tempt the Storm** | Conductivity can affect Hexproof Enemies; 30% increased Effect of Shocks you inflict on Cursed Enemies | inert (no stat read); conditional |
| **Thaumophage** | 60% of Spell Damage Leeched as Energy Shield; 25% increased Spell Damage while on Full Energy Shield; 10% chance to Hinder Enemies on Hit with Spells | inert (no stat read); conditional |
| **Thunderstruck** | 20% increased Lightning Damage; 30% increased Global Critical Strike Chance; Your Critical Strikes Knock Back Shocked Enemies | partial (2/3 stats read) |
| **Titanic Swings** | 15% increased Area of Effect while wielding a Two Handed Melee Weapon; 20% increased Area Damage while wielding a Two Handed Melee Weapon | inert (no stat read) |
| **Touch of Cruelty** | Chaos Spells have 10% chance to Hinder Enemies on Hit; Enemies Hindered by you take 10% increased Chaos Damage | inert (no stat read); conditional |
| **Towering Threat** | 10% increased Area of Effect; 8% increased maximum Life | works |
| **Uncompromising** | Determination has 50% increased Mana Reservation Efficiency; 20% increased Stun Threshold | partial (1/2 stats read) |
| **Unholy Grace** | 30% increased Chaos Damage; 10% increased Attack and Cast Speed | partial (1/2 stats read) |
| **Unspeakable Gifts** | Enemies you Kill have a 10% chance to Explode, dealing a quarter of their maximum Life as Chaos Damage | inert (no stat read); conditional |
| **Untouchable** | +10% chance to Suppress Spell Damage; 30% increased Evasion Rating | works |
| **Unwavering Focus** | Channelling Skills deal 30% increased Damage; 50% chance to Avoid being Stunned while Channelling | inert (no stat read); conditional |
| **Unwaveringly Evil** | 30% increased Chaos Damage; Chaos Skills ignore interruption from Stuns | inert (no stat read); conditional |
| **Vast Power** | 3% increased Area of Effect per Power Charge, up to a maximum of 50%; 20% increased Area Damage | partial (2/2 stats read); conditional |
| **Righteous Path** | Zealotry has 50% increased Mana Reservation Efficiency; 10% increased Effect of Consecrated Ground you create | inert (no stat read) |
| **Veteran Defender** | +15 to all Attributes; 60% increased Defences from Equipped Shield; +15% Elemental Resistances while holding a Shield | partial (1/3 stats read); conditional |
| **Vicious Bite** | Minions have +15% to Critical Strike Multiplier; Minions have 30% increased Critical Strike Chance | inert (no stat read) |
| **Vicious Guard** | +6% Chance to Block Attack Damage; Regenerate 90% of Life per second; 40% of Attack Damage Leeched as Life | partial (2/3 stats read); conditional |
| **Vicious Skewering** | 15% increased Effect of Impales inflicted by Hits that also inflict Bleeding; 10% chance to Impale Enemies on Hit with Attacks; Attacks cannot cause Bleeding | partial (2/3 stats read); conditional |
| **Victim Maker** | Curse Skills have 8% increased Cast Speed; Enemies Cursed by you have Malediction if 33% of Curse Duration expired | inert (no stat read) |
| **Vile Reinvigoration** | 24% increased Damage over Time; 6% increased maximum Energy Shield; Regenerate 120% of Energy Shield per second if you've Killed an Enemy Recently | partial (1/3 stats read); conditional |
| **Vital Focus** | Regenerate 90% of Life per second while Channelling; Channelling Skills deal 30% increased Damage | inert (no stat read); conditional |
| **Vivid Hues** | 20% increased Damage with Bleeding; 200% of Attack Damage Leeched as Life against Bleeding Enemies; 20% increased total Recovery per second from Life Leech | inert (no stat read); conditional |
| **Wall of Muscle** | 6% increased maximum Life; 5% increased Strength | works |
| **Forbidden Words** | 25% increased Area of Effect of Curse Aura Skills; 15% increased Mana Reservation Efficiency of Curse Aura Skills | inert (no stat read) |
| **Warning Call** | Gain 25% increased Armour per 5 Power for 8 seconds when you Warcry, up to a maximum of 100% | inert (no stat read); conditional |
| **Wasting Affliction** | 20% increased Damage with Ailments; Damaging Ailments deal damage 5% faster | inert (no stat read) |
| **Weight Advantage** | base_strength 20; Mace, Sceptre or Staff Attacks deal 30% increased Damage with Hits and Ailments; 4% chance to deal Double Damage while wielding a Mace, Sceptre or Staff | partial (1/3 stats read); conditional |
| **Evil Eye** | Enemies you Curse take 6% increased Damage | inert (no stat read); conditional |
| **Wicked Pall** | 30% increased Chaos Damage; 5% increased Skill Effect Duration | partial (1/2 stats read) |
| **Widespread Destruction** | 20% increased Elemental Damage; 6% increased Area of Effect | works |
| **Will Shaper** | Gain 5% of Maximum Mana as Extra Maximum Energy Shield | inert (no stat read) |
| **Wind-up** | 10% chance to gain a Power Charge on Non-Critical Strike with a Claw or Dagger; +15% to Critical Strike Multiplier with Claws or Daggers | partial (1/2 stats read); conditional |
| **Frantic Aspect** | Haste has 50% increased Mana Reservation Efficiency; Debuffs on you expire 10% faster | inert (no stat read) |
| **Winter Prowler** | +2% to maximum Cold Resistance; 10% increased Life Recovery from Flasks | works |
| **Wish for Death** | You have Culling Strike against Cursed Enemies | inert (no stat read) |
| **Wizardry** | +2% to maximum Lightning Resistance; 8% increased maximum Mana | works |
| **Wound Aggravation** | 20% increased Physical Damage with Axes or Swords; Axe or Sword Attacks deal 20% increased Damage with Ailments; +5% to Physical Damage over Time Multiplier while wielding an Axe or Sword | inert (no stat read); conditional |
| **Wrapped in Flame** | 20% increased Fire Damage; 5% chance to Cover Enemies in Ash on Hit while you are Burning; Cannot be Chilled while Burning | inert (no stat read); conditional |

## Cluster keystones (7)

The item can carry these keystones; the cluster keystone nodes and their effects are not modelled by the server.

| Keystone | Mod | Server |
|---|---|---|
| **Disciple Of Kitava** | `JewelExpansionKeystoneDiscipleOfKitava_` | not modelled (keystone node and effect) |
| **Lone Messenger** | `JewelExpansionLoneMessenger_` | not modelled (keystone node and effect) |
| **Natures Patience** | `JewelExpansionNaturesPatience` | not modelled (keystone node and effect) |
| **Secrets Of Suffering** | `JewelExpansionSecretsOfSuffering` | not modelled (keystone node and effect) |
| **Kineticism** | `JewelExpansionKineticism` | not modelled (keystone node and effect) |
| **Veterans Awareness** | `JewelExpansionVeteransAwareness_` | not modelled (keystone node and effect) |
| **Hollow Palm Technique** | `JewelExpansionHollowPalmTechnique` | not modelled (keystone node and effect) |

## Small-passive effect

`local_affliction_jewel_small_nodes_have_effect_+%` scales the theme of every small passive. The two current rolls (25% and 35%) work for a fully allocated cluster; notables are unaffected.
