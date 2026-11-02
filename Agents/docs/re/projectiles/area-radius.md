# Shared area-radius arithmetic and Fireball/KB inputs (3.29.3b)

> Frozen detailed reference from R81. Historical next-step language is provenance, not an active backlog.
> Current scope and unresolved obligations are in [the delivery matrix](delivery-matrix.md) and
> [verification and limitations](verification-and-limitations.md).

Part of [RE 012](../012-projectile-execution.md).
These are static client calculations, including presentation radius; they do not establish authoritative damage.
Preserve cached final-stat paths, integer rounding and float32 instruction order when comparing later traces.

## 1. Inputs and actual final bindings

| Native stat key | Stats name |
|---|---|
| `0x16c4` | `active_skill_base_radius_+` |
| `0x1591` | `total_skill_area_of_effect_radius_+%_including_final` |
| `0xd7` | `skill_area_of_effect_+%` |
| `0x1670` | `skill_area_of_effect_+%_final` |
| `0x16ee` | `active_skill_area_of_effect_radius_+%_final` |
| `0x28d7` | `blight_tower_skill_area_of_effect_radius_+%_final` |
| `0x4875 / 0x4876` | `active_skill_base_area_of_effect_radius` / `active_skill_area_of_effect_radius` |
| `0x4878 / 0x4879` | `active_skill_base_secondary_area_of_effect_radius` / `active_skill_secondary_area_of_effect_radius` |

These are one-based native keys, not zero-based JSON row numbers.
Getter offsets below are **vtable slots**, not action fields; in particular slot `+0x330` is unrelated to
Tornado Shot's action-field `+0x330` continuation flags.

| Getter slot | Fireball table `0x14333ac40` | KB table `0x14326b9c0` |
|---|---|---|
| `+0x328` | `0x1400ecd70`, constant zero | same |
| `+0x330` | `0x1419e7730` | `0x141b57320` |
| `+0x338` | `0x1419e77e0` | `0x14123d6e0` |

Let `A = slot330()` and `M = slot338()`.
The initial `A` is action integers `+0x30c + +0x234`.
Both getters add an amount per completed repeat, using `total2d4 - remaining2d0`:
Fireball uses stat `0x25e0`, KB uses `0x3194`.
Fireball also adds stat `0x3b33` when total repeats are nonzero and remaining repeats are zero.
The `total2d4 > 0` branch is an unsigned nonzero check; do not impose an extra signed-positive restriction.
These are reachable input branches, not proof that every named modifier applies to both skills in ordinary play.
Selected cached-field producers and lifetimes are mapped below; their full upstream stat admission remains separate.

### 1.1 Action-field producers, transfer and recomputation

These are action caches, distinct from the final-radius stat pairs consumed in section 5.
The selected construction/copy/transfer paths establish:

| Field | Producer and lifetime |
|---|---|
| `+0x234 / +0x238` | Base ctor `0x141937f0c` clears both; common copy `0x14193851e/526` transfers them together |
| `+0x14c` | Base ctor clears it; common copy preserves it; context receiver copies nonzero input `+0x18` into it |
| `+0x2bc` | Area ctor computes an intensity contribution from a matching owner entry; area copy preserves it |
| `+0x2cc` | Area ctor captures owner stat `0x57e8` under its mine/trap gates; area copy preserves it |
| `+0x30c` | Area ctor and copy explicitly clear it, then call `0x1419e3030`; that helper conditionally recomputes it |

In `0x1419e2a50`, intensity requires nonzero `virtual_skill_gains_intensity` (`0x3182`) and
`support_spell_boost_area_of_effect_+%_final_per_charge` (`0x2378`).
The owner component returned by `0x1401ebc40` is searched over its `+0x160..+0x168` entries, skipping
entry flag `+0xd0 & 0x10`, requiring type `0x500`, and matching skill and action identifiers at `+0x48/+0x4a`.
The selected entry is converted by `0x141e092a0`; its signed16 quantity at `+0x40`, when nonzero,
is multiplied by stat `0x2378` with signed32 wrapping and stored at `+0x2bc` (`0x1419e2c47`).
This is a construction-time contribution, subsequently copied; it is not a fresh live quantity lookup in each radius getter.
The checked KB getter does not consume `+0x2bc`, whereas Fireball's getter does.

