# Arcane Might Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans with test-driven-development, inline in the existing isolated worktree.

**Goal:** Implement issue #364's bounded Arcane Might consumer across current attacks and panels.
**Architecture:** A small resolver consumes resolved spell increased/reduced, selects the highest eligible grant, and preserves source-boundary aggregation. Existing attack/panel/projectile orchestration remains responsible for timing and context.
**Tech Stack:** C# / .NET 10, xUnit, extracted 3.29.3b data.
**Spec:** Agents/docs/game/arcane-might.md and https://github.com/LocalExile/LocalExile/issues/364.

## Global Constraints

- Work on fix/arcane-might from upstream/main; no CI integration or deployment.
- No Iron Will/provenance engine, new unsupported skills or variant acquisition.
- Existing skill base+quality adds before independent grants select max.
- Existing snapshot, conversion, support and rounding stages remain authoritative.
- No raw-data or client changes; all commands run at repository root.

## Review Focus

- No grant, flag value >1, negative spell increase and fractional transfers.
- Independent source aggregation versus quality addition, with removal/fallback.
- Active wand context, requirements/inactive sets, retaliation classification.
- All local/global damage components and no spell more/flat/speed transfer.
- Panel refresh and projectile release snapshots, actual target life changes.

## Task 1: Resolver and independent grant aggregation

Files: new src/GameServer.Gateway/ArcaneMight.cs; DamageStats.cs, StatKeys.cs, ItemModStats.cs, PassiveTree.cs, Program.GemSupports.cs; ArcaneMightTests.cs.
Interfaces: resolver Increase(Func<string,int>, bool wieldingWand=false, bool retaliation=false) and dictionary overload; source-grant Combine overloads. Existing SpellAttackIncrease wrappers delegate.
- [x] Run current Arcane regressions; expect missing stat consumers, aggregation and Crown/local input failures.
- [x] Implement modern stat contracts, max grants, flag OR semantics at independent source boundaries. Preserve skill construction's own quality addition.
- [x] Add zero/conditional/real-source/quality regressions; expect independent numeric results, never derive expectations from resolver.
- [x] Run resolver/aggregation regression group green (gateway integrations may remain red until Task 2).

## Task 2: Current attack paths and normal Crown

Files: SkillCatalog.cs, DamageStats.cs, SupportedAttackDamage.cs, Program.Equipment.cs, Program.Skills.cs, AttackSkillHit.cs, OrdinaryBowShot.cs, UniqueCatalog.cs; ArcaneMightGatewayTests.cs.
Interfaces: weapon snapshot carries actual wand context; all attack components use same transferred percentage; existing call sites retain orchestration.
- [x] Extend gateway tests to reproduce Crown lifecycle, mastery context, local damage and scope gaps.
- [x] Wire weapon/skill context, fix directly related local component omission/scaling, and define normal Crown with real current mods.
- [x] Verify existing attack, spell isolation, support and conversion tests alongside Arcane tests.

## Task 3: Lifecycle and runtime acceptance

Files: ArcaneMightTests.cs, ArcaneMightGatewayTests.cs and narrowly required source fixes.
- [x] Exercise real mastery grants/refund, stronger-source removal fallback, quality, Crown persistence and non-stacking, inactive weapons.
- [x] Check direct+area release packets, change inputs after capture, compare subsequent panel/hit and prior snapshot, and verify actual target life loss through real gateway runtime.
- [x] Run all focused regressions; inspect red failures before each required source correction.

## Task 4: Documentation, final checks and review

Files: Agents/docs/game/arcane-might.md, Agents/docs/protocol/20-damage.md, Agents/status/skills-and-combat.md, Agents/roadmap/skills-and-combat.md.
- [x] Replace planning state with actual support, source dispositions, evidence and known limitations; retain Iron Will exclusions.
- [x] Fetch main again; rebase if necessary, preserving unrelated work.
- [x] Release solution build and complete Core/Data/Gateway suites; store logs under logs/arcane-might.
- [x] Review whole diff and regressions; correct material findings. Record exact base/head/check results and native acceptance limits. Commit only relevant files in English; no remote publication/deployment.

## Execution outcome

Base and freshly fetched upstream/main: `5d5b5150e1b2e287c642edcaf75a49d075b9b4fe`.
All four tasks are implemented on `fix/arcane-might`; the worktree and branch are retained locally.
Release build: zero warnings/errors. Core 353, Data 34 and complete single-thread Gateway 1313 passed.
The final focused Arcane/Whisperer/damage/unique group covers 62 cases, including strengthened Crown and mana assertions.
A read-only independent reviewer found no Critical/Important issue; its minor mana-composition coverage request was added.
Earlier default-parallel Gateway failures and an isolated chill failure remain in local logs.
Matched two-test comparisons on unchanged main and this branch both passed; the intermittent cause is not proven.
No unrelated timing code was changed. Native acceptance of Crown/mastery/current composition remains pending.
Full logs and red/green evidence: `logs/arcane-might/` (local, ignored).
No remote publication, CI integration, deployment or client operation is included in this delivery.

## Native acceptance follow-up: effectiveness decoding

The user's KB49/169 report exposed a baseline reader error, also present in earlier test expectations.
GESSPL DamageEffectiveness is an offset: normalize to10000+raw permyriad, as pinned PoB's export does.
KB level1 now uses140%; zero-offset Clustering remains100%; negative offsets remain real reductions.
Spell base damage stays unchanged; flat added spell damage uses the corrected coefficient.
The existing shared packet scalar still does not separate weapon BaseMultiplier from added-damage coefficients.
Regression red8/10 ->green10/10, including realKB/Clustering panels and independently fixed173/593 endpoints.
The corrected direct/area target-Life regression applies the0.6factor only on secondary explosions.
Full Release build0warnings/errors; Core353/Data34/Gateway1322 passed. Read-only follow-up review found no findings.
Native corrected absolute-damage acceptance remains pending. Logs: logs/arcane-might/effectiveness-*.
