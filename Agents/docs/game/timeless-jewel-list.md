# Timeless jewels: full list

Generated from the target client's `AlternatePassiveSkills`, `AlternatePassiveAdditions` and `Mods.json`, with names and effect text from Path of Building's `LegionPassives.lua` and `stat_descriptions.lua`. `works` means every stat has a server consumer and is unconditional; `partial` means a condition is not modelled or some stats are unread; `inert` means no stat has a consumer. Keystone rows are applied as stats, but their special gameplay mechanics are separate. The additions section covers all 96 rows for the six timeless factions, including the four that Path of Building's `LegionPassives.lua` no longer lists; the client table's other 241 addition rows belong to the abyss jewels and are out of scope here.

See [timeless-jewels](timeless-jewels.md) for the item, seed/conqueror and radius rules.

## Keystones (23 rows; 18 current conquerors + 5 legacy)

| Faction | Conqueror | Index/version | Keystone | Effect | Server |
|---|---|---|---|---|---|
| Vaal | Xibaqua | 1/0 | **Divine Flesh** | All Damage taken bypasses Energy Shield; 50% of Elemental Damage taken as Chaos Damage; +5% to maximum Chaos Resistance | stat row applied; mechanic not modelled |
| Vaal | Zerphi (legacy) | 2/0 | **Eternal Youth** | 50% less Life Regeneration Rate; 50% less maximum Total Life Recovery per Second from Leech; Energy Shield Recharge instead applies to Life | stat row applied; mechanic not modelled; not creatable |
| Vaal | Ahuana | 2/1 | **Immortal Ambition** | Energy Shield starts at zero; Cannot Recharge or Regenerate Energy Shield; Lose 5% of Energy Shield per second; Life Leech effects are not removed when Unreserved Life is Filled; Life Leech effects Recover Energy Shield instead while on Full Life | stat row applied; mechanic not modelled |
| Vaal | Doryani | 3/0 | **Corrupted Soul** | 50% of Non-Chaos Damage taken bypasses Energy Shield; Gain 15% of Maximum Life as Extra Maximum Energy Shield | stat row applied; mechanic not modelled |
| Karui | Kaom | 1/0 | **Strength of Blood** | Life Recovery from Non-Instant Leech is not applied; 2% additional Physical Damage Reduction for every 3% Life Recovery per second from Leech | stat row applied; mechanic not modelled |
| Karui | Rakiata | 2/0 | **Tempered by War** | 50% of Cold and Lightning Damage taken as Fire Damage; 50% less Cold Resistance; 50% less Lightning Resistance | stat row applied; mechanic not modelled |
| Karui | Kiloava (legacy) | 3/0 | **Glancing Blows** | Chance to Block Attack Damage is doubled; Chance to Block Spell Damage is doubled; You take 65% of Damage from Blocked Hits | stat row applied; mechanic not modelled; not creatable |
| Karui | Akoya | 3/1 | **Chainbreaker** | Mana Recovery from Regeneration is not applied; 1 Rage Regenerated for every 25 Mana Regeneration per Second; Does not delay Inherent Loss of Rage; Skills Cost +3 Rage | stat row applied; mechanic not modelled |
| Maraketh | Deshret (legacy) | 1/0 | **Wind Dancer** | 20% less Attack Damage taken if you haven't been Hit by an Attack Recently; 10% more chance to Evade Attacks if you have been Hit by an Attack Recently; 20% more Attack Damage taken if you have been Hit by an Attack Recently | stat row applied; mechanic not modelled; not creatable |
| Maraketh | Balbala | 1/1 | **The Traitor** | Flasks Gain 4 Charges per empty Flask Slot every 5 seconds | stat row applied; mechanic not modelled |
| Maraketh | Asenath | 2/0 | **Dance with Death** | Can't use Helmets; Your Critical Strike Chance is Lucky; Your Damage with Critical Strikes is Lucky; Enemies' Damage with Critical Strikes against you is Lucky | stat row applied; mechanic not modelled |
| Maraketh | Nasima | 3/0 | **Second Sight** | You are Blind; Blind does not affect your Light Radius; 25% more Melee Critical Strike Chance while Blinded | stat row applied; mechanic not modelled |
| Templar | Venarius (legacy) | 1/0 | **The Agnostic** | Removes all Energy Shield; While not on Full Life, Sacrifice 20% of Mana per Second to Recover that much Life | stat row applied; mechanic not modelled; not creatable |
| Templar | Maxarius | 1/1 | **Transcendence** | Armour applies to Fire, Cold and Lightning Damage taken from Hits instead of Physical Damage; -15% to all maximum Elemental Resistances | stat row applied; mechanic not modelled |
| Templar | Dominus | 2/0 | **Inner Conviction** | 3% more Spell Damage per Power Charge; Gain Power Charges instead of Frenzy Charges | stat row applied; mechanic not modelled |
| Templar | Avarius | 3/0 | **Power of Purpose** | 80% of Maximum Mana is Converted to twice that much Armour | stat row applied; mechanic not modelled |
| Eternal | Cadiro | 1/0 | **Supreme Decadence** | Life Recovery from Flasks also applies to Energy Shield; 30% less Life Recovery from Flasks | stat row applied; mechanic not modelled |
| Eternal | Victario | 2/0 | **Supreme Grandstanding** | Nearby Allies and Enemies Share Charges with you; Enemies Hitting you have 10% chance to gain an Endurance, ; Frenzy or Power Charge | stat row applied; mechanic not modelled |
| Eternal | Chitus (legacy) | 3/0 | **Supreme Ego** | Auras from your Skills can only affect you; Aura Skills have 1% more Aura Effect per 2% of maximum Mana they Reserve; 40% more Mana Reservation of Aura Skills | stat row applied; mechanic not modelled; not creatable |
| Eternal | Caspiro | 3/1 | **Supreme Ostentation** | Ignore Attribute Requirements; Gain no inherent bonuses from Attributes | stat row applied; mechanic not modelled |
| Kalguur | Vorana | 1/0 | **Black Scythe Training** | Removes all Energy Shield; Chance to Evade Hits is based off of 200% of your Ward instead of your Evasion Rating; Physical Damage Reduction from Hits is based off of 200% of your Ward instead of your Armour | stat row applied; mechanic not modelled |
| Kalguur | Uhtred | 2/0 | **Celestial Mathematics** | While you have Unbroken Ward, your next non-channelling Attack you Use yourself breaks your Ward to gain Added Cold Damage equal to 25% of Ward | stat row applied; mechanic not modelled |
| Kalguur | Medved | 3/0 | **The Unbreaking Circle** | 40% less Ward; Ward has a 60% chance to not Break | stat row applied; mechanic not modelled |

