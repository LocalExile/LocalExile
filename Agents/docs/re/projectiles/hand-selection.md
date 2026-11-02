# Weapon eligibility and projectile hand selection (3.29.3b)

> Frozen detailed reference from R81. Historical next-step language is provenance, not an active backlog.
> Current scope and unresolved obligations are in [the delivery matrix](delivery-matrix.md) and
> [verification and limitations](verification-and-limitations.md).

Part of [RE 012](../012-projectile-execution.md), [release lifecycle](release-and-repeat.md)
and [synthetic startup](synthetic-start-and-server-commands.md).
**S:** these are stable-input algorithms recovered from the original executable, not observed gameplay.
The actual equipment publications, selected-stat values and arbitrary nested callback effects remain inputs.
Target SHA256: `78a0e227189c263658a7f65a02f086428b859bfc114ae8e7ce8aab721ebd48d5`.

## 1. Availability and selection are separate operations

All five final tables for bow/wand, Fireball, KB, TS and Burning Arrow bind availability slot `+480`
to `0x1419e4b60`. Four bind selection slot `+488` to `0x141b6fb10`; Fireball instead uses `0x141241980`.
These are virtual offsets, not action fields. In particular action field `+518` is a saved selection;
virtual `+518` is the unrelated Return operation.

The availability producer first calls `0x141ee88f0(skill, owner.World)`.
A nonnull result selects that entity's Actor component; a null result falls back to the action owner's
indexed Actor. A successful alternate-entity lookup whose Actor component is null does not take that fallback.
The selected Actor's virtual `+148` supplies the two class integers at profile `+14c/+150`.
No Actor/profile means availability zero.

The first integer is checked by `0x141ee8410(skill,class,1)`, the second by the same helper with last argument zero.
The result is a bit mask: bit 0 for eligible main hand, bit 1 for eligible off hand.
Class `104` is rejected without querying skill stats.

The inspected owner resolver itself checks skill DWORD `+a4`: zero returns null; otherwise it lazily obtains
and caches an entity at skill `+a8`, returning an existing nonnull cache directly.
This is a separate object cache, not a fresh lookup on every availability query.
Its complete invalidation/removal policy is not established here.

## 2. Ordered weapon-compatibility predicate

`0x141ee8410` reads the skill-selected view using `0x1400f3a50(skill,0)` anew at each query.
It does not use the action-selected adapter `0x14193b350` for these queries.
With stable inputs, apply the following tests in order. A nonzero restriction rejects a class outside its set:

| Key | Actual stat | Allowed class IDs before rejection |
|---|---|---|
| `2a3d` | `skill_can_only_use_non_melee_weapons` | `9,14` |
| `2a3e` | `skill_can_only_use_bow` | `14` |
| `4fd2` | `supported_skill_can_only_use_wand` | `9` |

Next obtain `allTypes = S[1066 + (hand XOR 1)] != 0`, where `hand=1` means main hand and `0` off hand.
Thus the inputs are the actual main/off-hand Varunastra flags. Four more restrictions follow in order:

| Key | Actual stat | Allowed class IDs; nonzero `allTypes` bypasses this rejection |
|---|---|---|
| `26e6` | `supported_skill_can_only_use_axe_and_sword` | `10,11,12,16,17` |
| `27ad` | `supported_skill_can_only_use_dagger_and_claw` | `7,8,57` |
| `26e7` | `supported_skill_can_only_use_mace_and_staff` | `13,15,18,33,58` |
| `49cb` | `supported_skill_can_only_use_axe_mace_and_staff` | `12,13,15,17,18,33,58` |

The last two sets are the exact native bit masks `0x040000020004a000` and `0x040000020006b000`,
guarded by unsigned class `<=58`. `allTypes` does **not** bypass the earlier bow/wand/non-melee tests.
These checks precede the later ignore-restrictions stat too.

The packed skill resource provides a list of 16-byte foreign-row references. After the restrictions above:

1. An empty list, or nonzero `21b3 ignore_skill_weapon_restrictions` for a nonempty list, accepts immediately
   **except** class `21` (Quiver) and `27` (Shield). Those exceptions continue through the remaining tests.
2. With `allTypes` and a nonempty list, walk it in stored order. Null foreign rows supply enum `104`.
   If any listed class passes `0x1424aad10(enum,7)`, accept.
