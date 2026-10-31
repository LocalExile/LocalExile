# Orb of Chance: admission check and CoE boundary

## Scope

Orb of Chance uses the shared 0x71/0x72 currency path.
The local outcome rule is deliberately **not** the official one: the roadmap keeps official Chance/Vaal outcome
distributions as separate work.
This change adds a validated `ItemRolls.Chance` entry point and hardens admission; the documented 30% rare / 70%
magic outcome is unchanged.
`CurrencyEffects.Chance` delegates to it.

The local rule remains: a normal weapon or armour piece becomes rare (30%: 4-6 affixes and a rare name) or magic
(2-4 affixes, at most two prefixes and two suffixes), identified.
New refusals before rolling/spending: corrupted/mirrored items, unsupported/unknown or `Unmodifiable` bases, missing
Mods, and empty pools (previously `Roll` could spend an orb and leave an item with no affixes when the data was
missing).

## CoE boundary

Queried the external library's beta engine, dataset `3.29.3.1.2`, combined snapshot
`20261024T171803Z-combined-3.29.3.1.2`.
Exact correspondence to official 3.29.3b is explicitly unverified by the source.

The beta method tree has **no Orb of Chance handler**.
`craft methods` lists transmutation, alteration, augmentation, regal, alchemy, chaos, exalted, annulment, scouring,
divine, blessed, fracture, vaal and other currencies, but not Chance.
The adapter therefore cannot simulate the official Chance unique/rarity outcome, and no CoE probability or pool is
claimed here.

The official Orb of Chance can create uniques and uses per-unique rarity weights; the local server rule does not.
Implementing the official table is a separate feature per the roadmap, together with the Vaal outcomes.
The local 30%/70% split and the 2-4 magic count are server rules maintained by their own regression tests.

## Automated coverage

`ChanceTests` checks 500 mixed outcomes for the documented rare/magic shapes, names, side limits, families, values and
the full modifier pool; the 30% rare share over 5,000 rolls; preserved implicits/quality/sockets/links/charges; and
refusal of mirrored/corrupted/unsupported/unmodifiable/unknown/no-Mods/empty-pool items and non-weapon/armour targets.
`ChanceGatewayTests` sends framed 0x71 and 0x72 messages through `Program.OnClientMessages`: inventory and stash
currency/target combinations, one orb spent, saved magic/rare result after cache eviction, refused second use, and
refused corrupted/mirrored/unsupported/jewel targets without spending.

No fresh real-client run was made for this change.
The old live check (2026-10-21) is not evidence for the new admission rules; native re-acceptance remains to be
recorded.
