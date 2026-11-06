"""Compare opening-pack allocation hypotheses exactly; neither model recovers the official server algorithm."""
import argparse
from collections import defaultdict
from fractions import Fraction
from itertools import product
import json
from pathlib import Path

from semantics import table


def weighted_model(weights, minimum, maximum):
    """Current local policy: draw a variety independently for each of N members."""
    outcomes = defaultdict(Fraction)
    total = sum(weights)
    for count in range(minimum, maximum + 1):
        for indices in product(range(len(weights)), repeat=count):
            chance = Fraction(1, maximum - minimum + 1)
            for index in indices:
                chance *= Fraction(weights[index], total)
            outcomes[count, count != len(set(indices))] += chance
    return outcomes


def proportional_model(weights, minimum, maximum):
    """Unverified alternative: stochastic-round N * weight / 100 independently per entry."""
    outcomes = defaultdict(Fraction)
    for count in range(minimum, maximum + 1):
        partial = {(0, False): Fraction(1, maximum - minimum + 1)}
        for weight in weights:
            lower, remainder = divmod(count * weight, 100)
            updated = defaultdict(Fraction)
            for (members, duplicate), probability in partial.items():
                for amount, chance in [(lower, Fraction(100-remainder, 100)),
                                       (lower+1, Fraction(remainder, 100))]:
                    if chance:
                        updated[members+amount, duplicate or amount > 1] += probability * chance
            partial = updated
        for key, value in partial.items():
            outcomes[key] += value
    return outcomes


def summary(outcomes):
    if sum(outcomes.values()) != 1:
        raise ValueError("Model probabilities do not sum to one")
    counts = defaultdict(Fraction)
    for (count, _), chance in outcomes.items():
        counts[count] += chance
    return dict(count_probabilities={str(n): float(p) for n, p in sorted(counts.items())},
                expected_members=float(sum(n*p for n, p in counts.items())),
                duplicate_variety_probability=float(sum(p for (_, duplicate), p in outcomes.items() if duplicate)))


def study():
    packs, entries = table("MonsterPacks"), table("MonsterPackEntries")
    results = []
    for name in ["Act1-ZombieDrownedStrand", "Act1-SandspitterStrand"]:
        row, pack = next((i, p) for i, p in enumerate(packs) if p["Id"] == name)
        members = [e for e in entries if e["MonsterPacksKey"] == row]
        if not all(e["Flag"] and e["Weight"] > 0 for e in members):
            raise ValueError("Opening model assumes positive, true-flag entries")
        weights = [e["Weight"] for e in members]
        args = weights, pack["MinCount"], pack["MaxCount"]
        results.append(dict(pack=name, entry_weights=weights,
                            independent_weighted=summary(weighted_model(*args)),
                            independent_proportional_rounding_hypothesis=summary(proportional_model(*args))))
    return dict(packs=results, limitations=[
        "Assumes a uniform inclusive MinCount/MaxCount draw and no area or system pack-size scaling.",
        "Proportional rounding is an unverified hypothesis; its per-entry rolls are assumed independent.",
        "No bosses, rarity rolls, placement failures, capture loss, or observation-based grouping are modeled.",
        "Matching a possible group size does not identify an allocation rule or its RNG."])


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path, help="Private report path, normally under Tools/re/out")
    args = parser.parse_args()
    report = study()
    args.output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))
