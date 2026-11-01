# First client damage baseline: delivery acceptance

This audit applies to the [bounded first delivery](client-damage-delivery.md),
not completion of the original R2–R4 investigation. The
[baseline guide](client-damage-baseline.md) is the review entry.

## Claim-by-claim decisions

| Gate / claim | Inspected evidence and result |
|---|---|
| D0: finite scope and honest follow-ups | Workbench, PR #371 and issue #370 distinguish this delivery from the umbrella investigation. Original criteria record remains SHA256 `74a47612048be28c7f1bf5400e72738d551c80c9ffee44e388eb8f0be1a1f6b9`; R2/R3/R4 remain Partial |
| D1: received DoT rate → Life | Inspected R29 inputs, ordered notification traces, actual final-key reads and five two-step witnesses. Values match the guide, including explicit-zero precedence, pair order, raw-key separation and captured old recovery. Rechecked original instructions and input identities; did not infer source-instance publication |
| D1: correction, carry and changes | Inspected four R27 witnesses: each selector's assignment-to-250 followed by gain-to-253/carry `0.5499999523`, plus zero-recovery assignment retaining `0.25`. Setter stores, separate revival exception and checkpoint placement are documented. R32 witnesses separately retain loss after Buff removal/clock expiry and stop it by Stats update while a Buff remains |
| D2: actual Fireball result path | Inspected all 42 retained R53 cases, original consumer call, saved-register assertion, before/after pools, assignment inputs, visited setter store and queue cursors. Eighteen assignments apply; future stamp 6 and zero-motion cases retain their distinct outcomes. No accepted-Hit amount or causal hit-to-assignment link is asserted |
| D2: borrowed evidence | Rechecked all 106 fixed R60 snapshot files and both exact PR #373 Git source hashes. Capture data/decoder remain unavailable; reported counts/timing stay explicitly author-reported. No peer producer was rerun or peer file edited |
| D3: readable package and privacy | Short baseline, two annotated diagrams, formula map and evidence review distinguish core from supplementary material. Three machine JSON files default to collapsed GitHub diffs; both equation files are unchanged. Only two absolute support locators were made repository-relative in the public manifest; all support hashes/byte counts and numerical metadata remain unchanged. Public branch content was scanned for machine paths/usernames |
| D4: original evidence integrity | Fresh PE/Stats hashes match the pinned inputs. All 4,113 identities / 5,143 ranges map to the expected PE bytes; 309 pointer slots match offset, digest and target. All 1,276 sealed support digests match. Retained selected instruction bytes and 28 displayed original-code anchors were checked, including the peer's executable ranges |
| D4: documentation and tools | Repository-relative links across the PR resolve; new guide/workbench fragment links and balanced fences pass. Python syntax and CLI help pass; the included audit tool freshly decodes the getter, common pool helper and absolute setter (38/59/71 instructions, zero undecoded bytes). The Ghidra helper compiles against the installed Ghidra/JDK without opening a project |
| D4: integration and publication | Latest fetched main is `d377bd256036242d2fb43f53eb7110b4897e1862`, already an ancestor of the research branch; no rebase/merge or conflict resolution is needed. Whitespace checks pass. The PR validation section records the exact published head and final GitHub mergeability/readiness check |

## What the checks prove

The retained compositions and current audit support the guide's conditional
numerical and ordering statements. The fresh audit checks source bytes, metadata,
witness inputs/traces/results and borrowed-source identities; it does not rerun
all historical interpreters or replace their disclosed fixtures with runtime evidence.
The work is same-agent review, not independent-worker sign-off.

Local delivery records are `logs/client-damage-delivery-check.json` and
`logs/client-damage-tool-check/result.json`; these are ignored and not distributed.
The public manifest and linked reports preserve source identities and explain
how to inspect the published metadata with matching inputs. Local-only snapshots,
private captures and target binaries are not made available by citing their hashes.

No client execution/attachment, capture, server behavior change, game tests,
CI/deployment or shared-analysis mutation was performed. Such execution is outside
this research delivery. No source-selection/shared-cap publisher, final numerical
Hit application, generic clock refresh or Mod gameplay consumer was promoted to
Established. Those remain the [parked follow-ups](client-damage-delivery.md#follow-up-queue-not-required-for-this-pr).

Once the matching head is published, D0–D4 pass for this baseline and PR #371 may
be marked Ready. Do not merge it or close issue #370 as part of this acceptance;
stop rather than starting the follow-up queue.
