# Transmutation: CoE comparison and native acceptance

## Scope

Transmutation already used the shared 0x71/0x72 currency path.
This change adds admission checks, not a second crafting engine or a new packet layout.
`CurrencyEffects.Transmutation` delegates to `ItemRolls.Transmute`.
All modifier data remains from the current client's extraction pipeline.

Admission requires a normal, non-corrupted, non-mirrored item with Mods and a known, serializable base with Mods.
`ItemCatalog.BaseType.Unmodifiable` combines the existing BaseItemTypes and ItemClasses flags.
Unmodifiable bases and empty eligible ordinary pools are refused before the Gateway spends currency.
Refusing missing data or an empty pool is explicit LocalExile safety policy, not official-server parity evidence.
Other currencies do not consume the new flag in this change.

## Pinned reference

Queried the external library's beta engine, dataset `3.29.3.1.2`, combined snapshot
`20261024T171803Z-combined-3.29.3.1.2`.
Exact correspondence to official 3.29.3b is explicitly unverified by the source.

| Asset | SHA256 |
|---|---|
| `package.js` | `09f9e940bdfbb38e1edc3af84d6008cc1c3758408dbb35504eb70e0c18fccf41` |
| `data.js` | `280792696ffa6700d389012accd357d9a708f8b7adc0bcb5ca42fe544f6a8489` |
| `extended.js` | `5bb7c9ad296397a565c141f362cbbe54a9316aa56cd16018986b19234e7ffab9` |

Research queries used the external reference library's persistent JSONL API, including:

```json
{"action":"rule","q":"transmute(","limit":8}
{"action":"rule","q":"static generateNumberOfAffixes","limit":2}
{"action":"rule","q":"affixNumberGeneration:","limit":2}
{"action":"source","file":"data.js","pointer":"/methods/crafting"}
{"action":"craft","request":{"operation":"pool","engine":"beta","base":"Driftwood Wand","method":"transmutation","ilvl":1,"limit":100}}
{"action":"craft","request":{"operation":"pool","engine":"beta","base":"Small Life Flask","method":"transmutation","ilvl":1,"limit":100}}
```

`transmute` calls `generateNumberOfAffixes(2, item)`, sets magic rarity and rerolls the explicit affixes.
The ordinary count table is `[.5,.5]`: one or two affixes with equal target probability.
Prefix/suffix limits and incompatible families can leave fewer than the target count.
For example, the level-1 Small Life Flask has only two prefix candidates, each weight 600.
It therefore receives one explicit mod even when the target count is two.
The level-1 Driftwood Wand pool matches all 13 local modifier IDs and weights, total 7483.
These are bounded comparisons, not a complete audit of all bases or item levels.

CoE's method constraints include `not_graft_base`, `is_modifiable`, `rarity_normal` and `can_be_normal`.
Its internal class 212 is an Uber-map category and cannot be normal/magic; do not join that number to ItemClasses rows.
The local client already marks the inspected Uber-map bases unmodifiable.
Special CoE class overrides, unsupported grafts, influences, memory mechanics and other crafting systems are not added.
Different RNGs and source data versions mean equal seeds are not expected to produce equal items.

## Automated coverage

`TransmutationTests` uses small synthetic tables to exercise base/class restrictions, missing bases/pools,
both missing Mods checks, corrupted/mirrored/non-normal refusal, count frequencies, non-uniform weights,
level/domain/tag/essence filtering, family exclusion, one-sided pools and preserved properties.
The first admission run failed seven regressions against the old implementation before the fix.

`TransmutationDataTests` checks representative supported native equipment, jewellery, jewels, maps and flasks.
It also pins the two independently queried CoE pools.
The initial branch refused generic `MapKey` bases because their component was not serializable.
Upstream PR #351 supplied that serializer; after synchronizing to `b2737133`, the regression now checks that a generic
map upgrades and serializes using upstream's implementation, without a currency-specific layout workaround.

`TransmutationGatewayTests` sends framed 0x71 and 0x72 messages through `Program.OnClientMessages`.
It checks inventory/stash currency and targets, exactly one orb spent, saved mod values after cache eviction,
refused repeat use and unmodifiable-target refusal without spending.

## Real-client checks

Verified on 2026-10-29 with the copied 3.29.3b client, launched only through `scripts/dev/live.ps1`.
These native checks were recorded before the upstream synchronization to `b2737133`, on the original `59154b74` base
with the local Transmutation changes; they are not a new live acceptance run of the synchronized source.
The local run used a separate temporary save database and throwaway accounts, not the player's save database.
Known 18-byte 0x71 requests were submitted on the game thread through the client's existing encrypted socket.
This is framed bridge evidence, not a mouse/tooltip or native currency-writer/UI test.

- A level-1 Driftwood Wand became identified magic; the orb stack changed 5 to 4.
  Its 11% spell-damage implicit, two white sockets and their link survived; the rolled prefix was 49% physical damage.
- Reapplying to that magic wand was refused; the stack stayed 4 and the item did not change.
- A level-1 Small Life Flask became magic with one prefix (41% increased recovery, 33% reduced recovery rate).
  Its 21 charges stayed; the orb stack changed 4 to 3.
- A deliberately normal Sanctuary Map retained its normal rarity and `IsUberMap` implicit.
  Transmutation was refused and the stack stayed 3.
- Return to character selection and Play preserved the wand, flask and remaining three orbs.

A read-only Frida probe observed `0x142201d70` completing each upgrade's magic-properties read.
The wand's wire flags were 57, the flask's 41, and the stored native rarity at ObjectMagicProperties +0x7c was 1.
The client remained in an active area after the checks and relog.
Shift and stash combinations have automated production-dispatch coverage; they were not rechecked natively here.
The bridge has no native currency UI operation; no unknown Shift-writer trailer or mouse input was invented.
No official-server distribution parity or full manual UI verification is claimed.

Local evidence (not included in this change): `logs/transmutation-full-tests.log`,
`native-evidence.json` and a copied `native-gateway.log` in the ignored verification workspace.
The native Gateway DLL SHA256 was `25b01295cb4da6c4fd6945a5bf549f2a8eb82f12fa5e64e9656d24944b2dfece`.
Check the local evidence for the actual build hashes before reusing the run as evidence for another source revision.
