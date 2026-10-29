# Surfcaster

Surfcaster is the Shadow's Phrecia Saboteur identity (`Saboteur`, ascendancy row 17, 3.29.3b), start node 18635.
No node grants passive points, so a build spends the class's eight ascendancy points.
The class is in progress; the table below marks which effects have a gameplay consumer and which do not yet.
Native acceptance is still open and needs the reviewed combined graph installed (see Admission below).

## Node checklist

| Node | Effect (wiki text) | Status |
|---|---|---|
| 37082 Baited Breath | All hits are Critical Strikes while holding a Fishing Rod | Works: every player hit crits without a roll. |
| | You can catch Corrupted Fish | No effect in 3.29.3b: the game has no fishing. |
| 64012 Screaming Reels | 60% more Cast Speed while holding a Fishing Rod | Works: server cast wait and the native cast clip. |
| 61696 Sea Legs | +40% to Cold and Lightning Resistances; Evasion Rating and Critical Strike Chance increased by overcapped Cold / Lightning Resistance | Works (`ResistanceOvercap`). |
| 60505 Stormy Seas | 100% of Lightning Damage Converted to Cold Damage | Works on every hit path (`DamageStats.Conversion`). |
| | Your Cold Damage can Shock | Not yet: monsters have no outgoing ailment state. |
| 52264 Brine Bound | Cannot be Frozen | Works: `cannot_be_frozen` blocks incoming freeze. |
| | 50% chance to Freeze Enemies for 1 second when they Hit you; Enemies you Freeze remain Frozen for at least 1 second | Not yet: no monster freeze. |
| 6252 Glacial Wave | Enemies Chilled by your Hits take increased damage and deal less damage by the Chill Effect | Not yet: no monster chill. |
| 61246 Within the Tempest | Shocks you inflict spread to other Enemies within 1.5 metres; Enemies you kill are Shocked | Not yet: no monster shock. |
| 52944 Changing Seasons | The Effect of Chill on you is reversed; All Damage Taken from Hits can Chill you | Works: any monster hit chills by its total damage, and the chill raises action speed instead; freeze is unchanged. |
| 37553 Ghost of the Deep | Touched by a Tormented Illegal Fisherman: 23% increased Movement, Attack and Cast Speed; nearby Allies deal 23% increased Damage | Works: the Torment Touched buff gives the player all four 23% bonuses and shows its icon. No allies or tormented spirits exist to use the rest. |
| 48926, 53922, 41643, 34767, 33031, 36782, 37534, 56184, 42290 | 4% increased Cast Speed | Works: `base_cast_speed_+%` reaches `cast_speed_+%`. |
| | 5% chance to Cause Monsters to Flee | Not yet: monsters have no flee state. |
| | 10% increased Rarity of Fish Caught | No effect in 3.29.3b: the game has no fishing. |

Effect text and numbers are the wiki's (from the game's stat descriptions) and the 3.29.3b PassiveSkills values.

Three legal eight-point builds cover all 18 non-start nodes:

```text
48926 64012 33031 37082 36782 37553 42290 61696
41643 60505 34767 61246 53922 6252 37534 52944
41643 60505 56184 52264 36782 37553 42290 61696
```

## How the effects are applied

- **Holding a Fishing Rod** means the active weapon set's main hand holds a `FishingRod` item class whose requirements
  are met (`HoldingFishingRod`; PoB's `UsingFishing` condition from `weaponData1`).
  The inactive set and an unusable rod do not count.
  The client's calculators test `main_hand_weapon_type` 15 for both rod stats, so 15 is the rod in that enum (inferred
  from those two reads).
- **Baited Breath.** `RollCriticalStrike` returns a critical strike without drawing while the condition holds and
  `always_crit_while_holding_fishing_rod` is set.
  That covers the default attack, area skills and projectiles, including hits with a 0% base chance.
  Any cannot-crit stat still wins (wiki).
  As the wiki describes, `main_hand_critical_strike_chance`, the pooled `spell_critical_strike_chance` and each
  skill's chance show 100% where the base chance is not 0, and stay 0 for a 0% base.
  Native: `virtual_always_crit` (key 0x2549, calculator `0x14275ba00`) is `always_crit`, Pressure Points, this stat with
  `main_hand_weapon_type` 15, or the non-channelling attack flag.
  `scripts/re/statkeys.py` places that read in `~0x14275b560`, but that function is the `virtual_global_cannot_crit`
  (0xb34) calculator before it.
  Bow shots keep their own path, since a bow and a two-handed rod cannot be held together.
- **Screaming Reels.** `FinalCastSpeedPct` applies the 60% more to the summed increases, the skill's own
  `base_cast_speed_+%` included, the way the client's `cast_speed_+%` calculator `0x142709610` does it:
  `round((increased + 100) × (1 + more / 100) − 100)`, at least −99.
  Its combined final (`combined_cast_speed_+%_final`, `0x142708190`) reads key 0x529f only for weapon type 15.
  The server cast wait (`CastMs`) and the skill record use that total.
  The skill record now carries the total as its `cast_speed_+%`, because the native action divides the base cast time
  by that stat (`0x1419e7500`).
  The client's stat getter `0x1424fc1e0` is a plain lookup and runs no calculator, so the client uses the value the
  server sends.
  Example: 16% increased with a rod is 86%, so a 750 ms Fireball takes 403 ms.
  Sending the total also adds a support's own cast speed (Faster Casting) to the record's `cast_speed_+%`, which the
  native clip had left out before.
