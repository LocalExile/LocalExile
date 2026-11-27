# Uniques with open lines

Made on 2026-11-25 from `fx_stats` and each unique's mods: 863 of 1364 uniques have every item effect handled;
the 501 below have 921 open lines between them. A line is open when its stat has `handled = none` in
`fx_stats`; that row's `why` says what is missing. To make the list again, take each unique's mods, their stats, and
keep the stats with `handled = none`. The plan for working through it is in
[items and crafting](items-and-crafting.md) ("Effect rules, next"); how to build a line is in the
[guide](../docs/dev/effect-rules-guide.md).

Each unique is listed under its hardest open line. After a line: [e] easy, [m] medium, [h] hard, [x] needs a whole
mechanic of its own.

## Why lines are open

| Reason | Lines |
|---|---|
| A number or flag nothing reads yet | 215 |
| A property of the item itself; needs the item code to honour it | 214 |
| An effect on an event: needs the event or the effect as a part | 122 |
| A condition on a number: needs the condition and the plain number to be read | 105 |
| Changes how a skill behaves; needs its own code | 99 |
| Needs minions or totems to carry rules of their own | 78 |
| Not classified; needs a look | 67 |
| Internal value with no line of its own; needs the mechanic it belongs to | 21 |

## Easy: the parts exist, a row or a small reader finishes it (42 uniques)

- **Ascent From Flesh**: You have Phasing if Energy Shield Recharge has started Recently [e]
- **Bear's Girdle**: Nearby Enemies are Crushed while you have at least {0} Rage [e]
- **Blasphemer's Grasp**: Remove an Ailment when you use a Flask if all Equipped Items are Elder Items [e]
- **Bloodseeker**: {0}% increased Maximum Recovery per Life Leech for each 5% of Life Reserved [e]
- **Brinerot Flag**: Warcries grant Arcane Surge to you and Allies, with {0}% increased effect per 5 power, up to 50%
  [e]
- **Cerberus Limb**: {0:+d} to Maximum Energy Shield per 5 Armour on Equipped Shield [e]; {0:+d} to Armour per 5
  Evasion Rating on Equipped Shield [e]; {0:+d} to Evasion Rating per 5 Maximum Energy Shield on Equipped Shield [e]
- **Chain of Endurance**: Regenerate {0}% of Life per second for each different Ailment affecting you [e]
- **Chill of Corruption**: {0}% chance to gain an additional Vaal Soul per Enemy Shattered [e]
- **Deidbellow**: If you've Warcried Recently, you and nearby allies have {0}% increased Attack, Cast and Movement
  Speed [e]
- **Doon Cuebiyari**: 1% increased Damage per {0} Strength when in Main Hand [e]; 1% increased Armour per {0} Strength
  when in Off Hand [e]
- **Esh's Mirror**: Adds {0} to {1} Lightning Damage for each Shocked Enemy you've Killed Recently [e]; Adds {0} to
  {1} Lightning Damage for each Shocked Enemy you've Killed Recently [e]
- **Forbidden Flame**: Allocates {0} if you have the matching modifier on Forbidden Flesh [e]
- **Forbidden Flesh**: Allocates {0} if you have the matching modifier on Forbidden Flame [e]
- **Gloomfang**: Projectiles that have Chained gain {0}% of Non-Chaos Damage as extra Chaos Damage [e]
- **Hand of Thought and Motion**: Adds {0} to {1} Lightning Damage to Attacks with this Weapon per 10 Dexterity [e];
  Adds {0} to {1} Lightning Damage to Attacks with this Weapon per 10 Dexterity [e]
- **Heatshiver**: Gain {0}% of Cold Damage as Extra Fire Damage per 1% Chill Effect on Enemy [e]; Gain {0}% of Cold
  Damage as Extra Fire Damage against Frozen Enemies [e]
- **Iron Commander**: Siege Ballista has {0:+d} to maximum number of Summoned Totems per 200 Dexterity [e]
- **Kitava's Hunger**: {:+d} to maximum Life if there are no Life Modifiers on other Equipped Items [e]
- **Lavianga's Spirit**: Skills Cost no Mana during Effect [e]
- **Leadership's Price**: You have Scorching Conflux, Brittle Conflux and Sapping Conflux while your two highest
  Attributes are equal [e]
- **Lightpoacher**: Trigger Level {0} Spirit Burst when you Use a Skill while you have a Spirit Charge [e]
- **Malachai's Loop**: Shocks you when you reach Maximum Power Charges [e]
- **Mantra of Flames**: Adds {0} to {1} Fire Attack Damage per Buff on you [e]; Adds {0} to {1} Fire Attack Damage per
  Buff on you [e]; Adds {0} to {1} Fire Spell Damage per Buff on you [e]; Adds {0} to {1} Fire Spell Damage per Buff
  on you [e]
- **Might and Influence**: With at least 40 Dexterity in Radius, Dual Strike has {0}% increased / Accuracy Rating
  while wielding a Sword [e]
- **Omeyocan**: {0}% increased Evasion Rating per 500 Maximum Mana [e]; Lose {0}% of Mana per Second [e]
- **Replica Eternity Shroud**: Gain {0}% of Physical Damage as Extra Chaos Damage per Elder Item Equipped [e]
- **Replica Heatshiver**: Gain {0}% of Lightning Damage as Extra Cold Damage per 2% Shock Effect on Enemy [e]
- **Replica Iron Commander**: Shrapnel Ballista has +{0} to maximum number of Summoned Totems per 200 Strength [e]
- **Replica Maloney's Mechanism**: {0}% chance to Trigger a Socketed Bow Skill when you Cast a Spell while wielding a
  Bow, with a 1 second Cooldown [e]
- **Replica Red Trail**: {0}% chance for Spell Hits against you to inflict Poison [e]
- **Replica Tulfall**: Take {0} Cold Damage on reaching Maximum Power Charges [e]
- **Rotmother's Mutiny**: {0:+d}% to Chaos Resistance per 1% Lightning Resistance [e]
- **Survival Secrets**: Flasks gain {0} Charges every 3 seconds while they are inactive [e]
- **Tainted Pact**: Taking Chaos Damage over Time heals you instead while Leeching Life [e]
- **Temptation Step**: Poison you inflict with Travel Skills is Reflected to you if you / have fewer than 5 Poisons on
  you [e]
- **The Baron**: With at least 1000 Strength, {0}% of Damage dealt by your Raised Zombies is Leeched to you as Life
  [e]
- **The Caged Mammoth**: Lose {0} Rage per second [e]; {0}% less Damage taken per 5 Rage, up to a maximum of 30% [e]
- **The Eternal Struggle**: Critical Strikes have {0}% chance to inflict Malignant Madness if The Eater of Worlds is
  dominant [e]; Kill Enemies that have 15% or lower Life on Hit if The Searing Exarch is dominant [e]
- **The Grey Wind**: Nearby Enemies have Fire Exposure while at maximum Rage [e]
- **The Tempest Rising**: You and Enemies in your Presence count as moving while affected by Elemental Ailments [e]
- **Twyzel**: Attacks fire {0} additional Projectile when in Off Hand [e]; Attacks have {0}% increased Area of Effect
  when in Main Hand [e]
- **Vixen's Entrapment**: {0}% of Spell Damage Leeched as Energy Shield for each Curse on Enemy [e]

## Medium: one new condition, reader or part (318 uniques)

- **Abberath's Hooves**: When you Kill an Ignited Enemy, inflict an equivalent Ignite on each nearby Enemy [m]
- **Abhorrent Interrogation**: Enemies take {0}% increased Elemental Damage from your Hits for / each Withered you
  have inflicted on them [m]
- **Akoya's Gaze**: Life Leech from Exerted Attacks is instant [m]; Non-Exerted Attacks deal no Damage [m]
- **Algor Mortis**: {0}% chance to Sap Enemies in Chilling Areas [m]; Enemies in your Chilling Areas take {0}%
  increased Lightning Damage [m]
- **Ancestral Vision**: Modifiers to Chance to Suppress Spell Damage also apply to Chance to Avoid Elemental Ailments
  at {0}% of their Value [m]
- **Apep's Slumber**: {0:+d}% chance to be Poisoned [m]; Poisons on you expire {0}% faster [m]
- **Apep's Supremacy**: You take Chaos Damage instead of Physical Damage from Bleeding [m]; {0:+d}% chance to be
  Poisoned [m]; Poisons on you expire {0}% faster [m]
- **Apex Mode**: Spells which have gained Intensity Recently gain 1 Intensity every {0} Seconds [e]; Spells have {0}%
  increased Critical Strike Chance per Intensity [m]
- **Araku Tiki**: Gain Elusive on reaching Low Life [m]
- **Architect's Hand**: {0}% chance to throw up to 4 additional Traps [m]; Increases and Reductions to Cast Speed also
  Apply to Trap Throwing Speed [m]
- **Assailum**: Grants Level {0} Snipe Skill [m]
- **Astral Projector**: Nova Spells have {0}% more Area of Effect [m]
- **Atziri's Reign**: Vaal Skills have {0}% chance to regain consumed Souls when used [m]
- **Atziri's Rule**: Queen's Demand can Trigger Level {0} Flames of Judgement [m]; Queen's Demand can Trigger Level
  {0} Storm of Judgement [m]
- **Augyre**: You have Resolute Technique while you do not have Elemental Overload [e]; {0}% increased Physical Damage
  while you have Resolute Technique [e]; Every 16 seconds you gain Elemental Overload for 8 seconds [m]
- **Azadi Crest**: Your Lucky or Unlucky effects use the best or / worst from three rolls instead of two [m]
- **Balefire**: {0}% increased Scorching Ray beam length [m]
- **Berek's Respite**: When you Kill an Ignited Enemy, inflict an equivalent Ignite on each nearby Enemy [m]
- **Betrayal's Sting**: {0}% chance to inflict an additional Poison on the same Target when you inflict Poison [m]
- **Bisco's Collar**: {0}% increased Rarity of Items Dropped by Slain Magic Enemies [m]; {0}% increased Quantity of
  Items Dropped by Slain Normal Enemies [m]
- **Bitter Instinct**: Damaging Retaliation Skills become Usable every {0} seconds [m]
- **Blackflame**: Enemies Ignited by you take Chaos Damage instead of Fire Damage from Ignite [m]; Withered does not
  expire on Enemies Ignited by you [m]
- **Blood Price**: Nearby Enemy Monsters have at least {0}% of Life Reserved [m]
- **Blood of the Karui**: Recover {0}% of Life at the end of the Effect [m]
- **Bloodsoaked Medallion**: Every 10 seconds: / Gain 2% of Life per Enemy Hit with Attacks for 5 seconds / Gain 5% of
  Life per Enemy Killed for 5 seconds [m]
