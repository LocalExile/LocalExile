#!/usr/bin/env python3
"""Offline PoE 1 3.29.3b shared-emission RNG and object-seed reference.

This reads no game files and does not load or execute game code. It reconstructs
only the statically checked four/five/seven-word initializers, object hash,
scalar random-angle paths and local cosmetic-orientation sampling. Fixed
vectors were independently checked with an integer interpreter of retained
listings; they are not running-client comparisons.

Four words (14146d8f0), collision bounce:
  [sequence11a, emittingByte11c, incrementedBounceCount468, owner88]
Five words (141a0c9d0), batch angle/distance variance:
  [sequence11a, emittingByte11c, stage120, repeats2d0, releaseOrdinal3f8]
Seven words (141bba070), random-direction layout:
  [sequence11a, emittingByte11c, repeats2d0, field458, owner88,
   slot540Value, signedSlotAsUint32]
These are separate states. Other uses of either initializer may use a different
recipe. Volley mutates layout word 3 by +529 per side group before initialization.
This tool does not infer those inputs, mutation order, admission or skipped draws.

The inherited transition/bounded sampler is the shipped variant, not canonical
TinyMT32. Four/five-word initialization includes three/two no-input mix rounds;
none can be replaced with zero-padding to the TS eight-word initializer.
The native variance distribution samples bounded(3001); ordinary randomized
direction uses bounded(7001). Native float conversion is reconstructed; trig and
geometry are outside this tool. The TLS orientation helper takes explicit local
thread state, not an action seed. See Agents/docs/re/projectiles/actions-and-emission.md.

Examples:
  python scripts/re/projectiles/native_projectile_rng.py --self-test
  python scripts/re/projectiles/native_projectile_rng.py stream --words 1 2 0 0 1 --bound 3001
  python scripts/re/projectiles/native_projectile_rng.py object --sequence 1 --owner-id 4096 --parent-byte 0 --derived-byte 2 --ordinal 0
"""

from __future__ import annotations

import argparse
import json
import math
from typing import Sequence

from native_tornado_rng import MASK, MAT1, MAT2, TMAT, TerminalRandom, f32, u32


def require_uint(value: int, bits: int, name: str) -> int:
    """Reject bools, fractional values, negatives and silently truncated inputs."""
    if type(value) is not int or not 0 <= value < (1 << bits):
        raise ValueError(f'{name} must be a uint{bits}')
    return value


class ProjectileRandom(TerminalRandom):
    """Only the checked four/five/seven-word initializers; no implicit padding."""

    def __init__(self, words: Sequence[int]):
        words = tuple(words)
        if len(words) not in (4, 5, 7):
            raise ValueError('exactly four, five or seven uint32 seed words are required')
        for index, word in enumerate(words):
            require_uint(word, 32, f'words[{index}]')
        self.steps = 0
        state = [0, MAT1, MAT2, TMAT]

        def mix1(value: int) -> int:
            return u32((value ^ (value >> 27)) * 1664525)

        def mix2(value: int) -> int:
            return u32((value ^ (value >> 27)) * 1566083941)

        mixed = mix1(state[0] ^ state[1] ^ state[3])
        state[1] = u32(state[1] + mixed)
        mixed = u32(mixed + len(words))
        state[2] = u32(state[2] + mixed)
        state[0] = mixed
        index = 1
        for word_index in range(7):
            mixed = mix1(state[index] ^ state[(index + 1) % 4] ^ state[(index + 3) % 4])
            state[(index + 1) % 4] = u32(state[(index + 1) % 4] + mixed)
            word = words[word_index] if word_index < len(words) else 0
            mixed = u32(mixed + index + word)
            state[(index + 2) % 4] = u32(state[(index + 2) % 4] + mixed)
            state[index] = mixed
            index = (index + 1) % 4
        for _ in range(4):
            mixed = mix2(u32(state[index] + state[(index + 1) % 4] + state[(index + 3) % 4]))
            state[(index + 1) % 4] ^= mixed
            mixed = u32(mixed - index)
            state[(index + 2) % 4] ^= mixed
            state[index] = mixed
            index = (index + 1) % 4
        self.state = state
        for _ in range(8):
            self._transition()

    def bounded(self, exclusive_max: int) -> int:
        if type(exclusive_max) is not int:
            raise ValueError('exclusive_max must be an integer in 1..2**32')
        return super().bounded(exclusive_max)