The same ctor separately asks `0x14013de10` for an owner component and skips this branch for action hash `0x7b44`.
Its selected stats must have nonzero `0x36c`, or, if that is zero, nonzero `is_trap_object` (`0x9bf`).
The first branch also captures `mine_detonation_cascade_index` (`0x27e3`) at `+0x2c8`.
Both accepted branches capture `trap_or_mine_duration_before_triggering_ms` (`0x57e8`) at `+0x2cc`.
This cached value is not a continuously advancing wall clock.

Helper `0x1419e3030` skips its computed-stat overlay body when startup global `0x1445b38cd` is nonzero,
but jumps to `0x1419e34bc`, **before** the following tail:

```text
if action.int2cc != 0:
    perSecond = currentStat(0x57e7)
    if perSecond != 0:
        seconds = truncSigned(action.int2cc / 1000)
        action.int30c = minSigned(100, w(seconds * perSecond))
```

Stat `0x57e7` is `trap_and_mine_area_of_effect_+%_per_second_placed_up_to_100`.
The native signed division uses high multiplication by `0x10624dd3`, arithmetic shift six and sign correction.
The upper clamp does not add a zero lower bound, and multiplication wraps before the clamp.
If either gate is zero, this invocation leaves the existing `+0x30c` unchanged.
Ctor/copy therefore differ from later calls: they reset before recomputation, while transfer helper
`0x1419e42b0` copies `+0x2bc/+0x2cc` and invokes recomputation without first clearing the recipient's `+0x30c`.
For example, a prior value 77 survives a zero-gate later call, but a fresh copy starts at zero.
Startup disabling of the overlay cannot be generalized to suppression of these cached area inputs.

Helper `0x14193b680` is a separate donor-to-recipient contribution path.
It requires donor byte `+0x230` to be nonzero, marks/records the recipient's sequence and processes the donor's
count-preservation chance before querying **the donor's** virtual slots `+0x3f0/+0x3f8`:

```text
recipient.int234 = w(recipient.int234 + donor.virtual3f0(recipient.sequence))
recipient.int238 = w(div100(w(w(recipient.int238+100) * w(donor.virtual3f8(recipient.sequence)+100))) - 100)
```

The Fireball/KB final tables return zero from these slots when acting as donors.
This does not prove their recipient fields stay zero: a different donor has its own table and admission rules.
Alternate helper `0x14193b870` copies a donor's contribution fields rather than adding them.
Full donor-class admission, all writers and context-input producers remain outside this bounded cache investigation;
no particular support/warcry is declared functional merely from these recipients.

### 1.2 Native final-value callbacks upstream of the action getters

Three registered formulas supply values that the action's cached branches can consume:

| Key | Registration | Callback | Inputs |
|---|---|---|---|
| `1591` | `0x1400bac10` | `0x142749620` | `1671,16ee,28d7,4da0,29c8` |
| `4876` | `0x1400bac70` | `0x142749be0` | `4875,16c4,1591` |
| `4879` | `0x1400bacd0` | `0x14274a000` | `4878,16c4,1591` |

