# Buff-backed inner-repeat inputs (3.29.3b)

> Frozen detailed reference from R81. Historical next-step language is provenance, not an active backlog.
> Current scope and unresolved obligations are in [the delivery matrix](delivery-matrix.md) and
> [verification and limitations](verification-and-limitations.md).

Part of [012 — projectile execution](../012-projectile-execution.md).
This closes two concrete `+58` callbacks feeding [release/repeat](release-and-repeat.md)
and the inner-repeat word in [random contexts](random-inputs.md).
They consume mutable, sequence-qualified Buff state; the maximum-repeat stat is insufficient.
These are static original-instruction/data-flow results, not observations of gameplay or official server timing.
The pinned PE identity and evidence conventions are in [the ledger](evidence-and-open-questions.md).

## 1. Positive definition-to-callback bindings

| Internal definition key | Zero-based table/wire row | BuffDefinitions.Id / Id2 | Factory table / creator | Backend table / `+58` |
|---|---|---|---|---|
| `4fc` (1276) | 1275 | `rapid_fire_charge` / `RapidFireCharge` | `14359ba28` / `141e0a150` | `1435c6e50` / `14205a090` |
| `502` (1282) | 1281 | `anticipation` / `Anticipation` | `14359bb58` / `141e0a210` | `14359bda8` / `141e09700` |

Addresses in the tables are preferred hexadecimal VAs, with the usual image base `140000000`.
Do not send the internal key as the zero-based row. Reader `141fdf870` passes the transported row
directly to `142454b20`, which indexes a 314-byte (`13a`) table row. Its resource helper
`1402365c0` names `Data/BuffDefinitions.dat`. Reverse lookup `142454bd0` divides the row-pointer
displacement by 314, then adds one; a null row returns sentinel `aff`, not zero.
The extract has 2,814 rows, SHA256 `3a23c6fdf7ba29fb8508e5c9560a44e5292345e45dd2f886ef7126b8427fd659`.

Startup `141e07370` registers the exact two UTF-16 Id2 strings. At `141e08379/8380`, it installs
Anticipation's factory table into singleton `144755c40`, then stores that object into the named map
entry at `141e083ce`. RapidFireCharge uses `141e0852d/8534`, singleton `144755af0`, and `141e08582`.
The retained factory contract is the damage research's fixed
`6fbdd9a6f1bcb853211de2c303680def72392f96`, `client-buff-default-backend.md`: lookup uses the
actual Id2, and a miss supplies no backend. That shared registry/lifecycle work is reused here.

Both concrete creators request 16 bytes, retain the actual Buff pointer at backend `+8`, and install
the backend table above. Allocation failure returns a null backend. Their selected lifecycle slots
`+8/+10/+18/+20/+50` all bind exact no-op `1400f0f00` (`c2 00 00`); this means no operation,
not a returned numerical zero. It does not suppress component notifications or arbitrary external listeners.

## 2. State fields and action admission

Within these methods, define:

```text
C = signed16(Buff[40])
L = uint16(Buff[42])
H = uint32(Buff[28])
Q = uint16(action[11a])
```

`H` is used as a consumed-count cache by Anticipation. These are context-specific uses of shared Buff
fields, not recovered universal field names. Constructor `141e06290` supplies `40/42` from its input
words `38/3a`; it does not universally initialize this state to zero.

Setup `1419e3bd0` retains the previously mapped admission rules: initial outer action, selected stat,
first active matching definition in the owner's Buffs vector, exact skill ID and action `118` match.
Missing skill means current skill ID zero; it does not make Buff skill zero a wildcard.
A null backend skips the call. Matching occurs before invoking `+58`.

For RapidFireCharge, selected stat `2342 support_rapid_fire_repeat_count` must be nonzero.
Its `+58` receives `Q`, and **true AL** allows the stat's low byte to overwrite action `2b9`.
For Anticipation, `2905 skill_max_unleash_seals` must be nonzero; its callback receives the action pointer,
and **returned AL itself** overwrites `2b9`. This second branch can overwrite the first.
Legal support combinations are not inferred. Finally setup copies prepared `2b9` to remaining `2b8`.

## 3. RapidFireCharge: sequence-qualified accumulation and consumption

`14205a090` implements the following ordered state machine. All returns below specify **AL**;
the method does not promise a clean Boolean in every bit of EAX.

```text
if C == 3 and L != Q:
    Buff[40:u16] = 0
    Buff[42:u16] = Q
    return true
if C == 0 and L == Q:
    return true
if L != Q:
    Buff[40:u16] = uint16(min_signed32(C + 1, 3))
    Buff[42:u16] = Q
return false
```

