# Default bow/wand attacks and Burning Arrow (3.29.3b)

> Frozen detailed reference from R81. Historical next-step language is provenance, not an active backlog.
> Current scope and unresolved obligations are in [the delivery matrix](delivery-matrix.md) and
> [verification and limitations](verification-and-limitations.md).

Part of [RE 012](../012-projectile-execution.md).
These are bounded static client findings, not another server implementation or runtime acceptance.

## 1. Resolve the final registry, not only the shared constructor

Startup `0x1400fe9f0` calls base registration `0x141947e70` at `0x1400feae1`,
then client registration `0x141249120` at `0x1400feae6`.
The latter overwrites factories for matching `(16-bit action hash, ranged selector)` keys.
Registry `0x141941bb0` hashes ASCII names and writes both selector values when its mode is zero,
or only false/true for modes one/two.
Selector `0x141941c90` reads actual equipment class, not merely a projectile tag.

| Witness | ActionType | Client registration / factory | Final client class |
|---|---|---|---|
| Default bow attack | `Melee`, hash `0xb188`, ranged=true | `0x1412629d0 → 0x1412cb0d0` | ctor `0x141ac9dc0`, vtable `0x14326d930`, size `0x530` |
| Default wand attack | same ranged Default Attack binding | same | same, with equipment-specific branches |
| Burning Arrow | `BurningArrow`, hash `0x0415` | `0x1412696f0 → 0x1412956d0` | ctor `0x141ac58f0`, vtable `0x1432ec7e8`, size `0x550` |

Both factories can delegate to mine/trap/totem cases.
Their ordinary constructors call bow base `0x141b6eb70`, then shared projectile constructor `0x141ba2160`.
Base tables `0x14323d540`, `0x143273730` and `0x1432f0ff8` are not their final client vtables.
An earlier `WandProjectile` string hit in `0x141b70a30` belongs to Puncture and is not this default attack.

**I:** ordinary bow/wand projectiles share one ranged action class, with equipment-dependent count/resource/presentation.
The differences cannot be explained by a universal attack-versus-spell prediction rule.

## 2. Counts, resource and motion inputs

Final virtual `+0x598 → 0x141b6fa10` is true only for actual Bow class `0xe`
and nonzero `skill_can_fire_arrows` (`0x354`).
The common count getter then reads `virtual_total_number_of_arrows` (`0x4b4c`)
or `virtual_total_number_of_projectiles` (`0x4b4b`).
The same Bow condition reads `arrow_number_of_targets_to_pierce` (`0x18d8`) into cached `+0x44c`.

Default flight resolver `+0x510 → 0x141ac9e70` uses `0x1419417c0(selection=0,flag=true)` for bows.
For a non-bow it looks up `Metadata/Projectiles/WandProjectile`; a missing resource returns null.
The bow resolver constructs independent eight words
`[sequence,derived,owner,selection,0,0,0,0] → 0x1419c29b0 → 0x141f67770`.
Full resource/MTX/variation priority remains open.

Default mover wrapper `+0x558 → 0x1412df070` first calls common `0x141bacf60`,
then presentation helper `0x1413789e0(action+0x520,entity,flag)`.
Its Bow/flag branch updates entity components; their complete rendering semantics are not named here.
This is not established as a new universal height correction.
Actual path sampling, origin/model inputs and terrain remain linked to the shared model.

## 3. Actual local hit and continuation bindings

For both final tables, `+0x570 → 0x141b6f950` and `+0x778 → 0x1400ed4a0` (false).
They therefore take the local attack gate
`0x14193a8c0 → 0x141ebcff0 → 0x141ebc990`, with the applicable entropy branch.
They do not take that function's alternative `0x141ebd2f0` branch on these bindings.
This establishes local accuracy/evasion prediction, not authoritative life loss or absence of later S2C effects.

| Final slot | Both classes bind | Role |
|---|---|---|
| `+0x4b8` | `0x141ba4930` | release |
| `+0x4c8/+0x4d0` | `0x141ba68a0/0x141ba68b0` | natural/blocked terminal adapters |
| `+0x5f8` | `0x141ba6210` | Split entry |
| `+0x640` | `0x141bb6600` | Fork gate |
| `+0x638` | `0x141bb6530` | Chain budget/gate |
| `+0x678` | `0x141ba96a0` | Pierce policy |
| `+0x698/+0x518` | `0x141bb04f0/0x141bb0710` | Return qualification/execution |
| `+0x768` | `0x141bb09c0` | random/terrain Bounce |
| `+0x618/+0x620` | `0x141bb13a0/0x141bb2ae0` | emitter/redirect |

These are read from the final tables.
Effective stat gates, copied counters, phase memory and terminal branch order still constrain actual execution.
Same bindings do not make every combination eligible.

## 4. Default bow versus wand presentation