## Notables (119 rows)

| Faction | Notable | Effect | Server |
|---|---|---|---|
| Vaal | **Ritual of Immolation** | (25-35)% increased Fire Damage; Damage Penetrates (2-4)% Fire Resistance | partial (1/2 stats read) |
| Vaal | **Revitalising Flames** | (25-35)% increased Fire Damage; 0.2% of Fire Damage Leeched as Life | inert (no stat read) |
| Vaal | **Flesh to Flames** | (25-35)% increased Fire Damage; 10% of Physical Damage Converted to Fire Damage | inert (no stat read) |
| Vaal | **Ritual of Stillness** | (25-35)% increased Cold Damage; Damage Penetrates (2-4)% Cold Resistance | works |
| Vaal | **Revitalising Frost** | (25-35)% increased Cold Damage; 0.2% of Cold Damage Leeched as Life | partial (1/2 stats read); conditional |
| Vaal | **Flesh to Frost** | (25-35)% increased Cold Damage; 10% of Physical Damage Converted to Cold Damage | partial (1/2 stats read) |
| Vaal | **Ritual of Thunder** | (25-35)% increased Lightning Damage; Damage Penetrates (2-4)% Lightning Resistance | works |
| Vaal | **Revitalising Lightning** | (25-35)% increased Lightning Damage; 0.2% of Lightning Damage Leeched as Life | partial (1/2 stats read); conditional |
| Vaal | **Flesh to Lightning** | (25-35)% increased Lightning Damage; 10% of Physical Damage Converted to Lightning Damage | partial (1/2 stats read) |
| Vaal | **Ritual of Might** | (2-4)% chance to deal Double Damage; (25-35)% increased Physical Damage | partial (1/2 stats read) |
| Vaal | **Revitalising Winds** | 0.2% of Physical Damage Leeched as Life; (25-35)% increased Physical Damage | partial (1/2 stats read); conditional |
| Vaal | **Bloody Savagery** | Bleeding you inflict deals Damage 10% faster; (25-35)% increased Physical Damage | partial (1/2 stats read) |
| Vaal | **Ritual of Shadows** | (25-35)% increased Chaos Damage; 25% chance to inflict Withered for 2 seconds on Hit | inert (no stat read) |
| Vaal | **Revitalising Darkness** | (25-35)% increased Chaos Damage; 0.2% of Chaos Damage Leeched as Life | inert (no stat read) |
| Vaal | **Thaumaturgical Aptitude** | (25-35)% increased Spell Damage; (35-50)% increased Spell Critical Strike Chance | works |
| Vaal | **Hierarchy** | Minions have (15-20)% increased maximum Life; Minions deal (25-35)% increased Damage | partial (1/2 stats read) |
| Vaal | **Exquisite Pain** | (25-35)% increased Damage over Time; (7-11)% increased Skill Effect Duration | partial (1/2 stats read) |
| Vaal | **Ritual of Flesh** | (6-10)% increased maximum Life; Regenerate (0.7-1.2)% of Life per second | partial (2/2 stats read); conditional |
| Vaal | **Flesh Worship** | (6-10)% increased maximum Life; 0.4% of Attack Damage Leeched as Life | partial (1/2 stats read); conditional |
| Vaal | **Ritual of Memory** | (17-23)% increased maximum Mana; (15-25)% increased Mana Regeneration Rate | works |
| Vaal | **Automaton Studies** | (30-40)% increased Armour; (3-4)% additional Physical Damage Reduction | works |
| Vaal | **Construct Studies** | (30-40)% increased Evasion Rating; (5-7)% chance to Blind Enemies on Hit | partial (2/2 stats read); conditional |
| Vaal | **Energy Flow Studies** | (8-12)% increased maximum Energy Shield; (10-15)% increased Energy Shield Recharge Rate | works |
| Vaal | **Soul Worship** | (8-12)% increased maximum Energy Shield; 0.3% of Spell Damage Leeched as Energy Shield | partial (1/2 stats read); conditional |
| Vaal | **Blood-Quenched Bulwark** | (6-10) Life gained when you Block; +8% Chance to Block Attack Damage | partial (2/2 stats read); conditional |
| Vaal | **Thaumaturgical Protection** | 8% Chance to Block Spell Damage; (20-30)% increased Defences from Equipped Shield | partial (1/2 stats read) |
| Vaal | **Jungle Paths** | (8-10)% chance to Avoid Elemental Ailments; (8-10)% chance to Avoid being Stunned | works |
| Vaal | **Temple Paths** | +6% chance to Suppress Spell Damage; +(8-10)% to all Elemental Resistances | works |
| Vaal | **Commanding Presence** | 20% increased Area of Effect of Aura Skills; (7-10)% increased effect of Non-Curse Auras from your Skills | works |
| Vaal | **Ancient Hex** | (4-6)% increased Effect of your Curses; Curse Skills have 20% increased Skill Effect Duration | partial (1/2 stats read) |
| Vaal | **Cult of Fire** | +1% to maximum Fire Resistance; +(20-30)% to Fire Resistance | works |
| Vaal | **Cult of Ice** | +1% to maximum Cold Resistance; +(20-30)% to Cold Resistance | works |
| Vaal | **Cult of Lightning** | +1% to maximum Lightning Resistance; +(20-30)% to Lightning Resistance | works |
| Vaal | **Cult of Chaos** | +1% to maximum Chaos Resistance; +(13-19)% to Chaos Resistance | works |
| Vaal | **Might of the Vaal** |  | inert (no stats) |
| Vaal | **Legacy of the Vaal** |  | inert (no stats) |
| Templar | **Heated Devotion** | 15% of Physical Damage Converted to Fire Damage while you have at least 150 Devotion | inert (no stat read) |
| Templar | **Calming Devotion** | 15% of Physical Damage Converted to Cold Damage while you have at least 150 Devotion | inert (no stat read) |
| Templar | **Thundrous Devotion** | 15% of Physical Damage Converted to Lightning Damage while you have at least 150 Devotion | inert (no stat read) |
| Templar | **Thoughts and Prayers** | Gain 5% of Maximum Mana as Extra Maximum Energy Shield while you have at least 150 Devotion | inert (no stat read) |
| Templar | **Zealot** | Gain Arcane Surge on Hit with Spells if you have at least 150 Devotion | inert (no stat read) |
| Templar | **Enduring Faith** | +1 to Minimum Endurance Charges while you have at least 150 Devotion | inert (no stat read) |
| Templar | **Powerful Faith** | +1 to Minimum Power Charges while you have at least 150 Devotion | inert (no stat read) |
| Templar | **Frenzied Faith** | +1 to Minimum Frenzy Charges while you have at least 150 Devotion | inert (no stat read) |
| Templar | **Cloistered** | Immune to Elemental Ailments while on Consecrated Ground if you have at least 150 Devotion | inert (no stat read) |
| Templar | **Martyr's Might** | 5% additional Physical Damage Reduction while you have at least 150 Devotion | inert (no stat read) |
| Templar | **Intolerance of Sin** | +1% to all maximum Resistances if you have at least 150 Devotion | inert (no stat read) |
| Templar | **Smite the Wicked** | 10% chance to inflict Fire Exposure on Hit if you have at least 150 Devotion | inert (no stat read) |
| Templar | **Smite the Ignorant** | 10% chance to inflict Cold Exposure on Hit if you have at least 150 Devotion | inert (no stat read) |
| Templar | **Smite the Heretical** | 10% chance to inflict Lightning Exposure on Hit if you have at least 150 Devotion | inert (no stat read) |
| Eternal | **Flawless Execution** | 80% increased Critical Strike Chance | works |
| Eternal | **Brutal Execution** | +40% to Critical Strike Multiplier | works |
| Eternal | **Eternal Resilience** | Gain 1 Endurance Charge every second if you've been Hit Recently | partial (1/1 stats read); conditional |
| Eternal | **Eternal Fortitude** | 8% increased Armour per Endurance Charge | partial (1/1 stats read); conditional |
| Eternal | **Eternal Dominance** | 10% increased Damage per Endurance Charge | partial (1/1 stats read); conditional |
| Eternal | **Eternal Fervour** | 10% chance to gain a Frenzy Charge on Hit | partial (1/1 stats read); conditional |
| Eternal | **Eternal Adaptiveness** | 8% increased Evasion Rating per Frenzy Charge | partial (1/1 stats read); conditional |
| Eternal | **Eternal Bloodlust** | 10% increased Damage per Frenzy Charge | partial (1/1 stats read); conditional |
| Eternal | **Eternal Subjugation** | 15% chance to gain a Power Charge on Critical Strike | partial (1/1 stats read); conditional |
| Eternal | **Eternal Separation** | 4% increased Energy Shield per Power Charge | partial (1/1 stats read); conditional |
| Eternal | **Eternal Exploitation** | 10% increased Damage per Power Charge | partial (1/1 stats read); conditional |
| Eternal | **Rites of Lunaris** | 30% increased Effect of Chill | works |
| Eternal | **Rites of Solaris** | 80% chance to Avoid being Chilled | works |
| Eternal | **Virtue Gem Surgery** | 30% increased Effect of Shock | works |
| Eternal | **Rural Life** | 80% chance to Avoid being Shocked | works |
| Eternal | **City Walls** | +12% Chance to Block Attack Damage | works |
| Eternal | **Sceptre Pinnacle** | 12% Chance to Block Spell Damage | works |
| Eternal | **Secret Tunnels** | 20% chance to Avoid Elemental Ailments | works |
| Eternal | **Purity Rebel** | +12% chance to Suppress Spell Damage | works |
| Eternal | **Superiority** | 12% increased effect of Non-Curse Auras from your Skills | works |
| Eternal | **Slum Lord** | Minions deal 80% increased Damage | works |
| Eternal | **Axiom Warden** | Minions have 80% increased maximum Life | inert (no stat read) |
| Eternal | **Gemling Inquisition** | 80% increased Spell Damage | works |
| Eternal | **Gemling Ambush** | 80% increased Spell Critical Strike Chance | works |
| Eternal | **Night of a Thousand Ribbons** | 80% increased Fire Damage with Attack Skills | inert (no stat read) |
| Eternal | **Bloody Flowers' Rebellion** | 80% increased Cold Damage with Attack Skills | inert (no stat read) |
| Eternal | **Chitus' Heart** | 80% increased Lightning Damage with Attack Skills | inert (no stat read) |
| Eternal | **Gemling Training** | 80% increased Physical Damage | works |
| Eternal | **Rigwald's Might** | 80% increased Melee Physical Damage | works |
| Eternal | **Geofri's End** | 50% increased Damage with Bleeding; Bleeding you inflict deals Damage 10% faster | inert (no stat read) |
| Eternal | **Lioneye's Focus** | 80% increased Projectile Attack Damage | inert (no stat read) |
| Eternal | **Voll's Coup** | 15% increased Attack Speed | works |
| Eternal | **Dialla's Wit** | 15% increased Cast Speed | works |
| Eternal | **Discerning Taste** | 12% increased Mana Reservation Efficiency of Skills | works |
| Eternal | **Gleaming Legion** | 80% increased Armour | works |
| Eternal | **Shadowy Streets** | 80% increased Evasion Rating | works |
| Eternal | **Crematorium Worker** | +50% to Fire Resistance | works |
| Eternal | **Street Urchin** | +50% to Cold Resistance | works |
| Eternal | **Baleful Augmentation** | +50% to Lightning Resistance | works |
| Eternal | **With Eyes Open** | +37% to Chaos Resistance | works |
| Eternal | **Robust Diet** | 10% increased maximum Life | works |
| Eternal | **Pooled Resources** | 30% increased maximum Mana | works |
| Eternal | **Laureate** | 50% increased Mana Regeneration Rate | works |
| Eternal | **War Games** | 25% increased Accuracy Rating | works |
| Eternal | **Freshly Brewed** | 20% increased Flask Effect Duration | works |
| Kalguur | **Warding Flasks** | 10% increased Flask Effect Duration; 20% increased Ward during any Flask Effect | partial (1/2 stats read) |
| Kalguur | **Starlight Swig** | 10% increased Flask Charges gained; 15% chance for Ward to be Restored when you Use a Flask | inert (no stat read) |
| Kalguur | **Enduring Sigil** | 20% increased Endurance Charge Duration; Gain 1 Endurance Charge when Ward Breaks | partial (1/2 stats read); conditional |
| Kalguur | **Powering Sigil** | 20% increased Power Charge Duration; Gain 1 Power Charge when Ward Breaks | partial (1/2 stats read); conditional |
| Kalguur | **Frenzying Sigil** | 20% increased Frenzy Charge Duration; Gain 1 Frenzy Charge when Ward Breaks | partial (1/2 stats read); conditional |
| Kalguur | **Fortified Ward** | 15% faster Restoration of Ward; 30% increased Armour while you have Unbroken Ward | partial (1/2 stats read); conditional |
| Kalguur | **Elusive Ward** | 15% faster Restoration of Ward; 30% increased Evasion Rating while you have Unbroken Ward | partial (1/2 stats read); conditional |
| Kalguur | **Crown of Kalguur** | +2 to Ward per 10 Armour on Equipped Helmet | inert (no stat read) |
| Kalguur | **Grip of Kalguur** | +4 to Ward per 10 Evasion Rating on Equipped Gloves | inert (no stat read) |
| Kalguur | **Stride of Kalguur** | +8 to Ward per 10 Energy Shield on Equipped Boots | inert (no stat read) |
| Kalguur | **Persisting Drive** | 15% faster Restoration of Ward; 30% increased Damage while you have Unbroken Ward | partial (1/2 stats read); conditional |
| Kalguur | **Shattered Piercing ** | 15% increased Elemental Damage; Damage Penetrates 8% Elemental Resistances while your Ward is Broken | partial (1/2 stats read); conditional |
| Kalguur | **Vengeful Runes** | 8% increased Area of Effect of Aura Skills; 12% increased effect of Non-Curse Auras from your skills while your Ward is Broken | partial (1/2 stats read); conditional |
| Kalguur | **Mage's Ward** | 5% Chance to Block Spell Damage; 10% chance to Restore Ward when you Block | partial (1/2 stats read); conditional |
| Kalguur | **Warrior's Ward** | 5% Chance to Block Attack Damage; 10% chance to Restore Ward when you Block | partial (1/2 stats read); conditional |
| Kalguur | **Starborn Birth** | 25% increased Ward | works |
| Kalguur | **Pure Faith** | 12% increased Ward; +8% to Chaos Resistance | works |
| Kalguur | **Magic Faith** | 12% increased Ward; 8% increased maximum Energy Shield | works |
| Kalguur | **Guile Faith** | 12% increased Ward; 16% increased Evasion Rating | works |
| Kalguur | **Resolute Faith** | 12% increased Ward; 16% increased Armour | works |
| Kalguur | **Starsoaked Time** | 20% faster Restoration of Ward; 20% faster start of Energy Shield Recharge | works |
| Kalguur | **Etched Runes** | 12% increased Ward; 20% increased Ward from Equipped Armour Items | partial (1/2 stats read); conditional |
| Kalguur | **Cleansing Runes** | Remove a random Damaging Ailment when Ward is Restored | inert (no stat read) |
| Kalguur | **Regenerative Runes** | 10% increased Flask Effect Duration; Iron Flasks gain 2 charges when your Ward Breaks | partial (1/2 stats read); conditional |