def object_seed(sequence: int, owner_id: int, parent_byte: int,
                derived_byte: int, ordinal: int) -> int:
    """Pure uint32 hash at 14193a720 for ordinary admitted projectile objects.

    parent_byte belongs to the emitting action; derived_byte is the newly
    allocated byte before cloning. Resource-selected seed-zero bypass is the
    caller's responsibility. No random state or derived-byte allocator advances.
    """
    require_uint(sequence, 16, 'sequence')
    require_uint(owner_id, 32, 'owner_id')
    require_uint(parent_byte, 8, 'parent_byte')
    require_uint(derived_byte, 8, 'derived_byte')
    require_uint(ordinal, 32, 'ordinal')
    multiplier = 0x5BD1E995

    def mix(value: int) -> int:
        value = u32(value * multiplier)
        return u32((value ^ (value >> 24)) * multiplier)

    hashed = mix(sequence) ^ 0x22C1780B
    for word in (owner_id, parent_byte, derived_byte, ordinal):
        hashed = u32(hashed * multiplier) ^ mix(word)
    hashed = u32(hashed * 0x6DC79CAD)
    hashed ^= hashed >> 13
    hashed = u32(hashed * multiplier)
    return hashed ^ (hashed >> 15)


def finite_f32(value: float, name: str) -> float:
    if type(value) not in (int, float):
        raise ValueError(f'{name} must be a finite float32 value')
    try:
        if not math.isfinite(value):
            raise ValueError(f'{name} must be finite')
        converted = f32(value)
    except OverflowError as error:
        raise ValueError(f'{name} must fit float32') from error
    if not math.isfinite(converted):
        raise ValueError(f'{name} must fit float32')
    return converted


def float_distribution(random: ProjectileRandom, minimum: float, maximum: float) -> float:
    """Normal finite branch of 1404cd340/1404ce3f0, including quantized endpoints.

    Reject ranges whose constructor would overflow its uint32 integer bound;
    malformed/overflowing native distribution inputs have not been reconstructed.
    Equal endpoints still consume a sample.
    """
    minimum = finite_f32(minimum, 'minimum')
    maximum = finite_f32(maximum, 'maximum')
    if minimum > maximum:
        raise ValueError('minimum must not exceed maximum')
    span = finite_f32(maximum - minimum, 'span')
    integer_max = int(finite_f32(span + 1.0, 'quantized span')) * 1000
    if not 1 <= integer_max <= MASK:
        raise ValueError('range is outside the reconstructed finite integer-bound domain')
    sample = random.bounded(integer_max + 1)
    fraction = f32(f32(sample) / f32(integer_max))
    return f32(f32(fraction * span) + minimum)


def require_context(words: Sequence[int], length: int) -> ProjectileRandom:
    if len(words) != length:
        raise ValueError(f'this path requires exactly {length} seed words')
    return ProjectileRandom(words)


def bounce_sample(words: Sequence[int], variance_degrees: int) -> dict:
    """Random offset only; reflection/base direction and bounce eligibility excluded."""
    random = require_context(words, 4)
    require_uint(variance_degrees, 31, 'variance_degrees')
    pi = f32(3.1415927410125732)
    half = f32(f32(f32(variance_degrees) * pi) / f32(180.0)) if variance_degrees else pi
    return dict(angle_radians=float_distribution(random, -half, half),
                half_width_radians=half, sample_transitions=random.steps - 8)


def coincident_sample(words: Sequence[int]) -> dict:
    """Fallback angle when effective source/aim floor-grid cells coincide.

    Native uses ray (sin(angle),cos(angle))*radius before later range processing;
    no trig or geometry is executed here. Context is [seq,byte,458,2d0,owner].
    """
    random = require_context(words, 5)
    angle = float_distribution(random, 0.0, f32(6.283180236816406))
    return dict(angle_radians=angle, temporary_ray_radius=652.1738891601562,
                sample_transitions=random.steps - 8)


