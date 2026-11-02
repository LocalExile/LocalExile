# Projectile speed, progress curves and sample clocks (3.29.3b)

> Frozen detailed reference from R81. Historical next-step language is provenance, not an active backlog.
> Current scope and unresolved obligations are in [the delivery matrix](delivery-matrix.md) and
> [verification and limitations](verification-and-limitations.md).

Part of [RE 012](../012-projectile-execution.md); supplements [motion/contact](motion-and-contact.md).
These are bounded static reconstructions of the pinned binary, not a new client run or server port.
Arithmetic below describes ordinary finite values with float32 rounding after each native operation.

## 1. A concrete shared speed getter

Shared/checked TS and bow slot `+0x628 → 0x141bb38d0` returns the speed used by the motion setup.
Its early branch returns `5000.0f` when both `spell_projectiles_arrive_instantly` (`0x530a`)
and `casting_spell` (`0x1b6`) are nonzero.
The ordinary branch, `0x141bb392c..0x141bb3a18`, is:

```text
modifier = SpeedModifier141bb35f0(action)        # integer result; section 1.1 preserves its two roundings
divisor = 1.0f
if action.flightObject328 exists:
    size = f32(flight.Positioned.metadata5c * flight.Positioned.scale2b0)
    excess = f32(size - 1.0f)
    weighted = f32(float(stat2dbe) * excess)
    divisor = f32(1.0f + f32(weighted / 100.0f))
factor = action.speedCache3a4
if factor == 0.0f: factor = 1.0f                # NaN does not take this fallback
base = f32(float(action.descriptor358.int38) * 0.09200000017881393f)
percent = f32(f32(float(modifier) * 0.009999999776482582f) + 1.0f)
speed = f32(f32(f32(base * percent) * factor) / divisor)
```

Stat `0x2dbe` is `projectile_speed_is_divided_by_%_of_scale`.
The modifier helper's selected virtual sources, two roundings and `-99` lower bound are reconstructed below.
The cached random multiplier is documented in [random inputs](random-inputs.md), including its `/200` amplitude.

Finalizer call `0x141baa1a6` obtains this getter and the later `+0x558` callback passes the speed to `0x141bacf60`.
The straight wrapper writes it into specification `+0x08`, which the integer-path initializer receives unchanged.
The `0.092f` conversion is therefore necessary before using descriptor speed with grid distance.
Every specialized getter and the global frame-delta producer are not covered here.

### 1.1 Shared integer modifier, selected views and rounding

All five final client tables for the six skill witnesses retain `+0x628 → 0x141bb38d0`.
Their `+0x4f8` binds to integer-zero leaf `0x1400ecd70`, and `+0x6a0` to
`0x141241550`, which reads returning byte `+0x461`.
Fireball's `+0x598` is false; the other tables use `0x141b6fa10`, requiring actual Bow equipment
and nonzero `skill_can_fire_arrows` before selecting arrow stats.
This does not select the arrow branch for every attack or every projectile-tagged skill.

Helper `0x141bb35f0..0x141bb38c9` reads the current selected stat view through
`0x14193b350(action, action.byte174)` each time it queries a stat.
For ordinary finite, representable arithmetic, preserving float32 operation order:

```text
returned = virtual6a0() ? stat49e7 : 0
baseIncrease = virtual598() ? stat12d : stat57f
chainIncrease = i32(stat389f * action.chainDone458)
increased = i32(100 + baseIncrease + returned + chainIncrease + virtual4f8())
contextFactor = f32(1.0f + f32(float(action.int434) / 100.0f))
first = roundNative(f32(float(increased) * contextFactor))
amplitude = stat5379                         # queried before the following gates
factor = 1.0f
if byteTable142fdd740[stat3] != 0 and stat0bad != 0:
    q = freshSpeedVariationSample()          # section 1.2; integer 0..2000
    u = f32(float(q) / 2000.0f)
    positive = float(amplitude)
    negative = f32(positive * -1.0f)
    variation = f32(negative + f32(f32(positive-negative) * u))
    factor = f32(1.0f + f32(variation / 100.0f))
second = roundNative(f32(float(first) * factor))
modifier = maxSigned(-99, i32(second - 100))
```