- **Corrupted fish and fish rarity.** Fishing is not a gameplay feature in 3.29.3b (wiki: no confirmed fishing
  gameplay); there is no fishing skill or table, so `can_catch_corrupted_fish` and `fish_rarity_+%` have no consumer.
  No client code reads key 0x8e6 as a stat. Its two uses (`0x14017ae7c`, `0x140183a45`) pass 0x8e6 to the
  ClientStrings getter `0x1421fdc40` for the whisper "From"/"To" labels.
- **Ghost of the Deep.** Its PassiveSkills row lists BuffTemplates 671 (`CustomAscendancyIllegalFisherman`,
  AuraRadius 0), which puts BuffDefinitions 2550 (`Torment Touched`, BuffVisuals 1875, CancelOnDeath false) on the
  player with 23 each to `base_movement_velocity_+%`, `attack_speed_+%`, `base_cast_speed_+%` and `damage_+%`, and 1
  to `cant_touch_this` and `monster_is_touched_by_ghost`.
  `PassiveBuffAuras` reads that chain from the tables and adds the values to the allocated passives' sum, so the
  existing consumers take them like any passive: movement speed (server walk and the sent `movement_velocity_+%`),
  attack speed, cast speed and damage.
  The wiki's infocard and its 3.27.0g history give the player all four bonuses.
  The stat text's "nearby allies" line has no aura behind it (the template's radius is 0), and the engine has no allies.
  The two flags have no consumer: there are no tormented spirits to touch the player again, and the client reads
  `monster_is_touched_by_ghost` only in a monster drop-quantity calculator.
  Fairgraves' Tricorne's interaction (wiki) does not apply.
  The buff is published with S→C 0x1c1 at area entry and after an allocation, and removed with 0x1c2 on refund; it has
  no duration and stays through death.
  Whether the native client keeps it after a death is not checked live.
- **Changing Seasons.** `all_damage_taken_can_chill` lets a monster hit of any type chill the player.
  The magnitude uses the hit's total damage taken, all types together, against the usual maximum-Life threshold
  (wiki Changing Seasons: hits chill "with any damage type").
  Freeze and shock still need their own cold or lightning damage.
  `chill_effect_is_reversed` turns the chill into increased action speed by its effect (PoB `CalcPerform`: ActionSpeed
  INC +effect), capped at 30% like any chill, and still scaled by `chill_effectiveness_on_self_+%`.
  A freeze still stops the player (wiki: other action speed modifiers such as Freeze are unaffected).
  `not_affected_by_action_speed_modifiers` cancels the speed-up as well.
  The server walk, attack and cast timers and the sent action-speed stats follow `PlayerElementalAilments.ActionFactor`:
  a 30% chill gives 1.3, with `virtual_action_speed_+%` +30, `action_speed_-%` −30 and the intermediary +30.
  The client's `virtual_chill_effect_is_reversed` (0x1e46) is only a calculator (`0x14277bcd0`: key 0x1e44, or 0x1e45
  on chilled ground), network stats run none ([04](../protocol/04-player-entity.md) §4.1a), and its other uses are
  ClientStrings lookups, so the sent action speed carries the reversal.
  Chilled ground is not modelled, so `chill_effect_is_reversed_when_on_chilled_ground` has no source.
- **Sea Legs** already had its consumer (`ResistanceOvercap`, [11-skills](../protocol/11-skills.md) §12); the
  Gateway test compares the node with plain +40% cold and lightning resistance and both flags.

## Admission

The default Phrecia gate stays closed.
`Game_VERIFIED_SURFCASTER=1` admits only this identity after the same read-only installed-graph check as Gambler
(`VerifiedGamblerGraph`, ordinary `82D82CD3…` and combined `F2766B5A…`). A client with the original Phrecia graph is
refused before any save. Nothing installs or patches the archive.

With the graph installed, start through `scripts/dev/live.ps1` and run:

```text
/ascendancy phrecia Surfcaster
/ascendancy_points all
/add_passive 48926 64012 33031 37082 36782 37553 42290 61696
/item FishingRod1
```

Equip the rod and socket a spell, then check that its casts per second rise by about 60% and every hit is a critical
strike.
Take the rod off to see both end.
Build A also holds Ghost of the Deep: the Torment Touched icon shows, and the character panel has 23% more movement,
attack and cast speed than without it.
To try Changing Seasons, refund and allocate build B, then let a physical monster hit you: you are chilled and move
and attack faster for two seconds.

```text
/add_passive 41643 60505 34767 61246 53922 6252 37534 52944
```
To try the rod with Stormy Seas and Within the Tempest, refund and allocate:

```text
/add_passive 48926 64012 33031 37082 41643 60505 34767 61246
```

Coverage: `AscendancyGatewayIntegrationTests.Surfcaster` (Ghost of the Deep against plain stats with its publication,
reload and refund; Changing Seasons on a physical and a cold hit, a freeze and the unaffected flag) and
`VerifiedGamblerGraphTests`.