- **Bound Fate**: Every 5 seconds, gain one of the following for 5 seconds: / Your Hits are always Critical Strikes /
  Hits against you are always Critical Strikes / Attacks cannot Hit you / Attacks against you always Hit / Your Damage
  with Hits is Lucky / Damage of Hits against you is Lucky [m]
- **Brain Rattler**: Enemies you Shock have {0}% increased Cast Speed [m]; Enemies you Shock have {0}% increased
  Movement Speed [m]; Hits with this Weapon Shock Enemies as though dealing {0}% more Damage [m]
- **Bronn's Lithe**: {0}% increased Damage with Movement Skills [m]
- **Bubonic Trail**: Triggers Level {0} Death Walk when Equipped [m]
- **Calamitous Visions**: Adds Lone Messenger [m]
- **Call of the Void**: Enemies Chilled by your Hits can be Shattered as though Frozen [m]
- **Callinellus Malleus**: Cannot Knock Enemies Back [m]; All Attack Damage Chills when you Stun [m]
- **Cameria's Avarice**: {0}% increased Rarity of Items Dropped by Frozen Enemies [m]
- **Cameria's Maul**: {0}% increased Rarity of Items Dropped by Frozen Enemies [m]
- **Cane of Unravelling**: Gain a Power Charge after Spending a total of 200 Mana [m]
- **Ceaseless Feast**: {0}% chance to inflict Corrosion on Hit with Attacks [m]; {0}% chance to gain an Endurance
  Charge on Hitting an Enemy with no Armour [m]; {0}% chance to gain a Frenzy Charge on Hitting an Enemy with no
  Evasion Rating [m]
- **Chalice of Horrors**: Inflict Decay on Enemies you Curse with Hex Skills, dealing {0} Chaos Damage per Second for
  8 Seconds [e]; {0}% Chance to Cause Monster to Flee on Block [m]; {0:+d}% Chance to Block Attack Damage from Cursed
  Enemies [m]
- **Chin Sol**: {0}% more Damage with Arrow Hits at Close Range [m]; Bow Knockback at Close Range [m]
- **Circle of Ambition**: Herald of Thunder has {0}% increased Mana Reservation Efficiency [m]
- **Coated Shrapnel**: Traps and Mines have a {0}% chance to Poison on Hit [m]
- **Collateral Damage**: With at least 40 Dexterity in Radius, Galvanic Arrow deals {0}% increased Area Damage [m]
- **Combat Focus**: With 40 total Dexterity and Strength in Radius, Prismatic Skills cannot choose Lightning [m]; With
  40 total Dexterity and Strength in Radius, Prismatic Skills deal 50% less Lightning Damage [m]; With 40 total
  Intelligence and Dexterity in Radius, Prismatic Skills cannot choose Fire [m]; With 40 total Intelligence and
  Dexterity in Radius, Prismatic Skills deal 50% less Fire Damage [m]; With 40 total Strength and Intelligence in
  Radius, Prismatic Skills cannot choose Cold [m]; With 40 total Strength and Intelligence in Radius, Prismatic Skills
  deal 50% less Cold Damage [m]
- **Command of the Pit**: With a Hypnotic Eye Jewel Socketed, gain Arcane Surge on Hit with Spells [m]
- **Corona Solaris**: Adds {0} to {1} Fire Damage to Hits with this Weapon against Blinded Enemies [e]; Adds {0} to
  {1} Fire Damage to Hits with this Weapon against Blinded Enemies [e]; Increases and Reductions to Light Radius also
  apply to Accuracy [m]
- **Corpsewalker**: For each nearby corpse, Regenerate {0} Life per second [e]; Triggers Level {0} Corpse Walk when
  Equipped [m]; For each nearby corpse, {0}% increased Movement Speed [m]
- **Corrupted Energy**: With {0} Corrupted Items Equipped: 50% of Chaos Damage taken does not bypass Energy Shield,
  and 50% of Physical Damage taken bypasses Energy Shield [m]
- **Coruscating Elixir**: Removes all but one Life on use / Removed life is Regenerated as Energy Shield over {1:d}
  seconds [m]; Removes all but one Life on use / Removed life is Regenerated as Energy Shield over {1:d} seconds [m]
- **Cowl of the Ceraunophile**: Can have a second Enchantment Modifier [m]; Lightning Resistance is {0}% [m]
- **Cowl of the Cryophile**: Can have a second Enchantment Modifier [m]; Cold Resistance is {0}% [m]
- **Cowl of the Thermophile**: Can have a second Enchantment Modifier [m]; Fire Resistance is {0}% [m]
- **Crown of the Inward Eye**: {0}% increased maximum Life, Mana and Global Energy Shield [m]; Transfiguration of Soul
  [m]; Transfiguration of Body [m]
- **Crown of the Pale King**: {0}% of Damage you Reflect to Enemies when Hit is leeched as Life [m]
- **Crown of the Tyrant**: {0:+d}% to All Resistances [m]; You and Nearby Allies have {0} to {1} added Fire Damage per
  Red Socket [m]; You and Nearby Allies have {0} to {1} added Fire Damage per Red Socket [m]; You and Nearby Allies
  have {0} to {1} added Cold Damage per Green Socket [m]; You and Nearby Allies have {0} to {1} added Cold Damage per
  Green Socket [m]; You and Nearby Allies have {0} to {1} added Lightning Damage per Blue Socket [m]; You and Nearby
  Allies have {0} to {1} added Lightning Damage per Blue Socket [m]; You and Nearby Allies have {0} to {1} added Chaos
  Damage per White Socket [m]; You and Nearby Allies have {0} to {1} added Chaos Damage per White Socket [m]
- **Crystallised Omniscience**: +{0}% to all Elemental Resistances per 15 Omniscience [e]; Penetrate {0}% Elemental
  Resistances per 15 Omniscience [e]; Modifiers to Attributes instead apply to Omniscience [m]
- **Darkscorn**: {0}% chance for Poisons inflicted with this Weapon to deal 300% more Damage [m]
- **Dawnbreaker**: {0:+d}% Chance to Block Attack Damage for every 200 Fire Damage taken from Hits Recently [m];
  Scorch Enemies in Close Range when you Block [m]
- **Death's Oath**: You take {1} Chaos Damage per second for {0} seconds on Kill [m]; You take {1} Chaos Damage per
  second for {0} seconds on Kill [m]
- **Debeon's Dirge**: Warcries Knock Back and Interrupt Enemies in a smaller Area [m]
- **Deerstalker**: 30% increased Movement Speed for {0} seconds on Throwing a Trap [m]; 30% increased Movement Speed
  for {0} seconds on Throwing a Trap [m]
- **Deidbell**: Skills which Exert an Attack have {0}% chance to not count that Attack [m]
- **Demon Stitcher**: Sacrifice {0}% of Life to gain that much Energy Shield when you Cast a Spell [m]
- **Dialla's Malefaction**: Gems Socketed always have the Quality bonus from Socket Colour [m]
- **Dissolution of the Flesh**: Removes all Energy Shield [m]; Life that would be lost by taking Damage is instead
  Reserved / until you take no Damage to Life for {0} second [m]; {0}% more Maximum Life [m]
- **Divide and Conquer**: {0:+d}% to Off Hand Critical Strike Multiplier per 10 Maximum Energy Shield on Shield [m]
- **Divine Inferno**: With at least 40 Strength in Radius, Attacks Exerted by Infernal Cry deal {0}% more Damage with
  Ignite [m]; With at least 40 Strength in Radius, Combust is Disabled [m]
- **Doedre's Elixir**: Removes {0}% of your maximum Energy Shield on use [m]; You take {0}% of your maximum Life as
  Chaos Damage on use [m]
- **Doedre's Tongue**: Cursed Enemies cannot inflict Elemental Ailments on You [m]
- **Doryani's Prototype**: Armour also applies to Lightning Damage taken from Hits [m]; Lightning Resistance does not
  affect Lightning Damage taken [m]
- **Dyadus**: Chill Enemies for {0} second on Hit with this Weapon when in Off Hand [e]; Adds {0} to {1} Fire Damage
  in Main Hand [m]; Adds {0} to {1} Fire Damage in Main Hand [m]; Adds {0} to {1} Cold Damage in Off Hand [m]; Adds
  {0} to {1} Cold Damage in Off Hand [m]; {0}% chance to Ignite when in Main Hand [m]; {0}% increased Damage with
  Ignite inflicted on Chilled Enemies [m]
- **Dying Breath**: Nearby Enemies have {0}% increased Effect of Curses on them [m]
- **Eber's Unification**: Trigger Level {0} Void Gaze when you use a Skill [m]
- **Echoes of Creation**: When you Attack, take {0}% of Life as Physical Damage for / each Warcry Exerting the Attack
  [m]; Skills deal {0}% more Damage for each Warcry Exerting them [m]
- **El'Abin's Visage**: Has a Crucible Passive Skill Tree / Crucible Passive Skill Tree is removed if this Modifier is
  removed [m]
- **Elixir of the Unbroken Circle**: Recover {0}% of Life per Endurance Charge on use / Lose all Endurance Charges on
  use [m]
- **Emberwake**: {0}% more Burning Damage [m]
- **Emperor's Vigilance**: Damage taken from Blocked Hits cannot bypass Energy Shield / Damage taken from Unblocked
  hits always bypasses Energy Shield [m]
- **Endless Misery**: With at least 40 Intelligence in Radius, Discharge Cooldown is {0} ms [m]; With at least 40
  Intelligence in Radius, Discharge deals {0}% more Damage [m]; With at least 40 Intelligence in Radius, Discharge has
  {0}% more Area of Effect [m]
- **Enmity's Embrace**: Take {0} Fire Damage when you Use a Skill [m]; Damage Penetrates Fire Resistance equal to your
  Overcapped Fire Resistance, up to a maximum of 200% [m]
- **Esh's Visage**: Reflect Shocks applied to you to all Nearby Enemies [m]
- **Eternal Damnation**: Gain additional Elemental Damage Reduction equal to half your Chaos Resistance [m]
- **Expedition's End**: Freeze Chilled Enemies as though dealing {0}% more Damage [m]; Nearby Enemies are Chilled [m]
- **Eye of Innocence**: Take {0} Fire Damage when you Ignite an Enemy [m]
- **Eye of Malice**: Nearby Enemies have {0}% increased Fire and Cold Resistances [m]
- **Fairgraves' Tricorne**: You can be Touched by Tormented Spirits [m]
- **Faithguard**: Increases and Reductions to Maximum Energy Shield instead apply to Ward [m]
- **Faminebind**: Deals {0} Chaos Damage per second to nearby Enemies [m]
- **Farrul's Chase**: Trigger Level {0} Intimidating Cry when you lose Cat's Stealth [m]
- **Fate of the Vaal**: Hits with this Weapon always Ignite, Freeze, and Shock [m]
- **Fated End**: Non-Aura Hexes gain 20% increased Effect per second [e]; Non-Aura Hexes expire upon reaching {1}% of
  base Effect [m]
