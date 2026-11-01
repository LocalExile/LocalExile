# Mod-derived Buff descriptor preparation (3.29.3b)

## Result and boundary

A concrete native numerical preparation path now connects a Mod's values to a
separate Buff descriptor. In the selected primary-template prefix of
`0x1421ff8f0`, the client copies `BuffTemplates.Buff_StatValues`, matches each
Mod stat against `BuffTemplates.StatsKey`, and overwrites the matching descriptor
slot with the supplied Mod value. Unmatched stats enter a separate ordinary
aggregate. This is stronger than a schema/name correspondence, but it is **not**
an applied gameplay Buff or target incoming-rate publication.

The original source-instance, selection/aggregation, shared-cap and expiry
withdrawal gaps remain open. The descriptor is not assigned the identity or
clock of the [network Buff instance](client-buff-network-clock-contract.md).
Nor does this construction establish when gameplay offence is snapshotted.
Original R2/R3/R4 remain **Partial**.

This is offline analysis of original executable SHA256
`78a0e227189c263658a7f65a02f086428b859bfc114ae8e7ce8aab721ebd48d5`,
using preferred image base `0x140000000`, pinned extracted tables, and bounded
original-instruction interpretation. No client launch, attachment, new capture,
server implementation, CI or shared native-analysis mutation was performed.

## 1. Direct template references and actual rate declarations

The applicable schema variants (`validFor` 1 or 3) have ten tables declaring
foreign-row references to BuffTemplates: Shrines, BeyondFactions,
UltimatumModifiers, SentinelTaggedMonsterStats, AncestralEmbraceVariations,
DivinationBuffTemplates, ExplodingStormBuffs, Mods, PassiveSkills and
PlayerBuffsFromMapStats. All ten extracted tables were inspected structurally.

Nine templates grant at least one of the five exact
`base_<type>_damage_taken_per_minute` Stats. Seven have direct references from
the inspected parent columns; all seven references are `Mods.BuffTemplate1`.
Rows are zero-based extract ordinals; native stat keys are Stats row plus one.

| Mod row / Id | Template row | Template default values | Descriptor values with supplied table-minimum Mod values |
|---|---:|---|---|
| 4072 `MonsterAuraEnemyLifeDegenRatTornado` | 83 | `[6000, -50, -50, -50]` | `[6000, -50, -50, -50]` |
| 7300 `ChaosDegenerationAuraPlayersUniqueBodyStr3` | 130 | `[0]` | `[27000]` |
| 7301 `ChaosDegenerationAuraNonPlayersUniqueBodyStr3` | 131 | `[0]` | `[15000]` |
| 7302 `ChaosDegenerationAuraNonPlayersUnique__1` | 132 | `[0]` | `[3000]` |
| 7303 `ChaosDegenerationAuraPlayersUnique__1` | 133 | `[0]` | `[3000]` |
| 7304 `ChaosDegenerationAuraNonPlayersUnique__1Royale__` | 134 | `[0]` | `[1200]` |
| 7305 `ChaosDegenerationAuraPlayersUnique__1Royale` | 135 | `[0]` | `[1200]` |

Template 83's keys are native 356, 2451, 8286 and 140: physical incoming
rate, Life regeneration, ES regeneration and ES recharge modifiers.
Templates 130–135 each map native 456, the chaos incoming base rate. The
`unique_deaths_oath_aura` definition is linked by the actual foreign row for
templates 130/131; same-name inference is unnecessary.

The other two selected templates are the previously retained
[Olroth RF template 352](client-dot-source-publication.md) and template 451
`InfiniteHungerSideArenaDebuff`. Neither has a direct reference in those ten
tables' declared template columns. This is a bounded reference result, not
absence of script, indirect, cached or other runtime use.

## 2. Native fields and numerical preparation

The packed schema gives Mods stride `0x28e` and BuffTemplates stride `0x8a`.
The previously retained Mod table fixup `0x1422b3ab0` independently binds the
primary template row/resource pair at `+0x24d/+0x255`, using the actual
`0x8a` row stride at `0x1422b47bd`. Its `+0x269/+0x271` array is a separate
secondary-template count/pointer. The selected numerical prefix concerns the
primary template only.

