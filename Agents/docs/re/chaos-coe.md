# Chaos Orb: CoE comparison

## Scope

Chaos Orb already used the shared 0x71/0x72 currency path.
This change adds a validated `ItemRolls.Chaos` entry point and keeps the shared CoE rare-count and jewel limits.
`CurrencyEffects.Chaos` delegates to it.
All modifier data remains from the current client's extraction pipeline.

Admission requires a rare, non-corrupted, non-mirrored item with Mods and a known, serializable base with Mods that is
not `Unmodifiable`, plus a non-empty ordinary pool.
Flasks are magic-only; a rare flask is refused.
`Roll` with `respectMetamods: true` keeps affixes protected by the prefix/suffix locks.

## Pinned reference

Queried the external library's beta engine, dataset `3.29.3.1.2`, combined snapshot
`20261024T171803Z-combined-3.29.3.1.2`.
Exact correspondence to official 3.29.3b is explicitly unverified by the source.

| Asset | SHA256 |
|---|---|
| `package.js` | `09f9e940bdfbb38e1edc3af84d6008cc1c3758408dbb35504eb70e0c18fccf41` |
| `data.js` | `280792696ffa6700d389012accd357d9a708f8b7adc0bcb5ca42fe544f6a8489` |
| `extended.js` | `5bb7c9ad296397a565c141f362cbbe54a9316aa56cd16018986b19234e7ffab9` |

The method tree lists Chaos under `Currencies` with the parent constraints `not_graft_base` and `is_modifiable`, plus
`rarity_rare` and `can_be_rare`.
Its handler rolls a count from the base's maximum and rerolls the explicits:
`chaos(e) { let t = getMaximumAffixesForBase(item); let a = generateNumberOfAffixes(t, item); rerollAffixes(a, e); }`.
The count table is the same as Alchemy: `affixNumberGeneration[6] = [2/3, 1/4, 1/12]` for 4, 5 or 6 affixes, and
jewels use `affixNumberGeneration[4] = [.65,.35]` for 3 or 4 with two per side.

## Apply results

300 `apply` runs on a rare level-1 Driftwood Wand, seeds 1..300: 211 items with four affixes, 63 with five and 26 with
six (70.3%, 21.0%, 8.7%), the same sample as Alchemy and within 1.6 standard errors of the table.
No item had more than three prefixes or three suffixes.
A magic target is refused with `Invalid crafting setup: ["Item is not rare"]`.

The beta `pool` operation returns nine candidates (5150) for a four-mod rare wand because it evaluates the current
mods as if they stayed; Chaos removes them first. `apply` and the local `Roll` are the evidence, not that pool query.

A rare Small Life Flask was rerolled by CoE and still returned a rare flask; LocalExile refuses rare flasks.
A rare Sanctuary Map was rerolled despite its `unmodifiable` flag; LocalExile refuses it by policy.

**Prefix lock.** A rare wand with `LocalIncreasedPhysicalDamagePercent1`, `LocalIncreasedAttackSpeed1` and the
`StrMasterItemGenerationCannotChangePrefixes` craft rerolled to three prefixes and one suffix: the protected prefix
stayed, the craft was consumed, and the suffixes were replaced.
The local `Roll(..., respectMetamods: true)` keeps the protected prefix and removes the unprotected craft the same way.

## Automated coverage

`ChaosTests` checks the 4/5/6 count distribution over 5,000 rolls, the 4-6 reroll and new name, jewel 3/4 with two per
side, the prefix-lock result and craft consumption, preserved implicits/quality/sockets/links/charges, flask and
unmodifiable refusal, full-item rerolls, unsupported/no-Mods/unknown/empty-pool bases, corrupted/mirrored and non-rare
items.
`ChaosGatewayTests` sends framed 0x71 and 0x72 messages through `Program.OnClientMessages`: inventory and stash
currency/target combinations, two rerolls spending one orb each, saved mods and name after cache eviction, a full rare
item rerolled, and refused magic/flask/unmodifiable targets without spending.

The full suite also exposed an unrelated time-base bug in `GameModeAilmentIntegrationTests`: it applied a chill at
`now = 0` but checked `PlayerActionFactor` at the connection clock, so a cold catalog load longer than the two-second
chill made the test fail. The test now applies at `Clock.Elapsed` and checks immediately; this is a test-only fix and
does not change production behaviour.

No fresh real-client run was made for this change yet.
The old live check of the unvalidated path (2026-10-21) is not evidence for the new admission rules; native
re-acceptance remains to be recorded.
