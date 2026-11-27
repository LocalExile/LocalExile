# Item-triggered skills: what the client does with a triggered action

Why the [effect rules](../dev/effect-rules.md) show an item skill's art as a server-made effect object instead of
relying on the client. Client 3.29.3c (build 327984); addresses are that release's.

## The message

S→C 0x1ce (`InstanceProtocol.InstantAction(..., triggered: true)`) makes the client create and run a skill's action as a
triggered one. The skill must be among the Actor's skills. Sequence: `NextLockstepSequence` for a lockstep client, else
the connection's own counter; flags 0x101 for lockstep. Twister uses it live.

## What the client's executor does (`0x14219c1f0`)

It logs every refusal to `Client\logs\LatestClient.txt`:

- `Instant/Triggered action - Client failed to create aciton.`: the skill id is not in the Actor's skills (+6f0..+6f8,
  a binary search on the skill id word +0x40 for the triggered form).
- `Client couldn't execute a triggered|an instant action from the server. action type id = N`: the action's vtable +130
  predicate failed; N is the ActionTypes HASH16.
- `Client couldn't perform … action from the server without animation.` (vtable +270).
- `Client couldn't consume skill use for … action from the server.` (Actor +198: the client's own use and cooldown).
- Then `Instant/Triggered actionwas serialized to the client, but the client failed to execute the action.` with
  `skill_instance_id`, `action_id` (the sequence), `object_id`, `target_id`, `flags`.

In a session of about 40 triggered actions the client refused two, both Fire Burst (`action type id = 7485`) aimed at a
monster about 139 cells from the player, far off screen: the base predicate refuses a far target.

## Why nothing is drawn

| Skill | ActionTypes row, HASH16 | Factory near | Vtable |
|---|---|---|---|
| Lightning Bolt (`LightningSpell`) | 640, 0xBA44 | `0x1412ad1bd` | `0x143306E60` |
| Fire Burst (`FireBurst`) | 555, 0x1D3D | `0x1412a169d` | `0x1433C0C40` |
| Fire Burst on Kill (`TriggeredExplosion`) | 454, 0x39D3 | `0x1412c545d` | `0x1432B4EF8` |

All three derive from one base (constructor `0x1419e9b10`): start +F8 `0x1419e9840`, contact +140 `0x1419e9d30`,
predicate +130 `0x1419ea0e0` (fails for a spell while stat 0x28ea is set, else the base predicate `0x141942130`, where
the far target is refused), +270 always true, synthetic start wrapper +268 `0x141940ff0`.

The wrapper sets action +1e8, calls the start (which returns at once when +1e8 is set), then calls the contact with a
synthesised event whose hash is the release hash `0x9b8d`. So 0x1ce does reach the class's contact routine. There, for
hash 0x9b8d, it calls vtable +4c8: empty (`ret`) for LightningSpell, `0x141d31bf0` for FireBurst, `0x141243770` for
TriggeredExplosion. FireBurst's routine reads the area radius (`0x14193efd0`), gathers the entities in a box around the
aim (action +0x130) with `0x141d3d500`, and calls `0x14193f690(action, entity id)` for each. It gathers targets and
creates no effect. LightningSpell overrides vtable +450 (`0x14138b770`, not read).

No effect path is hard-coded for them, and no table links these ActiveSkills (rows 1026, 931, 791) or GrantedEffects
(6435, 6117, 5581) to art: `SkillArtVariations`, `MiscAnimated`, `ExecuteGEAL`, `GeometryTrigger`,
`DamageEffectVariations` and `SkillSurgeEffects` were checked.

## Where the art is

`MiscAnimated` holds art for other item skills (`enchantment_skills/*`: `ThunderboltStrike`, `FireNovaEnchantment`,
`TriggeredCleave`, `HolyNova`; uniques: `SolartwineNova`, `BitingBraidNova`, `ShieldShatterExplosion`,
`IgnitionBlastExplode`, `StarfellArrow`, `StarfellImpact`, `GhostCannonTriggered`, `BarnacleSnapSnap`,
`VoidShotExplosion`, `DeathWalkAreaEffect`, the Aegis effects). Its `__col4` is the effect's own radius in units (250
units to 23 cells; `Art.ArtRadius`), 0 for some (`FireNovaEnchantment`), and then a size fitted to a radius is wrong.

The official server shows explosions as effect objects (`KillExplosions.cs`: MiscAnimated 2877 `HolyExplosion`, recorded
from the official client). The effect rules do the same: an `EffectObject` at the spot, removed by the projectile
session's scheduler.

## Buffs with two effect lists

`BuffVisuals.EPKFiles1` is drawn on whoever wears the buff; `EPKFiles2` on the one who states it as an aura's source
(`Buff.AuraSource`, the 0x01 flag of the add-buff record). Death's Oath (`unique_deaths_oath_aura`, BuffDefinitions 218,
visual 222): stated on the wearer as an ordinary buff, the wearer shows the victims' purple flames; stated as the aura's
source, a small swirl on the character. Neither is a circle at the aura's radius; the effect rules draw one as an effect
object parented to the wearer, as Righteous Fire's ring is.

## Open

- Whether to skip a trigger whose target is beyond the client's range (the client refuses it anyway).
- Whether 0x1ce is worth sending at all for these skills: it makes the client count the skill's use and draws nothing.
