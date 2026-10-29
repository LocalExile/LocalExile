# Polytheist

Polytheist is the Templar's Phrecia `Guardian` identity (3.29.3b), start node 409.
It is being built in five groups. This page lists every node with its consumer, or the group that still owes one,
or the engine system it is waiting for.
Native acceptance is still open: it needs the reviewed combined graph installed (see Admission below).

## Graph and builds

No Polytheist node grants passive points, so the 8 ascendancy points buy 8 nodes.
Start 409 (free) links to six small nodes, and they open three rings of seven nodes, each ring holding three notables:

```text
A: 27664 - 22056 Garukhan - 26231 - 32742 Solaris - 21979 - 61900 Abberath - 59443
B: 46465 - 64171 Yugul - 51229 - 43703 Lunaris - 29846 - 1830 Ralakesh - 6605
C: 30769 - 5266 Gruthkul - 322 - 24342 Brine King - 33835 - 23429 Ryslatha - 25478
```

Three legal eight-point builds (one whole ring plus a small node of the next ring) cover every node:

```text
27664 22056 26231 32742 21979 61900 59443 46465
46465 64171 51229 43703 29846 1830 6605 30769
30769 5266 322 24342 33835 23429 25478 27664
```

## Node checklist

Effect text and numbers are the wiki's (from the game's stat descriptions) and the 3.29.3b PassiveSkills values.

| Node | Effect | State |
|---|---|---|
| 409 Guardian | Start node, no stats. | Done |
| 27664 small (ring A) | 5% increased Light Radius, 10% increased Defences. | Done |
| 26231 small (ring A) | Same as 27664. | Done |
| 21979 small (ring A) | Same as 27664. | Done |
| 59443 small (ring A) | Same as 27664. | Done |
| 46465 small (ring B) | Same as 27664. | Done |
| 51229 small (ring B) | Same as 27664. | Done |
| 29846 small (ring B) | Same as 27664. | Done |
| 6605 small (ring B) | Same as 27664. | Done |
| 30769 small (ring C) | Same as 27664. | Done |
| 322 small (ring C) | Same as 27664. | Done |
| 33835 small (ring C) | Same as 27664. | Done |
| 25478 small (ring C) | Same as 27664. | Done |
| 22056 Devout of Garukhan | Evasion, Life and Mana Regeneration while moving; Unaffected by Shock; ground immunity. | Pending (group 2) |
| 32742 Devout of Solaris | Nearby Enemies are Blinded; crit against and from Blinded enemies; less damage from them. | Pending (group 4) |
| 61900 Devout of Abberath | Triggers Level 20 Abberath's Fury; Burning Hoofprints; Unaffected by Ignite. | Pending (group 5) |
| 64171 Devout of Yugul | Hex Reflection; Hexes affect Hexproof enemies; Immune to Reflected Damage; Unaffected by Curses. | Engine gap (all four) |
| 43703 Devout of Lunaris | Nova spells at the target; Spells fire 2 additional Projectiles; 30% increased spell area. | Pending (group 3) |
| 1830 Devout of Ralakesh | +3 to Minimum Frenzy, Power and Endurance Charges; Corrupted Blood cannot be inflicted on you. | Charges done; Corrupted Blood: engine gap |
| 5266 Devout of Gruthkul | Spells deal no damage and are disabled; 8% Life regeneration; +400 Life; enemy attack slow. | Pending (group 3) |
| 24342 Devout of the Brine King | +10% to maximum Cold Resistance; Cannot be Frozen; Cannot be Chilled. | Done |
| 23429 Devout of Ryslatha | Enemies you've Hit Recently have 100% reduced Life Regeneration; 30% more Maximum Attack Damage. | Pending (group 2) |

## How the effects are applied