def distance_angle_budget(distance: float, stat: int) -> float:
    """Full width in degrees at ba9d3d..ba9da2; callers handle the enable gate."""
    distance = finite_f32(distance, 'distance')
    if distance < 0:
        raise ValueError('distance must be nonnegative')
    if type(stat) is not int or not -(1 << 31) <= stat < (1 << 31):
        raise ValueError('stat must be int32')
    remaining = f32(f32(100.0) - f32(distance * f32(0.092)))
    scaled = finite_f32(remaining * f32(stat), 'scaled angle budget')
    return max(f32(5.0), f32(scaled / f32(100.0)))


def distance_angle_sample(words: Sequence[int], distance: float, stat: int) -> dict:
    """Stat21ae scalar offset, separately reseeded from any coincidence fallback."""
    random = require_context(words, 5)
    width = distance_angle_budget(distance, stat)
    if stat == 0 or f32(distance) < f32(0.1):
        return dict(enabled=False, angle_radians=0.0, sample_count=0, sample_transitions=0)
    half = f32(width * f32(0.5))
    count = random.bounded(3) + 1
    total = f32(0.0)
    samples = []
    for _ in range(count):
        sample = float_distribution(random, -half, half)
        samples.append(sample)
        total = f32(total + sample)
    mean = f32(total / f32(count))
    angle = f32(mean * f32(0.01745329238474369))
    return dict(enabled=True, width_degrees=width, samples_degrees=samples,
                angle_radians=angle, sample_count=count, sample_transitions=random.steps - 8)


def orientation_sample(tls_state: int) -> dict:
    """Metadata+41 cosmetic orientation; explicit TLS state, not an action RNG.

    Only the checked ten-bit use is exposed. The shared helper can concatenate
    more chunks, but this path advances the LCG once and keeps ten high bits.
    Thread initialization and unrelated consumers are not inferred.
    """
    require_uint(tls_state, 32, 'tls_state')
    new_state = u32(tls_state * 0x41C64E6D + 0x3039)
    index = (new_state >> 16) & 0x3FF
    angle = f32(f32(f32(index) * f32(3.141590118408203)) * f32(0.001953125))
    return dict(tls_state=new_state, index=index, angle_radians=angle)


# Checked against an independent static-listing integer interpreter: seed words,
# initialized core state (after 8 warmups), first 5 raw outputs, first bounded
# sample (bound 3001 for five words; 7001 for four/seven). No raw listing dependency.
VECTORS = (
    ((0, 0, 0, 0),
     (859320635, 1047470579, 3344213023, 791829985),
     (3776620231, 3610305418, 2767560398, 2963402547, 3342499406), 791),
    ((0, 1, 2, 3),
     (3133485874, 1819495082, 1429763350, 1567765672),
     (2298915361, 2077848642, 414963388, 1395453902, 3358480311), 3992),
    ((MASK,) * 4,
     (3634736975, 844255100, 995530289, 2957914273),
     (2894106706, 2325742921, 2863280711, 2829942557, 2827583945), 5322),
    ((0, 0, 0, 0, 0),
     (1301901858, 269798056, 2453394462, 1509391389),
     (1698612801, 1961358779, 100590738, 4060411460, 182262814), 1786),
    ((0, 1, 2, 3, 4),
     (795587243, 964636694, 176734019, 496498722),
     (973622041, 1945489800, 2700813111, 1984820093, 1896128661), 1609),
    ((MASK,) * 5,
     (1565676013, 727982066, 3905106049, 2061846162),
     (1928828157, 1803615211, 115691468, 4036173078, 2191154466), 1429),
    ((0, 0, 0, 0, 0, 0, 0),
     (2899988409, 4292459350, 4067025168, 2142589694),
     (2261659304, 675893288, 682904340, 3105359054, 1497193306), 256),
    ((0, 1, 2, 3, 4, 5, 6),
     (4117798322, 1403290482, 2206920083, 3934160766),
     (2860183388, 3928522842, 1750273833, 563404412, 2979284745), 1849),
    ((MASK,) * 7,
     (1731472570, 1021482888, 951491299, 903470465),
     (824417196, 4054714302, 312446847, 488636780, 3625373964), 439),
)

OBJECT_VECTORS = (
    ((0, 0, 0, 2, 0), 3639037662),
    ((1, 4096, 0, 2, 0), 3035113828),
    ((37, 4096, 3, 5, 0), 41616220),
    ((65535, MASK, 255, 2, MASK), 578757890),
)


