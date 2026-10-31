# Unify Arcane Might across skills, equipment and passives

## Problem and goal

Kinetic Blast, Crown of Eyes and applicable passives should use the same Arcane Might rules in actual attacks and character/skill panels. The inspected main baseline had divergent consumers, incomplete stat support and generic aggregation that could incorrectly add independent grants.

This is a bounded correction on main; the DOs and DON'Ts below define completion.

### Input and responsibility boundary

Arcane Might consumes the **already-resolved spell damage increased/reduced percentage** supplied by the existing stat layer for the calculation context (currently `spell_damage_+%`). Here, "resolved" means the increased/reduced input, **not** final spell damage or a multiplier containing more/less effects.

This change owns selection of the applicable Arcane Might ratio and applying that ratio to the supplied percentage in eligible attacks. The stat layer continues to own how that percentage is produced. Iron Will, Strength-to-spell conversion, new conditional spell-damage producers and provenance-based anti-double-dipping are outside this delivery. Existing upstream changes to the resolved input must still be reflected in subsequent attack/panel calculations.

## Expected behavior

- Apply the supplied spell damage **increases and reductions** to eligible attacks using the **highest applicable Arcane Might ratio**. With no eligible grant, the contribution is zero. Independent grants do not stack: KB 150% plus Crown 150% remains 150%; an applicable 200% grant wins. Select the ratio before multiplying, including when spell increase is negative.
- Treat a fixed-150 stat as a flag: any nonzero value enables 150%, not value times 150%.
- Preserve negative and fractional contributions. At 150%, 20% spell increase contributes +30% attack increase, -20% contributes -30%, and 1% contributes +1.5%. Add this to the increased/reduced bucket, not as a more multiplier.
- Do not transfer spell-only added damage, spell more/less multipliers or cast speed; do not alter raw spell stats or ordinary spell calculations.
- Respect actor-wide, skill-local, active-wand and retaliation-only scope. An inactive weapon must not satisfy the wand condition. Grant removal, equipment changes and mastery refund must recompute from remaining eligible sources; removing 200% falls back to 150% if present, and removing the last grant yields zero.
- Apply the contribution once to all eligible attack components: weapon physical, weapon-local elemental/chaos, global added attack damage and existing mana-derived damage. Preserve effectiveness, conversion and support stages.
- Combine a skill's own base ratio and quality contribution **before** comparing it with independent grants. Preserve the existing quality-stat rounding convention; retain fractional transferred increases until established damage/panel rounding stages. Replacing every addition with max would also be wrong.
- Preserve existing calculation/capture timing: subsequent calculations use updated inputs, while an already-captured projectile retains its release-time offense, including its direct/area packets. Panel projection must not feed derived attack increases back into raw inputs or apply Arcane Might a second time.

Example: base damage 100, generic increased damage 100%, spell increased damage 20%, Arcane Might 150% => 230 before later stages. Another 150% source must leave this at 230.

## DOs — Required for this delivery

Deliver the following as one coherent correction. KB, Crown and passives must share the same modern semantics rather than receive separate source-specific fixes.

- [x] One context-aware resolver for all six modern stat contracts; dictionary/name-query parity; highest eligible ratio, signed/fractional values and correct flag handling. Consume the already-resolved spell increased/reduced input without reimplementing its producers.
- [x] Correct item/passive/actor/skill ratio aggregation, preserving a skill's base-plus-quality addition and raw character inputs.
- [x] Supply actual active-weapon and retaliation classification context; cover inactive-set and wrong-skill negative controls on existing paths. If retaliation has no executable path yet, require consumer/classification tests and report execution as unavailable rather than adding a new skill.
- [x] Use the common result for existing ordinary/unarmed/melee/bow/wand attacks, supported skills, projectile direct/area captures and character/skill damage panels. Preserve scope for existing triggered-child paths.
- [x] Fix directly related weapon-local damage omission/scaling; include global and mana-derived attack components once.
- [x] Add normal Crown of Eyes with verified current modifiers; verify equip/remove refresh, persistence and no stacking with KB/Whisperer. Document any unrelated item mechanics that remain partial.
- [x] Cover real wand mastery grant/refund, existing Whisperer consumer, skill constant/quality loading and all inventoried modern stat contracts. Consume Foulborn's 100% and the mutated retaliation stat without requiring a new full item/skill implementation.
- [x] Give every inventory row an explicit coverage disposition. Integrate already-supported execution paths; use calculation/data tests for unsupported records and document their runtime limits.
- [x] Add production-path regressions for absent/competing/removed grants, equipment, resolved-input updates, capture timing, conversion/support restrictions and panel/hit parity. Re-run Release build plus Core/Data/Gateway suites; keep native acceptance distinct. At least one supported attack must verify real target life change; packet or panel assertions alone are insufficient.

