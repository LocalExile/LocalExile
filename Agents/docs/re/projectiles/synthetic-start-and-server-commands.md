# Synthetic projectile startup from server commands (3.29.3b)

> Frozen detailed reference from R81. Historical next-step language is provenance, not an active backlog.
> Current scope and unresolved obligations are in [the delivery matrix](delivery-matrix.md) and
> [verification and limitations](verification-and-limitations.md).

Part of [RE 012](../012-projectile-execution.md), [release lifecycle](release-and-repeat.md#21-temporary-synthetic-contact-context)
and [results/protocol](results-and-protocol.md).
The existing [instant-skill protocol](../../protocol/11-skills.md) already identifies `0x1ce`.
This chapter connects its concrete execution path to the previously unbound action `+0x268` wrapper.
It does not introduce a new message or establish the official server's choice of route for every skill.
Target SHA256: `78a0e227189c263658a7f65a02f086428b859bfc114ae8e7ce8aab721ebd48d5`.
All evidence is original-file static data or offline interpretation, without client execution.

## 1. Two concrete message callers

Actor dispatcher `0x1421940a0` reads message ID from message `+8`.
Its original byte selector at `0x1421957bc`, followed by the RVA table at `0x142195778`, gives:

| Message | Selector | Branch | Call to common executor |
|---|---:|---|---|
| `0x1ce` | 8 | `0x142194a01` | `0x142194a46 → 0x142195880` |
| `0x1cf` | 9 | `0x142194b62` | `0x142194cdd → 0x142195880` |

Both calls use the concrete Actor as RCX, skill ID as DX, transported sequence as R8W and target ID as R9D.
The later arguments are pointers to aim and optional extra aim, a copied optional configuration,
flags and a nullable animation-event pointer. Their message-relative fields differ:

| Executor input | `0x1ce` field | `0x1cf` field |
|---|---|---|
| Skill / sequence | `+20 / +22` | `+2a / +2c` |
| Target object ID | `+24` | `+30` |
| Aim / extra aim | `+28 / +30` | `+34 / +3c` |
| Flags / optional configuration | `+38 / +40` | `+50 / +58` |
| Event pointer | null | local copy of selected animation event |

The `0x1cf` branch resolves the owner's animation state through `0x141db3870`.
For a nonzero transported event hash, that helper selects events with DWORD `+8` zero or in `[16,26]`,
then matches WORD `+38` through the linked records. Hash zero instead selects a default record.
The caller advances eligible matching links while message float `+24` is **later than** event time `+0c`.
At `0x142194b9a..0x142194ba4`, it loads message time, compares it with event time using `COMISS`,
and selects the current event on `JBE`: equality keeps the current event.
An unordered comparison also takes that branch; this is an instruction fact, not a claim of legitimate NaN traffic.
The earlier prose reversed the two operands; the original instructions and separate comparison controls correct it.
Copy constructor `0x1412e1160` copies the selected event into a local event;
the caller replaces its vector at `+48..+53` with message `+44..+4f` before passing its address to the executor.
Failure to locate the required event takes the logging/exit path; it is not an unconditional startup.
The complete animation-resource traversal and the server's scheduling of this message remain separate.

Reader `0x141fde150` confirms `0x1ce` skill/sequence/flags as network-order u16s.
Transport flag `0x20` enables a target u32 and is then removed from the stored flags;
`0x2000` enables two compact extra-aim integers, and `0x10` enables optional configuration and is then removed.
The final aim is a separate pair of compact integers.
Consequently executor flag fields are **post-reader** fields; treating the raw wire flags as unchanged is incorrect.

### 1.1 Event-bearing packet factory and successful payload decoding

Factory `0x1421c6150` receives packet ID in R8W, output pointer in RDX and stream in R9.
Its selector/RVA tables at `0x1421c7278 / 0x1421c71ac` select `0x1421c64a9` for `0x1cf`.
That branch allocates `0xd8` bytes, writes the ID at `+8` and installs table `0x1435c20e8`:
writer `+8 → 0x141fde370`, reader `+10 → 0x141fde530`, copy `+18 → 0x141fdd6a0`.
The common factory call at `0x1421c718d` invokes the selected reader.
The factory's code ends at `0x1421c71a8`; the rest of its unwind range through `0x1421c73a0`
contains alignment/dispatch data, not additional instructions.

The reader consumes the following **payload after message selection**, without reading the message ID again.
BE denotes network byte order; destination offsets are relative to this message object.

| Order | Payload | Destination / condition |
|---|---|---|
| 1 | u32 BE, u32 BE, u16 BE | common object/action header `+10,+14,+18`, via `0x141f92000` |
| 2 | u16 BE skill, u16 BE sequence, u16 BE flags | `+2a,+2c,+50` |
| 3 | optional u32 BE target | raw flag `0x20`; store `+30`, then strip flag; absent target is explicitly zero |
| 4 | u32 BE context, u32 BE float bits, u16 BE event hash | `+20,+24,+28`; context/hash select animation through `0x141db3870` |
| 5 | three u32 BE bit patterns | event vector `+44,+48,+4c` |
| 6 | optional pair of signed compact integers | flag `0x2000`, extra aim `+3c,+40`; flag remains set |
| 7 | optional action configuration | raw flag `0x10`, storage `+58`; default then decode, strip outer flag |
| 8 | pair of signed compact integers | ground aim `+34,+38` |

The configuration's presence byte is message `+d0`, equivalently configuration `+78`.
With an absent configuration, the reader destroys a previously present value and clears that byte;
it need not erase stale scalar storage. A present configuration is freshly defaulted before decoding.
Absent extra aim does not refresh its storage in a reused message; the fresh factory initializes it to zero.
Consumers must obey flags/presence, rather than read optional storage unconditionally.
Message `+24` is decoded as four bits-preserving network bytes and consumed as a float by `MOVSS`.
This closes byte-to-field transfer, not the official server's event timing or chosen field values.

The signed compact reader `0x141fa3d60` is a prefix-length format, not LEB128 or zigzag:
prefixes `00..7f / 80..bf / c0..df / e0..ef / f0..ff` select 1/2/3/4/5 total bytes.
The first four forms sign-extend 7/14/21/28 payload bits; the final form reads raw BE32 after the prefix.
The unsigned count reader `0x14089cad0` uses the same length classes without sign extension.
Signed writer `0x141fa3c50` chooses half-open ranges `[-64,64)`, `[-8192,8192)`,
`[-1048576,1048576)` and `[-67108864,67108864)`, then uses `f0` plus raw BE32 outside them.
Its four-byte choice is narrower than the decoder's representable range; preserve that asymmetry.
Decoding noncanonical prefixes in controls does not establish their validity in official traffic.

## 2. The executor creates an action before synthetic startup

In `0x142195880`, flags bit 7 selects the creation family, independently of bit 8:

- Bit 7 clear: call `0x141ee97f0(Actor+598, ..., skillId, &Actor+1c8)`.
  This is the same manager/factory family used by ordinary local/S2C action creation.
- Bit 7 set: find the skill in Actor `+6f0..+6f8`, then call `0x141ee9950`.
  Its adapter `0x141ee9ad0` reaches the same `0x141941c90` action factory and initializes the action.
  This is not a copy of a currently flying projectile. The returned action's flags include bit 7.

Either creation can fail; successful factory lookup does not bypass later action predicates.
The executor installs the transported sequence at action `+11a`, flags at `+122` and aim at `+130`.
For a nonzero target ID it resolves/binds the target through `0x14193e0a0`.
If binding succeeds, target Positioned `+294` replaces the ground aim; a matching owner ID reuses the owner entity.
If flag `0x2000` is set, the extra aim is copied into action `+138`.
Optional configuration can invoke action `+80` and may depend on the existing Actor action.

Before startup, the action must pass virtual `+130` and `+270` predicates.
It must additionally have byte `+154` set or pass Actor `+198(skill, action.slot340())`.
The existing protocol chapter describes that latter route's skill-use/cooldown role;
this chapter does not reconstruct its complete cost/charge semantics.
The checked executor has no direct raw World `+984` mode or Actor `+1c8` echo-suppression test.
It forwards `&Actor+1c8` to creation; downstream mode-dependent eligibility remains possible.

On the successful path:

```text
create and configure action
  → 14193daf0(action, derived=1)
      writes 11e/11c; appends to the owner's retained-action vector
  → action.slot268(nullable event)
      sets 1e8=1
      → concrete action.slotF8()    skill startup
      → concrete action.slot140()   forced contact event
      clears source 1e8=0 on ordinary return
  → separate Actor notifications/finalization
  → Actor.slotF8(action)            retained-action removal
  → action destruction
```

This is a typed producer for the synthetic-contact context.
It bypasses the ordinary queued-root animation route; it is not evidence that every ordinary projectile
cast should use `0x1ce`, or that this packet individually creates each flying projectile entity.
The concrete skill callbacks still decide whether and what to emit.

## 3. Equal virtual offsets have different receivers

The executor contains **two** calls using offset `+268`:

| Site | Actual receiver | Meaning |
|---|---|---|
| `0x142195eb9` | newly created skill action | synthetic startup `0x14193b8a0` for the five checked final projectile classes |
| `0x142195ecd` | Actor, only on bit-7 path | separate triggered-action finalization |

Concrete client Actor table `0x14346e558` binds Actor `+268` to tail thunk `0x1416666a0`.
It forwards `(actor,action,1,0)` to Actor `+1e8`; it does not call the action startup wrapper again.
The non-triggered path instead performs a notification and calls Actor `+260`, forwarding flags bit 8.
Client `+260 → 0x1416666b0` first calls Actor `+1e8` with `(actor,action,0,bit8)` and has additional presentation work.
The complete downstream finalization/listener behavior is not declared recovered here.

If action flags lack bit `0x10`, the executor sets it and invokes a registration helper followed by action `+250`.
It then calls Actor `+f8 → 0x141e930e0` and destroys this temporary action.
That removal uses sequence/derived identity and removes the selected retained pointer;
it does not directly clear `1e8` in separately copied children.
Child ownership and arbitrary nested cleanup must not be inferred from source destruction alone.

## 4. Random and identity consequences

The transported u16 sequence is an actual action input, including `0xffff`; there is no local replacement with zero here.
Successful registration supplies **derived byte 1**, not an allocator draw at this call.
These inputs can affect the existing object hashes and action-dependent random recipes.
The source's `1e8` is one during both concrete start and event dispatch.
As established in the release chapter, common start then skips its ordinary `448=0` source-ordinal reset,
while selected preparation still runs. The wrapper does not initialize or increment `448` itself.
The four attack-class concrete starts still execute [hand selection](hand-selection.md); synthetic state skips
the model override, then the two-eligible-hand Barrage selector can read the prior ordinal.

Do not substitute ordinary-root ordinal zero for this route.
The actual constructor/configuration/callback path must establish its value before each emission.
Base copy preserves `1e8`, so any child copied during contact can retain one after the source returns to zero.
This round does not establish every later child clear, every eligible skill or all other synthetic callers.
It also does not establish a per-projectile C2S hit/terrain/continuation report.
The observed static contract is **S2C action instruction → client skill execution**;
authoritative result ingress and damage remain distinct contracts.

## 5. Concrete configuration and admission boundaries

### 5.1 A transported speed-context producer

The formerly unbound [speed context](motion-speed-and-time.md#11-shared-integer-modifier-selected-views-and-rounding)
has concrete producers on both routes:

```text
wire flags 0x10: optional configuration exists
  → 0x1ce reader 141fde150 / 0x1cf reader 141fde530 initializes defaults, then calls 1419e54d0
      configuration mask byte bit 4: read network-order u32 into configuration+1c
  → 141fddf90 copies the present configuration into the dispatcher's local value
  → 142195880, call 142195baf: action.slot80(local configuration)
  → final receiver 141bae280 calls base 14193f320
      then unconditionally copies configuration+1c into action+434
  → shared speed modifier 141bb35f0 consumes it as signed32 percent
```

The outer packet flag and inner configuration-mask bit happen both to be `0x10`;
they have different scopes. The outer flag is removed by the packet reader.
The configuration is still present at `+78`, independently of that removed flag.
The inner bit controls the four-byte value, not whether the configuration callback occurs.
With a present, freshly defaulted configuration whose inner bit 4 is clear, the callback writes zero.
With no configuration, this executor skips `+80`; it does not explicitly zero an existing action field there.
Constructor zero remains a separate fact about newly created actions.

Reader `0x1419e54d0` starts with one mask byte and processes these fields in increasing bit order:

| Mask | Action-configuration destination | Read behavior |
|---|---|---|
| `0x01` | byte `+48` | store mask bit as boolean; no following payload |
| `0x02` | byte `+2c` | store mask bit as boolean; no following payload |
| `0x04` | DWORD `+08` | network-order u32 |
| `0x08` | DWORD `+18` | network-order u32 |
| `0x10` | DWORD `+1c` | network-order u32, later consumed as signed speed percentage |
| `0x20` | DWORD `+20`, byte `+24` | network-order u32 and set presence byte to one |
| `0x40` | DWORD `+28` | network-order u32 |
| `0x80` | vector at `+30` | unsigned compact count `14089cad0`, then count times four raw bytes; no per-element byte swap |

The scalar u32 conversion calls use the same PE-import cell for `ntohl`.
Copy/move helper `0x141fe2150` and optional-copy helper `0x141fddf90` both preserve `+1c`.
The latter checks source presence and writes destination presence; the absent branch does not copy scalar data.
The dispatcher passes the address of that local optional value to the executor, not a pointer to an arbitrary owner context.

All five final tables for the six witnesses bind `+80` to `0x141bae280` and `+88` to `0x14193f410`.
The executor's conditional `+88(existing action)` call shares reference-counted storage at `+d8/+e0`;
its checked body does not copy the projectile source ordinal `+448` or synthetic byte `+1e8`.
This bounded body fact is not a complete constructor-to-emission write inventory.

The startup global can suppress the base helper's stat-overlay writes; it does **not** suppress
the outer assignment to `+434`. The value enters the previously recovered factor
`f32(1 + f32(float(signed32(value)) / 100))`, before the first native rounding.
For example, transported 100 supplies factor 2 and transported `0xffffffff` supplies signed -1.
This is a speed input, not an RNG seed or authoritative damage value.
Both packet readers, their configuration presence/copy and the shared final receiver are now linked.
The configuration vector's element semantics remain separate from its raw byte transfer.
Other callers/producers and actual official-server field values remain open.

### 5.2 Actual final-class gates do not guarantee emission

All five final tables bind `+270` to `0x140125860`, the exact three-byte `mov al,1; ret` leaf.
That particular executor check always succeeds for these bindings.
The preceding `+130` is different: bow/wand, KB, TS and BA bind `0x14193c9e0`;
Fireball binds `0x1419eeee0`, which can reject on its skill-class test and selected stat `0x28ea`
before tail-calling the shared predicate.

The shared predicate begins with `+128 → 0x14193c990`: every callback in the `+250..+258`
16-byte-record vector must return nonzero; an empty vector passes.
It then checks skill/flag/stat and owner/target-distance conditions before calling
`0x14193aa30` and `0x14193ab70(action,1)`, followed by further owner/skill checks.
The executor has already written byte `+11f=1`, so `0x14193aa30` takes its immediate success branch here;
the other resource/stat eligibility helper is not thereby bypassed.
In particular, its selected inputs, nested predicates and actual actor state are not reconstructed as
an unconditional pass or a new server cost model in this chapter.
The final Actor `+198` check remains separate unless configuration sets byte `+154`.

Initialization `+8 → 0x14193a700` uses byte `+124` to enter `0x14193bae0` once;
that scoped helper has the independently documented startup overlay gate.
The [concrete initialization contract](action-initialization.md) establishes that `124` is set before
that gate, retained insertion precedes initialization, and copied projectiles reset `124` while retaining `1e8/448`.
The checked gate-nonzero registration path supplies no implicit ordinal reset or cloned-flag clear;
the gate-zero path invokes concrete inherited/Fire Wall/Kinetic Wall contribution adapters.
The concrete starts still enter the previously mapped common setup through their own callbacks.
These additional bindings replace some opaque premises from the ingress investigation,
but do not close the actual pre-emission `+448`, all cloned-`1e8` cleanup or successful release for every input.

## 6. Verification and limits

Round28 `audit_synthetic_start.py/json` preserves code portions of seventeen complete bodies/thunks,
4254 instructions, 48 semantic anchors, fourteen final virtual bindings and both original dispatch-table entries.
The dispatcher's embedded table bytes are retained separately as data rather than counted as instructions.
The initial slot-offset candidate index supplied eleven candidates; it was only a discovery aid.
The typed action/Actor chains above, not the equal offset, establish the receiver distinctions.
Other candidate receivers and unresolved indirect call forms are not exhausted.

`check_ingress_paths.py` interprets the original executor and wrapper on 576 synthetic successful paths,
146208 instruction steps and 325 covered instructions. It varies bit 7/8/13, sequence, target/self/ground,
supplied/absent event, skill-use gate bypass and prior source ordinal.
Factories, target binding, eligibility predicates, retained registration, strings and gameplay callbacks
are explicit opaque-callee fixtures. This verifies local field transfer, route selection and callback order;
it does not execute or validate those mocked subcontracts or produce a gameplay projectile.
Forced contact type/hash and retained source ordinal are checked before return.
Separate fresh decoding and literal controls include a rejected skill-use gate.
The controls share this integer interpreter; they are not independent native execution or an official capture.

Ordinary-root clip admission, cloned-flag lifetime, actual-thread TLS/world ordering, remote/monster resources,
and the official Fireball area-result route remain open in their respective chapters.

Round29 `configuration-audit.json` retains thirteen original bodies/leaves, 641 instructions,
31 anchors and forty final bindings. `configuration-paths.json` interprets the reader, optional copy
and base/projectile receiver across all 256 mask bytes, seven scalar bit patterns, both overlay-global states
and present/absent copies: 7168 cases, 881664 interpreted steps and 204 covered instructions.
Fresh direct PE/Capstone review checks all seventeen ranges, five extra ingress controls and the `ntohl` import;
five literal wire controls separately check ordering, sign-bit preservation and the boolean input.
Those controls share the disclosed interpreter, not an independent native execution engine.
Empty string/vector lifetimes, optional-vector count and byte swapping are
explicit library fixtures; this is neither a complete packet/executor run nor a gameplay-emission proof.
The integer machinery is reused from round28 without rerunning or modifying that round's evidence.

Round30 `audit_round30.py/json` retains 31 functions in 35 code ranges, 4571 instructions,
36 anchors and fourteen bindings, with factory dispatch data kept separate.
`check_packet_paths.py` interprets the original factory/reader/config transfer/receiver and native compact codecs:
4300 cases, 1305404 steps and 836 covered instructions.
These include reused-message presence/default controls, nonempty raw vectors with counts through 512,
all 256 initial compact bytes with four tails for both readers, and signed writer/reader boundary round trips.
Allocation success, stream copying, vector storage, empty container lifetimes and byte-swap imports are explicit fixtures.
They do not model failed I/O, allocation limits, gameplay admission or official traffic.
Fresh direct PE/Capstone review rechecks the code/ranges/bindings, factory tables/imports and six handler anchors.
Eight independent COMISS/JBE controls establish the corrected event-time comparison.
Two hand-written literal packets additionally exercise the disclosed interpreter; they are not native execution.
