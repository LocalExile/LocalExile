#!/usr/bin/env python3
"""Offline reconstruction of the PoE1 3.29.3b Tornado Shot terminal RNG.

This tool does not read, load, attach to, or execute a game client. It reproduces
the statically traced eight-word initialization, variant uint32 transition,
bounded integer sampling and float32 angle conversion. It does not reproduce
native trig, aim quantization, projectile geometry or combat.

The constants and array initialization resemble TinyMT32, but the shipped
transition differs from canonical TinyMT32. Do not substitute a standard library.
Fixed vectors are static-translation regressions, not live-game comparisons.

See Agents/docs/re/projectiles/tornado-geometry.md.
"""

from __future__ import annotations

import argparse
import json
import struct
from typing import Sequence


MASK = 0xFFFFFFFF
MAT1, MAT2, TMAT = 0x8F7011EE, 0xFC78FF1F, 0x3793FDFF
TAU_F32 = 6.283180236816406
ANGLE_INTEGER_MAX = 7000


def u32(value: int) -> int:
    return value & MASK


def f32(value: float) -> float:
    return struct.unpack('<f', struct.pack('<f', value))[0]


def float_bits(value: float) -> str:
    return f"0x{struct.unpack('<I', struct.pack('<f', value))[0]:08x}"


class TerminalRandom:
    """Only the traced eight-word RNG, with a diagnostic transition counter."""

    def __init__(self, words: Sequence[int]):
        if len(words) != 8 or any(not 0 <= value <= MASK for value in words):
            raise ValueError('exactly eight uint32 seed words are required')
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
        for word in words:
            mixed = mix1(state[index] ^ state[(index + 1) % 4] ^ state[(index + 3) % 4])
            state[(index + 1) % 4] = u32(state[(index + 1) % 4] + mixed)
            mixed = u32(mixed + word + index)
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
        # The traced initializer has no intervening period-certification branch.
        for _ in range(8):
            self._transition()

    def _transition(self) -> None:
        s0, s1, s2, s3 = self.state
        combined = (s0 & 0x7FFFFFFF) ^ s1 ^ s2
        left = u32(s3 ^ u32(s3 << 1))
        last = u32(combined ^ (combined >> 1) ^ left)
        self.state = [s1, s2, u32(left ^ u32(last << 10)), last]
        if last & 1:
            self.state[1] ^= MAT1
            self.state[2] ^= MAT2
        self.steps += 1

    def next_u32(self) -> int:
        self._transition()
        first, _, third, last = self.state
        tempered = u32(first + (third >> 8))
        return u32(last ^ tempered ^ (TMAT if tempered & 1 else 0))

    def bounded(self, exclusive_max: int) -> int:
        """Uniform [0, exclusive_max); rejected draws still advance RNG state.

        A one-element range consumes no draw. The full uint32 range consumes one
        raw draw. Other ranges reject the incomplete bucket at the top before
        applying modulo. The TS angle uses exclusive_max=7001.
        """
        if not 1 <= exclusive_max <= 1 << 32:
            raise ValueError('exclusive_max must be in 1..2**32')
        if exclusive_max == 1:
            return 0
        limit = ((1 << 32) // exclusive_max) * exclusive_max
        while True:
            value = self.next_u32()
            if value < limit:
                return value % exclusive_max


def angle_from_sample(sample: int) -> float:
    if not 0 <= sample <= ANGLE_INTEGER_MAX:
        raise ValueError('angle sample must be in 0..7000')
    fraction = f32(f32(sample) / f32(ANGLE_INTEGER_MAX))
    return f32(f32(fraction * f32(TAU_F32)) + f32(0))


# Independently checked against a miniature integer interpreter of the bounded
# static disassembly. No executable bytes or disassembly are included here.
# Each entry: keys, initialized state, first five raw outputs, angle sample/bits.
VECTORS = (
    (
        (0, 0, 0, 0, 0, 0, 0, 0),
        (1787275976, 3850046887, 3094294842, 1581353703),
        (727980856, 2579274948, 502002237, 2368369794, 1838621512),
        2874, '0x402519b6',
    ),
    (
        (1, 2, 4096, 0, 0, 0, 0, 0),
        (3227931310, 2515714579, 462529914, 99662),
        (1057121180, 3340007944, 3504218363, 2803218774, 732219467),
        5185, '0x4094ede8',
    ),
    (
        (37, 3, 4096, 0, 0, 0, 0, 0),
        (2572582878, 2684440612, 2891682072, 1999591900),
        (300056805, 1741005030, 1726347452, 1626865951, 2795986374),
        946, '0x3f596062',
    ),
    (
        (65535, 255, MASK, MASK, 255, 0, 0, 0),
        (3268441007, 3484786005, 169862843, 4012640113),
        (620758244, 4032464437, 222485437, 1296521856, 4058166863),
        577, '0x3f0495fc',
    ),
)


def self_test() -> dict:
    for words, state, raw, sample, bits in VECTORS:
        random = TerminalRandom(words)
        if tuple(random.state) != state or random.steps != 8:
            raise AssertionError(f'initialization differs for {words}')
        if tuple(random.next_u32() for _ in range(5)) != raw:
            raise AssertionError(f'raw output differs for {words}')
        random = TerminalRandom(words)
        if random.bounded(7001) != sample or float_bits(angle_from_sample(sample)) != bits:
            raise AssertionError(f'angle sample differs for {words}')

    random = TerminalRandom(VECTORS[0][0])
    before = (random.steps, random.state.copy())
    if random.bounded(1) != 0 or (random.steps, random.state) != before:
        raise AssertionError('one-element range must not consume a draw')
    if random.bounded(1 << 32) != VECTORS[0][2][0]:
        raise AssertionError('full uint32 range must use the next raw draw')

    # Start at the second output: 2579274948 is rejected for bound 2**31+1,
    # while the following output 502002237 is accepted.
    random = TerminalRandom(VECTORS[0][0])
    random.next_u32()
    before_steps = random.steps
    if random.bounded(0x80000001) != 502002237 or random.steps - before_steps != 2:
        raise AssertionError('rejected bounded draws must advance state')
    if float_bits(angle_from_sample(0)) != '0x00000000':
        raise AssertionError('zero angle endpoint differs')
    if angle_from_sample(7000) != TAU_F32:
        raise AssertionError('upper angle endpoint differs')
    return {
        'result': 'passed',
        'fixed_vectors': len(VECTORS),
        'boundary_checks': ['unused draw', 'full uint32 draw', 'rejection advances state', 'angle endpoints'],
        'evidence_type': 'static-translation regression; not a live-game comparison',
    }


def uint_argument(bits: int):
    def parse(value: str) -> int:
        try:
            number = int(value, 0)
        except ValueError as error:
            raise argparse.ArgumentTypeError('use a decimal or 0x-prefixed integer') from error
        if not 0 <= number < 1 << bits:
            raise argparse.ArgumentTypeError(f'value must fit uint{bits}')
        return number
    return parse


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--self-test', action='store_true', help='run fixed static-translation vectors and exit')
    parser.add_argument('--sequence', type=uint_argument(16), help='action +0x11a, uint16')
    parser.add_argument('--action-byte', type=uint_argument(8), help='parent action +0x11c, uint8; not inferred here')
    parser.add_argument('--owner-id', type=uint_argument(32), help='owner GameObject +0x88, uint32')
    parser.add_argument('--attack-repeats', type=uint_argument(32), default=0, help='action +0x2d0, default 0')
    parser.add_argument('--subrelease', type=uint_argument(8), default=0, help='action +0x2b8, default 0')
    args = parser.parse_args()
    if args.self_test:
        print(json.dumps(self_test(), indent=2))
        return
    if args.sequence is None or args.action_byte is None or args.owner_id is None:
        parser.error('--sequence, --action-byte and --owner-id are required unless using --self-test')

    words = [args.sequence, args.action_byte, args.owner_id, args.attack_repeats, args.subrelease, 0, 0, 0]
    random = TerminalRandom(words)
    initial_state = random.state.copy()
    before_steps = random.steps
    sample = random.bounded(ANGLE_INTEGER_MAX + 1)
    phi = angle_from_sample(sample)
    draws = random.steps - before_steps
    print(json.dumps({
        'scope': 'offline TS terminal RNG reconstruction; no live-game parity claim',
        'native_context': {
            'sequence': args.sequence, 'parent_action_byte': args.action_byte, 'owner_id': args.owner_id,
            'attack_repeats': args.attack_repeats, 'subrelease': args.subrelease,
        },
        'seed_words': words,
        'state_after_init': initial_state,
        'warmup_transitions': before_steps,
        'sample': {'min': 0, 'max': 7000, 'value': sample, 'raw_draws': draws, 'rejections': draws - 1},
        'phi': {'radians_f32': phi, 'bits': float_bits(phi), 'range_upper_f32': TAU_F32},
        'not_modelled': ['native trig', 'integer aim', 'flight/contact geometry', 'actual context lifecycle'],
    }, indent=2))


if __name__ == '__main__':
    main()