Integer additions/multiplication above wrap to 32 bits before signed conversion.
`roundNative` is leaf `0x140664f10..0x140664f46`: for finite, in-range values it computes
`floor(f32(x + 0.5f))`, including negative half-values rounding toward positive infinity.
It is neither banker's rounding nor unconditional truncation.
Do not merge the two roundings, replace division by a reciprocal without checking float32 results,
or apply the lower bound before the second rounding.
NaN, out-of-range conversion and exceptional MXCSR behavior are outside this finite summary.
An offline distinguishing vector has increased integer 101, context factor 1.5 and sampled factor
`1.4739999771118164`: first rounding maps 151.5 to 152, producing final modifier 124.
Incorrectly fusing the roundings produces 123.

| Stat key | Input |
|---|---|
| `0x49e7` | `returned_projectile_speed_+%`, selected by returning byte, not extra-phase cache |
| `0x12d / 0x57f` | effective `arrow_speed_+% / projectile_speed_+%` |
| `0x389f` | `projectile_speed_+%_per_chain`, multiplied by executed Chain count, not remaining budget |
| `0x5379` | `projectile_speed_variation_+%_final_with_melee_weapon_attacks` |
| `3 / 0x0bad` | `main_hand_weapon_type / skill_is_attack`, controlling the native table/attack gates |

Native weapon-stat enum and equipment-object class enum are distinct.
The checked classification table's first 18 bytes are `1,1,1,1,1,1,0,1,1,0,1,1,1,1,1,1,1,1`;
do not index it with the equipment-object Bow class value merely because both describe a weapon type.
Selected stat composition remains the responsibility of the stat layer; this formula consumes its outputs.

