# Projectile TLS random state: one Pierce qualification

## Question and current state

Can one ordinary projectile's **prevent-Pierce** decision be reconstructed from a bounded local random
scope, and what inputs would a server need to reproduce it?

This is the K1 investigation under [#467](https://github.com/LocalExile/LocalExile/issues/467).
It starts with the ordinary Fireball binding of shared qualification: selected prevention chance strictly
between 0 and 100, a legal Pierce opportunity and no earlier mode/prohibition rejection. These are witness
conditions, not a claim that a particular running character currently supplies those values.

**Draft; local gate, selected contact ingress and one queue-worker startup route checked; no scope closure yet.** The baseline already
recovers the helper arithmetic and selected consumption chains. Actual incoming TLS state, its enclosing
scope, thread membership and preceding consumer order remain open. A stopped search is not proof that
no enclosing scope exists.

The research uses client 3.29.3b, executable SHA256
`78a0e227189c263658a7f65a02f086428b859bfc114ae8e7ce8aab721ebd48d5`.
The source base is `d377bd256036242d2fb43f53eb7110b4897e1862`.

## Fixed evidence and reuse boundary

Baseline [PR #468](https://github.com/LocalExile/LocalExile/pull/468), fixed at
`aaebde497419b5167f9c56cacfe7aa70a18f5f51`, is a documentation dependency, not this branch's parent:

- [Random inputs, scopes and joined TLS consumption](https://github.com/LocalExile/LocalExile/blob/aaebde497419b5167f9c56cacfe7aa70a18f5f51/Agents/docs/re/projectiles/random-inputs.md).
- [K1 definition and finite followup](https://github.com/LocalExile/LocalExile/blob/aaebde497419b5167f9c56cacfe7aa70a18f5f51/Agents/docs/re/projectiles/verification-and-limitations.md).
- [World/Actor scheduling boundaries](https://github.com/LocalExile/LocalExile/blob/aaebde497419b5167f9c56cacfe7aa70a18f5f51/Agents/docs/re/projectiles/world-scheduling.md).

Reuse the existing seed-writer classifications, temporary-scope examples and callback-envelope audits.
Do not restart a whole-image TLS writer/consumer inventory or the old interpreter dependency graph.
Historical navigation is a lead; any new assertion must be checked against the pinned original bytes.

The selected gate is the call at `0x141ba9708 → 0x140e3b450 → 0x140e3b310`.
The known thread-local state is at TLS `+0x170`; its transition is uint32
`state = state * 0x41c64e6d + 0x3039`. A chance request samples seven bits and rejects values at least
100, consuming state on rejection. These inherited facts do not supply the actual incoming state.

## Investigation steps and closure criteria

| Step | Required result | State |
|---|---|---|
| T0: freeze and preflight | Pin baseline, witness, known stopped routes and exact missing edge | Complete; prior World/scope audits reused |
| T1: local gate | Verify reached/skip conditions, TLS address and direct caller state access | Fresh original-byte and final-slot checks complete; inherited arithmetic reconfirmed |
| T2: enclosing boundary | Bind the selected caller to a concrete seed/save/restore or an evidenced inherited-state boundary; list intervening reached draws | Partial: D connects one queue-worker route to thread initialization; actual Fireball route and enclosing scope remain open |
| T3: reproducibility | Check exact state transitions with declared inputs; classify which inputs are action-derived, external or unobserved | Open |
| T4: review and delivery | Independent evidence review, current-base checks, bounded implementation implications and final PR scope | A/B scope review and D evidence review complete; closure and Ready validation pending |

A **local reconstruction closure** requires a proved scope entry/state source, actual selected control-flow
ordering, draw/restore behavior and a finite comparison witness. Supplied state may verify arithmetic but
does not establish the runtime state source or thread.

An **external-dependency closure** requires positive evidence that the selected decision inherits a named
external state/order input. Report the precise missing server input and the limits of the traced envelope.
This can answer the architectural question without proving client/server outcome equality. Seeing no seed
write in a few bodies is insufficient to prove dependence on the entire thread's history.

An **unresolved result** remains unresolved: an untyped callback, unknown caller or unobserved state is not
converted into either closure above. K1 retains all other witnesses and obligations after a local result.

## Investigation A: selected local contact envelope

The [compact receipt](projectile-tls-scope-evidence.json) pins A's six inspected code spans, ordinary
Fireball's final `+0x678` binding and fixed source documents. This is original-byte inspection, not runtime
execution. Reconfirming these inherited rules is not counted as discovery of a new seed producer.

| Selected boundary | Freshly checked fact | What it does not prove |
|---|---|---|
| Fireball final table `0x14333ac40`, slot `+0x678` | Binds qualification `0x141ba96a0` | Actual runtime class selection or effective chance |
| Qualification prefix through `0x141ba9715` | Mode/prohibition gates precede chance; selected key `0x3438` is read at `0x141ba96f1`; signed positive chance calls the helper at `0x141ba9708`; forced mask is checked afterward | A nonzero Pierce grant alone does not guarantee this TLS draw is reached |
| Chance helper `0x140e3b450..0x140e3b4a9` | Zero and unsigned at-least-100 shortcuts; ordinary chance draws seven bits and retries values at least 100 | No action-to-incoming-state seed is provided by this helper |
| Bit helper `0x140e3b310..0x140e3b393` | Reads the TLS base through `gs:[0x58] → [0]`; multiplies/adds current `+0x170` and stores it at `0x140e3b35b` | The helper does not identify the actual enclosing scope or executing OS thread |
| Ordinary contact `0x141ba7b30` | Calls virtual `+0x678` at `0x141ba8a15`; its inspected inline body has no GS access | Earlier direct/virtual callees and pointer aliases have not been proved free of state effects |
| Capture thunk `0x141a43720..0x141a4372c` | Loads the target and captured action, then tail-jumps to ordinary contact; this leaf does not establish a seed scope | State established before entering the thunk remains unknown |
| Query tail `0x141e32fb3..0x141e330a7` | Selected ordinary route loads capture pointer at Positioned `+0x198` (`0x141e33082`) and invokes its `+0x10` at `0x141e3301e`; special routes remain distinct | Earlier query/filter work and the caller of the spatial query remain outside this selected tail |

The leaf/slot/transfer facts join the chosen gate to the existing ordinary contact contract. This narrows
the local input path, but does not bind its incoming random value. In particular, absence of an inline GS
write cannot certify transitive absence of save/seed/restore, and the prior system-time seed writer cannot
be substituted for a real decision-point value.

The current missing edge is **state at the selected capture ingress, including effects of earlier reached
callbacks and any enclosing scope**. Previous World/dispatcher studies already left their scope/thread
binding open; repeating those generic traversals is not a new lead. The next investigation must name one
concrete connection to that ingress, or specify a bounded observation of its caller/state/ordering. If no
new edge narrows, the scope-review rule below applies. No whole-thread consumer inventory is authorized
by this local result.

## Investigation B: selected movement/query ingress

Fresh bytes match the fixed baseline's ten selected spans for Positioned update `0x141e32710`, rectangle
query `0x141e47a40`, candidate filter `0x141e47ca0` and capture dispatcher `0x141e32dc0`. These contain
795 linear instructions, not 795 executed instructions. Their established straight-frame path connects:

```text
Positioned update: 0x141e329ca -> rectangle query 0x141e47a40
rectangle query:   0x141e47c3c -> candidate filter 0x141e47ca0
candidate filter:  0x141e47dd4 -> capture dispatcher 0x141e32dc0
ordinary capture: +0x198 -> callable +0x10 -> 0x141a43720
```

These inspected inline bodies have no GS access. That observation does not eliminate state effects in
earlier callees, pointer aliases, the update's pre-query virtual `+0x180` callback at `0x141e32846`, or
an enclosing caller. It is not evidence of a globally unscoped random stream or a complete draw-order list.
The wrapper/query chain is reused evidence checked against the executable, not a newly recovered seed source.

After A and B the same core state-source edge remains open. **The agreed scope/method review is triggered**
before further traversal. In particular, do not keep expanding the World scheduler merely because it
is an ancestor. Any further static investigation must identify a concrete typed state/control boundary
and how its result could change the conclusion; otherwise retain the precise observation requirement.

## Scope/method review after A and B

The A/B independent read-only review confirmed that neither acceptance route had been met. The typed
call route was better pinned, but the required incoming-state edge had not narrowed. At that review,
no retained positive writer or alias connection justified another static state-source traversal.
Investigation D below is a subsequent, bounded static re-entry; it does not change A/B's result.

The pre-query `+0x180` call has returned at `0x141e32854`; its result is later passed as the rectangle-query
receiver at `0x141e329c4/0x141e329ca`. A save/seed/restore confined to that method cannot enclose the later
Pierce decision. A persistent write there could matter, but no inspected evidence establishes one.
Resolving this getter and finding another body without inline GS access would not close the state source.

Further generic wrapper traversal stops at this review boundary. Re-entry requires a concrete state-writer
connection or one source-attributed observation of the selected invocation, with:

1. Actual action/Positioned/contact identity, executing thread and dynamic call stack.
2. State entering the selected gate, the nearest identifiable seed/save boundary and that boundary's inputs.
3. All intervening reached state changes through the decision, including rejected samples, earlier contacts
   and any associated restoration. Candidate traversal and movement loops can repeat consumers.

Use one ordinary reached chance strictly between 0 and 100. Bound the observation to one selected invocation
and its nearest identifiable state boundary. If recording starts after that boundary or misses preceding
writes, the source remains unresolved. An observed decision-point state alone can check scalar arithmetic;
it does not identify server-required seed inputs or prove a complete random scope.

No such observation is asserted here and none was performed. Existing baseline observation tooling is not
itself evidence that a source-attributed record exists. The review constrains further investigation;
it does not replace the goal with documentation-only completion or promote K1 to established.

## Investigation D: a concrete queue worker reaches thread initialization

Following the user's choice to continue statically, D addresses one explicit gap in the fixed
[worker audit](https://github.com/LocalExile/LocalExile/blob/aaebde497419b5167f9c56cacfe7aa70a18f5f51/Agents/docs/re/projectiles/world-scheduling.md):
the connection between the generic thread setup and an actual queue-execution callable. It does not
repeat the World ancestor traversal or inventory all TLS writers. A preliminary check of the ordinary
Fireball pre-Pierce callback only reconfirmed an inherited condition; it is not counted as new progress.

Use `S` for an initializer's pool receiver, `Q` for one queue context and `TC` for one thread context.
These are descriptive labels for pointer roles, not recovered source class names.

| Concrete edge | Original-byte result | Limit |
|---|---|---|
| Initializer `0x140e30940` to creator `0x140e2f960` | Four callsites `0x140e30dbb/0x140e30e57/0x140e30efa/0x140e30f93` pass `Q = S + group * 0x178`, for groups 0–3; initialization later sets `S+0x600` | This does not identify the actual runtime `S`, configured worker counts or which branch a Fireball task takes |
| Creator to thread handoff | Creator captures `Q` in callable payload `+8`, installs table `0x1431fa7d0` and calls `0x140e2f3c0` at `0x140e2ff69`; copy/swap transfers that callable into `TC+0x50`, with its selected pointer at `TC+0x88` | Callable transfer is not a claim that unrelated destructors or callbacks are TLS-neutral |
| New-thread branch | The handoff passes entry `0x140e338e0` and a wrapper carrying `0x140e2f350(TC)` to `CreateThread`; the entry forwards through `0x1402167b0 → 0x1402167e0` | When `TC+0x20` and `TC+0x28` are both zero, the other branch calls setup on the current thread; it does not invoke this loop here |
| Worker setup and invocation | `0x140e2f350` first calls setup `0x140e2f210`; setup calls imported `GetTickCount` at `0x140e2f2fe` and stores its uint32 result into TLS `+0x170` at `0x140e2f315`. On the normal successful COM-initialization path, `0x140e2f37b` invokes the stored callable | This is a thread-start seed source, not a per-task seed, nor a measured state at the first task |
| Concrete callable to loop | Table `+0x10 = 0x140e33240` adds eight to the callable receiver and tail-jumps to `0x140e30000`; the loop loads the captured `Q`, calls dequeue `0x140e329c0` at `0x140e300c2`, then task `+0x10` at `0x140e300dc` | No inline `+0x170` reseed appears in this loop; callees and invoked tasks can still change or temporarily scope the stream |
| Submission to the queue's task output | Submission `0x140e31310` selects `Q = [wrapper+8] + group * 0x178`, conditionally enqueues via `0x140e2f550`; enqueue and dequeue transfer the selected callable through `0x140133540`. Dequeue places its selected pointer at output `+0x48`, which the loop reads at stack `+0x68` | This is a same-`Q` transfer contract. Equality of the initializer's actual `S` and a particular submission's `[wrapper+8]` is still a premise, not an observed identity |

The resulting **conditional** route is:

```text
initialize S's Q -> create TC with concrete Q capture
  -> new-thread branch -> setup(TC): GetTickCount -> TLS+0x170
  -> successful entry -> concrete capture +0x10 -> queue loop(Q)
  -> dequeue(Q) -> selected task +0x10
```

This closes the previously missing concrete worker-loop-to-startup connection. Together with the fixed
Simulation submission contract, it supplies an evidenced candidate route to an external thread-start
seed. It does **not** close the selected Fireball's incoming-state source. Current-thread node execution,
submission's direct fallback at `0x140e313b4`, and waiting-thread task execution remain alternatives.
The actual selected route, same pool instance, nearest scope inside the task, and prior reached consumers
are not established. In particular, `GetTickCount` plus a known LCG is insufficient to replay the decision.

The [receipt](projectile-tls-scope-evidence.json) includes the concrete capture leaves, transfer helpers,
all selected setup fragments, imported-function identities and instruction anchors. Addresses are
preferred-image VAs; contiguous unwind fragments are recorded separately. These are static facts,
not a client execution record or proof that every call in a body was reached.
Independent read-only review reproduced D's range, slot, import, anchor and bounded-reference checks,
including the same-queue capture transfer and successful-entry conditions. It found no substantive
finding and confirmed that actual Fireball routing and T2/T3 remain unresolved.

The finite reference lookup used the scheduler slices `0x140e31180..0x140e32c70` and
`0x140e30000..0x140e31180`, validating candidate instruction alignment. It found the four creator
calls above, but no direct reference to the initializer within those slices. The selected singleton
constructor and its installed callback methods did not establish that initializer connection.
Those negative results remain bounded; they do not prove absence of an initializer caller or a
different pool. Further generic scheduler/initializer expansion stops here.

The next static acceptance edge must bind **the selected Simulation submission's pool and execution
route**, or a concrete state-changing/scope boundary on the already selected contact path. A typed
pointer/call connection can justify that followup; another matching layout or no-GS wrapper cannot.
If neither is available, retain the unresolved result and the observation alternative above. D advances
one candidate source branch; T2/T3 and K1 remain open and this PR stays **Draft**.

## Scope review rules

- Each investigation records its starting edge, hypothesis, bounded query, new evidence, remaining edge and
  whether that edge narrowed. Re-reading a frozen result is not a new discovery.
- If the witness closes quickly, review whether the same proved scope permits **one adjacent consumer**
  comparison. Record the reason and revised acceptance criteria before expanding. Quick closure does not
  require adding unrelated work or delaying publication.
- After two consecutive investigations that do not narrow the same missing edge, stop further traversal
  and review whether evidence, scope or method is the blocker. Keep the Draft honest about the unresolved result.
- Reaching a whole-thread consumer census, general task scheduler reconstruction or unrelated render/asset
  graph triggers a scope review instead of automatic expansion. Record callable boundaries without recursively
  claiming every callee is harmless to TLS.
- New scope changes and substantial findings update this workbench and the Draft description. Ready requires
  a reviewed result and appropriate validation; elapsed time or number of rounds is not a correctness measure.

## Exclusions and observation boundary

This stage does not implement server RNG, promise universal TLS synchronization or change projectile
gameplay. It does not investigate all skill variants, damage/Buff/DoT formulas, all threads or all random
consumers. No client operation, launch, attach, new capture, CI notification or deployment is part of this
initial investigation. If runtime evidence is necessary, record the smallest required observation explicitly.

Public material contains no workstation paths, private captures, extracted tables, game files or disassembly
dumps. Evidence distinguishes original-byte facts, supplied-input checks, observations and unknowns.