Helper ctor `0x141378630` stores context, Bow-condition and event hash `0x9b8d`.
Client action-start enables the held-arrow helper.
Animation-event override `+0x140 → 0x1412df000` calls shared `0x141ba4620`,
then hashes `0x9b8d/0xdc44` select held-arrow changes through `0x141378850`.
Default non-Bow additionally requests equipment effect `0x49`; the exact asset meaning remains open.

Default terminal callback `+0x590 → 0x1412df7c0` returns on natural completion.
For nonnatural completion with actual Bow equipment, it calls `0x141374480`.
Wand skips that Bow branch.
The helper reads flight render/position and arrow-attachment metadata, including
`map_no_arrow_terrain_attachment_in_area`, then selects rendering/attachment effects.
**I:** this supports an arrow terrain/target-attachment presentation role.
It does not establish another hit/damage package or another gameplay-layout RNG draw.

## 5. Burning Arrow overrides

Final BA adds update `0x141379570`, arrow resolver `0x141ac5960`,
reason callback `0x141ac59d0`, terminal presentation `0x141378fd0`,
and extra visual slot `+0x7b8 → 0x141379730`.
Arrow selection uses selection 1 for nonzero `vaal_burning_arrow_explode_on_hit` (`0x86f`), otherwise 0.
Ordinary BA must not inherit unconditional Vaal explosion behavior from the shared ActionType name.

The reason callback calls that extra visual slot when `0x86f != 0`,
`skill_behaviour_variation` (`0x1921`) is not 1 and reason is zero.
Natural terminal with variation 1 also calls the extra slot.
Virtual `+0x310 → 0x1419397d0` reads behavior variation, not the authoritative result-stage byte.

For a nonnatural Bow terminal with a target, helpers `0x141379210/0x1413793d0`
prepare target-attached resources and inspect buff type hash `0x5a4`/owner.
They may retain a cloned action with `+0x530=true`, `+0x534=0.33f`.
Update `0x141379570` runs the shared update, counts down and conditionally creates target effects before retirement.
This is a deferred presentation lifecycle; no new damaging projectile is established by that clone.

Extra visual `0x141379730` uses TLS LCG `state=state*0x41c64e6d+0x3039`, taking high 15 bits for rotation,
then selects resources and reaches position-effect creator `0x141f64020`.
This local visual random state must stay separate from action-seeded gameplay layout.

## 6. Result stage follows reason dispatch

Shared reason dispatcher `0x141baf5f0`, call site `0x141bafc35`, invokes final `+0x530`
with `edx=reason,r8=contact,r9=flag`.
Default callback `0x1412e34e0` advances stage once through `0x14193a170`,
plus once if virtual `+0x498` is true.
BA's callback does the same after the conditional visual work above.
Stage advance saves the previous byte, increments current `+0x120` and processes the old result range.
Thus stage number is not simply the count of successful damaging contacts.

The second-advance gate `0x141ba2b70` checks nonzero `projectiles_barrage` (`0x29ce`),
exclusion virtuals `+0x4a0/+0x4a8`, zero `power_siphon_fire_at_all_targets` (`0x874`) and count greater than one.
It is not an unconditional extra stage.
Another same-slot call at `0x141bad95a` has a different receiver type hash; it is not attributed to these classes.

The later [same-Actor stage/result composition](results-and-protocol.md#61-joining-emitted-contacts-installed-skill-callbacks-and-the-same-actor-queue)
executes the installed default/TS/ordinary-BA callbacks after a real emitted contact or natural terminal.
The old-stage range is actually selected, but ordinary queued results are retained under both supplied client
mode bytes. A matching subsequent control can process an old-stage key against the retained action/current stats.
Source components and final presentation remain supplied; the single ranged `Melee` table is not two complete
Bow/Wand equipment releases, and ordinary BA controls do not exercise the conditional Vaal explosion.

## 7. Coverage and retained evidence

Registry/equipment/final factory, count aliases, release/mover/continuation bindings,
local hit gate, reason/stage and ordinary client presentation paths are connected.
Complete resources/MTX, all attachment semantics, live dt/action/result correspondence,
remote owners, Lockstep and all special variants remain incomplete.
No usable C2S per-projectile report was found in these bounded paths; no global absence is claimed.

Retained local report: `logs/projectile-execution-model/round3/bow-wand-burning-findings.zh-CN.md`.
Relevant listings include `bow-wand-entry.asm`, `bow-wand-melee-registry.asm`, `bow-wand-final-factories.asm`,
`bow-wand-bindings.asm`, `bow-wand-final-tail.asm`, `bow-wand-extra.asm`,
`bow-wand-last-leaves.asm`, `bow-wand-gates.asm` and `shared-reason-full.asm`.
Explicit bounded ranges supersede unwind fragments and adjacent-function previews.
