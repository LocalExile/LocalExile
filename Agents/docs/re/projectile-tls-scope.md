# Projectile TLS random state: one Pierce qualification

## Question and current state

Can one ordinary projectile's **prevent-Pierce** decision be reconstructed from a bounded local random
scope, and what inputs would a server need to reproduce it?

This is the K1 investigation under [#467](https://github.com/LocalExile/LocalExile/issues/467).
It starts with the ordinary Fireball binding of shared qualification: selected prevention chance strictly
between 0 and 100, a legal Pierce opportunity and no earlier mode/prohibition rejection. These are witness
conditions, not a claim that a particular running character currently supplies those values.

**Draft; no new scope closure yet.** The baseline already recovers the helper arithmetic and selected
consumption chains. Actual incoming TLS state, its enclosing scope, thread membership and preceding
consumer order remain open. A stopped search is not proof that no enclosing scope exists.

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
| T0: freeze and preflight | Pin baseline, witness, known stopped routes and exact missing edge | Defined; selected historical envelope evidence to inspect |
| T1: local gate | Verify reached/skip conditions, TLS address and direct caller state access | Pending fresh check |
| T2: enclosing boundary | Bind the selected caller to a concrete seed/save/restore or an evidenced inherited-state boundary; list intervening reached draws | Open |
| T3: reproducibility | Check exact state transitions with declared inputs; classify which inputs are action-derived, external or unobserved | Open |
| T4: review and delivery | Independent evidence review, current-base checks, bounded implementation implications and final PR scope | Pending |

A **local reconstruction closure** requires a proved scope entry/state source, actual selected control-flow
ordering, draw/restore behavior and a finite comparison witness. Supplied state may verify arithmetic but
does not establish the runtime state source or thread.

An **external-dependency closure** requires positive evidence that the selected decision inherits a named
external state/order input. Report the precise missing server input and the limits of the traced envelope.
This can answer the architectural question without proving client/server outcome equality. Seeing no seed
write in a few bodies is insufficient to prove dependence on the entire thread's history.

An **unresolved result** remains unresolved: an untyped callback, unknown caller or unobserved state is not
converted into either closure above. K1 retains all other witnesses and obligations after a local result.

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
