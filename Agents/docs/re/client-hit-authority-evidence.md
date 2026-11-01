# One-hit authority contract: evidence availability

## Current result

Q0 is Partial.
The historical Sand Spitter report remains the preferred candidate, but no
independently inspectable single-hit record or explicit client networking mode
has been recovered.
Public sniffer source establishes tooling capabilities, not an observed hit.
This ledger supports the [contract workbench](client-hit-authority-contract.md).

## Evidence and provenance

| Input | Fixed source and availability | What it establishes / limit |
|---|---|---|
| Historical official-traffic report | PR #373, `b0afa90ea5c58e9082099d887180df8299dcdaf3`, [report](https://github.com/LocalExile/LocalExile/blob/b0afa90ea5c58e9082099d887180df8299dcdaf3/Agents/docs/dev/ranged-monster-actions-port.md#live-reference-official-servers); source blob SHA256 `5f1f63861fc03b73b6ea42b034faba3228ec2a00cc1da82757876bb1e61aa824` | Author-reported 3.29.3b traffic; the report explicitly keeps captures private because they contain account data. Neither capture nor the named Python decoder is included in this report. |
| Static result/resource receivers | PR #371, `801358d732adbc558d657d8773e273a02bc891b1`, [combat signals](https://github.com/LocalExile/LocalExile/blob/801358d732adbc558d657d8773e273a02bc891b1/Agents/docs/protocol/29-client-combat-signals.md) and [comparison](https://github.com/LocalExile/LocalExile/blob/801358d732adbc558d657d8773e273a02bc891b1/Agents/docs/re/client-damage-pr373-comparison.md) | Separate `0x1af` feedback/control and `0x1b1` absolute pool assignment; action identity and scheduling fields have static consumers. These are not a recorded association between a hit and a resource update. |
| Local artifact discovery | Bounded filename searches of existing research output/log locations and the supplied reference library | No accepted official raw capture was identified in those searches. This is not a machine-wide absence claim. Source snapshots, synthetic compositions and local-server logs retain their separate provenance. |
| evrySniff | Public discovery checked 2026-11-01, described below | Exact project/source and record format remain unavailable to this investigation. A name in another report does not prove an open-source repository exists. |
| Related open-source sniffer | `ncatlin/exileSniffer`, `33794bb0c7ea0ae260452cba3771ea136610132b`, [README](https://github.com/ncatlin/exileSniffer/blob/33794bb0c7ea0ae260452cba3771ea136610132b/README.md) | Inspectable historical tool source, explicitly described as working with April 2018 PoE. No demonstrated relationship to evrySniff or compatibility with 3.29.3b. |

## Sand Spitter candidate

The report distinguishes two captures.
In the first, the player was never hit; the player's resource messages changed
Mana (pool 1), so those messages cannot witness incoming Life damage.
The second reportedly contains 11 hits from three
`Metadata/Monsters/SandSpitters/SandSpitter12@1` monsters, type `0xe63d7611`.
Ten reportedly cost 4 Life and one critical hit 5 Life.
Reported action sequences include `0x80bc` and `0x80bd`;
`0x1af` reportedly uses kind 3, flags `0x4201` or `0x4221`, and code 0.

These observations identify a promising scenario, not a frozen witness.
The narrative does not expose one complete action/result/resource record set,
its record ordinals, client mode or the controls needed to attribute a Life change.
Its field called "skill index" must not override the static finding that the
byte selects primary/secondary action state rather than indexing a skill list.

## Public sniffer investigation

Exact-name GitHub repository searches, a readme-inclusive search and code search
for `evrySniff` found the LocalExile report but no public tool source.
The report author's public repository list contained `evryPath` but no matching
sniffer repository; the expected same-owner repository URL returned HTTP 404.
That response does not distinguish a private, renamed, deleted or nonexistent repository.
The recursive tree of `evryPath` at
`33ea01513d38a839da09c0e77900e106540459dc` supplied no candidate sniffer/capture path.
These bounded checks do not prove that evrySniff is unavailable everywhere.

The related `ncatlin/exileSniffer` repository is Apache-2.0 licensed.
Only selected source files were downloaded for inspection; nothing was installed or executed.
Its README warns that protocol changes break compatibility and describes reading
session keys from process memory before decoding network traffic.
The source provides these useful historical references:

- [Capture path](https://github.com/ncatlin/exileSniffer/blob/33794bb0c7ea0ae260452cba3771ea136610132b/exileSniffer/packet_capture_thread.cpp#L184): live network capture with TCP stream following.
- [JSON envelope](https://github.com/ncatlin/exileSniffer/blob/33794bb0c7ea0ae260452cba3771ea136610132b/exileSniffer/uiMsg.cpp#L127): payload, direction, process and stream type; message ID/name are added separately.
- [JSON pipe serialization](https://github.com/ncatlin/exileSniffer/blob/33794bb0c7ea0ae260452cba3771ea136610132b/exileSniffer/json_pipe_thread.cpp#L139): UTF-16 JSON output for external consumers.
- [Resource decoder](https://github.com/ncatlin/exileSniffer/blob/33794bb0c7ea0ae260452cba3771ea136610132b/exileSniffer/gameserver_packet_deserialisers.cpp#L1983): `SRV_MOBILE_UPDATE_HMS` reads `NewValue` and a pool selector documented as Life/Mana/Shield.

The historical resource message is numbered `0xf1`, not current `0x1b1`,
and its decoded tail differs from the current reader.
An old comment guesses that a header word might identify a causing object;
that is not evidence of causality and does not supersede the current scheduling analysis.
The README's example prints resource values, not attributed damage events.
No verified 3.29.3b witness was identified in the inspected source tree.
No source or file-format equivalence between this tool and evrySniff is established.

## Smallest missing input and next edge

First preference is an existing sanitized excerpt from the second #373 capture,
with its decoder revision and explicit networking mode.
It needs stable record ordinals and direction, the source/target entity mapping,
one action announcement and matching feedback sequence/selector, and the player's
pool-0 updates before/after that event, with enough surrounding traffic to expose
other hits, regeneration and other resource changes.
Include both directions in that window so a client report is not omitted by
selecting only server-to-client messages; absence claims remain limited to that window.
Preserve message bytes/unknown fields and relative timing; omit credentials and account identity.
Such an excerpt may narrow attribution but must still be assessed against the
actual application/scheduling consumers; adjacency alone is insufficient.

Until that input is available, Q1 can reuse static field/receiver obligations.
The pinned combat-signals audit already separates an action-associated result
from an entity/pool-addressed absolute assignment.
It establishes no action key linking the latter to the former; the shared header
fields have scheduling consumers and cannot simply be renamed a damage-event ID.
The [responsibility/identity matrix](client-hit-authority-obligations.md) assembles
those fixed audits and retains mode-dependent guards.
It remains a partial contract, distinct from demonstrating a recorded event in Q0/Q2.
Do not expand into formula discovery or port the historical sniffer as a substitute
for the missing record.
New collection, client attachment and game operation are outside this research authorization.
