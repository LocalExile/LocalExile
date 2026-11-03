"""Offline timing analysis. Never attributes nearby loot to a corpse automatically."""
import argparse
import json
from pathlib import Path
import statistics


def analyze(records):
    errors = []
    starts = [r for r in records if r.get("event") == "start"]
    finishes = [r for r in records if r.get("event") == "finish"]
    events = [r for r in records if r.get("lootTiming") == 1]
    if len(starts) != 1 or len(finishes) != 1:
        errors.append("need exactly one observer start and finish; capture may be interrupted/appended")
    frequency = int(starts[0]["frequency"]) if len(starts) == 1 else 0
    if frequency <= 0:
        errors.append("missing/invalid QPC frequency")
    if [r.get("n") for r in events] != list(range(len(events))):
        errors.append("missing, duplicate, or reordered sequence records")
    if any(r.get("event") in ("error", "host_error", "unexpected_detach") for r in records):
        errors.append("observer/host/detach errors present")
    if finishes and any(finishes[0].get(k, 0) for k in ("errors", "dropped", "pending")):
        errors.append("observer reports errors, dropped events, or unclosed native scopes")
    if finishes and finishes[0].get("activeTrial") is not None:
        errors.append("capture ended with an armed trial")
    manifests = [r for r in records if r.get("event") == "manifest"]
    if len(manifests) != 1:
        errors.append("missing or duplicate capture manifest")
    if any(r.get("event") in ("trial_begin", "trial_end", "item_construct", "life_zero_apply", "chest_open_apply")
           and r.get("trial") is not None and not any(b.get("event") == "trial_begin" and b.get("trial") == r["trial"]
                                                    for b in events) for r in events):
        errors.append("orphan trial records")
    intake = {r["key"]: r for r in events if r["event"] == "intake"}
    dispatch = {r["key"]: r for r in events if r["event"] == "dispatch" and r.get("key") is not None}
    trials = []
    for begin in (r for r in events if r["event"] == "trial_begin"):
        number, kind = begin["trial"], begin["kind"]
        subset = [r for r in events if r.get("trial") == number]
        problems = []
        if len([r for r in subset if r["event"] == "trial_end"]) != 1:
            problems.append("trial not explicitly closed")
        event_name = "life_zero_apply" if kind in ("kill", "pack") else "chest_open_apply"
        all_sources = [r for r in subset if r["event"] in ("life_zero_apply", "chest_open_apply")]
        sources = sorted((r for r in all_sources if r["event"] == event_name), key=lambda r: int(r["q"]))
        if kind == "pack":
            if not sources or len(all_sources) != len(sources):
                problems.append("pack needs monster deaths and no competing chest opens")
        elif len(all_sources) != 1 or len(sources) != 1:
            problems.append("need exactly one source transition and no competing deaths/opens")
        source = sources[0] if sources and (kind == "pack" or len(sources) == 1) else None
        if source and begin.get("source") is not None and source["id"] != begin["source"]:
            problems.append("source ID differs from armed ID")
        if kind in ("kill", "pack") and any(not (s.get("path") or "").startswith("Metadata/Monsters/") for s in sources):
            problems.append("life-zero source not positively identified as monster")
        items = sorted((r for r in subset if r["event"] == "item_construct"), key=lambda r: int(r["q"]))
        item_ids = [r["id"] for r in items]
        if len(set(item_ids)) != len(item_ids):
            problems.append("item ID reused within trial")
        if kind != "pack" and any(r.get("event") == "construct" and (r.get("path") or "").startswith("Metadata/Monsters/") for r in subset):
            problems.append("monster construction during trial: possible area-load/spawn contamination")
        metrics = {}
        if source and frequency > 0:
            expected_op = 0x1b1 if kind in ("kill", "pack") else 0x1bd
            for lookup in (intake, dispatch):
                if any((matched := lookup.get(s.get("key"))) is not None and
                       (matched.get("id") != s["id"] or matched.get("op") != expected_op) for s in sources):
                    problems.append("source message identity mismatch")
                if any((matched := lookup.get(item.get("key"))) is not None and
                       (matched.get("id") != item["id"] or matched.get("op") != 0x2b9) for item in items):
                    problems.append("item construction did not match a create message")
            stages = {"decoded_intake": (intake, [intake.get(s.get("key")) for s in sources]),
                      "dispatch": (dispatch, [dispatch.get(s.get("key")) for s in sources]),
                      "construct_from_source_apply": (None, sources)}
            for stage, (lookup, references) in stages.items():
                selected = [lookup.get(r.get("key")) if lookup is not None else r for r in items]
                if any(r is None for r in references + selected):
                    problems.append(f"unmatched message at {stage}")
                    continue
                ordered = sorted(zip(items, selected), key=lambda pair: int(pair[1]["q"]))
                times = [int(r["q"]) for _, r in ordered]
                stage_ids = [item["id"] for item, _ in ordered]
                origin = min(int(r["q"]) for r in references)
                last_source = max(int(r["q"]) for r in references)
                delays = [(q - origin) * 1000 / frequency for q in times]
                gaps = [(b - a) * 1000 / frequency for a, b in zip(times, times[1:])]
                metrics[stage] = {"delay_ms": delays, "spacing_ms": gaps,
                                  "reference": "first pack death" if kind == "pack" else "single source",
                                  "delay_from_last_source_ms": [(q - last_source) * 1000 / frequency for q in times],
                                  "item_order": stage_ids, "order_differs_from_construction": stage_ids != item_ids,
                                  "items_before_source": sum(t < 0 for t in delays),
                                  "first_ms": delays[0] if delays else None,
                                  "burst_ms": (times[-1] - times[0]) * 1000 / frequency if times else None}
            # Detect same IDs' create messages arriving before the trial was armed.
            if any(intake.get(r.get("key"), {}).get("trial") != number for r in items):
                problems.append("item intake outside armed trial")
            if any(intake.get(s.get("key"), {}).get("trial") != number for s in sources):
                problems.append("source intake outside armed trial")
        trials.append({"trial": number, "kind": kind, "source_id": source["id"] if source else None,
                       "source_ids": [s["id"] for s in sources],
                       "source_path": source.get("path") if source else None, "items": item_ids,
                       "valid": not problems and not errors, "problems": problems, "metrics": metrics,
                       "attribution": "pack-level only; individual item ownership unknown" if kind == "pack" else
                                      "isolated-trial candidate, not a protocol source-to-loot link"})
    if len({t["trial"] for t in trials}) != len(trials):
        errors.append("duplicate trial identities")
    if errors:
        for trial in trials:
            trial["valid"] = False
    summary = {}
    for kind in ("kill", "chest", "pack"):
        good = [t for t in trials if t["kind"] == kind and t["valid"]]
        firsts = [t["metrics"]["decoded_intake"]["first_ms"] for t in good
                  if t["metrics"].get("decoded_intake", {}).get("first_ms") is not None]
        summary[kind] = {"valid_trials": len(good), "zero_loot_trials": sum(not t["items"] for t in good),
                         "median_first_intake_ms": statistics.median(firsts) if firsts else None}
    return {"complete": not errors, "errors": errors, "reference": manifests[0].get("reference") if manifests else None,
            "clock": "QPC; milliseconds are observed client intervals, not server timers or render times",
            "summary": summary, "trials": trials}


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("capture", type=Path)
    ap.add_argument("--out", type=Path)
    args = ap.parse_args()
    try:
        with args.capture.open(encoding="utf-8-sig") as stream:
            records = [json.loads(line) for line in stream if line.strip()]
        report = analyze(records)
    except (ValueError, KeyError, TypeError) as exc:
        raise SystemExit(f"Invalid capture: {exc}") from exc
    encoded = json.dumps(report, indent=2, ensure_ascii=False)
    if args.out:
        with args.out.open("x", encoding="utf-8") as stream:
            stream.write(encoded + "\n")
    for kind, result in report["summary"].items():
        print(f"{kind}: {result['valid_trials']} valid, {result['zero_loot_trials']} zero-loot; "
              f"median first decoded intake = {result['median_first_intake_ms']} ms")
    for trial in report["trials"]:
        print(f"Trial {trial['trial']} {trial['kind']}: {'VALID' if trial['valid'] else 'INVALID'}; "
              f"{len(trial['items'])} items; {', '.join(trial['problems'])}")
        if trial['valid']:
            for stage, values in trial['metrics'].items():
                print(f"  {stage}: delay ms {values['delay_ms']}; spacing ms {values['spacing_ms']}")
                if trial['kind'] == 'pack':
                    print(f"    first pack death reference; from last death ms {values['delay_from_last_source_ms']}")
    for error in report["errors"]:
        print("INVALID:", error)
    if not args.out:
        print(encoded)
    return 0 if report["complete"] and all(t["valid"] for t in report["trials"]) else 1


if __name__ == "__main__":
    raise SystemExit(main())
