# Aristocrat

Aristocrat is the Duelist's Phrecia Slayer identity (`Slayer`, 3.29.3b), start node 33795.
Every node in the installed graph has a server consumer, with Gateway allocation, refund and reload coverage.
Native acceptance is still open: it needs the reviewed combined graph installed (see Admission below).

## Node checklist

| Node | Gameplay consumer |
|---|---|
| 48846 Inherited Fortune | Grants 5 ordinary passive points (PassiveSkills `SkillPointsGranted`). |
| 54114, 24607, 61147, 27473, 34482, 21905, 3952, 35432, 64905, 55065 | Each "Passive Point" grants 1 ordinary passive point. |
| 46836 Family Heirlooms | +1 level of active skill gems; items and gems have 25% reduced attribute requirements. |
| 35022 Gem-lined Cap | +4 level of support gems without the GemTags `exceptional` tag (Empower, Enlighten, Enhance, ...). |
| 30851 Centerpiece | +20% quality of active skill gems. |
| 50743 Private Tuition | 40% increased Intelligence; cannot be shocked while Intelligence > Strength. |
| 11154 Fancy Foil | 40% increased Dexterity; cannot be frozen while Dexterity > Intelligence. |
| 2748 Illicit Gains | 40% increased Strength; cannot be ignited while Strength > Dexterity. |
| 2688 Dreadful Attitude | 8% increased damage per complete 5 of the lowest final attribute; 50% increased attribute requirements. |

Effect text and numbers are the wiki's (from the game's stat descriptions).
"All Skill Gems" means active skill gems only, as in PoB (`grants_active_skill`); the level and quality stats never
reach supports, and the support stat never reaches active gems.

Three legal eight-point builds cover every node except the small node 3952, which only links Fancy Foil and
Dreadful Attitude:

```text
54114 48846 24607 46836 61147 35022 27473 30851
54114 48846 64905 11154 55065 2748 35432 2688
54114 48846 24607 46836 34482 50743 21905 2688
```

## How the effects are applied

- Gem levels and quality go through `EffectiveGemLevel` and `GemQualityBonus`, the same path as the family levels.
  Stored gem experience, quality and level requirements are unchanged.
- Requirement scaling is `floor(requirement × (100 + pct) / 100)`, never below 0 (PoB `CalcPerform`).
  It applies to gem attribute requirements and to worn item activation. Only passive sources and `/set_stat` count:
  an item's own requirement modifier is not used to decide that item's activation.
- While a condition holds (strictly higher; ties are not immune), the player stats carry the plain
  `cannot_be_frozen` / `cannot_be_ignited` / `cannot_be_shocked`. The Defence tab shows them as "Cannot be ...", and the
  ailment consumer reads the same stats; `Reconcile` drops an ailment already active once one starts to hold.
- The client checks gem and item requirements scaled by `global_item_attribute_requirements_+%` (`0x1402ce5f0`,
  `0x1421eca50`, `0x14246fc40`'s callers). The tooltip's number stays unscaled; the red/white colour and the socket
  refusal show the change.
- Dreadful Attitude adds to `damage_+%` once per stat build, like the other derived bonuses; a `/set_stat damage_+%`
  replaces it.

## Admission

The default Phrecia gate stays closed.
`Game_VERIFIED_ARISTOCRAT=1` admits only this identity after the same read-only installed-graph check as Gambler
(`VerifiedGamblerGraph`, ordinary `82D82CD3…` and combined `F2766B5A…`). A client with the original Phrecia graph is
refused before any save. Nothing installs or patches the archive.

With the graph installed, start through `scripts/dev/live.ps1` and run:

```text
/ascendancy phrecia Aristocrat
/ascendancy_points all
/add_passive 54114 48846 24607 46836 61147 35022 27473 30851
/ascendancy
```

Coverage: `AscendancyGatewayIntegrationTests.Aristocrat` and `VerifiedGamblerGraphTests`.
