# Eldritch Ichors: CoE comparison

## Scope

The four Eater of Worlds Eldritch Ichors are new currencies:
`CurrencyEldritchIchor1..4` (Lesser, Greater, Grand, Exceptional).
They use the shared 0x71/0x72 currency path and `ItemRolls.EldritchIchor`; the Gateway handler spends one on success.
All modifier data remains from the current client's extraction pipeline.

The local rule, matching the wiki: the ichor adds one Eater of Worlds implicit of its tier to a body armour, boots,
gloves or helmet. It replaces every existing implicit modifier except Searing Exarch ones.
Only normal, magic or rare items are accepted; uniques, corrupted/mirrored items and non-armour classes are refused
before spending. The item's explicit affixes, quality, sockets, links, rarity and name stay.

The implicit is stored as a normal `ModSection.Implicit` entry in the item's single implicit list.
The Ember family mirrors this (see [the Ember comparison](eldritch-ember-coe.md)); the Orb of Conflict and a separate
Eldritch dominance/influence state are not modelled, and the native client display of the new implicit still needs an
acceptance run.

## Pinned reference

Queried the external library's beta engine, dataset `3.29.3.1.2`, combined snapshot
`20261024T171803Z-combined-3.29.3.1.2`.
Exact correspondence to official 3.29.3b is explicitly unverified by the source.

| Asset | SHA256 |
|---|---|
| `package.js` | `09f9e940bdfbb38e1edc3af84d6008cc1c3758408dbb35504eb70e0c18fccf41` |
| `data.js` | `280792696ffa6700d389012accd357d9a708f8b7adc0bcb5ca42fe544f6a8489` |
| `extended.js` | `5bb7c9ad296397a565c141f362cbbe54a9316aa56cd16018986b19234e7ffab9` |

The CoE method tree puts the Ichors under an `Eldritch` parent with the constraints `eldritch_base`, `no_influence`,
`can_be_rare` and `is_modifiable`.
Its handler removes the existing Eater-type affixes, then rolls one with `_types=[EATER_INFLUENCE_TYPE]`,
`_ignoreItemLevel=true` and `_fixedLevel={level:tier,max:6}`.
`EATER_INFLUENCE_TYPE` is 29 and `EXARCH_INFLUENCE_TYPE` is 28, the same GenerationType values in the local Mods.dat.
The tier maps to the `...EldritchImplicitN` suffix: Lesser 1, Greater 2, Grand 3, Exceptional 4.

CoE's method tree names the second ichor handler `lesser_eldritch_ichor` too (item 338 is Greater); the adapter cannot
address Greater by name. The local tier 2 uses the same data rule and the `...Implicit2` suffix; no CoE tier-2
probability is claimed.

## Apply results

CoE `apply` with the Lesser Ichor on an ilvl-84 rare base, seed 7:

| Base | Eater implicit |
|---|---|
| `HelmetStr1` | `EnergyShieldRegenerationEldritchImplicit1` |
| `GlovesStr1` | `ChanceToSuppressSpellsEldritchImplicit1` |
| `BootsStr1` | `LifeRegenerationRateEldritchImplicit1` |
| `BodyStr1` | `PhysicalDamageTakenAsFireBodyUberEldritchImplicit1` |

A rare `HelmetStrDexRitual1` with its normal base implicit lost that implicit and received the Eater one.
A gloves item that already had the Exarch implicit `IncreasedAttackSpeedEldritchImplicit1` kept it and received the
Eater implicit next to it.

Grand and Exceptional applies (tier 3/4) produced the `...Implicit3`/`...Implicit4` variants and, over 100 seeds,
40 observed selections were `UniquePresence`/`PinnaclePresence` variants out of 44 distinct keys.
The local pool includes those variants, weighted by the base's tags.

The local `EldritchIchorPool` was checked against the four observed base keys and against the tier-3/4 presence
variants; all selected rows have GenerationType 29 and a positive weight for the base's tags.

## Differences and limits

- CoE does not expose a usable Greater handler name; the local tier 2 is verified through the data table and the
  tier-suffix rule, not through a CoE apply.
- `no_influence` cannot be enforced locally because the server has no Shaper/Elder/Elderslayer influence state.
- The item carries the implicit in the normal implicit list; a separate Eldritch dominance state, the Orb of
  Conflict and the influence icons are out of scope.
- The wiki's "normal, magic or rare" rule is enforced; uniques are refused even though `TryModifiable` alone would
  allow them.

## Automated coverage

`EldritchIchorTests` checks the Lesser/tier variants, normal-implicit removal with Exarch preservation, replacement of
an existing Eater implicit, non-armour and unique/corrupted/mirrored refusal, unknown/no-Mods bases, preserved
explicits/quality/sockets/links/name, and the four CoE base keys plus the tier-3/4 presence variants.
`EldritchIchorGatewayTests` sends framed 0x71 and 0x72 messages through `Program.OnClientMessages`: inventory and stash
combinations for Lesser, two uses replacing one Eater implicit, all four tier suffixes, saved implicit after cache
eviction, and refused non-armour/unique/corrupted/mirrored targets without spending.

No native client run was made for this change; item tooltip/display acceptance of the new implicit remains to be
recorded.
