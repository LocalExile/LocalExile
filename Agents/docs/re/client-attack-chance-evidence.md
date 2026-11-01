# Adopted attack hit-chance evidence (3.29.3b)

## Result and ownership

The parallel projectile investigation has recovered the complete selected **stable-input attack hit-chance
expression**, its finite native power path and its handoff to target entropy. This investigation now adopts
that numerical evidence. The earlier main-report statement that full accuracy/evasion composition and power
were still open is superseded for this bounded contract. This is evidence reuse, not a new formula discovery.

It closes neither numerical Hit damage nor a critical trial: accepted Hit amount, defender mitigation and pool
deduction remain unbound. Actual stat publication/cache timing, other caller classes and correction policy retain
their own limits. Original R0/R1 remain Established within their audited contracts; R2/R3/R4 remain Partial.

The canonical peer chapter is `Agents/docs/re/projectiles/attack-hit-chance.md` at fixed peer revision
`53b29759f3ae9cf87095ae148f284b8c182bc134`. Its SHA256 is
`b69f5dcf961615d601c41e7ba1b3ebbe075034941e8447d225f52a275b53c6ba`.
The same chapter bytes were already present in this investigation's R60 source snapshot; that snapshot did not
include the R32 numerical audit. This pass adds the original numerical support and corrects its coverage index.

No client was started, attached or run as native code. No captures, server implementation or CI changes were made.
The executable and Stats identities are those of the
[main investigation](client-damage-calculation.md#artifact-identity-and-reproducibility).

## Reusable calculation contract

The original builder is `0x141ebc990`. Its inputs are attacker Entity, selected attacker and target providers,
target Entity, hand selector, caller-projectile flag and a probability callable. It returns a Boolean from the
callable or an ordered early-success condition; that Boolean is not a damage amount.

| Boundary | Recovered behavior |
|---|---|
| Hand | Selector 0 reads off-hand accuracy/always-hit inputs; selector 1 reads main-hand inputs. |
| Early success | Hand always-hit, target cannot-evade, burning, blinded and sword-qualified conditions have ordered shortcuts. They skip probability invocation but still dispose the callable. Outer dodge/target gates remain separate. |
| Attacker conditions | Unique, owned-mark, close-range and monster qualifications modify accuracy in order. Factors round as binary32; the optional final accuracy rounding is `floor(f32(value + 0.5))` in its finite in-range domain. |
| Owned mark | A retained target Buff must have removal bit clear, definition class 14, and owner ID equal to the attacker. An unrelated owner's mark does not qualify. Multiple matches do not repeatedly multiply the modifier. |
| Close range | Wrapped integer squared distance is compared unsigned with 400, including equality. The ordinary distance boundary is 20 native coordinate units. |
| Defender base | `keystone_voranas_adaption` substitutes twice Ward for ordinary evasion and skips the projectile-evasion-keystone multiplier. Otherwise projectile and melee classification qualify separate evasion factors. |
| Effective level | Signed wrapping integer multiplication precedes binary32 conversion and division. The base helper consumes zero-extended u32 result bits; negative intermediates are not silently clamped. |
| Final chance | Virtual evade is added before generic/projectile/melee final modifiers. Generic multiplication precedes division by 100; base and final chance clamps are separate. |
| Entropy | The selected callable receives chance plus final accuracy/evasion integers. The original entropy body truncates `f32(chance * 100)` before changing the target accumulator and its timer. |

For u32 effective accuracy `A` and evasion `E`, original helper `0x1424ff510` returns `0.75f` when both are zero.
Otherwise, with binary32 rounding at each operation:

```text
x = f32(f32(E) / 5)
p = NativePowF(x, float32_bits(0x3f666666))
baseChance = clamp(f32(f32(f32(A) / f32(f32(A) + p)) * 1.25), 0.05f, 1f)
```

Divisor five, exponent bits and multiplier come from original PE data. Do not replace this with another
wiki-era formula. Native power `0x142d79cb0` has ordinary and AVX/FMA paths; CPU dispatch and floating-point
environment are explicit offline inputs. The peer's finite nonnegative domain does not certify an arbitrary
library `Pow` replacement, all IEEE special values or every runtime environment.

These qualifications complement [contact/avoidance production](client-hit-production.md) and
[critical-stat calculations](client-critical-formulas.md). They are separate from both critical confirmation
and the [unresolved numerical Hit-to-resource edge](client-hit-resource-boundary.md).

## Receiving-side verification

Fixed peer manifest SHA256 is
`38de91369daf55a11ef6ffea4ce9403a0ada509a842db938feb65b83b03df24e`.
All 55 snapshot files, including 39 exact Git sources, were checked against their lengths and SHA256s;
the 39 source records also matched the fixed Git blobs. Parent-manifest hashes were checked separately.
The private copy retains the source's original provenance without publishing machine-specific paths.

Fresh original-byte verification checks ten functions/leaves, ten ranges, 1,386 decoded instructions,
two callable bindings, 31 constant operands, 594 retained binary data reads and 32 exact Stats names.
The two newly indexed leaf ranges have actual caller anchors and checked terminal/direct-branch boundaries:
`0x14013d550..0x14013d56c` and `0x140664f10..0x140664f47` (end exclusive).

Twenty-two focused controls use definition-only reuse of the peer interpreter. They execute original
instructions for both explicit CPU dispatch values, covering main/off hand, owned/other-owner/removed marks,
Ward substitution, close-range equality/exclusion, virtual evade, the actual entropy callback and a
cannot-evade shortcut. Main-hand A=3/E=5 yields `0.9375`; the supplied off-hand A=1 yields `0.625`.
With initial entropy 37, the first case adds 93, succeeds and leaves 30, with timer three seconds.

These controls visit 635 original sites over 8,497 interpreted instructions. Stat/component lookup and callable
disposal remain disclosed fixtures; stable inputs and CPU dispatch are supplied. No sealed producer main or
output-writing module initializer is executed. This is a fresh adoption check using the same interpreter,
not a second independent execution engine. The peer's 2,958 broader controls remain attributed peer results.

Private reproduction is `Tools/re/out/client-damage-audit/r71-adopt-hit-chance.py`, with retained audit,
controls, fixed peer snapshot and verification under `r71-attack-chance-adoption/`.
The [evidence manifest](client-damage-evidence.json) records identities and supporting-file hashes.
Previously sealed evidence is preserved; this adoption does not relax the original completion criteria.

## Next boundary

Use the established probability/entropy contract directly. Repeating its formula, power implementation or
the same immediate/cache scans will not identify a numerical damage consumer. Projectile caller, input
publication, RNG and entropy correction stay with their owner. This investigation continues after acceptance:
actual amount and critical/mitigation-to-pool, DoT instance selection/publication/shared cap/withdrawal and
timing/authority across those connections. A successful probability callback supplies none of those edges.