- **Fight for Survival**: With at least 40 Dexterity in Radius, Melee Damage / dealt by Frost Blades Penetrates {0}%
  Cold Resistance [m]
- **First Snow**: With at least 40 Intelligence in Radius, {0}% increased Freezing Pulse Damage if / you've Shattered
  an Enemy Recently [e]; With at least 40 Intelligence in Radius, Freezing Pulse fires an additional Projectile [m]
- **Fleshrender**: Cannot Poison Enemies with at least {0} Poison on them [m]; Wither on Hit with this weapon against
  Enemies with at least {0} Poison on them [m]
- **Forbidden Shako**: Socketed Gems are Supported by Level {0} {1} [m]; Socketed Gems are Supported by Level {0} {1}
  [m]
- **Fragile Bloom**: {0}% of Life Regenerated per second per Fragile Regrowth [e]; Maximum {0} Fragile Regrowth [m];
  Lose all Fragile Regrowth when Hit [m]; Gain {0} Fragile Regrowth each second [m]
- **Frozen Trail**: With at least 40 Intelligence in Radius, Frostbolt Projectiles gain {0}% increased Projectile
  Speed per second [m]; With at least 40 Intelligence in Radius, Frostbolt fires an additional Projectile [m]
- **Gamblesprint**: When Hit, gain a random Movement Speed modifier from {0}% reduced to {1}% increased, until Hit
  again [m]; When Hit, gain a random Movement Speed modifier from {0}% reduced to {1}% increased, until Hit again [m]
- **Garb of the Ephemeral**: {0:+d} Strength and Intelligence Requirement [m]; {0}% chance to gain a Divine Charge on
  Hit [m]; {0:+d} to maximum Divine Charges [m]; You gain Divinity for {0} seconds on reaching maximum Divine Charges
  / Lose all Divine Charges when you gain Divinity [m]; Nearby Allies' Action Speed cannot be modified to below Base
  Value [m]; Nearby Enemies cannot deal Critical Strikes [m]
- **Geofri's Devotion**: Trigger Level {0} Elemental Warding on Melee Hit while Cursed [m]
- **Gifts from Above**: {0}% increased Rarity of Items Dropped by Enemies killed with a Critical Strike [m]
- **Glimpse of Chaos**: {0}% increased maximum Life and reduced Fire Resistance [m]; {0}% increased maximum Mana and
  reduced Cold Resistance [m]; {0}% increased Global maximum Energy Shield and reduced Lightning Resistance [m]; Chaos
  Resistance is Zero [m]
- **Gluttony**: Take {0} Physical Damage when you use a Movement Skill [m]; You have no Armour or Maximum Energy
  Shield [m]
- **Gorgon's Gaze**: Grants Level {0} Petrification Statue Skill [m]
- **Gravebind**: Nearby Enemies Killed by anyone count as being Killed by you instead [m]
- **Great Old One's Tentacles**: {0}% chance for Impales on Enemies you Kill to Reflect Damage to surrounding Enemies
  [m]
- **Growing Agony**: With at least 40 Dexterity in Radius, Viper Strike deals {0}% increased Damage with Hits and
  Poison for each Poison on the Enemy [m]; With at least 40 Dexterity in Radius, Viper Strike has a {0}% chance per
  Poison on Enemy to grant Unholy Might for 4 seconds on Hit [m]
- **Hale Negator**: Gain a Spirit Charge every second [m]; You lose all Spirit Charges when taking a Savage Hit [m];
  Recover {0}% of Life when you lose a Spirit Charge [m]; Recover {0}% of Energy Shield when you lose a Spirit Charge
  [m]
- **Hand of Heresy**: Excommunicate Enemies on Melee Hit for {0} second [m]; Melee Attacks have {0:+d}% to Critical
  Strike Chance against Excommunicated Enemies [m]
- **Hands of the High Templar**: Can be modified while Corrupted [e]; Can have a up to 1 Implicit Modifier while Item
  has this Modifier [m]
- **Haunting Memories**: {0}% of Suppressed Spell Damage taken bypasses Energy Shield [m]; {0}% of Suppressed Spell
  Damage taken Recouped as Energy Shield [m]
- **Hazardous Research**: With at least 40 Intelligence in Radius, Spark fires an additional Projectile [m]
- **Hiltless**: Enemies you Attack Reflect {0} Physical Damage to you [m]
- **Hungry Abyss**: With {0} Corrupted Items Equipped: Life Leech recovers based on your Chaos Damage instead [m]
- **Hyrri's Truth**: Bow Attacks have Culling Strike [m]; Precision has 100% increased Mana Reservation Efficiency [m]
- **Ichimonji**: Allies' Aura Buffs do not affect you [m]; Your Aura Buffs do not affect allies [m]
- **Impresence**: Despair has no Reservation if Cast as an Aura [e]; Gain Maddening Presence for 10 seconds when you
  Kill a Rare or Unique Enemy [m]
- **Incandescent Heart**: Light Radius is based on Energy Shield instead of Life [m]
- **Inevitability**: With at least 40 Intelligence in Radius, Rolling Magma deals {0}% more Damage per Chain [m]; With
  at least 40 Intelligence in Radius, Rolling Magma deals {0}% more Damage [m]; With at least 40 Intelligence in
  Radius, Rolling Magma / has {0}% increased Area of Effect per Chain [m]
- **Inextricable Fate**: All Damage inflicts Poison against Enemies affected by at least {0} Grasping Vine [e]; You
  gain a Grasping Vine when you take a Critical Strike [m]; Nearby stationary Enemies gain a Grasping Vine every
  second [m]; You take {0}% increased Extra Damage from Critical Strikes by Poisoned Enemies [m]
- **Ixchel's Temptation**: Maximum Quality is {0}% [m]
- **Kahuturoa's Certainty**: Unaffected by Damaging Ailments [m]
- **Kalisa's Grace**: Gain {0:+d}% to Critical Strike Chance for 2 seconds after Spending a total of 800 Mana [m]
- **Kaom's Binding**: Take no Burning Damage if you've stopped taking Burning Damage Recently [e]; Nearby Enemies
  Convert {0}% of their Physical Damage to Fire [m]
- **Kaom's Spirit**: Regenerate 1 Rage per second for every {0} Life Recovery per second from Regeneration / Does not
  delay Inherent Loss of Rage [e]; Life Recovery from Regeneration is not applied [m]
- **Khatal's Geyser**: Increases and Reductions to Effect of Flasks applied to you / also applies to Effect of Arcane
  Surge on you [m]
- **Khatal's Weeping**: Can't use Mana Flasks [m]; On non-channelling Attack, set a Life Flask with greater than 50%
  of maximum Charges remaining to 50% / For each Charge removed this way, that Attack gains {0:+d}% to Damage over
  time Multiplier [m]
- **Kiara's Determination**: Immunity to Freeze, Chill, Curses and Stuns during Effect [m]
- **Kitava's Feast**: Enemies Killed by your Hits are destroyed [m]
- **Kitava's Teachings**: Adds Disciple of Kitava [m]
- **Kongming's Stratagem**: {0}% increased Fire Damage with Hits and Ailments against Blinded Enemies [e]; Trigger
  Level {0} Fog of War when your Trap is triggered [m]; {0}% increased Spell Damage taken from Blinded Enemies [m]
- **Lash of Retribution**: All Hits with your next Non-Channelling Attack within {0} second of taking a Critical
  Strike will be Critical Strikes [m]
- **Law of the Wilds**: {0}% chance to Trigger Level 25 Summon Spectral Wolf on Critical Strike with this Weapon [m]
- **Legacy of Fury**: Nearby Enemies are Scorched [m]; {0}% chance when you Kill a Scorched Enemy to Burn Each
  surrounding / Enemy for 4 seconds, dealing 8% of the Killed Enemy's Life as Fire Damage per second [m]
- **Leper's Alms**: Shared Suffering [m]
- **Lioneye's Glare**: Attacks you use yourself Repeat an additional time [m]; Attacks you use yourself have {0}% more
  Attack Speed [m]
- **Lycosidae**: Adds {0} to {1} Cold Damage to Retaliation Skills [m]; Adds {0} to {1} Cold Damage to Retaliation
  Skills [m]
- **Mageblood**: Magic Utility Flask Effects cannot be removed [m]
- **Mahuxotl's Machination**: Everlasting Sacrifice [m]
- **Malachai's Mark**: Grants Malachai's Endurance, Frenzy and Power for 6 seconds each, in sequence [m]
- **Malicious Intent**: {0}% chance to Gain Unholy Might for 4 seconds on Melee Kill [m]
- **Maligaro's Restraint**: Shocks you cause are reflected back to you [m]
- **Manastorm**: When you Cast a Spell, Sacrifice all Mana to gain Added Maximum Lightning Damage / equal to {0}% of
  Sacrificed Mana for 4 seconds [m]
- **Mark of the Doubting Knight**: {0}% chance to Cause Poison on Critical Strike [m]
- **Mark of the Shaper**: {0}% chance to Trigger Level 20 Summon Volatile Anomaly on Kill [m]
- **Mask of the Spirit Drinker**: Immortal Ambition [m]
- **Mask of the Stitched Demon**: Immortal Ambition [m]
- **Mask of the Tribunal**: Nearby Allies have {0}% increased Cast Speed per 100 Intelligence you have [e]; {0}%
  increased Mana Reservation Efficiency of Skills per 250 total Attributes [e]; Nearby Allies have {0}% Chance to
  Block Attack Damage per 100 Strength you have [m]; Nearby Allies have {0:+d}% to Critical Strike Multiplier per 100
  Dexterity you have [m]
- **Meginord's Vise**: Melee Hits with Strike Skills always Knockback [m]
- **Memory Vault**: {0}% increased Armour per 50 Reserved Mana [e]; {0}% reduced Reservation Efficiency of Skills [m]
- **Mercenary's Lot**: {0}% of Damage from Hits is taken from Marked Target's Life before you [m]
- **Midnight Bargain**: Cannot be used with Chaos Inoculation [m]
- **Mjölner**: Trigger a Socketed Lightning Spell on Hit, with a 0.25 second Cooldown / Socketed Lightning Spells have
  no Cost if Triggered [m]
- **Mokou's Embrace**: All Damage Taken from Hits can Ignite you [m]
- **Moonbender's Wing**: Trigger Level {0} Lightning Warp on Hit with this Weapon [m];  [m];  [m]; Hits with this
  Weapon gain {0}% of Physical Damage as Extra Cold or Lightning Damage [m]