## DON'Ts — Explicitly outside this delivery

- Implement Iron Will, Strength-to-spell-damage conversion, The Iron Fortress or Iron Grip interactions, or reconstruct modifier origins to prevent Strength double-dipping. These are not prerequisites for the bounded Arcane Might consumer.
- Build a new general conditional/provenance modifier engine or implement missing spell-damage producers such as Umbral Army. Already-resolved values from existing upstream evaluators are consumed as supplied.
- Implement full Foulborn/mutated item definitions, new acquisition/crafting systems, new retaliation/variant/Vaal/triggered/mercenary skill execution, or Phrecia allocation/client compatibility. Normal Crown of Eyes is the explicitly included item addition.
- Implement the mercenary-specific `spell_damage_modifiers_apply_to_attack_damage` consumer for Charged Dash of the Arcane. This is an excluded mercenary skill path, not an identified legacy player-item requirement; do not automatically treat it as an actor-wide grant.
- Implement spell-damage-to-DoT application or unrelated missing item mechanics such as leech.
- Require full PoB architectural parity or its provenance-sensitive complex-interaction cases as acceptance gates. Use pinned PoB as a reference for the supported bounded rules.

## Known limitations to carry into implementation docs / PR

- **Resolved-input fidelity:** Arcane Might reflects the supplied spell increased/reduced percentage. Missing or incorrect upstream spell-damage contributions remain missing or incorrect; this change does not calculate them independently.
- **Iron Will / Strength provenance:** no claim of correct Iron Will + Arcane Might anti-double-dipping, including Iron Fortress / Iron Grip combinations. If an upstream contribution already reaches both spell and attack increases, this scalar-based consumer cannot identify and deduplicate that origin. This requires separate work and must be documented explicitly.
- **Source lifecycle versus consumer support:** an understood stat/quality record does not make an unsupported skill executable or a variant item obtainable. Record each source's actual coverage without implying full gameplay support.
- **Mercenary contract and patch evidence:** the mercenary-specific implicit contract is excluded from this player-facing delivery; dated wiki/PoB references do not establish exact client-patch parity.

These are intentional boundaries, not mandatory later phases of this issue. The issue can be completed when its DOs and compact acceptance pass and its actual limitations are documented; DON'Ts do not keep it open. Do not describe that completion as universal Arcane Might interaction parity.

## Compact acceptance

1. Known fixed damage and spell increase: KB uses 150%; Crown equip/remove does not double KB, but enables/disables 150% for a ratio-free attack. With no eligible grant, spell increase adds no attack damage. Compare panels with actual target life loss under controlled hit conditions.
2. Wand mastery alone: active wand enables 100%, non-wand disables it, inactive-set wand has no effect. Retaliation-only grants never affect ordinary KB.
3. Competing 100/150/200% sources and skill quality: highest eligible candidate wins, including negative/fractional spell increases; removing the strongest falls back to the next, and removing all clears the effect. Check another skill/default attack for leakage.
4. Physical, weapon-local and global added components; spell-only negative controls; KB direct/explosion captures; existing support/conversion behavior. Input changes affect later calculations but do not alter already-captured shots; panel refreshes cannot accumulate the transfer. Expected values must be independent of implementation.

## Supporting inventory and evidence

<details>
<summary>Expand source inventory, stat contracts and main-code evidence</summary>

## Source inventory

Checked extracted 3.29.3b stat-reference fields in `Mods`, `PassiveSkills`, `PassiveSkillMasteryEffects`, `GrantedEffectStatSets` and `GrantedEffectQualityStats`: **3 item modifiers, 1 passive node, 1 mastery, 24 numeric skill-constant records, 3 quality records and 1 mercenary-specific implicit skill record**. Counts include internal/mercenary/variant records, not that many supported player skills. They do not prove obtainability or runtime support. A cross-check of `GrantedEffectStatSetsPerLevel` AdditionalStats/AdditionalFlags/FloatStats found no additional references to these spell-to-attack contracts.

