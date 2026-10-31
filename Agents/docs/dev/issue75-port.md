# Completed issue #75 jobs: local port record

This branch contains the completed character-stat work from the paused LocalExile checkpoint, adapted to the
destination's current upstream server. This records a local transfer and automated integration validation;
it does not mark all of [issue #75](https://github.com/Wrathen/LocalExile/issues/75) complete.

## Revisions and recovery

- Source checkout: `F:/evry/evryPath/LocalExile`, `main` at
  `83ead2e0745f74b7d1cc555744c8628375a89039`.
- Destination checkout: `F:/evry/evryPath/evryPath`, branch `issue-75-character-stats`.
- Destination before this port: `c18572f8cdc7a24546c3e4476a60823e3cd98a5c`.
  It already includes upstream integration base `939b44f` and the earlier defence branch through `a8fe474`.
- Recovery branch: `backup/issue75-before-completed-jobs-20261027`, pointing to the unchanged pre-port head.
- New source commits: 27 cherry-picks, ending at `db6b2de`, retaining original messages and source-commit trailers.
- Integration adjustments and tests: `147ca9ecf3bdcb82213fa6637de2d53251554874`.
  The subsequent documentation commit changes no production or test code.

The source checkout was clean at the start and remains clean at the same commit. The paused full-issue goal was
not resumed. Nothing was pushed, published or posted to GitHub. Existing source server processes, client files,
profiles, saves and native evidence were preserved.

## Scope and integration decisions

Earlier jobs already present cover item quality/local defences, saved variable base rolls, Sacred Orbs,
special item ES rules, equipment ES-to-Life conversion, shield/body defences, rounding, Discipline and
full-ES suppression/restoration. The first mapping table identifies their existing destination commits;
they were not copied twice.

The added jobs cover regeneration modifiers/prohibitions, pool-based leech limits, typed weapon-local damage,
single application of local attack speed, hybrid flask lifetimes and charge gains, final supported spell ranges,
combined Frenzy speed, percentage attributes and equipment qualification, damage-conversion history,
level-scaled panel sources, flask duration/effect, self-ailment duration, added-base critical chance,
Brand/special-charge defaults, selected-skill aliases, signed flat pool sources, ES publication timing,
attribute-derived defence scaling, the corrected Intelligence ES bonus and inherent attribute-bonus prohibitions.

Shared code was resolved against both implementations:

- Preserve upstream's wide `AttributeModifiers` collector and override/removal behavior. New percentage and
  derived-pool inputs use that calculation rather than a competing attribute engine. (The port let a gem failing its
  own requirements disable only that gem while its host kept its stats; main restored host deactivation on
  2026-10-29 and the branch follows it, [19](../protocol/19-gems.md).)
- Preserve Chaos Inoculation's final one-Life rule, chaos-hit armour, own-aura restriction, actor-scale projection,
  spell damage restrictions, projectile return/spread and current bow impact handling.
- Apply both `local_elemental_damage_+%` and `local_attacks_with_this_weapon_elemental_damage_+%` in the shared
  weapon calculator. `ItemWeapons` and production weapon construction now use the same typed damage/rounding rule.
- Feed Mageblood the raw flask-effect sources and the level currently being rebuilt, preserving permanent/running
  strongest-effect deduplication. Combined regressions check level changes, source removal and panel/pool agreement.
- Adapt imported tests to the destination's actual ordinary catalogs, API signatures and resolved-attribute marker.
  Existing expectations changed only for the imported Intelligence ES and item-rounding rules.
- Repair imported documentation encoding and distinguish historical validation from this branch's fresh checks.

The destination deliberately excludes PoB character import support (`c18572f`); this port preserves that choice.
The source's private Hardcore/SSF/Ruthless server behavior, custom client/league setup and unrelated ranged/wand
action lifecycle work were not imported. Ruthless-only cases and duplicate mode parameters are therefore absent
from the ported tests; their ordinary calculation cases remain. The asynchronous assertion fix retains the relevant
conditional-critical tests but does not introduce the source-only wand test suite. This branch is not a replacement
for the source's private game-mode checkout.

## Validation

Fresh solution build and all three full suites passed on the integrated production/test code:

| Check | Result |
|---|---|
| `dotnet build GameServer.slnx` with isolated artifacts | 0 warnings, 0 errors |
| Core | 555 passed, 0 failed, 0 skipped |
| Data | 18 passed, 0 failed, 0 skipped |
| Gateway | 4,066 passed, 0 failed, 0 skipped |
| Total | 4,639 passed, 0 failed, 0 skipped |

The new composition regressions cover both local elemental buckets, Mageblood level/raw-source changes, and
attribute-source removal combined with inherent bonus prohibition, Chaos Inoculation and chaos-hit armour.
The full suites also exercise the earlier defence jobs and the destination's existing upstream regressions.
Suite counts differ from LocalExile because these checkouts contain different private/upstream features.

Reproduction from a PowerShell terminal:

```powershell
Set-Location 'F:\evry\evryPath\evryPath'
dotnet build GameServer.slnx --artifacts-path artifacts/issue75-port-20261027/build
dotnet test tests/GameServer.Core.Tests --no-build --artifacts-path artifacts/issue75-port-20261027/build
dotnet test tests/GameServer.Data.Tests --no-build --artifacts-path artifacts/issue75-port-20261027/build
dotnet test tests/GameServer.Gateway.Tests --no-build --artifacts-path artifacts/issue75-port-20261027/build
```

Local build logs, TRX results and their hashes, source/destination mapping, conflict-resolution records and the
pre-port plan are preserved under `artifacts/issue75-port-20261027/`. These ignored artifacts are not committed.
The receipt is `verified-port.json`; `results/*-final.trx` are the three successful full runs.
Earlier unsuccessful integration attempts remain alongside them and are not reported as passing runs.

## Evidence limits and remaining work

No real-client run was performed on this ported integration. Native checks quoted in the imported protocol/status
documents belong to the original LocalExile revisions, including their Standard/Ruthless matrices and bounded
UNPROVED results. Their `artifacts/issue75-completion/` paths resolve in the source checkout; raw captures were not
rewritten or copied into the branch as current proof. Native validation of the integrated branch remains required
before claiming it is client-verified. Standard test success also does not establish fault-free background execution;
the original source diagnostics retain their recorded disposed-stream task faults.

The reference-backed formulas and explicitly labelled local policies are unchanged in scope. Pinned PoB `16de4b82`
is the source's labelled 3.29.x reference fallback, not proof of every official fractional boundary. Unsupported
operators and the source's pending inherent-bonus native checks remain pending. The paused source checkpoint's
387-row ledger still reports 96 scoped certificates and 291 not-yet entries; this transfer does not certify more rows.

## Commit mapping

Both tables use source LocalExile commit IDs and destination IDs. All existing entries are ancestors of the
pre-port head; all newly imported entries have a matching cherry-pick source trailer.

### Earlier jobs already present

| Source | Destination | Completed job |
|---|---|---|
| `e768325` | `bfdb3c1` | Publish energy shield regeneration in the character panel |
| `8fd0529` | `983b073` | Calculate equipped defences from local modifiers and quality |
| `49301cc` | `0485965` | Generate and persist variable item defence base rolls |
| `db1c0cc` | `b9bb1d9` | Document native defence rolls and character panel verification |
| `fd22f71` | `a1d0489` | Model Sacred Orb defence rerolls and native target eligibility |
| `5096bc1` | `63c1039` | Handle Sacred Orbs through normal and repeat currency use |
| `3664b06` | `744e360` | Centralize item defences and honor native zero-ES overrides |
| `40a2a4f` | `0bae37a` | Convert equipped armour energy shield into maximum life |
| `f70fdc0` | `c9060e4` | Apply corrupted-item ES increases to character-wide scaling |
| `a36b74d` | `865ad0b` | Round final defence totals using the native half-up rule |
| `36200dd` | `0334d4e` | Apply shield-specific defence increases and exclusions |
| `5fc8998` | `c19e68f` | Connect Discipline to the shared energy shield pool |
| `be85bf8` | `d4f7bd7` | Clamp raw aura stat totals when publishing character stats |
| `335cf02` | `c2de3e9` | fix(stats): truncate scaled defence aura stats to integers |
| `f1a872f` | `302ff14` | fix(stats): preserve exact defence aura and flask multipliers |
| `7dbd021` | `9d46a06` | feat(stats): apply body-armour-specific defence operators |
| `b39ab7c` | `de82379` | feat(stats): apply spell suppression chance while ES is full |
| `b571175` | `a8fe474` | fix(stats): refresh restored suppression after resurrection |

### Newly imported commits

| Source | Destination | Completed job |
|---|---|---|
| `8c4afc8` | `2f1ee05` | docs(stats): document defence rules, aura scaling and suppression lifecycle |
| `bf3561c` | `8a4ea3a` | docs(status): record verified issue75 defence work and remaining gaps |
| `60f840c` | `2a89698` | fix(stats): share ordinary regeneration modifiers and prohibitions with recovery |
| `3de5777` | `9aa1cd0` | feat(stats): calculate maximum-pool leech limits and preserve refresh snapshots |
| `2ec5197` | `89b344e` | fix(combat): share typed weapon-local damage and apply local speed once |
| `0153a31` | `f8f78eb` | fix(flasks): compose gain sources and split hybrid recovery lifetimes |
| `3546c39` | `c9efdce` | fix(stats): publish final supported spell ranges after linked modifiers |
| `3a9dbf6` | `51c4b5e` | test(combat): synchronize asynchronous ranged impact assertions |
| `1d8aee4` | `8f0c94d` | fix(stats): compose combined Frenzy speed in per-charge panel rows |
| `5a99f51` | `f776eee` | feat(stats): resolve ordinary percentage attributes across pools and equipment |
| `cf1f772` | `166f038` | feat(combat): retain visited damage types through conversion paths |
| `10ebebc` | `7fb3b8f` | fix(combat): apply converted damage modifiers from complete type history |
| `008abec` | `fc9e50b` | fix(stats): resolve level-scaled panel amounts and combined recovery aliases |
| `19a6c3b` | `3ad4057` | fix(flasks): share raw level duration and multiply local utility factors |
| `8a58d96` | `14ba581` | fix(stats): include all-ailment self duration in elemental reduction rows |
| `0d7d0af` | `e775fec` | fix(flasks): resolve per-level global effect once at actual use |
| `1bec9e0` | `00477a2` | fix(stats): compose ordinary added base critical chance before increases |
| `aa4a700` | `6f6ae50` | feat(stats): publish native Brand defaults and special charge mirrors |
| `9c5e4b2` | `6fd7559` | fix(stats): derive selected skill aliases from original source snapshots |
| `6bbae87` | `8e31894` | fix(stats): consume signed level and attribute-derived flat pool sources |
| `0aabeb6` | `ec7cddf` | docs(stats): record native per-level flask effect validation |
| `a830ab9` | `8d035f3` | docs(stats): record selected critical chance native controls |
| `4aa895b` | `5288930` | fix(stats): sample energy shield publication at the send gate |
| `69d1530` | `910ac67` | feat(stats): apply item defence increases per final attribute group |
| `810d98b` | `8e280ad` | fix(stats): correct the inherent intelligence energy shield bonus |
| `e05c0f7` | `db4282d` | docs(stats): record current attribute-defence native controls |
| `83ead2e` | `db6b2de` | feat(stats): apply inherent attribute bonus prohibitions |