def self_test() -> dict:
    for words, state, raw, sample in VECTORS:
        random = ProjectileRandom(words)
        if tuple(random.state) != state or random.steps != 8:
            raise AssertionError(f'initialization differs for {words}')
        if tuple(random.next_u32() for _ in range(5)) != raw:
            raise AssertionError(f'raw outputs differ for {words}')
        random = ProjectileRandom(words)
        bound = 3001 if len(words) == 5 else 7001
        if random.bounded(bound) != sample:
            raise AssertionError(f'bounded output differs for {words}')
        random = ProjectileRandom(words)
        before = (random.steps, tuple(random.state))
        if random.bounded(1) != 0 or (random.steps, tuple(random.state)) != before:
            raise AssertionError('one-element range consumed a draw')
        if random.bounded(1 << 32) != raw[0]:
            raise AssertionError('full uint32 range differs')
    for values, expected in OBJECT_VECTORS:
        if object_seed(*values) != expected:
            raise AssertionError(f'object seed differs for {values}')
    random = ProjectileRandom((0, 0, 0, 0, 0))
    for _ in range(3):
        random.next_u32()
    before_steps = random.steps
    # Fourth raw output is outside the complete bucket for this bound;
    # fifth output is accepted. Rejection must still advance the state.
    if random.bounded(0x80000001) != 182262814 or random.steps - before_steps != 2:
        raise AssertionError('rejected draw did not advance state correctly')
    invalid = [(0,) * length for length in (0, 3, 6, 8, 9)]
    invalid += [(value, 0, 0, 0, 0) for value in (-1, 1 << 32, True, 0.0)]
    for words in invalid:
        try:
            ProjectileRandom(words)
        except ValueError:
            continue
        raise AssertionError(f'invalid seed accepted: {words}')
    for index, bits in enumerate((16, 32, 8, 8, 32)):
        for bad in (-1, 1 << bits, True, 0.0):
            values = [0] * 5
            values[index] = bad
            try:
                object_seed(*values)
            except ValueError:
                continue
            raise AssertionError(f'invalid object input accepted: {values}')
    # Fixed bounds/LCG vectors from the independent round-3 scalar/integer
    # listing interpreters, not values inferred from the functions under test.
    for distance, stat, width in ((0.1, 100, 99.99079895019531),
                                   (0.1, 10000, 9999.080078125),
                                   (10000.0, 10000, 5.0)):
        if distance_angle_budget(distance, stat) != width:
            raise AssertionError('distance-angle width differs')
    for state, expected_state, index in ((0, 12345, 0), (1, 1103527590, 454),
                                          (MASK, 3191464396, 569)):
        result = orientation_sample(state)
        if (result['tls_state'], result['index']) != (expected_state, index):
            raise AssertionError('TLS orientation differs')
    random = ProjectileRandom((0, 0, 0, 0, 0))
    before = random.steps
    if float_distribution(random, 0.0, 0.0) != 0.0 or random.steps == before:
        raise AssertionError('equal float endpoints must consume a sample')
    for distance, stat in ((0.0, 100), (0.099, 100), (100.0, 0)):
        result = distance_angle_sample((0, 0, 0, 0, 0), distance, stat)
        if result['enabled'] or result['sample_transitions'] != 0:
            raise AssertionError('disabled distance-angle path consumed samples')
    invalid_calls = (
        lambda: bounce_sample((0, 0, 0, 0), -1),
        lambda: bounce_sample((0, 0, 0, 0), True),
        lambda: coincident_sample((0, 0, 0, 0)),
        lambda: distance_angle_budget(float('nan'), 100),
        lambda: distance_angle_budget(float('inf'), 100),
        lambda: distance_angle_budget(10**1000, 100),
        lambda: distance_angle_budget(1.0, 1 << 31),
        lambda: distance_angle_budget(1.0, True),
        lambda: orientation_sample(True),
        lambda: orientation_sample(1 << 32),
        lambda: float_distribution(random, 2.0, 1.0),
        lambda: float_distribution(random, False, 1.0),
        lambda: float_distribution(random, 0.0, 1e10),
    )
    before = (random.steps, tuple(random.state))
    for invalid_call in invalid_calls:
        try:
            invalid_call()
        except ValueError:
            continue
        raise AssertionError('invalid scalar/context input accepted')
    if (random.steps, tuple(random.state)) != before:
        raise AssertionError('invalid distribution input changed RNG state')
    return dict(result='passed', stream_vectors=len(VECTORS), object_vectors=len(OBJECT_VECTORS),
                checks=['initial state', 'raw outputs', 'bounded output', 'no-draw bound',
                        'full-width bound', 'rejection advances state',
                        'strict word lengths and unsigned widths', 'scalar distance budget',
                        'TLS orientation', 'equal float endpoints consume a sample',
                        'distance-angle enable gate', 'scalar/context rejection preserves state'],
                evidence_type='static-translation regression; not a running-client comparison')