Thus an explicitly supplied `C=0,L=0` followed by sequences `1,2,3,4` returns
`false,false,false,true`, with counts `1,2,3,0`. Calling again with sequence 4 returns true
without consuming another charge. Repeating sequence 2 while its count is 2 returns false
and does not increment it. Sequence equality is exact u16 equality, not chronological comparison.
The count is sign-extended before increment/capping; do not replace this with unsigned saturation.
These are controls for specified inputs, not claims about which initial state official gameplay supplies.

## 4. Anticipation: consume once, replay the cached count

`141e09700` implements:

```text
if C > 0 and L != Q:
    Buff[40:u16] = 0
    Buff[42:u16] = Q
    Buff[28:u32] = C
    return uint8(C)
if C == 0 and L == Q:
    return uint8(H)
return 0
```

A supplied count 8 and different sequence therefore yield 8, clear the count, save the sequence,
and cache 8. Reentry with the same sequence yields 8 again. A different sequence with no new count yields
zero and does not overwrite the last sequence/cache. Positive count with an already equal sequence yields
zero; the cache fallback requires **count exactly zero**, not merely nonpositive.
The action consumes AL, so 256 is consumed/cached as 256 but supplies inner byte zero.
Large/negative values are boundary controls, not evidence that legal builds can acquire them.

Neither callback contains a call, RNG transition or TLS access. They deterministically transform
the supplied state. Different state/callback ordering can nevertheless change `2b8`, and therefore
the eight-word motion/Fork/Return/Split/Chain-source random recipe and subsequent emitted stages.
These two callbacks do not directly write source ordinal `448` or synthetic flag `1e8`.

## 5. A concrete network input route, including overwrite semantics

For an existing Buff instance, the `0x1c1` partial-update route reused from the damage research's
`client-buff-network-clock-contract.md` positively reaches these additional writes:

| Decoded message field | Existing Buff write | Write site | Reader contract |
|---|---|---|---|
| `64:u32` | `28:u32` | `141e11b95` | Unconditional transported u32, byte-swapped at `141fdf8ec/8f2` |
| `60:u16` | `42:u16` | `141e11ba4` | Unconditional transported u16, byte-swapped at `141fdf90b/911` |
| `70:u16` | `40:u16` | `141e11b9c` | Flags `04` supplies u16; absence decodes **1** at `141fdfa8f` |

The receiver copies all three on this partial branch; omission of the count is not “keep current count”.
Its later backend `+10/+20` callbacks are no-ops for both concrete tables above. External publication
and component subscribers remain separate boundaries. The route matches the existing instance by its
instance key; it does not choose one by these overloaded fields or install a replacement backend here.

The generic protocol labels for `64/60` (source object/skill in other Buff uses) must not replace their
actual semantics in these repeat callbacks. This is a supported synchronization route, not evidence of
the official server's chosen values, cadence, correction policy or every producer of those fields.
Updating the selected count stat alone does not supply the complete state used by these methods.
Conversely, the local count-consumption rule does not demonstrate any C2S Buff-state report.

## 6. Evidence and remaining obligations

Private round36 retains original bytes/ranges in `repeat-sources-audit.json` and
`repeat-sources-original.asm`, the original-instruction checker `check_repeat_sources.py`,
`repeat-sources-controls.json`, and a separate same-agent `review_repeat_sources.py/json`.
The byte audit retains twelve bodies/leaves, twelve aligned ranges, 3,093 decoded instructions
and fourteen factory/backend slot bindings; reused bodies are not counted as newly discovered functions.
The two slot methods have a closed direct CFG; RapidFireCharge is a no-unwind leaf bounded by its
positive table target, internal branch targets and terminating returns, not by guessed `.pdata` extent.

All 65,536 count bit patterns, same/different sequence, and both callbacks give 262,144 comparisons;
720 further boundary cases, four stateful flows, four factory controls and 2,815 reverse-index controls pass.
The run interprets 3,581,766 original instructions. Buff/action inputs are explicit fixtures;
factory allocation and already-resolved table handles are fixtures. The slot bodies themselves contain
no fixture calls. Network input mapping is original-byte/data-flow evidence, not part of that execution count.
Fresh review checks 31 extra anchors, both registration bindings and 16 separately specified literal cases.
This is not native execution or a claim of an independent worker review.

Still open: actual instance creation/replacement and server-supplied values for each witness, other local
writers/replenishment, external observer consequences, correction/callback ordering and resulting runtime
event admission. The completed local transformations replace “unknown actual callback” for these two
backends; they do not close the full synthetic startup, TLS stream envelope or official server implementation.
