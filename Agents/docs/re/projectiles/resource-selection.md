# Projectile and effect resource selection (3.29.3b)

> Frozen detailed reference from R81. Historical next-step language is provenance, not an active backlog.
> Current scope and unresolved obligations are in [the delivery matrix](delivery-matrix.md) and
> [verification and limitations](verification-and-limitations.md).

Part of [RE 012](../012-projectile-execution.md).
These are bounded static paths in the pinned executable, checked against extracted table identities.
They explain resource inputs and local selection order; no rendered MTX comparison has been recorded.

The selected Projectiles row's ID also supplies the flying object type.
[Object-type binding](object-type-binding.md) follows that retained handle through generated inheritance,
metadata parsing and runtime component creation; art selection alone is not the full type contract.

## 1. Flight and effect resources use separate selectors

| Caller-facing resolver | Association callback | Generic selector | Resource family |
|---|---|---|---|
| `0x141940f60` | `0x141937ad0` | `0x141f66ea0 → 0x141f70f30` | animated effects |
| `0x1419417c0` | `0x141937b70` | `0x141f67770 → 0x141f73060` | flying projectiles |

The callbacks return ranges of 16-byte references from a SkillArtVariations row:
animated count/pointer at `+8/+0x10`, projectile count/pointer at `+0x78/+0x80`.
These are separate authored slot arrays; an effect slot number is not a projectile slot number by definition.
The generic selectors load MiscAnimatedArtVariations and ProjectilesArtVariations respectively.
The flight path is used by ordinary Fireball `0x141a8b580` when its special-context byte `+0x528` is zero.
Its separate monster branch is not replaced by this finding.

Both caller-facing resolvers:

1. Initialize a fresh eight-word random context with
   `[sequence11a, derived11c, owner88, requestedSlot, 0, 0, 0, 0]` through `0x1419c29b0`.
2. Select current stats through `0x14193b350(action, action.byte174)`.
3. Call action virtual `+0x380` for an ordered vector of one-based SkillArtVariations IDs.
4. Pass that vector, the selected stat view, requested slot, local RNG and optional selection controls downstream.

Thus the checked resource selection has its own fresh action-keyed random context.
It does not advance the emission-geometry context or TS wind's TLS stream.
This does not make every resource consumer action-seeded: the generic selectors also accept a null RNG.

## 2. Candidate producers and cache boundary

For the final Default bow/wand, Burning Arrow, Fireball and KB witness tables,
virtual `+0x380` points to `0x1419401e0`.
Its action vector has begin/end/capacity at `+0x100/+0x108/+0x110`.
Base construction `0x141937cc0` clears all three, and base copy `0x141938020` clears the new vector
even though it retains the source's skill pointer. The five checked final clone chains reach that copy.
Thus clones do not share or inherit the source candidate allocation.

If begin equals end, the getter builds a list through `0x14193faf0` and copies it into the action vector;
otherwise it reuses the nonempty list. It then returns a separately allocated deep copy.
An empty result has no independent initialized marker and is rebuilt on the next call.
Destroying the returned temporary vector does not invalidate the action cache.
Common action cleanup `0x14193a460 → 0x1401327b0` frees/zeros the action vector;
this is an action-lifetime cleanup edge, not an established live support/equipment invalidation notification.

The builder queries two SkillArtVariations association ranges:

| Lookup | Supplied key | Table association storage |
|---|---|---|
| `0x14227b6f0` | skill `+0x48` GrantedEffects reference | `+0x78/+0x80` |
| `0x142211b50` | retained ActiveSkills reference at resource `+0x63/+0x6b` | `+0x60/+0x68` |

It appends the entire first range and then the entire second range, preserving each range's order.
No deduplication or sorting occurs in this builder.
The maps themselves are sorted as described below: within each associated row, lower SkillArtVariations row indices
come first. Thus GrantedEffect-specific candidates precede ActiveSkill candidates on this checked common path.
This is not a complete MTX-versus-support priority rule; a current variation stat is a separate input.
IDs are one-based indices of native `0xd8`-byte rows.
A null row is encoded as `0x132`, one past the extracted 305-row table, rather than a normal fallback row.
The generic selectors assume valid candidate references at subsequent callback use;
this observation is not permission to synthesize that sentinel as a valid skill-art input.

TS overrides `+0x380` with `0x141a82610`, returning exactly one candidate:
`0x86` when the resolved Actor-associated object has DWORD `+0x14c == 0xe`, otherwise `0x87`.
The missing-object branch also chooses `0x87`.
Extracted rows 133/134 are `tornado_shot_bow` and `tornado_shot_wand` respectively.
This establishes the selector outputs and resource names, without inferring the complete equipment enum from `0xe`.
Both rows reference `skill_art_variation`; their projectile association arrays differ.

### Candidate cache, selected flight row and variation have distinct lifetimes

