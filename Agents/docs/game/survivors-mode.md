# Survivors mode (MVP)

![Survivors preview](../../../docs/preview/survivors-mode-preview.png)

A Vampire Survivors-style run inside the normal client ([issue #84](https://github.com/LocalExile/LocalExile/issues/84)).
The horde is the current area's own monsters, the run's skills cast themselves, and the player's only job is to move.

The mode lives in the `game` module (`Modules/Core/Game/Survivors/Survivors.cs`, `static partial class Game`); its core
model is `src/GameServer.Core/World/SurvivorsMode.cs`, and per-connection state is
`src/GameServer.Gateway/Connection/ClientState.Survivors.cs`.

The structure follows [Azeroth Survivors](https://github.com/OutThisLife/azeroth-survivors-re)'s rules (wave clock,
auto-cast skills, a choice on every level). No code from it, or from any other project, was copied.

## Commands

| Command | Does |
|---|---|
| `/survivors start` | Starts a run in the current area. Needs an area with monster packs (The Coast, not a town or hideout). |
| `/survivors status` | Clock, kills, run level, learned skills and the horde target. |
| `/survivors pick <1-3>` | Takes a choice without the window (the window's click does the same). |
| `/survivors level` | Dev: banks a few run levels at once, so the choice window can be tested without waiting for kills. |
| `/survivors stop` | Ends the run and prints the result. |

A run also ends on death, on victory at 15:00, or when the area changes.

## Run skills

The run starts with the first skill the item catalogue resolves (normally **Fireball**). Each level-up opens the client's
own reward window with three items: a new skill while a slot is free (four slots), a support for a learned skill, a new
blessing while one is free (six), the next rank of a skill or blessing, and — every third run level — a unique item.
Clicking an item sends the window's normal C→S `0x88`; the server maps the item id to the choice, clears the window and
applies it.

**Skills** are every skill gem whose projectile metadata the client can draw and the projectile runtime can resolve, so
the catalogue grows with the item data. A gem with no projectile (Ice Nova, Arc) has no flight the client can draw here
and is left out.

**Supports** are real support gems linked to one skill, up to five per skill (the level-up offers them once a skill
slot is full). They are the run's own effects on that skill's casts:

| Support | Effect on the linked skill |
|---|---|
| Added Fire/Cold/Lightning Damage | ×1.25 damage each |
| Faster Casting | ×1.25 cast speed |
| Increased Area of Effect | ×1.3 radius |
| Increased Critical Strikes | +10% critical chance |
| Faster Projectiles | ×1.3 projectile speed |
| Life Leech | +3 life per kill |

**Blessings** are run-local passives shown as support gems, mirroring Azeroth Survivors' passives:

| Blessing | Item | Effect per level |
|---|---|---|
| Might | Added Fire Damage Support | +12% skill damage |
| Haste | Faster Casting Support | +10% cast speed |
| Area | Increased Area of Effect Support | +10% projectile radius |
| Critical | Increased Critical Strikes Support | +5% critical chance (×2 damage) |
| Leech | Life Leech Support | heal 2 life per kill |
| Wisdom | Enlighten Support | +10% run experience |
| Fortitude | Fortify Support | monster hits deal 2% less (25% base) |
| Swiftness | Faster Projectiles Support | +10% projectile speed |

Neither the character's stats nor its save change: every blessing and support is read by the run's own calculation.

## Unique items

Every third run level the window offers one **unique item** (`UniqueCatalog`'s definitions; `Mageblood`, `Crown of Eyes`
today) beside the skills. The item is created when the offer is dealt, so the pick equips exactly the item the window
showed: `PlayerInventories.Add` puts it into the first free equipment row its class fits (the main inventory when every
slot is taken), the client gets the inventory change and, for a visible slot, the equipment update, and the character's
stats are published again.

Unlike blessings, a unique is a real item on the character: it stays equipped, is saved with the character and keeps
its mods. A run waives its requirements while it lasts, because a low-level character could not otherwise wear it:
the item's **level requirement is sent as 0** (`Item.RequiredLevel`; the client keeps that byte and refuses an equip
above the player's level), the three `virtual_*_for_requirements` stats go to 1000 while a run item is worn (the client's
attribute check), and `ClientState.SurvivorsItemIds` keeps the item out of the server's inactive set so its mods count.
After the run the item follows the normal requirements again. An item already taken in the run, or already worn by the
character (matched by its name word), is not offered again.

## Skill bar

The run's learned skills are published as **Actor skills** (`0x02ba` with the actor's skill list) under ids from
`0x0300`, above the gem band and away from every ActionTypes id, so a socketed gem never takes one. Each record is the
real gem's effect at the run skill's rank, with one stored use and its cast interval as `CooldownMs`; the client's own
skill manager then draws the gem's icon. The ids go into the free slots of the saved skill bar with `0x0053` (sent,
never saved: they belong to the run), and every cast sends the cooldown trailer of `0x02ba` (`PlayerSingleUseCooldowns`)
with the real time to the next cast. On the run's end the skills are published again without them and the bar is resent,
so the slots clear.