- **Nadir Mode**: Spells which have gained Intensity Recently lose 1 Intensity every {0} Seconds [e]; Spells have {0}%
  increased Critical Strike Chance per Intensity [m]
- **Natural Affinity**: Adds Nature's Patience [m]
- **Nightgrip**: Gain Added Chaos Damage equal to {0}% of Ward [m]
- **Omen on the Winds**: With at least 40 Dexterity in Radius, Ice Shot has {0}% increased Area of Effect [m]; With at
  least 40 Dexterity in Radius, Ice Shot Pierces an additional Target [m]
- **One With Nothing**: Adds Hollow Palm Technique [m]
- **Oni-Goroshi**: Uses both hand slots [m]
- **Orbala's Stand**: Debilitate Enemies for {0} Seconds when you Suppress their Spell Damage [m]
- **Original Sin**: All Elemental Damage Converted to Chaos Damage [m]; Nearby Enemies' Chaos Resistance is 0 [m];
  Chaos Resistance is Zero [m]
- **Overwhelming Odds**: With at least 40 Strength in Radius, Hits with Cleave Fortify [m]; With at least 40 Strength
  in Radius, Cleave has +0.1 metres to Radius per Nearby / Enemy, up to a maximum of +1 metre [m]
- **Pearl of Tsoatha**: Skills Socketed in your Helmet are Supported by level {2} {1} [m]; Skills Socketed in your
  Helmet are Supported by level {2} {1} [m]; Skills Socketed in your Helmet are Supported by level {2} {1} [m]; Skills
  Socketed in your Helmet are Supported by level {2} {1} [m]; Skills Socketed in your Helmet are Supported by level
  {2} {1} [m]; Skills Socketed in your Helmet are Supported by level {2} {1} [m]
- **Perandus Signet**: {0}% chance for Slain monsters to drop an additional Scroll of Wisdom [m]
- **Perquil's Toe**: Lightning Damage of Enemies Hitting you is Lucky [m]; Nearby Allies' Damage with Hits is Lucky
  [m]
- **Plume of Pursuit**: Non-critical strikes deal {0}% more Damage [m]; Spell Skills always deal Critical Strikes on
  final Repeat [m]; Spell Skills cannot deal Critical Strikes except on final Repeat [m]
- **Polaric Devastation**: Left Ring slot: Cover Enemies in Ash for {0} seconds when you Ignite them [m]; Right Ring
  slot: Cover Enemies in Frost for {0} seconds when you Freeze them [m]
- **Progenesis**: When Hit during effect, {0}% of Life loss from Damage taken occurs over 4 seconds instead [m]
- **Pyre**: Ignited Enemies Killed by your Hits are destroyed [m]
- **Pyroshock Clasp**: Enemies Shocked by you have {0}% of Physical Damage they deal converted to Lightning [m]
- **Queen of the Forest**: {0:+d} Physical Damage taken from Hits by Animals [m]
- **Rakiata's Dance**: Treats Enemy Monster Elemental Resistance values as inverted [m]
- **Rapid Expansion**: With at least 40 Strength in Radius, Ground Slam has a {0}% chance / to grant an Endurance
  Charge when you Stun an Enemy [m]
- **Ravenous Passion**: Rage grants Spell Damage instead of Attack Damage [m]
- **Reckless Defence**: Hits have {0}% increased Critical Strike Chance against you [m]
- **Refuge in Isolation**: Physical Damage of Enemies Hitting you is Lucky [m]
- **Relentless Fury**: You gain Onslaught for {0} seconds on Culling Strike [m]
- **Replica Allure**: Gain {0:+d} Life when you Taunt an Enemy [m]; You gain Onslaught for {0} seconds on Killing
  Taunted Enemies [m]
- **Replica Blood Thorn**: Curse Enemies with Flammability on Block [m]
- **Replica Dragonfang's Flight**: +{0} to Level of all {1} Gems [m]; +{0} to Level of all {1} Gems [m]
- **Replica Forbidden Shako**: Socketed Gems are Supported by Level {0} {1} [m]; Socketed Gems are Supported by Level
  {0} {1} [m]
- **Replica Fragile Bloom**: {0}% of Life Regenerated per second per Fragile Regrowth [e]; Maximum {0} Fragile
  Regrowth [m]; Gain up to maximum Fragile Regrowth when Hit [m]; Gain {0} Fragile Regrowth each second [m]
- **Replica Hyrri's Truth**: Bow Attacks have Culling Strike [m]; Hatred has {0}% increased Mana Reservation
  Efficiency [m]
- **Replica Leer Cast**: You and nearby Allies have 30% increased Mana Regeneration Rate [m]
- **Replica Midnight Bargain**: Cannot be used with Chaos Inoculation [m]
- **Replica Reckless Defence**: {0:+d}% chance to be Frozen, Shocked and Ignited [m]
- **Replica Rumi's Concoction**: Petrified during Effect [m]
- **Replica Sorrow of the Divine**: Life Recovery from Flasks also applies to Energy Shield during Effect [m]
- **Replica Unstable Payload**: Recover {0} Life when your Trap is triggered by an Enemy [m]
- **Replica Veil of the Night**: Defences are Zero [m]
- **Replica Wings of Entropy**: Counts as Dual Wielding [m]; {0:+d}% to Off Hand Critical Strike Chance [m]; {0}% more
  Main Hand attack speed [m]
- **Retaliation Charm**: Blind you inflict is Reflected to you [m]
- **Rigwald's Curse**: Modifiers to Claw Damage also apply to Unarmed Attack Damage with Melee Skills [m]; Modifiers
  to Claw Attack Speed also apply to Unarmed Attack Speed with Melee Skills [m]; Modifiers to Claw Critical Strike
  Chance also apply to Unarmed Critical Strike Chance with Melee Skills [m]
- **Ring of Blades**: With at least 40 Dexterity in Radius, Ethereal Knives fires an additional Projectile [m]
- **Rive**: {0}% Chance to cause Bleeding Enemies to Flee on hit [m]
- **Rotgut**: Gain Onslaught for {0} second per Frenzy Charge consumed on use [e]; Consumes 1 Frenzy Charge on use [m]
- **Ryslatha's Coil**: {0}% more Minimum Physical Attack Damage [m]; {0}% more Maximum Physical Attack Damage [m]
- **Sandstorm Visage**: Base Spell Critical Strike Chance of Spells is equal to that of Main Hand Weapon [m]
- **Saqawal's Flock**: Trigger Level 20 Twister when you gain Avian's Might or Avian's Flight [m]
- **Saresh's Darkness**: Enemies Poisoned by you cannot deal Critical Strikes [m]
- **Scaeva**: {0}% increased Global Defences per Empty Socket [e]; {0:+d}% Chance to Block Attack Damage when in Off
  Hand [m]
- **Scornflux**: Increases to Cast Speed from Arcane Surge also applies to Movement Speed [m]
- **Self-Flagellation**: {0}% increased Damage per Curse on you [e]; An additional Curse can be applied to you [m]
- **Serle's Masterwork**: Can be Enchanted by a Kalguuran Runesmith [m]; Can have {0} additional Runesmithing
  Enchantment [m]; Can be Runesmithed as though it were all One Handed Melee Weapon Types [m]
- **Shackles of the Wretched**: You cannot be Chilled for {0} second after being Chilled [m]; You cannot be Frozen for
  {0} second after being Frozen [m]; You cannot be Ignited for {0} second after being Ignited [m]; You cannot be
  Shocked for {0} second after being Shocked [m]; You grant {0} Frenzy Charges to allies on Death [m]
- **Shadowstitch**: Has an additional Implicit Mod [m]
- **Shaper's Seed**: Nearby Allies gain {0}% of Life Regenerated per second [e]; Nearby Allies gain {0}% increased
  Mana Regeneration Rate [m]
- **Shattered Chains**: With at least 40 Strength in Radius, {0}% increased / Rarity of Items dropped by Enemies
  Shattered by Glacial Hammer [m]
- **Sibyl's Lament**: Left ring slot: you and your Minions prevent {0:+d}% of Reflected Elemental Damage [m]; Right
  ring slot: you and your Minions prevent {0:+d}% of Reflected Physical Damage [m]
- **Singularity**: Nearby Enemies are Hindered, with {0}% increased Movement Speed [m]
- **Sinvicta's Mettle**: Gain a Frenzy Charge on every 50th Rampage Kill [m]
- **Skin of the Lords**: You can only Socket Corrupted Gems in this item [m]; Arsenal of Vengeance [m];  [m]; Divine
  Shield [m]; Ghost Dance [m]
- **Skyforth**: {0}% reduced Reservation Efficiency of Skills [m]
- **Snakepit**: Left ring slot: Projectiles from Spells cannot Chain [m]; Left ring slot: Projectiles from Spells Fork
  [m]; Right ring slot: Projectiles from Spells Chain +{0} times [m]; Right ring slot: Projectiles from Spells cannot
  Fork [m]
- **Solerai's Radiance**: Enemies Blinded by you cannot inflict Damaging Ailments [m]
- **Solstice Vigil**: Gain Shaper's Presence for 10 seconds when you kill a Rare or Unique Enemy [m]
- **Soul Ripper**: Consumes Maximum Charges to use [m]
- **Soul Taker**: Eat a Soul when you Kill a Rare or Unique Enemy with this Weapon [m]
- **Soulbound**: Linked Targets Cannot Die for {0} seconds after you Die [m]; Lose no Experience when you die because
  a Linked target died [m]
- **Soulthirst**: Lose all Eaten Souls when you use a Flask [m]
- **Southbound**: Your Hits can only Kill Frozen Enemies [m]
- **Spinesnatch**: {0}% increased Attack Speed per socketed Searching Eye Jewel [m]; {0}% increased Critical Strike
  Chance per socketed Hypnotic Eye Jewel [m]; {0}% increased Physical Damage per socketed Murderous Eye Jewel [m]
- **Spirit Guards**: With at least 40 Dexterity in Radius, Animate Weapon can Animate up to {0} Ranged Weapons [m]
- **Spirited Response**: With at least 40 Intelligence in Radius, {0}% of Damage taken Recouped as Mana if you've
  Warcried Recently [m]
- **Spreading Rot**: With at least 40 Intelligence in Radius, Blight inflicts Withered for 2 seconds [m]; With at
  least 40 Intelligence in Radius, Blight has {0}% increased Hinder Duration [m]
- **Squirming Terror**: An Enemy Writhing Worm spawns every 2 seconds [m]
- **Starlight Chalice**: Inflict Fire, Cold and Lightning Exposure on nearby Enemies when used [m]
- **Steel Spirit**: With at least 40 Dexterity in Radius, each Spectral Throw Projectile gains {0}% increased Damage
  each time it Hits [m]
