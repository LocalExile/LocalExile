# Projectile movement configuration: collision layers and call inputs

> Frozen detailed reference from R81. Historical next-step language is provenance, not an active backlog.
> Current scope and unresolved obligations are in [the delivery matrix](delivery-matrix.md) and
> [verification and limitations](verification-and-limitations.md).

Part of [012 — projectile execution](../012-projectile-execution.md), for the pinned 3.29.3b executable.
This follows the actual inputs to the state transitions in [motion admission inputs](motion-admission-inputs.md).
The findings are original-byte/data-flow evidence, not observations from a running client.

## 1. The base byte is a named collision-hull selector

Metadata factory `0x141e24b70` installs table `0x14359fb88` at `0x141e24c00`.
Its consumed slots connect runtime Positioned creation (`+0x08 → 0x141e2e790`) and string-property parsing
(`+0x30 → 0x141e2dbb0`) on the same metadata class.
At `0x141e24c0f`, the factory initializes word `+0x24` to `0x0100`: byte `+0x24` defaults to zero;
the neighboring byte is independently one.

Parser `0x141e2dbb0` compares the exact ASCII property key `collision_hull_type` at `0x1435a2028`.
For that key, it compares UTF-16 values using their stored lengths and inline/heap string storage:

| Exact value | Metadata byte `+0x24` | Success |
|---|---:|---|
| `walk` (`0x1435916c8`) | 0 | true |
| `projectile` (`0x14352cf50`) | 1 | true |
| Other value in this key branch | unchanged | false |

The comparisons are case sensitive. The writes are at `0x141e2dc57` and `0x141e2dc9a`.
This branch does not assign a name to value 2 or accept arbitrary numeric values.
Do not generalize it into an exhaustive enum declaration for the whole program.

Runtime constructor `0x141e2e960` retains its metadata pointer at Positioned `+0x158` and copies metadata
byte `+0x24` into both base `+0x2c0` and current `+0x2c1` at `0x141e2eb18..0x141e2eb2d`.
Those fields select collision layers; they are not the projectile's numerical height or a ground/air height enum.

Read-only GGPK configuration supplies a conditional default witness: `Metadata/Projectiles/Projectile.ot`
sets `collision_hull_type = "projectile"`, and the extracted DefaultArrow/WandProjectile `.ot` files extend it
without overriding that key. The subsequent [object-type binding](object-type-binding.md) connects selected row ID, retained resource handle,
generated inheritance and the actual Positioned factory/component creator. It distinguishes same-name `.ot` priority
from the generated-parent fallback and binds the checked common route to byte 1.
Final overlays, configuration lifetime, reloads and later writers remain bounded separately.

## 2. Initial placement and redirect supply different inputs

The ordinary finalizer `0x141ba99d0` obtains the flying Entity's Positioned at `0x141baa1b9`.
After birth callbacks, it checks that action `+0x328` still exists at `0x141baa58a`.
On the surviving path, `0x141baa59c..0x141baa5bf` calls final action virtual `+0x558` with:

```text
arg1 RCX  = projectile action
arg2 RDX  = prepared target gridXY address
arg3 R8d  = prepared target height
arg4 XMM3 = selected speed
arg5      = flying Positioned.byte2c0
arg6      = finalizer's separately supplied phase/presentation byte
```

The sixth argument comes from the finalizer's incoming seventh ABI argument, reloaded at `0x141baa4c5`.
Do not mistake an earlier `ESI=1` or later reuse of RSI for its source at the movement call.
Birth-callback mutation and replacement aliases remain separate obligations; this is the checked direct flow.

The shared redirect `0x141bb2ae0` instead calls the same virtual at `0x141bb324c` with **arg5=1, arg6=0**.
The stores are at `0x141bb3235/323a`; the preceding `+0x628/+0x548` calls prepare speed and height.
Thus redirect explicitly requests the projectile collision layer, independently of the base byte.
If the selected object's actual base is already 1, these two routes agree on collision layer;
otherwise they can differ. Neither input establishes a new absolute height.

The six witnesses reach these checked final bindings:

| Final class | Table | `+0x558` movement entry |
|---|---|---|
| Default bow / wand | `0x14326d930` | `0x1412df070` |
| Burning Arrow | `0x1432ec7e8` | `0x1412df070` |
| Fireball | `0x14333ac40` | `0x141a8b640` |
| Kinetic Blast | `0x14326b9c0` | `0x141bacf60` directly |
| Tornado Shot | `0x143423898` | `0x1414fc170` |

The three wrappers preserve arguments 5 and 6 into shared `0x141bacf60`.
They can also perform skill-specific work: Fireball may rotate its aim or replace height;
TS may substitute stored child height under its initial/no-continuation gates.
Preserving the collision input does not make these wrappers equivalent in every other argument or effect.
All five tables bind redirect `+0x620` to `0x141bb2ae0` and distance override `+0x608` to `0x141ba6350`.

## 3. Shared straight and arc calls have explicit ABI values

For shared setup `0x141bacf60`, let R be entry RSP.
After the prologue, RBP is `R-0x58`, RSP is `R-0x158`.
The byte at `[rbp+0x80]` is therefore incoming argument 5 at `R+0x28`.

At `0x141bad3c6..0x141bad428`, the straight route passes:

```text
0x141e34220 argument 7 = incoming argument 5 (collision layer)
             argument 8 = 0
             argument 9 = 1
```

