# Orb of Annulment: CoE comparison

## Scope

Orb of Annulment already used the shared 0x71/0x72 currency path and `ItemRolls.Annul`.
This change adds the shared admission check; the removal rule is unchanged.
`CurrencyEffects.Annulment` delegates to `ItemRolls.Annul`.
All modifier data remains from the current client's extraction pipeline.

Admission requires a magic or rare, non-corrupted, non-mirrored item with Mods and a known, serializable base with
Mods that is not `Unmodifiable`, plus at least one eligible explicit affix after the metamod and fractured filters.
A normal or unique item, and an item whose explicit affixes are all protected, is refused before spending.
Rarity and the rare name stay, even when a magic item loses its last affix (it remains magic with no explicit mods).

## Pinned reference

Queried the external library's beta engine, dataset `3.29.3.1.2`, combined snapshot
`20261024T171803Z-combined-3.29.3.1.2`.
Exact correspondence to official 3.29.3b is explicitly unverified by the source.

| Asset | SHA256 |
|---|---|
| `package.js` | `09f9e940bdfbb38e1edc3af84d6008cc1c3758408dbb35504eb70e0c18fccf41` |
| `data.js` | `280792696ffa6700d389012accd357d9a708f8b7adc0bcb5ca42fe544f6a8489` |
| `extended.js` | `5bb7c9ad296397a565c141f362cbbe54a9316aa56cd16018986b19234e7ffab9` |

The method tree lists Annulment under `Currencies` with the parent constraints `not_graft_base` and `is_modifiable`,
plus `rarity_not_normal` and `minimum_1_explicit`.

## Apply results

- A magic level-1 Driftwood Wand with one explicit prefix became a magic item with no explicit mods; the magic rarity
  stayed.
- A rare wand with two explicit mods became rare with one; the rare rarity stayed.
- A normal target is refused with `Invalid crafting setup: ["Item is normal","Item needs to have at least 1 modifier(s)"]`.
- A corrupted target is refused with `Invalid crafting setup: ["Item cannot be modified"]`.
- A rare Sanctuary Map with one mod was annulled by CoE despite its `unmodifiable` flag; LocalExile refuses it by
  policy, as with the other currency checks.

**Prefix lock.** A rare wand with a protected prefix, a suffix and `StrMasterItemGenerationCannotChangePrefixes`
always kept the prefix across 30 seeds. The craft itself is a suffix and was removed in 14 of 30 runs; the ordinary
suffix was removed in the other 16.
The local `Annul` uses the same rule: the prefix is protected, while the craft and the ordinary suffix compete as
removable explicits.
The cannot-roll-attack/caster crafts likewise exclude tagged affixes; fractured affixes are never candidates.

## Automated coverage

`AnnulmentTests` checks the magic-last-affix and rare-one-affix results, the rare name and rarity, jewel removal,
prefix-lock and cannot-roll-attack protection, fractured-affix protection, preserved implicits/quality/sockets/links/
charges, non-magic/unique refusal, unmodifiable, corrupted/mirrored, unsupported/unknown/no-Mods bases and items with
no eligible explicit.
`AnnulmentGatewayTests` sends framed 0x71 and 0x72 messages through `Program.OnClientMessages`: inventory and stash
currency/target combinations, two removals spending one orb each, a refused third use, saved empty magic item after
cache eviction, and refused normal/corrupted/mirrored/unmodifiable/unsupported targets without spending.

No fresh real-client run was made for this change.
The old live check (2026-10-21) is not evidence for the new admission rules; native re-acceptance remains to be
recorded.