## Small passives (40 replacement rows)

| Faction | Passive | Effect | Server |
|---|---|---|---|
| Vaal | **Fire Damage** | (7-12)% increased Fire Damage | inert (no stat read) |
| Vaal | **Cold Damage** | (7-12)% increased Cold Damage | works |
| Vaal | **Lightning Damage** | (7-12)% increased Lightning Damage | works |
| Vaal | **Physical Damage** | (7-12)% increased Physical Damage | works |
| Vaal | **Chaos Damage** | (7-12)% increased Chaos Damage | inert (no stat read) |
| Vaal | **Minion Damage** | Minions deal (8-13)% increased Damage | works |
| Vaal | **Attack Damage** | (7-12)% increased Attack Damage | works |
| Vaal | **Spell Damage** | (7-12)% increased Spell Damage | works |
| Vaal | **Area Damage** | (7-12)% increased Area Damage | works |
| Vaal | **Projectile Damage** | (7-12)% increased Projectile Damage | works |
| Vaal | **Damage Over Time** | (7-12)% increased Damage over Time | inert (no stat read) |
| Vaal | **Area of Effect** | (4-7)% increased Area of Effect | works |
| Vaal | **Projectile Speed** | (7-12)% increased Projectile Speed | works |
| Vaal | **Critical Strike Chance** | (7-14)% increased Critical Strike Chance | works |
| Vaal | **Critical Strike Multiplier** | +(6-10)% to Critical Strike Multiplier | works |
| Vaal | **Attack Speed** | (3-4)% increased Attack Speed | works |
| Vaal | **Cast Speed** | (2-3)% increased Cast Speed | works |
| Vaal | **Movement Speed** | (2-3)% increased Movement Speed | works |
| Vaal | **Ignite Chance** | (3-6)% chance to Ignite | works |
| Vaal | **Freeze Chance** | (3-6)% chance to Freeze | works |
| Vaal | **Shock Chance** | (3-6)% chance to Shock | works |
| Vaal | **Skill Duration** | (4-7)% increased Skill Effect Duration | works |
| Vaal | **Life** | (2-4)% increased maximum Life | works |
| Vaal | **Mana** | (4-6)% increased maximum Mana | works |
| Vaal | **Mana Regeneration** | (12-17)% increased Mana Regeneration Rate | works |
| Vaal | **Armour** | (7-12)% increased Armour | works |
| Vaal | **Evasion** | (7-12)% increased Evasion Rating | works |
| Vaal | **Energy Shield** | (3-5)% increased maximum Energy Shield | works |
| Vaal | **Block** | +2% Chance to Block Attack Damage | works |
| Vaal | **Spell Block** | 2% Chance to Block Spell Damage | works |
| Vaal | **Avoid Elemental Ailments** | 3% chance to Avoid Elemental Ailments | works |
| Vaal | **Spell Suppression** | +4% chance to Suppress Spell Damage | works |
| Vaal | **Aura Effect** | (2-4)% increased effect of Non-Curse Auras from your Skills | works |
| Vaal | **Curse Effect** | 2% increased Effect of your Curses | works |
| Vaal | **Fire Resistance** | +(9-14)% to Fire Resistance | works |
| Vaal | **Cold Resistance** | +(9-14)% to Cold Resistance | works |
| Vaal | **Lightning Resistance** | +(9-14)% to Lightning Resistance | works |
| Vaal | **Chaos Resistance** | +(6-10)% to Chaos Resistance | works |
| Templar | **Devotion** | +10 to Devotion | works |
| Eternal | **Price of Glory** |  | inert (no stats) |

