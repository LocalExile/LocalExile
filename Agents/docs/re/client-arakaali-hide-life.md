# ArakaaliHide: conditional integer Life targeting (3.29.3b)

One previously unclassified Life integer-delta caller belongs to the registered
`ArakaaliHide` action. Its selected art-variation branch targets approximately
65% maximum Life, subject to the real pool's recovery restrictions and capacity.
It is a concrete numerical resource mutation, not the missing ordinary
accepted-Hit damage/mitigation resolver. R2/R3/R4 remain Partial.

The evidence is offline original PE bytes and interpreted original instructions.
No client was launched, attached or executed natively; no traffic was captured.
The executable SHA256 is
`78a0e227189c263658a7f65a02f086428b859bfc114ae8e7ce8aab721ebd48d5`.
The Stats extract SHA256 is
`91c2ed42f6d8094a2704f56aea32f6f16243881b04aadbe4fdbbdd3a566f05da`;
native keys are zero-based rows plus one. Addresses use preferred base `0x140000000`.

## 1. Positive action and pool identities

Two original registration bodies pass the same ASCII key at `0x14325d8a8`:

| Registration | Factory supplied to `0x141941bb0` | Selected installed table |
|---|---|---|
| `0x14126a530` | `0x14128e740` | `0x143258158` |
| `0x141973880` | `0x14198a2d0` | `0x143258158` |

Both factories have earlier conditional alternative branches; this report follows
their selected final allocation paths, not every alternative or a claimed startup
registry winner. They initialize action byte `+0x318` to zero and install this
table. Its slot `+0x148` is the actual body `0x141afe5a0`.

On this body's zero-`+0x318` path, action `+0xe8` supplies the owner context,
then context `+8` the Entity. The selected skill provider uses action byte
`+0x174`; getter key 349 is `skill_art_variation`. Only a returned value of
exactly 1 enters the `stance2` branch and its Life operation. The Life component
is resolved through `0x14013dda0`.

The existing Life constructor `0x141e678d0` binds embedded pool `Life+0x178`
to table `0x1435a5da0`; its slot `+0x28` is integer-delta method `0x141e699d0`.
Therefore the indirect call at `0x141afe706` has a positively typed receiver.
Maximum/current reads at Life `+0x1a4/+0x1a8` are this pool's `+0x2c/+0x30`.
See [integer pool contracts](client-integer-resource-mutations.md).

## 2. Exact numerical contract

For nonnegative signed32 current `C` and maximum `M`, this selected path is:

```text
if C > M: skip the Life call
P = unsigned64(C * 100) / unsigned64(M), or 0 when M = 0
if signed32(low32(P)) >= 65: skip the Life call

K = binary32 at 0x1436a49b8, bytes 66 66 26 bf
  = -0.6499999761581421
Q = truncateTowardZero(binary32(binary32(M) * K))
delta = signed32Wrapping(-C - Q)
LifePool.integerDelta(delta, context=0, reason=14)
```

The original unsigned division, signed comparison, float32 conversion/product
and signed32 delta are retained separately. The negative coefficient means that
ordinary small inputs produce a positive increment toward about 65%, rather
than sacrificing 65%. This is not a general damage, recovery-rate or DoT formula.
Negative current/maximum domains and malformed-input division faults are outside
these numerical controls; do not generalize the nonnegative spelling to them.

With maximum 1,000 and current 100, delta is +550 and the ordinary unreserved,
recovery-enabled pool reaches 650. Current 649 reaches 650; current 650 skips the
call. Actual pool policy still applies: current zero remains zero, recovery block
byte `+0x24` rejects the positive increment, and supplied nonzero 6981
`virtual_keystone_vaal_pact` also rejects it on the tested reason-14 path.
With 500 Life reserved, the first example clamps to 500, not 650.
Fractional carry is preserved; Mana and ES are unchanged in these controls.

Do not implement an unconditional nonnegative delta or an ideal exact 0.65
multiplier. For a supplied maximum 16,777,217 and current 10,905,191, the integer
ratio still passes but float32 targeting produces delta -1 and Life 10,905,190.
Maximum 2,147,483,647 and current 1,395,864,370 gives delta -50.
These are arithmetic boundary witnesses, not claims about legal encounter values.

After this branch, the original body sends an Actor request whose leading kind
is 43, then sets action `+0x318` to one. The nonzero-latch entry delegates to
`0x1419e7190`; that parent's effects are not reconstructed here. Actor request
and stance handlers are external observational fixtures in the controls, so their
transitive effects and upstream action invocation remain separate.

## 3. Verification and authority limits

Private batch `r57-arakaali-heal` imports retained integer-interpreter definitions
without invoking their producer mains. **462 supplied-state controls / 61,732
interpreted original instructions / 274 visited original sites** pass against a
separately spelled scalar contract. The full original zero-latch action body,
actual Life integer wrapper and absolute setter execute as interpreted instructions.
This is same-agent verification, not an independent worker or native replay.

Supplied fixtures cover the Action/Actor/Entity/provider graph, component/provider
resolvers, input Stats, reservation helper, string construction, observational
stance/request handlers and nonmutating notifications. Directed controls cover
the art gate, 65% threshold, above-maximum guard, zero maximum/current, reservation,
recovery restrictions and float32 boundaries. Lower-Life threshold inputs are zero.

Fresh checking reproduces **10 identities / 18 ranges / 773 decoded records**,
36 instruction anchors, two pointer slots, the exact scalar/string bytes and
three named Stats. All 274 visited instruction starts are checked against the
original image. Four newly indexed function identities are two registrars and
two factories, not four newly recovered damage mechanisms.
The manifest retains [original-byte provenance](client-damage-evidence.json).

This closes the classification of one retained R22 resource-write candidate.
It does not establish an ordinary network invocation, official-server behavior,
a generic local hit predictor, source-instance DoT aggregation/publication,
shared-cap reachability or generic existing-Buff clock refresh. A native numerical
method's presence alone does not assign final authority to the client.