Clicking one of those slots does not cast: the run casts for the player, and the client's own action would spawn a second
projectile next to the server's. The use packet is dropped (C→S `0x25`, logged).

## Rules

| Rule | Value |
|---|---|
| Run length | 900 s |
| Levels (areas) | the run moves to the next area every 120 s: `1_1_1`, `1_1_2`, `1_1_3`, `1_1_5`, `1_1_6`, `1_1_9`, `1_1_11_2`; the horde, run level and skills carry over |
| Monsters kept alive | 14 at the start growing to the cap (120) at the end |
| Spawn cadence | every 0.5 s, at most 4 per step, on a 30-38 cell ring around the player |
| Roster | the area's own `MonsterPacks`, deduplicated, ordered by the life the client gives the variety at the area level; the last four unlocked varieties are drawn from |
| Rarity | 15% magic after 20% of the run, 3% rare after 50% |
| Boss | the area's `WorldAreas` unique boss every 135 s |
| Ring wave | halfway between bosses, a closing ring of the weakest variety: 28 bodies at the start, 88 by the end |
| Monster scaling | life ×(1 + 0.75 × progress), damage ×(1 + 0.9 × progress) |
| Cast | each learned skill casts on its own timer, 1.2 s at rank 1, 7% shorter per rank (floor 200 ms); an empty cast retries in 200 ms |
| Targets | one projectile per target in 34 cells: the nearest, or the toughest for Pyroblast, Aimed Shot, Exorcism and Starfire |
| Projectiles | one more at ranks 3, 5 and 7 (four at rank 7) |
| Damage | the roster's median life at the area level, +30% per rank above the first, ×2 on a critical |
| Radius | +8% per rank, before the run's area blessings and supports |
| Run experience | 2 normal, 4 magic, 10 rare, 50 unique |
| Level curve | `5 + 10 × (level − 1)` below 20, then `195 + 13 × (level − 20)`, then `455 + 16 × (level − 40)` |
| Incoming hits | monster hits on the player deal 25% of their damage during a run |

## Packets

The mode adds no message layouts.

- **Spawns**: `0x2b9` create monster + `0x01a9` wake zone, reusing the normal monster pipeline.
- **Skills**: the projectile runtime's own server-created objects (the payload is sent with the client-predicted flag
  off, so the server, not the client, draws the projectile), plus `0x01af` on contact, `0x01b1` life and `0x01b2`
  flinch.
- **Level-up window**: `0x00c0` registers inventory `0x600` with the three gems (a `0x00bf` for an id the client does
  not know is fatal: "Got an inventory set change for an inventory that isn't in the set"), `0x0087` opens the reward
  window, and the pick is the window's usual C→S `0x88`/`0x8a`. Clearing sends `0x00bf` with the offered ids removed.
- **Chat**: `0x0017` for the run's own lines.

## Verified live (2026-11-02)

