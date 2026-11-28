# Combat effect rules

A combat effect rule in `world.db` names the source that supplies a value, the event that fires the rule,
the conditions it must pass, and the action and its recipient. The Game module validates enabled rules and compiles
them once per world-data snapshot. Combat uses the compiled rules. If a rule fails validation on reload, the Gateway
keeps the previous snapshot. [Compiler](../../../Modules/Core/Game/Combat/ItemEffectRules.Compile.cs) ·
[World-data reload](../../../src/GameServer.Gateway/Data/GameData.cs)

## Frenzy on skill hit

A character or hostile monster with a chance to gain a Frenzy Charge on skill hit rolls that chance when it lands a hit.
These are the five parts of the shipped `frenzy_on_skill_hit` rule. [Complete SQL for all three hit rolls](../../../sql/updates/world/2026_11_27_014022_shared_hit_charges.sql)

| Table | Frenzy rule | Meaning |
| --- | --- | --- |
| `fx_rules` | `id = 'frenzy_on_skill_hit'`, `status = 'built'`, `enabled = 1`, `position = 10` | One switch controls the rule for both actors. |
| `fx_rule_sources` | `kind = 'stat'`, `key = 'add_frenzy_charge_on_skill_hit_%'` | The source stat supplies the chance as a percentage. |
| `fx_rule_when` | Two `hit` rows, one with `{}` and one with `{"who":"monster"}`. | The first applies to the player, the second to a hostile monster. |
| `fx_rule_if` | `chance` with `{"percent":{"from":"roll"}}` | Each eligible hit rolls the value of the source stat. |
| `fx_rule_do` | `charge` with `{"charge":"frenzy"}`, target `self` | The actor that landed the hit gains the charge through its existing charge handler. |

For a player, the chance is the sum of the character's stat and the stat on the skill used.
For a monster, the chance comes from its own effects, including modifiers supplied by its map.
The player rolls Frenzy first (10), Power on hit second (20), and Power on skill hit third (30).
The monster rolls only at 10 and 30. A blocked monster hit grants neither charge from these rules.
`ord` numbers rows **within** a rule. `fx_rules.position` orders the rules of one event **for each actor**.
[Player rolls](../../../Modules/Core/Game/Combat/Resources/ChargeGains.cs) ·
[Monster rolls](../../../Modules/Core/Game/Monster/Mods/MonsterCharges.cs)

## Which effects can a row express?

| Event | Source and actor | Action, target and supported conditions |
| --- | --- | --- |
| `kill` | Stat on a player or a supported minion | Charge to self. Can require a frozen enemy at death, a poison count at death (at least, or negated: below), or equipment (player only). Minions read their supported skill's stat. |
| `hit` | Stat on a player, a hostile monster or a supported minion | Charge to self. Can require a critical strike (or, negated, not), a `skill_type` of `melee` or `attack`, `equipment` (`holding_shield`, `dual_wielding`, `wand`, `claw_or_dagger`), an `enemy_rarity` of `rare_or_unique` or `unique`, `enemy_frozen`, `enemy_bleeding`, `enemy_marked`, `enemy_killed`, `self_bleeding`, `self_poisoned`, `primary_target`, or a `used_skill` (`{"id":"Sweep"}`). |
| `when_hit` | Stat on a player or a hostile monster | Charge to self. Can require that the hit was a critical strike. |
| `block`, `stun`, `skill_use` | Stat on a player | Charge to self. Can require a `skill_type`: `attack` or `spell` (the blocked hit), `melee` (the stun), `vaal`, `curse` or `trap` (the skill used). |
| `kill` | Stat on a player | Grant Onslaught to self. Duration may be fixed, read from the roll in milliseconds, or read from another stat with a fallback. Chance is optional. Multiple sources use the strongest roll as the duration of a guaranteed grant. |
| `hit` | Skill stat on a worn player grant | Cast the granted mark on the enemy. Requires the skill's cooldown. Can require a rare or unique enemy and no active mark. |
| `block` | Skill stat on a worn player grant | Trigger an eligible spell socketed in the granting item against the attacker. Svalinn uses this combination. |