For that wrapper, RBP is entry RSP minus `0x48`, local RSP is entry RSP minus `0x148`,
and the specification begins at local `[rsp+0x20]`.
At `0x141e34329..0x141e34364`, the wrapper constructs:

| Specification byte | Value |
|---|---|
| `+0x90` | wrapper argument 7 |
| `+0x91` | 1: use the explicit layer override |
| `+0x92` | wrapper argument 8 |
| `+0x95` | wrapper argument 9: clear the optional speed profile on this route |

The nontrivial straight setter therefore writes `Positioned.byte2c1 = incoming layer` and `byte2c2 = 0`.
Its earlier virtual configuration call selects the same override.
This shared wrapper forces the override; the setter's base-byte fallback still exists for other specifications.

At `0x141bad362..0x141bad3bf`, the arc route passes argument 7 as that same incoming layer and argument 8 as 0
directly to `0x141e34a70`. Its nontrivial movement path writes those bytes to `+0x2c1/+0x2c2`.
The zero-time/reset paths restore the base state as described in the parent chapter.
These are configured values on actual call edges, not unnamed/uninitialized stack bytes.

Shared setup chooses among these routes using additional inputs:

- Nonzero stat `0x4324` (`projectile_ballistic_angle_from_reference_event`) and action byte `+0x4e8`
  enter the reference-event branch. For finite components, a zero saved reference vector takes the straight branch directly;
  it does not fall through to the other ballistic option. A nonzero vector enters its angle calculation.
  The comparison leaf `0x140c967f0` uses `UCOMISS/JNE` without a parity check; this is not an IEEE-safe
  nonzero-vector predicate for arbitrary NaNs.
- When that reference branch is not entered, nonzero stat `0x42b8`
  (`projectile_ballistic_angle_from_target_distance`) selects the target-distance ballistic calculation;
  zero selects straight movement.
- Before these branches, nonzero stat `0x5432` (`projectiles_travel_at_fixed_height`) replaces the requested
  height using the flying Positioned's effective height getter `0x141e35ae0`.
- A nonzero final `+0x608` result, stat `0x1b2b` (`projectile_distance_override`) for these bindings,
  rescales the target displacement and recomputes terrain height plus action `+0x334` before the fixed-height check.

This closes route selection and byte/height input ordering. It does not claim a complete mathematical recovery
of the two ballistic angle solvers, their exceptional domains, or every other caller's specification.

## 4. Collision admission uses the layer without conflating it with height

In frame subrange `0x141e328fd..0x141e329cf`, the terrain check normally selects current `+0x2c1`;
Positioned `byte1e7 & 8` instead selects 1 on that check.
For a selected byte at most 2, it indexes one of three 24-byte grid vectors at `grid+0xd0 + 24*layer`.
Bounds, extent and grid-availability gates precede a nibble comparison.
The following entity-query call at `0x141e329ca` separately receives the actual current `+0x2c1` in R9d.
It does not receive the terrain-check override simply because that flag was set.

Consequently, even the terrain and entity queries in one frame need not use the same selected layer.
Neither query's layer argument alone establishes accepted contact, damage, or a message to the server.
The separate height/terrain-height checks and [local contact filtering](continuation-and-memory.md) still apply.

The [ordered Chain predicate](chain-candidate-admission.md) uses:

```text
selected(P) = base2c0 if byte1e6 has bit 7 else current2c1
reject source layer 0 with target layer 1
```

The metadata names now explain that particular 0/1 distinction. They do not prove an airborne-target rule,
a flying visual model's elevation, or every target object's actual configuration.
Suppression, stop/reset and new-movement transitions remain those already recovered in
[motion admission inputs](motion-admission-inputs.md).

## 5. Evidence and remaining boundaries

Ignored round40 `audit_motion_configuration.py` and `motion-configuration-audit.asm/json` seal 14 full unwind
bodies, one frame subrange and one caller-bound comparison leaf: 33 ranges / 3543 decoded instructions, 17 consumed slots, four RIP bindings,
three original string literals and 29 fresh instruction anchors. Raw extracted game assets remain private and excluded.

`check_configuration.py` interprets original instructions for 172 controls / 4023 steps / 205 visited sites.
It checks typed parsing and initial/redirect/wrapper/specification ABI transfers against separate string/ABI expectations.
Inputs, selected branch entry, Fireball's ordinary variation and absent optional height resource are prepared;
`memcmp` uses its library contract. Surrounding selection/callbacks, actual setters and angle solvers are not
counted as executed by those controls. This is same-agent offline verification, not native execution or worker review.
The later [layout-to-installed-motion composition](motion-and-contact.md#8-joining-emission-to-installed-straight-motion-and-frame-advancement)
executes the actual straight wrapper/setter, function-object transfers, integer initializer and subsequent
frame samples from emitted objects. It corrects the storage attribution: ordinary contact is captured
at Positioned `+0x160`, special-object contact at `+0x1a0` on this route. The remaining entity query,
callback subscribers and ballistic solvers are not executed by that composition.

Still open: the complete generated-resource metadata binding and overlays, encountered values/phase flags,
remaining indirect callers/writers and target configurations, ballistic arithmetic/exceptional inputs,
and the wider official C/S scheduling/result-route contract. These gaps do not negate the closed parser and call edges.
