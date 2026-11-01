# Instance frame inputs: integer time and concrete tick callers

> Frozen detailed reference from R81. Historical next-step language is provenance, not an active backlog.
> Current scope and unresolved obligations are in [the delivery matrix](delivery-matrix.md) and
> [verification and limitations](verification-and-limitations.md).

Part of [RE 012](../012-projectile-execution.md). **S:** bounded static evidence in the pinned 3.29.3b PE;
finite original-instruction controls use declared fixtures. This chapter connects concrete callers to the
[registered World receive route](world-message-inputs.md#3-registered-pump-factory-and-owner-handoff).
It does not establish a complete frame-to-action-thread chain or the official server's schedule.

[Area changes](../../protocol/09-area-change.md#6-exit-to-character-selection-cs-0x153) already identifies
the in-game frame update, application `+0x340/+0x348` selection and the state-stack singleton.
Those are reused discoveries. The additions here are exact context/table identities, integer clock production,
argument preservation, callback ordering, final Instance publication and its binary32 time conversion.

## 1. Keep the listener, update context and application distinct

The previously mapped startup listener uses table `0x142f9cce0`, slot zero **`0x1401027d0`**.
Its complete body preserves incoming `RCX` as **T**, then replaces `RCX` with global
**`[0x1445c7e88]`**, called **U**, before invoking **`0x14164e250`** at `0x1401027e0`.
The incoming dispatcher in RDX and double delta in XMM2 are forwarded. The later tail call
`0x140e3c730(T+0xe8, 0, 1)` uses T again. T and U must not be assigned the same virtual table.

The startup slice `0x1400fdfca..0x1400fdffb` allocates `0x188` bytes, calls constructor `0x140103d80`
and publishes U to that global. The constructor's final table is **`0x14301c5f0`**;
its slot `+0x68` is **`0x140105480`**. `0x14164e250` calls U's slot at `0x14164e2bb`, after the
already mapped synchronous World drain. The complete slot body has application/helper branches and no direct
Instance-tick call. This is a direct-body observation, not a transitive absence claim.

The application obtained through **`0x1400f35b0`** is another object, **A**. Accessors `0x1406df340`
and `0x1400f34b0` obtain an owned cell, read `A+0x340` and `A+0x348` respectively, release the temporary
control block and return the retained raw pointer. U, T and A are not interchangeable contexts.

## 2. Integer elapsed time has a separate producer

Before registering the later frame tasks, `0x14164e250` executes slice
**`0x14164e303..0x14164e367`**. It obtains a frequency pointer through `0x1400ef9e0`, calls
QueryPerformanceCounter, multiplies the signed 64-bit counter by 1000 and uses signed IDIV by the frequency.
The resulting signed timestamp is retained in RDI. The multiplication itself has machine-width wrapping;
the finite clock controls use positive, nonoverflowing products and nonzero frequency.

The static manager **M = `0x144488230`** has initialized PE table pointer **`0x1434565e0`**;
slot zero is **`0x1415567f0`**. The selected arguments are:

```text
nowMs = trunc_toward_zero(wrap_i64(counter * 1000) / frequency)
if M.u32[+8] == 0:
    elapsed = M.u32[+0x18]
else if M.u64[+0x10] == 0:
    elapsed = 0
else:
    elapsed = low_u32(nowMs - M.u64[+0x10])
M.slot0(M, nowMs, elapsed)      // RCX, RDX, R8d; call at 0x14164e365
// Other code intervenes before the next store:
M.u64[+0x10] = nowMs           // 0x14164e3af
```

The subtraction actually uses EAX and the low dword of the previous timestamp; modulo-32 subtraction is
equivalent to the stated low-u32 expression. The previous-timestamp-zero check reads the whole qword.
Mode and forced-elapsed writers/actual values are not closed by this cut. A supported forced input is not
evidence that the client runs every callback at 30 Hz.

The incoming double delta is captured independently for the later **EngineUpdate** callable:
table `0x14346e058`, slot `+0x10 = 0x1416774c0`, registration key `0xa6aac92d`.
The capture is not a substitution for M's independently constructed integer arguments. Task names alone
are insufficient to identify the Instance pump or its execution thread.

## 3. Integer driver order and the still-missing registration edge

The complete **`0x1415567f0`** body preserves timestamp and elapsed across callbacks:

1. Traverse pointer vector `[M+0x20, M+0x28)` forward, invoking each object's slot zero with the same RDX/R8d.
2. If byte `M+0x70` is set, copy pending vector `[M+0x78, M+0x80)` into the vector at `M+0x58`
   through `0x140143930`, then clear the byte.
3. Load `[M+0x58, M+0x60)` after that step and traverse forward, invoking slot `+8` with the same inputs.

Thus pending publication separates the passes; the second vector is not a snapshot captured before the first.
The controls include empty/multiple vectors, pending replacement and callbacks that deliberately clobber volatile
argument registers. Vector contents and the pending-copy effect are fixtures; no global membership is inferred.

The state-stack singleton `0x1400f2600` has final table **`0x14301d4a8`**, slot `+8 = 0x1415269e0`.
After its transition handling, that complete body traverses active shared-pointer entries `[stack+0x20, stack+0x28)`
in reverse. Each nonnull state's slot `+8` receives the preserved timestamp/elapsed. If the just-called state's
byte `+0x20a` is nonzero, lower entries are skipped.
Controls cover an empty pending-transition queue, null entries and stop positions; full transitions and reference
cleanup are not replayed. **The actual registration/membership connecting M's callbacks to this state stack
is still open.** Matching virtual signatures does not supply that edge.

## 4. In-game state to concrete Instance tick

Constructor **`0x140d55be0`** installs in-game table **`0x1431ec558`**.
Its slot `+8` is **`0x140d574d0`**, a five-byte jump to **`0x140d574e0`**.
The latter's `0x140d57674..0x140d576d8` branch selects:

| Application pointer | Selected helper | Instance-tick call |
|---|---|---|
| A `+0x340` nonnull | `0x140d57730` | `0x140d5776d`, selected Instance primary slot zero |
| A `+0x340` null, A `+0x348` nonnull | `0x140d57cd0` | `0x140d57e7c`, selected Instance primary slot zero |
| both null | neither | no tick from this branch |

Both receive the original 64-bit timestamp in RDX and u32 elapsed in R8d, with RCX changed from the state
to the selected Instance. The `+0x340` helper's entry prefix preserves/reloads those arguments directly.
The `+0x348` helper retains Instance in R12 and elapsed in R13d; its tick slice reloads the saved entry RDX
from `RBP+0x1c8`. The full prologue establishes that slot as entry `RSP+0x10`.
Intervening gameplay work is not included in this latter slice's controls.

The creation chain supplies a concrete final class, rather than assuming the shared constructor's table survives:

- In-game handler `0x140d59b70` obtains A through `0x1400f35b0`, then calls **`0x1415a4fe0`**
  at `0x140d59e60`, passing World from state `+0x3a0`.
- The factory allocates `0xe208` bytes and calls shared Instance constructor **`0x142144b50`** at `0x1415a505b`.
- It installs table `0x14345e810`, then final client table **`0x14345ede0`** at `0x1415a50e0`.
  Both slot-zero entries are **`0x142148880`**, the previously mapped Instance receive-loop owner.
- At `0x1415a5162`, the new pointer is stored to A `+0x340` **before** the old Instance's `+0x38` interface
  is destroyed. Bounded publication controls cover absent/present old pointers; construction and all later
  transitions, including publication to `+0x348`, are not replayed.

Inside **`0x142148880`**, `0x1421488ac` writes **R15d = R8d**, zero-extending into R15.
After the early `Instance+0xe1f8` guard, `0x1421488c7` converts that signed-64 register to binary32;
`0x1421488cc` divides by the PE constant **1000.0f** at `0x1436a4580` (LE bytes `00 00 7a 44`):

```text
elapsedSeconds = f32(f32(uint32(elapsed)) / 1000.0f)
```

It is not a signed-i32 or double-seconds argument. The explicit conversion controls execute the move and
conversion/division slices, with the early guard bypassed as a declared path condition. Inputs include zero,
33/34 ms, `2^24+1`, `0x7fffffff`, `0x80000000` and `0xffffffff`; an independent rational nearest/even
oracle checks both rounding stages. These finite normal-value checks assume round-to-nearest/even and do not
recover the actual floating-point environment.

## 5. Consequences, evidence and stopping boundary

The recovered contracts connect in-game state callers to the concrete Instance table and its receive loop,
and separately recover the outer integer clock/driver and state-stack forwarding. They narrow the input side
of projectile World/result processing. They do **not** establish that every displayed frame is a server tick,
that all callbacks share a thread, or that EngineUpdate's double delta is the Instance's input.

Round45 seals nine complete unwind functions (including split ranges), twelve caller slices and one table-bound
tail-jump leaf: **32 ranges / 917 decoded instructions, 24 anchors and eight table slots**.
The **171 finite controls / 9,337 original-instruction steps / 259 visited sites** check clock selection composed
with the real integer driver, driver order/pending reload, reverse state traversal, Instance selection/getters,
the two concrete tick-call slices, time conversion, final publication and distinct listener contexts.
They are same-agent offline interpretation, not native execution. Fixtures include QPC/frequency, registration
vectors/state contents, singleton owned-cell results, pending copy, selected caller frames, gameplay callbacks
and cleanup. Branch-selection controls mock the selected gameplay helpers; their tick-call slices are checked
separately, not represented as one fully executed frame.

Ignored evidence under the projectile execution log's `round45/`: `audit_frame_inputs.py`,
`frame-input-audit.asm/json`, `check_frame_inputs.py` and `frame-input-controls.json`.
Existing protocol field mapping and the earlier World ingress/checkpoint evidence are reused.

Remaining questions are actual callback registration, mode/elapsed writers, action-thread/TLS history and official
ordering/result choices. These are distinct from the established arithmetic and call arguments. Further generic
engine traversal is deferred in favor of projectile input/cache lifetimes, random consumption order and the
unclosed [Fireball result route](fireball-and-kinetic-blast.md).