Each enabled rule needs `status = 'built'`, at least one source, one event kind, and one action; a charge rule may
have several (`do` rows in order: Yaomac's Accord gains all three charges). All conditions belong to group 0 and must
pass. Each condition name can occur once. Actions take `{}` for `target_args` and `value`. Only Onslaught uses a
duration timer.

A charge action is `{"charge":"endurance"}` (or `frenzy`, `power`, `random`), with an optional `"does"`: `to_maximum`,
`lose_all` or `lose_one`. A charge rule with `chance` rolls its stat as a percentage; without it the stat is a flag
and any value but 0 gains. A minion or monster rule needs a chance, a named charge that it gains, and only the
conditions its actor knows (critical on hit, the kill conditions at death; none for a monster).

Every charge rule has a `fx_rules.position`, where it rolls among its event's rules for each actor. Two rules of one
event and actor cannot share a position, and a stat has one rule per event and actor. An update that adds rules places
them among the rows already in the world, custom ones included. The positions keep the order of the C# lists they
replaced: on kill -30 to 70, on hit 10 to 450, on the other events 240 to 600. `fx_stats` lists every stat an enabled
rule reads, and the stats still handled in C# with the step that takes them ([design](effect-rules-design.md)).

For an effect that fits one of these combinations, start a new SQL file in `sql/updates/pending_world/`.
Use a shipped rule as a template, a stable rule ID, a stat key the actor already receives, and a free position. Opening a world applies pending files. Reloading reads edits already committed to the database.
When migrating a C# roll, keep it at the same point relative to the remaining rolls and make sure it runs only once.
[Database update conventions](databases.md) · [Order of compiled and existing rules](../../../Modules/Core/Game/Combat/ItemEffectRules.cs)

A new condition, such as “the enemy was ignited” on a hit-charge rule, needs C# support in the compiler and in the
combat path that fills the event. An action for a different recipient also needs a handler that can apply it.
Adding a name to a SQL row alone will fail validation. The shipped [Onslaught, mark and block rules](../../../sql/updates/world/2026_11_26_233754_combat_effect_rules.sql)
show the exact JSON shapes for those families.

## Verify an edit in a disposable world

Open PowerShell in the checkout folder that contains `GameServer.slnx`. Run these commands with the .NET 10 SDK.

```powershell
$env:Game_EFFECT_RULES = '1'
dotnet test tests/GameServer.Gateway.Tests -c Release --filter "FullyQualifiedName~ChargeWorldRuleTests"
```

The test `Disabling_one_hit_rule_on_reload_changes_both_actors` opens a temporary world database, checks both actors,
commits the SQL below there, and calls `GameData.Reload`. Running the test performs the edit for you.

```sql
UPDATE fx_rules SET enabled = 0 WHERE id = 'frenzy_on_skill_hit';
```

| One hit with all three chances at 100% | Player gains | Hostile monster gains |
| --- | --- | --- |
| Before the edit | 1 Frenzy, 2 Power | 1 Frenzy, 1 Power |
| After reload | 2 Power | 1 Power |

The same test then moves the player-only Power roll from 20 to 40 and checks the order of the two Power awards.
The seeded tests in the same class compare the awards and the next random draw with the C# lists, for every charge
event, and when a monster's hit is blocked. A successful run reports zero failed tests. To compare the original C# behavior, set
`$env:Game_EFFECT_RULES = '0'` and repeat the test command. [Test and disposable-world helper](../../../tests/GameServer.Gateway.Tests/ChargeWorldRuleTests.cs)

## Where the design came from

Marshmellion's [proposal #1375](https://github.com/LocalExile/LocalExile/pull/1375) supplied the composable rule format.
[Pilot #1418](https://github.com/LocalExile/LocalExile/pull/1418) supplied the three initial behaviors and tests.
The implementation on `main` adds supported combinations as mechanics need them and keeps effect execution in the
existing combat handlers. The draft's wider stat catalog, always-on modifiers, and area effects remain separate work;
[the design](effect-rules-design.md) says how they fit.

| Merged work | Implementation commits |
| --- | --- |
| #1619 compiled the pilot's Onslaught, granted marks, and Svalinn rules. | [Pilot rules](https://github.com/LocalExile/LocalExile/commit/8f3a1f106) |
| #1621, #1622 and #1623 added basic, equipment-dependent and frozen-enemy charge gains on kill. | [Basic](https://github.com/LocalExile/LocalExile/commit/fd2cc944d) · [Equipment](https://github.com/LocalExile/LocalExile/commit/ffab83ca4) · [Frozen](https://github.com/LocalExile/LocalExile/commit/42983ae6a) |
| #1624 added combined conditions and explicit kill-roll order. #1625 used both for Maligaro's Cruelty. | [Composition](https://github.com/LocalExile/LocalExile/commit/77812c5a1) · [Maligaro's](https://github.com/LocalExile/LocalExile/commit/7c9a9aa4f) · [Lethal poison count](https://github.com/LocalExile/LocalExile/commit/94768c80f) |
| #1626 shared Ice Bite with supported minions. #1629 shared on-hit charge rolls with hostile monsters. | [Minion kills](https://github.com/LocalExile/LocalExile/commit/994d9e3ac) · [Monster hits](https://github.com/LocalExile/LocalExile/commit/a21e82a28) |
| Step 1 of [the design](effect-rules-design.md) gave every rule one position and moved every charge rule rolled on an event to rows. | This branch |
