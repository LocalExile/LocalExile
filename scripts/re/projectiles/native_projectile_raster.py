#!/usr/bin/env python3
"""Offline reference for the ordinary integer path in PoE1 3.29.3b.

Reconstructs the axis ordering, error accumulators and strict >0 tie rule from
0x141e4d180/0x141e4d390. Inputs are integer cells and an optional integer height
marker, not rendered world Z. The source sample is excluded (birth overlap is
handled elsewhere); the endpoint is included.

This is a bounded static reconstruction, not native execution or live parity.
It excludes timing, terrain-height sentinels, parabolic overrides, interpolation,
collision queries, overflow behavior, skills and networking. No client is read.
"""

from __future__ import annotations

import argparse
import itertools
import json
from typing import Iterator, Sequence


def ordered_axes(delta: Sequence[int]) -> list[int]:
    axes = [0, 1] if abs(delta[0]) >= abs(delta[1]) else [1, 0]
    if len(delta) == 3:
        axes.append(2)
        if abs(delta[2]) > abs(delta[axes[0]]):
            axes[0], axes[2] = axes[2], axes[0]
    return axes


def samples(start: Sequence[int], end: Sequence[int]) -> Iterator[tuple[int, ...]]:
    """Yield ordinary nonzero-length path samples, without a frame clock.

    The signed int32 squared-distance guard deliberately rejects overflow cases
    instead of guessing how invalid or extreme inputs reach the native helper.
    """
    if len(start) not in (2, 3) or len(start) != len(end):
        raise ValueError('start/end must both contain two or three integers')
    if any(type(v) is not int or not -(1 << 31) <= v < (1 << 31) for v in (*start, *end)):
        raise ValueError('coordinates must be signed int32 integers')
    if len(start) == 3 and (start[2] == 0x7fffffff or end[2] == 0x7fffffff):
        raise ValueError('terrain-height sentinel is outside this reference')
    delta = [b - a for a, b in zip(start, end)]
    if sum(d * d for d in delta) > 0x7fffffff:
        raise ValueError('squared-distance overflow is outside this reference')
    axes = ordered_axes(delta)
    major = axes[0]
    length = abs(delta[major])
    if not length:
        raise ValueError('zero-length initialization is outside this reference')
    position = list(start)
    signs = [1 if d > 0 else -1 for d in delta]
    errors = {axis: -(length // 2) for axis in axes[1:]}
    for _ in range(length):
        for axis in axes[1:]:
            errors[axis] += abs(delta[axis])
            if errors[axis] > 0:
                position[axis] += signs[axis]
                errors[axis] -= length
        position[major] += signs[major]
        yield tuple(position)


def self_test() -> dict:
    # In particular, a half-cell tie stays on the old minor coordinate.
    fixed = (
        ((0, 0), (4, 2), [(1, 0), (2, 1), (3, 1), (4, 2)]),
        ((4, 2), (0, 0), [(3, 2), (2, 1), (1, 1), (0, 0)]),
        ((0, 0), (2, 4), [(0, 1), (1, 2), (1, 3), (2, 4)]),
        ((0, 0, 0), (2, 1, 4), [(0, 0, 1), (1, 0, 2), (1, 1, 3), (2, 1, 4)]),
    )
    for start, end, expected in fixed:
        assert list(samples(start, end)) == expected

    cases = 0
    # Compare the incremental translation with a separate closed-form oracle.
    # This checks arithmetic consistency, not observed native execution.
    for dims in (2, 3):
        for start in (tuple(0 for _ in range(dims)), tuple(-7 + i for i in range(dims))):
            for delta in itertools.product(range(-4, 5), repeat=dims):
                if not any(delta):
                    continue
                end = tuple(a + d for a, d in zip(start, delta))
                length = max(map(abs, delta))
                bias = (length - 1) // 2
                expected = [tuple(a + (1 if d > 0 else -1) * ((k * abs(d) + bias) // length)
                                  for a, d in zip(start, delta)) for k in range(1, length + 1)]
                actual = list(samples(start, end))
                assert actual == expected, (start, end, actual, expected)
                assert actual[-1] == end
                cases += 1
    for start, end in (((0, 0), (0, 0)), ((0, 0), (50000, 0)),
                       ((0, 0, 0x7fffffff), (1, 1, 0x7fffffff))):
        try:
            list(samples(start, end))
        except ValueError:
            pass
        else:
            raise AssertionError('unsupported input accepted')
    return {'result': 'passed', 'fixed_paths': len(fixed), 'closed_form_cases': cases,
            'scope': 'static reconstruction arithmetic; not client execution'}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--self-test', action='store_true')
    parser.add_argument('--start', nargs='+', type=int)
    parser.add_argument('--end', nargs='+', type=int)
    args = parser.parse_args()
    if args.self_test:
        print(json.dumps(self_test(), indent=2))
        return
    if args.start is None or args.end is None:
        parser.error('--start and --end are required unless using --self-test')
    try:
        path = list(samples(args.start, args.end))
    except ValueError as error:
        parser.error(str(error))
    print(json.dumps({'start': args.start, 'end': args.end, 'samples': path,
                      'includes_source': False, 'scope': 'ordinary integer raster only'}, indent=2))


if __name__ == '__main__':
    main()