| Source | Exported behavior | Baseline gap / coverage intent | This delivery |
|---|---|---|---|
| Kinetic Blast / AltX / AltY | Skill-local 150% | Unify existing skill, direct-hit, explosion and panel calculations | integrate existing executable paths |
| Crown of Eyes | Actor-wide fixed 150% | Modifier exists, but no curated unique definition; add normal item and equip/remove/non-stacking checks | normal item and integration |
| Whisperer `your mind is destroyed`, graph 50932 | Actor-wide fixed 150% | Preserve existing consumer/regressions; Phrecia allocation/client compatibility remains separate | existing consumer; allocation/client work excluded |
| Wand mastery `Wand4`, effect row 328 / hash 61352 | 100% while wielding a wand | Missing consumer; test real mastery, active weapon changes and refund | consumer and existing mastery lifecycle |
| Generic percentage stat | Actor-wide actual percentage | Missing consumer and correct independent-source aggregation | shared consumer and aggregation |
| Foulborn Crown modifier `DivergentSpellDamageModifiersApplyToAttackDamageUniqueHelmetInt7` | Actor-wide 100% flag | Support the stat; full variant creation/acquisition is outside this delivery | 100% stat consumer; full variant creation/acquisition excluded |
| `MutatedUniqueHelmetInt10AdditiveSpellModifiersApplyToRetaliationAttackDamage` | Retaliation-only 200% | Support the conditional consumer on existing paths; new item/retaliation lifecycles are outside this delivery | conditional stat consumer; new item/skill lifecycles excluded |
| PowerSiphon / AltX / AltY / VaalPowerSiphon / PowerSiphonMercenary | Skill-local 150% | Shared calculation and isolation; full execution depends on each skill's support | calculation and existing execution; new full skills excluded |
| KineticBolt / AltY / KineticBoltMercenary | Skill-local 200% | Same shared calculation, including base-plus-quality aggregation | calculation/quality and existing execution; new full skills excluded |
| KineticRain / KineticRainKineticInstability / AltX / AltXMercenary | Skill-local 200% | Shared calculation; internal records are not automatically enabled player skills | calculation and existing execution; new full skills excluded |
| HeavyStrikeAltY / HeavyStrikeAltMercenary; SomaticShell | Skill-local 150% | Shared calculation; verify identity before public skill-support claims | calculation and existing execution; new full skills excluded |
| KineticFusillade / AltX; TriggeredSupportKineticInstability / GreaterKineticInstability | Skill-local 150% | Shared calculation; preserve triggered-child scope when execution exists | calculation and existing child paths; new full skills excluded |
| GigaKineticBlastMercenary / KineticBlastAltMercenary | Skill-local 200% | Separate these from player KB; no new mercenary runtime in this delivery | data isolation; new mercenary runtime excluded |
| Quality: HeavyStrikeAltY; KineticBolt / AltY | +1 / +2.5 ratio points per 1% quality | Verify permille interpretation: KBolt 20% quality gives a 250% candidate, then compare with global grants | shared quality calculation |
| ChargedDashSpellScalingMercenary / Charged Dash of the Arcane | Mercenary-specific implicit flag | Current skill description explicitly applies spell damage increases/reductions to this attack; not an identified player-item legacy source | mercenary execution outside this delivery |

Rows group related records; counts above refer to unique source records. Preserve an explicit disposition for each row: integrated on an existing path, calculation/stat-consumer only, or excluded. Full unsupported skill execution, variant acquisition and Phrecia allocation are outside this issue. Raw-stat tests cannot establish those lifecycles as complete.

### Stat contracts

Client keys are one-based Stats rows for the inspected data, not version-independent identifiers.

