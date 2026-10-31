# Eldritch Embers: CoE comparison

## Scope

The four Searing Exarch Eldritch Embers are the mirror of the Eater of Worlds Ichors:
`CurrencyEldritchEmber1..4` (Lesser, Greater, Grand, Exceptional).
They use the shared 0x71/0x72 currency path and `ItemRolls.EldritchEmber`; the Gateway handler spends one on success.
All modifier data remains from the current client's extraction pipeline.

The local rule, matching the wiki: the ember adds one Searing Exarch implicit of its tier to a body armour, boots,
gloves or helmet. It replaces every existing implicit modifier except Eater of Worlds ones.
Only normal, magic or rare items are accepted; uniques, corrupted/mirrored items and non-armour classes are refused
before spending. Explicit affixes, quality, sockets, links, rarity and name stay.

The implicit is stored as a normal `ModSection.Implicit` entry in the item's single implicit list.
A separate Eldritch dominance/influence state and the Orb of Conflict are not modelled; the native client display of
the new implicit still needs an acceptance run.

## Pinned reference

Queried the external library's beta engine, dataset `3.29.3.1.2`, combined snapshot
`20261024T171803Z-combined-3.29.3.1.2`.
Exact correspondence to official 3.29.3b is explicitly unverified by the source.

| Asset | SHA256 |
|---|---|
| `package.js` | `09f9e940bdfbb38e1edc3af84d6008cc1c3758408dbb35504eb70e0c18fccf41` |
| `data.js` | `280792696ffa6700d389012accd357d9a708f8b7adc0bcb5ca42fe544f6a8489` |
| `extended.js` | `5bb7c9ad296397a565c141f362cbbe54a9316aa56cd16018986b19234e7ffab9` |

The CoE method tree puts the Embers under the same `Eldritch` parent as the Ichors, with `eldritch_base`,
`no_influence`, `can_be_rare` and `is_modifiable`.
Its `eldritch(e)` handler removes the existing Exarch-type affixes, then rolls one with `_types=[28]`,
`_ignoreItemLevel=true` and `_fixedLevel={level:tier,max:6}`.
The tier maps to the `...EldritchImplicitN` suffix: Lesser 1, Greater 2, Grand 3, Exceptional 4.

## Apply results

CoE `apply` with the Lesser Ember on an ilvl-84 rare base, seed 7:

| Base | Exarch implicit |
|---|---|
| `HelmetStr1` | `IncreasedCastSpeedEldritchImplicit1` |
| `GlovesStr1` | `IncreasedAttackSpeedEldritchImplicit1` |
| `BootsStr1` | `MovementVelocityEldritchImplicit1` |
| `BodyStr1` | `SpellCriticalStrikeMultiplierEldritchImplicit1` |

A rare `HelmetStrDexRitual1` lost its normal base implicit and received the Exarch one.
A gloves item that already had the Eater implicit `ChanceToSuppressSpellsEldritchImplicit1` kept it and received the
Exarch implicit next to it.

Greater/Grand/Exceptional applies over 100 seeds on a helmet produced 40/46/46 distinct keys, with 36/42/42
`UniquePresence`/`PinnaclePresence` selections.
The local pool includes those variants, weighted by the base's tags.

## Differences and limits

- `no_influence` cannot be enforced locally because the server has no Shaper/Elder/Elderslayer influence state.
- The item carries the implicit in the normal implicit list; a separate Eldritch dominance state, influence icons and
  the Orb of Conflict are out of scope.
- The wiki's "normal, magic or rare" rule is enforced; uniques are refused even though `TryModifiable` alone would
  allow them.
- The four Ember handlers are named correctly in CoE, unlike the Greater Ichor entry.

## Automated coverage

`EldritchEmberTests` checks the Lesser/tier variants, normal-implicit removal with Eater preservation, replacement of
an existing Exarch implicit, non-armour and unique/corrupted/mirrored refusal, unknown/no-Mods bases, preserved
explicits/quality/sockets/links/name, and the four CoE base keys plus the tier-3/4 presence variants.
`EldritchEmberGatewayTests` sends framed 0x71 and 0x72 messages through `Program.OnClientMessages`: inventory and stash
combinations for Lesser, two uses replacing one Exarch implicit, all four tier suffixes, saved implicit after cache
eviction, and refused non-armour/unique/corrupted/mirrored targets without spending.

No native client run was made for this change; item tooltip/display acceptance of the new implicit remains to be
recorded.