Shared projectile copy retains selected Projectiles row/owner `+0x358/+0x360` while base copy clears
the candidate vector. A clone can therefore temporarily retain a selected flight row with an empty candidate list.
Later high-level release eagerly republishes the selected descriptor; its resolver may reuse a nonempty
candidate list. Preparation's null-row fallback and low-level Return/redirect retention are separate branches.

Both generic action resolvers obtain their selected stat view anew and pass it to the generic selector,
which reads variation independently of the candidate IDs. A retained candidate list does not itself freeze
the variation value. Actual provider updates/read timing remain premises.
TS's `+0x380` override bypasses this vector altogether: each invocation queries the current Actor-associated
provider and returns the bow/wand candidate. A provider change can change a later TS selection without
clearing the common vector. The peer's fixed ranged-action-selection report supplies the existing
equipment/provider subscription chain; it is not an action-vector invalidator.

The bounded five-table entry/caller cut establishes constructor/copy/getter/cleanup transitions above.
It does not establish either automatic invalidation on every live metadata change or permanent immutability.
Unexpanded callees, aliases, higher slots, resource reload and actual action replacement order remain open.

### 2.1 Association-map construction and ordering

The SkillArtVariations factory `0x1419c6520 → 0x1419c9be0` installs load callback `0x1419cc890`,
which invokes loader `0x1422863b0`; raw row fill is `0x142286e70`.
The factory's R9 callback is destruction, not an association producer.
The loader visits native `0xd8`-byte rows and inserts the following foreign references:

| SkillArtVariations row count/pointer | Resolved foreign table | Sorted association vector |
|---|---|---|
| `+0xa8/+0xb0` | ActiveSkills | table `+0x60/+0x68/+0x70` |
| `+0xb8/+0xc0` | GrantedEffects | table `+0x78/+0x80/+0x88` |
| `+0xc8/+0xd0` | MTXTypeGameSpecific | table `+0x90/+0x98/+0xa0` |

These identities are connected through row-fill context producers, not inferred solely from extracted column names:
`0x140481fb0` loads `Data/ActiveSkills.dat`, `0x1416aefe0` loads `Data/GrantedEffects.dat`,
and `0x1408e4fb0` loads `Data/MTXTypeGameSpecific.dat`.
The checked reference resolution uses foreign row strides `0x101`, `0xeb` and `0x44` respectively.

Insertion `0x14225d3c0` builds 24-byte entries
`[foreignRowPointer, foreignTableOwner, skillArtRowPointer]`.
Its lower-bound comparator orders unsigned foreign row pointer first, then SkillArt row pointer.
Both in-place shifting and growth helper `0x142236840` preserve that position; lookup `0x142226d40`
returns the equal range for the foreign row pointer alone.
For one associated row in a loaded table, candidates are therefore in ascending SkillArt row-index order.
There is no pair deduplication in this insertion path.
The common builder queries the first two vectors above; it does not query the MTX vector.
That does not prove MTX is ignored: selection still reads the current variation stat, and other consumers are outside this cut.

Extracted base witnesses give one-based candidates `6` (default ranged), `61` (Burning Arrow),
`136` (Fireball) and `182` (KB). The default ranged row uses `arrow_projectile_variation`;
the ordinary Fireball/KB/BA rows use `skill_art_variation`.
TS's common ActiveSkill associations would contain both bow and wand rows, but its final override explicitly selects one;
the common builder must not replace that override.

## 3. Ordered lookup, variant selection and fallthrough

For each candidate, both checked generic selectors perform this sequence:

1. Resolve its SkillArtVariations row through `0x141937b90`.
2. Obtain the association range through the supplied callback.
   If the requested slot is outside that range, continue to the next candidate.
3. Select the variant integer from the explicit fixed-value option, or read the stat identified by row `+0x88`
   from the supplied stat object through its getter.
4. Use the requested association's ID string together with that integer as the resource-directory lookup key.
   A missing directory entry continues to the next candidate.
5. With an explicit resource index, return that indexed entry if in range; otherwise continue to the next candidate.
6. Without an explicit index, select from the matched directory row's resource array and return immediately.

In step 6, an empty array produces an empty result; it does **not** resume scanning later candidates.
A singleton uses no random draw.
For multiple resources, a supplied RNG uses `0x140484fa0` with a 32-bit bounded wrapper;
without one, a rejection loop obtains bits through TLS sampler `0x140e3b310`.
These generic alternatives exist even though both checked action resolvers supply their own local context.

The animated selector's positive path is `0x141f71050..0x141f71459`;
the projectile counterpart is `0x141f73180..0x141f735a1`.
Directory-row resource count/pointer are `+8/+0x10` for animated resources and `+0xc/+0x14` for projectiles.
Do not reuse one directory's struct offsets for the other.
Final failure produces an empty result, with diagnostic construction controlled by the caller.