Constructor `0x141ba22f8` clears action `+0x434`; common copy `0x141ba2661/667` preserves it.
Context callback `0x141bae280` invokes the base context helper, then copies its parameter's signed `+0x1c`
into `+0x434` at `0x141bae29d`.
That field is separate from virtual-table slot `+0x4f8`, which returns zero for these classes.
Two concrete network routes are now established: the `0x1ce/0x1cf` optional configuration's inner mask bit 4
reads a network-order u32, copies it through the dispatcher and calls this receiver before admission/start.
See [the exact route, mask and absence semantics](synthetic-start-and-server-commands.md#51-a-transported-speed-context-producer).
The same field can therefore carry a signed speed percentage from the server; actual official values,
all other producers/callers and the configuration vector's gameplay semantics remain open.
All five final tables bind this receiver to virtual `+0x80`.
The complete base helper is `0x14193f320..0x14193f40c`, across an unwind split at `0x14193f361`.
Its startup global `0x1445b38cd` can skip selected stat-overlay writes, but it does not skip the outer
`context+0x1c → action+0x434` assignment after the base call returns.
Default construction or the overlay gate therefore does not prove this factor is always zero.
Round19 `speed-context-producer-findings.zh-CN.md` and its audit retain this bounded receiver contract;
heuristic `+0x80` call candidates are not treated as proven callers.
Round20's typed factory/initialization/target-setter cut also found no producer.
In particular, the root factory's owner/component context is a different structure, and the checked direct
initialization/notification edges do not supply this input. Their unexpanded nested helpers are not an absence proof.
Round27 expands nested initialization helper `0x14193bbe0..0x14193bc36`: it reads an optional
action `+0x1a8` vector of eight-byte `{key,value}` pairs and passes each pair to
`0x14193c850(action+0x178,key,value)`. This typed frontier adds no identified context publisher;
the nested stat writer and all other possible callers are not excluded by that bounded check.
Those older bounded searches did not exclude the subsequently typed `0x1ce/0x1cf` callers.
Repeating a whole-image slot-offset scan would not establish additional trusted callers.
The [optional receiver observer](capture-contract.md#25-context-receiver-actual-assignment-and-caller-discovery)
now records caller, entry input and actual store value independently of root scope for a future authorized capture.
Its offline tests and byte checks do not observe actual official-server values or close all remaining producers.

### 1.2 A fresh deterministic variation, separate from the speed cache

The branch above constructs a `[0,1]` float distribution through `0x1404cd340`, giving integer maximum
`trunc(f32((1-0)+1))*1000 = 2000`.
It then uses a fresh eight-word context and inclusive sample `0..2000` before interpolating `-amplitude..+amplitude`:

```text
[sequence11a:u16, derived11c:u8, owner88:u32, outer2d0:u32, inner2b8:u8, 0, 0, 0]
    → 0x1419c29b0 → 0x140484fa0(bound=2001)
```

The draw occurs whenever both native gates pass, even for amplitude zero.
It is not TLS and does not consume the separately prepared `+0x3a4` speed cache's stream.
Equal actual inputs restart the same first sample on another invocation; Chain count and Return phase are absent
from this recipe, while changed derived/repeat identity can change the sample.
The signed amplitude is not clamped or made absolute before interpolation.
See [random inputs](random-inputs.md#611-shared-speed-modifier-variation-restarts-another-context).
Round19 `speed-modifier-aligned.asm` and `speed-modifier-audit.json` retain the exact helper/leaves,
20 final-table slots, classification bytes, constants and finite arithmetic vectors.

### 1.3 Descriptor speed is a selected Projectiles row field

Publisher `0x141bb66a0` calls final virtual `+0x510` at `0x141bb66b8`, then stores its returned
Projectiles row pointer in action `+0x358` at `0x141bb66c1` and retains the table owner at `+0x360`.
The checked preparation calls this publisher when the descriptor is null; ordinary reads do not reselect the row.
Common construction clears the pair, and common copy preserves it with owner retention.
This cache lifetime differs from the current stat queries in section 1.1.

The data path is `0x14126ef80 → 0x1412d5850 → 0x1412d6380 → callback 0x1412d6b40 → 0x142340dd0`.
Its exact UTF-16 literal is `Data/Projectiles.dat` at `0x14325be60`.
Native loader `0x142340dd0` validates row-count times packed stride `0xea`, then row fill `0x142341280`
copies the fixed row bytes and relocates strings/arrays/foreign rows.
The complete checked row loop's 15 RBX-relative writes do not overlap `+0x38..+0x3b`.
Getter `0x141bb39f2` converts those preserved four bytes as a **signed** integer.

The pinned local PoE1 schema places an eight-byte Id and three 16-byte arrays before `ProjectileSpeed:i32`,
giving offset `0x38` and complete stride `0xea`, matching native loader and row-walk offsets.
This identifies the field using schema, code and selected extracted tables together;
the audit does not claim a fresh raw-GGPK export or infer speed from a skill name.

| Final class | `+0x510` | Selected row path |
|---|---|---|
| Default bow/wand | `0x141ac9e70` | actual Bow provider: art slot 0; non-Bow: exact `Metadata/Projectiles/WandProjectile` Id |
| Fireball | `0x141a8b580` | ordinary byte `+0x528=0`: art slot 0; otherwise exact DragonFireball lookup |
| KB | `0x141ba43e0` | explicit-Id virtual `+0x508` or art slot 0; this final KB class's `+0x508` returns null |
| TS | `0x141a826f0` | art slot index from whether action byte `+0x530` is nonzero |
| Burning Arrow | `0x141ac5960` | art slot 1 for nonzero `vaal_burning_arrow_explode_on_hit`, otherwise 0 |

These selectors reuse the [resource association/variant contract](resource-selection.md), rather than proving
every MTX choice, later invalidation or hot reload here.
Copy retains the descriptor temporarily; the subsequent release lifecycle determines whether it is reselected.
The ordinary TS secondary chain is now closed in section 1.4.

Conditional default data witnesses, before modifier/cache/scale division, are:

| Projectiles Id suffix | Raw signed speed | `f32(raw * 0.092f)` |
|---|---:|---:|
| DefaultArrow, WandProjectile, BurningArrow, VaalBurningArrow | 3260 | 299.9200134277344 |
| Fireball, DragonFireball | 1040 | 95.68000030517578 |
| TornadoShotArrow / SecondaryArrow and corresponding wand rows | 2173 | 199.91600036621094 |

These are selected table witnesses, not live measurements or constants for every related resource.
Round19 `descriptor-speed-verified.asm` and `descriptor-speed-audit.json` preserve loader/fill/selector
spans, ten final slots, schema/data hashes, field layout and individual row/art witnesses.

### 1.4 Selected-row lifetime across release, TS children and Return

Five final classes bind high-level release `+0x4b8` to `0x141ba4930`.
This body calls publisher `0x141bb66a0` **unconditionally** at `0x141ba4946`, replacing an already-selected row.
The publisher has no internal nonnull-cache shortcut. Rain-scheduled clones refresh at `0x141ba4c16`,
and count/origin preparation refreshes again at `0x141ba51dd`, before its count admission gates.
The earlier null-only preparation check is a fallback, not the entire lifecycle.
Selection can therefore occur even when later launch admission rejects all projectiles;
whether selection draws randomness still follows the selector's empty/single/multiple-candidate rules.

Ordinary TS children follow this positive same-clone chain:

```text
0x141a827c0: clone via final +0x450
  → common copy retains descriptor/owner and clears flightObject328
0x141a82927: clone.byte530 = 1
0x141a82b3d: clone.virtual4b8(...)
  → 0x141ba4946 → publisher → final +0x510 = 0x141a826f0
  → select association slot 1 for the child
```

Thus their final emitted resource is not inferred from the intermediate copied mother row.
Helper `0x141bade00` between subtype assignment and release installs existing-entity subscriptions;
the new clone's null flight object makes it return early. It is not the resource invalidation step.

Return executor `0x141bb0710` instead calls low-level `+0x618` for its clone-emission path or `+0x620`
for same-instance redirect; these checked paths do not enter high-level release.
They use the selected row, while redirect calls current speed getter `+0x628` at `0x141bb321a` before
reinstalling motion. Retaining the base-speed row does not retain the previously calculated final speed:
Returning state, current stats, counts and the inherited speed factor can still affect that getter.
Copy retains the row owner; replacing the row releases the old owner and transfers the new owner;
destruction releases ownership rather than preparing another release.

Round20 `descriptor-lifecycle-audit.json` and `descriptor-lifecycle-verified.asm` check these forward paths
in 21 ranges, with 5011 instructions, 41 anchors and 14 final slots.
This is not a global writer inventory or a proof that arbitrary nested overrides, hot reload or runtime MTX
changes cannot mutate a row. Candidate-list caching and directory mutation remain distinct open questions.

## 2. Ordinary shared projectile setup clears optional curve state

Positioned supports an optional cumulative progress profile at `+0x308`.
Its existence does not establish that ordinary projectiles use it.
Shared straight setup `0x141bad3c6` writes caller stack `+0x40 = 1` before `0x141e34220`.
The wrapper maps this argument to motion specification `+0x95`:
with entry stack `R`, `rbp=R-0x48`, specification=`R-0x128`,
`[rbp+0x90]` is caller stack `+0x40`, and `[rbp-0x4b]` is specification `+0x95`.
Setter `0x141e34441..0x141e3447e` clears the profile pointer, normalization/period and phase fields for that flag.
Arc setter `0x141e34ab0..0x141e34ae7` unconditionally clears the same state.
The checked shared arc branch reaches that setter as well.

Thus the checked initial shared mover returns raw delta from its progress helper.
Later specialized callbacks could reinstall a profile; the observation does not exclude every later override.
No forced 30 Hz quantization is established by these float-delta paths.

## 3. A redirect can preserve progress within the current step

Initializer `0x141e4d180` calculates duration from integer-grid distance divided by supplied speed,
then divides by dominant-axis length to obtain time per step.
If the old step time is positive and the new step time differs, it rescales residual progress:

```text
residual = f32(f32(residual / oldStepTime) * newStepTime)
stepTime = newStepTime
rawTime = 0
```

It does not unconditionally erase residual progress on every redirected path.
The raw clock reset is at `0x141e4d375`; the called `0x14015a730` is only `sqrtss/ret` and preserves EAX zero.
Zero-length, signed distance overflow, nonfinite speed and other exceptional inputs remain outside this summary.

Frame `0x141e32794..0x141e327c9` adds integrated progress to residual and raw delta to raw time **once**.
The catch-up loop then processes all due integer samples, rechecking movement state after callbacks.
In special-end mode, raw time reaching duration can enter the sampler even with less than one residual step.
In that case it marks completion without advancing the integer-line sample or subtracting another residual step;
an installed arc still evaluates its height at the clamped raw clock.
With enough residual it can advance one sample and mark it last.
This is not an unconditional teleport to the old endpoint.

Arc height samples `min(rawTime, duration)` using the frame-accumulated clock.
Multiple XY samples caught up in one frame can therefore use the same height time.
Do not reconstruct a separate exact sub-frame arrival time for each sample without further evidence.
Cross-object update order and moving-target relative intersections remain unresolved.

## 4. Optional curve arithmetic, when another caller installs it

Sampler `0x141e33a00` uses a float vector `C` at descriptor `+0x30..+0x38`, with `N` entries and period `T`.
For ordinary nonnegative finite phases and positive period:

```text
r = nativeRemainder(t, T)
q = f32(f32(r / T) * float(N-1))
i = floor(q)
fraction = f32(q - float(i))
local = f32(C[i] + f32(f32(C[i+1] - C[i]) * fraction))
cycles = floor(f32(t / T))
F(t) = f32(f32(float(cycles) * f32(C[N-1] - C[0])) + local)
```

This unwraps a cumulative value over periods; it is not a linear sample of instantaneous speed.
The native remainder helper and boundary indexing need further exceptional-input coverage.
Installer `0x141e33af0` converts unsigned descriptor `+0x20` milliseconds to `T` seconds using `/1000.0f`.
For at least two samples it stores:
`mean = f32(abs(f32(F(B)-F(A))) / max(f32(B-A),0.0001f))`.
The two located direct installers are `0x141c0ce99` and `0x141e5829d`; their full asset/owner contracts remain open.

Integrator `0x141e33c40(rawDelta,A,B)` falls back to raw delta when no usable profile exists
or mean is at most `1e-5f` (its unordered comparison also falls back).
Otherwise negative A becomes B; span below `1e-5f` returns zero.
For the remaining finite positive-span path it returns, in this operation order:

```text
value = abs(f32(F(B)-F(A)))
value = f32(value / mean)
value = f32(value * rawDelta)
return f32(value / f32(B-A))
```

The absolute value does not mean reverse phases advance: negative span fails the earlier gate.
Render phases are `A=+0x2f8,B=+0x2f4`; integer phases are `A=+0x300,B=+0x2fc`.
Setter `0x141e33d70` selects these with its fourth argument and can copy integer phases to render state.
The phase values are not established network timestamps or release times.

## 5. Evidence and remaining work

Ignored `logs/projectile-execution-model/round4/motion-findings.zh-CN.md` records the exact aligned sources,
stat names and finite-value limits.
Supporting listings include `motion-speed-origin.asm`, `motion-speed-time.asm`, `motion-path-frame.asm`,
`motion-profile-callers.asm`, `motion-supplement.asm` and `motion-phase-helper.asm`.
Remaining work includes other context `+0x434` producers/callers and actual server values beyond the typed
`0x1ce/0x1cf` optional-configuration routes, full curve schema/defaults,
indirect phase writers, exceptional arithmetic and world/entity/callback scheduling.
No production timing or existing server 30 Hz schedule was changed.