def uint_argument(bits: int):
    def parse(value: str) -> int:
        try:
            return require_uint(int(value, 0), bits, 'argument')
        except ValueError as error:
            raise argparse.ArgumentTypeError(str(error)) from error
    return parse


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--self-test', action='store_true')
    commands = parser.add_subparsers(dest='command')
    stream = commands.add_parser('stream', help='sample a checked four/five/seven-word context')
    stream.add_argument('--words', nargs='+', type=uint_argument(32), required=True)
    stream.add_argument('--count', type=int, default=5)
    stream.add_argument('--bound', type=int, help='exclusive bound in 1..2**32; omit for raw uint32')
    obj = commands.add_parser('object', help='compute the ordinary object-seed hash')
    obj.add_argument('--sequence', type=uint_argument(16), required=True)
    obj.add_argument('--owner-id', type=uint_argument(32), required=True)
    obj.add_argument('--parent-byte', type=uint_argument(8), required=True)
    obj.add_argument('--derived-byte', type=uint_argument(8), required=True)
    obj.add_argument('--ordinal', type=uint_argument(32), required=True)
    bounce = commands.add_parser('bounce', help='sample a bounce angular offset; excludes reflection')
    bounce.add_argument('--words', nargs=4, type=uint_argument(32), required=True)
    bounce.add_argument('--variance', type=uint_argument(31), required=True)
    coincident = commands.add_parser('coincident', help='sample a same-grid-cell fallback angle')
    coincident.add_argument('--words', nargs=5, type=uint_argument(32), required=True)
    distance_angle = commands.add_parser('distance-angle', help='sample the separately seeded stat21ae offset')
    distance_angle.add_argument('--words', nargs=5, type=uint_argument(32), required=True)
    distance_angle.add_argument('--distance', type=float, required=True)
    distance_angle.add_argument('--stat', type=int, required=True)
    orientation = commands.add_parser('orientation', help='cosmetic angle from explicit thread-local state')
    orientation.add_argument('--tls-state', type=uint_argument(32), required=True)
    args = parser.parse_args()
    if args.self_test:
        print(json.dumps(self_test(), indent=2))
        return
    try:
        if args.command == 'stream':
            if not 0 <= args.count <= 10000:
                raise ValueError('count must be in 0..10000')
            if args.bound is not None and not 1 <= args.bound <= 1 << 32:
                raise ValueError('bound must be in 1..2**32')
            random = ProjectileRandom(args.words)
            initial_state = random.state.copy()
            values = [random.next_u32() if args.bound is None else random.bounded(args.bound)
                      for _ in range(args.count)]
            result = dict(words=args.words, initial_state=initial_state, values=values,
                          exclusive_bound=args.bound, sample_transitions=random.steps - 8)
        elif args.command == 'object':
            values = (args.sequence, args.owner_id, args.parent_byte, args.derived_byte, args.ordinal)
            seed = object_seed(*values)
            result = dict(inputs=values, object_seed=seed, object_seed_hex=f'0x{seed:08x}')
        elif args.command == 'bounce':
            result = bounce_sample(args.words, args.variance)
        elif args.command == 'coincident':
            result = coincident_sample(args.words)
        elif args.command == 'distance-angle':
            result = distance_angle_sample(args.words, args.distance, args.stat)
        elif args.command == 'orientation':
            result = orientation_sample(args.tls_state)
        else:
            parser.error('choose a subcommand, or use --self-test')
    except ValueError as error:
        parser.error(str(error))
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
