# Vaal Orb: admission check and CoE boundary

## Scope

Vaal Orb uses the shared 0x71/0x72 currency path.
The local outcome rule is deliberately **not** the official Vaal table: the roadmap keeps official Chance/Vaal outcome
distributions as separate work.
This change adds a validated `ItemRolls.Vaal` entry point and hardens admission; it does not change the documented
outcomes.
`CurrencyEffects.Vaal` delegates to it.

The local rule remains: a weapon, armour piece or jewellery (ring, amulet, belt) that is modifiable, not unique and
not corrupted/mirrored is always corrupted.
A normal or magic item becomes rare with 4-6 affixes; a rare item replaces 1 (20%), 2 (20%), 3 (20%), 4 (20%) or 5
(10%) affixes, or rerolls all of them (10%, 4-6 affixes, new name).
One white socket is coloured.
New refusals: unsupported/unknown or `Unmodifiable` bases, missing Mods, and empty pools (previously `Roll` could
corrupt an item with no affixes when the data was missing).

## Pinned reference

Queried the external library's beta engine, dataset `3.29.3.1.2`, combined snapshot
`20261024T171803Z-combined-3.29.3.1.2`.
Exact correspondence to official 3.29.3b is explicitly unverified by the source.

| Asset | SHA256 |
|---|---|
| `package.js` | `09f9e940bdfbb38e1edc3af84d6008cc1c3758408dbb35504eb70e0c18fccf41` |
| `data.js` | `280792696ffa6700d389012accd357d9a708f8b7adc0bcb5ca42fe544f6a8489` |
| `extended.js` | `5bb7c9ad296397a565c141f362cbbe54a9316aa56cd16018986b19234e7ffab9` |

CoE lists the Vaal handler with the constraints `can_corrupt` and `corruptable_base`.
Its `apply` on a normal level-1 Driftwood Wand (seed 7) returned the item still normal with the corrupted flag set:
the official outcome there was "no change".
A corrupted item is refused with `Invalid crafting setup: ["Item is already corrupted","Item cannot be modified"]`.
Those constraints support the shared corrupted/mirrored refusal, but not the local 1-5/reroll outcomes.

CoE's official Vaal table includes no-change, reroll-to-6/8, rare upgrades, white sockets, unique jewels and
per-base outcomes. It also allows flasks, jewels, maps and gems, and unique items.
The local server rule intentionally stays narrower (weapon/armour/jewellery, no uniques); implementing the official
table is a separate feature per the roadmap.

## Automated coverage

`VaalTests` checks normal/magic → corrupted rare, the rare replacement branch keeping the name and six affixes, the
rare reroll branch getting a new name and 4-6 affixes, one white socket coloured with quality/links/implicits kept,
corrupted/mirrored/unique refusal, unsupported weapons, empty pools, non-target jewels/flasks/maps, and unknown or
no-Mods items.
`VaalGatewayTests` sends framed 0x71 and 0x72 messages through `Program.OnClientMessages`: inventory and stash
currency/target combinations, one orb spent, saved corrupted rare after cache eviction, refused second use, and
refused corrupted/unique/mirrored/jewel targets without spending.

No fresh real-client run was made for this change.
The old live check (2026-10-21) is not evidence for the new admission rules; native re-acceptance remains to be
recorded.