The mode was first built on the pre-refactor Gateway and then ported to the game module on top of main (dfcdc8f3):
the run starts, the skills cast and kill, the reward window offers skills/supports/blessings/uniques, and a unique item
is equipped (Stasis Prison into the body-armour row).

An ungeared level-1 Witch in `1_1_1`, driven through `scripts/dev/client_control.py`:

- `/survivors start` reported 15 minutes, 14 monsters, 120 at most, the area and its base damage; the bar went out with
  the default attack on the left mouse button and **Fireball on Q** (`0x0053 slot 0 0x0102, slot 3 0x0300`), and the two
  mouse slots stayed empty.
- Every level-up sent `0x00c0` (inventory `0x600`, three items) and `0x0087` (the window) and paused the clock; taking
  an item cleared it with `0x00bf`, published the new skills (`0x02ba`, five skills: the attack and four run skills) and
  the bar again (`slot 3 0x0300, slot 4 0x0304, slot 5 0x0312, slot 6 0x0316`).
- Supports linked through the window: "Linked Faster Projectiles Support to Static Tether", "Linked Added Cold Damage
  Support to Lightning Trap".
- Each cast sent one `0x1ce` per target and the actor's cooldown trailer (`0x02ba … 1.20/1.20 s`). Sampling one skill
  icon's brightness over a second showed the bar's cooldown sweep (168 → 340 of 765 as the skill recovered), and four
  skills sat on Q/W/E/R.
- With the client-predicted flag on, the server sent no projectile object and the client still drew each shot (a
  Fireball trail over the horde), while the server's simulation kept scoring kills (160 kills at 00:44).
- Halfway between bosses the closing ring spawned and surrounded the player (a visible ring of 28 bodies at ~26 cells).
- `/survivors stop` and the connection's exit both ended the run and put the skills off the Actor.

The first attempt sent `0x00bf` for `0x600` before the inventory was registered and the client showed
"Got an inventory set change for an inventory that isn't in the set"; the `0x00c0` registration above is the fix.

## What is deliberately out of the MVP

- No custom arena or boundary, no run save.
- Only projectile gems are offered: a skill with no flight (Arc, a nova, a curse) has no visual here yet.
- Some projectile gems have no cast animation of their own yet: the server casts them and the client draws the shot, but
  the character may stand still through the cast. The per-gem animations are a later pass (see "Needs testing").
- Every run skill is its gem's own projectile with the run's own damage, not the gem's real conversion, cost or cooldown;
  the supports are the run's eight.
- The run window is the quest-reward window: it lists gems, not custom cards with upgrade text.
- The unique pool is `UniqueCatalog`'s definitions (two today), not the game's whole unique list: the client data has no
  table linking a unique to its mods.
- No loot, gold or gem experience from run kills: `DropLoot` is skipped while a run is active.
- No HUD panel for the run's clock; the skills themselves are on the normal skill bar.

## Needs testing

The catalogue now holds every projectile skill gem the item data resolves, and the supports link to any of them, but
only a handful have been watched live (Fireball, Ice Spear, Kinetic Blast, Flammable Shot, Static Tether). Before calling
the mode done, walk the whole catalogue with `Game_SURVIVORS_MAX_ALIVE=10` so the field stays readable:

- **Every projectile gem:** its icon appears on the bar within a level-up, a cast draws a flight the client renders, a
  contact kills a monster, and the bar's cooldown sweeps for the skill's interval.
  **Known gap:** some gems have no cast animation yet (the character stands still while the shot leaves); each needs its
  own animation pass later.
- **Every support on every skill:** five links on one skill (Added Fire/Cold/Lightning, Faster Casting, Increased Area,
  Increased Critical, Faster Projectiles, Life Leech, in any five) and the skill's numbers change as the table above says.
- **A unique item** on the third level: the window shows it, the pick equips it in the right slot, its mods reach the
  character panel, and it survives the area change.

## Tuning

`Game_SURVIVORS_MAX_ALIVE` (10-400, default 120) changes how many monsters a run keeps alive.
