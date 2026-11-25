# Vaal Orb: admission check and CoE boundary

## Scope

Vaal Orb (`Metadata/Items/Currency/CurrencyCorrupt`) uses the shared `0x71`/`0x72` currency path and Hinekora's Lock
`0x29d`/`0x2a0` foresight path via `Game.CurrencyResult`, dispatching to `VaalCorruption.Apply` in
`Modules/Core/Game/Item/VaalCorruption.cs`.

Full 3.29 retail Vaal Orb outcomes are implemented for skill/support gems, equipment (weapons, armour, shields, quivers,
rings, amulets, belts, fishing rods — normal through unique, including 3.29 white-to-coloured socket corruption,
`GenerationType = 5` implicits respecting `Level`/`MaxLevel`, Eldritch single-implicit replacement, and 6-affix rare
bricking with 1/36 max-socket full-link chance), maps/MapKeys (no-change, unidentified, 8-mod rare, +1 tier / T16 to
Vaal Temple, plus unique map rules), jewels (standard, Abyss, Cluster, Prismatic, Timeless and unique, including
base-matched corruption-exclusive unique jewels), and flasks (-10% to +10% quality shift). Unidentified non-gem items,
tinctures (`CanBeCorrupted = 0`), already-corrupted, mirrored, vestigial and unmodifiable bases are refused before
spending. `CurrencyMoreCorrupt` and `CurrencyIncursionCorrupt1` keep their separate paths via
`CurrencyEffects.TryAction` (and `ItemRolls.Vaal`).

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

CoE's official Vaal table includes no-change, reroll-to-6/8, rare upgrades, socket colour corruption (inverted to
white-to-coloured in 3.29), corrupted implicits, unique jewels and per-base outcomes across equipment, flasks, jewels,
maps, gems and uniques. `VaalCorruption.Apply` now implements those 3.29 retail tables in `Modules/Core/Game/Item/`.

## Verification

No fresh real-client run was made for this change.
The old live check (2026-10-21) is not evidence for the new admission rules; native re-acceptance remains to be
recorded.
