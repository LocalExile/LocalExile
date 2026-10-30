# Orb of Alchemy: CoE comparison

## Scope

Orb of Alchemy already used the shared 0x71/0x72 currency path.
This change adds a validated `ItemRolls.Alchemy` entry point and aligns the shared rare affix-count table with CoE.
`CurrencyEffects.Alchemy` delegates to it.
All modifier data remains from the current client's extraction pipeline.

Admission requires a normal, non-corrupted, non-mirrored item with Mods and a known, serializable base with Mods that is
not `Unmodifiable`, plus a non-empty ordinary pool.
Flasks are magic-only and cannot become rare.
A normal item whose pool is empty, and missing data, are refused before the Gateway spends an orb.

## Pinned reference

Queried the external library's beta engine, dataset `3.29.3.1.2`, combined snapshot
`20261024T171803Z-combined-3.29.3.1.2`.
Exact correspondence to official 3.29.3b is explicitly unverified by the source.

| Asset | SHA256 |
|---|---|
| `package.js` | `09f9e940bdfbb38e1edc3af84d6008cc1c3758408dbb35504eb70e0c18fccf41` |
| `data.js` | `280792696ffa6700d389012accd357d9a708f8b7adc0bcb5ca42fe544f6a8489` |
| `extended.js` | `5bb7c9ad296397a565c141f362cbbe54a9316aa56cd16018986b19234e7ffab9` |

The method tree lists Alchemy under `Currencies` with the parent constraints `not_graft_base` and `is_modifiable`,
plus `rarity_normal` and `can_be_rare`.
Its handler rolls a count from the base's maximum and rerolls every explicit affix:
`alchemy(e = new rollAffixParameters) { let t = getMaximumAffixesForBase(item); let a = generateNumberOfAffixes(t, item);
item.rarity = RARE; rerollAffixes(a, e); return true; }`.

The beta counts table is:

```text
affixNumberGeneration[6] = [2/3, 1/4, 1/12]   // 4, 5 or 6 affixes
affixNumberGeneration[4] = [.65, .35]          // 3 or 4 affixes
```

Classes with `affixes = 4` include the jewel classes (`Jewel`, `AbyssJewel` locally); their rare items therefore get
three (65%) or four (35%) affixes, with at most two prefixes and two suffixes.
The default for equipment is 6, with at most three of each side.

## Apply results

300 `apply` runs on a normal level-1 Driftwood Wand, seeds 1..300: 211 items with four affixes, 63 with five and 26 with
six (70.3%, 21.0%, 8.7%).
Against the table's 66.7%, 25% and 8.3%, the deviations are 1.35, 1.6 and 0.25 standard errors; the sample supports the
source table, which is the authority for the local `Roll` change.
No item had more than three prefixes or three suffixes.

A normal Small Life Flask was upgraded by CoE to a rare flask with one prefix.
The official game has no rare flasks; LocalExile refuses any item with a `Flask` component and does not spend the orb.
A magic or rare target is refused with `Invalid crafting setup: ["Item is not normal"]`.
CoE also upgrades a normal Sanctuary Map despite its `unmodifiable` flag; LocalExile refuses it by policy.

## Shared generator change

`ItemRolls.Roll` now uses the CoE table for every rare roll, not just Alchemy:
4 affixes 2/3, 5 affixes 1/4, 6 affixes 1/12, and jewel classes 3/4 with at most 2 per side.
This affects Chaos, Vaal, rare drops and other callers of the shared generator.
A full per-class audit beyond jewels (for example special legacy classes) is not part of this change; the bounded
comparison here is ordinary equipment and jewels.

## Automated coverage

`AlchemyTests` checks the 2/3–1/4–1/12 count distribution over 20,000 rolls, the jewel 3/4 distribution with two per
side over 500 rolls, the wand/map/ring/body-armour bases, rare names, weighted values and families, preserved
implicits/quality/sockets/links/charges, flask and unmodifiable refusal, unsupported/no-Mods/unknown and empty-pool
bases, corrupted/mirrored and non-normal items.
`AlchemyGatewayTests` sends framed 0x71 and 0x72 messages through `Program.OnClientMessages`: inventory and stash
currency/target combinations, exactly one orb spent, saved mods and name after cache eviction, refused second use, and
flask/normal/magic/unmodifiable refusal without spending.

No fresh real-client run was made for this change yet.
The old live check of the unvalidated path (2026-10-21) is not evidence for the new admission rules or the changed count
table; native re-acceptance remains to be recorded.
