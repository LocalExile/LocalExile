# Visiting Harbinger companions

Phrecia Harbinger nodes 20105, 60867 and 18859 grant usable summon skills without a socketed gem.
Each creates one friendly, immortal companion of its own kind after the player's cast completes.
The companion follows its owner and casts its own buffs; it has no melee attack.
This implements those summon/buff paths, with the limitations below, rather than every Harbinger mechanic.

## Current-client data

The source is the pinned 3.29.3b GGPK, extracted by `scripts/dev/extract-data.ps1`.
`PassiveSkills.GrantedEffectsPerLevel` identifies the summon and its level.
Its `alternate_minion` selects the current monster variant, rather than an older item-granted Harbinger.

| Node | Summon effect | Alternate | MonsterVarieties row | Buff effects |
|---|---|---|---|---|
| 20105 | SummonHarbingerOfTheArcaneAscendancy | 12 | 5892 | HarbingerArcaneSurgeUber, HarbingerArcaneSurgeUber2 |
| 60867 | SummonHarbingerOfTimeAscendancy | 13 | 5893 | HarbingerActionSpeedAuraUber |
| 18859 | SummonHarbingerOfFocusAscendancy | 14 | 5894 | HarbingerChannelBuffUber |

The summon skills have a one-second base cast, six-second base cooldown and a one-of-each limit.
Their `MinionType.ActiveCountStat` is absent; no invented counter stat is sent.
The monster type supplies maximum life 1, immunity to damage and untargetability by monster AI.
Its own modifier supplies 100% increased cooldown recovery; owner recovery does not change its buff cycle.

`GrantedEffects.SkillID` matches `AddBuffToTargetVarieties.__col5`, not that table's row index.
The binding provides a fixed value prefix followed by values from the skill's stats.
`BuffDefinitions.StatsKeys` provides the recipient keys.

| SkillID | Binding row | BuffDefinitions row | Recipient |
|---|---|---|---|
| 1 | 1 | 895 | Arcane Surge: 20% increased cast speed, 58% increased mana regeneration |
| 32 | 31 | 929 | Lightning buff: 10% lightning penetration |
| 31 | 30 | 922 | Time: `action_speed_-% = -10` |
| 29 | 28 | 924 | Focus: ailment immunity, cannot be stunned, unaffected by curses, 20% less damage taken |

Arcane's two skills each cast for 700 ms, last four seconds and have an eight-second base cooldown.
Time casts for 700 ms, lasts eight seconds and has a ten-second base cooldown.
Focus uses 700 ms, four seconds and eight seconds respectively.
All four set `CooldownNotRecoverDuringActive`.
Cooldown recovers during casting and pauses during that skill's active buff, without adding cast time twice.
Focus's skill description explicitly requires the owner to be channelling before a new buff cast starts.

The current Arcane Surge binding supplies zero spell damage and the lightning binding supplies zero shock chance.
Descriptions alone do not establish missing numerical values; these are not replaced with guessed constants.

## Lifecycle, timing and packets

`HarbingerRoster` holds transient per-character companions, with an area-connection lease and active clock.
Travel preserves their cycles and remaining buff deadlines, including loading time; pause freezes their clock.
Logout, owner death and source refund clear them.
Re-summoning replaces that kind and starts a new cycle.
Owned companions are excluded from ordinary area-monster saves and player attack targets.

The client receives the current monster type, its actual warp/buff skill records and native buff definitions.
Create/wake registration, cast starts, following and buff packets validate the member, source and area inside the
shared cipher send gate.
A source removed while its packet waits cannot create an obsolete immortal actor or publish an old buff.
A newly replaced member cannot start a cast until its own native actor exists.
Buff duration is sent as floating-point seconds in the existing buff-duration field.
Owner effects remain dirty until the corresponding player stats and pools have been published.

Arcane cast speed and mana regeneration feed the existing skill/recovery consumers.
Lightning penetration feeds the common player-hit resistance calculation.
Focus's final damage-taken factor applies after mitigation and before Ward, including self-burning damage.
Its immunity flags are exposed, but incoming ailment, stun and curse engines still need their shared consumers.

The client contains no server AIS scripts establishing the official follow radius or skill-selection priority.
The server follows the owner beyond 12 grid units and selects ready buffs in metadata order, one cast at a time.
These are explicit local AI policies.
Warp behaviour, ally-wide application and other Harbinger duration/debuff nodes remain separate work.
Time's raw recipient stat needs the shared action-speed timing integration before it changes all gameplay timing.

## Verification

Data tests resolve all three current variants and exact binding rows, including missing count stats.
Timing tests cover initial recovery, suspended recovery, simultaneous Arcane skills and delayed polling.
Gateway tests allocate actual connected nodes, cast the granted skills, reject damage to companions and refund sources.
Deterministic send-queue tests cover pause, replacement, source removal, area invalidation and deferred stats publication.

One real client and fresh synthetic characters verify both Arcane buffs, Time's buff and cross-area retention,
Focus's channel gate and the recipient stats, with paid source refunds.
With the fixture's Iron Hat, a controlled 100 physical hit removes 98 life after armour and 78 with Focus active.
Paid source refund restores the same hit to 98; these are direct native life-pool measurements.
Native traces and results are retained under `logs/phrecia-completion/harbinger-*-native.jsonl` and `*-results.json`.

Code: `HarbingerCompanionCatalog.cs`, `HarbingerCycles.cs`, `HarbingerRoster.cs`, `Program.Harbingers.cs`.
