# Verification boundaries and finite followups

Part of [RE 012](../012-projectile-execution.md).
This page defines what the delivery proves and where implementation must retain an explicit assumption.
Unknowns do not automatically authorize another reverse-engineering round.

## 1. Reproducible public checks

Run from repository root with Python 3 and Node.js:

```powershell
python scripts/re/projectiles/verify_delivery.py
```

This runs the three scalar references, explicit public vectors, synthetic capture/analyzer tests and mocked
probe tests without reading a client, attaching a process or using game data.
Individual commands and vector provenance are in [the tools README](../../../../scripts/re/projectiles/README.md).

| Evidence | What can be checked | What is not established |
|---|---|---|
| Reference algorithm / T | Explicit integer/binary32 seed, hash and raster vectors; boundary rejection; synthetic trace diagnostics | Actual native invocation, trig/geometry, real stats, scheduling or damage |
| Static / S | Exact named inputs, ordered reads, final factory bindings, branch formulas and state transitions in the pinned version | Runtime frequency, selected actual values, completeness outside audited ranges |
| Original instructions / E | Historical local instruction interpretation with explicit input/storage/API fixtures | Full native process execution, unmodeled external APIs, official server behavior or a public rerun of the private graph |
| Observation / O | Exactly the cited capture/client/version/inputs | All skills, modes or server packet choices; there is no new recording in this delivery |
| Project source / L | Behavior at the stated LocalExile source revision | Official behavior or an executable fact |

The sanitized [evidence receipt](../../../../scripts/re/projectiles/fixtures/evidence-receipt.json) records selected
private audit source hashes and case boundaries without copying game instructions or the ancestor harness graph.
Checking that receipt is not re-executing those instructions.
Most deep joins remain S/E reference evidence rather than self-contained public native replay tests.

## 2. Critical correctness gaps

| ID | Unknown and implementation decision affected | Smallest followup evidence | Stop condition |
|---|---|---|---|
| K1 | Gameplay-affecting TLS initial state, enclosing scope and consumer order. Blocks deterministic TLS-dependent Pierce/Chain outcomes and any claim that deferring visuals preserves gameplay draws. | One fixed witness reaching one selected TLS gate: establish incoming TLS state, enclosing scope identity and preceding/reached draw order; compare with existing conditional helper contract. | Either close that one witness's state/order contract or document the exact missing producer/order edge. Do not expand into every engine/TLS consumer. |
| K2 | Official Fireball direct and extra-area target result route. Blocks official per-target feedback/queue routing and deductions about direct-plus-area double hits. | One ordinary official Fireball event with fixed action identity, direct target and a nearby area-only target, showing decoded result kinds/keys/control order plus independent resource outcomes. Existing immediate/queued-control paths are candidates, not observed choices. | Identify the route for that event or state that the capture cannot disambiguate it. No blanket client reporting or numerical-damage-engine investigation. |
| K3 | Necessary effective inputs and read/update timing for each of the six witnesses. Blocks real-input/full-skill parity even when getter formulas are closed. | For one witness, one ordinary release and continuation: match final class, selected views, aliases, row, hand, repeat/ordinals, model/mover/frame inputs and result timing to the existing read points. | Complete that bounded witness input table or list precise missing fields/update edges. Repeat only for the other five named witnesses, without tracing every stat/Buff producer. |

K3 has six finite records: K3-Bow, K3-Wand, K3-Fireball, K3-KB, K3-TS and K3-BA.
They share [the input table](client-server-contract.md), but none is marked complete merely because another class
uses the same getter.
Stable supplied provider values, a constructor default or an enabled capability stat do not prove official publication.

These gaps can coexist with a research PR containing closed arithmetic and transitions.
They cannot be relabeled presentation-only, deleted, or described as solved by generated fixtures.

## 3. Extensions outside the present witness scope

- All monster/trap/mine/totem/remote-owner action paths and every transfigured/Vaal skill.
- Every exotic movement, support combination and special contact-object interaction.
- Full numerical hit/mitigation/resource, Buff/DoT, fee/cooldown/soul and stat-producer systems.
- General prediction ownership policy beyond verified classes/modes/inputs.
- Exceptional/unreachable numeric inputs beyond explicit reference-tool domains.

Existing positive findings about these paths remain in the detailed references.
They do not enlarge the six-witness delivery or justify claiming all projectile skill coverage.
Numerical damage and resource ownership are distinct from local contact-memory correctness.

## 4. Presentation and full-engine details

Complete MTX/render/attachment behavior, every effect asset, populated component teardown, allocator internals,
complete Entity removal draining and the entire engine task graph are not completion conditions here.
R81's source retirement and surviving descendant histories remain valid inside their fixture boundary.
They are not a claim that the full Entity lifecycle or official scheduling has been restored.
If any deferred path shares gameplay TLS draws, retain its consumption/order requirement under K1.

Rendered size is not an independent numerical damage measurement.
Client height defaults/sentinels and bow/model attachments must not be replaced with a universal visual offset.

## 5. Implementation handoff rules

Use [the matrix](delivery-matrix.md) to choose the next implementation scope, with explicit K1/K2/K3 assumptions.
Keep client executable facts, public skill descriptions, official observations and provisional server policy separate.
Do not suppress a server phase until its client inputs, geometry and lifecycle agree; removing duplicate visuals
alone cannot verify matching collision or damage.
No server changes, deployment, client attach, CI notification or remote publishing are part of this delivery.