- **Stormfire**: {0}% increased Burning Damage for each time you have Shocked a Non-Shocked Enemy Recently, up to a
  maximum of 120% [m]
- **Stormshroud**: Modifiers to Chance to Avoid being Shocked apply to all Elemental Ailments [m]
- **Story of the Vaal**: Hits with this Weapon always Ignite, Freeze, and Shock [m]; Hits with this Weapon Freeze
  Enemies as though dealing {0}% more Damage [m]; Hits with this Weapon Shock Enemies as though dealing {0}% more
  Damage [m]
- **Stranglegasp**: Can have a second Enchantment Modifier [m]
- **String of Servitude**: Implicit Modifier magnitudes are tripled [m]
- **Sudden Dawn**: {0}% chance for Energy Shield Recharge to start when you Kill an Enemy [m]; {0}% more Energy Shield
  Recharge Rate [m]
- **Sudden Ignition**: Ignited Enemies Killed by your Hits are destroyed [m]
- **Sunblast**: Traps cannot be triggered by Enemies [m]; Traps from Skills are thrown randomly around targeted
  location [m]
- **Surgebinders**: {0}% chance to Steal Power, Frenzy, and Endurance Charges on Hit [m]
- **Survival Skills**: Flasks gain {0} Charges when you hit a Non-Unique Enemy, no more than once per second [m]; {0}%
  more Flask Charges gained from Kills [m]
- **Tanu Ahi**: {0}% chance to gain Adrenaline for 2 Seconds when Leech is / removed by Filling Unreserved Life [m];
  {0}% chance to gain Onslaught for 4 Seconds when Leech is / removed by Filling Unreserved Life [m]
- **The Admiral**: Elemental Damage you Deal with Hits is Resisted by lowest Elemental Resistance instead [m]
- **The Adorned**: {0}% increased Effect of Jewel Socket Passive Skills containing Corrupted Magic Jewels [m]
- **The Annihilating Light**: Deal Triple Damage with Elemental Skills [m]
- **The Bandit Lord's Band**: {0}% increased Quantity of Gold Dropped by Slain Enemies [m]
- **The Bringer of Rain**: Can't use Chest armour [m]
- **The Burden of Shadows**: Lose {0} Life per second [e]; Spells deal added Chaos Damage equal to {0}% of your
  maximum Life [m]
- **The Burden of Truth**: Chaos Damage taken does not bypass Energy Shield [m]; Supreme Decadence [m]
- **The Coming Calamity**: 35% chance to avoid being Stunned for each Herald Buff affecting you [m]; Mana Reservation
  of Herald Skills is always 45% [m]
- **The Crimson Storm**: Enemies you inflict Bleeding on grant {0}% increased Flask Charges [m]
- **The Dancing Dervish**: Triggers Level {0} Manifest Dancing Dervishes on Rampage [m]; Manifested Dancing Dervishes
  disables both weapon slots [m]; Manifested Dancing Dervishes die when Rampage ends [m]
- **The Dancing Duo**: Triggers Level {0} Manifest Dancing Dervishes on Rampage [m]; Manifested Dancing Dervishes
  disables both weapon slots [m]; Manifested Dancing Dervishes die when Rampage ends [m]
- **The Dark Seer**: Unaffected by Blind [m]; Enemies Blinded by you have Malediction [m]
- **The Draugur's Lantern**: [Draugurflame|Draugurflames] burn enemies around you for 8 seconds after you Ignite a
  target [m]
- **The Druggery**: {0}% of Life Recovery from Flasks is applied to nearby Allies instead of You [m]
- **The Effigon**: Damage Penetrates {0}% Fire Resistance against Blinded Enemies [e]; Your Hits can't be Evaded by
  Blinded Enemies [m]
- **The Eternity Shroud**: Gain {0}% of Elemental Damage as Extra Chaos Damage per Shaper Item Equipped [e]; {0}%
  chance to Trigger Level 20 Glimpse of Eternity when Hit [m]
- **The Flame of Hope**: Inflict Fire Exposure on Hit against Enemies with / 5 Cinderflame, applying {0}% to Fire
  Resistance [m]
- **The Formless Flame**: Armour is increased by Overcapped Fire Resistance [m]
- **The Front Line**: Adds Veteran's Awareness [m]
- **The Fulcrum**: Elemental Damage with Hits is Lucky while you are Shocked [e]; Elemental Ailments you inflict are
  Reflected to you [m]
- **The Geomantic Gyre**: Has a Crucible Passive Skill Tree with only Support Passive Skills / Crucible Passive Skill
  Tree is removed if this Modifier is removed [m]
- **The Gluttonous Tide**: Lose all Frenzy Charges on reaching Maximum Frenzy Charges to make the next Bow Attack you
  perform fire that many additional Arrows [m]
- **The Goddess Bound**: Uses both hand slots [m]
- **The Goddess Scorned**: Uses both hand slots [m]; You can only deal Damage with this Weapon or Ignite [m]
- **The Grey Spire**: Implicit Modifier magnitudes are doubled [m]
- **The Gull**: {0}% chance to Trigger Level 1 Create Lesser Shrine when you Kill an Enemy [m]
- **The Highwayman**: {0}% chance to Steal Power, Frenzy, and Endurance Charges on Hit [m]
- **The Hungry Loop**: Consumes Socketed Uncorrupted Support Gems when they reach Maximum Level / Can Consume {0}
  Uncorrupted Support Gem / Has not Consumed any Gems [m]
- **The Long Winter**: With 40 Intelligence in Radius, Glacial Cascade has an additional Burst [m]; With 40
  Intelligence in Radius, {0}% of Glacial Cascade Physical Damage / Converted to Cold Damage [m]
- **The Monastery Bell**: Count as Blocking Attack Damage from the first target Hit with each Shield Attack [m]
- **The Princess**: {0}% increased Damage taken from Ghosts [m]
- **The Queen's Hunger**: Trigger Level {0} Bone Offering, Flesh Offering, Spirit Offering every 5 seconds in sequence
  / Offering Skills Triggered this way also affect you [m]
- **The Redblade**: Has a Two Handed Sword Crucible Passive Skill Tree / Crucible Passive Skill Tree is removed if
  this Modifier is removed [m]
- **The Retch**: {0}% of Life Leech applies to Enemies as Chaos Damage [m]
- **The Snowblind Grace**: Arctic Armour has no Reservation [m]
- **The Sorrow of the Divine**: Zealot's Oath during Effect [m]; Life Recovery from Flasks also applies to Energy
  Shield during Effect [m]
- **The Sundered Will**: Increases and Reductions to Chaos Damage also apply to Effect of / Auras from Chaos Skills at
  {0}% of their value, up to a maximum of 150% [m]
- **The Supreme Truth**: Attacks with this weapon inflict Hallowing Flame on Hit [m]
- **The Taming**: {0}% increased Elemental Damage with Hits and Ailments for / each type of Elemental Ailment on Enemy
  [m]
- **The Trickster's Smile**: When an Enemy Hit deals Elemental Damage to you, their Resistance to those Elements
  becomes zero for 4 seconds [m]
- **The Unblinking Eye**: Increases and Reductions to your Evasion Rating also apply to your Spell Damage [m];
  Physical Damage Reduction is zero [m]
- **The Unseen Hue**: Hits with Prismatic Skills always Sap [m]
- **The Vigil**: With at least 40 Strength in Radius, Hits with Vigilant Strike Fortify you and Nearby Allies for {0}
  seconds [m]
- **The Will of Esh**: Increases and Reductions to Lightning Damage also apply to Effect of / Auras from Lightning
  Skills at {0}% of their value, up to a maximum of 150% [m]
- **The Will of Tul**: Increases and Reductions to Cold Damage also apply to Effect of / Auras from Cold Skills at
  {0}% of their value, up to a maximum of 150% [m]
- **The Will of Uul-Netol**: Increases and Reductions to Physical Damage also apply to Effect of / Auras from Physical
  Skills at {0}% of their value, up to a maximum of 150% [m]
- **The Will of Xoph**: Increases and Reductions to Fire Damage also apply to Effect of / Auras from Fire Skills at
  {0}% of their value, up to a maximum of 150% [m]
- **The Winds of Fate**: {0}% of Physical Damage converted to a random Element [m]; Maximum Critical Strike Chance is
  50% [m]; Non-Critical Strikes deal no Damage [m]
- **The Writhing Jar**: An Enemy Writhing Worms escape the Flask when used / Writhing Worms are destroyed when Hit [m]
- **Thief's Torment**: Can't use other Rings [m]
- **Thirst for Horrors**: {0}% Chance to Cause Monster to Flee on Block [m]
- **Tidebreaker**: {0}% chance to gain a [BrineCharges|Brine Charge] instead of an Endurance Charge [m]
- **Tinkerskin**: {0}% chance to gain Phasing for 3 seconds when your Trap is triggered by an Enemy [m]; {0}% chance
  to gain Phasing for 3 seconds when your Trap is triggered by an Enemy [m]; Recover {0} Life when your Trap is
  triggered by an Enemy [m]; Recover {0} Energy Shield when your Trap is triggered by an Enemy [m]
- **Tombfist**: With a Murderous Eye Jewel Socketed, Intimidate Enemies for 4 seconds on Hit with Attacks [m]; With a
  Searching Eye Jewel Socketed, Maim Enemies for 4 seconds on Hit with Attacks [m]
- **Touch of Anguish**: Critical Strikes do not inherently Freeze [m]
- **Tremor Rod**: Mines can be Detonated an additional time [m]
- **Unlight Extant**: Chaos Skills inflict up to 15 Withered Debuffs on Hit for {0} seconds [m]; Cannot Inflict Wither
  on targets that are not on Full Life [m]
- **Usurper's Penance**: {0}% chance to gain a Frenzy Charge on Critical Strike at Close Range [m]
- **Uul-Netol's Kiss**: Exerted Attacks deal {0}% increased Damage [m]; Exerted Attacks Knock Enemies Back on Hit [m]
- **Veil of the Night**: Elemental Resistances are Zero [m]
- **Voice of the Storm**: Lightning Damage with Non-Critical Strikes is Lucky [m]
- **Volley Fire**: With at least 40 Dexterity in Radius, Barrage fires an additional projectile simultaneously on the
  first and final attacks [m]
- **Voltaxic Rift**: Treats Enemy Monster Chaos Resistance values as inverted [m]
- **Warrior's Legacy**: {0}% more Attack Speed [m]
- **Weight of the Empire**: With at least 40 Strength in Radius, Heavy Strike has a  / {0}% chance to deal Double
  Damage [m]
- **Wellwater Phylactery**: Removes {0}% of Life when Used [m]; Energy Shield Recharge is not delayed by Damage during
  Effect [m]