- **Defences** (`global_defences_+%`): the passive stat bonus adds it to the armour, evasion, energy shield and ward
  increases (PoB's "Defences" applies to all four), so the final pools the hit and recharge code read grow with it.
- **Light Radius** (`light_radius_+%`) is the client's own effect: the player stats (0x1b3) carry the summed stat
  and the client sizes the player's light from it. The server has nothing to do with light radius.
  `FUN_1400b97b0` registers `virtual_light_radius_+%` (key 0x1e59) with `FUN_14273e960`, which returns
  `light_radius_+%` (0x4c1) plus `light_radius_+%_while_phased` (0xce7) while `phase_through_objects` (0x76a) is set.
  `FUN_1416574c0` reads 0x1e59 at `0x14165762a` (checked in capstone) and passes it into the light it attaches to
  `hip_jntBnd`.
- **Devout of the Brine King**: `base_maximum_cold_damage_resistance_%` raises the cold maximum the mitigation uses.
  `base_cannot_be_frozen` and `base_cannot_be_chilled` become the panel's `cannot_be_frozen` and `cannot_be_chilled`
  (PanelStats ailment rows). The incoming-ailment consumer (`PlayerElementalAilments`) reads them: a monster's cold hit
  never chills or freezes, and a chill or freeze already running ends at the next player tick (`Reconcile`).
- **Devout of Ralakesh**: `base_minimum_*_charges` set each type's minimum in `Charges.LimitsOf`, at most the maximum
  (PoB: max(charges, min(maximum, minimum))). The character always has three of each, with their usual effects, buffs
  and Charges-tab counts. Gains count from the minimum up to the maximum; when they expire, and at death, the count
  goes back to three.

## Engine gaps

These effects are not faked. Each waits for an engine system that does not exist yet:

- **Hex Reflection, Hexes affect Hexproof enemies, Unaffected by Curses** (Yugul): there are no curses or hexes.
  Monsters never curse the player, and no curse the player casts is applied to monsters.
  `MonsterModCatalog.Vacuous` lists `hexproof` and `reflect_hexes_chance_%` for this reason.
  The client derives `virtual_unaffected_by_curses` (0x4030: `FUN_1400e53c0` registers it, `FUN_1428d1880` sets it
  from `unaffected_by_curses` 0x1d7d); no other literal read of 0x4030 was found, so it is display only.
- **Immune to Reflected Damage** (Yugul): nothing reflects damage. Monster reflect mods never roll (`SupportOf` None),
  and the map device applies no map modifiers. The client has no literal read of `immune_to_reflected_damage`
  (0x45ac).
- **Corrupted Blood cannot be inflicted on you** (Ralakesh): nothing inflicts Corrupted Blood.
  The only related code maps a flask's bleed-and-Corrupted-Blood immunity to `immune_to_bleeding`.

## Admission

The default Phrecia gate stays closed.
`Game_VERIFIED_POLYTHEIST=1` admits only this identity, after the same read-only installed-graph check as Gambler
(`VerifiedGamblerGraph`, ordinary `82D82CD3…` and combined `F2766B5A…`).
A client with the original Phrecia graph is refused before any save, and the refusal points to this page.
Nothing installs or patches the archive.

With the graph installed, set `Game_VERIFIED_POLYTHEIST=1` for the Gateway, start through `scripts/dev/live.ps1` and,
on a Templar, run:

```text
/ascendancy phrecia Polytheist
/ascendancy_points all
/add_passive 30769 5266 322 24342 33835 23429 25478 27664
/ascendancy
```

What to check with that build (ring C):

- The Defence tab shows +10% maximum Cold Resistance and "Cannot be Frozen" / "Cannot be Chilled".
  A cold monster's critical hit never chills or freezes.
- The player's light is larger.
- Armour, evasion and energy shield increases are 50% higher than before the allocation.

The ring B build (`46465 64171 51229 43703 29846 1830 6605 30769`) shows three of each charge at once.

Coverage: `AscendancyGatewayIntegrationTests.Polytheist` and `VerifiedGamblerGraphTests`.
