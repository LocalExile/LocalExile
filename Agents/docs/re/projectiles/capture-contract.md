# Projectile execution capture contract (3.29.3b)

> Frozen detailed reference from R81. Historical next-step language is provenance, not an active backlog.
> Current scope and unresolved obligations are in [the delivery matrix](delivery-matrix.md) and
> [verification and limitations](verification-and-limitations.md).

Part of [RE 012](../012-projectile-execution.md).
This is a research instrument, not a server implementation or a completed client parity test.
The current static reconstruction leaves actual call/result-arrival order and some RNG input lifecycles open.
Closed static data flow already establishes the documented rules; recording is useful for actual runtime inputs,
ordering and parity comparisons through the existing [Frida tooling](../tooling.md#4-frida-through-rea-live-client).

## 1. Scope and implementation plan

1. Add a self-contained probe to the existing `scripts/re/frida/run.py` JSONL workflow.
   Record action scopes, identity snapshots, initializer inputs/output state, bounded draws, object hashes,
   binding, contacts, terminal calls, result arrivals, exact lookups and stage changes.
2. Check x64 PE timestamp/image size and every hook prefix before installing any hook.
   These checks identify a compatible hook profile, not a full runtime executable digest.
3. Use QPC ticks, thread IDs, monotonic event numbers and invocation IDs.
   Keep nested scopes per thread; do not infer game scheduling order from Python log timestamps.
4. Bound capture volume and retain explicit loss/error indicators.
   Capture only the named fields, without raw packet buffers or login data.
5. Add an offline analyzer and tests for RNG mismatches, reused state pointers, duplicate result keys,
   malformed/truncated logs, nested calls and appended capture sessions.
6. Run offline checks only in this research batch.
   A user-run client recording remains necessary before asserting runtime agreement.

The probe reads game memory and installs Frida observation hooks when explicitly run by an operator.
It does not call game functions, change gameplay fields, launch a client or implement damage.
OS QPC calls supply the clock.
Hook overhead can change timing; a successful recording is not a timing-neutral measurement.

## 2. Record interpretation

RNG state means `[transitionCount, s0, s1, s2, s3]`, not five seed words.
Initializer records retain four/five/seven/eight input words and the state after warmup.
Bounded calls retain wrapper width/mask, before/after state and returned sample.
Only the checked 32-bit wrapper is compared to the arithmetic references.
An observed state change between records is an untraced consumer or context reuse until explained.
The analyzer may compare one bounded call independently from its captured before-state;
that does not prove all intervening random consumers were captured.

Each action snapshot contains its address, installed vtable, owner ID, sequence, derived byte and stage.
Projectile snapshots also retain release/source ordinals, continuation counters, return-phase flags,
current grid and raw height marker/world Z when an associated flying entity exists.
Repeat fields retain both remaining and total values; speed cache is recorded as raw uint32 bits.
Contact/terminal scopes include the supplied target; terminal retains its natural-end flag.
The corrected profile declares `observations.contactTargetLayout = 1`:
contact `0x141ba7b30` receives a Positioned pointer and resolves `Positioned+8 → Entity+0x88:u32`,
whereas terminal `0x141ba68c0` receives an Entity pointer directly and reads `Entity+0x88:u32`.
Null targets record zero. These are different native parameter layouts, not interchangeable pointers.
Older profiles without this marker read contact target IDs from the wrong structure;
do not use those contact IDs to correlate result keys. Their terminal target reads are unaffected.
The raw height marker can be the sentinel; the probe does not invoke the native effective-height getter.
Pointer addresses are observation-local identities and may be reused.
Result keys are `(owner, sequence, derived, stage, target)`.
Incoming kind-bit-zero records and exact lookup successes are correlated as observations;
the probe does not cover every queue consumer, so it cannot reconstruct the entire pending queue.
An earlier matching arrival is a candidate source, not proof of which duplicate was consumed.
Neither a contact nor a lookup proves damage.

The probe does not yet capture every integer motion sample, world-index mutation, TLS draw,
numeric stat getter, C2S writer or rendered object.
Use the existing movement/projectile-create probes where needed; their different record formats are not silently merged.
Per-projectile reporting remains unresolved after bounded static searches.

### 2.1 Skill-stat input and selected-view observations

The profile also observes Actor message dispatch `0x1421940a0` for decoded kind `0x1b4`.
`skill_stat_update` retains owner, full u16 skill ID, u8 stat-set selector and decoded unsigned-key/signed-value pairs.
It reads at most 256 pairs per message and explicitly marks a longer vector as truncated.
The analyzer treats that truncation as an incomplete recording, not as a complete update.
These are decoded input observations; a skill missing from the Actor can legitimately produce no setter calls.
`skill_stat_end` marks return from that dispatch; an unreturned observed dispatch makes the recording incomplete.

`skill_stat_apply` records return from raw setter `0x1424cfaf0` only for the checked pair-loop return address
`0x142194652`, correlated to the enclosing dispatch invocation on the same thread.
It retains the actual raw stat-object pointer and pair; it does not call a numeric getter to verify the result.
Nested writes from other callers are outside this observation.

`stat_view` observes shared action selector `0x14193b350` only inside the six projectile scopes and
the final-class-filtered root-start scope described below.
It retains action identity, supplied selector, skill ID/pointer, primary raw/effective pointers and returned selected pointer.
These reads do not request a stat evaluation, install an overlay or refresh a projectile cache.
They therefore supplement, rather than replace, the static [lifetime reconstruction](actions-and-emission.md#61-selected-stat-views-and-cached-values-have-different-lifetimes).

The analyzer validates pair bounds and correlation and counts later views of an earlier updated pointer as candidates.
It resets that history at each capture start.
Pointer reuse within a session, an intervening replacement or a wrapper around the raw object can invalidate an inference.
A pointer match does not prove a particular value was read; a mismatch does not prove the update failed to propagate.
The selected getter remains a hot path even with scoped logging; the additional hooks can increase capture overhead.
The current 35-site profile has only been checked against the pinned on-disk executable and exercised offline.
No real-client recording or native stat-propagation comparison has been made with it.

### 2.2 Main animation requests and event candidates

The [animation reconstruction](animation-and-release.md) supplies five additional observation sites:
action start `0x14193d870`, normal/alternate requests `0x141e9a3c0/0x141e9bb80`, and
Actor listener entries `0x141e938c0/0x141e93e90`.
All five final vtables for the six witnesses are whitelisted before taking projectile-layout snapshots;
unknown action classes are not treated as having the large projectile layout.
Root-start scopes join the same per-thread nested invocation stack as the six existing projectile scopes.
Entering root start can still precede a native rejection.

`animation_request` requires an enclosing witnessed root start, matching Actor owner ID and one of the six
confirmed main-builder return addresses listed in the animation chapter.
It retains request `+8:u32`, Actor `+0x21c:u16`, route and caller, plus action identity and outer repeats
re-read at request time rather than copied from the earlier root-entry snapshot.
Attached helper requests and delayed/repeated return address `0x141ba48ec` are excluded from this observation.
The analyzer checks the enclosing invocation and `requestSeed == u32(sequence + outerRemaining)`.
A zero request seed is a fallback candidate; the actual selector/clip is not observed.
Wrapped u32 addition is intentional, rather than a widened mathematical seed.

`animation_event` passively records release-hash `0x9b8d` candidates at either Actor listener entry,
with event DWORD `+8`, descriptor DWORD `+0x4c`, Actor byte `+0x218` and the current action's basic identity.
The actual Windows arguments are listener/controller/descriptor/event, with event in R9.
Normal/alternate labels refer to descriptor/resource routes, not network modes.
These records precede the native resource getter and forwarding gates, so the analyzer counts candidates only.
It does not report confirmed releases, even if the observed hash is correct.
The probe never calls action `+0x68`, a selector or a resource resolver;
those functions can have side effects including reference-count and control-field writes.

Root start and Actor/animation hooks add observation overhead even when many calls do not produce records.
The 20,000-event cap, error counters, clean-end requirement and existing incompleteness diagnostics still apply.
The profile has no runtime recording: syntax, on-disk prefixes and synthetic analyzer cases are its current validation.

### 2.3 Result control, exact matching and actual processing attempts

Capture start declares `observations.resultIngress = 1` for the additional result observations.
The analyzer reports covered and uncovered sessions separately.
Legacy logs without that marker remain readable; no recorded `0x1b0` events in a legacy profile means no coverage,
not an observation that the client received none.

Three additional sites are `0x141e90c30` (decoded `0x1b0` handler), `0x141e90cc8` (its first full-key match)
and `0x141e91580` (Actor ProcessResult).
Result invocations have their own per-thread stack, independent of the projectile/RNG scopes.
The handler reads an eight-byte decoded handle: sequence u16, derived/stage u8 and target u32.
`result_control_begin` retains its key, Actor pointer, invocation, caller and raw process byte.
Here "raw" means the handler argument. The actual S2C reader `0x141fddc20` has already converted the wire
byte to `byte != 0`; this observation cannot distinguish different nonzero wire values.
Only caller `0x1421952bb` is labelled the checked S2C dispatcher; other callers remain unknown.
`result_control_match` observes RSI=Actor, R14=handle and RBX=matched record at the verified interior instruction.
`result_control_end.matched` means that invocation reached the match point, **not** a handler return-register bool.
The handler has no unified business bool in AL.
Matched process-zero requests remove only; missing requests do not call ProcessResult even if process is nonzero.
Both are normal observations.

Shared result snapshots retain `+0x34:u16 auxiliary34`, `+0x36:u16 flags`, `+0x39:u8 code`, the full key
at `+0x40..+0x47` and kind bytes at `+0x48/+0x49`.
The auxiliary/code fields stay raw; they are not named damage.
These snapshots accompany `0x1af` arrival, control match and processing entry/exit.
Exit copies occur before the caller frees the record; native changes to auxiliary/flags are legitimate.

`result_process_begin` retains the input action pointer/basic identity and two pointers in the supplied stats handle.
It does not assume an unknown action has the larger projectile layout or call a getter/reference-count operation.
`inputActionMissing` describes R9 on entry; the native body may look up an action again internally.
`result_process_end.completed` is the native AL bool, not hit success, damage, a visible effect or queue-match success.
Exact consume can return found=true while ProcessResult returns false; these outcomes remain independent.

| ProcessResult return address | Analyzer source |
|---|---|
| `0x141e9109e` | `1af_immediate` |
| `0x141e90e04 / 0x141e90e22` | `1b0_retained_found / 1b0_retained_missing` |
| `0x141e90ec6 / 0x141e90ee8` | `1b0_root_found / 1b0_root_missing` |
| `0x14193a106` | `exact_consume` |
| `0x141e914b5` | `batch` |
| other | `unknown`, with raw caller preserved |

The analyzer checks nested invocation IDs, match keys/widths, control-processing correlation and clean returns.
Matching previous arrivals remain candidates even after another observed consumer; this is not a queue reconstruction.
This coverage can distinguish immediate, queued-control, queued-exact and batch processing in a future recording.
It does not establish which route official Fireball uses for additional area targets.
No client attach or capture was performed while developing this extension.

### 2.4 Scoped TLS chance and actual bit-helper state

Coverage marker `observations.tlsRandom = 1` adds three checked sites:

| Site | Observation |
|---|---|
| `0x140e3b450` | chance helper entry/return, raw unsigned probability argument, caller and result |
| `0x140e3b310` | bit helper entry/return, requested width, caller and enclosing chance invocation when present |
| `0x140e3b34f` | each pre-LCG step, with native-selected state pointer, actual before-state and chunk width |

All are logged only inside an existing projectile/root-start scope.
At the aligned step site RCX is the TLS base, RBX is checked as `0x170`, and R9D supplies the chunk width.
The probe reads that native-selected pointer; it does not call a game function, read an assumed global seed,
or write TLS. At bit-helper return it reads the actual final state while the synchronous invocation is still active.
A zero-bit call records no step and a null final-state field.

`tls_chance_begin/end`, `tls_bits_begin/end` and `tls_step` have per-thread nested invocation IDs separate from
the ordinary scope and result stacks. The analyzer checks requested widths 0 through 32, 15-bit chunks,
LCG transitions, concatenated return value, final state and the chance helper's rejection loop over 100..127.
It also checks zero/at-least-100 no-draw boundaries using the helper's **unsigned** argument semantics.
Callers' signed guards remain separate rules; a raw negative bit pattern reaching this helper is not treated as zero.
Missing steps/returns, scope lifetime violations, mismatched parents, unknown coverage versions or TLS events without
the marker make the recording incomplete. Older logs with no TLS events/marker remain readable.

The captured state can anchor a later comparison at this decision even when earlier TLS history is unknown.
It does not establish all earlier consumers, all seed/restore scopes, unscoped draws or any official server policy.
Hooks on this frequently called helper also add overhead outside logged scopes; timing is not neutral.
Offline tests execute the actual callback-registration block with synthetic pointers, including rejection and zero-bit
cases, and separately test analyzer corruption/truncation/coverage checks. They never attach or execute game code.

### 2.5 Context receiver, actual assignment and caller discovery

The [speed-context receiver](motion-speed-and-time.md#11-shared-integer-modifier-selected-views-and-rounding)
has a closed static assignment rule but no trusted producer/caller yet.
Coverage marker `observations.contextReceiver = 1` adds two checked sites:

| Site | Observed fields |
|---|---|
| `0x141bae280`, final action `+0x80` receiver | Basic action key/pointer/final table, borrowed input pointer label, signed input `+0x1c`, previous action `+0x434`, caller |
| `0x141bae29d`, aligned store instruction | Actual CPU EAX low32 as signed value and RDI action pointer, before the native store |

`context_begin → context_assign → context_end` uses an independent per-thread LIFO invocation stack.
It does not require an enclosing root-start or projectile scope: this callback may precede either.
The five final witness classes are checked before reading action-specific fields at both observation sites;
unknown classes nested inside a watched receiver do not consume the outer invocation's assignment.
This is a receiver-specific whitelist, not a generic context-structure detector.

The begin record retains `call/tid/parentCall`, `action/vtable/key`, `context/input1c/before434`,
and `callerRaw/caller`. `callerRaw` is the actual return address; `caller` is normalized to the preferred image
only when that address lies inside the loaded main module, otherwise null.
The normalized caller histogram can anchor a later typed producer investigation.
Pointer labels are observation-local; they do not identify a protocol field or establish object lifetime beyond the call.

The borrowed input is read synchronously at entry and is never dereferenced again by this observer.
The base helper runs before the native input load, so the entry snapshot must not be assumed to equal the later value.
The internal site records **actual EAX**, after that load and after RBX has been restored; it never treats RBX or RDX
at that site as the input pointer. Zero is assigned unconditionally too.
The end callback reads the retained action's `+0x434` before return, without invoking a game getter.

The analyzer requires exactly one assignment for each observed return, correct nesting/thread/action pairing,
valid signed32 widths, known final tables and supported coverage version.
Missing/duplicate/late assignments, unfinished invocations, probe loss or unknown versions make the recording incomplete.
A changed entry input is counted separately and is not an arithmetic failure.
A returned field differing from the actual observed store produces an inconsistency diagnostic;
it does not by itself disprove the static rule or identify which concurrent/untraced mutation occurred.
No receiver events in a covered interval means only that no matching call was observed in that interval.
Legacy recordings without the marker report uncovered sessions, rather than claiming an observed absence of producers.

Round21 `context-observer-audit.json` checks all 32 prefixes, the full 41-byte receiver, its EAX/RDI premises
and all five final `+0x80` bindings against original PE bytes.
Offline tests execute the real callback block with changed/unavailable borrowed input, zero/negative assignments,
unknown nested classes and ASLR caller bounds, and separately corrupt analyzer coverage/pairing/width records.
No hook has been installed in a client in this research batch.

### 2.6 Inline Tornado Shot wind randomness

`observations.windRandom = 1` covers the separate inline LCG in the final client's wind constructor.
The shared child generator hook returns before this wind call, and helper-only TLS hooks cannot observe
its inlined instructions. Three additional sites close that observation gap:

| Site | Observation |
|---|---|
| `0x1414fc340` entry/return | Final TS table `0x143423898`, action/key, packed signed XY, signed height, raw/normalized caller and up to 12 normalized/null backtrace frames |
| `0x1414fc4ae` before `imul` | RSI action, native-selected RCX TLS base plus RDX `0x170`, actual uint32 state before the draw |
| `0x1414fc4bd` after the store, before `shr` | Same action/state pointer, CPU EAX low32 and actual stored uint32 state, read independently |

The emitted sequence is `scope_enter`, `wind_begin`, `wind_before`, `wind_after`, `wind_end`, `scope_exit`.
Exactly one inline transition occurs on the checked normal returning path:
`after = u32(before * 0x41c64e6d + 0x3039)`. CPU and memory observations are both checked against it.
Independent per-thread invocation stacks distinguish same-action reentry. Unknown final classes are filtered
before projectile-field reads and shield the observed parent's inline and generic RNG scopes while nested.
The wind scope also makes existing resource initializer/helper observations available during construction;
it does not turn that resource RNG into the wind's TLS rotation or the child's geometry context.

Caller returns `0x1414fbdec` and `0x1414fc32e` identify the checked terminal and scheduled wind call sites.
Other callers remain unknown; an external frame is null in the normalized list, not a guessed game address.
Backtraces are bounded discovery hints, not complete stacks or proof of a seed/save/restore envelope.
The analyzer requires coverage, signed widths, the exact class, active scopes and ordered paired phases;
missing, duplicated, changed-pointer/action, late or unfinished records are incomplete.
Legacy sessions without the marker remain readable and report uncovered wind observation.
No continuity between separate wind calls is assumed: outside draws, thread history and reseeding remain open.
Neither final Positioned orientation, child count/angles, damage nor official reconciliation is observed here.

Round74 `wind-observer-audit.json` rechecks all 35 aligned prefixes, three full unwind-owned bodies,
two final TS slots and both call returns against the original image. Its 1,028 finite controls interpret only
the five straight-line LCG/sample instructions, not the complete wind/resource construction or float setter.
Offline callback tests exercise packed signed inputs, separate CPU/memory reads, nesting/thread isolation,
unknown-class shielding and lost/duplicate phases; analyzer tests additionally corrupt the record contract.
No client attach or live capture was performed.

## 3. Operator workflow

See [the evidence ledger](evidence-and-open-questions.md) for the original unmodified input digest.
Use only the project client copy and the normal local-development workflow.
Do not load `common.js`: this probe is self-contained.
The existing runner appends with `--log`; each probe start is a separate capture session.

```powershell
python scripts/re/frida/run.py Game.patched.exe --seconds 30 --finish-rpc finish --log logs/projectile-execution.jsonl scripts/re/frida/trace-projectile-execution.js
python scripts/re/projectiles/analyze_execution_capture.py logs/projectile-execution.jsonl
python -m unittest discover -s scripts/re/projectiles -p test_execution_capture.py
node --test scripts/re/projectiles/test_execution_probe.js
```

Read the capture-start limitations, probe errors, event-cap status and analyzer diagnostics before comparing outcomes.
A missing clean end marker is reported as an incomplete session, including a normal detach without a final status read.
When using an interactive Frida host, call the probe's `finish` RPC before detaching for an explicit end marker.
The runner's optional `--finish-rpc finish` requests that marker before its normal detach.
Do not interpret a clean prefix or offline synthetic fixture as a real-client recording.

For each of the six skill witnesses, record one projectile first, then additional projectiles/Volley,
an entity contact, a terrain stop and an applicable continuation separately.
Keep Predictive local owner, remote owner and Lockstep evidence distinct.
Preserve effective stats, source/aim coordinates, scene/terrain, supports and client build beside each log.
Runtime data and disassembly stay outside version control.

## 4. Official/local comparison obligations from external evidence

[PR 373's published capture report](external-ranged-evidence.md) adds concrete discrepancies to test,
but its private traffic is not a recording made by this probe and has no independently checked loss/digest metadata here.
Keep those reports separate from our static evidence and future operator captures.

Record the effective skill ID/record and selected stat set at action creation, then root/derived identities,
release events and native object creation for ordinary bow, wand and a selected monster action separately.
Compare local no-native-shot cases against the report's official no-server-object cases.
Preserve real sequences instead of treating `0xffff` as a universally valid action identity.
Correlate reported hit-byte value 2 with actual derived allocation and result stage, rather than assuming an index.
Include S2C skill-stat updates when reconstructing effective inputs; compact integer pairs are not inherently coordinates.
Do not infer per-projectile C2S reporting from a local callback or an incoming point-like packet.

For contact comparisons retain target IDs, base speed, speed-roll limits, type-associated seed word and current position.
For release comparisons retain class/rig, selected clip, contact transform, model scale and effective origin flags.
The present probe captures decoded `0x1b4` dispatch/pair writes and scoped selected-view pointers;
it does not automatically capture all these fields or other packet paths.
Use the existing probes/decoder or extend their bounded observation coverage before claiming a complete comparison.
Arrival slopes and roughly 33 ms steps alone do not identify launch, movement and result scheduling independently.