- **Widowmaker**: {0}% increased Critical Strike Chance against Enemies that are on Full Life [m]; {0}% of Attack
  Damage Leeched as Life on Critical Strike [m]
- **Wildfire**: With at least 40 Strength in Radius, Molten Strike Projectiles Chain on impacting ground [m]; With at
  least 40 Strength in Radius, Molten Strike Projectiles Chain {0:+d} time [m]; With at least 40 Strength in Radius,
  Molten Strike fires {0}% more Projectiles [m]
- **Wildslash**: {0}% increased Damage with Movement Skills [m]; {0}% increased Attack Speed with Movement Skills [m]
- **Windripper**: {0}% increased Rarity of Items Dropped by Slain Shocked Enemies [m]; {0}% increased Rarity of Items
  Dropped by Frozen Enemies [m]
- **Wine of the Prophet**: Grants a random Divination Buff for {0} seconds when Used [m]
- **Wings of Entropy**: Adds {0} to {1} Fire Damage in Main Hand [m]; Adds {0} to {1} Fire Damage in Main Hand [m];
  Adds {0} to {1} Chaos Damage in Off Hand [m]; Adds {0} to {1} Chaos Damage in Off Hand [m]; Counts as Dual Wielding
  [m]
- **Winter Burial**: With at least 40 Strength in Radius, Glacial Hammer deals / Cold-only Splash Damage to
  surrounding targets [m]; With at least 40 Strength in Radius, {0}% of Glacial / Hammer Physical Damage Converted to
  Cold Damage [m]
- **Winter's Bounty**: With at least 40 Intelligence in Radius, Cold Snap grants Power Charges instead of Frenzy
  Charges when Enemies die in its Area / With at least 40 Intelligence in Radius, Cold Snap's Cooldown can be bypassed
  by Power Charges instead of Frenzy Charges [m]
- **Woespike**: {0}% chance on Melee Hit for the Strongest Impale on target to last for 1 additional Hit [m]
- **Xoph's Heart**: Nearby Enemies are Covered in Ash [m]
- **Yaomac's Accord**: Shepherd of Souls [m]
- **Zerphi's Heart**: Chaos Damage can Ignite, Chill and Shock [m]; Gain Soul Eater for {0} seconds when you use a
  Vaal Skill [m]
- **Zerphi's Last Breath**: Grants Last Breath when you Use a Skill during Effect, for {0}% of Mana Cost [m]

## Hard: new code in a system (126 uniques)

- **Al Dhih**: Enemies Taunted by your Warcries have {0}% chance to Explode on death, dealing 8% of their maximum Life
  as Chaos Damage [h]; Warcry Skills' Cooldown Time is 4 seconds [h]
- **Amanamu's Gaze**: Minions have {0:+d}% to Damage over Time Multiplier per / Ghastly Eye Jewel affecting you, up to
  a maximum of +30% [h]
- **Ancient Waystones**: {0}% increased Cost of Aura Skills that summon Totems [h]
- **Arohongui's Tending**: Life Flasks used while on Low Life apply Recovery Instantly [h]; Mana Flasks used while on
  Low Mana apply Recovery Instantly [h]
- **Asenath's Chant**: {0}% chance to Trigger a Socketed Spell when you Attack with a Bow, with a 0.3 second Cooldown
  [h]
- **Asenath's Mark**: {0}% chance to Trigger a Socketed Spell when you Attack with a Bow, with a 0.3 second Cooldown
  [h]
- **Asphyxia's Wrath**: Hexes Transfer to all Enemies within 3 metres when Hexed Enemy dies [h]
- **Atziri's Acuity**: You have Vaal Pact if you've dealt a Critical Strike Recently [h]
- **Auxium**: Chill Effect and Freeze Duration on you are based on {0}% of Energy Shield [h]
- **Bitterbind Point**: {0:+d} to Armour during Soul Gain Prevention [e]; Raised Spectres have {0:+d}% to all maximum
  Resistances [h]; Raised Spectres fire {0} additional Projectiles [h]; Raised Spectres have {0:+d}% to Critical
  Strike Chance [h]; Raised Spectres have {0}% increased Area of Effect [h]
- **Bound By Destiny**: Nearby Enemies are Blinded if 2 Redeemer Items are Equipped [h]; Nearby Enemies are
  Intimidated if 2 Warlord Items are Equipped [h]; Every 4 seconds, Regenerate {0}% of Life over one second if 2
  Hunter Items are Equipped [h]; {0}% of Elemental Damage taken as Chaos Damage if 4 Hunter Items are Equipped [h];
  Gain {0} Endurance Charge every second if you've been Hit Recently and / 4 Warlord Items are Equipped [h]; Skills
  fire an additional Projectile if 6 Hunter Items are Equipped [h]; Gain {0}% of Physical Damage as Extra Damage of
  each Element if / 6 Shaper Items are Equipped [h]
- **Breath of the Council**: Chaos Skills have {0}% increased Skill Effect Duration [h]
- **Broken Faith**: You have Unholy Might while you have no Energy Shield [e]; Create Profane Ground instead of
  Consecrated Ground [h]
- **Cadigan's Authority**: You cannot have more than 2 Summoned Totems of the same type [h]
- **Chains of Command**: {0}% chance to Trigger Level 20 Animate Guardian's Weapon when Animated Guardian Kills an
  Enemy [m]; {0}% chance to Trigger Level 18 Animate Guardian's Weapon when Animated Weapon Kills an Enemy [m];
  Animated Guardian deals {0}% increased Damage per Animated Weapon [h]
- **Chains of Emancipation**: Immune to Curses while you have at least {0} Rage [e]; Enemy Hits inflict Temporal
  Chains on you [h]; When you lose Temporal Chains you gain maximum Rage [h]
- **Cinderswallow Urn**: Your Physical Damage can Ignite during Effect [m]; Enemies Ignited by you during Effect have
  Malediction [m]; You can apply one fewer Curse during Effect [m]; Ignites you inflict during Effect spread to other
  Enemies within {0} metre [m]; Leech {0}% of Expected Ignite Damage as Life when you Ignite an Enemy during Effect
  [m]; Prevent {0:+d}% of Reflected Damage during Effect [m]; {0}% of Damage Taken from Hits is Leeched as Life during
  Effect [h]
- **Clayshaper**: Golems have {0} to {1} Added Attack Physical Damage [h]; Golems have {0} to {1} Added Attack
  Physical Damage [h]
- **Cloak of Tawm'r Isley**: Found Magic Items drop Identified [m]; Socketed Minion Gems are Supported by Level {0}
  Life Leech [h]; Minions cannot be Blinded [h]
- **Coiling Whisper**: Targets are Unaffected by your Hexes [m]; When 90% of your Hex's Duration Expires on an Enemy,
  Eat 1 Soul per Enemy Power [h]
- **Coralito's Signature**: {0:+d}% to Damage over Time Multiplier for Poison from Critical Strikes during Effect [h]
- **Cowards' Wail**: Totems which would be killed by Enemies become Spectral Totems for {0} seconds instead [h];
  Skills used by Spectral Totems deal {0}% more Damage [h]; Maximum {0} Spectral Totems [h]
- **Dendrobate**: {0}% increased Poison Duration if you have at least 150 Intelligence [h]
- **Doomsower**: You have Vaal Pact while all Socketed Gems are Red [e]; Attack Skills gain {0}% of Physical Damage as
  Extra Fire Damage per Socketed Red Gem [h]
- **Drillneck**: Arrows deal 50% increased Damage with Hits and Ailments to Targets they Pierce [h]
- **Earendel's Embrace**: Summoned Skeletons take {0}% of their Maximum Life per second as Fire Damage [h]
- **Essence Worm**: {0}% reduced Reservation Efficiency of Skills [m]; Socketed Gems have no Reservation / Your
  Blessing Skills are Disabled [h]
- **Ewar's Mirage**: Attacks Chain an additional time when in Main Hand [h]; Attacks fire an additional Projectile
  when in Off Hand [h]
- **Extractor Mentis**: {0}% chance to grant Chaotic Might to nearby Enemies on Kill [h]; {0}% chance to grant
  Onslaught to nearby Enemies on Kill [h]
- **Festering Resentment**: Trigger a Socketed Spell when you Block, with a 0.25 second Cooldown [m]; {0:+d}% Chance
  to Block Spell Damage while in Off Hand [m]; Triggered Spells Poison on Hit [h]; All Damage with Triggered Spells
  can Poison [h]
- **Firesong**: Modifiers to Ignite Duration on you apply to all Elemental Ailments [h]
- **Garukhan's Flight**: Immune to Burning Ground, Shocked Ground and Chilled Ground [h]
- **Goblinedge**: {0:+d} seconds to Duration of Frenzy and Power Charges on Culling Strike [h]
- **Hand of the Fervent**: Hits have {0}% chance to ignore Enemy Physical Damage Reduction while you have Sacrificial
  Zeal [e]; Gain Sacrificial Zeal when you use a Skill, dealing you {0}% of the Skill's Mana Cost as Physical Damage
  per Second [h]
- **Hand of the Lords**: You cannot Cast Socketed Hex Curse Skills / Inflict Socketed Hexes on Enemies that trigger
  your Traps [h]
- **Hezmana's Bloodlust**: Attacks Cost Life instead of Mana [h]
- **Howlcrack**: Non-Instant Warcries ignore their Cooldown when Used [h]; Warcries Cost {0:+d}% of Life [h]
- **Icefang Orbit**: Non-Chilled Enemies you Poison are Chilled [m]; Poisoned Enemies you Kill with Hits Shatter [m];
  You are Chilled when you are Poisoned [h]
- **Inpulsa's Broken Heart**: Shocked Enemies you Kill Explode, dealing {0}% of / their Life as Lightning Damage which
  cannot Shock [h]
- **Jorrhast's Blacksteel**: {0}% chance to Trigger Level 20 Animate Weapon on Kill [m]; Weapons you Animate create an
  additional copy [m]; {0}% more Animate Weapon Duration [h]
- **Kitava's Thirst**: Trigger Socketed Spells when you Spend at least {0} Mana on an / Upfront Cost to Use or Trigger
  a Skill, with a 0.1 second Cooldown [h]; Trigger Socketed Spells when you Spend at least {0} Mana on an / Upfront
  Cost to Use or Trigger a Skill, with a 0.1 second Cooldown [h]
- **Lion's Roar**: Adds Knockback to Melee Attacks during Effect [e]; Knocks Back Enemies in an Area when you use a
  Flask [m]; {0}% chance to cause Enemies to Flee on use [m]; {0}% more Melee Physical Damage during effect [h]
- **Malachai's Artifice**: {0:+d}% to Fire Resistance when Socketed with a Red Gem [h]; {0:+d}% to Cold Resistance
  when Socketed with a Green Gem [h]; {0:+d}% to Lightning Resistance when Socketed with a Blue Gem [h]
