# World message headers, queue publication and frame admission

> Frozen detailed reference from R81. Historical next-step language is provenance, not an active backlog.
> Current scope and unresolved obligations are in [the delivery matrix](delivery-matrix.md) and
> [verification and limitations](verification-and-limitations.md).

Part of [012 — projectile execution](../012-projectile-execution.md).
This connects network header bytes to [the ordered component checkpoints](world-scheduling.md#10-component-checkpoints-can-consume-ordered-incoming-records).
The pinned executable and evidence labels are listed in [the evidence ledger](evidence-and-open-questions.md).
The original common codec, registered pump/factory/owner route and concrete World intake are established
statically. Socket/read internals, the enclosing Instance-tick thread and official ordering choices are separate.

## 1. The two ordering fields are transmitted inputs

Common writer **`0x141f91f60`** writes 12 bytes. Common reader **`0x141f92000`** reads the ten bytes after the
message kind, which its caller has already selected. All four wire integers are big-endian:

| Wire offset from kind | Width | Parsed record offset | Bound role |
|---|---|---|---|
| 0 | u16 | `+8` | message kind |
| 2 | u32 | `+0x10` | update target / object key |
| 6 | u32 | `+0x14` | ordering object key |
| 10 | u16 | `+0x18` | ordering checkpoint |

These wire offsets must not be confused with the parsed object's offsets, which include its vtable/storage.
Writer sites `0x141f91f92/0x141f91fb2/0x141f91fd2` load the last three fields; reader sites
`0x141f92015/0x141f92036/0x141f92054` select their destinations.
The PE imports resolve the called endian helpers to `WS2_32.dll` `htonl/htons/ntohl/ntohs`.
Original reader calls from skill-start reader `0x141fdda8a` and result reader `0x141fddbb0` provide two concrete
callers; their distinct payload algorithms are reused from existing protocol/result research.

At the nonzero-mode checkpoint consumer, parsed `+0x14` is compared against World `+0x9b0` and parsed `+0x18`
against World `+0x9b4`. The generic dispatcher's separate `+0x10` selects the update target.
The ordering object can therefore differ from the updated object. The local server's use of zeros in common
headers is a source implementation choice; it does not make these fields padding or recover the official choices.
The component prefix is `uint16(componentIndex << 12)`; the low 12 bits count supported intra-component
checkpoints. This value is neither elapsed milliseconds nor a projectile identifier.

## 2. Normalize the embedded interface before reading fields

Intake **`0x1421872b0`** receives `I = World + 0x1c8` in RCX and the address of an owned parsed-message pointer
in R8. Interpreting RCX as the whole World would misidentify every positive-offset field below.
Interface table `0x14346e488` has this intake at slot zero. Shared constructor `0x142182aa0` installs that table
at World `+0x1c8` (`0x142182aea..0x142182af8`); client constructor `0x141653d40` calls the shared constructor,
installs primary table `0x14346e0c8`, and installs the same embedded table at `0x141653daf..0x141653dbd`.

| Intake-relative | World-relative | Role in this body |
|---|---|---|
| `+0x7bc` | `+0x984` | raw network mode |
| `+0x818/+0x820/+0x828` | `+0x9e0/+0x9e8/+0x9f0` | record pointer-vector begin/end/capacity end |
| `+0x830` | `+0x9f8` | record cursor |
| `+0x838` | `+0xa00` | activation/frame-admission flag |
| `+0x7f0` | `+0x9b8` | frame-length deque container; map/count at `+0x9c0/+0x9d8` |
| `-0xac` | `+0x11c` | current frame length, stored as u32 |

The positive constructor/table chain establishes the interface's ownership. The registered route below connects
it to the Instance receive pump, without selecting an execution thread or the official server's producer.

## 3. Registered pump, factory and owner handoff

The existing [instance connection notes](../../protocol/02-instance-connection.md#5-the-read-loop-pump-and-handler-groups)
identify the pump and groups. Their route now joins the common header to the concrete intake:

```text
Instance tick 0x142148880, connected-state slice at 0x142148c1e
  stream prefetch 0x140dfb3d0
  while 0x14299e440(Instance+c2b0, Instance[c288]) succeeds:
    read kind; visit registered groups until one accepts
    World group (World+a08), generic handle 0x14299e300
      World factory 0x1421c6150, group.slot10
        construct selected parsed message
        message.slot10 reads its body, within the factory
      if parsed pointer nonnull and stream failure flag is clear:
        owner = pointer_at(World+a10) = World+1c8
        owner.slot0 = 0x1421872b0
          move parsed pointer into World's incoming vector
```

Distinguish **group object `World+0xa08`** from **owner interface `World+0x1c8`**. The shared constructor installs
group table `0x1435da300` at `+0xa08`. Leaf `0x142188cc0`, directly called by the Instance constructor, stores
`World+0x1c8` at the group's `+8`, then tail-calls registration `0x14299e3b0` with that group and the Instance pump.
The Instance constructor first registers its own group `Instance+0xc2a0`, owner `Instance+8`, then the World group.
Registration inserts at the front, so the resulting order is **World group, Instance group**.
The concrete Instance pump's kind-width byte is initialized to zero: it reads a **u16 BE** kind.
The generic pump also supports u8 mode; that is not the concrete Instance setting.

The pump invokes each group's `+8` until the first true result. The generic group invokes its `+0x10` factory
with kind and stream. An unrecognized kind leaves the output pointer null and returns false, allowing another
group to try. The factory itself constructs and reads the message through its installed `+0x10` reader;
there is no second body-read call in the generic group handler.

For a concrete result witness, factory's original two-level switch maps `0x1af` through byte index 7 to
`0x1421c6509`. It constructs an `0xa8`-byte object, installs table `0x1435ddb38`, then calls message reader
`0x141fddba0`, which calls the previously recovered common header `0x141f92000` before its result payload.
The original round44 controls fixture the selected result payload. The later
[result-decoder composition](results-and-protocol.md#31-actual-payload-decoding-fresh-defaults-and-dispatch)
executes the actual payload and handle readers, constructor defaults and both result/control message factories.
Its Actor dispatch is a separately supplied entry, rather than a full World-to-Actor replay.

Ownership and failure are separate from type recognition:

- A nonnull factory result means the group accepts that kind. If stream byte `+0x252` is set, the group destroys
  the parsed object and does **not** invoke the owner; it still returns true to stop other groups from retrying it.
- On the clear-flag path, the group moves the parsed pointer from its local cell to the owner's call argument
  and calls owner slot zero. The concrete World intake takes ownership as described below.
- Kind zero bypasses the factory and calls the owner with a null message; World intake returns without enqueueing.
  This is a supported control path, not an attribution of a particular official packet purpose.

The pump checks `stream+0x252` after reading kind and again after an accepting group. Either failure branch clears
that flag and pending-consumed count `+0x278`, then returns false. Its successful branch updates last nonzero kind
at stream `+0x1a0`, advances the ring offset `+0x268` modulo buffer length, subtracts pending consumption from
`+0x270`, clears `+0x278` and returns true. Optional stream flush/side-buffer callbacks and error diagnostics
have distinct branches; they are not reconstructed by the finite success/failure controls.
The Instance loop calls the pump again on true, stops on false, and is skipped when its state is at most one.

This binds receipt-to-queue publication to an actual Instance tick slice. It does not establish the enclosing
frame node, a fixed OS thread or the value/history of projectile TLS RNG. A node named `MsgProc` alone is
insufficient to supply those facts. No socket/decryption implementation is needed to state the recovered
parsed-message handoff contract.

### 3.1 Joining wire receipt to release checkpoints and activation

**S:** the registered pump/factory, common header, concrete result/control readers and World intake now compose
with the [actual release checkpoints](world-scheduling.md#101-joining-ordered-result-application-to-an-actual-release).
The composition starts with independent big-endian message bytes, rather than supplied parsed records or direct
Actor-handler calls. Original code performs group registration, kind selection, construction, parsing, owned-pointer
handoff and vector append; the installed six-witness controller/release path consumes those records through
original World/Actor dispatch at its checkpoints.
Socket/decryption and stream copying remain boundaries; these bytes are valid selected decoded messages, not a
recording or a complete encrypted transport session.

Two literal inputs establish the ordinary root case:

```text
1af: 01af 31415926 0000004d 0001 1234 00 00 00000065 0000 00
1b0: 01b0 31415926 0000004d 0002 1234 00 00 00000065 01
```

They address object `0x31415926`, ordering object 77, checkpoints 1/2 and result key
`(sequence=0x1234, derived=0, stage=0, target=101)`. Both messages enter World storage without processing the
Actor result immediately. With the explicitly supplied active ordered-mode state, the first checkpoint queues
the result before release; the conditional second checkpoint removes it after three movement preparations and
enters the observing ProcessResult boundary. Ordinary bow, wand, Fireball, KB, TS and Burning Arrow each execute
both installed event channels. Optional result fields and a noncanonical nonzero control byte also pass through
the actual readers and this same consumption path; this does not re-exhaust the isolated reader matrices.

Arrival order has observable consequences even when both records eventually become due. Receiving the stamp-2
control before the stamp-1 queued result blocks the first checkpoint. At the second checkpoint the control runs
first, finds no result, then the result is inserted and remains queued. Intake does not sort by checkpoint and an
early control does not create a tombstone. Duplicate results remain distinct. A future, different-order-owner or
unsigned `0xffff` head blocks later records; destination lookup and ordering-owner eligibility remain separate.

The original small-capacity growth helper also executes on these parsed messages, from null storage and across
a full vector. It moves pointers, clears old slots and frees old storage with allocator/free boundaries. An interior
cursor preserves its index across relocation. A separate explicit append after the cursor reaches the old end
restarts it at begin, so retained records can be visited again **if whole-core cleanup is deliberately omitted**.
That control establishes the intake rule; it is not evidence that official scheduling interleaves receipt there or
replays old records. The later [core composition](world-scheduling.md#41-joining-the-cores-ordering-production-component-updates-and-queue-cleanup)
executes ordinary prefix cleanup and delimiter compaction through selected supplied component callbacks;
completed core invocations remove the consumed prefix. Reentrant delivery/cleanup effects remain separate.

A supplied IO failure flag during an otherwise complete result/control read suppresses owner delivery and destroys
the unaccepted owned message. It clears pending-read bookkeeping without committing ring advancement or reducing
the remaining byte count. Retrying the same supplied bytes without that failure reaches ordinary delivery and
release; the failed message is not silently skipped. These controls establish the delivery gate and local stream
transaction, not real short-read/socket semantics or arbitrary malformed-payload recovery.

Selected frame-control bytes are also composed through their real factory/reader tables:

| Kind | Actual reader | Consequence through intake |
|---|---|---|
| `0x2b5` | table `0x14316c930 + 0x10` → `0x1400f0f00`, `ret 0` | no payload; null delimiter/deque publication follows the existing mode/activation gates |
| `0x2b6` | table `0x14345ef10 + 0x10` → `0x140e290e0` | read one byte at message `+0x10`; intake zero-extends it before calling the World frame-duration setter |

These kinds use the factory's **second** switch, indexed from `0x2b5` at table `0x1421c7358`; the lower
`0x1a8` switch cannot be indexed with them. Frame-deque push and owned destruction are observing boundaries.
The original activation slice now directly dispatches the parsed result/control through the real World/Actor
bodies, calls their destructors and clears the vector before enabling later frame admission. It does so for both
raw-zero and nonzero modes, without ordinary checkpoint eligibility filtering. A future/wrong-order pair therefore
processes during this supplied activation replay; this is distinct from the ordered checkpoint drain.

Remaining boundaries are explicit: actual root/clip/timeline and model/terrain/resource/effective-stat inputs,
mode/ordering/TLS production, complete core/frame scheduling, object/component lookup, allocators, nonempty
owned storage/destructors and ProcessResult consequences. Neither the original server's field choices nor
Predictive mid-release receipt follows from this offline composition. Accepted numerical Hit/pool/Buff processing
remains owned by the paired investigation.

Ignored round61 `audit_wire_release.py`, `wire-release-audit.asm/json`, `check_wire_release.py` and
`wire-release-controls.json` retain the fresh byte identities, original imports/table bindings, input messages and
executed paths. Factory inline switch data is retained for identity and excluded from executable decoding.
The fresh audit retains 182 ranges / 12316 decoded sites, seven table slots, eight anchors and ten endian-import
call sites. Thirty-seven finite controls execute 534500 original-instruction steps / 4213 visited sites, including
unsigned divide boundary controls at the pump's original instruction. These are offline checks, not native execution.

## 4. Intake preserves arrival order and moves ownership

A null input pointer returns immediately. For an ordinary message, both raw-zero and nonzero modes:

1. Save the old cursor index, using **zero if cursor equals the old end**, otherwise `(cursor-begin)/8`.
2. Move the incoming pointer into a temporary, clearing the caller's owned-pointer cell before append.
3. Append the message pointer, directly when capacity permits or through vector helper `0x14014c170`.
4. Restore the cursor to `newBegin + savedIndex*8`.

Consequently, `cursor==oldEnd` restarts at the beginning after append; it is not preserved as the former end index.
An interior cursor retains its index even when vector growth relocates storage. The intake performs no sorting
or lookup by the ordering fields. The consumer's head-only rules therefore depend on the producer's order.

The original growth helper moves the source pointer, clears moved old slots, frees old storage and publishes new
begin/end/capacity pointers. Finite controls execute its small-capacity append path with allocator/free fixtures;
they do not replace that path with a reimplemented vector. Large aligned allocations and error branches are
outside the interpreted controls. No moved message is destroyed in those growth cases.

Appending is distinct from dispatch. This intake does not directly run an ordinary message's update handler.
Its caller no longer owns an appended ordinary message; the queued message is destroyed by the later consumer.
Control-message branches destroy their input without clearing the caller cell in this body. That cell must not
be treated as a still-live owned object after the consuming interface call returns.

## 5. Frame controls and activation

`0x2b6` is handled before the raw-mode test. Intake subtracts `0x1c8`, reads message byte `+0x10` with zero
extension and invokes World virtual `+0x150`. Both World tables `0x1435da318/0x14346e0c8` bind that slot to
**`0x141645fa0`**, a two-instruction leaf that stores **EDX as u32** at World `+0x11c` and returns.
Thus a byte-valued frame duration becomes a u32 field. This control is destroyed and never added to the record vector.

`0x2b5` has mode/admission-dependent behavior:

| Condition | Pointer vector | Frame deque |
|---|---|---|
| raw mode zero | unchanged | unchanged |
| nonzero, World `+0xa00 == 0` | no delimiter appended | unchanged |
| nonzero, World `+0xa00 != 0` | append one null pointer | append current u32 frame length |

The last branch invokes deque helper `0x141238e80` with World `+0x9b8` and a local copy of `+0x11c`.
Its cursor is reconstructed with the same old-end-to-zero rule. The null is a frame delimiter, not an empty
ordinary object message. Deque internals and subsequent frame stepping are covered by the existing
[Lockstep protocol notes](../../protocol/07-latency-visibility-movement.md#6-lockstep-not-served-yet) and
[World scheduling](world-scheduling.md); this cut checks the publication arguments.

The shared constructor initializes `+0xa00=0` from zeroed RBP. Existing activation **`0x142183670`**, previously
identified in [the world protocol](../../protocol/03-area-and-world.md#4-world-messages-are-queued-until-the-world-is-active),
supplies the transition: its `0x1421836a5..0x142183755` slice sets `+0xb7a=1`, dispatches the retained vector,
destroys its nonnull records, empties it, sets the cursor to the end, clears `+0xb7a`, then sets `+0xa00=1`.
The initial replay occurs in both raw-zero and nonzero modes. Later local-player/listener initialization is
outside this slice. The active-dispatch flag is therefore used during activation as well as the normal core update.

## 6. Client/server consequence and remaining questions

The recovered path distinguishes packet arrival, parsed-message ownership, frame delimitation, activation replay
and ordered application. Nonzero-mode core updates apply initial key-zero messages and interleave eligible later
messages with object/component updates. Describing this as "all frame messages, then all objects" loses the
ordering checkpoints. Predictive's ordinary core batches and clears its incoming vector before object updates.
These rules extend the common projectile/result contract; they do not implement network Lockstep in LocalExile.

Established inputs are the common header fields, registered pump/factory/owner handoff, the interface and its
queue/cursor ownership, both control-message branches, and the activation-to-frame-admission transition. Still open:

- actual callback membership between the integer frame driver and state stack, and the execution thread;
  [Instance frame inputs](instance-frame-inputs.md) now close the concrete state/tick callers, final table and
  elapsed-time arguments; socket/read internals are outside this cut;
- the official server's ordering-object/checkpoint values and its frame/result-route choices;
- the projectile RNG thread/seed envelope around these scheduler boundaries;
- the separate accepted-Hit numerical/resource and Buff payload contracts owned by the paired investigation.

Round43 seals seven complete bodies, six constructor slices and the table-bound first-ret leaf:
21 ranges / 523 decoded instructions, 43 anchors, three slots and four named imports.
The 671 finite original-instruction controls cover independent BE header bytes, both actual caller prefixes,
mode/activation/control/ownership/cursor combinations, real small-capacity growth and activation slices:
52,801 steps / 343 visited sites. Stream copy, endian imports, allocation/free/destruction, deque push and parsed
dispatch effects are explicit fixtures. These same-agent offline checks do not execute the client or observe traffic.
Ignored evidence: round43 `audit_message_inputs.py`, `message-input-audit.asm/json`, `check_message_inputs.py`
and `message-input-controls.json` under the projectile research log root.

Round44 adds the registered ingress composition: three complete bodies, the factory's executable prefix and
whole-span identity including inline switch data, a caller-bound tail-jump leaf and six caller/constructor slices.
These 12 identity ranges contain 1,247 decoded code instructions, 34 anchors and seven slots; the prior 21 ranges /
523 instructions are rechecked and reused. Factory table bytes after the executable return are data.
The 35 finite controls execute the selected factory construction, common header and concrete intake together,
including failure suppression, first-reject/next-accept, null control, registration order and repeated pump calls:
12,945 steps / 414 sites. Result payload, stream prefetch/copy/failure flag, allocation/destruction, registration
memmove and a rejecting Instance factory are fixtures. Constructor/loop slices have explicit caller-frame inputs.
The supplied header plus payload fixture is not a complete valid packet replay. No native execution, socket or
encryption semantics, all-kinds factory coverage, full diagnostics, fixed thread or official scheduling is claimed.
Ignored evidence: round44 `audit_ingress.py`, `ingress-audit.asm/json`, `check_ingress.py` and `ingress-controls.json`.
