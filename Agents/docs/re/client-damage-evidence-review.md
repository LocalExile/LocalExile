# Reviewing the client damage evidence

Read [the first baseline](client-damage-baseline.md) before opening the machine
inventory. The baseline and its linked composition reports contain the claims,
guards and numerical examples; the full investigation is supplementary.

## Three retained machine references

| File | What to inspect when a claim needs it |
|---|---|
| [client-damage-evidence.json](client-damage-evidence.json) | `binary` / `stats` identify inputs; `functions[address].ranges` bind original bytes and file offsets; `pointerSlots` bind dispatch; `supportingRecords` identify local reports by length/hash. Verification blocks summarize finite audits, not universal coverage |
| [client-attack-hit-equations.json](client-attack-hit-equations.json) | Ordered attack range operations and dependencies, read alongside [attack formula explanations](client-attack-hit-formulas.md) |
| [client-spell-hit-equations.json](client-spell-hit-equations.json) | Ordered spell range operations and dependencies, read alongside [spell formula explanations](client-spell-hit-formulas.md) |

Only these three files carry `linguist-generated=true` in `.gitattributes`, so
GitHub collapses their diffs by default. Expand an individual file or inspect the
raw JSON when needed. Evidence content, order and historical identities are
retained; two machine-specific support locators were made repository-relative
without changing their hashes or byte counts. No evidence has been minified,
removed or relocated to hide size; both equation files remain unchanged.
Handwritten reports and reproduction tools remain ordinary reviewable diffs.
The inventory has 4,113 identities, 5,143 ranges, 309 pointer slots and 1,276
support records. These are evidence counts, not a count of implemented mechanics.

## Audit trail for the two selected paths

All paths in this table are below ignored `Tools/re/out/client-damage-audit/`.
They identify existing sealed records, not files distributed by this PR.

| Baseline claim | Existing record and relevant inspection |
|---|---|
| Received final rate, ordered writes, explicit zero, notification and Life loss | `r29-network-rate-composition/{audit,verification,fresh-check}.json`: inspect `witnesses` for input pairs, notification traces and two-step current/carry; 474 composition controls plus 40 getter/fallback controls |
| Absolute correction plus subsequent integration | `r27-checkpoint-resources/{audit,verification,fresh-check}.json`: inspect `witnesses` for assignment stamp, selector, captured recovery, current/carry and queue cursor; 165 controls |
| Fireball result with independent resource assignment | `r53-result-join/{audit,verification,frontier,fresh-check}.json`: inspect each `cases` entry's assignments, actual consumer/ABI state, checkpoints, applied records and final pools; 16 baseline, 24 assignment and two zero-emission cases |
| Fixed upstream release path | `r53-peer-r60/manifest.json` and `coordination-r60-reuse.json`: 106 files at `4e9c9a39363a8de49c672d1c2c393a11e7a4fe20`, manifest SHA256 `0e79559abb7c02b71e7bdbb32c537d4ff342244b25b67d6209f6000cfd9618b5`; reused standalone numerical audit was not rerun |
| PR #373 comparison | `comparisonSources` in the public manifest pins two Git blobs at `b0afa90ea5c58e9082099d887180df8299dcdaf3`; [comparison report](client-damage-pr373-comparison.md) identifies author-reported observations versus independently bound instructions |

The delivery audit rechecks file identities, original PE ranges/pointers, cited
instructions and retained witness values. It does **not** rerun sealed producer
mains or reinterpret every historical numerical case. Witness review includes
input and trace assertions, not merely a stored `PASS` field. This remains
same-agent work, not an independent human or worker review.

## Availability and reproduction limits

The repository distributes analysis metadata, equations, bounded reproduction
tools and explanatory reports. It does not distribute `Game.exe`, extracted data,
full disassembly/decompilation, private captures or every local interpreter and
snapshot. A digest identifies a record; it does not make an unavailable record
publicly reproducible. Exact peer revisions retained locally are not promised
to be fetchable from GitHub. PR #373's raw captures and decoder were unavailable
for independent packet/timing checks.

With matching legally obtained inputs, [damage_audit.py](../../../scripts/re/damage_audit.py)
can reproduce selected original-byte identities; see
[bounded audit usage](tooling.md#bounded-damage-audit). Use a fresh output directory.
Do not overwrite sealed outputs or execute their producer mains. The included
Ghidra helper decompiles selected functions in an offline project; do not run it
against a shared analysis database. The delivery does not require a new target
execution or capture to validate the established static contracts.

## Acceptance interpretation

The first-delivery audit must verify the baseline's numerical/ordering claims,
links, public-path hygiene, unchanged evidence content, affected tools and exact
main/head relationship. The [workbench](client-damage-delivery.md) records the
gate decisions. No game build, real-client acceptance or official-server parity
is claimed for documentation/tool validation. Unknown actual instance publication,
numerical Hit application and generic clock refresh stay parked; original R2–R4
remain Partial after the first PR becomes Ready.