- **Maloney's Mechanism**: {0}% chance to Trigger a Socketed Bow Skill when you Attack with a Bow, with a 1 second
  Cooldown [h]
- **Maloney's Nightfall**: {0}% chance to create a Smoke Cloud when Hit [h]
- **Mark of the Red Covenant**: Summoned Raging Spirits' Hits always Ignite [h]; Summoned Raging Spirits' Melee
  Strikes deal Fire-only Splash / Damage to Surrounding Targets [h]
- **Mon'tregul's Grasp**: {0}% increased Raised Zombie Size [h]; Enemies Killed by Zombies' Hits Explode, dealing {0}%
  of their Life as Fire Damage [h]
- **Null's Inclination**: Trigger Socketed Minion Spells on Kill with this Weapon / Minion Spells Triggered by this
  Item have a 0.25 second Cooldown with 5 Uses [h]
- **Nuro's Harp**: {0}% chance to create Chilled Ground when you Freeze an Enemy [h]; {0}% chance to create
  Consecrated Ground when you Shatter an Enemy [h]; {0}% increased Effect of Chilled Ground [h]
- **Obscurantis**: {0}% increased Projectile Attack Damage per 200 Accuracy Rating [h]
- **Olroth's Resolve**: Ward does not Break during Effect [m]; {0}% more Ward during Effect [h]
- **Oskarm**: Trigger Level {0} Assassin's Mark when you Hit a Rare or Unique Enemy and have no Mark [h]
- **Precursor's Emblem**: {0}% chance to Intimidate Enemies for 4 seconds on Hit with Attacks while at maximum
  Endurance Charges [h]; {0}% chance to gain a Flask Charge when you deal a Critical Strike while at maximum Frenzy
  Charges [h]; {0}% chance to Gain Onslaught for 4 seconds on Hit while at maximum Frenzy Charges [h]; {0}% chance to
  Gain Arcane Surge on Hit with Spells while at maximum Power Charges [h]
- **Reach of the Council**: Arrows fired from the third firing points Return to you [m]; Arrows fired from the first
  firing points always Pierce [h]; Arrows fired from the second firing points Fork [h]; Arrows fired from the fourth
  firing points Chain {0:+d} time [h]
- **Replica Atziri's Acuity**: You have Perfect Agony if you've dealt a Critical Strike recently [h]
- **Replica Bones of Ullr**: Summoned Phantasms have {0}% chance to refresh their Duration when they Hit a Rare or
  Unique Enemy [h]; Summoned Raging Spirits have {0}% chance to refresh their Duration when they Hit a Rare or Unique
  Enemy [h]
- **Replica Earendel's Embrace**: Raised Zombies take {0}% of their Maximum Life per second as Fire Damage [h]
- **Replica Gifts from Above**: {0}% increased Rarity of Items Dropped by Enemies killed with a Critical Strike [m];
  Inflict Hallowing Flame on Hit while on Consecrated Ground [h]
- **Replica Kalisa's Grace**: Gain {0}% increased Area of Effect for 2 seconds after Spending a total of 800 Mana [h]
- **Replica Kongor's Undying Rage**: Regenerate {}% of Energy Shield per second if you've dealt a Critical Strike with
  this weapon Recently [h]
- **Replica Malachai's Artifice**: {0:+d}% to Fire Resistance when Socketed with a Red Gem [h]; {0:+d}% to Cold
  Resistance when Socketed with a Green Gem [h]; {0:+d}% to Lightning Resistance when Socketed with a Blue Gem [h]
- **Replica Volkuur's Guidance**: Your Chaos Damage can Ignite [m]; Chaos Skills have {0}% chance to Ignite [m]; {0}%
  more Ignite Duration [h]
- **Replica Witchfire Brew**: Grants Level {0} Vulnerability Curse Aura during Effect [h]
- **Rotting Legion**: {0}% more Elemental Damage taken per Raised Zombie [h]
- **Scold's Bridle**: Your Skills deal you {0}% of Mana Spent on Upfront Skill Mana Costs as Physical Damage [h]
- **Sentari's Answer**: Curse Enemies with Punishment when you Block their Melee Damage, ignoring Curse Limit [m];
  Curse Enemies with Elemental Weakness when you Block their Spell Damage, ignoring Curse Limit [m]; Curse Enemies
  with Temporal Chains when you Block their Projectile Attack Damage, ignoring Curse Limit [h]
- **Seven Teachings**: {0}% more Attack Speed with Unarmed Melee Attacks [m]; Trigger a Socketed Spell on Unarmed
  Melee Critical Strike, with a {0} second Cooldown [h]
- **Severed in Sleep**: Minions have +{0}% to Critical Strike Multiplier per Withered Debuff on Enemy [h]
- **Shadows and Dust**: Gain Unholy Might for {0} second on Rampage [m]; Creates a Smoke Cloud on Rampage [h]
- **Sidhebreath**: Minions deal no Non-Cold Damage [h]
- **Sin's Rebirth**: Unholy Might during Effect [h]
- **Skysunder**: Ignites you cause are reflected back to you [m]; Ignites you inflict with this weapon spread to other
  Enemies within {0} metre [m]; {0}% more Duration of Ignites you inflict [h]
- **Slavedriver's Hand**: Increases and Reductions to Cast Speed also Apply to Trap Throwing Speed [m]; Skills which
  throw Traps Cost Life instead of Mana [h]; {0}% chance to gain an Endurance, Frenzy or Power Charge when any / of
  your Traps are Triggered by an Enemy [h]
- **Slivertongue**: {0}% increased Critical Strike Chance with arrows that Fork [h]; Arrows that Pierce have {0:+d}%
  to Critical Strike Multiplier [h]
- **Soul Catcher**: Cannot gain Mana during effect [m]; Vaal Skills used during effect have {0}% increased Soul Gain
  Prevention Duration [h]
- **Soul Mantle**: Inflicts a random Hex on you when your Totems die [h]
- **Soulwrest**: If you Consumed a corpse Recently, you and nearby Allies Regenerate {0}% of Life per second [e];
  Trigger Level {0} Summon Phantasm Skill when you Consume a corpse [h]
- **Spinehail**: Bow Attacks Sacrifice a random Damageable Minion to fire {0} additional Arrow [h]
- **Spire of Stone**: Totems cannot be Stunned [h]
- **Sporeguard**: You have Fungal Ground around you while stationary [e]; Trigger Level {0} Contaminate when you Kill
  an Enemy [m]; Enemies on Fungal Ground you Kill have {0}% chance to Explode, dealing 10% of their Life as Chaos
  Damage [h]
- **Star of Wraeclast**: You cannot be Cursed with Silence [m]; Frostblink has {0}% increased Duration [h]
- **Steelworm**: Attacks that Fire Projectiles Consume up to {0} additional Steel Shard [h]; Skills Fire {0}
  additional Projectile for 4 seconds after / you consume a total of 8 Steel Shards [h]
- **Steppan Eard**: {0}% increased Movement Speed on Shocked Ground [h]; {0}% increased Damage on Burning Ground [h];
  Regenerate {0}% of Life per second on Chilled Ground [h]; Unaffected by Desecrated Ground [h]
- **Sublime Vision**: Aura Skills other than Purity of Elements are Disabled [m]; Aura Skills other than Purity of
  Fire are Disabled [m]; Aura Skills other than Purity of Ice are Disabled [m]; Aura Skills other than Purity of
  Lightning are Disabled [m]; Always Scorch while affected by Anger [h]; {}% increased Action Speed while affected by
  Haste [h]; Always inflict Brittle while affected by Hatred [h]; {}% more Critical Strike chance while affected by
  Precision [h]; {}% of Cold and Lightning Damage taken as Fire Damage while affected by Purity of Fire [h]; {}% of
  Fire and Lightning Damage taken as Cold Damage while affected by Purity of Ice [h]; {}% of Fire and Cold Damage
  taken as Lightning Damage while / affected by Purity of Lightning [h]; Regenerate {}% Life over one second when hit
  while affected by Vitality [h]; Always Sap while affected by Wrath [h]
- **Tavukai**: Summoned Raging Spirits take {0}% of their Maximum Life per second as Chaos Damage [h]
- **Tawhanuku's Timing**: Spells cause you to gain Energy Shield equal to their Upfront / Cost every third time you
  Pay it [h]
- **The Arkhon's Tools**: Summon Skitterbots also summons a Scorching Skitterbot [h]; Summoned Skitterbots' Auras
  affect you as well as Enemies [h]; {0}% increased Effect of Non-Damaging Ailments inflicted by Summoned Skitterbots
  [h]
- **The Balance of Terror**: Gain {0}% of Physical Damage as a Random Element if you've cast Elemental Weakness in the
  past 10 seconds [h]; Immune to Reflected Damage if you've cast Punishment in the past 10 seconds [h]
- **The Brine Crown**: {0}% chance to create Chilled Ground when Hit with an Attack [h]
- **The Broken Elegy**: When a nearby Minion dies, gain Rotten Bulwark equal to {0}% of its maximum Life [h]
- **The Crustacean's Call**: Totems have {0}% increased Movement Speed [h]
- **The Eternal Apple**: {0}% chance to Trigger a Socketed Warcry Skill when you lose Endurance Charges, with a 0.25
  second Cooldown [h]
- **The Felbog Fang**: Enemies Cursed by you are Hindered if 25% of Curse Duration expired [h]; Enemies Cursed by you
  take {0}% increased Damage if 75% of Curse Duration expired [h]
- **The Fledgling**: Projectiles cannot collide with Enemies in Close Range [h]
- **The Goddess Unleashed**: Uses both hand slots [m]; {0}% chance to gain Her Blessing for 3 seconds when you Ignite
  an Enemy [m]; {0}% chance to Avoid being Ignited, Chilled or Frozen with Her Blessing [m]; {0}% increased Attack and
  Movement Speed with Her Blessing [m]; {0}% chance to Blind nearby Enemies when gaining Her Blessing [h]
- **The Infinite Pursuit**: Bleeding on you expires {0}% faster while Moving [h]
- **The Nomad**: {0}% increased Projectile Attack Damage while you have at least 200 Dexterity [h]
- **The Overflowing Chalice**: {0}% increased Charges gained by Other Flasks during Effect [h]; Gains no Charges
  during Effect of any Overflowing Chalice Flask [h]
- **The Poet's Pen**: Adds {0} to {1} Physical Damage to Attacks with this Weapon per 3 Player Levels [m]; Adds {0} to
  {1} Physical Damage to Attacks with this Weapon per 3 Player Levels [m]; {0}% chance to Trigger a Socketed Spell
  when you Attack with this Weapon, with a 0.25 second Cooldown [h]
