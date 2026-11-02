# Fireball and Kinetic Blast: concrete native stages (3.29.3b)

> Frozen detailed reference from R81. Historical next-step language is provenance, not an active backlog.
> Current scope and unresolved obligations are in [the delivery matrix](delivery-matrix.md) and
> [verification and limitations](verification-and-limitations.md).

Part of [RE 012](../012-projectile-execution.md).
All findings below are static client evidence in the pinned executable.
They establish local execution/presentation contracts; authoritative damage rules need separate evidence.

## 1. Registry and concrete factories

| Skill | Registry | Factory / constructor | Final client vtable |
|---|---|---|---|
| Ordinary Fireball | `0x14126a770` | `0x14128d9f0 → 0x141a8b150`, size `0x530` | `0x14333ac40` |
| Kinetic Blast / Clustering | `0x141269390` | `0x141297170 → 0x141b6eb70`, size `0x550` | `0x14326b9c0` |

The common factories may delegate to mine/trap/totem forms for effective stats `0x36c/0x14c/0x35a`.
The ordinary concrete path must not be assumed for those owners.
KineticBlast/KineticBlastAltX use ActiveSkills rows 539/540 and ActionType `ClusterBurst` (`0x4d96`).
The internal `[DNT] KineticBlastAltY` entry does not establish another playable gem.

An early exploratory Fireball candidate with hash `0xaa4e`, factory `0x1412817b0` and table `0x1434462c8`
was a different charge-like class.
The exact ordinary Fireball registry above supersedes that candidate; hash equality alone is insufficient.

### 1.1 Why the client Fireball table supersedes the base table

The checked startup registration pass in `0x1400fe9f0` calls base initializer `0x141947e70` at
`0x1400feae1`, then client initializer `0x141249120` at `0x1400feae6`, with no branch between them.
Their guarded Fireball registrations are respectively `0x14194d923 → 0x141973cd0` and
`0x14124fe57 → 0x14126a770`.
Both wrappers pass the **same** literal at `0x14325d858` (`Fireball`, hash `0xaa4e`), metadata callback
`0x14128d9e0`, and fourth argument zero to registrar `0x141941bb0`.
That fourth argument means register both key variants 0 and 1; it is not a network mode.

Registrar lookup uses shared registry `0x144755188`. Existing-key lookup returns the existing payload,
then the registrar unconditionally writes factory/metadata at `0x141941c46/c49` and `0x141941c72/c75`.
Thus the later client factory replaces the earlier base factory for both checked keys:

| Registration | Factory | Ordinary branch's final primary table | `+0x530` |
|---|---|---|---|
| Earlier base | `0x141988dd0` | `0x143335d80` | `0x1412e34e0` |
| Later client | `0x14128d9f0` | `0x14333ac40` | `0x1413dfd00` |

Both call constructor `0x141a8b150`, which initially installs the base table. The client factory then
loads `0x14333ac40` at `0x14128dadb` and stores it to the object at `0x14128dae2`, before returning.
The common constructor alone therefore does not identify the final client callbacks.
The standard lookup `0x141941c90` resolves the keyed record and calls its stored factory at `0x141941d5c`.

This closes the ordinary final-class selection for the checked successful startup pass, including its
static-initialization guards. It does not generalize the ordinary branch to proxy wrappers or claim that every
possible later registry writer has been exhausted. Round17 `fireball-startup-winner.zh-CN.md`,
`fireball-startup-focused.asm` and `fireball-startup-audit.json` retain the exact calls, keys, overwrite stores
and four base/final table slots. The audit independently reads 12 aligned functions and 34 instruction/RIP
assertions from the pinned PE. This startup rule is static evidence, independently of live route observation.

## 2. Ordinary Fireball

Concrete table binds common release `+0x4b8 → 0x141ba4930`, update `+0x118 → 0x141ba9090`,
memory insertion `+0x4c0 → 0x141bae0c0` and the shared Split/Chain/Fork/Pierce/Return helpers.
Effective eligibility still depends on their stat guards.
Its important specializations are:

