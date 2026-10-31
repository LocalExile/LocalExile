# Augmentation: CoE comparison

## Scope

Orb of Augmentation already used the shared 0x71/0x72 currency path.
This change adds a validated `ItemRolls.Augment` entry point and the same admission policy as Transmutation; it is not
a second crafting engine.
`CurrencyEffects.Augmentation` delegates to it.
`ItemRolls.AffixesToAdd` exposes the ordinary weighted pool that `AddAffix` already used, so the comparison below tests
the real filter rather than a copy.
All modifier data remains from the current client's extraction pipeline.

Admission requires a magic, non-corrupted, non-mirrored item with Mods and a known, serializable base with Mods that is
not `Unmodifiable`, plus at least one eligible affix on the open side.
A full item, a base whose open side has no ordinary mods, and missing data are refused before the Gateway spends an orb.

## Pinned reference

Queried the external library's beta engine, dataset `3.29.3.1.2`, combined snapshot
`20261024T171803Z-combined-3.29.3.1.2`.
Exact correspondence to official 3.29.3b is explicitly unverified by the source.

| Asset | SHA256 |
|---|---|
| `package.js` | `09f9e940bdfbb38e1edc3af84d6008cc1c3758408dbb35504eb70e0c18fccf41` |
| `data.js` | `280792696ffa6700d389012accd357d9a708f8b7adc0bcb5ca42fe544f6a8489` |
| `extended.js` | `5bb7c9ad296397a565c141f362cbbe54a9316aa56cd16018986b19234e7ffab9` |

Research requests used the library's persistent JSONL API and the equivalent `craft --file` requests:

```json
{"operation":"pool","engine":"beta","base":"Driftwood Wand","method":"augmentation","ilvl":1,"rarity":"magic","modifiers":[{"id":"mods/1231"}],"limit":100}
{"operation":"pool","engine":"beta","base":"Driftwood Wand","method":"augmentation","ilvl":1,"rarity":"magic","modifiers":[{"id":"mods/2833"}],"limit":100}
{"operation":"apply","engine":"beta","base":"Driftwood Wand","method":"augmentation","ilvl":1,"rarity":"magic","modifiers":[{"id":"mods/1231"},{"id":"mods/2833"}],"seed":7}
{"operation":"simulate","engine":"beta","base":"Driftwood Wand","method":"augmentation","ilvl":1,"rarity":"magic","modifiers":[{"id":"mods/1231"}],"targets":[{"modifier":"mods/184"}],"trials":5000,"seed":11}
```

The method tree lists Augmentation under the `Currencies` parent with `not_graft_base` and `is_modifiable`, and the
method itself adds `rarity_magic`, `open_affix` and `can_be_magic`.
Its handler is one affix: `augment(e = new rollAffixParameters) { this.rollAffixes(1, e); return true; }`.
`rollAffix` takes one weighted draw from `getFilteredModPool`, so prefixes and suffixes compete by weight on the open
side rather than by a separate 50/50 side choice.
This is the same combined-pool model as the local `ItemRolls.AffixesToAdd`/`Pick`.

## Pools

A level-1 Driftwood Wand with the prefix `LocalIncreasedPhysicalDamagePercent1` (`mods/1231`) has exactly six
candidates, all suffixes, total weight 2833:

| Modifier | Weight |
|---|---:|
| `Intelligence1` | 333 |
| `LocalIncreasedAttackSpeed1` | 500 |
| `LifeGainedFromEnemyDeath1` | 500 |
| `ManaGainedFromEnemyDeath1` | 500 |
| `LocalCriticalStrikeChance1` | 500 |
| `LocalIncreasedAccuracyNew1` | 500 |

With the suffix `LocalIncreasedAttackSpeed1` (`mods/2833`) it has exactly seven prefixes, total weight 4650:
`IncreasedManaWeapon1` 1000; `LocalIncreasedPhysicalDamagePercentAndAccuracyRating1` 500;
`LocalIncreasedPhysicalDamagePercent1` 500; `LocalAddedFireDamage1` 250; the three level-9 spell added-damage mods 800
each.
An empty magic wand offers all 13 mods, total weight 7483, the pool already pinned by the Transmutation comparison.

The local regression pins both filtered dictionaries and their totals.
A full magic wand returns an empty pool; CoE refuses it with `Invalid crafting setup: ["Item needs at least 1 open
modifier(s)"]`.
A normal item is refused with `["Item is not magic", "Item needs at least 1 open modifier(s)"]`.

## Simulated probabilities

CoE `simulate` starts each trial from the same magic wand and applies Augmentation.
With the prefix present, the observed target probabilities match the pinned weights (5000 trials, seed 11):

| Target | Weight | Expected | Observed |
|---|---:|---:|---:|
| `Intelligence1` | 333 | 0.1175 | 0.1142 |
| `LocalIncreasedAttackSpeed1` | 500 | 0.1765 | 0.1764 |
| `LifeGainedFromEnemyDeath1` | 500 | 0.1765 | 0.1812 |
| `ManaGainedFromEnemyDeath1` | 500 | 0.1765 | 0.1764 |
| `LocalCriticalStrikeChance1` | 500 | 0.1765 | 0.1736 |
| `LocalIncreasedAccuracyNew1` | 500 | 0.1765 | 0.1782 |

With the suffix present, `IncreasedManaWeapon1` (weight 1000 of 4650) was observed at 0.2226 and
`LocalAddedFireDamage1` (250 of 4650) at 0.0568, against expected 0.2151 and 0.0538.

## Differences kept deliberately

**No-affix targets.** A magic Small Life Flask has two level-1 prefixes and no suffixes. After one prefix, CoE's pool is
empty, but its `augment` ignores the `COULD_NOT_GENERATE_MOD_ERROR` result and still reports success: the `apply`
response spends `CurrencyAddModToMagic` and leaves the item unchanged.
LocalExile returns null from `Augment`, so the Gateway refuses the use and spends nothing.
This is a local safety policy, not parity with that CoE quirk.

**Unmodifiable bases.** CoE's `is_modifiable` checks only `corrupted` and `sanctified`; it does not read the base's
`unmodifiable` flag, and CoE happily augments a magic Sanctuary Map.
The local client marks that base unmodifiable, and Transmutation already refuses it for the same reason.
Augmentation keeps that refusal; it is not evidence that the official servers allow it.

Special CoE class overrides, influences, memory mechanics, fractured treatment beyond the existing `AddAffix` rules and
other crafting systems are not added.
Different RNGs and source data versions mean equal seeds are not expected to produce equal items.

## Automated coverage

`AugmentationTests` checks real level-1 wand pools for the prefix and suffix cases, the full pool, a full item, a
prefix-only flask, unknown/unsupported/no-Mods bases, corrupted and mirrored items, non-magic rarities, unmodifiable
bases, preserved implicits/quality/sockets/links/charges and the unidentified flag.
`AugmentationGatewayTests` sends framed 0x71 and 0x72 messages through `Program.OnClientMessages`: inventory and stash
currency/target combinations, exactly one orb spent, the added affix and saved values after cache eviction, refused
repeat use, refused full/flask/unmodifiable targets without spending.

No fresh real-client run was made for this change yet.
The old live check of the unvalidated path (2026-10-21) is not evidence for the new admission checks; native
re-acceptance remains to be recorded.
