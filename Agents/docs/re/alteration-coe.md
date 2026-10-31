# Alteration: CoE comparison

## Scope

Orb of Alteration already used the shared 0x71/0x72 currency path.
This change adds a validated `ItemRolls.Alter` entry point and the same admission policy as Transmutation,
Augmentation and Regal; it is not a second crafting engine.
`CurrencyEffects.Alteration` delegates to it.
All modifier data remains from the current client's extraction pipeline.

Admission requires a magic, non-corrupted, non-mirrored item with Mods and a known, serializable base with Mods that is
not `Unmodifiable`, plus a non-empty ordinary pool at the item's level.
The empty-pool refusal is LocalExile safety policy: `Roll` would otherwise spend the orb and leave a magic item with no
affixes when the data is missing or the level is too low.

## Pinned reference

Queried the external library's beta engine, dataset `3.29.3.1.2`, combined snapshot
`20261024T171803Z-combined-3.29.3.1.2`.
Exact correspondence to official 3.29.3b is explicitly unverified by the source.

| Asset | SHA256 |
|---|---|
| `package.js` | `09f9e940bdfbb38e1edc3af84d6008cc1c3758408dbb35504eb70e0c18fccf41` |
| `data.js` | `280792696ffa6700d389012accd357d9a708f8b7adc0bcb5ca42fe544f6a8489` |
| `extended.js` | `5bb7c9ad296397a565c141f362cbbe54a9316aa56cd16018986b19234e7ffab9` |

The method tree lists Alteration under `Currencies` with the parent constraints `not_graft_base` and `is_modifiable`,
plus `rarity_magic` and `can_be_magic`.
Its handler rolls a new count and rerolls the explicit affixes:
`alteration(e = new rollAffixParameters) { let t = generateNumberOfAffixes(2, this._item); this.rerollAffixes(t, e); return true; }`.
The count table is `affixNumberGeneration[2] = [.5,.5]`: one or two target affixes with equal probability, at most one
prefix and one suffix.

## Apply results

300 `apply` runs on a magic Driftwood Wand (ilvl 1) that started with a prefix and a suffix, seeds 1..300:

- 151 items kept one explicit affix and 149 had two (50.3% singles).
- No item had two prefixes or two suffixes.
- All 13 level-1 wand modifiers appeared; the observed frequencies follow the pinned 7483-weight pool.

A magic Small Life Flask (two level-1 prefixes, no suffixes) rerolled to exactly one prefix, one of
`FlaskIncreasedRecoveryAmount1` and `FlaskIncreasedRecoverySpeed1`.
A normal or rare target is refused with `Invalid crafting setup: ["Item is not magic"]`.

The beta `pool` operation returns 0 candidates for a full magic item, because it evaluates the item's current open
slots rather than the post-reroll state. `apply` is the evidence for Alteration; the local `Roll` already removes the
old explicits first.

## Differences kept deliberately

**Unmodifiable bases.** CoE's `is_modifiable` does not read the base `unmodifiable` flag and rerolls a magic Sanctuary
Map. LocalExile refuses it, as with the other currency checks.

**Empty pools.** CoE's `alteration` ignores the `rollAffixes` error string and still reports success. LocalExile
refuses a base with no eligible ordinary affix, so the orb is not spent and an item is never left with no affixes by
missing data.

## Automated coverage

`AlterationTests` checks the 50/50 count distribution over 10,000 rolls, one-affix-per-side and family rules, the full
wand pool and values, the two-mod flask pool, preserved implicits/quality/sockets/links/charges, unmodifiable,
unsupported, no-Mods, unknown and empty-pool bases, corrupted/mirrored and non-magic items.
`AlterationGatewayTests` sends framed 0x71 and 0x72 messages through `Program.OnClientMessages`: inventory and stash
currency/target combinations, two consecutive rerolls spending one orb each, saved mods after cache eviction, and
refused normal/unmodifiable targets without spending.

No fresh real-client run was made for this change yet.
The old live check of the unvalidated path (2026-10-21) is not evidence for the new admission rules; native
re-acceptance remains to be recorded.