3. Otherwise resolve the current class to its table row and accept only exact pointer membership in that list.

`0x1424aad10` tests `(cachedClassMask & 7) == 7`; its second argument is a **mask**, not category number 7.
It rejects enums outside `1..103` before the cache access. The inspected initializer builds each mask by
ORing the low byte of `1 << category` from the row's category list, with native shift masking.
The cache's TLS initialization/actual loaded categories remain separate inputs to these controls.
`0x1424aac70` resolves enum `k` through row `k-1` of the exact `Data/ItemClasses.dat` table.
The retained extracted table has 103 rows: `9=Wand`, `14=Bow`, `21=Quiver`, `27=Shield`;
the numbers above are not indices in `WeaponTypes.dat`.

This is the weapon-compatibility subroutine. It is not the complete skill-use/cost or empty-hand admission policy.
In particular, rejecting Shield as an attacking hand does not imply shields forbid an otherwise unarmed skill.

## 3. Sequence, stage and source-ordinal parity

Let `M = slot480()`, `Q = action11a:u16`, `T = action120:u8`, `O = action448:u32`.
The returned **AL** is the selection bit; upper register bits are not its result contract.

```text
if M == 2: return 0
if M != 3: return 1
p = NOT(wrap8(T + low8(Q))) AND 1

Fireball: return p
Other four final classes:
    if current action-selected S[29ce projectiles_barrage] == 0: return p
    return p == (NOT(O) AND 1)
```

Availability zero therefore returns one from the selector. It is not proof of a usable main-hand weapon:
the selector supplies a default independently of the outer skill-use predicates.
Fireball does not query Barrage or source ordinal in this callback. The other four query Barrage only when
both hands are eligible, then read `448` only when that stat is nonzero.
The low-byte addition/wrap is explicit; it is not a random draw or a TLS operation.

For both eligible hands and `T=Q=0`, ordinary selection is one; Barrage selection is one at `O=0`
and zero at `O=0xffffffff`. Other combinations follow the equation rather than a universal alternating-hand rule.

## 4. Startup saves a selection before ordinary ordinal reset

Bow/wand, KB, TS and BA starts call `0x141b6f650(action,1)` **before** shared `0x141ba2e60`.
The wrapper initially derives a count from the skill resource's stored range, using signed 64-bit division
by stride `77`; an absent range retains the supplied fallback count, one in these concrete starts.
This resource-record count is not the final projectile count.

For `action1e8 == 0`, it queries the Actor model helper `0x141e98030` at that index.
A present result with nonzero DWORD `+224` overrides selection: exactly one gives true, any other nonzero
value false. Zero/missing result falls back to virtual `+488`.
With `action1e8 != 0`, the model override is skipped and `+488` is called directly.
The chosen bit is saved to action byte `+518` at `0x141b6f740`.

A false saved selection then queries the default model resource. A present range there replaces the returned
count using the same stride; an absent range keeps the earlier count. A true selection skips that fallback.
Resource resolution, row ownership and model lookup are disclosed fixtures in the offline controls.

Consequently ordinary common start's later `448=0` does not retrospectively recompute this saved bit.
A fresh constructor sentinel can be read by the Barrage selector **before** the ordinary reset;
synthetic startup skips the reset as documented separately, but still runs this selection wrapper.
The wrapper's checked body neither resets `448` nor clears `1e8`.
This is a real ordering/input distinction, not proof that all preparation callbacks preserve those fields.
Fireball has a different concrete startup and is not assigned this four-class wrapper merely because it has `+488`.

## 5. Evidence and remaining boundary

Round35 retains nine original bodies, their chained ranges, ten final bindings, the table literal,
Stats/ItemClasses digests and independent fresh-byte anchors under the established private research prefix.
The original-instruction controls compare weapon restrictions, availability, both parity selectors and the
startup wrapper with separately written equations. They disclose selected getters, Actor/resource lookup,
foreign-row/classification access and owner management as fixtures.
The helper/cache bodies inspected but kept as fixtures are not counted as executed native implementations.

The full synthetic startup-to-first-launch source ordinal is still open: these stable-input contracts do not
erase Buff callbacks, initializer effects, later selected-view changes or other writers.
No current equipment state, official route/timing, arbitrary callback purity, server change or game recording
is claimed. Native stat dirty-queue scheduling remains owned by the collaborating damage research.
