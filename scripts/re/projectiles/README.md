# Projectile research tools (3.29.3b)

Start with [RE 012](../../../Agents/docs/re/012-projectile-execution.md) and its delivery matrix.
These tools support the fixed research; they do not implement server gameplay or a whole-client emulator.

## Offline verification

From repository root, with Python 3 and Node.js available:

```powershell
python scripts/re/projectiles/verify_delivery.py
```

No third-party Python packages, game files, running server or client are needed for this command.
Every child check must exit zero; a failure stops the command with a nonzero status.
Do not run with Python optimization (`-O`), which disables assertions in the original reference self-tests.

| Check | Evidence / supported scope |
|---|---|
| `native_tornado_rng.py --self-test` | Eight-word initialization, shipped variant transition, rejection and binary32 angle vectors |
| `native_projectile_rng.py --self-test` | Four/five/seven-word initialization, object hash, finite distributions and explicit scalar TLS controls |
| `native_projectile_raster.py --self-test` | Finite ordinary nonoverflow integer sampler; fixed ties and independent closed-form arithmetic comparison |
| `python -m unittest discover -s scripts/re/projectiles -p test_delivery_vectors.py` | Published ignored-evidence RNG vectors, including nondefault keys; no PE execution |
| `python -m unittest discover -s scripts/re/projectiles -p test_execution_capture.py` | Synthetic trace validation, identity/order/coverage/truncation diagnostics |
| `python -m unittest discover -s scripts/re/projectiles -p test_runner.py` | Mocked Frida lifecycle, finish-before-detach, load failure and externally detached handling |
| `node --test scripts/re/projectiles/test_execution_probe.js` | Mocked observer callbacks, budgets/finish and defensive reads |

Scalar/reference checks validate translations and declared input boundaries.
They do not establish native trig, actual TLS scope/order, real skill inputs, official packets or end-to-end parity.
The raster reference excludes timing, arc/sentinel height, terrain, queries and overflow inputs.
The explicit TLS scalar helper does not discover the actual incoming thread state.

## Fixtures and provenance

`fixtures/terminal-rng.json` is a minimal projection of preexisting ignored static-interpretation vectors.
It records input words, warm state, raw outputs and angle results with its source SHA-256.
Its expected values come from the retained original report, not regeneration from the reference under test.
`fixtures/evidence-receipt.json` records the source freeze, original file hashes, selected KB/TS outcomes and
explicit private-audit boundaries without instruction bytes or interpreter dependencies.
Those outcomes are review receipts, not replayable native fixtures; the offline command does not pretend to
execute KB target queries, timer lifecycle or TS retirement from them.

## Optional observation tooling

The bounded [observer](../frida/trace-projectile-execution.js),
[runner](../frida/run.py) and [capture contract](../../../Agents/docs/re/projectiles/capture-contract.md) describe
a separately authorized future recording workflow.
It retains short hook-site byte signatures for version validation, not function bodies or disassembly dumps.
The probe observes selected methods and emits JSONL; it neither changes projectile decisions nor sends packets.
The optional runner `--finish-rpc finish` requests a final coverage marker before normal detach.
Unexpected process/session termination may leave an incomplete recording and is reported as such.
The runner keeps main's detach notification, stop event and serialized callback logging.
No live attach is performed by the offline entry point or claimed by this delivery.

The analyzer can inspect a separately supplied authorized capture:

```powershell
python scripts/re/projectiles/analyze_execution_capture.py logs/projectile-execution.jsonl
```

Real recordings can contain identities, paths or unrelated trace messages; no such recording is included here.