`0x1421ff3c0` supplies five retained direct calls to `0x1421ff8f0`, iterating
`0x38`-byte source records from separate owner vectors.
These caller/input records are retained without claiming their full upstream
creation or an actual target Entity.

The prefix constructs a `0x68`-byte record in destination vector
`+0x160/+0x168/+0x170`, distinct from the ordinary key/value aggregate at
destination `+0x140`:

| Original anchor | Verified operation |
|---|---|
| `0x1421ff942` | Test source Mod's primary template pointer at `+0x24d` |
| `0x1421ff9e4` | With reserved capacity, call original descriptor zero-constructor `0x142207aa0` |
| `0x1421ffa03` | Copy template's actual BuffDefinitions row pointer into descriptor `+0` |
| `0x1421ffa47` / `0x1421ffa56` | Copy AuraRadius and template `__col11` into descriptor `+0x10/+0x14` |
| `0x1421ffb27` | Store the mask derived from template `__col4` at descriptor `+0x48`; unnamed values remain unnamed |
| `0x1421ffb88`–`0x1421ffc13` | Copy template `Buff_StatValues` into descriptor vector `+0x18` using original vector move/destruction |
| `0x1421ffccb` | Call original key-to-template-slot helper `0x1424305a0` for each supplied Mod stat |
| `0x1421ffd78` | Matching slot: write the supplied signed 32-bit value directly into descriptor's value vector |
| `0x1421ffd52` | No matching slot: send the separately computed ordinary contribution to sorted key/value accumulator `0x142427880` |

The matching helper gets the Mod's selected stat row through `0x142430710`,
computes its native key through `0x142290090`, then scans primary template
`StatsKey` at count/pointer `+0x61/+0x69`. It returns the **first** matching
slot, or `-1`. The selected slot is determined by Stats identity, not merely
the Mod stat's ordinal. Native Stats handle-to-key conversion uses the
already recovered `0x69` Stats stride.

Mapped values are overwritten, not added to the template default. They also
bypass the ordinary branch's supplied scale/multiplier arithmetic in this
prefix. The seven actual rows leave no entries in the separate ordinary
aggregate in these controls. This is a **local preparation policy**, not a
claim that aura damage ignores every modifier, that all callers use the same
parameters, or that these ordinary aggregates are already actor Stats.

The independent table declarations explain why reading template `[0]` alone
would miss a concrete Mod-provided magnitude. Conversely, exporting every Mod
stat directly to the owner would lose this recovered separation. Any server
reconstruction should preserve this distinction before deciding how the
descriptor is applied to recipients.

## 3. Verification and remaining consumer

Thirty-two same-author original-instruction/scalar comparisons pass,
interpreting 17,270 instructions across 546 distinct original sites. They cover
the seven actual table rows, three supplied scale/multiplier pairs, explicit
zero values, a clearly synthetic partial key mapping with ordinary-stat
controls, and a real non-template Strength Mod. Original slot selection,
key conversion, descriptor zero-construction, vector move/destruction and
sorted ordinary accumulation execute in the interpreter.

Fixtures are explicit: reconstructed packed input handles and supplied Mod
values, destination/stack/allocation and reserved capacity, initialized TLS
and fallback state, an already-valid resource-handle getter, memory-copy/free
support, and an empty-range destructor. The prefix stops at `0x1421fff44`,
before secondary templates and later special Mod handling. It does not run
the whole upstream owner or create an ordinary gameplay Buff.

Canonical ignored evidence is under
`Tools/re/out/client-damage-audit/r75-template-parents/`. R19 VM/operand
definitions are reused by AST only; its sealed producer and initializer are
not executed. Fresh original-byte verification and the retained controls are
indexed in [the evidence manifest](client-damage-evidence.json).

The next relevant edge is a **numerical consumer of these prepared descriptor
records**, with concrete owner/recipient and application/removal semantics.
Existing template visual consumers dispatch to the already recovered
[resource dependency collector](client-addbuff-visual-dependencies.md); they
must not be relabelled target publication. Separate aggregate-rate transport
and [resource loss](client-network-rate-composition.md) remain valid downstream
contracts, without an invented connection to these descriptors.
