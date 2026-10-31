# Direct resource-setter sources and selected LegionBreakout Life targeting (3.29.3b)

The retained direct-caller inventory for absolute pool setter `0x141e698e0`
contains **24 callsites in 18 owners**. Its one newly followed variable operation
is guarded by the target's current `LegionBreakout` action and ordinarily targets
1 Life. It does not recover the missing accepted-Hit amount/mitigation path.
R2/R3/R4 remain Partial.

This is offline original-byte research and interpretation, not client execution,
capture acquisition or official-server observation. Executable SHA256:
`78a0e227189c263658a7f65a02f086428b859bfc114ae8e7ce8aab721ebd48d5`;
preferred base `0x140000000`. Stats SHA256:
`91c2ed42f6d8094a2704f56aea32f6f16243881b04aadbe4fdbbdd3a566f05da`.

## 1. Finite direct-caller classification

This pass reuses the R21 E8/E9 inventory instead of repeating a whole-image
search. Each retained call and its local value-producing instructions is checked
against the original bytes and instruction boundaries.

| Setter-input category | Calls | Boundary |
|---|---:|---|
| Literal current zero, reason 14 | 12 | Each selected slice has `mov r8b,14; xor edx,edx` before the call; upstream qualification and whole-caller purpose are not classified together |
| Target-current-derived operation in `0x141c46e00` | 4 | Selected LegionBreakout guard and inlined Life policy recovered below; two sites execute in the supplied controls |
| Maximum refresh in `0x141e697e0` | 1 | Reuse [maximum refresh](client-pool-maximum-refresh.md) |
| Life/Mana/ES integer wrappers | 6 | Four Life, one Mana and one ES setter calls; reuse [integer pool contracts](client-integer-resource-mutations.md) |
| Resource payment in `0x141e6ca30` | 1 | Reuse the separately guarded payment contract |

The twelve literal-zero sites are `0x1413f37f5`, `0x14166a036`, `0x141819f11`,
`0x14195eefc`, `0x141a962a0`, `0x141ac573c`, `0x141d1344b`, `0x141d34a8a`,
`0x141d35541`, `0x141d46f83`, `0x141e1928a` and `0x141e19315`.
Their fixed setter argument does not prove that all preceding code or downstream
notifications are unrelated to combat. This classification is not a census of
indirect/inlined writes, dynamically selected receivers or unwindless code.

## 2. Actual guard and Life receiver

Body `0x141c46e00` receives a supplied small object whose first pointer is a source
action, and a candidate target Entity. Its original guards require:

1. The target type's component-name lookup contains ASCII `Monster` at `0x1435a8eb0`.
2. Target and source action's owner Entity are different.
3. Target Actor `+0x1b0` holds a current action with word `+0x118 == 0xd8fe`.
4. That action's `+0x350` pointer is nonnull; following its `+0x20` pointer yields
   a dword whose **unsigned** value is at most 1.

The exact ActionTypes row 996 is `LegionBreakout`, HASH16 55,550 / `0xd8fe`.
The 1,351-row extract has SHA256
`eef7b004b13a3f32e487d5c102c41362b62131f83249f45194ece05b8518d9d6`.
The stored dword's producer and gameplay meaning remain unbound; do not name it
a particular Legion phase merely because of the adjacent action identity.

After those guards, the original Life resolver `0x14013dda0` supplies the component.
The receiver is its embedded pool `+0x178`, with established table `0x1435a5da0`.
Its `+0x50` leaf `0x1400ecfa0` returns the floor 1. The source Entity's `+0x88`
identifier is retained as callback context.

## 3. Numerical operation and policy differences

For the controlled nonnegative current `C`, this installed floor gives:

```text
delta = -max(signed32Wrapping(C - 1), 0)
if C <= 0: return without the Life operation
```

The body then **inlines selected Life policy**, rather than calling the generic
Life integer-delta method. With zero minimum-Life input and nonmutating callbacks,
ordinary eligible positive Life reaches 1 through the actual absolute setter.
Mana, ES and fractional carries stay unchanged in these controls.

For positive key 3240 `cannot_drop_below_%_life`, it computes in original order:

```text
Q = truncateTowardZero(f32(f32(f32(S[3240]) / f32(100)) * f32(maximumLife)))
if signed32Wrapping(C + delta) <= Q:
    adjustedDelta = signed32Wrapping(Q - C + 1)
    notify selected mutation boundary
    apply adjustedDelta unless recovery-block rejects its positive increment
    invoke pool +0x68 with minimum percentage and source Entity identifier
else:
    continue the remaining inlined policy and setter path
```

Maximum/current 1,000 with 3240=0 reaches 1. With 3240=50 it reaches **501**,
not 500. Current 1, 3240=50 and recovery-block byte `+0x24` set remains at 1,
but still reaches the threshold callback. Reservation can constrain the setter's
result independently. In the selected zero-minimum branch, nonzero 401 `cannot_die`
sets Life bytes `+0x268/+0x26d` on the first transition from above 1 to 1.

The callback slot `0x1435a5da0+0x68` is `0x141e6b280`; its original body includes
separate one-shot and script/event consequences. Those transitive consequences
are observed as an external ABI fixture here, not reconstructed or declared empty.
The same applies to the mutation notification helper and setter notifications.

One original caller, `0x141c496d0`, invokes this body at `0x141c4985e` only on a
selected zero-return branch after `0x141d87160`. Its other component, relation,
coordinate and provider checks remain upstream evidence, not interpreted
eligibility or a complete action attachment. No numerical Hit amount or mitigation
is supplied by the recovered current-to-floor calculation itself.

## 4. Verification and remaining work

**438 supplied-state controls / 63,879 interpreted original instructions /
210 visited original sites** check the complete selected body and actual absolute
setter against a separately spelled scalar contract. Cases cover the Monster,
different-owner, current-action, hash, nonnull-holder and unsigned-value guards;
zero/one/above-maximum Life; recovery block, reservation, minimum percentage and
cannot-die inputs. Of the four retained setter sites in this body, only
`0x141c46f94` and `0x141c470c3` execute under these installed-floor/nonmutating states.
Do not count the other two sites as tested paths or additional mechanics.

Fresh verification reproduces **six identities / twelve ranges / 530 decoded
records**, 31 anchors, two pool slots and all 24 retained callsite contexts.
Three new manifest entries are the selected body, its caller and threshold
callback; only the stated numerical subset is interpreted. Existing 1,136 sealed
supports remain unchanged. See [the manifest](client-damage-evidence.json).

Action/Actor/Entity/provider objects, component lookup, the byte-checked return-one
leaf, reservation and observational notification/script boundaries are explicit
fixtures. This is same-agent original-instruction/scalar verification, not an
independent worker, native replay or proof of ordinary network invocation.

The finite list is now classified at its setter-input boundary. Continue from a
different actual numerical receiver/producer or a stronger upstream connection;
do not rescan these same sites as unidentified Hit amounts. Accepted-Hit numerical
resolution, actual DoT source selection/publication/shared cap, generic existing
Buff-clock refresh and their missing timing/authority connections remain open.