## Additions (96 rows)

These are the extra stats the client adds to a node that was not replaced (and the extra rolls on some Vaal notables). Four rows are in the client table but not in PoB's reference list; their row IDs are legacy names, so the effect below comes from the actual `Stats.dat` row (`karui_notable_add_physical_taken_as_fire` is Rage on Melee Hit, `maraketh_notable_add_ailment_avoid` is avoid Stun). The client table's other 241 addition rows are for the abyss jewels and are not part of the six timeless jewels.

| Faction | Addition | Effect | Server |
|---|---|---|---|
| Vaal | **Fire Damage** (type 3) | (7-12)% increased Fire Damage | inert (no stat read) |
| Vaal | **Cold Damage** (type 3) | (7-12)% increased Cold Damage | works |
| Vaal | **Lightning Damage** (type 3) | (7-12)% increased Lightning Damage | works |
| Vaal | **Physical Damage** (type 3) | (7-12)% increased Physical Damage | works |
| Vaal | **Chaos Damage** (type 3) | (7-12)% increased Chaos Damage | inert (no stat read) |
| Vaal | **Minion Damage** (type 3) | Minions deal (8-13)% increased Damage | works |
| Vaal | **Attack Damage** (type 3) | (7-12)% increased Attack Damage | works |
| Vaal | **Spell Damage** (type 3) | (7-12)% increased Spell Damage | works |
| Vaal | **Area Damage** (type 3) | (7-12)% increased Area Damage | works |
| Vaal | **Projectile Damage** (type 3) | (7-12)% increased Projectile Damage | works |
| Vaal | **Damage Over Time** (type 3) | (7-12)% increased Damage over Time | inert (no stat read) |
| Vaal | **Area Of Effect** (type 3) | (4-7)% increased Area of Effect | works |
| Vaal | **Projectile Speed** (type 3) | (7-12)% increased Projectile Speed | works |
| Vaal | **Critical Strike Chance** (type 3) | (7-14)% increased Critical Strike Chance | works |
| Vaal | **Critical Strike Multiplier** (type 3) | +(6-10)% to Critical Strike Multiplier | works |
| Vaal | **Attack Speed** (type 3) | (3-4)% increased Attack Speed | works |
| Vaal | **Cast Speed** (type 3) | (2-3)% increased Cast Speed | works |
| Vaal | **Movement Speed** (type 3) | (2-3)% increased Movement Speed | works |
| Vaal | **Chance To Ignite** (type 3) | (3-6)% chance to Ignite | works |
| Vaal | **Chance To Freeze** (type 3) | (3-6)% chance to Freeze | works |
| Vaal | **Chance To Shock** (type 3) | (3-6)% chance to Shock | works |
| Vaal | **Duration** (type 3) | (4-7)% increased Skill Effect Duration | works |
| Vaal | **Life** (type 3) | (2-4)% increased maximum Life | works |
| Vaal | **Mana** (type 3) | (4-6)% increased maximum Mana | works |
| Vaal | **Mana Regeneration** (type 3) | (12-17)% increased Mana Regeneration Rate | works |
| Vaal | **Armour** (type 3) | (7-12)% increased Armour | works |
| Vaal | **Evasion** (type 3) | (7-12)% increased Evasion Rating | works |
| Vaal | **Energy Shield** (type 3) | (3-5)% increased maximum Energy Shield | works |
| Vaal | **Attack Block** (type 3) | +2% Chance to Block Attack Damage | works |
| Vaal | **Spell Block** (type 3) | 2% Chance to Block Spell Damage | works |
| Vaal | **Attack Dodge** (type 3) | 3% chance to Avoid Elemental Ailments | works |
| Vaal | **Spell Dodge** (type 3) | +4% chance to Suppress Spell Damage | works |
| Vaal | **Aura Effect** (type 3) | (2-4)% increased effect of Non-Curse Auras from your Skills | works |
| Vaal | **Curse Effect** (type 3) | 2% increased Effect of your Curses | works |
| Vaal | **Fire Resistance** (type 3) | +(9-14)% to Fire Resistance | works |
| Vaal | **Cold Resistance** (type 3) | +(9-14)% to Cold Resistance | works |
| Vaal | **Lightning Resistance** (type 3) | +(9-14)% to Lightning Resistance | works |
| Vaal | **Chaos Resistance** (type 3) | +(6-10)% to Chaos Resistance | works |
| Karui | **Strength** (type 1) | +2 to Strength | works |
| Karui | **Strength** (type 2) | +4 to Strength | works |
| Karui | **Add Strength** (type 3) | +20 to Strength | works |
| Karui | **Add Percent Strength** (type 3) | 5% increased Strength | works |
| Karui | **Add Armour** (type 3) | 20% increased Armour | works |
| Karui | **Add Leech** (type 3) | 0.4% of Attack Damage Leeched as Life | inert (no stat read) |
| Karui | **Add Double Damage** (type 3) | 5% chance to deal Double Damage | inert (no stat read) |
| Karui | **Add Life** (type 3) | 4% increased maximum Life | works |
| Karui | **Add Fortify Effect** (type 3) | +1 to maximum Fortification | works |
| Karui | **Add Life Regen** (type 3) | Regenerate 1% of Life per second | partial (1/1 stats read); conditional |
| Karui | **Add Fire Resistance** (type 3) | +20% to Fire Resistance | works |
| Karui | **Add Melee Damage** (type 3) | 20% increased Melee Damage | works |
| Karui | **Add Damage From Crits** (type 3) | You take 10% reduced Extra Damage from Critical Strikes | works |
| Karui | **Add Melee Crit Chance** (type 3) | 30% increased Melee Critical Strike Chance | works |
| Karui | **Add Burning Damage** (type 3) | 20% increased Burning Damage | inert (no stat read) |
| Karui | **Add Totem Damage** (type 3) | 20% increased Totem Damage | inert (no stat read) |
| Karui | **Add Melee Crit Multi** (type 3) | +15% to Melee Critical Strike Multiplier | works |
| Karui | **Add Physical Damage** (type 3) | 20% increased Physical Damage | works |
| Karui | **Add Warcry Buff Effect** (type 3) | 8% increased Warcry Buff Effect | works |
| Karui | **Add Totem Placement Speed** (type 3) | 12% increased Totem Placement speed | inert (no stat read) |
| Karui | **Add Stun Duration** (type 3) | 20% increased Stun Duration on Enemies | works |
| Karui | **Add Faster Burn** (type 3) | Ignites you inflict deal Damage 10% faster | inert (no stat read) |
| Karui | **Add Reduced Stun Threshold** (type 3) | 10% reduced Enemy Stun Threshold | works |
| Karui | **Add Physical Added As Fire** (type 3) | Gain 5% of Physical Damage as Extra Fire Damage | works |
| Karui | **Add Physical Taken As Fire** (type 3) | Gain 1 Rage on Melee Hit | works (melee-hit event modelled) |
| Karui | **Add Endurance Charge On Kill** (type 3) | 5% chance to gain an Endurance Charge on Kill | partial (1/1 stats read); conditional |
| Karui | **Add Intimidate** (type 3) | 10% chance to Intimidate Enemies for 4 seconds on Hit | inert (no stat read) |
| Maraketh | **Dex** (type 1) | +2 to Dexterity | works |
| Maraketh | **Dex** (type 2) | +4 to Dexterity | works |
| Maraketh | **Add Dexterity** (type 3) | +20 to Dexterity | works |
| Maraketh | **Add Percent Dexterity** (type 3) | 5% increased Dexterity | works |
| Maraketh | **Add Evasion** (type 3) | 20% increased Evasion Rating | works |
| Maraketh | **Add Flask Charges** (type 3) | 10% increased Flask Charges gained | inert (no stat read) |
| Maraketh | **Add Speed** (type 3) | 5% increased Attack and Cast Speed | works |
| Maraketh | **Add Life** (type 3) | 4% increased maximum Life | works |
| Maraketh | **Add Blind** (type 3) | 5% chance to Blind Enemies on Hit | partial (1/1 stats read); conditional |
| Maraketh | **Add Movement Speed** (type 3) | 5% increased Movement Speed | works |
| Maraketh | **Add Cold Resistance** (type 3) | +20% to Cold Resistance | works |
| Maraketh | **Add Projectile Damage** (type 3) | 20% increased Projectile Damage | works |
| Maraketh | **Add Ailment Avoid** (type 3) | 20% chance to Avoid being Stunned | works |
| Maraketh | **Add Global Crit Chance** (type 3) | 25% increased Critical Strike Chance | works |
| Maraketh | **Add Poison Damage** (type 3) | 20% increased Damage with Poison | inert (no stat read) |
| Maraketh | **Add Minion Damage** (type 3) | Minions deal 20% increased Damage | works |
| Maraketh | **Add Accuracy** (type 3) | 5% increased Accuracy Rating | works |
| Maraketh | **Add Elemental Damage** (type 3) | 20% increased Elemental Damage | works |
| Maraketh | **Add Aura Effect** (type 3) | 8% increased effect of Non-Curse Auras from your Skills | works |
| Maraketh | **Add Minion Movement Speed** (type 3) | Minions have 15% increased Movement Speed | inert (no stat read) |
| Maraketh | **Add Ailment Duration** (type 3) | 10% increased Duration of Elemental Ailments on Enemies | works |
| Maraketh | **Add Faster Poison** (type 3) | Poisons you inflict deal Damage 10% faster | inert (no stat read) |
| Maraketh | **Add Ailment Effect** (type 3) | 10% increased Effect of Non-Damaging Ailments | works |
| Maraketh | **Add Physical Added As Cold** (type 3) | Gain 5% of Physical Damage as Extra Cold Damage | works |
| Maraketh | **Add Flask Effect** (type 3) | 25% chance to gain Alchemist's Genius when you use a Flask | inert (no stat read) |
| Maraketh | **Add Frenzy Charge On Kill** (type 3) | 5% chance to gain a Frenzy Charge on Kill | partial (1/1 stats read); conditional |
| Maraketh | **Add Onslaught** (type 3) | You gain Onslaught for 8 seconds on Kill | inert (no stat read) |
| Templar | **Devotion** (type 2) | +5 to Devotion | works |
| Templar | **Devotion** (type 3) | +5 to Devotion | works |
| Kalguur | **Ward** (type 2) | 2% increased Ward | works |
| Kalguur | **Ward** (type 1) | 1% increased Ward | works |

