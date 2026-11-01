# Six ordinary projectile witnesses

Part of [RE 012](../012-projectile-execution.md).
This is a finite implementation handoff for client rules, not six newly implemented server skills.
All rows retain critical gap K3 (actual effective inputs/read-update timing), plus K1 wherever TLS gates are reached.
Final binding, enabled input, local prediction, result consumption and numerical damage are separate assertions.

## 1. Shared contract and input checklist

All six use the common [execution model](execution-model.md) and [C/S contract](client-server-contract.md).
For each, preserve sequence/derived/stage, source/release ordinals, remaining repeats, selected input view,
usable hand, selected projectile row, origin/height, motion configuration, actual frame inputs and result timing.
Do not reset cloned continuation state or replace all count aliases with generic projectile count.
Exact final-table evidence is in the three reference chapters below.

| Witness | Factory/class rule and count | Skill-specific output | Source and unresolved part |
|---|---|---|---|
| Ordinary bow | Final ranged `Melee` class; actual Bow and `skill_can_fire_arrows` select total arrows | Bow resource/held-arrow/attachment branches; local accuracy/entropy gate; shared continuations | [Bow/wand/BA](bow-wand-and-burning-arrow.md); full ordinary equipment/model release inputs not joined end to end |
| Ordinary wand | Same ranged class with non-Bow resource/count branches | `WandProjectile` lookup, projectile total and local attack gate; skips Bow attachment branch | [Bow/wand/BA](bow-wand-and-burning-arrow.md); not a separate complete live Wand release replay |
| Fireball | Installed client Fireball factory, ordinary variation, common effective count | Shared mover/contact/continuations, reason-dependent terminal effect, radius/resource scaling and stages | [Fireball/KB](fireball-and-kinetic-blast.md); K2 official direct/area result route remains unresolved |
| Kinetic Blast | Concrete KB class creates registered explosion-group actions | Trigger-target exclusion, random sectors, separate distribution/explosion radii, timer and exact result consumers | [Fireball/KB](fireball-and-kinetic-blast.md); actual first update/send order remain inputs; all KBoC variants not covered |
| Tornado Shot | Concrete TS client class; primary arrow aliases, independent secondary count | Quantized group rotation, radial children, fresh history, wind, copied continuation/Return state | [TS](tornado-shot.md), [geometry](tornado-geometry.md); no universal homing or full actual-input parity claim |
| Burning Arrow | Concrete BA client class on shared bow base | Ordinary reason/stage advancement plus gated Vaal/variation/deferred target presentation | [Bow/wand/BA](bow-wand-and-burning-arrow.md); ordinary BA controls do not establish Vaal/variant damage |

## 2. Bow, wand and Burning Arrow

Final registration overrides earlier base factories.
Ordinary bow and wand are equipment branches of one ranged Default Attack class, not two conclusions inferred
from string searches.
Both and BA bind the local attack gate and shared continuation executors.
Same bindings do not mean every support combination is eligible, or that a runtime capture must emit a flight
with any supplied stat set.

Reason callbacks advance the result stage, potentially twice under a separate Barrage gate.
Stage numbers do not count damaging contacts.
BA's target-attached clone with a `0.33f` countdown is deferred presentation, not proof of another damaging arrow.
Its extra effect uses TLS orientation; deferring render work must retain any shared-consumption obligation under K1.
Do not enable Vaal explosions merely because ordinary BA shares an ActionType family.

## 3. Fireball

The concrete client callback can create a local terminal effect using selected radius/resources and client/default
height, while advancing result stages separately.
The checked nonzero Pierce/Return callbacks (reasons 2/7) advance stages without creating that large effect.
The concrete body creates it when a flight exists and reason is zero; those nonfinal composed controls do not
exercise reason-zero presentation or establish a live wall explosion.
Terrain/end/skill-special branches must follow their actual terminal reason and callback, not a generic visual rule.

Explosion radius is computed from its actual base/effective radius and area terms; effect scale normalizes against
the authored resource radius.
See [radius arithmetic](area-radius.md), [increased terms](area-increased-terms.md) and [more factors](area-more-factors.md).
An unchanged rendered effect is not proof of unchanged numerical damage radius.

The local spell-avoidance gate is not a queued S2C result lookup.
Supported immediate `0x1af` and queued-plus-processing-`0x1b0` routes do not establish which official route is used
for a direct victim versus nearby area-only victims.
K2 therefore remains critical: this research does not derive authoritative double-hit/shotgun damage from visual
explosion count, phase memory or queue-prefix admission.

## 4. Kinetic Blast

Source reason handling creates registered explosion groups with distinct identities and cleared flight pointers.
The group excludes the triggering target from its own candidate filter; another source's group can still consider it.
Each retained explosion advances the group stage and consumes exact queued target results at that stage.

For `n` sectors, the client first draws the additional-blast chance (strict comparison), then a whole-group phase.
It samples each candidate angle inside its sector at a **fixed** distribution radius.
The retry counter decrements before testing: at most three candidates are queried, with a fourth draw consumed
and discarded on exhausted retries.
Acceptance requires the recovered line/endpoint layer queries, not an assumed rendered-wall predicate.
Only successful centers are retained; rejected sectors leave no empty stage or time slot.

Distribution radius and damaging explosion radius use separate base/effective inputs and shared area calculations.
There is no random radial-distance draw in this path.
The timer starts at zero; a due update emits one explosion and adds `0.10000000149011612f`.
There is no catch-up loop, and negative time debt persists.
A later update detects exhaustion/retirement.
This does not determine when the first actual World update reaches the new group.
The earlier server zero-delay / 1/30-second interval was provisional policy, not a native measurement.
No production value is changed by this delivery.

## 5. Tornado Shot

Ordinary primary final runs child generation before wind creation.
Secondary state prevents ordinary recursive child/wind generation.
High-level release refreshes the selected child row; secondary count and Volley/radial overrides are distinct
from the primary's extra-arrow count and Volley points.

The mother-derived RNG creates an integer random aim; radial emission then spaces children around that base ray,
followed by world/grid quantization and any reached variance.
It is not independent uniform child angles.
The mother registers a secondary template without consuming the flying-child allocator at that step.
Each actual child allocates an identity; swapping mother completion order changes descendant identity assignment.

Normally one mother's children share a fresh history, without the mother's processed-target sets.
`can_shotgun` gives independent child histories; Fork/Split descendants still inherit ancestor contacts as specified.
Returning mothers copy returning state into children; they cannot be forced onto an initial-child height shortcut.
Fork/Split/Chain/Pierce/Return and retired-source history ownership have composed fixture coverage through R81.
That coverage assumes effective grants and supplied terrain/targets/frames; it does not prove all legal support
interactions or official update ordering.

The recovered nearby-target query is not a universal homing guarantee.
Selected accepted/empty/rejected query controls yielded equal initialized trajectories.
Remaining target-reference/later behavior and actual inputs remain explicit limits.
Final history release, source retirement and deferred Entity removal must stay separate.

## 6. Later implementation acceptance

For each witness, first declare the K1/K2/K3 assumptions and required effective inputs.
Then compare action identity, release, count/layout, origin/height, motion samples, first contact, continuation,
skill stages and result keys against those same inputs.
A matching screenshot or matching child count alone is insufficient to suppress the corresponding server phase.
Full numerical damage remains separate from these client contracts.