- **The Primordial Chain**: {0}% increased Golem Size [h]
- **The Sands of Time**: {0}% increased Cast Speed for each different Non-Instant Spell you've Cast Recently [m];
  Trigger level 20 Suspend in Time on Casting a Spell [h]
- **The Saviour**: Triggers Level {0} Reflection when Equipped [m];  [h]
- **Torchoak Step**: Totems Reflect {0}% of their maximum Life as Fire Damage to nearby Enemies when Hit [h]
- **Trolltimber Spire**: {0}% of Damage dealt by your Totems is Leeched to you as Life [h]
- **Unending Hunger**: With at least 40 Intelligence in Radius, Raised Spectres have a {0}% chance to gain Soul Eater
  for 20 seconds on Kill [h]
- **Unholy Accomplice**: Pact Skills have {0}% increased Cast Speed [m]; Pact Skills grant [PactBoon|Boons] instead of
  [PactAffliction|Afflictions] [m]; Pact Skills have {0}% increased Cooldown Recovery Rate [h]
- **Unstable Payload**: {0}% Chance for Traps to Trigger an additional time [h]
- **Uul-Netol's Vow**: Socketed Support Gems can also Support Skills from Equipped Body Armour [h]
- **Venarius' Astrolabe**: The stars are aligned if you have 6 Influence types among other Equipped Items [e]; You
  have Elemental Conflux if the stars are aligned [h]; {0:+d} to Level of all Elemental Skill Gems if the stars are
  aligned [h]; {0:+d} to Level of all Elemental Support Gems if the stars are aligned [h]
- **Venopuncture**: Non-Chilled Enemies you inflict Bleeding on are Chilled [m]; Bleeding Enemies you Kill with Hits
  Shatter [m]; You are Chilled while you are Bleeding [h]
- **Vessel of Vinktar**: Shocks nearby Enemies during Effect, causing 10% increased Damage taken [h]; You are Shocked
  during Effect, causing 50% increased Damage taken [h]
- **Violent Dead**: With at least 40 Intelligence in Radius, Raised / Zombies' Slam Attack has {0}% increased Cooldown
  Recovery Rate [h]; With at least 40 Intelligence in Radius, Raised Zombies' Slam / Attack deals {0}% increased
  Damage [h]
- **Vis Mortis**: Minions gain {0}% of Elemental Damage as Extra Chaos Damage [h]
- **Volkuur's Guidance**: Your Lightning Damage can Poison [m]; Lightning Skills have {0}% chance to Poison on Hit
  [m]; {0}% more Poison Duration [h]
- **Vorana's March**: Triggers Level {0} Summon Arbalists when Equipped [h]; Summoned Arbalists have {}% increased
  Attack Speed [h]; Summoned Arbalists' Projectiles Chain {:+d} times [h]; Summoned Arbalists' Attacks have {}% chance
  to inflict Bleeding [h]; Summoned Arbalists have {}% chance to Crush on Hit [h]; Summoned Arbalists have {}% chance
  to deal Double Damage [h]; Summoned Arbalists have {}% chance to Freeze, Shock, and Ignite [h]; Summoned Arbalists
  have {}% chance to inflict Cold Exposure on Hit [h]; Summoned Arbalists have {}% chance to inflict Fire Exposure on
  Hit [h]; Summoned Arbalists have {}% chance to inflict Lightning Exposure on Hit [h]; Summoned Arbalists have {}%
  chance to Intimidate for 4 seconds on Hit [h]; Summoned Arbalists have {}% chance to Maim for 4 seconds on Hit [h];
  Summoned Arbalists have {}% chance to Poison [h]; Summoned Arbalists have {}% chance to Unnerve for 4 seconds on Hit
  [h]; Summoned Arbalists fire {} additional Projectiles [h]; {0:+d} to number of Summoned Arbalists [h]; Summoned
  Arbalists' Projectiles Split into {} [h]; Summoned Arbalists gain {}% of Physical Damage as Extra Cold Damage [h];
  Summoned Arbalists have {}% chance to inflict Cold Exposure on Hit [h]; Summoned Arbalists gain {}% of Physical
  Damage as Extra Fire Damage [h]; Summoned Arbalists have {}% chance to inflict Fire Exposure on Hit [h]; Summoned
  Arbalists gain {}% of Physical Damage as Extra Lightning Damage [h]; Summoned Arbalists have {}% chance to inflict
  Lightning Exposure on Hit [h]; Summoned Arbalists Convert {}% of Physical Damage to Cold Damage [h]; Summoned
  Arbalists have {}% chance to Freeze [h]; Summoned Arbalists Convert {}% of Physical Damage to Fire Damage [h];
  Summoned Arbalists have {}% chance to Ignite [h]; Summoned Arbalists Convert {}% of Physical Damage to Lightning
  Damage [h]; Summoned Arbalists have {}% chance to Shock [h]; Summoned Arbalists' Projectiles Fork [h]; Summoned
  Arbalists' Projectiles Pierce {} additional Targets [h]
- **Wake of Destruction**: Drops Shocked Ground while moving, lasting {0} seconds [h]
- **Watcher's Eye**: {0}% of Damage taken while affected by Clarity Recouped as Mana [h]; Gain {0}% of Maximum Mana as
  Extra Maximum Energy Shield while affected by Clarity [h]; Prevent {0:+d}% of Reflected Physical Damage you would
  take while affected by Determination [h]; Gain {0} Energy Shield per Enemy Hit while affected by Discipline [h];
  Unaffected by Enfeeble while affected by Grace [h]; You gain Onslaught for {0} seconds on Kill while affected by
  Haste [h]; Damaging Ailments you inflict deal Damage {0}% faster while affected by Malevolence [h]; Prevent {0:+d}%
  of Reflected Elemental Damage you would take while / affected by Purity of Elements [h]; Unaffected by Elemental
  Weakness while affected by Purity of Elements [h]; {}% of Cold and Lightning Damage taken as Fire Damage while
  affected by Purity of Fire [h]; Unaffected by Flammability while affected by Purity of Fire [h]; {}% of Fire and
  Lightning Damage taken as Cold Damage while affected by Purity of Ice [h]; Unaffected by Frostbite while affected by
  Purity of Ice [h]; {}% of Fire and Cold Damage taken as Lightning Damage while / affected by Purity of Lightning
  [h]; Unaffected by Conductivity while affected by Purity of Lightning [h]; Gain {0} Life per Enemy Hit while
  affected by Vitality [h]; {0}% increased Life Recovery from Flasks while affected by Vitality [h]; Effects of
  Consecrated Ground you create while affected by Zealotry Linger for {0} seconds [h]; {0}% increased Critical Strike
  Chance against Enemies on Consecrated Ground while affected by Zealotry [h]
- **Waxen Soul**: Curse Skills cost Life instead of Mana [m]; {0}% chance to Trigger Explosive Toad when you kill an
  Enemy [h]
- **Whispers of Infinity**: {0} to {1} Added Attack Chaos Damage per 100 Maximum Mana [e]; {0} to {1} Added Attack
  Chaos Damage per 100 Maximum Mana [e]; Skills Cost Energy Shield instead of Mana or Life [h]
- **Wing of the Wyvern**: Trigger a Socketed Spell when a Hit from this / Weapon Freezes a Target, with a 0.25 second
  Cooldown [h]
- **Witchfire Brew**: Grants Level {0} Despair Curse Aura during Effect [h]
- **Worldcarver**: {0}% chance to Trigger Level 20 Arcane Wake after Spending a total of 200 Mana [h]
- **Wreath of Phrecia**: Increases and Reductions to Light Radius also apply to Damage [m]; Increases and Reductions
  to Light Radius also apply to Area of Effect at {0}% of their value [h]
- **Wyrmsign**: Lose all Endurance Charges when Rampage ends [m]; Gain Rampage while at Maximum Endurance Charges [h]
- **Xoph's Inception**: Projectiles Pierce all Burning Enemies [h]; Arrows deal {0} to {1} Added Fire Damage for each
  time they've Pierced [h]; Arrows deal {0} to {1} Added Fire Damage for each time they've Pierced [h]
- **Yriel's Fostering**: Projectile Attack Skills have {0}% increased Critical Strike Chance [h]

## Needs a whole mechanic (15 uniques)

- **Belt of the Deceiver**:  [x]
- **Cane of Kulemak**: {0:+d}% Critical Strike Multiplier while a Rare or Unique Enemy is Nearby [e]; {0}% increased
  Attack Speed while a Rare or Unique Enemy is Nearby [e]; {0}% chance to deal Double Damage while Focused [e]; {0}%
  increased Unveiled Modifier magnitudes [m]; Chaos Skills have {0}% increased Skill Effect Duration [h];  [x]
- **Hyperboreus**: Chill nearby Enemies when you Focus, causing 30% reduced Action Speed [h];  [x]
- **Lightbane Raiment**: {0}% chance to create Desecrated Ground when you Block [h];  [x];  [x];  [x]
- **Matua Tupuna**: {0}% increased effect of Non-Curse Auras from your Skills on your Minions [h]; Spreads Tar when
  you Block [h];  [x]
- **Paradoxica**: {0:+d}% Critical Strike Multiplier while a Rare or Unique Enemy is Nearby [e]; {0}% increased Attack
  Speed while a Rare or Unique Enemy is Nearby [e]; {0}% chance to deal Double Damage while Focused [e]; Chaos Skills
  have {0}% increased Skill Effect Duration [h];  [x]
- **Sign of the Sin Eater**: Enemies inflict Elemental Ailments on you instead of nearby Allies [m];  [x]
- **Singular Focus**:  [x];  [x]
- **Svalinn**: Trigger a Socketed Elemental Spell on Block, with a {0} second Cooldown [m];  [x]
- **The Devouring Diadem**: Take no Extra Damage from Critical Strikes if Energy Shield Recharge started Recently [e];
  Trigger Level {0} Feast of Flesh every 5 seconds [m]; {0}% chance for Energy Shield Recharge to start when you use a
  Skill [m]; Corpses you Spawn have {0}% increased Maximum Life [m]; {0}% additional Physical Damage Reduction while
  Focused [m]; {0}% increased Evasion Rating while Focused [m];  [x];  [x]
- **The Iron Mass**: Unholy Might [m];  [x]
- **The Whispering Ice**:  [x]
- **Twist of Fate**:  [x]
- **Whakatutuki o Matua**: {0}% increased effect of Non-Curse Auras from your Skills on your Minions [h]; Spreads Tar
  when you Block [h]; If you have Blocked Recently, you and nearby Allies Regenerate {0}% of Life per second [h];  [x]
- **Zahndethus' Cassock**: {0}% chance to create Consecrated Ground when you Block [h];  [x];  [x];  [x]