## Militant Faith devotion mods (16 rows; 15 current + 1 legacy)

Militant Faith is the only timeless jewel with a second random-mod group: each item carries two distinct mods from this pool. The server creates two of the 15 current rows; `ReducedManaCostPerDevotion` is the pre-3.29.0 "reduced Mana Cost of Skills" row and is kept only for legacy items. `DevotionStats` scales each per-10-Devotion stat by `floor(Devotion / 10)` into the normal stat named in the status column; a target with no consumer stays inert.

| Mod | Effect | Server |
|---|---|---|
| `TotemDamagePerDevotion` | 4% increased Totem Damage per 10 Devotion | inert (`totem_damage_+%` has no consumer) |
| `BrandDamagePerDevotion` | 4% increased Brand Damage per 10 Devotion | inert (`sigil_damage_+%` has no consumer) |
| `ChannelledSkillDamagePerDevotion` | Channelling Skills deal 4% increased Damage per 10 Devotion | inert (`channelled_skill_damage_+%` has no consumer) |
| `AreaDamagePerDevotion` | 4% increased Area Damage per 10 Devotion | works (`area_damage_+%`) |
| `ElementalDamagePerDevotion_` | 4% increased Elemental Damage per 10 Devotion | works (`elemental_damage_+%`) |
| `ElementalResistancesPerDevotion` | +2% to all Elemental Resistances per 10 Devotion | works (`base_resist_all_elements_%`) |
| `AilmentEffectPerDevotion` | 3% increased Effect of non-Damaging Ailments on Enemies per 10 Devotion | works (`non_damaging_ailment_effect_+%`, panel) |
| `ElementalAilmentSelfDurationPerDevotion_` | 4% reduced Elemental Ailment Duration on you per 10 Devotion | works (`self_elemental_status_duration_-%`, panel) |
| `CurseSelfDurationPerDevotion` | 4% reduced Duration of Curses on you per 10 Devotion | inert (`self_curse_duration_+%` has no consumer) |
| `MinionAttackAndCastSpeedPerDevotion` | 1% increased Minion Attack and Cast Speed per 10 Devotion | inert (`minion_attack_and_cast_speed_+%` has no consumer) |
| `MinionAccuracyRatingPerDevotion_` | Minions have +60 to Accuracy Rating per 10 Devotion | inert (`minion_accuracy_rating` has no consumer) |
| `AddedManaRegenerationPerDevotion` | Regenerate 0.6 Mana per Second per 10 Devotion | works (`base_mana_regeneration_rate_per_minute`) |
| `ReducedManaCostPerDevotion` | 1% reduced Mana Cost of Skills per 10 Devotion | inert (legacy; `mana_cost_+%` has no consumer) |
| `ManaCostEfficiencyPerDevotion` | 3% increased Mana Cost Efficiency per 10 Devotion | inert (`base_mana_cost_efficiency_+%` has no consumer) |
| `AuraEffectPerDevotion` | 1% increased effect of Non-Curse Auras per 10 Devotion | works (`non_curse_aura_effect_+%`) |
| `ShieldDefencesPerDevotion` | 3% increased Defences from Equipped Shield per 10 Devotion | inert (`combined_shield_defences_+%` has no consumer) |