These declared dependencies match the selected bodies' actual stat reads.
`S[k]` below is a stable native computation-context input, not an instruction to recalculate a supplied
network final value. Formula registration, transaction activation and effective overrides remain separate
[Stat contracts](actions-and-emission.md#62-raw-notifications-and-computed-transactions-are-separate-propagation-mechanisms).
Use section 2's wrap/float32 conventions, with `CVTT32` retaining native invalid-conversion behavior:

```text
factor(x) = max32(0f, f32(f32(w(100+x)) / 100f))
root = ISqrt(u(w(max_signed(0,w(S[1671]+100)) * 100)))
v = f32(f32(root) * factor(S[16ee]))
v = f32(v * factor(S[28d7]))
v = f32(v * factor(w(S[29c8]*S[4da0])))
v = f32(v - 100f)
S[1591] = CVTT32(f32(v + (v<0 ? -0.5f : +0.5f)))
```

`1671` is `total_skill_area_of_effect_+%_including_final`, upstream of the integer root;
it is not raw `d7` or the separate more-area input `1670`.
`4da0 active_skill_has_skill_area_of_effect_+%_final_per_additional_projectile` multiplies
the [additional projectile intermediary `29c8`](count-stat-inputs.md) after the root.
It does not use total projectiles or additional arrows, and its factor must not be moved inside the root.
The multiply order is root → final radius → tower radius → additional-projectile factor.
The final sign-dependent half then truncation rounds ties away from zero in the ordinary finite domain;
it is not the `Nearest` helper used by the action calculations below.
There is no final nonnegative clamp or positive saturation in `1591`.

For example, total area 300 produces radius percent 100 (twice the radius);
total area 2 still produces zero because the integer root is 100.
Total area 3 with `16ee=-50` yields `-49.5 → -50`; with `16ee=50`, `51.5 → 52`.
Four additional projectiles and `4da0=10` give radius percent 40 without other modifiers,
or 180 with total area 300. These synthetic examples establish arithmetic, not every combination's eligibility.

For `4876/4879`, choose base `S[4875]` / `S[4878]`, respectively:

```text
if base == 0: return 0
value = f32(f32(w(base+S[16c4])) * factor(S[1591]))
if value <= 0: return 0
if value >= 2147483648f: return INT_MAX
return CVTT32(value)
```

The zero-base gate precedes the flat addition; a zero base does not become nonzero just from `16c4` here.
A negative nonzero base can become positive after the wrapped flat sum.
Unlike the action helper in section 4, these final stat callbacks saturate at **signed** `INT_MAX`.
The body rereads base after the gate; the equation assumes stable/cache-ready inputs.
Fireball's final primary path and KB's primary/secondary paths can consume these outputs directly,
while additional action contributions take the distinct recomputation branches in section 5.
Do not replace all dynamic action getters with this callback formula.
The immediate `1671` composition is recovered below; its further upstream terms, actual wire publication
and transaction activation remain separate obligations.

### 1.3 Aggregate area precedes the integer-root radius formula

Registrar `0x1400babb0` binds `1671 total_skill_area_of_effect_+%_including_final` to `0x1427493d0`,
with declared and actual direct inputs `d7 skill_area_of_effect_+%` and `1670 skill_area_of_effect_+%_final`.
For stable inputs, using the conventions above:

```text
factor = max32(0f, f32(f32(w(S[1670]+100))/100f))
v = f32(f32(w(S[d7]+100))*factor)
v = f32(v-100f)
S[1671] = CVTT32(f32(v+(v<0 ? -0.5f : +0.5f)))
```

This callback has no square root, no lower clamp on the `d7+100` side, and no final nonnegative clamp
or positive saturation. The separate next-layer `1591` applies its signed clamp, wrapped multiplication
and unsigned integer root; do not move those operations across this rounding boundary.
For example `d7=300,1670=50` gives aggregate 500, then root 244 and radius percent 144 before other factors.

Immediate producers `0x142745c70` (`d7`) and `0x142747740` (`1670`) are separately decoded:
the former uses integer sums/products, conditional terms and bounds; the latter constructs 24 nonnegative
float32 factors and multiplies them in a fixed instruction order before sign rounding.
The 54/31 declared dependencies are not complete actual-read inventories. Current charges, conditional
raw gates and total projectile count appear outside them. The [complete increased-area expression](area-increased-terms.md)
and [ordered more-area expression](area-more-factors.md) are now compared independently.
Inputs must not be reduced to a sum/product of names from the registration arrays.

One concrete projectile interaction is established: `1670` admits factor
`5399 area_of_effect_+%_final_for_bow_attacks_firing_single_projectile` when `S[3]==9 && S[4b4b]==1`.
It reads total **projectiles**, not total arrows `4b4c`. Synthetic controls distinguish one versus two,
the wrong weapon category and an arrow count of one that cannot substitute for projectile count.
The selected `d7` tincture/recent-kill contribution also does not acquire an extra weapon-attack gate
merely because `4d77` appears in declared dependencies. These selected branches do not establish
every modifier's official grant/eligibility rules.

Round24 `area-upstream-audit.py/json` checks seven functions / seven ranges / 3660 instructions,
three registrars / 87 dependency entries / 38 anchors. `area-upstream-numeric.py/json` compares
2059 aggregate cases with an independent formula; 13/15 upstream explorations are explicitly partial.
Root independent review adds eleven expected aggregate controls and seven selected-input controls.
Action intensity/repeat caches, actual provider refresh, transaction activation and supplied final values
retain their separate contracts; this does not authorize recomputing a network final unconditionally.
Round25 extends the more-area side to all 24 factors and qualification rules, with 1598 original-instruction
comparisons and 38352 bit-exact multiplication prefixes. A constructed order witness returns 17065
in native order and 17064 in reverse order; this is not an observed gameplay discrepancy.
Round26 closes all 34 increased-area contributions, with 3875 original-instruction comparisons and
127875 intermediate addition prefixes. Twenty local qualifications, signed rage/rampage division,
two count caps and three wrapped-product caps are retained; final addition has no saturation.
The 64 actual inputs differ from the 54 declared dependencies. Neither formula closes upstream
grant eligibility, provider activation or actual wire publication.

## 2. Float and integer helper conventions

The pseudocode below covers ordinary finite inputs where float-to-signed-integer conversions are representable.
`f32(x)` means rounding at that operation, including integer-to-float conversion.
`w(x)` means the result of 32-bit integer arithmetic interpreted as signed; `u(x)` interprets the same bits unsigned.
`div100(x)` truncates signed division toward zero, not floor.
`Nearest(x) = floor(f32(x + 0.5f))` in this domain; it is not banker's rounding.
The actual helper is `0x140664f10..0x140664f46`.

Integer-root helper `0x141937840..0x141937a37` returns floor square root of a uint32 input.
The caller performs its multiply by 100 **before** this helper, in 32-bit integer arithmetic.
It must not be replaced by one floating-point square root of the original unrounded expression.

The `M` getter first computes:

```text
r0 = max(0, f32(f32(w(field238+100)) / 100.0f))
r1 = max(0, f32(f32(w(field14c+100)) / 100.0f))
n  = Nearest(f32(f32(r0 * 100.0f) * r1))
```

KB returns `w(n-100)`.
Fireball does so when field `+0x2bc` is zero; otherwise it continues with the **already rounded integer** `n`:

```text
r2 = max(0, f32(f32(n) / 100.0f))
r3 = max(0, f32(f32(w(field2bc+100)) / 100.0f))
M  = Nearest(f32(f32(f32(r2 * 100.0f) * r3) - 100.0f))
```

Float ordering has observable arithmetic consequences.
For `field238=-75, field14c=6`, the first native sequence rounds to **26** before subtracting 100;
algebraically regrouping as `Nearest(f32(25*106/100))` gives **27**.
Even an apparently equivalent percent formula can therefore change the radius.

## 3. Shared radius-percent helper

`0x141939880` accepts the action, extra increased-area input `E` and extra more-area input `F`.
If `w(A+E)==0`, `F==0` and `M==0`, it returns `max(0,w(stat1591+100))`.
This is a cached-final-stat branch, not permission to treat a raw area increase as a radius increase.

Otherwise the checked instruction sequence is:

```text
total = w(statD7 + A + E + 100)
m = max(0, f32(f32(w(M+100)) / 100.0f))
e = max(0, f32(f32(w(F+100)) / 100.0f))
p = Nearest(f32(f32(f32(w(stat1670+100)) * m) * e))
scaled = div100(w(p * total))
q = ISqrt(u(w(max(0,scaled) * 100)))
q = div100(w(q * w(stat16ee+100)))
q = div100(w(q * w(stat28d7+100)))
return max(0,q)
```

The signed `/100` instructions use high multiplication by `0x51eb851f`, arithmetic shift by five and sign correction.
Integer truncation occurs before the root and after each final-radius multiplier.
NaN, nonrepresentable conversions and extreme wrapping inputs require their native exceptional behavior;
this formula does not silently substitute ideal real arithmetic for them.

## 4. Shared radius from a supplied base

`0x141939aa0` accepts action, supplied base `B`, `E` and `F`:

```text
base = w(stat16c4 + slot328() + B)
percent = RadiusPercent(action,E,F)
v = f32(f32(f32(percent) * f32(base)) / 100.0f)
if v <= 0: return 0
if v >= 4294967296.0f: return 0xffffffff
return truncate_toward_zero_to_uint32(v)
```

The last conversion is implemented as a **64-bit** `cvttss2si rax` followed by EAX selection;
it is not a signed32 conversion that fails above `INT_MAX`.
The upper clamp uses the exact float constant `2^32`.

## 5. Fireball and KB caller branches

Fireball helper `0x141a8b2f0..0x141a8b514` uses total travel `T`.
For ordinary nonnegative finite travel:

```text
E = 0; flat = 0
if statD1A != 0:
    E = trunc(f32(f32(min(f32(T/50.0f),1.0f) * 70.0f) - 20.0f))
E += trunc(f32(clamp(f32(T/50.0f),0,1) * f32(statDA8)))
flat = trunc(f32(clamp(f32(T/50.0f),0,1) * f32(stat1702)))
if w(slot328()+flat)==0 and w(A+E)==0 and M==0:
    return max(0,stat4876)
return Radius(action,w(stat4875+flat),E,0)
```

`0xd1a` is the legacy close/long-range Fireball radius flag;
`0xda8` and `0x1702` supply distance-dependent increased area and flat base radius.
The `D1A` branch itself uses an upper-only minimum, not a lower clamp for negative travel.
The pseudocode's `E` additions have native 32-bit arithmetic; the branches do not establish ordinary modifier admission.

KB distribution radius returns `max(0,stat4879)` when slots `328/330/338` are all zero;
otherwise it calls `Radius(action,stat4878,0,0)`.
KB explosion radius `0x141ac7250..0x141ac732d` instead adds stat `0x3213` as `E` when
fork generation action `+0x45c` is unsigned nonzero, then uses final/base primary stats `4876/4875`.
The checked distribution helper does not add this Fork area input.
Thus distribution and blast radius are separate calls, even when they share other area modifiers.

These fast paths consume precomputed effective radius stats.
The [startup-gated action stat paths](actions-and-emission.md#621-successful-startup-disables-the-checked-action-computed-overlay-paths)
do not establish that ordinary client actions run the shared computed-stat library to produce them.
If the selected view is the network raw object and the key is locally present, its pair supplies the value;
the actual transmitted keys/values and any special paths remain unverified.
Closing the helper arithmetic and selected cache paths does not close the full producer of those stats,
resource/MTX selection or final native/server scale parity.

## 6. Offline evidence

Original-file reads and bounded interpreters are retained under ignored research logs:

- `round8/area-radius-initial.asm`, `area-radius-helpers.asm` and `area-integer-sqrt-complete.asm`.
- `round8/area-radius-check.py` and `area-root-validation.json`: 8,276 integer-root cases against `math.isqrt`.
- `round8/area-independent-review.asm`, `area-independent-review.zh-CN.md`,
  `area-independent-check.py` and `area-independent-validation.json`: independent formula/slot review,
  4,107 signed-division cases and bounded radius/grouping vectors.
- The actual Fireball/KB callers are in round3 `fireball-true-full.asm` and `kb-core.asm`.
- Round20 `area-cache-inputs-aligned.asm`, `area-cache-inputs-audit.json` and
  `audit_area_cache_inputs.py` retain selected cache construction/copy/recompute/transfer witnesses.
  Eleven aligned spans contain 734 instructions, 40 anchors and four final-table slots;
  40,010 independent signed-division cases, eight cache-state and five donor-composition vectors pass.
  `area-independent-review.md` independently checks the producer/lifetime boundaries.
- Round23 `area-final-audit.py/json` retains eight functions / 12 aligned spans / 1075 instructions,
  three registrars, 32 anchors and seven float references for `1591/4876/4879` and the bounded helpers.
  `area-final-numeric.py/json` compares decoded-original callback interpretation with separate formulas
  on 6160 stable/cache-ready fixtures, including 16 named controls; independent review reproduces them
  and adds 28 explicit expected-result edge controls. Helper models, provider/cache readiness and ordinary
  finite floating-point environment remain premises, rather than claims of native game execution.

Automatic unwind previews for the Nearest and zero getter landed on preceding unrelated methods;
only their explicitly aligned bodies establish the behavior above.
No code from the client was executed, and no result here is a gameplay or server-parity test.
