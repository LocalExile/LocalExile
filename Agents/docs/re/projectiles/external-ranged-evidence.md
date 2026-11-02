# External ranged-action evidence and reconstruction constraints (3.29.3b)

> Frozen detailed reference from R81. Historical next-step language is provenance, not an active backlog.
> Current scope and unresolved obligations are in [the delivery matrix](delivery-matrix.md) and
> [verification and limitations](verification-and-limitations.md).

Part of [RE 012](../012-projectile-execution.md).
This chapter incorporates another investigation's findings without adopting its server presentation policy.
It distinguishes a published capture report, checked source behavior and independently inspected native instructions.

## 1. Provenance and evidence boundary

The source is [LocalExile PR 373](https://github.com/LocalExile/LocalExile/pull/373), fixed at
`b0afa90ea5c58e9082099d887180df8299dcdaf3`, based on main
`c1b112e4890d909a11f612a68702b514e66bdb0d`.
Its [ranged/monster port report](https://github.com/LocalExile/LocalExile/blob/b0afa90ea5c58e9082099d887180df8299dcdaf3/Agents/docs/dev/ranged-monster-actions-port.md)
describes official-server recordings and a separate LocalExile comparison.
The author reports retail 3.29.3b; those recordings' executable digests and raw files were not supplied here.
Their account-bearing captures remain private in the author's checkout.
No raw official traffic, trace completeness or capture loss was independently verified in this batch.

Treat the reported packet counts/timings below as external observations, not our own live measurements.
The pinned source files were read directly; their implementation is **L**, not proof of the official algorithm.
Native checks use our existing read-only executable and digest in [the ledger](evidence-and-open-questions.md).
No client was launched, attached or instrumented, and no server implementation was changed.

## 2. Official/local contrasts that constrain the execution model

| Published observation | Consequence to investigate | What it does not establish |
|---|---|---|
| Official bow session: 30 default attacks and 15 gem uses, no server `0x2b9` arrows or `0x2bb` removals | native generation is the leading explanation for these visible shots; compare local action/skill inputs | all skills, remote owners or network modes use the same ownership policy |
| Official Sand Spitters likewise had no server mortar objects; local trace saw no native shots | distinguish missing local activation inputs from absence of a native monster path | every monster automatically predicts from the existing local echo |
| Official bow hits used kind 3 and a filled result header; the reported byte was 2 | correlate result identity and actual flying actions | literal 2 is a universal skill index or safe constant |
| Official monster actions and hits carried corresponding real sequences; the local port used `0xffff` | retain action identity across echo, local execution and authoritative results | changing sequence alone is sufficient to activate native flight |
| Official default/gem skill IDs were `0x4000`/`0x8002`, local comparison used `0x102`/`0x103` | compare skill-manager registration, ID interpretation and action construction | copying the two IDs alone fixes prediction |
| Official S2C `0x1b4` carried skill IDs and compact pairs | native receiver identifies skill/stat-set updates; reconstruct their effective inputs | a trajectory or a client-to-server projectile report |

The source's older generalization that default attacks/monsters do not predict is too broad for its own newer report.
The code can remain a current-stack fallback while the activation contract is unresolved.
Do not turn that fallback into a permanent attack-versus-spell or player-versus-monster rule.
Our final ranged action binding and common retained-action execution are consistent with native capability;
static reachability still does not prove the current local connection activates it.

The follow-up [root admission trace](actions-and-emission.md#11-root-creation-local-echo-suppression-and-release-events-are-different-gates)
now identifies a local Predictive echo-suppression byte and the common local/S2C factory-to-queue path.
Default-zero remote/monster Actors have a positive S2C creation route, subject to shared admission gates.
The [animation follow-up](animation-and-release.md) connects conditional Actor subscription, track advancement
and event forwarding to shared release, with a separately reconstructed sequence/repeat animation seed.
Successful clip admission/first-release timing remains open;
this does not establish native shots for the external report's particular monster or current local connection.
The [stat-path follow-up](actions-and-emission.md#621-successful-startup-disables-the-checked-action-computed-overlay-paths)
also finds a startup gate that bypasses the checked action computed-overlay bodies.
Do not assume client-side derived-stat recomputation merely because shared computed-library code is present.

### 2.1 Timing and damage are constraints, not recovered formulas

The report fits official bow hit arrival as approximately `258 ms + 3.25 ms/cell`, against local
`249 ms + 2.72 ms/cell`; it also describes approximately 33 ms steps in official arrivals.
It reports a default-arrow descriptor conversion of about 300 cells/s locally, close to the approximately
308 cells/s suggested by the official slope.
These observations do not identify the exact client update cadence, launch delay, target-boundary contact,
echo gate or network/result scheduling independently.
In particular, neither the slope nor arrival quantization proves the server's 30 Hz scheduler is the native loop.

The official Spitter sample reports ten ordinary hits of 4 and one critical hit of 5, against local 5--7.
Keep mitigation, effectiveness, rounding and random spread as separate candidate causes.
Do not retune a damage constant until the effective inputs and arithmetic have been identified.

### 2.2 New native cross-check: the apparent points are skill stats

Reader `0x141fdeb10` places a u16 skill ID at message `+0x20`, a stat-set selector at `+0x22`
and compact integer pairs in the `+0x28/+0x30` vector.
The Actor switch maps message `0x1b4` to case `0x142194482`.
It looks up the skill, selects primary or additional network stats, and calls raw-pair setter
`0x1424cfaf0` for each pair at `0x14219464d`.
Thus these are stat keys/values, not the path proposed tentatively in the external report.
See [the protocol layout](../../protocol/11-skills.md#31-s2c-0x1b4-update-one-skills-stat-set).

This changes the research question to which effective skill inputs official updates carry and when they take effect.
The private values, activation dependency and consequences for existing action snapshots remain unknown.
It does not establish an outgoing per-projectile report or justify changing production skill IDs.

### 2.3 Later main comparison: release, range, speed and Burning Arrow

The later source checkpoint is main `d377bd256036242d2fb43f53eb7110b4897e1862`, fast-forwarded from
`5d5b5150e1b2e287c642edcaf75a49d075b9b4fe`. This comparison reads exact Git blobs without rebasing the
research branch or modifying previous snapshots. Fourteen inspected sources and their blob/SHA-256 identities
are retained in ignored `round79/upstream-comparison.json`. The original PR-373 attribution above stays pinned.
Most Gateway projectile adapters now live in `Modules/Core/Game/Projectile/`; the Core runtime remains under
`src/GameServer.Core/Projectiles/`. A vanished old Gateway path therefore does not imply removal of its mechanic.

| New main behavior or documentation | Relationship to this research |
|---|---|
| Attack-gem release uses the weapon clip's forward contact/height; Burning Arrow has a server flight fallback and ignite inputs | Reuse the extracted contact as a resource input. The native model/scale/contact transformation and execution gates remain distinct from a forward-only, half-aim-distance clamp. Ignite numerical behavior belongs to the damage investigation. |
| `ProjectileAimHeight` uses the higher release/aim ground and 12 steps of clearance for selected player primaries | Its source explicitly labels this LocalExile model v1 from observation, not native collision code. It does not close terrain-layer, doodad or body semantics in this research. |
| Default range becomes 150; Fork/Split and Chain refresh a movement-leg budget | Compare against the recovered getter/redirect/clone consumers. A default of 150 is already known; a uniform fresh budget for every continuation is not established merely by sharing the default. |
| `projectile_maximum_range_override` is applied as a positive cap, using `min` | The recovered `141ba62d0` range getter instead selects a nonzero override, otherwise special 50 or default 150. These are not the same local operation; full caller composition is needed before equating that getter with a universal movement cap or diagnosing all server range behavior. |
| Ordinary server speed resolves additive sources and Slower Projectiles; action speed affects pacing separately | Preserve the recovered selected arrow/projectile view, two integer roundings, context field, chain/return contributions, variation and scale divisor. The simpler adapter does not replace that client contract or establish all clock/action-speed coupling. |
| Source sets primary `ClientPredicted = spell`, citing a local Burning Arrow trace that produced no arrow before a server object | This describes a current-stack activation/fallback choice. It coexists with the published official session reporting no server arrow objects for default/gem shots; it is not an original attack-versus-spell ownership rule. |

The framework document still has a broader gem-prediction sentence than this concrete release assignment.
Use the fixed code for current local behavior and the provenance-labelled official report for that external
observation; neither silently supersedes the native capability and activation evidence. The published official
timing/packet report is substantially the earlier evidence already adopted here, not a newly verified capture.
No private recordings were acquired or replayed for this comparison.

Continue at actual effective stat publication, native activation and result scheduling. Do not reimplement the
upstream muzzle/ignite/model-v1 work as a research deliverable, or import its approximations into native rules.
The branch's server implementation remains unchanged. Fixed upstream entries:
[release](https://github.com/LocalExile/LocalExile/blob/d377bd256036242d2fb43f53eb7110b4897e1862/Modules/Core/Game/Projectile/ProjectileRelease.cs),
[aim height](https://github.com/LocalExile/LocalExile/blob/d377bd256036242d2fb43f53eb7110b4897e1862/Modules/Core/Game/Projectile/ProjectileAimHeight.cs),
[runtime](https://github.com/LocalExile/LocalExile/blob/d377bd256036242d2fb43f53eb7110b4897e1862/src/GameServer.Core/Projectiles/ProjectileRuntime.cs).

## 3. What can be reused from the reverse engineering

### 3.1 Rig contacts and animation selection

The port extracts ACT animation pools, AO/rig event inheritance, contact timing/position and flight/impact clips.
This is useful evidence about release inputs, beyond screenshot-based muzzle adjustments.
The reported Ranger bow contact is approximately 5.5 cells forward and 16 height steps; those are a concrete
rig/example, not a universal player origin or a replacement for the native model/scale/contact transformation.
The implementation omits the lateral component and clamps a forward offset to half the aim distance.
Keep those choices separate from native equations already traced in [emission](actions-and-emission.md).
The monster clip seed is explicitly an assumption in the port and still needs its concrete action caller checked.

The animation-choice generator uses the same nonstandard TinyMT-like transition family as projectile contexts.
Bounded instruction checks confirm `shr r11d,1` at `0x140485008`, scalar initialization at `0x140e0bec0`,
and specialized two-word initialization at `0x1413cbe50`.
The checked selector uses a nonzero request seed at `+8` or the Actor u16 at `+0x21c`;
the particular caller that fills a monster request is not proved by that fallback rule.
Native entry conditions/exclusions are broader than the port's simple weighted-pool selection.
Shared primitives do not merge animation, motion-speed, layout, object-hash and TLS streams into one generator.
Each consumer still needs its own seed words, reset point, skipped draws and state lifetime.
The [main-request follow-up](animation-and-release.md#2-main-request-inputs-and-their-independent-rng)
now identifies the checked common/spell/bow callers that supply `u32(sequence + outerRemaining)`.
This narrows the seed obligation for those builders; it does not prove that a particular monster factory uses them.

### 3.2 Monster movement is an input to projectile comparison

The port traces Actor setup `0x141e9ca20` through keyed initializer `0x1413cbe50` and bounded draw
`0x1404d03d0` to a per-object movement-speed percentage.
Its reported key is `[objectId, type-associated word]`, with limits from the two MonsterVarieties fields
`__col75/__col76` preceding AilmentThreshold.
This is relevant to projectile contacts: equal launch paths cannot yield equal contacts if target positions drift.
Our independent bounded check follows entity `+8 → wrapper+0x28 → loadedTypeData+8` for the second word.
Type base construction `0x142474910` writes it using path-hash helper `0x1424748a0`, which type registration also calls.
This supports a path-hash source; complete resource/alias/levelled-path identity and live values remain unverified.
Offline retained-listing checks cover scalar/two-word initialization and sequential sampling in 207 cases.
They verify arithmetic, not the port's complete live animation/target-motion behavior.

Native equal min/max skips the draw but keeps that fixed percentage at `0x141e9ccb2..0x141e9ccd9`.
The port's `MonsterSpeedRoll.Percent` instead returns zero for every equal range.
The extracted 10,691-row MonsterVarieties table has three nonzero equal ranges:
Skitterbot `AbyssCurseBot` and `AbyssDebuffBot` use `(30,30)`, and
Faridun `SareshFleshPillar` uses `(-20,-20)`.
Their nominal native factors are 1.30/0.80 rather than the port's 1.00.
Whether the current Gateway spawns or moves those objects is separate from this confirmed semantic difference.
No production fix was made in this research batch.

### 3.3 Result presentation must preserve native identity

The port's kind-3 `0x1af` feedback is compatible with the mapped immediate result path.
Its call supplies a root sequence but leaves the two byte fields at zero.
Our [result reconstruction](results-and-protocol.md) identifies their wire roles as result stage and derived action,
not a skill-bar index.
Root action, retained flying clone and hit-memory partition must remain distinct.
An immediate result can attempt processing without a matching action; that is not guaranteed successful presentation.
The published constant byte value is a useful witness to correlate, not an instruction to hardcode it.

The [result observer](capture-contract.md#23-result-control-exact-matching-and-actual-processing-attempts)
now separates the immediate path from queued control, exact-consume and batch attempts at their verified native callers.
Its completion bool is not damage, and null input action can be resolved later inside processing.
This permits a targeted future comparison of the report's kind-3 feedback with ordinary queued results;
it does not reinterpret the private capture or establish an official Fireball result route.
The [contact/result reassessment](fireball-and-kinetic-blast.md#21-terminal-visual-and-result-stage)
also leaves Fireball's direct-contact exact-consume edge unclosed, rather than presuming only extra area targets
need another consumer. Immediate/controlled-queued ingress does not require local area enumeration.
The corrected observer resolves contact Positioned targets through their Entity before correlating target IDs;
its separate terminal hook already receives Entity directly.

### 3.4 Resource choice is another independent presentation input

The [resource-selector reconstruction](resource-selection.md) supplements the report's rig/contact extraction.
Native flight and animated effects select different slot arrays through ordered candidate groups and current variant stats.
Both checked action resolvers initialize their own action-keyed resource RNG.
Correct animation contacts or matching projectile geometry alone therefore do not prove correct flight/effect art or MTX.
The construction follow-up now identifies GrantedEffect-before-ActiveSkill candidate priority and exact variant/ID maps,
with no generated default-variant retry on the checked load/lookup path.
Cache invalidation, later map mutation and actual effective variant values remain comparison obligations.

## 4. Research obligations before changing execution ownership

1. Compare effective skill records and action inputs at native creation, including owner, ID, equipment/count aliases,
   sequence, installed class, release events and allocated derived actions.
2. Include the identified S2C `0x1b4` skill/stat-set updates in effective-input comparisons;
   recover actual changed keys and action-snapshot timing from authorized recordings.
3. Correlate each root, flying clone, continuation and skill-created child with local contacts and S2C result keys.
   Do not use a late hit packet as the unproved cause of the first missing launch.
4. Reconstruct animation selector inputs and monster target movement before comparing contact positions/times.
5. Compare independently generated primary, continuation, child, location effect and target feedback ownership.
   The current `ClientPredicted` boolean is an implementation policy, not evidence that only primaries are native.

The [capture contract](capture-contract.md) records the operator-run comparisons still required.
Unresolved official/local differences stay on the research checklist; this supplement does not mark native parity complete.
