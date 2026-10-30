# Regal Orb: CoE comparison

## Scope

Orb of Regal already used the shared 0x71/0x72 currency path.
This change adds a validated `ItemRolls.Regal` entry point and the same admission policy as Transmutation and
Augmentation; it is not a second crafting engine.
`CurrencyEffects.Regal` delegates to it.
The rare pool is the existing `AddAffix(item, 3)` filter, whose candidates `ItemRolls.AffixesToAdd` exposes for tests.
All modifier data remains from the current client's extraction pipeline.

Admission requires a magic, non-corrupted, non-mirrored item with Mods and a known, serializable base with Mods that is
not `Unmodifiable`, plus at least one eligible affix.
Flasks are magic-only and cannot become rare.
A full item whose pool has no eligible affix, and missing data, are refused before the Gateway spends an orb.

## Pinned reference

Queried the external library's beta engine, dataset `3.29.3.1.2`, combined snapshot
`20261024T171803Z-combined-3.29.3.1.2`.
Exact correspondence to official 3.29.3b is explicitly unverified by the source.

| Asset | SHA256 |
|---|---|
| `package.js` | `09f9e940bdfbb38e1edc3af84d6008cc1c3758408dbb35504eb70e0c18fccf41` |
| `data.js` | `280792696ffa6700d389012accd357d9a708f8b7adc0bcb5ca42fe544f6a8489` |
| `extended.js` | `5bb7c9ad296397a565c141f362cbbe54a9316aa56cd16018986b19234e7ffab9` |

The method tree lists Regal under `Currencies` with the parent constraints `not_graft_base` and `is_modifiable`, plus
`rarity_magic`, `can_be_rare` and `can_be_magic`.
Its handler sets the rarity to rare and then rolls one affix:
`regal(e = new rollAffixParameters) { this._item.rarity = ItemRarity.RARE; this.rollAffixes(1, e); return true; }`.

## Pool and apply

A level-1 Driftwood Wand with the prefix `LocalIncreasedPhysicalDamagePercent1` (`mods/1231`) has the full rare pool
minus that mod: 12 candidates, total weight 6983.
Six are prefixes (4150 total, including `LocalIncreasedPhysicalDamagePercentAndAccuracyRating1`, which is a different
family) and six are suffixes (2833).
With both sides full (prefix `mods/1231` and suffix `LocalIncreasedAttackSpeed1`), 11 candidates remain (6483).

The beta `pool` operation returns only the six suffixes (2833) for the prefix case.
That is an artifact of evaluating the still-magic item: its open-suffix count is one, its open-prefix count is zero.
The actual `regal` handler sets rare before rolling, so it uses the rare capacity.
A 300-seed `apply` run on the prefix case added all 12 candidates: 172 prefixes and 128 suffixes (57.3% prefixes,
against the pool's 4150/6983 = 59.4%).
A full magic wand applied with 10 seeds became rare with three explicit mods every time.
A rare target is refused with `Invalid crafting setup: ["Item is not magic"]`.

## Differences kept deliberately

**Flasks.** CoE's class data marks flasks with `rarity: "magic"`, but its `can_be_rare` check did not stop the engine:
`apply` on a magic Small Life Flask produced a rare flask with one prefix and spent the orb.
The official game has no rare flasks.
LocalExile refuses any item with a `Flask` component, so the orb is not spent.
Tinctures are refused too because the local serializer does not support their component; charms and Sanctum relics are
already `Unmodifiable`.

**Unmodifiable bases.** As with the other currency checks, CoE's `is_modifiable` does not read the base
`unmodifiable` flag and happily upgrades a magic Sanctuary Map.
LocalExile keeps the refusal, matching the local client flag and the Transmutation/Augmentation policy.

## Automated coverage

`RegalTests` checks the 12/6983 rare pool, the 11/6483 full-item pool, the 20,000-draw rare-pool distribution, the rare
name, preserved implicits/quality/sockets/links/charges, flask and unmodifiable refusal, unsupported/no-Mods/unknown
bases, corrupted/mirrored and non-magic items.
`RegalGatewayTests` sends framed 0x71 and 0x72 messages through `Program.OnClientMessages`: inventory and stash
currency/target combinations, exactly one orb spent, a full magic item becoming rare with three affixes, saved mods and
name after cache eviction, refused second use, and flask/unmodifiable refusal without spending.

No fresh real-client run was made for this change yet.
The old live check of the unvalidated path (2026-10-21) is not evidence for the new admission rules; native
re-acceptance remains to be recorded.