| Slot | Target | Role |
|---|---|---|
| `+0x450` | `0x1413dfc70` | concrete clone |
| `+0x510` | `0x141a8b580` | flight resource, with special byte `+0x528` branch |
| `+0x530` | `0x1413dfd00` | result-stage advancement and terminal explosion presentation |
| `+0x558` | `0x141a8b640` | special mover inputs then common `0x141bacf60` |
| `+0x568` | `0x141a8b5d0` | borrowed `+0x520` release-context targets folded into common memory |
| `+0x570` | `0x1419eee20` | common local spell contact/avoidance gate |

### 2.1 Terminal visual and result stage

`0x1413dfd00..0x1413e00e1` advances result stage through `0x14193a170` on every invocation,
and advances once more if common virtual `+0x498` reports its special multi-result condition.
It creates the large local explosion object only when the flight object exists and local reason is zero.
Pierce/Fork/Chain continuation reasons therefore do not create that particular large EffectObject here.
With a target, the effect is positioned at its grid; without one, at the projectile grid.
This includes a reachable targetless terrain-terminal presentation path; it is not an in-game wall test.

The common spell gate branches on virtual `+0x770`:
true uses local gate `0x14193a8c0`; ordinary false passes owner stats, target,
sequence/derived/current-stage and a false flag to `0x141ebd2f0`.
Those identities also feed local avoidance calculation; arguments alone do not identify a queued S2C consumer.
The inspected `0x141ebd2f0` body uses spell-dodge stats and local random admission, not the Actor result queue.
It does not establish that the client authoritatively subtracts life.
The helper returns true when this dodge gate permits the target;
`0x1419eee20` inverts it, so virtual `+0x570` true means exclusion by this gate, not a confirmed hit.
Its ordinary path passes `nondeterministic=false`; see [local spell randomness](random-inputs.md#64-local-spell-dodge-is-another-random-family).

**U:** no separate area-candidate loop was found in this concrete contact method.
The ordinary Fireball exact-consume edge is not closed for either the contacted target or additional area targets.
Do not infer no area damage, direct-plus-explosion double damage or a shotgun rule from this gap.

The full `+0x530` body saves its Entity target in `r15` and uses it at `0x1413e004b..0x1413e0067`
only to choose Positioned grid coordinates for the effect. It does not enumerate area targets or pass that target
to an exact-result consumer within this body. Shared history insertion and reservation removal are different
operations from Actor result-queue consumption.
Immediate `0x1af` and queued `0x1af` followed by matching nonzero-process `0x1b0` can both attempt result feedback
without any local area consumer; their native ingress contracts are already established.
The [actual byte-reader composition](results-and-protocol.md#31-actual-payload-decoding-fresh-defaults-and-dispatch)
also checks the independently selected immediate/hit-data bits and fresh optional-field defaults;
it preserves the distinction between a parsed result, an attempted feedback callback and numerical damage.
Which route the official server chooses for ordinary Fireball requires message evidence.
Searching for a local AoE loop is therefore not a prerequisite to explaining every possible supported result route.
The [joined stage/result controls](results-and-protocol.md#61-joining-emitted-contacts-installed-skill-callbacks-and-the-same-actor-queue)
now execute the real Fireball reason callback after shared Pierce and Return, on the same emitted action/Actor queue.
Its nonzero reason naturally skips the local explosion; current-stage advancement, retained ordinary results and
later immediate/control processing execute. Reason-0 explosion presentation and the ordinary official result-route
choice are not closed by those nonfinal paths.

The final Fireball `+0x2d0/+0x2d8` result callbacks are both void no-ops;
they do not supply the missing area path (see [result dispatch](results-and-protocol.md), section 8).
After `+0x530`, common reason handler `0x141baf5f0` has a separate reason-zero resource-event path.
It resolves the flight object's Projectile component and calls
`0x141de6000(resource+0x58, componentEventReceiver, null)` with a target,
or the resource's `+0x70` list without one.
The target selects the list; it is not passed as the third parameter.
`0x141de6000 → 0x141de5ec0` builds a script context and dispatches `0x60`-byte **script entries**
through `0x141df49e0`; this loop is not an area-target enumeration.
The default resource-event path has now been closed as described below; it does not supply the missing area loop.
The located direct relative call/jump reference to radius helper `0x141a8b2f0` is
`0x1413dfd5e` in the explosion-visual path; this bounded search does not exclude other indirect area logic.
Do not derive an immediate-result kind or damage quota merely because these candidate local consumers are absent.

The default extracted art association is `ProjectilesArtVariations[166]` (`fireball`, variant 0)
to `Projectiles[0]` (`Metadata/Projectiles/Fireball`), inheriting `Metadata/Projectiles/Projectile`
and using `Metadata/Effects/Spells/fireball/fireball_new2.ao`.
These are zero-based JSON row indices, not one-based native stat keys.
The checked GGPK path inventory has `Projectile.ot`, with `extends "nothing"` and an empty Projectile block,
but no separate `Fireball.ot`.
The AO's direct configuration specifies sound/particle/trail effects and the `fireball_explode` impact animation,
without an explicit area/target script; its `Metadata/Parent` inheritance was not expanded in this cut.
Text alone would not prove runtime defaults; the following native constructor/parser/data-row paths supply that evidence.
The retained data association and read-only OT/AO identities are in
`round4/fireball-resource-table-witness.json` and `fireball-resource-identity.json`.
No extracted game assets belong in the repository.

### 2.1.1 Default Projectile events: exact binding and empty initialization

Resource constructor `0x141e23b00..0x141e23c1c` allocates `0xb8` bytes and installs table `0x1435a0400`.
It zeros every qword from `+0x10` through `+0xb0`, including three event vectors:

| Text field | Resource vector | Parser |
|---|---|---|
| `on_explode_hit_object` | `+0x58/+0x60/+0x68` | `0x141e37df0` |
| `on_explode_hit_terrain` | `+0x70/+0x78/+0x80` | same |
| `on_chain` | `+0x88/+0x90/+0x98` | same |

The parser compares literal keys and calls `0x141de5e20` on the selected vector.
Empty parsed input returns without appending; nonempty input appends and compiles/copies a `0x60`-byte script entry.
The constructor does not compile or inject a default event.
The complete Projectiles-row fill `0x141e378d0..0x141e37beb` writes main/preload resource lists, not event vectors.
Component factory `0x141e37850` stores the resource at component `+0x18`;
component AO selection `0x141e37ed0..0x141e37fe6` does not inject events either.

Together with the checked empty default OT, the mapped construction/parser/row/component chain contains no hidden
default AoE script to explain ordinary Fireball area-result admission.
Other generic overrides/late resource writes are not exhaustively enumerated.
This closes these specific default-event questions, not the global Fireball area mechanism.
The large explosion visual uses the separate action callback even when these event lists are empty.
The later [Actor configured-event dispatcher](results-and-protocol.md#81-a-separate-configured-event-dispatch-receives-the-existing-result-target)
also receives an existing single result target; its native loops select configuration, not AoE targets.
The Actor table parsers and ordinary Witch metadata inheritance are now traced in that chapter:
the two explicit custom-event property families are absent from the checked player configuration.
Other script behavior/listeners are not exhausted; ordinary Fireball queued extra-target consumption remains open.

The previously suspected `+0x520` list is not evidence for that ordinary area-result path.
Ordinary ctor `0x141a8b150` sets it null and `+0x528=false`.
Alternate ctor `0x141a8b1d0` stores a borrowed pointer and sets `+0x528=true`.
Its two located callers, `0x1413f0570/0x141c92c20`, require `monster_projectile_variation == 1`.
Their adapters pass a prepared 32-byte emission record through `0x141c928b0` and virtual `+0x590`.
`+0x568` resolves target references, inserts IDs into projectile phase memory and clears the borrowed pointer.
The record comes from the parent's `+0x368/+0x370` vector, or the additional group's `+0x398` vector.
Its leading begin/end vector is traversed at stride 16; target references supply IDs.
This monster emission-record branch is not a proved ordinary Fireball explosion-hit list.
The upstream target-entry preparation remains open.
The adapter at `0x141c928cb` passes its third argument; it does not pass through the separate incoming event context.

### 2.2 Radius, visual scaling and height

Radius helper `0x141a8b2f0..0x141a8b514` reads primary effective/base radius stats `0x4876/0x4875`
and common AoE modifiers.
Distance-sensitive stats `0xd1a/0xda8/0x1702` also affect its inputs;
the [shared radius supplement](area-radius.md) reconstructs its integer root, intermediate rounding,
cached final-stat branches and exact final-class getter bindings.
With no applicable modifiers the direct effective-radius branch uses `max(0,stat4876)`.

For ordinary radius at least 14, resource resolver `0x141940f60` first requests slot 1,
then falls back to slot 0 if unavailable; smaller radii request slot 0.
Selected resource integer `+0x24` supplies its authored radius.
For nonzero effective radius, visual scale is `effectiveRadius/(resourceRadius*0.09200000017881393f)`,
or 1 when no resource radius is supplied.
Zero effective radius skips that scaling block and keeps the object's default scale.
Positioned `+0x2b0` receives this scale unless `+0x1e6` bit 2 prevents it.
Grid XY becomes cell-center world XY and the default/client height path is requested.
Thus a fixed unscaled server effect can diverge from the native explosion even when damage radius is correct.

### 2.3 Mover and unresolved variants

`0x141a8b640..0x141a8b879` contains a caller-flag/subtype-1 random-direction specialization:
four words `[sequence,derived,owner,repeats2d0] → 0x14146d8f0`, then an angle in `[-15,+15]` degrees
and integer endpoint rotation around current flight grid.
This is not evidence to jitter ordinary Fireball unconditionally.
A separate action-key query `0x6db5` may replace target height with terrain before the common mover.
That lookup uses `0x141940b00`; its key namespace is not the one-based Stats table.

## 3. Kinetic Blast group identity and target exclusion

Concrete contact `0x1412e1b10..0x1412e1ede` stores the trigger target at `action+0x128`
through `0x14193e0a0`, stores impact grid `+0x130`, then clones through `0x1412e13a0`.
It assigns the group a fresh derived byte, sets `+0x520=1`, generates positions,
advances the source stage and creates a separate primary contact flash.
No contact-reason filter blocks group creation for Pierce/Fork/Chain in this method.

The clone chain reaches `0x141938020`:
`0x14193823d..0x141938245` copies the stage and `0x14193826a..0x141938271` copies the trigger pointer.
Fresh registration replaces the derived byte but does not reset the copied stage.
Each actual explosion later advances that group's stage, independently of its source action.

Explosion method `0x141ac7040` queries candidates, invokes
`+0x7c0 → 0x1412e9d10 → 0x141939f40` for each accepted target, creates its local effect and advances stage.
Candidate filter `0x141acbcb4..0x141acbcbe` explicitly rejects `candidate == action+0x128`.
This closes the trigger-pointer chain from contact through clone to each group explosion.
Different explosions may invoke the same other target; no whole-group consumed-target list was found in this loop.
This is a local callback fact; server damage admission is separate.
The [KB exact-consumption composition](results-and-protocol.md#41-joining-a-kb-explosion-to-actual-current-stage-consumption)
executes this candidate filter and current-key consumer on a supplied registered group and actual Actor queue.
Two explosions consume stage-3/stage-4 records for the same other target, while excluding the trigger and leaving
an old-stage duplicate. A late old-stage record is not consumed by the next explosion's new-stage query.
Clone/identity/center/radius/clear-path inputs, effect creation and ProcessResult remain explicit boundaries.

The native radius query `0x141acc1f0` includes signed Positioned body radius `+0x1e3`:
`centerDistanceSquared <= (explosionRadius+targetRadius)^2`.
It also checks team/state/local hit gate and a path query.
This differs from the current Gateway's point-target approximation; no server geometry was changed here.

### 3.1 Joining an emitted source to its constructed explosion group

**S:** Round55 continues the finite original-instruction composition on the emitted KB action. It invokes the
installed `+0x530` reason body `0x1412e1b10` through `0x1412e1bbb`, stopping before the separate contact-flash
presentation suffix. Trigger assignment, origin selection, cloning, fresh identity registration, sector generation
and source-stage advancement all execute; a registered group or chosen centers are no longer supplied.
This is a bounded invocation on the emitted object, not a complete root skill release or contact-flash reconstruction.

The ordinary factory's actual post-common-construction suffix `0x141297264..0x1412972d1` establishes
`+0x520=false`, timer/index `+0x524/+0x528=0`, an empty position vector and visual ordinal zero.
The base constructor remains supplied. The emitted action itself is now copied through the original six-level
factory/copy chain, with empty owned-event storage, cache-refresh and activation-event boundaries disclosed.

The resulting order matters:

1. Assign the trigger Entity, including its subscription boundary. If nonnull and its **unsigned** Positioned
   size byte is less than five, use the target grid; otherwise use the flight object's current grid.
2. Execute `0x1412e13a0 → 0x1412e12a0 → 0x1412439d0 → 0x141ba2410 → 0x1419e2d40 → 0x141938020`.
   Copy sequence, current stage, trigger, impact grid, repeat inputs and shared derived-counter pointer;
   clear the derived/registered/activation fields, flight pointer and this copy's contact-history pointers.
   KB's group flag, timer, index and successful-position vector are copied, not unconditionally reset.
3. Allocate the fresh derived byte through `0x141421a10 → 0x14193dc60`. Registration `0x14193daf0` sets
   `+0x11e`, writes that byte, appends to the same Actor retained vector and activates through the actual
   `+8 → 0x14193a700` latch before position generation. Its downstream activation event is a boundary.
4. Set group `+0x520=true`, generate positions using the group's newly assigned identity, then advance the
   source stage. The group retains the stage copied **before** that source advancement.

A finite source at `(sequence=4660,derived=2,stage=3)` therefore produces group `(4660,3,3)` and then
becomes stage 4. A second invocation on that source produces `(4660,4,4)` and advances only the source to 5;
the first group's identity, trigger and stage remain unchanged. These are supplied-input witnesses, not official
sequence values. Nondefault copy controls preserve timer 0.25/index 1, deep-copy two positions, clear history
and flight pointers, and increment a supplied nonlast shared-counter control block from two owners to three.
No target-event delivery, last-owner destruction or full history-sharing lifecycle is established by those controls.

The actual RNG initializer, bounded draws, finite distribution, rotation and grid conversion generate the centers.
Their samples agree with the separately sealed arithmetic reference. The trigonometric listing interpreter adds
the original scalar range-reduction body for the observed KB angle above two pi; eleven finite sine/cosine
controls cover signed inputs through four pi. This adds finite interpreter coverage, not all trig domains or a
floating-point-environment guarantee.

The generated group then executes its installed update, original common update and original explosion/area/result
chain. With a supplied radius and body-inclusive target, stages 3/4/5/6 consume four corresponding target-202
records, while excluding the trigger and retaining a duplicate stage-3 result. Both supplied World getter modes
1 and 2 retain this result. See [result composition](results-and-protocol.md#42-joining-source-generated-kb-groups-to-scheduled-results).

Remaining explicit inputs include common construction/equipment/raw stats/selected-provider fallback, actual
area-cache refresh and activation events, empty event/weak storage and allocation, terrain test outputs,
explosion-radius output, target metadata/components/registration, deeper prediction, presentation and ProcessResult.
This does not establish authoritative damage quotas, packet arrival times, official result routing or all owners/modes.

### 3.2 Joining the generated group to Actor snapshots and target references

**S:** Round56 continues the same emitted-source/group composition through the original initialization helper,
copy-time area-cache refresh, target subscription container and Actor retained snapshot. It replaces direct calls
to the group's updater with the actual Actor dispatch region `0x141e8f399..0x141e8f46f`. Its post-root registers
and float delta are supplied; the root runner, enclosing frame producer and later Actor timers are not executed.
The supplied raw World modes 0 and 2 do not change that region's snapshot/dispatch behavior. This is not an
official first-frame timing measurement or proof of every owner's admission.

For the established ordinary-startup premise `0x1445b38cd=1`, registration executes the actual
`+8 → 0x14193a700 → 0x14193bae0`: latch `+124` becomes one, then the contribution body is bypassed.
This is the [previously recovered initialization gate](action-initialization.md#1-registration-retains-first-then-initializes)
on the actual new group, not an inert activation callback. Copy also executes `0x1419e3030`; the same global
skips its overlay body **but preserves its placed-duration tail**. Supplied `(2cc,57e7)` inputs `(2500,25)` and
`(-2500,25)` produce copied cache `30c=50` and `-50`; `(10000,25)` clamps to 100. Nonzero caches continue into
the original radius/unsigned-integer-root helpers during position generation. These controls do not establish
actual mine/trap stat grants or invalidate the distinct [cache lifetime contract](area-radius.md#11-action-field-producers-transfer-and-recomputation).

The Actor region allocates/copies its retained pointer snapshot, checks each action's retired flag and invokes
the installed `+118` with the incoming delta. A group registered **before** that copy is already present and
can perform its first explosion on that pass, including supplied delta zero. In a separately declared interleaving,
registration **after** the copy does not add the group to that pass; it is visited on the next supplied pass.
This executes snapshot isolation, not an invented claim that ordinary registration occurs at that interleaving.
After the last explosion, a later group update marks it retired. A further Actor pass takes the removal branch:
original `+f8 → 0x141e930e0` removes the retained pointer, then calls action cleanup and deletion boundaries.
The actual runtime pointer-find scalar branch is executed with CPU-feature inputs zero. Temporary snapshot
allocation/memcpy, final action cleanup/deletion and last-owner destruction remain explicit boundaries.

Target setter `0x14193e0a0` now executes its actual list operations rather than a subscription placeholder:

- Remove the first old-target record whose callable type is zero, owner is this action, stored callback is
  `0x14193de50` and signed owner adjustment is zero; `0x140146560` compacts the remaining bytes.
- Invoke action `+3a0`, assign target Entity to `+128`, then subscribe a nonnull target through `0x14019ff20`
  at Entity `+c8`. The supplied empty inline container receives a `0x20`-byte record with actual table
  `0x14301c988`, owner action, callback and adjustment, followed by the empty sentinel.
- Source assignment and copy independently subscribe source and group. Replacing/clearing one action's target
  preserves the other's subscription. Reassigning the same target removes then appends that action's record;
  it does not accumulate an additional identical subscription in these controls.

Explicitly invoking that installed record's `+18 → 0x1401459a0` reaches the stored callback without substituting
its owner/function. `0x14193de50` invokes `+3a0(null)` and clears **that action's** `+128`; it neither retires
the group nor erases the subscription record itself in this body. Its remaining lookup uses the exact literal
`Pathfinding`, not Actor. With a supplied successful owner-component lookup, if Pathfinding `+538` borrows
the notified Entity's Positioned `+294` grid address, the callback copies that grid into Pathfinding `+550`
and redirects `+538` to the owned copy. A different borrowed pointer remains unchanged.
This is a conditional pointer-lifetime repair, not proof of homing behavior, Entity destruction timing or
the event-container producer's cleanup policy. Actual event delivery and component lookup remain supplied.

The fresh audit retains 258 ranges / 16,009 decoded sites, six slots and fourteen anchors. Twenty-four finite
controls execute 102,563 original-instruction steps / 4,560 visited sites, including six BTS and seven scalar
pointer-find support controls. These are offline instruction interpretations; source construction/stat publication,
terrain/target/prediction inputs, event delivery, complete object destruction, presentation/ProcessResult and
official frame/packet policy remain open. No production behavior or service deployment changed.

### 3.3 Deferred target removal and action cleanup are separate events

**S:** The target callback above is reached by a concrete deferred Entity-removal path. It is not inferred
from a destructor or an arbitrary `+c8` offset. Request `0x141ebe680` ordinarily sets Entity `+8c` bit 0,
obtains its manager through World `+190` and appends the Entity to manager `+b8/+c0/+c8`.
An already-set bit suppresses another append. Its separate bit-4 diagnostic branch is outside this ordinary
path. In client World table `0x14346e0c8`, `+190 → 0x14164dc50` returns World `+a18`;
the conditional `+98` call for object IDs below `0x40000000` is bound to void no-op `0x1400f0f00`.
This request body does not itself publish the target event or immediately free the Entity.

Manager `0x141ebfde0` first handles its distinct pending-add vector, then **detaches** the entire removal
vector into local storage and zeros the manager's three removal pointers. It visits the detached batch in
vector order through `0x141ec01c0`. New requests during that visit belong to a subsequent batch; this is
not an instruction to recursively drain them in the same invocation. The shared World simulation calls this
manager at `0x142183dd5` and `0x142184411`; their enclosing mode/message/frame contract remains in
[World scheduling](world-scheduling.md). These call sites do not establish a universal fixed tick rate.

For each removed Entity, `0x141ec01c0` has this order:

1. Visit its ordered component vector through component `+68`, then clear its task container.
2. Invoke `0x14019a240(Entity+c8, Entity)` at `0x141ec0228`.
3. Set Entity `+8c` bit 1, detach spatial/Positioned state and remove registration/storage references.
4. Invoke the Entity's deleting destructor through its actual primary slot zero.

The event publisher installs an active traversal cursor, advances its next-record pointer through callable
`+8` **before** invoking callable `+18`, and restores the prior cursor on ordinary completion.
It dispatches the source/group's installed `0x14301c988` records to `0x14193de50`: each action clears its
own target, with the conditional borrowed-Pathfinding-grid repair described above. Neither the request
flag alone nor the callback retires the KB explosion group. Its subsequent updates still follow its own
timer and lifetime; target-object removal is not synonymous with action completion.

The Entity final table `0x143456508` binds `+0 → 0x141556730 → 0x141556620`. That destructor destroys
the `+1c8`, `+148` and `+c8` containers through `0x140129940`. Container destruction invokes callable
**slot zero**, not callback slot `+18`; it also zeros active cursors' next pointers before releasing storage.
Consequently event publication and subscription destruction must not be counted as two target-loss events.
The exact constructor slice `0x141ebe014..0x141ebe05c` initializes those inline containers in the controls;
the rest of Entity/type/component construction remains supplied.

Action completion has its own publisher, **`0x14193d1f0`**, using action `+280` and an idempotence byte
`+126`. It sets the byte **before** traversing listeners, so a listener's recursive publication attempt returns
without a second notification. Actual Actor retirement calls it before virtual deletion. Base destruction
calls it again, but the same latch suppresses duplicate publication. This function is not target unsubscription.
An explicit listener control that destroys the container zeros the cursor's next pointer and stops traversal.
That path skips normal cursor restoration; the destroyed container is not claimed safe to reuse.

The deleting-destructor bindings are `0x1412465e0` for ordinary bow/wand and Fireball,
`0x1412e1440` for KB, `0x1413754b0` for TS and `0x141378f30` for BA. They reach shared
`0x141ba27c0 → 0x141240480 → 0x14193a460`, with class-specific resource cleanup. The shared projectile
destructor detaches its flight callbacks, requests deferred removal of a nonnull flight Entity, then clears
the flight pointer. The base action destructor independently removes its matching `0x14193de50`
subscription from a still-present target, before publishing/clearing the action event container.
Destroying the group first therefore leaves the source's separate target subscription intact. If the target
has already notified and cleared the action's target pointer, that base unsubscribe branch is skipped.

Round57's finite original-instruction compositions join the real request, detached batch, publisher, installed
target callback and Entity destructor with explicit empty-component/task and spatial/resource boundaries.
They also extend Actor retirement into the installed KB destructor and target unsubscribe. Explicit callback
controls test recursive action publication, removal of a later listener, container clearing during publication
and a new removal request during a detached batch; these mutations are test inputs, not identified official
subscribers. Root/frame production, actual target/component registration and lookup, nonempty task/component
destruction, final resource/storage management, numerical ProcessResult and official packet timing remain
separate. Ten controls execute 86,334 original-instruction steps / 4,798 visited sites; the fresh byte audit
covers 297 ranges / 17,477 decoded sites, eight slots and twenty-two anchors. This establishes a local
execution/lifetime contract, not a new C2S reporting message or full skill trace.

## 4. KB positions and exact random consumption

`0x141ac6bd0..0x141ac6f58` reads cluster count `0x4fea` and additional-blast chance `0x11f3`.
Its independent eight-word RNG input is
`[sequence,derived,owner,repeats2d0,zeroExtendedInnerRepeat2b8,0,0,0]`.
Let `U` be the native finite distribution `[0,1]`, which uses 2001 logical integer samples.

1. Always sample `U0`, including chance 0/100.
   If `float(chance) > f32(U0*100)`, increment the count `n`.
   The comparison is strict; chance 100 can fail at the endpoint `U0==1`.
2. Compute sector width `W=f32(6.2831854820251465f/n)` and always sample rotation `phase=f32(U1*tau)`.
3. Read fixed distribution radius from secondary effective/base radius `0x4879/0x4878` via common AoE inputs.
4. For each sector `i`, start at `f32(f32(float(i)*W+phase)-W*0.5)`.
   Each candidate draws a fresh angle inside that sector and rotates `(0,-R)`, rounded to integer XY.
5. A retry counter starts at four and decrements **before** the tests.
   Zero exits immediately: at most three candidates are queried; a fourth draw is consumed and discarded.
6. Acceptance requires no blocked line sample on layer 1 and endpoint layer-0 value at least 1.
   Append only successful positions; omitted sectors leave no timeline placeholder.

The line test is inclusive dominant-axis integer stepping.
World grid values are packed nibbles; absent/out-of-bounds values are zero.
The exact layer meanings relative to rendered beaches/walls remain unproved.
Do not replace these two different layer queries with an assumed rendered-wall test.
Draw count is two plus one to four logical angle draws per sector, plus any raw bounded-rejection draws.
There is no random radial-distance draw in this path.

## 5. KB timing, area and presentation

`0x141ac6f60..0x141ac7035` first runs the common projectile update.
For group actions, it subtracts frame delta from a float32 timer and, when due,
emits **one** explosion, adds `0.10000000149011612f` to the timer, and increments its position index.
There is no catch-up loop; negative time debt is preserved.
Completion is detected on a later update after the final explosion.
Ordinary source construction initializes timer/index to zero, but creation/update ordering still determines
when the first eligible group update actually runs.
Only retained successful positions participate, so omissions compact later explosion times.

The [source/group composition](#31-joining-an-emitted-source-to-its-constructed-explosion-group) executes this
timer with the original common update on a clone whose flight pointer was cleared by construction. Zero initial
timer permits an explosion on its first supplied update, even with delta zero; no fixed first-frame delay is added.
After a first zero-delta update, a supplied delta 0.35 emits only the second explosion and leaves timer about -0.15.
Two subsequent zero-delta updates emit one each, retaining the debt; a further update retires the exhausted group,
even though its timer is then positive. This does not determine the actual World/frame time of its first update.
Rejecting the first sector by three line tests or three endpoint tests discards its fourth sampled candidate,
leaves three retained centers and produces stages 3/4/5 at the first three due updates. No empty stage/time slot
is reserved for the rejected sector. Rejecting every sector leaves no explosions and retires on the first update.

This native interval is 0.1 seconds.
The existing server's first-delay-zero / interval-1/30-second values were provisional feel-based choices;
they are not native measurements and are not changed by this research.

Explosion radius uses primary radius stats `0x4875/0x4876`, separately from distribution radius.
After Fork, generation `+0x45c > 0` adds stat `0x3213` to the common AoE-percent input.
Each explosion effect requests resource slot 0 with incrementing visual ordinal `+0x548`,
falls back to an unspecified ordinal if missing, and normalizes scale using the authored resource radius.
The primary contact flash requests slot 1 and is not another secondary damage explosion.
Both effects use local objects and client/default height.

Common resource resolver `0x141940f60` initializes
`[sequence,derived,owner,requestedSlot,0,0,0,0]` for resource choice,
reads the action profile through `+0x380`, and reaches `0x141f66ea0 → 0x141f70f30`.
Resource slot, fallback, optional index and current stats participate.
The [resource-selector follow-up](resource-selection.md) maps ordered candidate lookup, current variant-stat input,
separate flight/effect association arrays and indexed/random selection.
Association ordering and exact-key directory construction/lookup are mapped without a generated variant-zero retry;
effective variant inputs, later map mutation and cache invalidation remain open.
Geometry RNG alone cannot establish MTX parity.

## 6. Evidence and next obligations

Retained local report: `logs/projectile-execution-model/round3/fireball-kb-findings.zh-CN.md`.
Use `fireball-true-*`, `fireball-result.asm`, `skill-shared-copy.asm`, `kb-core.asm`,
`kb-clone-contact.asm`, `kb-area-query.asm` and `kb-presentation.asm`.
`fireball-area-followup.zh-CN.md`, `fireball-area-gate.asm` and `fireball-monster-release-full.asm`
refine the local dodge gate and the borrowed monster emission-record identity.
Earlier `fireball-factory.asm`/`fireball-vtable.txt` describe the superseded same-hash candidate.
Unwind fragments alone truncate the ordinary Fireball mover; its explicitly checked end is `0x141a8b879`.

Close ordinary Fireball's actual result route, radius/cache input producers, effective resource inputs/cache lifecycle,
world-layer meaning, creation/update timing and each applicable continuation before a full parity claim.
No per-projectile C2S reporting contract was found in these bounded skill paths.
That negative search does not establish global absence.

Round16 `fireball-contact-result-reassessment.zh-CN.md`, `contact-target-identity.asm` and
`fireball-contact-result-boundary.asm` retain the complete terminal body and parameter-type correction:
common contact accepts Positioned, whereas terminal/reason callbacks accept Entity.