Stat-reference conversion `0x142290090` returns a one-based Stats key.
The TS rows' extracted `Stat: 348` maps to native key 349, `skill_art_variation`.
A null stat reference returns `0x5b33`, one past the 23,346-row Stats table; it is not a valid gameplay stat.
No explicit retry with variant zero appears in these selector bodies.
The checked directory construction and exact lookup below likewise add no such retry.

### 3.1 Directory construction, exact keys and duplicate handling

Animated factory `0x141788670 → 0x1417892d0` supplies load callback `0x141789c80 → 0x14234a8c0`;
projectile factory `0x1419c69a0 → 0x1419c9fe0` supplies `0x1419cc590 → 0x14221d6e0`.
Their raw row-fill functions are `0x14234af90` and `0x14221ddb0`.
Both retain native row stride `0x30`, relocate the ID/resource references and resolve the optional Stats reference.
They preserve the authored integer variant: animated row `+0x1c`, projectile row `+8`.

Each loader inserts exactly one `(variant, ID string) → row` entry per authored row through `0x14222a250`.
There is no synthesized default-variant alias in these checked loops.
The separately built/sorted ID-pointer index at table `+0x60` is not the exact-key map at `+0x20` used here.
Insertion and lookup compare UTF-16 ID contents and the complete variant DWORD, rather than string pointer identity.
On an equal key, insertion discards the newly appended candidate and returns the existing entry:
the first row wins on this path. The extracted 2206 animated and 327 projectile rows have no duplicate keys.

Lookup `0x1422283e0..0x1422285f9` probes hash collisions using that same unmodified key;
its cold continuation after `0x1422284a8` is collision probing, not a fallback to variant zero.
Failure returns the map's end pointer to the caller, which can then try a later SkillArt candidate.
Thus no automatic variant-zero retry or generated alias exists along the checked selector/load/map path.
Other callers, late mutation/reload and selection of effective variation values remain outside that conclusion.

## 4. Consequences and remaining obligations

Fireball's separate requested-effect-slot fallback and KB's indexed-then-unspecified choice still apply
as documented in [their skill chapter](fireball-and-kinetic-blast.md).
Candidate priority, requested association slot, stat variation, directory lookup and final array index are distinct inputs.
A server-side hardcoded resource path cannot be assumed to reproduce these choices or the native MTX scale inputs.
Conversely, correct resource identity does not establish damage admission, effect lifetime or pixel alignment.

The selected Projectiles row has a separate [release/copy/redirect lifetime](motion-speed-and-time.md#14-selected-row-lifetime-across-release-ts-children-and-return):
high-level release refreshes it, ordinary TS children reselect association slot 1 after changing subtype,
and the checked low-level Return/redirect paths reuse it while recomputing speed.
Candidate-list construction/copy/lazy/cleanup is now mapped separately from that selected-row lifetime.
Close remaining live-metadata candidate invalidation, later directory mutation/reload, concrete missing-resource cases
and effective variant values before claiming complete MTX priority or runtime parity.
This is a presentation-input contract, not a reason to mix resource draws into trajectory RNG.

Local evidence: round14 `resource-selector-initial.asm`, `resource-selector-core.asm`,
`resource-selector-priority.asm` (only the positive `0x141f70f30` body), `resource-list-producers.asm`,
`resource-candidate-order.asm`, `resource-base-and-support.asm`, `resource-flight-followup.asm`
and `resource-data-crosscheck.json` under ignored projectile research logs.
Exploratory `0x14193a1e0`/`0x141938ad0` previews in the priority listing are unrelated and excluded.
The correct common candidate producer and animated callback are `0x1419401e0` and `0x141937ad0`.
Round15 `resource-map-factories.asm`, `resource-map-fill-callbacks.asm`, `resource-map-loaders.asm`,
`resource-map-insertion-and-row-fill.asm` and `resource-map-identities-and-complete-lookup.asm`
close construction, foreign identities and exact-key lookup. The last listing explicitly extends beyond split unwind
ranges for growth/lookup; earlier truncated previews do not establish those complete bodies.
`resource-map-identity-literals.json` and `resource-table-construction-check.json` retain source identities,
extracted counts/candidate witnesses and the duplicate-key check; no game data is committed.

Round23 `resource-cache-audit.py/json` retains 32 functions / 38 aligned spans / 3827 instructions,
15 final-table slots, 37 anchors and 12 clone-chain edges. Independent review rechecks those bytes/bindings
and adds 31 semantic anchors. The separate five-table prefix index has 565 slots / 85 distinct targets;
67 decoded functions and 18 unclosed leaf/bridge targets are discovery coverage, not a whole-program
invalidation or writer-absence proof. The fixed peer ranged-action-selection report supplies equipment/provider
subscriptions, not action-vector invalidation. No client execution or server change is established.