| Client key | Stats.Id | Meaning |
|---|---|---|
| 2493 | `additive_spell_damage_modifiers_apply_to_attack_damage` | Modern 100% flag |
| 11810 | `additive_spell_damage_modifiers_apply_to_attack_damage_at_%_value` | Actor-wide percentage |
| 11811 | `additive_spell_damage_modifiers_apply_to_attack_damage_at_150%_value` | Fixed 150% flag |
| 11812 | `additive_spell_damage_modifiers_apply_to_attack_damage_at_%_value_while_wielding_wand` | Wand-conditional percentage |
| 11813 | `additive_spell_damage_modifiers_apply_to_retaliation_attack_damage_at_%_value` | Retaliation-conditional percentage |
| 11814 | `active_skill_additive_spell_damage_modifiers_apply_to_attack_damage_at_%_value` | Skill-local percentage, including quality contribution |
| 1655 | `spell_damage_modifiers_apply_to_attack_damage` | Mercenary-specific implicit contract; outside this delivery |

`spell_damage_modifiers_apply_to_skill_dot` is a different mechanic and is excluded.

Key 1655 is used by `ChargedDashSpellScalingMercenary` (stat-set 9470, GrantedEffects 9485), linked to MercenarySkills row 93, **Charged Dash of the Arcane**. Its description explicitly applies spell damage increases/reductions to this attack. No references were found in the inspected item Mods/player passive/mastery tables; this does not prove retirement or universal absence of other uses. It is excluded because its mercenary execution is outside scope. [Crown's version history](https://www.poewiki.net/w/index.php?title=Crown_of_Eyes&oldid=1674324#Version_history) says its INC/RED-only and 150% changes affected existing items; legacy accuracy/leech variants are not evidence of retained pre-change spell scaling.

## Main-code evidence

Inspected revision: `5d5b5150e1b2e287c642edcaf75a49d075b9b4fe`. The following are historical baseline findings addressed by the bounded implementation below.

- [DamageStats.SpellAttackIncrease](https://github.com/Wrathen/LocalExile/blob/5d5b5150e1b2e287c642edcaf75a49d075b9b4fe/src/GameServer.Gateway/DamageStats.cs#L17): name-query overload compares fixed 150% and active-skill ratio; dictionary overload only reads fixed 150%. Neither handles the other modern contracts.
- [MergeSkillStats](https://github.com/Wrathen/LocalExile/blob/5d5b5150e1b2e287c642edcaf75a49d075b9b4fe/src/GameServer.Gateway/Program.GemSupports.cs#L93), [ItemModStats.Sum](https://github.com/Wrathen/LocalExile/blob/5d5b5150e1b2e287c642edcaf75a49d075b9b4fe/src/GameServer.Gateway/ItemModStats.cs#L17) / Plus and [PassiveTree](https://github.com/Wrathen/LocalExile/blob/5d5b5150e1b2e287c642edcaf75a49d075b9b4fe/src/GameServer.Gateway/PassiveTree.cs#L61): generic aggregation adds independent numeric grants before the consumer can select a maximum. Preserve the distinction between separate grants and a skill's own base-plus-quality improvement.
- [SkillCatalog.Unarmed.HitDamage](https://github.com/Wrathen/LocalExile/blob/5d5b5150e1b2e287c642edcaf75a49d075b9b4fe/src/GameServer.Gateway/SkillCatalog.cs#L629) scales physical damage, then appends LocalAdded without those increases. [SupportedAttackDamage.Hit](https://github.com/Wrathen/LocalExile/blob/5d5b5150e1b2e287c642edcaf75a49d075b9b4fe/src/GameServer.Gateway/SupportedAttackDamage.cs#L5) omits weapon.LocalAdded. This directly blocks correct Arcane Might scaling of weapon-local damage.
- [CaptureGemProjectile](https://github.com/LocalExile/LocalExile/blob/5d5b5150e1b2e287c642edcaf75a49d075b9b4fe/src/GameServer.Gateway/Projectiles/Program.ProjectileRelease.cs#L65) captures direct and area offense at release. Preserve that timing while unifying the Arcane Might calculation.
- [UniqueCatalog](https://github.com/Wrathen/LocalExile/blob/5d5b5150e1b2e287c642edcaf75a49d075b9b4fe/src/GameServer.Gateway/UniqueCatalog.cs#L14) only defines Mageblood. Adding Crown's real item stats must not be presented as implementing otherwise-missing leech behavior.

</details>

## Implementation coverage

`ArcaneMight.cs` is the shared ratio consumer; existing damage constructors and release captures keep their timing.
Independent item/passive/actor/skill merges use flag union or numeric max only for these six grants.
A skill's own `EffectStats` base-plus-quality calculation is preserved.
The stat layer still supplies resolved spell increased/reduced; no modifier-source reconstruction is introduced.

| Inventory row | Delivered disposition / evidence |
|---|---|
| KB / AltX / AltY | Existing supported attack and direct/area capture paths share the consumer; all constants checked. Normal KB has actual gateway target-Life tests. No new variant executor. |
| Normal Crown | Curated current six-mod definition; real equip/remove, save reload, non-stacking with KB, ratio-free ordinary attack enable/remove and stronger-grant fallback checked. Native acceptance pending; leech is not implemented here. |
| Whisperer | Existing node/consumer regressions retained; allocation and Phrecia compatibility excluded. |
| Wand mastery | Real Wand4 selection/refund and active/inactive weapon checks; consumer and default panel parity. |
| Generic ratio | Shared dictionary/name-query consumers and independent-source maximum tests. |
| Foulborn Crown modifier | Real mod extraction and 100% consumer checked; full item/acquisition excluded. |
| Mutated retaliation modifier | Real mod extraction, 200% conditional consumer and ActiveSkillType 120 eligibility checked; new item/skill execution excluded. |
| Power Siphon group | All five numeric constants checked through real EffectStats and shared consumer; new full executors excluded. |
| Kinetic Bolt group | All three constants and applicable quality checked; same existing supported-attack constructor. No new execution admission. |
| Kinetic Rain group | All four constants checked; internal/mercenary records do not enable player skills. |
| Heavy Strike variants / SomaticShell | Three constants checked; HeavyStrikeAltY quality checked. New execution excluded. |
| Kinetic Fusillade / triggered supports | All four constants checked through isolated skill stat dictionaries; existing child orchestration unchanged. No new triggered executors. |
| Mercenary KB variants | Both constants checked for correct 200% calculation and isolation; runtime excluded. |
| Three quality records | KBolt/AltY 20% => 250%; HeavyStrikeAltY 20% => 170%; KBolt 1% => 202% under existing integer-stat truncation. |
| Charged Dash of the Arcane | Key 1655 consumer and mercenary execution excluded; no player-item retirement claim. |

Production-path checks compare independent expected results with damage packets, panels and real target Life loss.
For KB level 1 (140% effectiveness), fixed 100 chaos with 100% spell increase and 150% Arcane Might yields
350 chaos plus the wand's scaled physical damage; both direct-only and area-only runtime routes verify Life loss, with the explosion alone taking 40% less damage.
Changing spell increase after capture changes subsequent panels/hits but leaves that released packet unchanged.
Client acceptance exposed an existing effectiveness decoding error: raw 4000 is +40% above 100%, not 40%.
The reader now normalizes the offset, matching pinned PoB Export/Scripts/skills.lua lines 311–321.
Earlier tests and examples that assumed 40% were wrong and have been corrected.
Local fire is included before effectiveness and support restrictions; Brutality clears both actual and displayed fire.
No extracted tables, client resources or unrelated integration work are part of this change.

Native client acceptance remains user-owned; automated source/consumer coverage is not universal interaction parity.
Known limitations above, especially Iron Will/Strength provenance and upstream resolved-input fidelity, still apply.
Build/test evidence is recorded in the local execution ledger under `logs/arcane-might/`.

## References

- [Arcane might, wiki revision 1702775](https://www.poewiki.net/w/index.php?title=Arcane_might&oldid=1702775), 2026-09-27, library snapshot 2026-10-13: highest-source rule, exclusions, conditional modifiers and Strength anti-double-dipping.
- [PoB CalcOffence.lua](https://github.com/PathOfBuildingCommunity/PathOfBuilding/blob/16de4b82d57f1c0de6eb40f37143c32d4da36a02/src/Modules/CalcOffence.lua#L650): maximum ratio, INC-only copying with source/flag/condition preservation and explicit Strength anti-double-dipping. Pinned 2.67.2 development / 3.29 fallback; exact 3.29.3b parity unverified.
- Extracted client tables establish stat identities/values, not authoritative execution order or obtainability. Do not commit extracted data.
