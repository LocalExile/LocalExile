# Exalted Orb: CoE comparison

## Scope

Orb of Exalted already used the shared 0x71/0x72 currency path.
This change adds a validated `ItemRolls.Exalted` entry point and uses the base's rare per-side limit.
`CurrencyEffects.Exalted` delegates to it.
All modifier data remains from the current client's extraction pipeline.

Admission requires a rare, non-corrupted, non-mirrored item with Mods and a known, serializable base with Mods that is
not `Unmodifiable`, plus at least one eligible affix within the rare limits.
Flasks are magic-only; a rare flask is refused.
A full item and missing data are refused before the Gateway spends an orb.

## Pinned reference

Queried the external library's beta engine, dataset `3.29.3.1.2`, combined snapshot
`20261024T171803Z-combined-3.29.3.1.2`.
Exact correspondence to official 3.29.3b is explicitly unverified by the source.

| Asset | SHA256 |
|---|---|
| `package.js` | `09f9e940bdfbb38e1edc3af84d6008cc1c3758408dbb35504eb70e0c18fccf41` |
| `data.js` | `280792696ffa6700d389012accd357d9a708f8b7adc0bcb5ca42fe544f6a8489` |
| `extended.js` | `5bb7c9ad296397a565c141f362cbbe54a9316aa56cd16018986b19234e7ffab9` |

The method tree lists Exalted under `Currencies` with the parent constraints `not_graft_base` and `is_modifiable`,
plus `rarity_rare`, `can_be_rare` and the handler's `open_affix`.
The handler adds one affix (`rollAffixes(1)`), or two with the omen; the local server does not model that omen.

## Pool and apply

A level-1 Driftwood Wand with four affine mods (`LocalIncreasedPhysicalDamagePercent1`,
`IncreasedManaWeapon1`, `LocalIncreasedAttackSpeed1`, `Intelligence1`) has nine candidates, total weight 5150:
`LocalIncreasedPhysicalDamagePercentAndAccuracyRating1` 500, `LocalAddedFireDamage1` 250,
`LifeGainedFromEnemyDeath1` 500, `ManaGainedFromEnemyDeath1` 500, `LocalCriticalStrikeChance1` 500,
`LocalIncreasedAccuracyNew1` 500, and the three spell added-damage mods 800 each.
`apply` added one of them and kept the four existing mods.

A full three-prefix/three-suffix rare wand is refused with
`Invalid crafting setup: ["Item needs at least 1 open modifier(s)"]`.
A magic target is refused with `Invalid crafting setup: ["Item is not rare"]`.

Jewels are the per-class exception. CoE's jewel class has `affixes = 4` (two per side):
a rare jewel with 2 prefixes and 2 suffixes is refused by `open_affix`, while a 2+1 jewel gets a fourth affix.
The local `Exalted` uses the same limit through `MaxAffixesPerSide`, so it cannot create a fifth jewel affix.
A rare flask produced `Could not generate mod!`; the local server refuses rare flasks explicitly.

## Differences kept deliberately

**Flasks.** CoE's class data marks flasks magic-only; its Exalted result has no mod, while LocalExile refuses the
target without spending.

**Unmodifiable bases.** CoE's `is_modifiable` does not read the base `unmodifiable` flag and adds a mod to a rare
Sanctuary Map. LocalExile refuses it, as with the other currency checks.

## Automated coverage

`ExaltedTests` checks the nine-mod 5150 pool, the one-affix addition and preserved name/mods, the full three-and-three
refusal, the jewel 2+2 refusal and 2+1 suffix addition, preserved implicits/quality/sockets/links/charges,
unmodifiable, rare-flask, unsupported/no-Mods/unknown/empty-pool bases, corrupted/mirrored and non-rare items.
`ExaltedGatewayTests` sends framed 0x71 and 0x72 messages through `Program.OnClientMessages`: inventory and stash
currency/target combinations, two additions spending one orb each, saved mods and name after cache eviction, and
refused full/flask/jewel/unmodifiable targets without spending.

No fresh real-client run was made for this change yet.
The old live check of the unvalidated path (2026-10-21) is not evidence for the new admission rules; native
re-acceptance remains to be recorded.
